using FrameLink.Agent;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Local;
using FrameLink.Agent.Resources;
using FrameLink.Agent.Stage;
using FrameLink.Agent.State;
using FrameLink.Agent.Supervise;

namespace FrameLink.Tests;

/// <summary>
/// §2.7's browser stage and its <b>fallback rule</b> — "a blank or broken desktop is never an
/// acceptable state".
/// </summary>
public sealed class AgentBrowserStageTests
{
    private static readonly CancellationToken None = TestContext.Current.CancellationToken;

    private const string IsActive = "systemctl --user is-active chromium-kiosk.service";

    [Fact]
    public async Task Nothing_is_required_of_a_frame_with_no_graphical_stack_yet()
    {
        using var frame = new StagedFrame();
        frame.BrowserIs("inactive");

        // "Before any graphical stack exists" the console stage is the whole screen. There is no
        // page to require a check-in of, and requiring one would tear down a session that has not
        // been built yet.
        Assert.Equal(BrowserStagePhase.Console, await frame.Stage.TickAsync(None));
        Assert.Equal(0, frame.Stage.Teardowns);
    }

    [Fact]
    public async Task A_page_that_renders_takes_over_the_screen()
    {
        using var frame = new StagedFrame();
        frame.BrowserIs("active");

        Assert.Equal(BrowserStagePhase.Awaiting, await frame.Stage.TickAsync(None));

        frame.Clock.UtcNow += TimeSpan.FromSeconds(4);
        frame.Channel.Receive(new PageMessage { Kind = PageMessage.KindHello }, frame.Clock.UtcNow);

        Assert.Equal(BrowserStagePhase.Live, await frame.Stage.TickAsync(None));
        Assert.Equal(0, frame.Stage.Teardowns);

        // Nothing was stopped. The handover is a reveal — labwc draws over a console that never
        // stopped painting — so there is no session to tear down on the happy path.
        Assert.DoesNotContain(frame.Systemd.Commands, command => command.StartsWith("stop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_desktop_that_never_renders_is_taken_down_and_the_console_says_why()
    {
        using var frame = new StagedFrame();
        frame.BrowserIs("active");

        await frame.Stage.TickAsync(None);

        // The measured failure this rule exists for: labwc is up, the unit reports active, and the
        // page is a white rectangle. Every unit-level check passes and the frame shows nothing.
        frame.Clock.UtcNow += TimeSpan.FromSeconds(61);

        Assert.Equal(BrowserStagePhase.TornDown, await frame.Stage.TickAsync(None));
        Assert.Equal(1, frame.Stage.Teardowns);

        // Stopping getty@tty1 is what ends the session: killing labwc alone would have the getty
        // respawn the login that execs it straight back, and the frame would flap.
        Assert.Contains("systemctl --user stop chromium-kiosk.service", frame.Session.Commands);
        Assert.Contains("stop " + BrowserStage.GettyUnitName, frame.Systemd.Commands);

        // "...and returns to console narration explaining why." The narration is published to the
        // hub the console stage renders from, so the explanation is on the tty the teardown has
        // just uncovered.
        Assert.Contains("nothing was drawn", frame.Hub.Current.Narration.Detected, StringComparison.Ordinal);
        Assert.Contains("did not render within 60 s", frame.Hub.Current.Narration.ActionGloss, StringComparison.Ordinal);
        Assert.Contains(
            frame.Telemetry.Events,
            entry => entry.Delta?.Contains("did not render", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task The_teardown_is_not_read_as_drift_while_it_is_deliberate()
    {
        using var frame = new StagedFrame();
        frame.BrowserIs("active");

        await frame.Stage.TickAsync(None);
        frame.Clock.UtcNow += TimeSpan.FromSeconds(61);
        await frame.Stage.TickAsync(None);

        // A teardown makes three things transiently wrong: no browser process, no compositor, and
        // no live Wayland output to carry a transform. Without the window the reconciler would
        // repair a session the agent is deliberately holding down — and would reboot for it.
        foreach (var resource in BrowserStage.SessionResources)
        {
            Assert.True(frame.Interlock.Excuses(resource, frame.Clock.UtcNow));
        }

        // The files that produce those three stay under ordinary drift detection throughout.
        Assert.False(frame.Interlock.Excuses(ChromiumKioskUnitResource.ResourceName, frame.Clock.UtcNow));
        Assert.False(frame.Interlock.Excuses(LabwcAutostartResource.ResourceName, frame.Clock.UtcNow));
    }

    [Fact]
    public async Task The_unit_the_teardown_stops_is_the_one_the_interlock_has_to_cover()
    {
        // The measured defect: the window named the teardown's three *consequences* and not the
        // act itself, so the reconciler read `systemctl stop getty@tty1.service` as drift — delta
        // `observed 'getty@tty1.service is inactive'` — and repaired it with a reboot, on a frame
        // whose graphical session the agent was deliberately holding down.
        using var frame = new StagedFrame();
        frame.BrowserIs("active");

        await frame.Stage.TickAsync(None);
        frame.Clock.UtcNow += TimeSpan.FromSeconds(61);
        await frame.Stage.TickAsync(None);

        Assert.Contains("stop " + BrowserStage.GettyUnitName, frame.Systemd.Commands);
        Assert.True(frame.Interlock.Excuses(ConsoleAutologinResource.ResourceName, frame.Clock.UtcNow));

        // Written as an invariant rather than as a fourth string, because the list can only be
        // right while it names a resource for every unit the teardown stops.
        Assert.Equal(ConsoleAutologinResource.UnitName, BrowserStage.GettyUnitName);
        Assert.Contains(ConsoleAutologinResource.ResourceName, BrowserStage.SessionResources);
        Assert.Contains(ChromiumKioskRunningResource.ResourceName, BrowserStage.SessionResources);

        // And it is still a window rather than an amnesty: the retry restarts the getty at two
        // minutes and the cover runs out a minute after that, which is §2.10 clause 3's boundary.
        frame.Clock.UtcNow += frame.Stage.RetryDelay + frame.Stage.CheckInDeadline;
        Assert.False(frame.Interlock.Excuses(ConsoleAutologinResource.ResourceName, frame.Clock.UtcNow));
    }

    [Fact]
    public async Task The_console_is_not_where_the_frame_stays_forever()
    {
        using var frame = new StagedFrame();
        frame.BrowserIs("active");

        await frame.Stage.TickAsync(None);
        frame.Clock.UtcNow += TimeSpan.FromSeconds(61);
        await frame.Stage.TickAsync(None);

        // Still cooling off.
        frame.Clock.UtcNow += TimeSpan.FromSeconds(30);
        Assert.Equal(BrowserStagePhase.TornDown, await frame.Stage.TickAsync(None));

        frame.Clock.UtcNow += TimeSpan.FromMinutes(2);
        Assert.Equal(BrowserStagePhase.Console, await frame.Stage.TickAsync(None));
        Assert.Contains("start " + BrowserStage.GettyUnitName, frame.Systemd.Commands);

        // The retry gets the full deadline of its own, measured from the moment the browser is up
        // again rather than from the previous attempt.
        Assert.Equal(BrowserStagePhase.Awaiting, await frame.Stage.TickAsync(None));
        frame.Channel.Receive(new PageMessage { Kind = PageMessage.KindHello }, frame.Clock.UtcNow);
        Assert.Equal(BrowserStagePhase.Live, await frame.Stage.TickAsync(None));
    }

    [Fact]
    public async Task A_second_failure_falls_through_to_ordinary_drift_rather_than_looping_quietly()
    {
        using var frame = new StagedFrame();
        frame.BrowserIs("active");

        await frame.Stage.TickAsync(None);
        frame.Clock.UtcNow += TimeSpan.FromSeconds(61);
        await frame.Stage.TickAsync(None);

        // §2.10 clause 3 as the designed escape: the window covers the cool-off and the retry, and
        // when the page still never renders it expires. From that instant the reconciler owns the
        // condition, the device leaves InSync, and the repair gets the full §2.7 narration and a
        // reboot.
        frame.Clock.UtcNow += TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(61);

        Assert.False(frame.Interlock.Excuses(BashProfileLabwcResource.ResourceName, frame.Clock.UtcNow));
        Assert.NotEmpty(frame.Interlock.Expire(frame.Clock.UtcNow));
    }

    [Fact]
    public async Task The_daily_restart_does_not_tear_down_the_session_it_restarted()
    {
        // The measured defect, end to end and through the real supervisor. §2.10's 03:00 restart
        // fires, RestartBrowserAsync forgets the page's check-in so the liveness rule cannot
        // measure a new browser against an old heartbeat — and the stage then failed its own
        // check-in guard against an arm timestamp from the previous morning and tore the graphical
        // session down. Four consecutive nights, +0.220 s to +1.213 s after the restart, on a page
        // that had been rendering for 23 h 52 m. A supervised restart is not drift and must not
        // stop the product (§2.10).
        using var frame = new StagedFrame();
        frame.BrowserIs("active");

        Assert.Equal(BrowserStagePhase.Awaiting, await frame.Stage.TickAsync(None));
        frame.Channel.Receive(new PageMessage { Kind = PageMessage.KindHello }, frame.Clock.UtcNow);
        Assert.Equal(BrowserStagePhase.Live, await frame.Stage.TickAsync(None));

        // Yesterday's 03:00 has been marked as taken, so the next crossing is owed.
        Assert.Equal(0, await frame.Supervisor.TickAsync(None));

        frame.Clock.UtcNow += TimeSpan.FromHours(23) + TimeSpan.FromMinutes(52);
        Assert.Equal(1, await frame.Supervisor.TickAsync(None));
        Assert.Contains("systemctl --user restart chromium-kiosk.service", frame.Session.Commands);
        Assert.Null(frame.Channel.LastCheckInUtc);

        // The very next stage tick, which on a frame is at most five seconds later.
        frame.Clock.UtcNow += TimeSpan.FromSeconds(1);
        var phase = await frame.Stage.TickAsync(None);

        Assert.NotEqual(BrowserStagePhase.TornDown, phase);
        Assert.Equal(0, frame.Stage.Teardowns);
        Assert.DoesNotContain("stop " + BrowserStage.GettyUnitName, frame.Systemd.Commands);
    }

    [Fact]
    public async Task A_restarted_browser_gets_a_deadline_of_its_own_rather_than_none_at_all()
    {
        // The other half of the same line, and the half a bare "forget the old deadline" would
        // lose. A page that never comes back after a supervised restart is nobody else's business:
        // §2.10's liveness rule stands down explicitly when the channel has no check-in at all,
        // naming this rule as the owner. So the stage has to arm afresh — and then a blank desktop
        // is caught on this browser's own deadline instead of the previous one's.
        using var frame = new StagedFrame();
        frame.BrowserIs("active");

        await frame.Stage.TickAsync(None);
        frame.Channel.Receive(new PageMessage { Kind = PageMessage.KindHello }, frame.Clock.UtcNow);
        Assert.Equal(BrowserStagePhase.Live, await frame.Stage.TickAsync(None));

        frame.Clock.UtcNow += TimeSpan.FromHours(23) + TimeSpan.FromMinutes(52);
        frame.Channel.Forget();

        Assert.Equal(BrowserStagePhase.Awaiting, await frame.Stage.TickAsync(None));

        // Not one second early: the new page gets the whole deadline, measured from the restart.
        frame.Clock.UtcNow += TimeSpan.FromSeconds(59);
        Assert.Equal(BrowserStagePhase.Awaiting, await frame.Stage.TickAsync(None));
        Assert.Equal(0, frame.Stage.Teardowns);

        frame.Clock.UtcNow += TimeSpan.FromSeconds(2);
        Assert.Equal(BrowserStagePhase.TornDown, await frame.Stage.TickAsync(None));
        Assert.Equal(1, frame.Stage.Teardowns);
    }

    [Fact]
    public async Task A_page_that_keeps_checking_in_is_never_judged_again()
    {
        // The invariant behind both tests above: a deadline runs exactly while a live browser owes
        // a check-in. A page that met one is not measured against anything, however long it stays
        // up and whoever else is holding the panel — the ordinary state of a frame is 23 hours of
        // this, so a rule that could re-arm underneath a rendering page would be the same defect
        // pointing the other way.
        using var frame = new StagedFrame();
        frame.BrowserIs("active");

        await frame.Stage.TickAsync(None);
        frame.Channel.Receive(new PageMessage { Kind = PageMessage.KindHello }, frame.Clock.UtcNow);
        Assert.Equal(BrowserStagePhase.Live, await frame.Stage.TickAsync(None));

        // Somebody is logged in on another terminal, so the agent's console is what is in front.
        frame.Terminals.Active = TtyTerminal.AgentTerminal;
        Assert.True(await frame.Screen.TakeAsync(None));

        for (var hour = 0; hour < 24; hour++)
        {
            frame.Clock.UtcNow += TimeSpan.FromHours(1);
            Assert.Equal(BrowserStagePhase.Live, await frame.Stage.TickAsync(None));
        }

        Assert.Equal(0, frame.Stage.Teardowns);
    }

    [Fact]
    public async Task An_earlier_browsers_check_in_cannot_vouch_for_this_one()
    {
        using var frame = new StagedFrame();

        // A page checked in before the browser was restarted — the state supervision leaves behind
        // after a memory watchdog restart. Without forgetting it, the new browser's deadline is
        // measured against the old browser's heartbeat and a session that never renders at all
        // looks healthy on the strength of the one before it.
        frame.Channel.Receive(new PageMessage { Kind = PageMessage.KindHello }, frame.Clock.UtcNow);

        frame.BrowserIs("active");
        Assert.Equal(BrowserStagePhase.Awaiting, await frame.Stage.TickAsync(None));
        Assert.Null(frame.Channel.LastCheckInUtc);

        frame.Clock.UtcNow += TimeSpan.FromSeconds(61);
        Assert.Equal(BrowserStagePhase.TornDown, await frame.Stage.TickAsync(None));
    }

    [Fact]
    public async Task The_deadline_is_a_fleet_setting()
    {
        using var frame = new StagedFrame(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BrowserStage.CheckInDeadlineKey] = "10s",
            [BrowserStage.RetryDelayKey] = "30s",
        });

        Assert.Equal(TimeSpan.FromSeconds(10), frame.Stage.CheckInDeadline);
        Assert.Equal(TimeSpan.FromSeconds(30), frame.Stage.RetryDelay);

        frame.BrowserIs("active");
        await frame.Stage.TickAsync(None);

        frame.Clock.UtcNow += TimeSpan.FromSeconds(11);
        Assert.Equal(BrowserStagePhase.TornDown, await frame.Stage.TickAsync(None));
    }

    [Fact]
    public async Task The_screen_is_taken_before_anything_is_stopped()
    {
        using var frame = new StagedFrame();
        frame.BrowserIs("active");
        frame.Compositor(running: true);

        await frame.Stage.TickAsync(None);

        var stoppedWhenTaken = -1;
        frame.Terminals.OnActivate = _ => stoppedWhenTaken =
            frame.Systemd.Commands.Count(command => command.StartsWith("stop ", StringComparison.Ordinal))
            + frame.Session.Commands.Count(command => command.Contains(" stop ", StringComparison.Ordinal));

        frame.Clock.UtcNow += TimeSpan.FromSeconds(61);
        Assert.Equal(BrowserStagePhase.TornDown, await frame.Stage.TickAsync(None));

        // Stopping the getty kills the compositor, the compositor drops DRM master, and the panel
        // falls back to whatever text is on the product's terminal — a login prompt, for as long as
        // it takes anything to notice. Taking the screen first means the compositor dies on a
        // terminal nobody is looking at and the panel goes straight from the empty desktop to the
        // explanation.
        Assert.Equal(0, stoppedWhenTaken);
        Assert.Equal(ScreenOwner.Agent, frame.Screen.Held);
        Assert.Equal(TtyTerminal.AgentTerminal, frame.Terminals.Active);
    }

    [Fact]
    public async Task The_retry_narrates_on_the_console_until_there_is_something_else_to_show()
    {
        using var frame = new StagedFrame();
        frame.BrowserIs("active");
        frame.Compositor(running: true);

        await frame.Stage.TickAsync(None);
        frame.Clock.UtcNow += TimeSpan.FromSeconds(61);
        await frame.Stage.TickAsync(None);

        // The teardown stopped the getty, so the compositor is gone with it.
        frame.Compositor(running: false);
        frame.Clock.UtcNow += TimeSpan.FromMinutes(2);
        Assert.Equal(BrowserStagePhase.Console, await frame.Stage.TickAsync(None));

        // Nothing hands the panel back at that line, and the omission is the design: the getty has
        // been started but the compositor takes seconds to appear, so handing it back here would
        // put the panel on a terminal showing a login prompt and hand it to a compositor that has
        // not started.
        Assert.Equal(ScreenOwner.Agent, frame.Screen.Held);

        frame.Compositor(running: true);
        frame.Clock.UtcNow += frame.Screen.Settle + TimeSpan.FromSeconds(1);
        Assert.Equal(ScreenOwner.Product, await frame.Screen.ReconcileAsync(None));
    }

    [Fact]
    public async Task A_page_is_not_judged_on_a_panel_it_is_not_being_shown_on()
    {
        // §5.5 lets a person log in on another terminal, and while they are there the handover
        // stands aside — so the product's terminal is not in front and the page has no way to
        // render. Counting that against the deadline would tear the session down for something
        // nobody did wrong. The deadline is measured from when the panel is actually the
        // product's.
        using var frame = new StagedFrame();
        frame.BrowserIs("active");
        frame.Compositor(running: true);

        Assert.Equal(BrowserStagePhase.Awaiting, await frame.Stage.TickAsync(None));

        // Somebody pressed Ctrl+Alt+F3 and logged in, so the handover stands aside and the
        // product's terminal never comes to the front.
        frame.Terminals.Active = 3;
        await frame.Screen.ReconcileAsync(None);
        Assert.Null(frame.Screen.Held);

        // Held is null rather than Agent, and that distinction is load-bearing: an unknown panel is
        // not evidence of anything, so the deadline still applies. It is suppressed only once the
        // agent's console is confirmed to be the thing in front.
        frame.Terminals.Active = TtyTerminal.AgentTerminal;
        Assert.True(await frame.Screen.TakeAsync(None));
        Assert.Equal(ScreenOwner.Agent, frame.Screen.Held);

        frame.Clock.UtcNow += TimeSpan.FromMinutes(10);
        Assert.Equal(BrowserStagePhase.Awaiting, await frame.Stage.TickAsync(None));
        Assert.Equal(0, frame.Stage.Teardowns);
    }

    [Fact]
    public void The_page_is_sent_the_same_narration_the_console_renders()
    {
        var now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

        var status = AgentStatusFactory.Green() with
        {
            Drifted = true,
            Narration = new Narration
            {
                Detected = "The speaker volume setting is not what it should be.",
                WhyItMatters = "Nobody on a call would be able to hear you.",
                Action = "amixer -c 2 sset 'PCM',1 100%",
                ActionGloss = "Turning the speaker back up to the level it should be at.",
            },
            Reconcile = new ReconcileNarration
            {
                Resource = "audio.mixer.pcm1-playback-volume",
                Attempt = 2,
                AttemptBudget = 5,
                Countdown = new CountdownState(TimeSpan.FromSeconds(60), now + TimeSpan.FromSeconds(42), true),
            },
            Supervision = new SupervisionAnnotation
            {
                Behaviour = Supervisor.KioskLiveness,
                LastActionUtc = now,
                ActionsInWindow = 5,
                AtFaultLevel = true,
            },
        };

        var message = BrowserStage.Compose(status, now);

        // §2.7 items 1, 2, 3, 4 and 5, all present on the browser surface — §2.6's "any drift
        // stops the product" is what makes the page render them instead of the photos.
        Assert.False(message.ProductRuns);
        Assert.Equal("The speaker volume setting is not what it should be.", message.Detected);
        Assert.Equal("Nobody on a call would be able to hear you.", message.WhyItMatters);
        Assert.Equal("amixer -c 2 sset 'PCM',1 100%", message.Action);
        Assert.Equal(42, message.CountdownSeconds);
        Assert.Equal(2, message.Attempt);
        Assert.Equal(5, message.AttemptBudget);

        // The supervision annotation reaches the frame only at fault level.
        Assert.NotNull(message.SupervisionOverlay);
        Assert.Null(BrowserStage.Compose(status with { Supervision = null }, now).SupervisionOverlay);
    }

    /// <summary>A browser stage over a green frame, with the two systemd managers scripted.</summary>
    private sealed class StagedFrame : IDisposable
    {
        public StagedFrame(IReadOnlyDictionary<string, string>? settings = null)
        {
            Hub = new AgentStatusHub(AgentStatusFactory.Green());

            // The same forward reference AgentHost opens, for the same reason: the handover reads
            // the stage's phase and the stage takes the screen, so one of the two has to be wired
            // after the other exists.
            BrowserStage? stage = null;
            Screen = new ScreenHandover(
                Terminals,
                Processes,
                Clock,
                Log,
                () => stage?.Phase ?? BrowserStagePhase.Console);

            Stage = new BrowserStage(new BrowserStageServices
            {
                Channel = Channel,
                Session = Session,
                SystemControl = Systemd,
                Hub = Hub,
                Telemetry = Telemetry,
                Clock = Clock,
                Log = Log,
                Interlock = Interlock,
                Screen = Screen,
                Values = FleetValues.From(settings ?? new Dictionary<string, string>(StringComparer.Ordinal)),
                DeviceId = "TEST-DEVI-CEID-0001",
            });

            stage = Stage;

            // The real supervisor over the same channel, interlock and clock, because the defect
            // this fixture now has to be able to reproduce lives in the seam between the two: what
            // Supervisor.RestartBrowserAsync leaves behind is what BrowserStage.TickAsync reads
            // next. A hand-rolled Channel.Forget() would assert the fix against a stand-in for the
            // thing that caused it.
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
                TimeZone = TimeZoneInfo.Utc,
                DeviceId = "TEST-DEVI-CEID-0001",
            });
        }

        public LocalChannel Channel { get; } = new();

        public StubMemoryProbe Memory { get; } = new();

        public Supervisor Supervisor { get; }

        public FakeUserSession Session { get; } = new();

        public RecordingSystemControl Systemd { get; } = new();

        public AgentStatusHub Hub { get; }

        public RecordingTelemetry Telemetry { get; } = new();

        public ManualClock Clock { get; } = new();

        public RecordingLog Log { get; } = new();

        public SupervisionInterlock Interlock { get; } = new();

        public RecordingVirtualTerminals Terminals { get; } = new(TtyTerminal.ProductTerminal);

        public ScriptedProcessRunner Processes { get; } = new();

        public ScreenHandover Screen { get; }

        public BrowserStage Stage { get; }

        public void BrowserIs(string state) =>
            Session.Answers[IsActive] = new ProcessResult(
                string.Equals(state, "active", StringComparison.Ordinal) ? 0 : 3,
                state,
                string.Empty);

        public void Compositor(bool running) => Processes.CompositorRunning = running;

        public void Dispose()
        {
            Stage.Detach();
            Screen.Dispose();
        }
    }
}
