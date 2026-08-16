using FrameLink.Agent.Hosting;
using FrameLink.Agent.Local;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;
using FrameLink.Agent.State;
using FrameLink.Agent.Supervise;

namespace FrameLink.Tests;

/// <summary>
/// §2.10 — the agent's second responsibility, and the interlock that keeps it from fighting the
/// first.
/// </summary>
/// <remarks>
/// The memory watchdog and the kiosk liveness check "were the difference between v1 working and v1
/// dying every ninety minutes", so the tests here are written against the measured numbers rather
/// than against round ones: 1.8 GB, 350 MB, 90 s, and a tree sum that a main-process reading would
/// have missed entirely.
/// </remarks>
public sealed class AgentSupervisionTests
{
    private static readonly CancellationToken None = TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_watchdog_fires_on_the_tree_that_a_main_process_reading_would_have_missed()
    {
        using var frame = new SupervisedFrame();

        // The measured pathology: a renderer past 1.4 GB while the main process sat at an innocent
        // 130 MB. A watchdog that reads the main process never fires, which is exactly what v1's
        // would have reported while the frame died.
        frame.Memory.Sample = new MemorySample(BrowserTreeRssKb: 1_900_000, BrowserProcesses: 6, MemAvailableKb: 900_000);

        Assert.Equal(1, await frame.Supervisor.TickAsync(None));
        Assert.Contains("systemctl --user restart chromium-kiosk.service", frame.Session.Commands);
        Assert.Equal(Supervisor.MemoryWatchdog, frame.Hub.Current.Supervision?.Behaviour);
    }

    [Fact]
    public async Task A_healthy_tree_of_1_7_GB_is_left_alone_because_the_ceiling_is_deliberately_high()
    {
        using var frame = new SupervisedFrame();

        // "After hours of slideshow the healthy tree legitimately reaches ~1.7 GB of iframe image
        // cache — released the instant the iframe unloads." Any lower ceiling restarts healthy
        // frames every evening.
        frame.Memory.Sample = new MemorySample(1_700_000, 6, 900_000);

        Assert.Equal(0, await frame.Supervisor.TickAsync(None));
        Assert.Empty(frame.Session.Commands);
    }

