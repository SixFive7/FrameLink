using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;
using FrameLink.Agent.State;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// <b>Rejection is an answer; silence is not</b> (§2.6), as behaviour of the reconciliation loop.
/// </summary>
/// <remarks>
/// <para>
/// Found on the mule with the Fleet Manager deliberately stopped. The agent said <i>"agent.adoption:
/// did not survive the reboot — expected 'adopted', observed 'waiting for adoption'"</i> of a frame
/// that was adopted the entire time, spent attempts 1, 2 and 3 of a 5-attempt budget on a server
/// outage, and rebooted four times in twelve minutes trying to make an unreachable server
/// reachable. Left long enough it would have escalated twice and reached <c>Halted</c>, which
/// decision 49 makes device-wide — so an operator whose Fleet Manager was down for an afternoon
/// would have come back to frames that had stopped themselves, diagnosed as a persistent local
/// fault.
/// </para>
/// <para>
/// Every test here asserts an outcome an operator would notice: what the budget did, what the
/// reboot boundary was asked to do, which rung the device sat on, and whether the frame came back
/// on its own. None of them asserts how the loop is wired.
/// </para>
/// </remarks>
public sealed class AgentServerSilenceTests
{
    private static ReconcileOptions Fast => new()
    {
        Countdown = TimeSpan.Zero,
        AttemptBudget = 3,
        EscalationLimit = 2,
    };

    [Fact]
    public async Task An_outage_long_enough_to_exhaust_a_budget_costs_no_attempts_and_no_reboots()
    {
        using var files = new TemporaryFiles();
        using var harness = new ReconcileHarness(Fast, new AdoptionResource(files.Store, () => ServerAnswer.Silence));

        // Far longer than the budget would have survived: at three attempts and a growing backoff
        // the old behaviour reached Degraded within minutes and Halted on the second exhaustion.
        var outcome = await OutageAsync(harness, TimeSpan.FromHours(6));

        Assert.Equal(PassResult.Pending, outcome.Result);
        Assert.Empty(harness.Boundary.Crossings);
        Assert.False(harness.Loop.IsHalted);
        Assert.Empty(harness.Telemetry.OfKind(DeviceEventKinds.Escalation));
        Assert.Empty(harness.Telemetry.OfKind(DeviceEventKinds.Halted));
        Assert.Empty(harness.Telemetry.OfKind(DeviceEventKinds.Drift));

        var ledger = ReconcileJournal.EntryFor(harness.Journal.Read(), AdoptionResource.ResourceName);
        Assert.Equal(0, ledger.Attempts);
        Assert.Equal(0, ledger.Escalations);
        Assert.False(ledger.Halted);
    }

