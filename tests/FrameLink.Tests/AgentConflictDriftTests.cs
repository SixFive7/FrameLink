using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Tests;

/// <summary>
/// <b>Revert-after-verify, and the floor under it</b> — version2.md §2.4 and §2.6, decisions 78
/// and 79.
/// </summary>
/// <remarks>
/// <para>
/// <b>The failure these exist for looked like nothing at all.</b> A mixer value was applied,
/// verified across a reboot, and put back afterwards by a second owner. Every individual pass was a
/// success: the apply worked, the post-boot verify passed, the ledger cleared. So the attempt
/// counter never passed <c>1/3</c>, nothing escalated, decision 68 never stopped the pass, §2.4's
/// protection against an unbounded reboot cycle never engaged, and the Fleet Manager reported the
/// frame as working while it took <b>~25 reboots in eleven minutes, indefinitely</b>. In a household
/// that is a photo frame restarting every twenty-five seconds for ever with nobody told.
/// </para>
/// <para>
/// <b>Two independent mechanisms, tested separately because they have to hold separately.</b>
/// Decision 78 is the diagnosis — the loop remembers that a value has converged and been taken away,
/// and treats a run of that as §2.6's conflict drift. Decision 79 is the floor — past
/// <see cref="ReconcileOptions.RebootFloorCount"/> reboots in
/// <see cref="ReconcileOptions.RebootFloorWindow"/> the device stops rebooting whatever any resource
/// claims, which is the half that has to work when the diagnosis does not fire.
/// </para>
/// <para>
/// <b>The falsification is in the suite rather than performed once and described.</b>
/// <see cref="Without_the_conflict_rule_the_same_frame_reboots_for_ever_and_never_looks_wrong"/>
/// runs the identical scenario with the rule switched off and asserts the measured symptom —
/// thirty reboots, <c>att=1/3</c>, in sync, not stopped. It is the pre-fix behaviour, kept
/// executable.
/// </para>
/// <para>
/// <b>Nothing here has run on hardware.</b>
/// </para>
/// </remarks>
public sealed class AgentConflictDriftTests
{
    /// <summary>The resource that produced the measured livelock, and its measured delta.</summary>
    private const string Mixer = "audio.mixer.pcm0-playback-volume";

    private const string Wanted = "PCM,0=60";

    private static ReconcileOptions Options => new()
    {
        Countdown = TimeSpan.Zero,
        AttemptBudget = 3,
        InitialBackoff = TimeSpan.FromSeconds(30),
        BackoffCap = TimeSpan.FromMinutes(30),
    };

    private static RebootRequest Request => new()
    {
        Resource = Mixer,
        Change = "amixer -c 0 sset PCM,0 60",
        Attempt = 1,
    };