    [Fact]
    public async Task The_available_memory_floor_fires_whatever_is_consuming_the_memory()
    {
        using var frame = new SupervisedFrame();

        // "The 350 MB floor is the sharper instrument": the browser tree is modest and the machine
        // is still stalling, so the browser is restarted anyway because it is always this
        // machine's largest tenant.
        frame.Memory.Sample = new MemorySample(400_000, 5, 300_000);

        Assert.Equal(1, await frame.Supervisor.TickAsync(None));
        Assert.Contains("only 300000 kB", frame.Hub.Current.Supervision?.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_machine_with_no_meminfo_is_not_treated_as_being_out_of_memory()
    {
        using var frame = new SupervisedFrame();
        frame.Memory.Sample = new MemorySample(100_000, 3, -1);

        Assert.Equal(0, await frame.Supervisor.TickAsync(None));
    }

    [Fact]
    public async Task Ninety_seconds_of_silence_restarts_the_browser_systemd_still_calls_active()
    {
        using var frame = new SupervisedFrame();

        frame.Channel.Receive(new PageMessage { Kind = PageMessage.KindHello }, frame.Clock.UtcNow);
        Assert.Equal(0, await frame.Supervisor.TickAsync(None));

        // "An OOM-killed renderer leaves an 'Aw, Snap!' tab while the unit stays active, so
        // Restart= never fires — the app's dropped local channel is the only honest liveness
        // signal." Validated live on v1: a SIGKILLed renderer healed in exactly 90 s.
        frame.Clock.UtcNow += TimeSpan.FromSeconds(89);
        Assert.Equal(0, await frame.Supervisor.TickAsync(None));

        frame.Clock.UtcNow += TimeSpan.FromSeconds(2);
        Assert.Equal(1, await frame.Supervisor.TickAsync(None));
        Assert.Equal(Supervisor.KioskLiveness, frame.Hub.Current.Supervision?.Behaviour);
    }

    [Fact]
    public async Task A_page_that_loads_and_dies_again_is_not_restarted_on_a_loop()
    {
        using var frame = new SupervisedFrame();

        frame.Channel.Receive(new PageMessage { Kind = PageMessage.KindHello }, frame.Clock.UtcNow);
        frame.Clock.UtcNow += TimeSpan.FromSeconds(95);
        Assert.Equal(1, await frame.Supervisor.TickAsync(None));

        // The replacement browser renders, then dies just as quickly. Without the five-minute
        // floor this pair of events repeats every 90 s indefinitely.
        frame.Clock.UtcNow += TimeSpan.FromSeconds(5);
        frame.Channel.Receive(new PageMessage { Kind = PageMessage.KindHello }, frame.Clock.UtcNow);

        frame.Clock.UtcNow += TimeSpan.FromSeconds(100);
        Assert.Equal(0, await frame.Supervisor.TickAsync(None));
        Assert.Contains("cooldown", frame.Supervisor.LastStandDown, StringComparison.Ordinal);

        // Past the cooldown, the same silence is acted on again — the fault *rate* is what makes a
        // frame like this visible, never a budget that would stop the restarts (§2.10).
        frame.Clock.UtcNow += TimeSpan.FromMinutes(5);
        Assert.Equal(1, await frame.Supervisor.TickAsync(None));
    }

    [Fact]
    public async Task A_browser_that_never_renders_again_is_the_stages_problem_not_the_watchdogs()
    {
        using var frame = new SupervisedFrame();

        frame.Channel.Receive(new PageMessage { Kind = PageMessage.KindHello }, frame.Clock.UtcNow);
        frame.Clock.UtcNow += TimeSpan.FromSeconds(95);
        Assert.Equal(1, await frame.Supervisor.TickAsync(None));

        // The restart wiped the old page's check-in, and the replacement never renders. §2.10's
        // liveness rule owns a page that *was* rendering and stopped; a page that never rendered
        // at all is §2.7's fallback rule, whose answer is to tear the session down rather than to
        // restart the browser every 90 s forever.
        for (var tick = 0; tick < 5; tick++)
        {
            frame.Clock.UtcNow += TimeSpan.FromMinutes(10);
            Assert.Equal(0, await frame.Supervisor.TickAsync(None));
        }

        Assert.Null(frame.Channel.LastCheckInUtc);
    }

    [Fact]
    public async Task A_frame_that_has_never_run_a_daily_restart_has_not_missed_one()
    {
        using var frame = new SupervisedFrame(TimeZoneInfo.Utc);

        // Midday on a fresh frame. Firing here would blink the browser once on every agent start
        // after 03:00 — and §2.4 restarts this process once per resource during provisioning.
        Assert.Equal(0, await frame.Supervisor.TickAsync(None));
        Assert.Empty(frame.Session.Commands);

        // The stamp is v1's Persistent=true made durable, so a reboot cannot turn "already run"
        // back into "never run".
        Assert.Equal("2026-08-15", frame.Store.Store.ReadText(Supervisor.DailyRestartStampFile));
    }

    [Fact]
    public async Task A_restart_missed_while_the_frame_was_off_is_taken_as_soon_as_it_is_back()
    {
        using var frame = new SupervisedFrame(TimeZoneInfo.Utc);
        frame.Store.Store.WriteText(Supervisor.DailyRestartStampFile, "2026-08-12");

        // The frame was off across 03:00 and came back at midday three days later. v1's
        // Persistent=true fires at boot rather than waiting for tomorrow, and so does this.
        Assert.Equal(1, await frame.Supervisor.TickAsync(None));
        Assert.Equal(Supervisor.DailyRestart, frame.Hub.Current.Supervision?.Behaviour);
        Assert.Equal("2026-08-15", frame.Store.Store.ReadText(Supervisor.DailyRestartStampFile));
    }

    [Fact]
    public async Task The_daily_restart_waits_out_a_call_and_then_takes_the_run_it_missed()
    {
        using var frame = new SupervisedFrame(TimeZoneInfo.Utc);
        frame.Store.Store.WriteText(Supervisor.DailyRestartStampFile, "2026-08-15");
        frame.Supervisor.CallActive = true;

        frame.Clock.UtcNow = new DateTimeOffset(2026, 8, 16, 3, 0, 30, TimeSpan.Zero);
        Assert.Equal(0, await frame.Supervisor.TickAsync(None));
        Assert.Contains("call is in progress", frame.Supervisor.LastStandDown, StringComparison.Ordinal);

        // v1's Persistent=true, carried over: the schedule is still owed, so the first tick after
        // the call ends takes it rather than waiting for tomorrow.
        frame.Supervisor.CallActive = false;
        frame.Clock.UtcNow += TimeSpan.FromMinutes(20);

        Assert.Equal(1, await frame.Supervisor.TickAsync(None));
        Assert.Equal(Supervisor.DailyRestart, frame.Hub.Current.Supervision?.Behaviour);

        // And exactly once for that day.
        frame.Clock.UtcNow += TimeSpan.FromHours(2);
        Assert.Equal(0, await frame.Supervisor.TickAsync(None));
    }

    [Fact]
    public async Task The_memory_watchdog_does_not_defer_for_a_call_the_way_the_daily_restart_does()
    {
        using var frame = new SupervisedFrame();
        frame.Supervisor.CallActive = true;
        frame.Memory.Sample = new MemorySample(1_900_000, 6, 900_000);

        // "The memory watchdog defers for nothing — the alternative to acting during a call is an
        // OOM kill or a hardware-watchdog reset, which ends that call anyway and takes the frame
        // with it."
        Assert.Equal(1, await frame.Supervisor.TickAsync(None));
    }

    [Fact]
    public async Task A_call_ending_recycles_the_camera_node()
    {
        using var frame = new SupervisedFrame();
        frame.Channel.Receive(new PageMessage { Kind = PageMessage.KindCallEnded }, frame.Clock.UtcNow);

        Assert.Equal(1, await frame.Supervisor.TickAsync(None));
        Assert.Contains("systemctl --user restart framelink-camera.service", frame.Session.Commands);
    }

    [Fact]
    public async Task The_camera_recycle_switches_off_by_setting_for_PipeWire_1_6()
    {
        using var frame = new SupervisedFrame(settings: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SupervisionSettings.CameraRestartOnCallEndKey] = "false",
        });

        frame.Channel.Receive(new PageMessage { Kind = PageMessage.KindCallEnded }, frame.Clock.UtcNow);

        Assert.Equal(0, await frame.Supervisor.TickAsync(None));
        Assert.Empty(frame.Session.Commands);
    }

