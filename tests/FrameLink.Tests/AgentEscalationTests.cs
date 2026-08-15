using FrameLink.Agent.Reconcile;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// §2.4's per-resource backoff and §2.5's escalation ladder — <b>the loop must be willing to
/// give up</b>.
/// </summary>
/// <remarks>
/// Every rung is reached here by a resource that genuinely never converges, not by forcing a
/// status: retry with a growing delay, budget exhausted → <c>Degraded</c> with the exact delta
/// and attempt count, notification → <c>Escalated</c>, operator retry then a second exhaustion →
/// <c>Halted</c> for the device.
/// </remarks>
public sealed class AgentEscalationTests
{
    private static ReconcileOptions Options => new()
    {
        Countdown = TimeSpan.Zero,
        AttemptBudget = 3,
        EscalationLimit = 2,
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
    public async Task A_second_exhaustion_after_the_operator_retries_halts_the_device()
    {
        // §2.5 rung 4. Halted is only reachable through rung 3's retry, and that is deliberate:
        // without an operator asking for another go, "stop touching it" would never be undone and
        // a second exhaustion could not happen.
        var resource = Broken();
        using var harness = new ReconcileHarness(Options, resource) { Telemetry = { Connected = true } };

        await harness.ConvergeAsync();
        Assert.Equal(ResourceStatusKind.Escalated, ReconcileHarness.StatusOf(await harness.PassAsync(), "broken").Kind);

        harness.Loop.ResetBudget("broken");
        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Halted, outcome.Result);
        Assert.Equal(ResourceStatusKind.Halted, ReconcileHarness.StatusOf(outcome, "broken").Kind);
        Assert.True(harness.Loop.IsHalted);
        Assert.NotEmpty(harness.Telemetry.OfKind(DeviceEventKinds.Halted));
    }

    [Fact]
    public async Task A_halted_device_stops_reconciling_everything_not_just_the_broken_resource()
    {
        // "Halted for that device", not "halted for that resource". Continuing to reboot a
        // persistently broken frame is damage, and rebooting it for a different setting is the
        // same damage under another name.
        var broken = Broken();
        var healthy = new ScriptedResource("healthy", "want", "have-not");
        using var harness = new ReconcileHarness(Options, broken, healthy) { Telemetry = { Connected = true } };

        await harness.ConvergeAsync();
        harness.Loop.ResetBudget("broken");
        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Halted, outcome.Result);
        Assert.DoesNotContain(outcome.Statuses, status => string.Equals(status.Name, "healthy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_device_that_is_already_halted_touches_nothing_ordered_ahead_of_the_halted_resource()
    {
        // The other half of "Halted for that device", and the half a per-resource check misses:
        // a halt is inherited from an earlier process, and the walk reaches the halted entry only
        // after everything ordered before it. Anything drifting there would be observed, acted on
        // and rebooted for on every boot — §2.4's unbounded reboot loop, wearing the same hardware
        // under a different resource's name, on a frame an administrator has already been told
        // about twice.
        var first = new ScriptedResource("first", "want", "want");
        var broken = Broken();
        using var harness = new ReconcileHarness(Options, first, broken) { Telemetry = { Connected = true } };

        await harness.ConvergeAsync();
        harness.Loop.ResetBudget("broken");
        Assert.Equal(PassResult.Halted, (await harness.ConvergeAsync()).Result);

        // Now something ordered ahead of the halted resource drifts: a mixer value reset, a
        // hostname cloud-init put back.
        first.Drift();
        var observations = first.Observations;
        var acts = first.Acts;
        var crossings = harness.Boundary.Crossings.Count;

        harness.Clock.UtcNow += TimeSpan.FromHours(1);
        var outcome = await harness.PassAsync();

        Assert.Equal(PassResult.Halted, outcome.Result);
        Assert.Equal(observations, first.Observations);
        Assert.Equal(acts, first.Acts);
        Assert.Equal(crossings, harness.Boundary.Crossings.Count);

        // The drift is real and still there, so nothing above is passing because there was
        // nothing to do — the loop declined work it could have done.
        Assert.False((await first.ObserveAsync(TestContext.Current.CancellationToken)).InSync);

        // And the pass still says which resource stopped the device, because a halt that reported
        // nothing would be indistinguishable from a frame that had simply stopped.
        Assert.Equal(ResourceStatusKind.Halted, ReconcileHarness.StatusOf(outcome, "broken").Kind);
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
    public async Task An_operator_retry_clears_the_halt_but_not_the_history()
    {
        var resource = Broken();
        using var harness = new ReconcileHarness(Options, resource) { Telemetry = { Connected = true } };

        await harness.ConvergeAsync();
        harness.Loop.ResetBudget("broken");
        await harness.ConvergeAsync();
        Assert.True(harness.Loop.IsHalted);

        harness.Loop.ResetBudget("broken");
        var entry = ReconcileJournal.EntryFor(harness.Journal.Read(), "broken");

        Assert.False(entry.Halted);
        Assert.Equal(0, entry.Attempts);

        // Kept, so a frame already given up on once halts again the moment the fresh budget runs
        // out rather than starting the ladder from the bottom.
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

    private static ScriptedResource Broken() =>
        new("broken", "want", "have-not") { ActHasNoEffect = true };
}
