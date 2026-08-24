using System.Reflection;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Stage;
using FrameLink.Agent.State;
using FrameLink.Protocol;

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
/// The arithmetic that forced it: 80 resources at decision 48's 60 s default is 80 minutes of
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
/// Decision 53 — the provisioning pace, the countdown's sibling for a frame being set up.
/// </summary>
/// <remarks>
/// <para>
/// Decision 51 cut a bare provision from about 108 minutes to 30 by taking the countdown away
/// from it, and took with it the only thing that let a person watch one happen: 79 screens now
/// paint at machine speed. This puts that back as a fleet setting rather than as a default — zero
/// unless an operator raises it, so every assertion in <c>AgentCountdownScopeTests</c> above still
/// describes the shipping behaviour.
/// </para>
/// <para>
/// The two settings meet in one place, <see cref="CountdownScope.ForReboot"/>, which is what makes
/// them siblings rather than two mechanisms: one function, two durations, and the question of
/// which applies answered by the same durable "has this frame ever been green".
/// </para>
/// </remarks>
public sealed class AgentProvisioningPaceTests
{
    [Fact]
    public void The_built_in_pace_is_zero_so_nothing_changes_until_an_operator_asks()
    {
        Assert.Equal(TimeSpan.Zero, ProvisioningPace.Default);
        Assert.Equal(TimeSpan.Zero, ProvisioningPace.Resolve());
        Assert.Equal(TimeSpan.Zero, ProvisioningPace.Resolve(fleetValue: null));
        Assert.Equal(TimeSpan.Zero, ProvisioningPace.Resolve(fleetValue: "  "));
    }