    [Fact]
    public async Task Supervision_stands_down_when_the_product_is_not_running()
    {
        using var frame = new SupervisedFrame();
        frame.Hub.Publish(status => status with { Condition = DeviceStateLadder.Starting, LastAuthoritative = null });
        frame.Memory.Sample = new MemorySample(1_900_000, 6, 900_000);

        Assert.Equal(0, await frame.Supervisor.TickAsync(None));
        Assert.Contains("the product is not running", frame.Supervisor.LastStandDown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Supervision_runs_at_full_strength_with_the_Fleet_Manager_unreachable()
    {
        using var frame = new SupervisedFrame();

        // §2.6's NoContact rung for a frame that was green when contact dropped: the product keeps
        // running, so supervision keeps it alive. "That is the case where no help is coming."
        frame.Hub.Publish(status => status with
        {
            Condition = DeviceStateLadder.NoContact(status.LastAuthoritative, "the socket closed"),
        });

        Assert.True(frame.Hub.Current.ProductRuns);

        frame.Memory.Sample = new MemorySample(1_900_000, 6, 900_000);
        Assert.Equal(1, await frame.Supervisor.TickAsync(None));
    }

    [Fact]
    public async Task Repeated_action_raises_a_fault_that_never_stops_the_restarts()
    {
        using var frame = new SupervisedFrame();
        frame.Memory.Sample = new MemorySample(1_900_000, 6, 900_000);

        for (var restart = 0; restart < 3; restart++)
        {
            Assert.Equal(1, await frame.Supervisor.TickAsync(None));
            Assert.False(frame.Hub.Current.Supervision?.AtFaultLevel);
            frame.Clock.UtcNow += TimeSpan.FromMinutes(6);
        }

        // "More than supervision.faultRateThreshold actions of one behaviour within
        // supervision.faultRateWindow raises a supervision fault."
        Assert.Equal(1, await frame.Supervisor.TickAsync(None));
        Assert.True(frame.Hub.Current.Supervision?.AtFaultLevel);

        // "The fault never inhibits supervision — the restarts continue, because a frame
        // restarting every ten minutes still beats a dark one."
        frame.Clock.UtcNow += TimeSpan.FromMinutes(6);
        Assert.Equal(1, await frame.Supervisor.TickAsync(None));

        Assert.Contains(
            frame.Telemetry.Events,
            entry => string.Equals(entry.Kind, Supervisor.SupervisionFaultEventKind, StringComparison.Ordinal));

        // The annotation renders on the frame only at fault level (§2.10, §2.6's small overlay).
        Assert.NotNull(frame.Hub.Current.Supervision?.Overlay);
    }

    [Fact]
    public async Task A_supervised_restart_while_InSync_leaves_the_device_InSync()
    {
        using var frame = new SupervisedFrame();
        frame.Memory.Sample = new MemorySample(1_900_000, 6, 900_000);

        await frame.Supervisor.TickAsync(None);

        // §2.10 against the ladder: "an annotation, not a rung". The rung answers exactly one
        // question — does the product run — and a supervised restart does not change the answer.
        Assert.Equal(DeviceState.InSync, frame.Hub.Current.Condition.State);
        Assert.True(frame.Hub.Current.ProductRuns);
        Assert.NotNull(frame.Hub.Current.Supervision);
    }

    [Fact]
    public async Task Every_action_is_reported_because_nothing_is_repaired_invisibly()
    {
        using var frame = new SupervisedFrame();
        frame.Memory.Sample = new MemorySample(1_900_000, 6, 900_000);

        await frame.Supervisor.TickAsync(None);

        var reported = Assert.Single(
            frame.Telemetry.Events,
            entry => string.Equals(entry.Kind, Supervisor.SupervisionEventKind, StringComparison.Ordinal));

        // §2.10's list: which behaviour fired, the measured value against its threshold, and what
        // was restarted.
        Assert.Equal(Supervisor.MemoryWatchdog, reported.Resource);
        Assert.Contains("1900000 kB", reported.Summary, StringComparison.Ordinal);
        Assert.Contains("1843200 kB ceiling", reported.Summary, StringComparison.Ordinal);
        Assert.Contains("restart chromium-kiosk.service", reported.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_settings_parse_the_forms_the_specification_writes_them_in()
    {
        var settings = new SupervisionSettings(FleetValues.From(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SupervisionSettings.KioskSilenceTimeoutKey] = "90s",
            [SupervisionSettings.MemoryCheckIntervalKey] = "5m",
            [SupervisionSettings.FaultRateWindowKey] = "1h",
            [SupervisionSettings.DailyRestartTimeKey] = "04:30",
        }));

        Assert.Equal(TimeSpan.FromSeconds(90), settings.KioskSilenceTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), settings.MemoryCheckInterval);
        Assert.Equal(TimeSpan.FromHours(1), settings.FaultRateWindow);
        Assert.Equal(new TimeOnly(4, 30), settings.DailyRestartTime);

        // A mistyped interval must never become "sample continuously" or "consider the page dead
        // immediately", so anything unparseable falls through to the measured default.
        var mistyped = new SupervisionSettings(FleetValues.From(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SupervisionSettings.KioskSilenceTimeoutKey] = "ninety",
        }));

