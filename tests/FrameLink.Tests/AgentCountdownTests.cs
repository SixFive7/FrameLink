using System.Reflection;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Stage;
using FrameLink.Agent.State;

namespace FrameLink.Tests;

/// <summary>
/// §2.7 item 4's countdown before the verifying reboot, and decision 48's resolution order.
/// </summary>
/// <remarks>
/// Decision 48 replaced decision 25's chain with <b>per-device override → fleet default →
/// 60 s</b>, deleting the install flag and the boot-partition file that sat above them. The top
/// two levels are resolved by the Fleet Manager before a frame ever sees them — that ordering is
/// asserted where it happens, in <c>ControlSettingsTests</c> — so what is asserted here is the
/// agent's half: the effective value it was pushed, against the built-in default.
/// </remarks>
public sealed class AgentCountdownTests
{
    [Fact]
    public void The_built_in_default_is_sixty_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), CountdownDuration.Resolve());
        Assert.Equal(TimeSpan.FromSeconds(60), CountdownDuration.Default);
    }

    [Fact]
    public void The_fleet_value_wins_over_the_built_in_default()
    {
        // The whole configured chain, from the frame's side. Which of the two configured levels
        // produced this string is the Fleet Manager's business (§3.4) and deliberately invisible
        // here: the agent is told one effective value and has nothing to arbitrate.
        Assert.Equal(TimeSpan.FromSeconds(10), CountdownDuration.Resolve(fleetValue: "10"));
        Assert.Equal(TimeSpan.Zero, CountdownDuration.Resolve(fleetValue: "0"));
        Assert.Equal(CountdownDuration.Default, CountdownDuration.Resolve(fleetValue: null));
        Assert.Equal(CountdownDuration.Default, CountdownDuration.Resolve(fleetValue: "   "));
    }

    [Fact]
    public void There_is_no_configuration_source_left_outside_the_fleet_manager()
    {
        // Decision 48 removed the install flag and, with it, the boot-partition file that existed
        // only as its local pre-adoption sibling. Asserted as an absence on the type itself,
        // because the failure this guards against is somebody re-adding a "small" local override
        // and reintroducing exactly the channel the operator deleted.
        var members = typeof(CountdownDuration)
            .GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Select(member => member.Name)
            .ToList();

        Assert.DoesNotContain("Flag", members);
        Assert.DoesNotContain("Variable", members);
        Assert.DoesNotContain("BootFileKey", members);
        Assert.Contains("SettingKey", members);
    }

    [Fact]
    public void The_setting_key_is_the_one_the_fleet_manager_gui_offers()
    {
        // A key the agent reads but nobody can type is not a setting, and with the flag gone it
        // would leave the countdown permanently un-configurable. The GUI catalog is the only
        // place a human ever writes this string.
        Assert.Equal("repair.countdownSeconds", CountdownDuration.SettingKey);

        var catalog = File.ReadAllText(Path.Combine(
            GuiFreshnessTests.RepositoryRoot(),
            "src",
            "FrameLink.Control",
            "gui",
            "src",
            "lib",
            "settings-catalog.ts"));

        Assert.Contains($"'{CountdownDuration.SettingKey}'", catalog, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unadopted_frame_can_only_ever_get_the_built_in_default()
    {
        // §3.3: a pending device receives nothing — no configuration at all. Both remaining
        // levels are Fleet Manager settings, so this is not a case to handle but the shape of the
        // decision: with no server answer there is nothing above the default.
        Assert.Equal(CountdownDuration.Default, CountdownDuration.Resolve(fleetValue: null));
    }

    [Fact]
    public void The_development_switch_forces_zero_and_outranks_the_fleet_value()
    {
        // Kept deliberately (decision 48). §2.7's "development runs use 0" is otherwise
        // unreachable, because a mule being provisioned from scratch is unadopted and §3.3 gives
        // it nothing. An argument to the binary is not a setting: nothing writes it, nothing
        // persists it, and no operator can push it.
        Assert.Equal(TimeSpan.Zero, CountdownDuration.Resolve(development: true));
        Assert.Equal(TimeSpan.Zero, CountdownDuration.Resolve(fleetValue: "60", development: true));
    }

    [Fact]
    public void An_unparseable_or_negative_value_falls_back_to_the_default_rather_than_zero()
    {
        // A typo must not silently remove the one pause a person has to read the screen — and it
        // is now the only way to reach this path, since the fleet setting is the only input.
        Assert.Equal(CountdownDuration.Default, CountdownDuration.Resolve(fleetValue: "twenty"));
        Assert.Equal(CountdownDuration.Default, CountdownDuration.Resolve(fleetValue: "-5"));
        Assert.Equal(CountdownDuration.Default, CountdownDuration.Resolve(fleetValue: "999999"));
    }

    [Fact]
    public void The_development_switch_is_read_from_the_command_line()
    {
        Assert.True(CountdownDuration.IsDevelopmentRun(["run", "--development"]));
        Assert.False(CountdownDuration.IsDevelopmentRun(["run"]));
        Assert.False(CountdownDuration.IsDevelopmentRun(["run", "--development-mode"]));
    }

    [Fact]
    public void The_countdown_is_read_at_each_reboot_rather_than_once_at_startup()
    {
        // The frame's settings arrive after it starts and can change while it runs, and with the
        // flag gone they are the only source there is. A value captured at construction would
        // read an empty map and pin every frame to 60 s for the life of the process.
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        var options = new ReconcileOptions
        {
            CountdownSource = () => CountdownDuration.Resolve(
                settings.GetValueOrDefault(CountdownDuration.SettingKey)),
        };

        Assert.Equal(CountdownDuration.Default, options.CurrentCountdown());

        settings[CountdownDuration.SettingKey] = "5";
        Assert.Equal(TimeSpan.FromSeconds(5), options.CurrentCountdown());

        settings[CountdownDuration.SettingKey] = "12";
        Assert.Equal(TimeSpan.FromSeconds(12), options.CurrentCountdown());
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
            CountdownDuration.Default,
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
            new ReconcileOptions { Countdown = CountdownDuration.Default },
            resource);

        // Green first, then drift. Decision 51 means the countdown belongs to a repair on a frame
        // that has been working, so a provisioning reboot is no longer a place to look for it.
        await harness.ConvergeAsync();
        resource.Drift();

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
    }
}

