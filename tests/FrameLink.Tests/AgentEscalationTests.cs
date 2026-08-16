using FrameLink.Agent.Reconcile;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// §2.4's per-resource backoff and §2.5's escalation ladder — <b>the loop must be willing to
/// give up</b>.
/// </summary>
/// <remarks>
/// <para>
/// Every rung is reached here by a resource that genuinely never converges, not by forcing a
/// status: retry with a growing delay, budget exhausted → <c>Degraded</c> with the exact delta
/// and attempt count, notification → <c>Escalated</c>, and the whole pass stopping around it.
/// </para>
/// <para>
/// <c>Escalated</c> is the last rung (decision 66). A second exhaustion after a retry produces
/// another escalation and not a different, deader state, which is the property several of these
/// tests are really about: there has to be exactly one way back, and it has to keep working.
/// </para>
/// </remarks>
public sealed class AgentEscalationTests
{
    private static ReconcileOptions Options => new()
    {
        Countdown = TimeSpan.Zero,
        AttemptBudget = 3,
        InitialBackoff = TimeSpan.FromSeconds(30),
        BackoffCap = TimeSpan.FromMinutes(30),
    };

    [Fact]
    public async Task A_failed_verify_retries_with_a_growing_delay()
    {
        // §2.4: backoff exists to stop a reboot loop wearing the hardware, so the interval has to
        // actually grow rather than merely exist.
        var resource = Broken();
        using var harness = new ReconcileHarness(Options, resource);

        var first = await harness.PassAsync();
        var firstWait = first.NextAttemptUtc!.Value - harness.Clock.UtcNow;

        harness.Clock.UtcNow = first.NextAttemptUtc.Value;
        var second = await harness.PassAsync();
        var secondWait = second.NextAttemptUtc!.Value - harness.Clock.UtcNow;

        Assert.True(firstWait > TimeSpan.Zero);
        Assert.True(secondWait > firstWait, $"expected the second wait ({secondWait}) to exceed the first ({firstWait})");
        Assert.True(secondWait <= Options.BackoffCap);
    }

    [Fact]
    public async Task A_resource_inside_its_backoff_is_not_touched_and_says_when_it_will_be()
    {
        // §2.7 item 6: a pause must never look like a hang, which is only possible if the loop
        // publishes when the wait ends rather than merely that it is waiting.
        var resource = Broken();
        using var harness = new ReconcileHarness(Options, resource);

        await harness.PassAsync();
        var actsAfterFirst = resource.Acts;

        harness.Clock.UtcNow += TimeSpan.FromSeconds(1);
        var outcome = await harness.PassAsync();
        var status = ReconcileHarness.StatusOf(outcome, "broken");

        Assert.Equal(actsAfterFirst, resource.Acts);
        Assert.Equal(ResourceStatusKind.Progressing, status.Kind);
        Assert.NotNull(status.NextAttemptUtc);
        Assert.True(status.NextAttemptUtc > harness.Clock.UtcNow);
    }

    [Fact]
    public async Task An_exhausted_budget_stops_touching_the_resource_and_reports_the_exact_delta()
    {
        // §2.5 rung 2, verbatim: "stop touching it, mark Degraded with the exact
        // expected-versus-observed delta and attempt count".
        var resource = Broken();
        using var harness = new ReconcileHarness(Options, resource) { Telemetry = { Connected = false } };

        var outcome = await harness.ConvergeAsync();
        var status = ReconcileHarness.StatusOf(outcome, "broken");
        var actsAtGiveUp = resource.Acts;

        Assert.Equal(ResourceStatusKind.Degraded, status.Kind);
        Assert.Equal(Options.AttemptBudget, status.Attempts);
        Assert.Equal(Options.AttemptBudget, status.AttemptBudget);
        Assert.Contains("want", status.Delta!, StringComparison.Ordinal);
        Assert.Contains("have-not", status.Delta!, StringComparison.Ordinal);

        // Stop means stop: another pass does not act again.
        harness.Clock.UtcNow += TimeSpan.FromHours(1);
        await harness.PassAsync();
        Assert.Equal(actsAtGiveUp, resource.Acts);
    }