        Assert.Equal(TimeSpan.FromSeconds(90), mistyped.KioskSilenceTimeout);
        Assert.Null(new SupervisionSettings(FleetValues.From(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SupervisionSettings.DailyRestartTimeKey] = "off",
        })).DailyRestartTime);
    }

    [Fact]
    public void The_defaults_are_the_measured_constants_and_not_round_numbers()
    {
        var settings = SupervisionSettings.Defaults;

        Assert.Equal(1_843_200, settings.BrowserTreeRssCeilingKb);
        Assert.Equal(358_400, settings.MemAvailableFloorKb);
        Assert.Equal(TimeSpan.FromMinutes(5), settings.MemoryCheckInterval);
        Assert.Equal(new TimeOnly(3, 0), settings.DailyRestartTime);
        Assert.Equal(TimeSpan.FromSeconds(90), settings.KioskSilenceTimeout);
        Assert.Equal(TimeSpan.FromSeconds(15), settings.KioskCheckInterval);
        Assert.Equal(TimeSpan.FromMinutes(5), settings.KioskRestartCooldown);
        Assert.True(settings.CameraRestartOnCallEnd);
        Assert.Equal(TimeSpan.FromMinutes(2), settings.RecoveryDeadline);
        Assert.Equal(3, settings.FaultRateThreshold);
        Assert.Equal(TimeSpan.FromHours(1), settings.FaultRateWindow);
    }

    [Fact]
    public void The_probe_sums_the_tree_rather_than_reading_the_largest_process()
    {
        const string Listing = """
             133120 chromium
            1400320 chromium
             210000 chromium
              64000 labwc
            """;

        var (kilobytes, processes) = ProcMemoryProbe.SumBrowserTree(Listing);

        // 130 MB + 1.4 GB + 205 MB. Reading any one row — including the main process's innocent
        // 130 MB — misses the fault entirely.
        Assert.Equal(1_743_440, kilobytes);
        Assert.Equal(3, processes);

        Assert.Equal(
            1_234_567,
            ProcMemoryProbe.MemAvailableKb("MemTotal:        2000000 kB\nMemAvailable:    1234567 kB\n"));
        Assert.Equal(-1, ProcMemoryProbe.MemAvailableKb(null));
    }

    /// <summary>A supervisor over a green frame, with every seam scripted.</summary>
    private sealed class SupervisedFrame : IDisposable
    {
        public SupervisedFrame(TimeZoneInfo? zone = null, IReadOnlyDictionary<string, string>? settings = null)
        {
            Hub = new AgentStatusHub(AgentStatusFactory.Green());
            Clock = new ManualClock();
            Interlock = new SupervisionInterlock();

            Supervisor = new Supervisor(new SupervisionServices
            {
                Channel = Channel,
                Session = Session,
                Memory = Memory,
                Interlock = Interlock,
                Hub = Hub,
                Telemetry = Telemetry,
                Clock = Clock,
                Log = Log,
                Settings = new SupervisionSettings(FleetValues.From(
                    settings ?? new Dictionary<string, string>(StringComparer.Ordinal))),
                Store = Store.Store,
                TimeZone = zone ?? TimeZoneInfo.Utc,
                DeviceId = "TEST-DEVI-CEID-0001",
            });
        }

        public TemporaryStore Store { get; } = new();

        public LocalChannel Channel { get; } = new();

        public FakeUserSession Session { get; } = new();

        public StubMemoryProbe Memory { get; } = new();

        public SupervisionInterlock Interlock { get; }

        public AgentStatusHub Hub { get; }

        public RecordingTelemetry Telemetry { get; } = new();

        public ManualClock Clock { get; }

        public RecordingLog Log { get; } = new();

        public Supervisor Supervisor { get; }

        public void Dispose() => Store.Dispose();
    }
}