    [Fact]
    public async Task A_value_put_back_after_every_verify_stops_the_frame_instead_of_rebooting_for_ever()
    {
        // WirePlumber's shape exactly: the agent sets 60, the post-boot verify at boot+10 s reads
        // 60 and passes, and the login session comes up a fraction of a second later and puts 37
        // back. The loop is right about every pass and wrong about the frame.
        var resource = new ScriptedResource(Mixer, Wanted, "PCM,0=37") { PutBackAfterVerify = true };
        using var harness = new ReconcileHarness(Options, resource) { Telemetry = { Connected = true } };

        var outcome = await harness.ConvergeAsync(limit: 30);

        Assert.Equal(PassResult.Escalated, outcome.Result);
        Assert.True(harness.Loop.HasStopped, "the frame kept reconciling a value it cannot keep");

        // Three reboots, which is decision 78's threshold and nothing more. The fourth pass finds
        // the third reversion and refuses to act on it.
        Assert.Equal(
            Options.ConflictThreshold,
            harness.Boundary.Crossings.Count);

        var status = ReconcileHarness.StatusOf(outcome, Mixer);

        Assert.Equal(ResourceStatusKind.Escalated, status.Kind);

        // §2.5 rung 2's exact expected-versus-observed survives verbatim in front (decision 70),
        // and the sentence after it is what sends an operator looking for the other owner rather
        // than for a setting that will not apply.
        Assert.Contains("expected 'PCM,0=60'", status.Delta!, StringComparison.Ordinal);
        Assert.Contains("put back every time", status.Delta!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_conflict_rule_a_verify_that_wins_its_race_resets_the_ladder_for_ever()
    {
        // The falsification, and the first half of the mechanism this was built against.
        // ConflictThreshold = 0 is exactly the behaviour before decision 78: the ledger is written
        // and nothing reads it. Every assertion below is what the frame did.
        var resource = new ScriptedResource(Mixer, Wanted, "PCM,0=37") { PutBackAfterVerify = true };
        using var harness = new ReconcileHarness(
            Options with { ConflictThreshold = 0 },
            resource)
        {
            Telemetry = { Connected = true },
        };

        var outcome = await harness.ConvergeAsync(limit: 30);

        // It never converges and it never stops: the run ended because the harness stopped counting.
        Assert.Equal(PassResult.Rebooted, outcome.Result);
        Assert.Equal(30, harness.Boundary.Crossings.Count);
        Assert.Equal(30, resource.PutBacks);
        Assert.False(harness.Loop.HasStopped);

        // And this is what an operator saw while it happened: a healthy row on its first attempt.
        var status = ReconcileHarness.StatusOf(outcome, Mixer);

        Assert.Equal(ResourceStatusKind.InSync, status.Kind);
        Assert.Equal(1, status.Attempts);
        Assert.Equal(3, status.AttemptBudget);
    }

    [Fact]
    public async Task Without_the_conflict_rule_one_won_race_undoes_two_thirds_of_a_spent_budget()
    {
        // The second half of the mechanism, and the reason the frame *did* eventually escalate
        // where the first account said it never would. A verify that loses the race — the session
        // gets there before the agent's post-boot look — is an ordinary failed apply and spends an
        // attempt. A verify that wins clears the ledger outright. So the budget is only ever
        // exhausted by three *consecutive* losses, and a single win in the middle sends the counter
        // back to nothing however wrong the value is at that instant.
        //
        // Decision 65 measured that race at 0.03–0.7 s, which is why it goes both ways, and it is
        // the whole difference between "escalates in 3 reboots" and "escalates in 25".
        var resource = new ScriptedResource(Mixer, Wanted, "PCM,0=37")
        {
            PutBackAfterVerify = true,
            RevertedAtBoot = true,
        };

        using var harness = new ReconcileHarness(
            Options with { ConflictThreshold = 0 },
            resource)
        {
            Telemetry = { Connected = true },
        };

        var boots = 0;
        harness.Boundary.OnBoot = (_, _) =>
        {
            // Lose, lose, win — and round again.
            if (boots++ % 3 < 2)
            {
                resource.Boot();
            }

            return Task.CompletedTask;
        };

        await Step(harness);
        Assert.Equal(1, Entry(harness, Mixer).Attempts);

        await Step(harness);
        Assert.Equal(2, Entry(harness, Mixer).Attempts);

        // The third apply's verify wins the race by a fraction of a second, and the value is put
        // back immediately afterwards — so the frame is exactly as wrong as it was a moment ago.
        await Step(harness);

        Assert.Equal(0, Entry(harness, Mixer).Attempts);
        Assert.False(harness.Loop.HasStopped);

        // And round it goes.
        await Step(harness);
        Assert.Equal(1, Entry(harness, Mixer).Attempts);
    }

    [Fact]
    public async Task However_the_verify_race_falls_the_frame_stops_within_a_bounded_number_of_reboots()
    {
        // The property that replaces the statistical one, and it is a proof rather than an average:
        // every cycle ends in a verify that either won or lost, a lost one advances the attempt
        // counter and a won one advances the reversion counter, and neither is reset by the other's
        // advance. Two consecutive losses is the most an adversary can spend without escalating on
        // the ladder, and three wins is the most it can spend without escalating on the conflict
        // rule — so the worst schedule available to it is lose, lose, win, three times over.
        var resource = new ScriptedResource(Mixer, Wanted, "PCM,0=37")
        {
            PutBackAfterVerify = true,
            RevertedAtBoot = true,
        };

        using var harness = new ReconcileHarness(Options, resource) { Telemetry = { Connected = true } };

        var boots = 0;
        harness.Boundary.OnBoot = (_, _) =>
        {
            if (boots++ % 3 < 2)
            {
                resource.Boot();
            }

            return Task.CompletedTask;
        };

        var outcome = await harness.ConvergeAsync(limit: 60);

        Assert.Equal(PassResult.Escalated, outcome.Result);
        Assert.True(harness.Loop.HasStopped);

        const int Bound = 9;
        Assert.True(
            harness.Boundary.Crossings.Count <= Bound,
            $"the worst race schedule cost {harness.Boundary.Crossings.Count} reboots against a bound of {Bound}");
    }

    [Fact]
    public async Task An_escalated_conflict_is_notified_with_the_cause_rather_than_only_the_symptom()
    {
        var resource = new ScriptedResource(Mixer, Wanted, "PCM,0=37") { PutBackAfterVerify = true };
        using var harness = new ReconcileHarness(Options, resource) { Telemetry = { Connected = true } };

        await harness.ConvergeAsync(limit: 30);

        var escalation = Assert.Single(harness.Telemetry.OfKind(FrameLink.Protocol.DeviceEventKinds.Escalation));

        Assert.Equal(Mixer, escalation.Resource);
        Assert.Contains("put back every time", escalation.Delta!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_retry_after_a_conflict_gives_the_resource_a_real_second_chance()
    {
        // The counter has to be cleared by a retry or the retry is theatre: a resource carrying
        // three reversions into the next pass re-escalates before it is ever acted on, which is
        // decision 75's failure in a new place.
        var resource = new ScriptedResource(Mixer, Wanted, "PCM,0=37") { PutBackAfterVerify = true };
        using var harness = new ReconcileHarness(Options, resource) { Telemetry = { Connected = true } };

        await harness.ConvergeAsync(limit: 30);
        Assert.True(harness.Loop.HasStopped);

        var actsWhenRetried = resource.Acts;
        var reboots = harness.Boundary.Crossings.Count;

        Assert.Contains(Mixer, harness.Loop.ResetExhaustedBudgets());

        // Both counters, because both would otherwise make the retry powerless on their own.
        Assert.Empty(harness.Journal.Read().Reboots);
        Assert.Equal(0, Entry(harness, Mixer).Reversions);

        var after = await harness.PassAsync();

        Assert.True(
            resource.Acts > actsWhenRetried,
            $"the retry cleared nothing that mattered: {resource.Acts} acts against {actsWhenRetried} before it");

        Assert.True(harness.Boundary.Crossings.Count > reboots, "the frame never rebooted again after the retry");
        Assert.Equal(PassResult.Rebooted, after.Result);
    }

    [Fact]
    public async Task A_repair_that_holds_is_forgiven_so_ordinary_drift_never_accumulates()
    {
        // §2.2 makes repairing drift the ordinary job, and this is the rule that keeps it ordinary.
        // A package postinst rewriting config.txt during an unattended upgrade is a reversion by
        // every measure decision 78 uses; it must be repaired silently, however many times it
        // happens over a frame's life, because the repair holds.
        var resource = new ScriptedResource("boot.config.dtoverlay-waveshare-panel", "want", "have-not");
        using var harness = new ReconcileHarness(Options, resource) { Telemetry = { Connected = true } };

        Assert.Equal(PassResult.Rebooted, (await harness.PassAsync()).Result);
        Assert.Equal(PassResult.Converged, (await harness.PassAsync()).Result);

        for (var round = 0; round < 5; round++)
        {
            resource.Drift();
            Assert.Equal(PassResult.Rebooted, (await harness.PassAsync()).Result);

            // And it stays fixed for a whole drift-detection sweep, which is what a one-off is.
            harness.Clock.UtcNow += Options.ConflictHold;
            Assert.Equal(PassResult.Converged, (await harness.PassAsync()).Result);
        }

        Assert.False(
            harness.Loop.HasStopped,
            "five ordinary repairs across a frame's life were read as something fighting it");

        Assert.Equal(0, Entry(harness, resource.Name).Reversions);
    }

    [Fact]
    public async Task A_desired_value_the_operator_keeps_changing_is_not_something_fighting_the_frame()
    {
        // The other half of §2.6's sentence, and the one that would have made this rule
        // unshippable if it were conflated with the first. An operator tuning audio.playbackVolume
        // produces drift-after-convergence every time they save, with no hold between edits.
        var resource = new ScriptedResource(Mixer, "PCM,0=60", "PCM,0=37");
        using var harness = new ReconcileHarness(Options, resource) { Telemetry = { Connected = true } };

        Assert.Equal(PassResult.Rebooted, (await harness.PassAsync()).Result);

        foreach (var level in new[] { "PCM,0=55", "PCM,0=50", "PCM,0=45", "PCM,0=40", "PCM,0=35" })
        {
            resource.Retarget(level);

            var outcome = await harness.PassAsync();

            Assert.Equal(PassResult.Rebooted, outcome.Result);
            Assert.Equal(ResourceStatusKind.InSync, ReconcileHarness.StatusOf(outcome, Mixer).Kind);
        }

        Assert.False(harness.Loop.HasStopped, "an operator changing their mind five times stopped their own frame");
        Assert.Equal(0, Entry(harness, Mixer).Reversions);
    }

    [Fact]
    public async Task A_journal_written_before_the_floor_existed_does_not_kill_the_loop()
    {
        // Measured on the frame 2026-08-16. The frame carried a journal written by a build that
        // predates decision 79, so it had no `reboots` key at all. `WhenWritingNull` then keeps it
        // absent on every rewrite, so the omission is self-perpetuating rather than repaired by the
        // first write of the new build — and the first reboot request after the upgrade threw
        // `ArgumentNullException (Parameter 'reboots')` out of Within, inside the reconcile loop's
        // task. Nothing announced it: the process stayed up, the uplink stayed connected, the Fleet
        // Manager went on reporting the device online, and the frame sat in `awaiting-reboot` for
        // twenty-nine minutes until it was restarted by hand. The upgrade path was the one path the
        // floor's own tests could not reach, because they all start from a journal this build wrote.
        using var store = new TemporaryStore();
        var log = new RecordingLog();
        var clock = new ManualClock();

        store.Store.WriteText(
            ReconcileJournal.FileName,
            """
            {
              "ledger": [],
              "lastBootId": "d6ab25f9",
              "telemetrySequence": 1112
            }
            """);

        var journal = new ReconcileJournal(store.Store, log);

        Assert.NotNull(journal.Read().Reboots);
        Assert.Empty(journal.Read().Reboots);

        var floor = new RebootFloor(
            new InProcessRebootBoundary(new MutableBootIdentity()),
            journal,
            clock,
            log,
            limit: 5,
            window: TimeSpan.FromHours(6));

        var crossing = await floor.CrossAsync(Request, TestContext.Current.CancellationToken);

        Assert.Equal(RebootCrossing.Crossed, crossing.Crossing);
        Assert.Equal(1, floor.Recent());
    }

    [Fact]
    public void A_journal_missing_its_lists_reads_as_empty_rather_than_null()
    {
        // The same normalisation, asserted on the journal itself rather than through the floor,
        // because `Reboots` is not the only list here and the next one added would inherit the
        // identical defect. An absent key and an explicit null are both "this build wrote nothing
        // here", and neither may reach a caller as a null list.
        using var store = new TemporaryStore();
        var log = new RecordingLog();

        store.Store.WriteText(ReconcileJournal.FileName, """{ "reboots": null, "ledger": null }""");

        var state = new ReconcileJournal(store.Store, log).Read();

        Assert.NotNull(state.Reboots);
        Assert.NotNull(state.Ledger);
        Assert.Empty(state.Reboots);
        Assert.Empty(state.Ledger);
    }

    [Fact]
    public async Task The_floor_refuses_past_its_count_and_says_so_in_a_sentence()
    {
        using var store = new TemporaryStore();
        var log = new RecordingLog();
        var clock = new ManualClock();
        var journal = new ReconcileJournal(store.Store, log);
        var floor = new RebootFloor(
            new InProcessRebootBoundary(new MutableBootIdentity()),
            journal,
            clock,
            log,
            limit: 5,
            window: TimeSpan.FromHours(6));

        for (var reboot = 0; reboot < 5; reboot++)
        {
            var crossing = await floor.CrossAsync(Request, TestContext.Current.CancellationToken);

            Assert.Equal(RebootCrossing.Crossed, crossing.Crossing);

            // The measured livelock cadence, so the window is doing the work rather than the loop
            // simply running out of test.
            clock.UtcNow += TimeSpan.FromSeconds(25);
        }

        var refused = await floor.CrossAsync(Request, TestContext.Current.CancellationToken);

        Assert.Equal(RebootCrossing.Refused, refused.Crossing);
        Assert.Contains("stopped rebooting", refused.Detail!, StringComparison.Ordinal);
        Assert.Equal(5, floor.Recent());
    }

    [Fact]
    public async Task The_floor_survives_the_reboots_it_is_counting()
    {
        // It has to be durable for the reason that makes it hard: the process does not live through
        // the event. A floor held in memory would reset every time it fired.
        using var store = new TemporaryStore();
        var log = new RecordingLog();
        var clock = new ManualClock();

        for (var reboot = 0; reboot < 3; reboot++)
        {
            var perProcess = new RebootFloor(
                new InProcessRebootBoundary(new MutableBootIdentity()),
                new ReconcileJournal(store.Store, log),
                clock,
                log,
                limit: 3,
                window: TimeSpan.FromHours(6));

            Assert.Equal(
                RebootCrossing.Crossed,
                (await perProcess.CrossAsync(Request, TestContext.Current.CancellationToken)).Crossing);

            clock.UtcNow += TimeSpan.FromSeconds(25);
        }

        var next = new RebootFloor(
            new InProcessRebootBoundary(new MutableBootIdentity()),
            new ReconcileJournal(store.Store, log),
            clock,
            log,
            limit: 3,
            window: TimeSpan.FromHours(6));

        Assert.Equal(
            RebootCrossing.Refused,
            (await next.CrossAsync(Request, TestContext.Current.CancellationToken)).Crossing);
    }

    [Fact]
    public async Task The_window_ages_out_so_a_frame_nobody_comes_to_recovers_on_its_own()
    {
        using var store = new TemporaryStore();
        var log = new RecordingLog();
        var clock = new ManualClock();
        var journal = new ReconcileJournal(store.Store, log);
        var floor = new RebootFloor(
            new InProcessRebootBoundary(new MutableBootIdentity()),
            journal,
            clock,
            log,
            limit: 2,
            window: TimeSpan.FromHours(6));

        await floor.CrossAsync(Request, TestContext.Current.CancellationToken);
        await floor.CrossAsync(Request, TestContext.Current.CancellationToken);

        Assert.Equal(
            RebootCrossing.Refused,
            (await floor.CrossAsync(Request, TestContext.Current.CancellationToken)).Crossing);

        clock.UtcNow += TimeSpan.FromHours(6);

        Assert.Equal(0, floor.Recent());
        Assert.Equal(
            RebootCrossing.Crossed,
            (await floor.CrossAsync(Request, TestContext.Current.CancellationToken)).Crossing);
    }

    [Fact]
    public void A_clock_that_went_backwards_makes_the_floor_forget_rather_than_invent()
    {
        // A Pi that came up before NTP answered. Failing open is the deliberate direction: a floor
        // that broke a provision would be worse than no floor, and decision 78 is the mechanism
        // that is allowed to be strict.
        var now = new DateTimeOffset(2026, 8, 16, 9, 0, 0, TimeSpan.Zero);
        var future = new[] { now.AddYears(1), now.AddHours(1), now.AddMinutes(1) };

        Assert.Empty(RebootFloor.Within(future, now, TimeSpan.FromHours(6)));

        // And an ordinary mixture keeps only what is genuinely inside the window.
        var mixed = new[] { now.AddHours(-7), now.AddHours(-5), now.AddMinutes(-1), now.AddDays(3) };

        Assert.Equal(2, RebootFloor.Within(mixed, now, TimeSpan.FromHours(6)).Count);
    }

    [Fact]
    public async Task The_shipped_floor_is_above_a_whole_first_provision_rather_than_above_a_rate()
    {
        // The number has to clear a bare provision or it is useless, and the rates do not separate
        // it from the fault: a provision runs at ~2.6 reboots a minute (80 resources, ~30 minutes
        // at decision 64's measured 21.0 s mean) and the measured livelock ran at ~2.3. So the
        // floor is sized against the *total* a provision takes, and this asserts the shipped value
        // against the catalog as it stands rather than against a comment.
        var resources = new IResource[80];
        for (var index = 0; index < resources.Length; index++)
        {
            resources[index] = new ScriptedResource(
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"resource.{index:00}"),
                "want",
                "have-not");
        }

        using var harness = new ReconcileHarness(Options, resources);

        var outcome = await harness.ConvergeAsync(limit: 200);

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.Equal(resources.Length, harness.Boundary.Crossings.Count);
        Assert.True(
            resources.Length < Options.RebootFloorCount,
            $"the shipped floor of {Options.RebootFloorCount} does not clear a {resources.Length}-resource provision");
    }

    [Fact]
    public async Task A_frame_held_at_the_floor_escalates_rather_than_sitting_mid_apply()
    {
        // A refused reboot is already a first-class failure: the change is written and cannot be
        // proven, so it spends an attempt and reaches a person on the ordinary schedule — at no
        // cost in reboots, because none of them happen.
        // Whether the Act works is beside the point here, and that is the point: no reboot happens
        // at all, so nothing can be proven either way and the frame has to reach a person.
        var resource = new ScriptedResource("boot.config.dtoverlay-waveshare-panel", "want", "have-not")
        {
            ActHasNoEffect = true,
        };

        using var harness = new ReconcileHarness(
            Options with { RebootFloorCount = 1 },
            resource)
        {
            Telemetry = { Connected = true },
        };

        // One reboot already spent fills a floor of one, so the frame wants another and cannot
        // have it.
        harness.Journal.Update(state => state with { Reboots = [harness.Clock.UtcNow] });

        var outcome = await harness.ConvergeAsync(limit: 20);

        Assert.Equal(PassResult.Escalated, outcome.Result);
        Assert.Empty(harness.Boundary.Crossings);

        var status = ReconcileHarness.StatusOf(outcome, resource.Name);

        Assert.Equal(ResourceStatusKind.Escalated, status.Kind);
        Assert.Contains("stopped rebooting", status.Delta!, StringComparison.Ordinal);
    }

    private static ResourceLedgerEntry Entry(ReconcileHarness harness, string resource) =>
        ReconcileJournal.EntryFor(harness.Journal.Read(), resource);

    /// <summary>One pass, plus the wait its own backoff asked for.</summary>
    /// <remarks>
    /// The driver's wait applied by hand, exactly as <c>ConvergeAsync</c> does it, so a test that
    /// needs to look at the ledger between attempts costs nothing in wall clock and still follows
    /// the schedule the loop chose.
    /// </remarks>
    private static async Task<PassOutcome> Step(ReconcileHarness harness)
    {
        var outcome = await harness.PassAsync();

        if (outcome.NextAttemptUtc is { } next && next > harness.Clock.UtcNow)
        {
            harness.Clock.UtcNow = next;
        }

        return outcome;
    }
}