    [Fact]
    public void The_fleet_value_is_what_raises_it()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), ProvisioningPace.Resolve(fleetValue: "20"));
        Assert.Equal(TimeSpan.FromSeconds(0.5), ProvisioningPace.Resolve(fleetValue: "0.5"));
        Assert.Equal(TimeSpan.Zero, ProvisioningPace.Resolve(fleetValue: "0"));
    }

    [Fact]
    public void A_mistyped_pace_falls_back_to_zero_rather_than_to_a_pause()
    {
        // The opposite fallback from CountdownDuration's, on purpose. A typo there must not
        // silently remove the one pause a person has to read a repair; a typo here must not
        // silently add an hour and a half to a provision nobody is watching.
        Assert.Equal(TimeSpan.Zero, ProvisioningPace.Resolve(fleetValue: "slowly"));
        Assert.Equal(TimeSpan.Zero, ProvisioningPace.Resolve(fleetValue: "-5"));
        Assert.Equal(TimeSpan.Zero, ProvisioningPace.Resolve(fleetValue: "999999"));
    }

    [Fact]
    public void The_development_switch_forces_zero_for_the_pace_too()
    {
        // §2.7 keeps `--development` as a binary switch rather than a setting, and decision 53
        // does not give it a second meaning: a development run pauses for nothing at all, on a
        // frame that has been green and on one that has not.
        Assert.Equal(TimeSpan.Zero, ProvisioningPace.Resolve(development: true));
        Assert.Equal(TimeSpan.Zero, ProvisioningPace.Resolve(fleetValue: "60", development: true));
    }

    [Fact]
    public void The_setting_key_is_the_one_the_fleet_manager_gui_offers()
    {
        // Same requirement as the repair countdown's: a key the agent reads but nobody can type is
        // not a setting. Both live under the same heading in the catalog, so reading one tells an
        // operator the other exists.
        Assert.Equal("provisioning.paceSeconds", ProvisioningPace.SettingKey);
        Assert.NotEqual(CountdownDuration.SettingKey, ProvisioningPace.SettingKey);

        var catalog = File.ReadAllText(Path.Combine(
            GuiFreshnessTests.RepositoryRoot(),
            "src",
            "FrameLink.Control",
            "gui",
            "src",
            "lib",
            "settings-catalog.ts"));

        Assert.Contains($"'{ProvisioningPace.SettingKey}'", catalog, StringComparison.Ordinal);
    }

    [Fact]
    public void A_frame_that_has_never_been_green_gets_the_pace_and_not_the_repair_countdown()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(15),
            CountdownScope.ForReboot(CountdownDuration.Default, hasEverBeenInSync: false, TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void A_frame_that_has_been_green_gets_the_repair_countdown_and_never_the_pace()
    {
        // The pace belongs to setting a frame up, and a frame in somebody's living room is past
        // that. Leaving a raised pace on a converged fleet must not change a single repair.
        Assert.Equal(
            CountdownDuration.Default,
            CountdownScope.ForReboot(CountdownDuration.Default, hasEverBeenInSync: true, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Omitting_the_pace_leaves_decision_51_exactly_as_it_was()
    {
        // Every caller written before decision 53 asks for two arguments and means "no pause".
        Assert.Equal(TimeSpan.Zero, CountdownScope.ForReboot(CountdownDuration.Default, hasEverBeenInSync: false));
        Assert.Equal(
            TimeSpan.Zero,
            CountdownScope.ForReboot(CountdownDuration.Default, hasEverBeenInSync: false, ProvisioningPace.Default));
    }

    [Fact]
    public void The_pace_is_read_at_each_reboot_rather_than_once_at_startup()
    {
        // Sharper than the countdown's version of this: the frame being paced is mid-provision, so
        // its settings are arriving for the first time while the loop is already running. An
        // operator who raises the pace to watch a frame that is halfway through is served by the
        // next reboot rather than by the next reflash.
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        var options = new ReconcileOptions
        {
            ProvisioningPaceSource = () => ProvisioningPace.Resolve(
                settings.GetValueOrDefault(ProvisioningPace.SettingKey)),
        };

        Assert.Equal(TimeSpan.Zero, options.CurrentProvisioningPace());

        settings[ProvisioningPace.SettingKey] = "8";
        Assert.Equal(TimeSpan.FromSeconds(8), options.CurrentProvisioningPace());
    }

    [Fact]
    public async Task A_raised_pace_pauses_before_every_provisioning_reboot()
    {
        var resources = new[]
        {
            new ScriptedResource("first", "want", "have-not"),
            new ScriptedResource("second", "want", "have-not"),
        };

        using var harness = new ReconcileHarness(
            new ReconcileOptions
            {
                Countdown = CountdownDuration.Default,
                ProvisioningPace = TimeSpan.FromSeconds(15),
            },
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
        Assert.Equal(2, harness.Boundary.Crossings.Count);
        Assert.True(countdowns > 0, "a paced provision has to narrate the pause it is taking");

        // The pace, twice, and nothing else: the repair countdown is 60 s and would be visible
        // immediately in this total if the scope rule had let it through.
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            harness.Clock.Delays.Aggregate(TimeSpan.Zero, (total, step) => total + step));
    }

    [Fact]
    public async Task A_paced_provisioning_reboot_can_still_be_skipped()
    {
        // Because it is the same countdown, "Restart now" works during a provision without a
        // second implementation — which is the argument for putting this at
        // CountdownScope.ForReboot rather than beside it. An operator who has seen enough gets
        // the frame back at full speed by tapping the screen.
        var resource = new ScriptedResource("only", "want", "have-not");
        using var harness = new ReconcileHarness(
            new ReconcileOptions { ProvisioningPace = TimeSpan.FromMinutes(5) },
            resource);

        harness.Countdown.SkipNow();
        await harness.ConvergeAsync();

        Assert.Equal(1, harness.Countdown.Skips);
        Assert.Empty(harness.Clock.Delays);
    }

    [Fact]
    public async Task A_pace_left_raised_does_not_follow_the_frame_into_service()
    {
        // The whole risk of adding this setting: an operator raises it to watch a build, forgets
        // it, and every frame in the fleet inherits a pause on a path it does not belong to.
        var resource = new ScriptedResource("only", "want", "have-not");
        using var harness = new ReconcileHarness(
            new ReconcileOptions
            {
                Countdown = TimeSpan.Zero,
                ProvisioningPace = TimeSpan.FromSeconds(15),
            },
            resource);

        await harness.ConvergeAsync();
        harness.Clock.Delays.Clear();

        resource.Drift();
        await harness.PassAsync();

        Assert.NotNull(harness.Journal.Read().FirstInSyncUtc);
        Assert.Empty(harness.Clock.Delays);
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
    public void The_attempt_number_is_rendered_as_the_item_and_attempt_n_of_the_budget()
    {
        // §2.7 item 5 in the operator's own words (decision 70): one item at a time, named,
        // with its attempt count. The budget is what makes the count mean something — attempt 2
        // of 3 is patience, attempt 3 of 3 is the last one — so the item and the count travel
        // together as one sentence rather than as a bare number beside a bar.
        var frame = StageRenderer.Render(
            Base with
            {
                Reconcile = new ReconcileNarration { Attempt = 2, AttemptBudget = 3, Resource = "cpu.governor.performance" },
            },
            DateTimeOffset.UnixEpoch,
            tick: 0,
            columns: 100,
            rows: 24,
            colour: false);

        Assert.Contains("cpu.governor.performance attempt 2 of 3", frame, StringComparison.Ordinal);
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
        Assert.DoesNotContain("trying again in", frame, StringComparison.Ordinal);
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
    public void A_stopped_frame_never_renders_as_a_working_one()
    {
        // Decision 70, and the single most important assertion about this screen. Before it, a
        // resource that had permanently given up reached the renderer's "Attempt N of M" branch
        // on nothing more than Attempt > 0 and was drawn beside a *travelling marquee* — an
        // animation whose whole purpose is to prove that a pause is not a hang. That picture was
        // repainted on every boot for ever, and it is what made a frame look like it was
        // rebooting endlessly.
        var status = Base with
        {
            Resources =
            [
                new ResourceStatus
                {
                    Name = "audio.mixer.pcm-volume",
                    Kind = ResourceStatusKind.Escalated,
                    Delta = "expected '20', observed '0'",
                    Attempts = 3,
                    AttemptBudget = 3,
                    Escalations = 1,
                },
            ],
            Reconcile = new ReconcileNarration
            {
                Resource = "audio.mixer.pcm-volume",
                Attempt = 3,
                AttemptBudget = 3,
                Escalations = 1,
                AdminNotified = true,
            },
        };

        var first = StageRenderer.Render(status, DateTimeOffset.UnixEpoch, tick: 0, 160, 30, colour: false);
        var later = StageRenderer.Render(status, DateTimeOffset.UnixEpoch, tick: 7, 160, 30, colour: false);

        // The operator's wording, and the delta rendered rather than re-derived.
        Assert.Contains("audio.mixer.pcm-volume failed after 3 tries", first, StringComparison.Ordinal);
        Assert.Contains("expected '20', observed '0'", first, StringComparison.Ordinal);

        // Nothing that reads as work in progress.
        Assert.DoesNotContain("attempt 3 of 3", first, StringComparison.Ordinal);
        Assert.DoesNotContain("Attempt", first, StringComparison.Ordinal);

        // And nothing that moves. Two ticks of the same status paint identically, which is the
        // mechanical form of "a stopped item must not render like a running one": every animated
        // element in this renderer is a function of the tick.
        Assert.Equal(first, later);
    }

    [Fact]
    public void A_stopped_frame_says_who_to_ask_and_where_the_button_is()
    {
        // §2.7 items 8 and 9. The contact comes off the frame's own state, so this assertion holds
        // with no server anywhere in the test — which is the whole point of decision 71 — and the
        // console names the Fleet Manager rather than offering a button it cannot implement
        // (decision 72).
        var frame = StageRenderer.Render(
            Base with
            {
                Contact = new OperatorContact { Name = "Jori", Contact = "06 12 34 56 78", UpdatedUtc = DateTimeOffset.UnixEpoch },
                Reconcile = new ReconcileNarration
                {
                    Resource = "audio.mixer.pcm-volume",
                    Attempt = 3,
                    AttemptBudget = 3,
                    Escalations = 1,
                    AdminNotified = true,
                },
            },
            DateTimeOffset.UnixEpoch,
            tick: 0,
            columns: 160,
            rows: 30,
            colour: false);

        Assert.Contains("Ask Jori — 06 12 34 56 78.", frame, StringComparison.Ordinal);
        Assert.Contains("The buttons that restart it and switch it off are in the Fleet Manager", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void A_frame_that_was_never_told_who_to_ask_still_says_somebody_is_needed()
    {
        var frame = StageRenderer.Render(
            Base with
            {
                Reconcile = new ReconcileNarration
                {
                    Resource = "audio.mixer.pcm-volume",
                    Attempt = 3,
                    AttemptBudget = 3,
                    Escalations = 1,
                },
            },
            DateTimeOffset.UnixEpoch,
            tick: 0,
            columns: 160,
            rows: 30,
            colour: false);

        Assert.Contains("Ask whoever looks after your Fleet Manager", frame, StringComparison.Ordinal);
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