/// <summary>
/// §2.10's interlock, from both sides. <b>Nobody had verified the two do not fight.</b>
/// </summary>
public sealed class AgentSupervisionInterlockTests
{
    private static readonly CancellationToken None = TestContext.Current.CancellationToken;

    [Fact]
    public void The_reconciler_holds_what_it_is_progressing_awaiting_or_blocked_on()
    {
        var interlock = new SupervisionInterlock();

        interlock.PublishHolds(
        [
            new ResourceStatus { Name = "a", Kind = ResourceStatusKind.Progressing },
            new ResourceStatus { Name = "b", Kind = ResourceStatusKind.AwaitingReboot },
            new ResourceStatus { Name = "c", Kind = ResourceStatusKind.Blocked },
            new ResourceStatus { Name = "d", Kind = ResourceStatusKind.InSync },
            new ResourceStatus { Name = "e", Kind = ResourceStatusKind.Degraded },

        ]);

        Assert.True(interlock.ReconcilerHolds("a"));
        Assert.True(interlock.ReconcilerHolds("b"));
        Assert.True(interlock.ReconcilerHolds("c"));
        Assert.False(interlock.ReconcilerHolds("d"));

        // Degraded and Escalated mean the reconciler has stopped touching it, so there is nothing to
        // race — and a frame whose kiosk unit has been given up on still needs restarting to stay
        // alive. Holding them would mean a dark frame, which is what §2.10 refuses.
        Assert.False(interlock.ReconcilerHolds("e"));
        Assert.False(interlock.ReconcilerHolds("f"));
    }