    [Fact]
    public async Task Degraded_becomes_escalated_only_once_the_fleet_manager_actually_hears_it()
    {
        // §2.3 spells the rung Escalated(admin-notified). A frame whose server is unreachable has
        // exhausted its budget and told nobody, which is Degraded; the same frame becomes
        // Escalated when the buffered event drains. Collapsing the two would let an offline frame
        // claim an administrator had been told.
        var resource = Broken();
        using var harness = new ReconcileHarness(Options, resource);
        harness.Telemetry.Connected = false;

        var offline = await harness.ConvergeAsync();
        Assert.Equal(ResourceStatusKind.Degraded, ReconcileHarness.StatusOf(offline, "broken").Kind);

        harness.Telemetry.Connected = true;
        harness.Clock.UtcNow += TimeSpan.FromMinutes(10);
        var online = await harness.PassAsync();

        Assert.Equal(ResourceStatusKind.Escalated, ReconcileHarness.StatusOf(online, "broken").Kind);
        Assert.NotEmpty(harness.Telemetry.OfKind(DeviceEventKinds.Escalation));
    }

    [Fact]
    public async Task The_escalation_event_carries_the_delta_and_the_attempt_count()
    {
        var resource = Broken();
        using var harness = new ReconcileHarness(Options, resource) { Telemetry = { Connected = true } };

        await harness.ConvergeAsync();
        var escalation = harness.Telemetry.OfKind(DeviceEventKinds.Escalation).First();

        Assert.Equal("broken", escalation.Resource);
        Assert.Equal(Options.AttemptBudget, escalation.Attempts);
        Assert.Contains("have-not", escalation.Delta!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_escalation_event_carries_the_cause_and_not_only_the_symptom()
    {
        // §2.5 rung 3 offers the operator exactly two actions — retry, or open a remote shell —
        // and which one is right turns on why the resource failed, not on what is wrong with it.
        // "labwc is missing" is equally true of an unreachable archive, where retrying is the
        // whole fix, and of a package name the catalog got wrong, where retrying is the wrong
        // answer forever. The reason lives in the action the resource returned, so an event built
        // from the summary and delta alone asks for a decision while withholding what it turns on.
        var resource = Broken();
        using var harness = new ReconcileHarness(Options, resource) { Telemetry = { Connected = true } };

        await harness.ConvergeAsync();
        var escalation = harness.Telemetry.OfKind(DeviceEventKinds.Escalation).First();

        Assert.Contains(resource.Detected, escalation.Summary, StringComparison.Ordinal);
        Assert.Contains("set broken to want", escalation.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_frame_that_gave_up_while_offline_names_the_cause_too()
    {
        // The escalation event is built on two paths: the one that gives up, and the one that
        // re-offers it when a frame that gave up with no server reachable gets its server back.
        // An operator reading the second one needs the cause exactly as much as the first.
        var resource = Broken();
        using var harness = new ReconcileHarness(Options, resource);
        harness.Telemetry.Connected = false;

        await harness.ConvergeAsync();

        harness.Telemetry.Connected = true;
        harness.Clock.UtcNow += TimeSpan.FromMinutes(10);
        await harness.PassAsync();

        var escalations = harness.Telemetry.OfKind(DeviceEventKinds.Escalation).ToList();

        Assert.True(escalations.Count >= 2, $"expected both paths to have emitted, saw {escalations.Count}");
        Assert.All(
            escalations,
            escalation => Assert.Contains("set broken to want", escalation.Summary, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_exhaustion_after_a_retry_escalates_again_rather_than_reaching_a_deader_state()
    {
        // Decision 66. There used to be a rung below this — Halted — reachable only through a
        // retry, and it bought nothing: the action that cleared it was the same retry that clears
        // an escalation, so the second state added no recovery path and one more way for a frame
        // to be stuck. A frame that gives up twice is a frame that has given up, twice.
        var resource = Broken();
        using var harness = new ReconcileHarness(Options, resource) { Telemetry = { Connected = true } };

        await harness.ConvergeAsync();
        Assert.Equal(ResourceStatusKind.Escalated, ReconcileHarness.StatusOf(await harness.PassAsync(), "broken").Kind);

        harness.Loop.ResetBudget("broken");
        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Escalated, outcome.Result);
        Assert.Equal(ResourceStatusKind.Escalated, ReconcileHarness.StatusOf(outcome, "broken").Kind);
        Assert.True(harness.Loop.HasStopped);

        // Twice on the record, so the history is not lost with the state.
        Assert.Equal(2, ReconcileJournal.EntryFor(harness.Journal.Read(), "broken").Escalations);

        // And still recoverable, which is the whole argument for removing the rung below.
        harness.Loop.ResetBudget("broken");
        Assert.False(harness.Loop.HasStopped);
    }

    [Fact]
    public void A_device_wide_retry_clears_every_resource_that_gave_up_and_nothing_else()
    {
        // The device-wide form of the retry, and what it is for after decision 68: two resources
        // can no longer give up in the same pass, because the first one to give up stops the pass.
        // A frame can still carry more than one — a ledger written by an earlier build, or by a
        // catalog whose ordering has since changed — and the whole point of this verb is that an
        // operator looking at a stopped frame asks it to try again without having to name each
        // setting that contributed. Clearing one while another remained would look like it had
        // done nothing at all.
        //
        // The ledger is written rather than driven, deliberately: driving it is exactly what
        // decision 68 now prevents, and a test that drove it would be asserting the old behaviour
        // through the new one.
        using var harness = new ReconcileHarness(
            Options,
            Broken("first"),
            new ScriptedResource("healthy", "want", "want"),
            Broken("second"));

        harness.Journal.Update(state => ReconcileJournal.WithEntry(
            ReconcileJournal.WithEntry(
                ReconcileJournal.WithEntry(
                    state,
                    ReconcileJournal.EntryFor(state, "first") with
                    {
                        Attempts = Options.AttemptBudget,
                        Escalations = 1,
                        Delta = "expected 'want', observed 'have-not'",
                    }),
                ReconcileJournal.EntryFor(state, "healthy") with { Attempts = 1 }),
            ReconcileJournal.EntryFor(state, "second") with
            {
                Attempts = Options.AttemptBudget,
                Escalations = 1,
                Delta = "expected 'want', observed 'have-not'",
            }));

        var reset = harness.Loop.ResetExhaustedBudgets();

        Assert.Equal(["first", "second"], reset);

        // A resource that never gave up is not in the set and its ledger is untouched, so a
        // device-wide retry cannot quietly forgive a backoff somebody is still waiting out.
        Assert.DoesNotContain("healthy", reset);
        Assert.Equal(1, ReconcileJournal.EntryFor(harness.Journal.Read(), "healthy").Attempts);

        // And the frame is no longer stopped, which is the whole observable effect of pressing it.
        Assert.False(harness.Loop.HasStopped);
    }

    [Fact]
    public async Task A_retry_on_a_frame_where_nothing_gave_up_changes_nothing()
    {
        var healthy = new ScriptedResource("healthy", "want", "want");
        using var harness = new ReconcileHarness(Options, healthy) { Telemetry = { Connected = true } };

        await harness.ConvergeAsync();

        Assert.Empty(harness.Loop.ResetExhaustedBudgets());
        Assert.Equal(PassResult.Converged, (await harness.PassAsync()).Result);
    }

    [Fact]
    public void The_set_a_retry_clears_is_the_set_the_walk_refuses_to_touch()
    {
        // One predicate, two readers. Written twice they could disagree, and the disagreement has
        // one direction — something the walk skips forever that a retry cannot reach — which is a
        // frame nothing can recover short of a re-flash.
        Assert.False(ReconcileLoop.HasGivenUp(new ResourceLedgerEntry { Resource = "r" }, 5));

        // Attempts alone are a backoff, not a surrender: the ladder has not reached rung 2 yet.
        Assert.False(ReconcileLoop.HasGivenUp(new ResourceLedgerEntry { Resource = "r", Attempts = 5 }, 5));

        // An escalation with a budget that has since been reset is a frame already trying again.
        Assert.False(ReconcileLoop.HasGivenUp(new ResourceLedgerEntry { Resource = "r", Escalations = 1 }, 5));

        Assert.True(ReconcileLoop.HasGivenUp(
            new ResourceLedgerEntry { Resource = "r", Attempts = 5, Escalations = 1 },
            5));

    }

    [Fact]
    public void An_attempt_count_is_never_read_as_more_than_the_budget_can_express()
    {
        // Decision 74. The ledger is durable and the budget is not, so a frame provisioned under
        // decision 7's five carries counts of four and five into decision 67's three. Every read
        // that compares or displays one has to answer with a number the budget can express, or the
        // frame says `att=5/3` — which was measured, and which cannot be true.
        Assert.Equal(3, ReconcileLoop.AttemptsWithin(5, 3));
        Assert.Equal(3, ReconcileLoop.AttemptsWithin(3, 3));
        Assert.Equal(2, ReconcileLoop.AttemptsWithin(2, 3));
        Assert.Equal(0, ReconcileLoop.AttemptsWithin(-1, 3));

        // A budget of zero is not a clamp to zero: nothing in the loop sets one, and reading every
        // count as none would hide a whole history behind a misconfiguration.
        Assert.Equal(5, ReconcileLoop.AttemptsWithin(5, 0));
    }

    [Fact]
    public async Task A_ledger_written_under_a_larger_budget_narrates_a_pair_that_can_be_true()
    {
        // The measured defect: `Attempts=4, Escalations=0` survives a budget cut from five to
        // three. HasGivenUp does not catch it — no escalation is on the record — so the resource is
        // acted on, and the old arithmetic made that `attempt 5 of 3`.
        //
        // What this asserts is the coherence, not the escalation. The escalation is correct and
        // deliberate: a budget reduction is retroactive by design (decision 74), because the
        // operator lowered it precisely because attempts cost card wear.
        var resource = Broken();
        using var harness = new ReconcileHarness(Options, resource) { Telemetry = { Connected = true } };

        harness.Journal.Update(state => ReconcileJournal.WithEntry(
            state,
            ReconcileJournal.EntryFor(state, "broken") with { Attempts = 4, Escalations = 0 }));

        var outcome = await harness.PassAsync();
        var status = ReconcileHarness.StatusOf(outcome, "broken");

        Assert.Equal(PassResult.Escalated, outcome.Result);
        Assert.Equal(Options.AttemptBudget, status.AttemptBudget);
        Assert.Equal(Options.AttemptBudget, status.Attempts);
        Assert.True(
            status.Attempts <= status.AttemptBudget,
            $"a resource cannot have spent {status.Attempts} of {status.AttemptBudget} attempts");

        // The screen and the operator's alert read the same clamped number, so neither surface can
        // show a count the other cannot account for.
        Assert.Equal(Options.AttemptBudget, harness.Hub.Current.Reconcile.Attempt);
        Assert.Equal(Options.AttemptBudget, harness.Hub.Current.Reconcile.AttemptBudget);
        Assert.All(
            harness.Telemetry.OfKind(DeviceEventKinds.Escalation),
            escalation => Assert.True(
                escalation.Attempts <= Options.AttemptBudget,
                $"an escalation reported {escalation.Attempts} attempts against a budget of {Options.AttemptBudget}"));

        // The ledger keeps one counter, bounded by the budget in force, rather than a second
        // unbounded one beside it — the unbounded history is the escalation events, which §3.5
        // keeps for a month. What is deliberately *not* done is a reset: the escalation stands, so
        // the frame is still stopped and its operator has not been silently un-notified.
        var entry = ReconcileJournal.EntryFor(harness.Journal.Read(), "broken");
        Assert.Equal(3, entry.Attempts);
        Assert.Equal(1, entry.Escalations);
        Assert.True(harness.Loop.HasStopped);
    }

    [Fact]
    public async Task A_frame_that_gave_up_under_a_larger_budget_still_reports_a_coherent_pair()
    {
        // The other half of decision 74, and the one an operator sees for as long as the frame
        // stays stopped: the resource is never walked again, so the row comes from the ledger
        // through the stopped-device path rather than from a fresh observation.
        var resource = Broken();
        using var harness = new ReconcileHarness(Options, resource) { Telemetry = { Connected = true } };

        harness.Journal.Update(state => ReconcileJournal.WithEntry(
            state,
            ReconcileJournal.EntryFor(state, "broken") with
            {
                Attempts = 5,
                Escalations = 1,
                EscalationNotified = true,
                Delta = "expected 'want', observed 'have-not'",
            }));

        var outcome = await harness.PassAsync();
        var status = ReconcileHarness.StatusOf(outcome, "broken");

        Assert.Equal(PassResult.Escalated, outcome.Result);
        Assert.Equal(ResourceStatusKind.Escalated, status.Kind);
        Assert.Equal(Options.AttemptBudget, status.Attempts);
        Assert.Equal(Options.AttemptBudget, status.AttemptBudget);

        // Nothing was rewritten to achieve it: the true history is still on disk, where a
        // post-mortem can read it.
        Assert.Equal(5, ReconcileJournal.EntryFor(harness.Journal.Read(), "broken").Attempts);
    }

    [Fact]
    public async Task An_escalation_stops_the_whole_pass_and_not_just_the_resource_that_raised_it()
    {
        // Decision 68, and the measurement behind it: the attempt budget is per resource, so one
        // shared cause is multiplied by however many resources share it - on the frame, one 350 ms
        // race across five resources cost 41 reboots. Stopping at the first escalation makes that
        // multiplication structurally impossible rather than merely bounded.
        var broken = Broken();
        var other = new ScriptedResource("other", "want", "have-not");
        using var harness = new ReconcileHarness(Options, broken, other) { Telemetry = { Connected = true } };

        var outcome = await harness.ConvergeAsync();
        Assert.Equal(PassResult.Escalated, outcome.Result);

        // Whatever the other resource had managed before the stop, no *work* happens on it after
        // it: not an Act, not a reboot, not a spent attempt. That is the whole of "the frame holds
        // the failure and waits", and it is asserted as an absence of work rather than as a state,
        // because whether this particular resource had already converged before the stop is not
        // the point and would make the test depend on the order two unrelated things happened in.
        //
        // Observations are deliberately *not* in that list. Decision 68: "Stopping means stopping
        // acting, not stopping looking."
        var acts = other.Acts;
        var crossings = harness.Boundary.Crossings.Count;
        var attempts = ReconcileJournal.EntryFor(harness.Journal.Read(), "other").Attempts;

        harness.Clock.UtcNow += TimeSpan.FromHours(1);
        var after = await harness.PassAsync();

        Assert.Equal(PassResult.Escalated, after.Result);
        Assert.Equal(acts, other.Acts);
        Assert.Equal(crossings, harness.Boundary.Crossings.Count);
        Assert.Equal(attempts, ReconcileJournal.EntryFor(harness.Journal.Read(), "other").Attempts);
    }

    [Fact]
    public async Task A_stopped_pass_reports_what_the_frame_is_rather_than_what_it_is_waiting_for()
    {
        // Decision 76, and the payload it was diagnosed from. The old shape returned at the first
        // escalation and invented the rest of the catalog: every unreached resource was labelled
        // Blocked with the escalated resource as its blockedBy, whether or not it depended on it.
        // On the frame all 77 remaining rows claimed to be waiting on tool.xvf-host.installed,
        // including boot.config.dtoverlay-waveshare-panel, which had been in sync since M2 and has
        // no dependency on it at all — and the device reported 0 of 79 in sync while being almost
        // entirely configured.
        //
        // Here "second" and "third" genuinely converge before "broken" gives up, so the true answer
        // for both is in sync. Under the old shape they read as blocked on a resource neither of
        // them has ever depended on.
        var broken = Broken();
        var second = new ScriptedResource("second", "want", "have-not");
        var third = new ScriptedResource("third", "want", "have-not");
        using var harness = new ReconcileHarness(Options, broken, second, third) { Telemetry = { Connected = true } };

        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Escalated, outcome.Result);
        Assert.Equal(3, outcome.Statuses.Count);
        Assert.Equal(ResourceStatusKind.Escalated, ReconcileHarness.StatusOf(outcome, "broken").Kind);

        foreach (var name in new[] { "second", "third" })
        {
            var status = ReconcileHarness.StatusOf(outcome, name);

            Assert.Equal(ResourceStatusKind.InSync, status.Kind);
            Assert.Null(status.BlockedBy);
        }

        // And the count the operator reads is the count of the frame, not of the pass.
        var report = harness.Telemetry.Latest!;
        Assert.Equal(2, report.InSync);
        Assert.Equal(1, report.RebootsExpected);
    }

    [Fact]
    public async Task A_stopped_pass_claims_no_dependency_the_graph_does_not_contain()
    {
        // The unambiguous half of decision 76, asserted against the graph itself rather than
        // against a list of names — which is what makes it hold for any catalog. A row may only
        // say it is blocked by X if X is in its own DependsOn closure, or if X is the one authority
        // that is deliberately not a resource at all (§2.6's silent Fleet Manager).
        //
        // "downstream" genuinely depends on the resource that gives up; "unrelated" does not, and
        // is drifted at the moment of the stop so that it cannot pass by being in sync.
        var broken = Broken();
        var downstream = new ScriptedResource("downstream", "want", "have-not", "broken");
        var unrelated = new ScriptedResource("unrelated", "want", "have-not") { ActHasNoEffect = true };
        using var harness = new ReconcileHarness(Options, broken, downstream, unrelated)
        {
            Telemetry = { Connected = true },
        };

        var outcome = await harness.ConvergeAsync();
        Assert.Equal(PassResult.Escalated, outcome.Result);

        var depends = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var resource in harness.Graph.Ordered)
        {
            depends[resource.Name] = resource.DependsOn;
        }

        foreach (var status in outcome.Statuses)
        {
            if (status.BlockedBy is not { } blocker || blocker == ReconcileLoop.SilentAuthority)
            {
                continue;
            }

            Assert.True(
                Reaches(depends, status.Name, blocker),
                $"'{status.Name}' claims to be blocked by '{blocker}', which is not in its dependency closure");
        }

        // The real dependent is named, so removing the false claims did not remove the true one.
        var blocked = ReconcileHarness.StatusOf(outcome, "downstream");
        Assert.Equal(ResourceStatusKind.Blocked, blocked.Kind);
        Assert.Equal("broken", blocked.BlockedBy);

        // And the resource that shares nothing with the failure is reported as what it is.
        var untouched = ReconcileHarness.StatusOf(outcome, "unrelated");
        Assert.Null(untouched.BlockedBy);
        Assert.NotEqual(ResourceStatusKind.Blocked, untouched.Kind);
    }

    [Fact]
    public async Task A_frame_that_gave_up_while_offline_still_headlines_the_resource_that_gave_up()
    {
        // Decision 70 states as a fact that a resource which has given up "sorts worst" in
        // PublishStatusesAsync, and the enum's declaration order does not deliver it: Blocked is
        // declared after Degraded. So a frame that gave up while its server was unreachable — which
        // is Degraded rather than Escalated (§2.3) — headlined one of the blocked rows behind it,
        // and the screen lost the resource name, the attempt count and §2.7 item 7's "has anybody
        // been told" sentence. Decision 76 makes this reachable far more often, because a stopped
        // frame now publishes the real blocked rows instead of one fabricated per resource.
        var broken = Broken();
        var downstream = new ScriptedResource("downstream", "want", "have-not", "broken");
        using var harness = new ReconcileHarness(Options, broken, downstream) { Telemetry = { Connected = false } };

        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Escalated, outcome.Result);
        Assert.Equal(ResourceStatusKind.Degraded, ReconcileHarness.StatusOf(outcome, "broken").Kind);
        Assert.Equal(ResourceStatusKind.Blocked, ReconcileHarness.StatusOf(outcome, "downstream").Kind);

        var narration = harness.Hub.Current.Reconcile;
        Assert.Equal("broken", narration.Resource);
        Assert.Equal(1, narration.Escalations);
        Assert.Equal(
            "The Fleet Manager could not be reached, so nobody has been told yet.",
            narration.EscalationLine);
    }

    [Fact]
    public async Task A_frame_that_has_already_given_up_acts_on_nothing_ordered_ahead_of_it()
    {
        // The half a per-resource check misses: the stop is inherited from an earlier process, and
        // the walk reaches the escalated entry only after everything ordered before it. Anything
        // drifting there would be acted on and rebooted for on every boot — §2.4's unbounded reboot
        // loop, wearing the same hardware under a different resource's name, on a frame whose
        // operator has already been told.
        var first = new ScriptedResource("first", "want", "want");
        var broken = Broken();
        using var harness = new ReconcileHarness(Options, first, broken) { Telemetry = { Connected = true } };

        Assert.Equal(PassResult.Escalated, (await harness.ConvergeAsync()).Result);

        // Now something ordered ahead of the escalated resource drifts: a mixer value reset, a
        // hostname cloud-init put back.
        first.Drift();
        var acts = first.Acts;
        var crossings = harness.Boundary.Crossings.Count;

        harness.Clock.UtcNow += TimeSpan.FromHours(1);
        var outcome = await harness.PassAsync();

        Assert.Equal(PassResult.Escalated, outcome.Result);
        Assert.Equal(acts, first.Acts);
        Assert.Equal(crossings, harness.Boundary.Crossings.Count);
        Assert.Equal(0, ReconcileJournal.EntryFor(harness.Journal.Read(), "first").Attempts);

        // The drift is real and still there, so nothing above is passing because there was
        // nothing to do — the loop declined work it could have done.
        Assert.False((await first.ObserveAsync(TestContext.Current.CancellationToken)).InSync);

        // It is nevertheless *reported*, and reported as drifted rather than as blocked on the
        // failure: a person reading this frame learns that two things are wrong with it, which is
        // the truth, instead of one thing plus a false claim about the other (decision 76).
        Assert.NotEqual(ResourceStatusKind.InSync, ReconcileHarness.StatusOf(outcome, "first").Kind);
        Assert.Null(ReconcileHarness.StatusOf(outcome, "first").BlockedBy);

        // And the pass still says which resource stopped the frame, because a stop that reported
        // nothing would be indistinguishable from an agent that had simply died.
        Assert.Equal(ResourceStatusKind.Escalated, ReconcileHarness.StatusOf(outcome, "broken").Kind);
    }

    /// <summary>Whether <paramref name="from"/> depends on <paramref name="target"/>, transitively.</summary>
    private static bool Reaches(
        Dictionary<string, IReadOnlyList<string>> depends,
        string from,
        string target)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!seen.Add(name) || !depends.TryGetValue(name, out var dependencies))
            {
                continue;
            }

            foreach (var dependency in dependencies)
            {
                if (string.Equals(dependency, target, StringComparison.Ordinal))
                {
                    return true;
                }

                queue.Enqueue(dependency);
            }
        }

        return false;
    }