/// <summary>
/// Decision 51 — the countdown applies to drift repair, not to initial provisioning (§2.7).
/// </summary>
/// <remarks>
/// <para>
/// The arithmetic that forced it: 79 resources at decision 48's 60 s default is 79 minutes of
/// countdown against 29 minutes of measured reboot, so roughly three quarters of a bare provision
/// would be spent holding a screen still for somebody who is not there. §2.7's reason for the
/// pause is a viewer in front of a working frame watching a repair, and a frame that has never
/// displayed anything has neither the viewer nor the product being interrupted.
/// </para>
/// <para>
/// The rule is one function, <see cref="CountdownScope.ForReboot"/>, reading one durable field.
/// Both halves are asserted here: the decision on its own, and the loop actually obeying it
/// across the reboot and the process restart that the field has to survive.
/// </para>
/// </remarks>
public sealed class AgentCountdownScopeTests
{
    [Fact]
    public void A_frame_that_has_never_been_green_gets_no_countdown()
    {
        Assert.Equal(TimeSpan.Zero, CountdownScope.ForReboot(CountdownDuration.Default, hasEverBeenInSync: false));
        Assert.Equal(TimeSpan.Zero, CountdownScope.ForReboot(TimeSpan.FromMinutes(10), hasEverBeenInSync: false));
    }

    [Fact]
    public void A_frame_that_has_been_green_gets_the_configured_countdown_unchanged()
    {
        // Scope only ever removes the pause. Whatever decision 48's chain resolved to is passed
        // through untouched, including a fleet value the operator deliberately set to zero.
        Assert.Equal(CountdownDuration.Default, CountdownScope.ForReboot(CountdownDuration.Default, hasEverBeenInSync: true));
        Assert.Equal(TimeSpan.FromSeconds(25), CountdownScope.ForReboot(TimeSpan.FromSeconds(25), hasEverBeenInSync: true));
        Assert.Equal(TimeSpan.Zero, CountdownScope.ForReboot(TimeSpan.Zero, hasEverBeenInSync: true));
    }