    [Fact]
    public async Task Supervision_leaves_alone_what_the_reconciler_is_applying()
    {
        using var files = new TemporaryFiles();
        var hub = new AgentStatusHub(AgentStatusFactory.Green());
        var interlock = new SupervisionInterlock();
        var session = new FakeUserSession();
        var memory = new StubMemoryProbe { Sample = new MemorySample(1_900_000, 6, 900_000) };

        var supervisor = new Supervisor(new SupervisionServices
        {
            Channel = new LocalChannel(),
            Session = session,
            Memory = memory,
            Interlock = interlock,
            Hub = hub,
            Telemetry = new RecordingTelemetry(),
            Clock = new ManualClock(),
            Log = new RecordingLog(),
        });

        interlock.Applying(ChromiumKioskRunningResource.ResourceName);

        // §2.10 clause 1: "Restarting a browser the reconciler is deliberately holding down, or
        // racing an apply, produces exactly the interference that makes 'which change broke it'
        // unanswerable."
        Assert.Equal(0, await supervisor.TickAsync(None));
        Assert.Empty(session.Commands);
        Assert.Contains("the reconciler is working on", supervisor.LastStandDown, StringComparison.Ordinal);

        interlock.Applying(null);
        Assert.Equal(1, await supervisor.TickAsync(None));
    }