    [Fact]
    public async Task The_attempt_count_survives_the_reboots_it_is_counting()
    {
        // Without a durable ledger the counter resets on every boot, the budget never exhausts,
        // and §2.4's unbounded reboot loop — "more damaging than a stalled provision" — is exactly
        // what the frame does forever.
        var resource = Broken();
        using var harness = new ReconcileHarness(Options, resource);

        await harness.PassAsync();
        var afterFirst = ReconcileJournal.EntryFor(harness.Journal.Read(), "broken");

        // A brand-new journal object over the same directory is what the next process reads.
        var reread = ReconcileJournal.EntryFor(
            new ReconcileJournal(harness.Store, harness.Log).Read(),
            "broken");

        Assert.Equal(1, afterFirst.Attempts);
        Assert.Equal(1, reread.Attempts);
        Assert.True(harness.Boot.Boots >= 1);
    }

    [Fact]
    public async Task An_operator_retry_clears_the_stop_but_not_the_history()
    {
        var resource = Broken();
        using var harness = new ReconcileHarness(Options, resource) { Telemetry = { Connected = true } };

        await harness.ConvergeAsync();
        harness.Loop.ResetBudget("broken");
        await harness.ConvergeAsync();
        Assert.True(harness.Loop.HasStopped);

        harness.Loop.ResetBudget("broken");
        var entry = ReconcileJournal.EntryFor(harness.Journal.Read(), "broken");

        Assert.False(harness.Loop.HasStopped);
        Assert.Equal(0, entry.Attempts);

        // Kept, so a frame already given up on once does not start the ladder from the bottom -
        // the second escalation still says "this has happened before".
        Assert.Equal(2, entry.Escalations);
    }

    [Fact]
    public async Task A_resource_that_finally_converges_forgets_its_whole_failure_history()
    {
        var resource = new ScriptedResource("flaky", "want", "have-not") { ActHasNoEffect = true };
        using var harness = new ReconcileHarness(Options, resource);

        await harness.PassAsync();
        Assert.Equal(1, ReconcileJournal.EntryFor(harness.Journal.Read(), "flaky").Attempts);

        resource.ActHasNoEffect = false;
        harness.Clock.UtcNow += TimeSpan.FromMinutes(10);
        await harness.PassAsync();

        var entry = ReconcileJournal.EntryFor(harness.Journal.Read(), "flaky");
        Assert.Equal(0, entry.Attempts);
        Assert.Equal(0, entry.Escalations);
        Assert.Null(entry.NextAttemptUtc);
    }

    private static ScriptedResource Broken(string name = "broken") =>
        new(name, "want", "have-not") { ActHasNoEffect = true };
}