    [Fact]
    public async Task A_resource_that_could_not_be_read_says_so_instead_of_being_diagnosed()
    {
        using var files = new TemporaryFiles();
        using var harness = new ReconcileHarness(Fast, new AdoptionResource(files.Store, () => ServerAnswer.Silence));

        var outcome = await OutageAsync(harness, TimeSpan.FromMinutes(30));
        var status = ReconcileHarness.StatusOf(outcome, AdoptionResource.ResourceName);

        Assert.Equal(ResourceStatusKind.Blocked, status.Kind);
        Assert.Equal(ReconcileLoop.SilentAuthority, status.BlockedBy);
        Assert.Contains("could not be determined", status.Delta!, StringComparison.Ordinal);

        // The false diagnosis, named. "Did not survive the reboot" asserts the setting was lost,
        // and that sentence is what would send an operator hunting a persistence bug.
        Assert.DoesNotContain("did not survive the reboot", harness.Log.Transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("failed; next try in", harness.Log.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_that_needs_an_issued_value_is_touched_while_the_server_is_silent()
    {
        using var files = new TemporaryFiles();
        var names = new RecordingProcessRunner();

        using var harness = new ReconcileHarness(
            Fast,
            new AdoptionResource(files.Store, () => ServerAnswer.Silence),
            new DeviceNameResource(files.Store, () => null),
            new HostnameResource(files.Files, names, FleetValues.None));

        var outcome = await OutageAsync(harness, TimeSpan.FromMinutes(30));

        Assert.Equal(
            ResourceStatusKind.Blocked,
            ReconcileHarness.StatusOf(outcome, DeviceNameResource.ResourceName).Kind);
        Assert.Equal(
            AdoptionResource.ResourceName,
            ReconcileHarness.StatusOf(outcome, HostnameResource.ResourceName).BlockedBy);

        // Nothing was written and nothing was run, which is what "not attempted" has to mean.
        Assert.Null(files.Store.ReadText(AdoptionResource.FileName));
        Assert.Null(files.Store.ReadText(DeviceNameResource.FileName));
        Assert.DoesNotContain(names.Commands, command => command.Contains("set-hostname", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_frame_converges_normally_the_moment_the_Fleet_Manager_answers()
    {
        var answer = ServerAnswer.Silence;
        using var files = new TemporaryFiles();
        using var harness = new ReconcileHarness(
            Fast,
            new AdoptionResource(files.Store, () => answer),
            new DeviceNameResource(files.Store, () => answer is ServerAnswer.Adopted ? "Hallway" : null));

        await OutageAsync(harness, TimeSpan.FromMinutes(30));
        answer = ServerAnswer.Adopted;

        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.All(outcome.Statuses, status => Assert.Equal(ResourceStatusKind.InSync, status.Kind));
        Assert.Equal(AdoptionResource.AdoptedMarker, files.Store.ReadText(AdoptionResource.FileName));
        Assert.Equal("Hallway", files.Store.ReadText(DeviceNameResource.FileName));

        // Two resources, two applies, two reboots — the outage added none of its own.
        Assert.Equal(2, harness.Boundary.Crossings.Count);
    }

    [Fact]
    public async Task An_authoritative_rejection_still_fails_and_still_escalates_on_the_existing_schedule()
    {
        // The other half of §2.6, and the one that must not be softened. The server answered, and
        // what it said was "you are not adopted" — a real answer about a real state, which walks
        // §2.5's ladder exactly as it always has.
        using var files = new TemporaryFiles();
        using var harness = new ReconcileHarness(Fast, new AdoptionResource(files.Store, () => ServerAnswer.Rejected));
        harness.Telemetry.Connected = true;

        var outcome = await harness.ConvergeAsync();
        var status = ReconcileHarness.StatusOf(outcome, AdoptionResource.ResourceName);

        Assert.Equal(ResourceStatusKind.Escalated, status.Kind);
        Assert.Equal(Fast.AttemptBudget, status.Attempts);
        Assert.Equal(Fast.AttemptBudget, harness.Boundary.Crossings.Count);
        Assert.NotEmpty(harness.Telemetry.OfKind(DeviceEventKinds.Escalation));
        Assert.Equal("waiting for adoption", files.Store.ReadText(AdoptionResource.FileName));
    }

    [Fact]
    public async Task An_authoritative_rejection_still_reaches_Halted_on_the_second_exhaustion()
    {
        using var files = new TemporaryFiles();
        using var harness = new ReconcileHarness(Fast, new AdoptionResource(files.Store, () => ServerAnswer.Rejected));
        harness.Telemetry.Connected = true;

        await harness.ConvergeAsync();

        // §2.5 rung 3: the operator pressed retry, which resets the budget and keeps the
        // escalation count. The second exhaustion is the one that halts the device.
        harness.Loop.ResetBudget(AdoptionResource.ResourceName);
        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Halted, outcome.Result);
        Assert.True(harness.Loop.IsHalted);
    }

    [Fact]
    public async Task An_outage_does_not_erase_a_name_the_Fleet_Manager_issued()
    {
        // Measured shape: the settings a frame holds live in memory, so a reboot during an outage
        // comes back knowing nothing. Treating "not told" as "told: nothing" wrote the empty
        // string over an issued name and then reported the resource green for agreeing with
        // itself — silent data loss behind a green tick.
        string? name = "Hallway";
        using var files = new TemporaryFiles();
        using var harness = new ReconcileHarness(
            Fast,
            new AdoptionResource(files.Store, () => ServerAnswer.Adopted),
            new DeviceNameResource(files.Store, () => name));

        await harness.ConvergeAsync();
        Assert.Equal("Hallway", files.Store.ReadText(DeviceNameResource.FileName));

        name = null;
        var outcome = await OutageAsync(harness, TimeSpan.FromMinutes(30));

        Assert.Equal("Hallway", files.Store.ReadText(DeviceNameResource.FileName));
        Assert.Equal(
            ResourceStatusKind.Blocked,
            ReconcileHarness.StatusOf(outcome, DeviceNameResource.ResourceName).Kind);
    }

    [Fact]
    public async Task A_frame_that_was_adopted_stays_adopted_through_an_outage()
    {
        // The record on disk is the last authoritative answer, so a frame that was green when
        // contact dropped keeps running on it (§2.6). That is why the answer is persisted at all.
        using var files = new TemporaryFiles();
        files.Store.WriteText(AdoptionResource.FileName, AdoptionResource.AdoptedMarker);

        using var harness = new ReconcileHarness(Fast, new AdoptionResource(files.Store, () => ServerAnswer.Silence));
        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.Empty(harness.Boundary.Crossings);
    }

    [Fact]
    public async Task The_device_stays_on_the_silence_rung_rather_than_gaining_one_of_its_own()
    {
        // §2.6's ladder already has NoContact and the link is what publishes it. The loop's job
        // during an outage is to leave that alone — no new rung, and nothing that would move the
        // device off it.
        using var files = new TemporaryFiles();
        using var harness = new ReconcileHarness(Fast, new AdoptionResource(files.Store, () => ServerAnswer.Silence));

        await OutageAsync(harness, TimeSpan.FromMinutes(30));

        Assert.Equal(DeviceState.NoContact, harness.Hub.Current.Condition.State);
        Assert.False(harness.Hub.Current.Reconcile.Halted);
        Assert.Equal(0, harness.Hub.Current.Reconcile.Attempt);
    }

    [Fact]
    public async Task A_resource_that_cannot_be_read_is_asked_again_soon_rather_than_on_the_drift_sweep()
    {
        // A pause with no visible end reads as a hang (§2.7 item 6), and a five-minute drift
        // sweep is the wrong clock for a network round trip that has already failed.
        using var files = new TemporaryFiles();
        var options = Fast with { UnevaluableRecheck = TimeSpan.FromSeconds(30) };
        using var harness = new ReconcileHarness(options, new AdoptionResource(files.Store, () => ServerAnswer.Silence));

        var outcome = await harness.PassAsync();

        Assert.Equal(harness.Clock.UtcNow + TimeSpan.FromSeconds(30), outcome.NextAttemptUtc);
        Assert.Equal(
            outcome.NextAttemptUtc,
            ReconcileHarness.StatusOf(outcome, AdoptionResource.ResourceName).NextAttemptUtc);
    }

    [Fact]
    public async Task A_change_that_cannot_be_verified_after_the_reboot_is_neither_claimed_nor_charged()
    {
        // The server answers, the resource is applied, the frame reboots — and by the time it is
        // back the server has gone. The change is not proven and it has not failed, so the ledger
        // must move in neither direction.
        var resource = new ScriptedResource("app.config.room", desired: "living-room", observed: string.Empty);
        using var harness = new ReconcileHarness(Fast, resource);
        harness.Boundary.OnBoot = (_, _) =>
        {
            resource.Unevaluable = true;
            return Task.CompletedTask;
        };

        var outcome = await harness.PassAsync();
        var status = ReconcileHarness.StatusOf(outcome, "app.config.room");

        Assert.Equal(ResourceStatusKind.Blocked, status.Kind);
        Assert.Equal(ReconcileLoop.SilentAuthority, status.BlockedBy);
        Assert.Equal(1, resource.Acts);

        var ledger = ReconcileJournal.EntryFor(harness.Journal.Read(), "app.config.room");
        Assert.Equal(1, ledger.Attempts);
        Assert.Equal(0, ledger.Escalations);
        Assert.Null(ledger.NextAttemptUtc);

        // Not left mid-apply: the next pass is free to observe everything else on the frame
        // rather than sitting on an unfinished contract for the length of the outage.
        Assert.Null(harness.Journal.Read().Pending);
    }

    [Fact]
    public async Task A_further_outage_after_an_unverified_change_still_costs_nothing()
    {
        var resource = new ScriptedResource("app.config.room", desired: "living-room", observed: string.Empty);
        using var harness = new ReconcileHarness(Fast, resource);
        harness.Boundary.OnBoot = (_, _) =>
        {
            resource.Unevaluable = true;
            return Task.CompletedTask;
        };

        await harness.PassAsync();
        var acts = resource.Acts;
        await OutageAsync(harness, TimeSpan.FromMinutes(30));

        Assert.Equal(acts, resource.Acts);
        Assert.Single(harness.Boundary.Crossings);
        Assert.False(harness.Loop.IsHalted);
    }

    [Fact]
    public async Task An_unevaluable_observation_never_hides_a_resource_that_genuinely_cannot_be_applied()
    {
        // The guard on the guard. A resource whose Act does not work is still drift, still burns
        // its budget and still escalates — the new outcome is reserved for an authority that did
        // not answer, and must never become the place a real failure goes to be quiet.
        var resource = new ScriptedResource("cpu.governor.performance", desired: "performance", observed: "ondemand")
        {
            ActHasNoEffect = true,
        };

        using var harness = new ReconcileHarness(Fast, resource);
        harness.Telemetry.Connected = true;

        var outcome = await harness.ConvergeAsync();
        var status = ReconcileHarness.StatusOf(outcome, "cpu.governor.performance");

        Assert.Equal(ResourceStatusKind.Escalated, status.Kind);
        Assert.Equal(Fast.AttemptBudget, status.Attempts);
        Assert.NotEmpty(harness.Telemetry.OfKind(DeviceEventKinds.Escalation));
    }

    /// <summary>Runs the loop through an outage of <paramref name="duration"/>, as its driver would.</summary>
    /// <remarks>
    /// Passes are driven by hand rather than by <c>RunAsync</c> so the wall clock stays out of it,
    /// and the wait between them is the one the loop itself chose — which is the schedule under
    /// test as much as anything else is. Thirty minutes is already twenty times the whole
    /// escalation ladder at these options; the headline test runs six hours because that is the
    /// shape of the outage an operator actually leaves behind them.
    /// </remarks>
    private static async Task<PassOutcome> OutageAsync(ReconcileHarness harness, TimeSpan duration)
    {
        var until = harness.Clock.UtcNow + duration;
        PassOutcome outcome;

        do
        {
            outcome = await harness.PassAsync();
            harness.Clock.UtcNow = outcome.NextAttemptUtc is { } next && next > harness.Clock.UtcNow
                ? next
                : harness.Clock.UtcNow + TimeSpan.FromMinutes(5);
        }
        while (harness.Clock.UtcNow < until);

        return outcome;
    }
}