    [Fact]
    public async Task A_browser_restart_does_not_trip_the_any_drift_stops_the_product_rule()
    {
        var interlock = new SupervisionInterlock();
        var running = new ScriptedResource(ChromiumKioskRunningResource.ResourceName, "want", "want");
        var other = new ScriptedResource("other", "want", "want");

        using var harness = new ReconcileHarness(new ReconcileOptions { Countdown = TimeSpan.Zero }, running, other);

        var loop = new ReconcileLoop(new ReconcileServices
        {
            Graph = harness.Graph,
            Journal = harness.Journal,
            Boot = harness.Boot,
            Reboots = harness.Boundary,
            Countdown = harness.Countdown,
            Telemetry = harness.Telemetry,
            Hub = harness.Hub,
            Clock = harness.Clock,
            Log = harness.Log,
            Options = new ReconcileOptions { Countdown = TimeSpan.Zero },
            Interlock = interlock,
        });

        // A frame the Fleet Manager has cleared, so §2.6's InSync rung is what the drift rule acts
        // against rather than a starting frame that was never showing anything.
        harness.Hub.Publish(_ => AgentStatusFactory.Green());

        var converged = await loop.RunPassAsync(None);
        Assert.Equal(PassResult.Converged, converged.Result);
        Assert.False(harness.Hub.Current.Drifted);
        Assert.True(harness.Hub.Current.ProductRuns);

        // Supervision restarts the browser. For a second or two the running process is gone, which
        // is exactly what unit.chromium-kiosk.running-matches-content observes.
        var window = interlock.Open(
            Supervisor.KioskLiveness,
            Supervisor.BrowserResources,
            harness.Clock.UtcNow,
            TimeSpan.FromMinutes(2));

        running.Drift();

        var during = await loop.RunPassAsync(None);

        // §2.10 clause 2: expected rather than drift. The product keeps running, nothing is acted
        // on, and no reboot is requested — the collision §2.10 says would otherwise blank the
        // frame and kill the call every morning at 03:00.
        Assert.False(harness.Hub.Current.Drifted);
        Assert.True(harness.Hub.Current.ProductRuns);
        Assert.Equal(0, running.Acts);
        Assert.Empty(harness.Boundary.Crossings);
        Assert.Contains(
            "expected while supervision restarts it",
            ReconcileHarness.StatusOf(during, running.Name).Delta,
            StringComparison.Ordinal);

        // The deadline is the boundary. Once it expires the very same observation is ordinary
        // drift, the product stops, and the reconciler owns the repair.
        harness.Clock.UtcNow = window.DeadlineUtc + TimeSpan.FromSeconds(1);
        var after = await loop.RunPassAsync(None);

        Assert.NotEqual(PassResult.Converged, after.Result);
        Assert.Equal(1, running.Acts);
        Assert.Single(harness.Boundary.Crossings);
    }

    [Fact]
    public async Task Drift_outside_any_window_stops_the_product_exactly_as_2_6_says()
    {
        var interlock = new SupervisionInterlock();

        // A write that succeeds and leaves the value wrong — v1's governor bug in miniature, and
        // the case where drift is still true on the far side of the verifying reboot.
        var resource = new ScriptedResource("something", "want", "have-not") { ActHasNoEffect = true };

        using var harness = new ReconcileHarness(new ReconcileOptions { Countdown = TimeSpan.Zero }, resource);

        var loop = new ReconcileLoop(new ReconcileServices
        {
            Graph = harness.Graph,
            Journal = harness.Journal,
            Boot = harness.Boot,
            Reboots = harness.Boundary,
            Countdown = harness.Countdown,
            Telemetry = harness.Telemetry,
            Hub = harness.Hub,
            Clock = harness.Clock,
            Log = harness.Log,
            Options = new ReconcileOptions { Countdown = TimeSpan.Zero },
            Interlock = interlock,
        });

        harness.Hub.Publish(_ => AgentStatusFactory.Green());

        await loop.RunPassAsync(None);

        Assert.True(harness.Hub.Current.Drifted);
        Assert.False(harness.Hub.Current.ProductRuns);
    }

    [Fact]
    public void An_expired_window_is_handed_back_so_it_can_become_drift()
    {
        var interlock = new SupervisionInterlock();
        var opened = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

        var window = interlock.Open(Supervisor.MemoryWatchdog, ["x"], opened, TimeSpan.FromMinutes(2));

        Assert.True(interlock.Excuses("x", opened + TimeSpan.FromSeconds(30)));
        Assert.False(interlock.Excuses("x", opened + TimeSpan.FromMinutes(3)));
        Assert.False(interlock.Excuses("y", opened));

        var expired = interlock.Expire(opened + TimeSpan.FromMinutes(3));

        Assert.Equal([window], expired);
        Assert.Equal(0, interlock.OpenWindows);
    }
}
