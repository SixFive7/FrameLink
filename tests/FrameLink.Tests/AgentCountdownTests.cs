using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Stage;
using FrameLink.Agent.State;

namespace FrameLink.Tests;

/// <summary>
/// §2.7 item 4's countdown before the verifying reboot, and decision 25's resolution order.
/// </summary>
public sealed class AgentCountdownTests
{
    [Fact]
    public void The_built_in_default_is_twenty_five_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(25), CountdownDuration.Resolve());
        Assert.Equal(TimeSpan.FromSeconds(25), CountdownDuration.Default);
    }

    [Fact]
    public void Development_runs_use_zero()
    {
        // Decision 25, verbatim. Not a fast countdown — no countdown, because at 79 resources a
        // 25 s pause each is half an hour of waiting nobody is watching.
        Assert.Equal(TimeSpan.Zero, CountdownDuration.Resolve(development: true));
        Assert.Equal(TimeSpan.Zero, CountdownDuration.Resolve(installFlag: "25", development: true));
    }

    [Fact]
    public void The_install_flag_wins_over_the_boot_file_and_over_the_fleet_settings()
    {
        // The reading taken: §2.7's "install-flag/boot file → fleet default → per-device
        // override" is strongest-first, matching §4.3's identically shaped discovery sentence.
        // The alternative reading makes the flag useless on an adopted development frame, which
        // is exactly what a mule is.
        Assert.Equal(
            TimeSpan.FromSeconds(3),
            CountdownDuration.Resolve(installFlag: "3", bootFile: "10", fleetValue: "25"));

        Assert.Equal(
            TimeSpan.FromSeconds(10),
            CountdownDuration.Resolve(bootFile: "10", fleetValue: "25"));

        Assert.Equal(
            TimeSpan.FromSeconds(25),
            CountdownDuration.Resolve(fleetValue: "25"));
    }

    [Fact]
    public void An_unparseable_or_negative_value_falls_through_rather_than_becoming_zero()
    {
        // A typo must not silently remove the one pause a person has to read the screen.
        Assert.Equal(TimeSpan.FromSeconds(9), CountdownDuration.Resolve(installFlag: "twenty", bootFile: "9"));
        Assert.Equal(TimeSpan.FromSeconds(9), CountdownDuration.Resolve(installFlag: "-5", bootFile: "9"));
        Assert.Equal(CountdownDuration.Default, CountdownDuration.Resolve(installFlag: "999999"));
    }

    [Fact]
    public void Both_flag_spellings_are_read_from_the_command_line()
    {
        Assert.Equal(("7", false), CountdownDuration.ReadFlags(["run", "--countdown-seconds", "7"]));
        Assert.Equal(("7", false), CountdownDuration.ReadFlags(["run", "--countdown-seconds=7"]));
        Assert.Equal((null, true), CountdownDuration.ReadFlags(["run", "--development"]));
    }

    [Fact]
    public async Task A_zero_countdown_does_not_pause_or_publish_anything()
    {
        var clock = new ManualClock();
        var countdown = new RebootCountdown(clock);
        var published = 0;

        var skipped = await countdown.RunAsync(
            TimeSpan.Zero,
            _ => published++,
            TestContext.Current.CancellationToken);

        Assert.False(skipped);
        Assert.Equal(0, published);
        Assert.Empty(clock.Delays);
    }

    [Fact]
    public async Task A_countdown_publishes_a_shrinking_remainder_until_it_expires()
    {
        var clock = new ManualClock();
        var countdown = new RebootCountdown(clock);
        var remaining = new List<double>();

        await countdown.RunAsync(
            TimeSpan.FromSeconds(1),
            state => remaining.Add(state.Remaining(clock.UtcNow).TotalMilliseconds),
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(remaining);
        Assert.True(remaining[0] > remaining[^1], "the remaining time has to shrink");
        Assert.All(remaining, value => Assert.True(value >= 0));
    }

    [Fact]
    public async Task Reboot_now_ends_the_countdown_at_once()
    {
        // §2.7 item 4's skip: a tap on the touchscreen, and the same skip available remotely.
        // A method rather than an input device, so both surfaces reach the same thing.
        var clock = new ManualClock();
        var countdown = new RebootCountdown(clock);

        countdown.SkipNow();
        var skipped = await countdown.RunAsync(
            TimeSpan.FromSeconds(25),
            _ => { },
            TestContext.Current.CancellationToken);

        Assert.True(skipped);
        Assert.Equal(1, countdown.Skips);
        Assert.Empty(clock.Delays);
    }

    [Fact]
    public async Task The_countdown_runs_before_the_reboot_and_the_screen_says_so()
    {
        var resource = new ScriptedResource("spy", "want", "have-not");
        using var harness = new ReconcileHarness(
            new ReconcileOptions { Countdown = TimeSpan.FromSeconds(25) },
            resource);

        var frames = new List<string>();
        using var subscription = harness.Hub.Subscribe(status =>
        {
            if (status.Reconcile.Countdown is not null)
            {
                frames.Add(StageRenderer.Render(status, harness.Clock.UtcNow, 0, 160, 30, colour: false));
            }
        });

        await harness.PassAsync();

        Assert.NotEmpty(frames);
        Assert.Contains(frames, frame => frame.Contains("Restarting", StringComparison.Ordinal));
        Assert.Contains(frames, frame => frame.Contains("Restart now", StringComparison.Ordinal));
        Assert.Single(harness.Boundary.Crossings);
    }
}