    [Fact]
    public void The_development_switch_still_forces_zero_on_a_frame_that_has_been_green()
    {
        // §2.7 keeps `--development` as the local debugging switch, and scope sits below it: the
        // flag has already collapsed the duration to zero before this is asked anything.
        Assert.Equal(
            TimeSpan.Zero,
            CountdownScope.ForReboot(CountdownDuration.Resolve(fleetValue: "60", development: true), hasEverBeenInSync: true));
    }

    [Fact]
    public async Task Provisioning_a_bare_frame_never_waits_out_a_countdown()
    {
        var resources = new[]
        {
            new ScriptedResource("first", "want", "have-not"),
            new ScriptedResource("second", "want", "have-not"),
            new ScriptedResource("third", "want", "have-not"),
        };

        using var harness = new ReconcileHarness(
            new ReconcileOptions { Countdown = CountdownDuration.Default },
            resources);

        var countdowns = 0;
        using var subscription = harness.Hub.Subscribe(status =>
        {
            if (status.Reconcile.Countdown is not null)
            {
                countdowns++;
            }
        });

        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.Equal(0, countdowns);
        Assert.Empty(harness.Clock.Delays);
        Assert.Equal(3, harness.Boundary.Crossings.Count);
    }

    [Fact]
    public async Task Repairing_drift_on_a_frame_that_has_been_green_waits_out_the_countdown()
    {
        var resource = new ScriptedResource("spy", "want", "have-not");
        using var harness = new ReconcileHarness(
            new ReconcileOptions { Countdown = CountdownDuration.Default },
            resource);

        await harness.ConvergeAsync();
        Assert.Empty(harness.Clock.Delays);

        resource.Drift();
        await harness.PassAsync();

        // §2.7's pause, in the only place it is owed: a repair on a frame somebody is looking at.
        Assert.NotEmpty(harness.Clock.Delays);
        Assert.Equal(CountdownDuration.Default, harness.Clock.Delays.Aggregate(TimeSpan.Zero, (total, step) => total + step));
    }

    [Fact]
    public async Task Being_green_is_recorded_in_the_journal_and_survives_a_restart()
    {
        var resource = new ScriptedResource("spy", "want", "have-not");
        using var harness = new ReconcileHarness(
            new ReconcileOptions { Countdown = CountdownDuration.Default },
            resource);

        Assert.Null(harness.Journal.Read().FirstInSyncUtc);

        await harness.ConvergeAsync();
        var greenAt = harness.Journal.Read().FirstInSyncUtc;
        Assert.NotNull(greenAt);

        // A second journal over the same directory is what an agent restart, an agent update, or
        // the next boot actually looks like. If the field were process state, this is where the
        // frame would forget it had ever worked and hand the next repair the provisioning
        // behaviour.
        var reopened = new ReconcileJournal(harness.Store, harness.Log);
        Assert.Equal(greenAt, reopened.Read().FirstInSyncUtc);
    }

    [Fact]
    public async Task The_first_green_moment_is_recorded_once_and_never_moved()
    {
        var resource = new ScriptedResource("spy", "want", "have-not");
        using var harness = new ReconcileHarness(resource);

        await harness.ConvergeAsync();
        var first = harness.Journal.Read().FirstInSyncUtc;
        Assert.NotNull(first);

        harness.Clock.UtcNow += TimeSpan.FromHours(6);
        await harness.PassAsync();
        resource.Drift();
        await harness.ConvergeAsync();

        Assert.Equal(first, harness.Journal.Read().FirstInSyncUtc);
    }

    [Fact]
    public async Task A_frame_that_never_converges_never_claims_to_have_been_green()
    {
        // The countdown must not appear because a *different* resource went in sync. Nothing is
        // owed to a viewer until the frame has actually shown something, which is every resource
        // at once and not a majority of them.
        var good = new ScriptedResource("good", "want", "have-not");
        var bad = new ScriptedResource("bad", "want", "have-not") { ActHasNoEffect = true };

        using var harness = new ReconcileHarness(
            new ReconcileOptions { Countdown = CountdownDuration.Default, AttemptBudget = 2 },
            good,
            bad);

        await harness.ConvergeAsync();

        Assert.Null(harness.Journal.Read().FirstInSyncUtc);
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