/// <summary>
/// §2.7 items 1–7 as rendered text. The renderer is a pure function, so every claim §2.7 makes
/// about the repair screen is an assertion over a string.
/// </summary>
public sealed class AgentReconcileNarrationTests
{
    private static AgentStatus Base => new()
    {
        Condition = DeviceStateLadder.Starting,
        DeviceId = "TEST-DEVI-CEID-0001",
    };

    [Fact]
    public void The_attempt_number_is_rendered_as_attempt_n_of_the_budget()
    {
        // §2.7 item 5's own example is "Attempt 2 of 5", and the budget is what makes it mean
        // something: attempt 2 of 5 is patience, attempt 2 of 2 is nearly over.
        var frame = StageRenderer.Render(
            Base with
            {
                Reconcile = new ReconcileNarration { Attempt = 2, AttemptBudget = 5, Resource = "cpu.governor.performance" },
            },
            DateTimeOffset.UnixEpoch,
            tick: 0,
            columns: 100,
            rows: 24,
            colour: false);

        Assert.Contains("Attempt 2 of 5", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void The_backoff_shows_the_remaining_wait_so_a_pause_is_never_a_hang()
    {
        var now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

        var frame = StageRenderer.Render(
            Base with
            {
                Reconcile = new ReconcileNarration
                {
                    Attempt = 3,
                    AttemptBudget = 5,
                    BackoffTotal = TimeSpan.FromSeconds(60),
                    BackoffEndsAt = now + TimeSpan.FromSeconds(42),
                },
            },
            now,
            tick: 0,
            columns: 100,
            rows: 24,
            colour: false);

        Assert.Contains("trying again in 42s", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void The_countdown_outranks_the_attempt_line_because_it_is_asking_for_something()
    {
        var now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

        var frame = StageRenderer.Render(
            Base with
            {
                Reconcile = new ReconcileNarration
                {
                    Attempt = 2,
                    AttemptBudget = 5,
                    Countdown = new CountdownState(TimeSpan.FromSeconds(25), now + TimeSpan.FromSeconds(9), Skippable: true),
                },
            },
            now,
            tick: 0,
            columns: 160,
            rows: 24,
            colour: false);

        Assert.Contains("in 9s", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("Attempt 2 of 5", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void An_escalation_that_nobody_has_heard_says_so_rather_than_claiming_otherwise()
    {
        // §2.7 item 7, and the honest half of §2.5: a frame that gave up while its server was
        // unreachable has notified nobody, and the screen is the only place anyone will find out.
        var frame = StageRenderer.Render(
            Base with
            {
                Reconcile = new ReconcileNarration { Attempt = 5, Escalations = 1, AdminNotified = false },
            },
            DateTimeOffset.UnixEpoch,
            tick: 0,
            columns: 160,
            rows: 30,
            colour: false);

        Assert.Contains("nobody has been told", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void An_escalation_the_fleet_manager_received_points_at_the_person_who_can_fix_it()
    {
        var frame = StageRenderer.Render(
            Base with
            {
                Reconcile = new ReconcileNarration { Attempt = 5, Escalations = 1, AdminNotified = true },
            },
            DateTimeOffset.UnixEpoch,
            tick: 0,
            columns: 160,
            rows: 30,
            colour: false);

        Assert.Contains("Fleet Manager has been told", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void A_halted_frame_says_it_has_stopped_trying()
    {
        var frame = StageRenderer.Render(
            Base with { Reconcile = new ReconcileNarration { Halted = true, Escalations = 2, Attempt = 5 } },
            DateTimeOffset.UnixEpoch,
            tick: 0,
            columns: 160,
            rows: 30,
            colour: false);

        Assert.Contains("stopped trying", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blocked_resource_names_what_it_is_waiting_for()
    {
        // §2.2: a dependent is marked Blocked(dependency) rather than being left to fail
        // confusingly on its own — and "confusingly" is a property of what the reader sees.
        var frame = StageRenderer.Render(
            Base with
            {
                Resources =
                [
                    new ResourceStatus
                    {
                        Name = "cpu.governor.performance",
                        Kind = ResourceStatusKind.Blocked,
                        BlockedBy = "unit.cpu-performance.enabled",
                    },
                ],
            },
            DateTimeOffset.UnixEpoch,
            tick: 0,
            columns: 120,
            rows: 30,
            colour: false);

        Assert.Contains("waiting for unit.cpu-performance.enabled", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void The_exact_change_and_its_plain_language_gloss_are_both_rendered()
    {
        // §2.7 item 3 asks for both registers, and they are different sentences on purpose: one
        // is what a person can check afterwards, the other is what they can understand now.
        var frame = StageRenderer.Render(
            Base with
            {
                Narration = new Narration
                {
                    Detected = "The screen on this frame is not switched on yet.",
                    WhyItMatters = "Until it is, this frame cannot show you anything at all.",
                    Action = "append 'dtoverlay=vc4-kms-dsi-waveshare-panel-v2,10_1_inch_a' to /boot/firmware/config.txt",
                    ActionGloss = "Telling this frame which screen is attached to it.",
                },
            },
            DateTimeOffset.UnixEpoch,
            tick: 0,
            columns: 160,
            rows: 40,
            colour: false);

        Assert.Contains("dtoverlay=vc4-kms-dsi-waveshare-panel-v2", frame, StringComparison.Ordinal);
        Assert.Contains("Telling this frame which screen is attached", frame, StringComparison.Ordinal);
        Assert.Contains("cannot show you anything", frame, StringComparison.Ordinal);
    }
}
