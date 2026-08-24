using System.Globalization;
using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Reconcile;

/// <summary>What the host does about a loop that ended.</summary>
/// <param name="Verdict">
/// The ledger's last word on it, which is <see cref="AgentLoopFailures.Refused"/>'s rather than
/// <see cref="AgentLoopFailures.Record"/>'s whenever the restart could not be taken.
/// </param>
/// <param name="Restarting">Whether the machine has been asked to go down.</param>
/// <param name="Refusal">
/// Why it was not, or null. Non-null is the signal that the row and the event changed after they
/// were first sent, and therefore have to be sent again.
/// </param>
public readonly record struct AgentLoopOutcome(AgentLoopVerdict Verdict, bool Restarting, string? Refusal);

/// <summary>What the ledger says after a supervised loop ended on its own.</summary>
/// <param name="Resource">The ledger id this was recorded under.</param>
/// <param name="Attempts">How many consecutive short-lived runs have now ended this way.</param>
/// <param name="Budget">The budget those attempts count against.</param>
/// <param name="Stopped">Whether the budget is gone, so the frame stops rather than restarting.</param>
/// <param name="Row">The status row the screen and the Fleet Manager render.</param>
public readonly record struct AgentLoopVerdict(
    string Resource,
    int Attempts,
    int Budget,
    bool Stopped,
    ResourceStatus Row);

/// <summary>
/// <b>A loop that ends while the agent is still running is a failure like any other</b> — recorded
/// in the same ledger, against the same budget of three, and rendered on the same screen.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes is that a loop which simply <i>returned</i> was invisible.</b> The host
/// reacted to a fault and to nothing else, so a swallowed cancellation, a <c>break</c> on an
/// unexpected state, or a task that completed because its input closed took a whole responsibility
/// off the frame with no log line, no telemetry and no change on the panel — while every surface
/// went on reporting a healthy agent. Fourteen loops were watched for one of the two ways a loop
/// can die, and a fifteenth (the local origin's accept loop) was not watched at all.
/// </para>
/// <para>
/// <b>It is not a special case, and that is the design.</b> The record written here is an ordinary
/// <see cref="ResourceLedgerEntry"/>, so everything downstream already works on it:
/// <see cref="ReconcileLoop.HasStopped"/> reads it and stops the pass (decision 68),
/// <see cref="ReconcileLoop.ResetExhaustedBudgets"/> clears it when somebody presses retry or
/// restart, and <c>OrphanedGiveUps</c> renders it on the screen from the ledger's own last record
/// — which is exactly the path that already exists for a resource this build's catalog no longer
/// has. No new rung, no second screen, no second recovery.
/// </para>
/// <para>
/// <b>Forgiveness reuses <see cref="ReconcileOptions.ConflictHold"/> rather than inventing a
/// number.</b> The question is identical to decision 78's — <i>did this hold long enough to forgive
/// what came before?</i> — so a process that ran longer than that hold before its loop ended starts
/// the count again, and only runs that fail quickly, one after another, walk the ladder. Without it
/// three unrelated loop deaths spread over a year would eventually stop a frame that had been
/// working the whole time.
/// </para>
/// </remarks>
public static class AgentLoopFailures
{
    /// <summary>The ledger id prefix, so these read as what they are beside the resources.</summary>
    public const string LedgerPrefix = "agent.loop.";

    /// <summary>What the screen says was detected, whichever loop it was.</summary>
    /// <remarks>
    /// Deliberately not the loop's name: the person in front of the frame cannot do anything
    /// differently for a browser stage than for a package inventory, and the name is in the
    /// technical block a line below for the person who can.
    /// </remarks>
    public const string Detected = "Part of this frame's own software stopped running.";

    /// <summary>Why that matters, for the same reader.</summary>
    public const string WhyItMatters =
        "The frame cannot look after itself with a piece of it missing, so it has stopped rather than "
        + "carry on looking well.";

    /// <summary>
    /// <b>What the ledger says when the restart attempts one and two ask for could not be
    /// taken.</b> The ladder ends here, because the rung it was about to climb is gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a refusal has to be terminal rather than merely logged.</b> Attempts one and two are
    /// "restart the frame and try again". When the restart is refused — the allowance is spent, the
    /// card cannot record the spend, decision 79's floor has closed, or a firmware write is holding
    /// the machine still — there is no automatic retry left to wait for: this process is parked, the
    /// loop that died is not coming back, and nothing is scheduled. A frame in that state that went
    /// on reporting <c>attempt 1 of 3, reconciling</c> would be a frame whose screen offered a
    /// countdown that will never end instead of the two buttons that would fix it, which is
    /// decision 75's failure exactly.
    /// </para>
    /// <para>
    /// <b>It is the same row every other give-up writes, and that is what makes it need no new
    /// rung.</b> Attempts go to the budget and the escalation count goes up by one, so
    /// <see cref="ReconcileLoop.HasGivenUp"/> reads it, <see cref="ReconcileLoop.HasStopped"/> stops
    /// the pass around it, the walk's orphan path renders it, the screen offers restart and
    /// shutdown, and a retry clears it. Nothing downstream learns a new word.
    /// </para>
    /// <para>
    /// <b>The refusal joins the delta rather than replacing it.</b> Two facts have to survive to the
    /// screen and the notification — which loop stopped, and why the frame did not restart itself
    /// over it — and dropping either leaves a reader with half a story. So the delta is the whole of
    /// what was expected and observed, then the refusal, in the order they happened.
    /// </para>
    /// </remarks>
    /// <param name="journal">The durable ledger.</param>
    /// <param name="options">The budget this is written against.</param>
    /// <param name="verdict">What <see cref="Record"/> just wrote for this loop.</param>
    /// <param name="refusal">A whole sentence saying why the frame did not restart.</param>
    public static AgentLoopVerdict Refused(
        ReconcileJournal journal,
        ReconcileOptions options,
        AgentLoopVerdict verdict,
        string refusal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(refusal);

        var budget = options.AttemptBudget;

        // At the budget, never past it: decision 74's clamp is what keeps every "attempt N of M" on
        // every surface from asserting a pair that cannot be true. Never below one either, because a
        // budget of zero still has to leave a count that HasGivenUp can read as exhausted.
        var attempts = ReconcileLoop.AttemptsWithin(Math.Max(budget, 1), budget);
        var previous = ReconcileJournal.EntryFor(journal.Read(), verdict.Resource);

        // Escalations has to reach one or HasGivenUp will not see this as a give-up at all, and the
        // frame would sit parked with a screen that says it is still trying.
        var escalations = previous.Escalations + 1;
        var delta = string.Create(CultureInfo.InvariantCulture, $"{verdict.Row.Delta}; {refusal}");

        journal.Update(state => ReconcileJournal.WithEntry(
            state,
            ReconcileJournal.EntryFor(state, verdict.Resource) with
            {
                Attempts = attempts,
                Escalations = escalations,

                // Whether the escalation reached the Fleet Manager is decided by the send that
                // happens after this, not here, and claiming it from a buffered event is the
                // Degraded-versus-Escalated confusion ResourceLedgerEntry warns about.
                EscalationNotified = false,
                Delta = delta,
                Change = refusal,
                NextAttemptUtc = null,
                Reversions = 0,
                HeldExpected = null,
                HeldSinceUtc = null,
            }));

        return verdict with
        {
            Attempts = attempts,
            Stopped = true,
            Row = verdict.Row with
            {
                Kind = ResourceStatusKind.Degraded,
                Delta = delta,
                Action = refusal,
                Attempts = attempts,
                Escalations = escalations,
            },
        };
    }

    /// <summary>
    /// <b>What a frame does about a loop that ended: restart itself, or stop and ask.</b> The
    /// operator's model, unchanged — reboot and retry automatically three times, and after the third
    /// no automatic reboot, the screen with Restart and Shutdown, no timer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Attempts one and two reboot the machine, not the process.</b> Standing the agent down and
    /// letting the unit's <c>Restart=always</c> bring it straight back was cheaper and was not the
    /// same thing: a fresh process inherits the machine's state, and the whole reason §2.4 reboots
    /// for every change is that the machine's state is not something a write can be trusted about.
    /// A loop dying is treated identically to every other failure, which is the operator's rule
    /// and the reason there is no third behaviour here.
    /// </para>
    /// <para>
    /// <b>It crosses the same boundary a resource's reboot crosses</b>, so a firmware write in
    /// flight holds it off (decision 91) and decision 79's floor counts it, with no new vocabulary
    /// anywhere. What the change does <i>not</i> inherit is systemd's start limit:
    /// <c>StartLimitBurst</c> counts process starts inside <c>StartLimitIntervalSec</c> and that
    /// count is in the running <c>systemd</c>, so it does not survive the reboot it would be
    /// bounding. Moving this path from a process restart to a machine restart therefore removes the
    /// one protection on it that needed no durable state at all, and
    /// <see cref="RebootAllowance"/> is what replaces it.
    /// </para>
    /// <para>
    /// <b>Three layers, and they are consulted in order of how much they trust.</b> The ladder in
    /// <see cref="Record"/> is the diagnosis and reads the journal. <see cref="RebootAllowance"/> is
    /// the backstop and reads nothing that can be parsed, so it holds when the journal does not —
    /// including the two cases no reader of that file can detect: a journal that is genuinely absent,
    /// which is correctly read as a first boot, and one that reads perfectly and cannot be written.
    /// Decision 79's floor is underneath both, bounds every reboot on the frame rather than only
    /// these, and refuses outright while <see cref="ReconcileJournal.Unreadable"/> is set. A refusal
    /// from any of them is the same outcome, because a frame that must not restart itself must not
    /// restart itself for whichever reason.
    /// </para>
    /// <para>
    /// <b>The allowance is spent before the boundary is crossed, and a refused crossing does not
    /// give it back.</b> It has to be: on a frame the process does not survive the crossing, so
    /// anything written afterwards is written never. The cost is one restart lost to a refusal that
    /// happened for some other reason — and it is bounded at exactly one, because a refusal ends the
    /// ladder here, so a second cannot follow without a person having pressed something that refills
    /// it. Paying that back would mean a second way for the count to go <i>up</i>, which is the one
    /// property the file's whole design is a proof of.
    /// </para>
    /// <para>
    /// <b>The allowance is refilled by a run that lasted, and the test is the ladder's own.</b>
    /// <see cref="ReconcileOptions.ConflictHold"/> is what <see cref="Record"/> forgives on, so
    /// using anything else here would let the two mechanisms disagree about how many restarts a
    /// frame is owed — and the one that disagreed downwards would be the one that mattered.
    /// </para>
    /// </remarks>
    /// <param name="journal">The durable ledger.</param>
    /// <param name="options">The budget, the hold and the floor's numbers.</param>
    /// <param name="allowance">The journal-free backstop.</param>
    /// <param name="reboots">The boundary that restarts the machine.</param>
    /// <param name="verdict">What <see cref="Record"/> just wrote for this loop.</param>
    /// <param name="ranFor">How long this process ran before the loop ended.</param>
    /// <param name="why">What happened to the loop, verbatim, for the boundary's own log line.</param>
    /// <param name="log">Where the decision is recorded.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<AgentLoopOutcome> RestartOrStopAsync(
        ReconcileJournal journal,
        ReconcileOptions options,
        RebootAllowance allowance,
        IRebootBoundary reboots,
        AgentLoopVerdict verdict,
        TimeSpan ranFor,
        string why,
        IAgentLog log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(allowance);
        ArgumentNullException.ThrowIfNull(reboots);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(why);

        var name = NameOf(verdict.Resource);

        if (verdict.Stopped)
        {
            // Attempt three, and the frame stays up. The surviving loops keep running so the screen
            // says what happened and the two buttons on it still work.
            log.Fail(
                $"'{name}' has ended {verdict.Attempts} times. This frame has stopped and is waiting "
                + "for a person: restart it or shut it down from its own screen, or restart it from "
                + "the Fleet Manager.");

            return new AgentLoopOutcome(verdict, false, null);
        }

        if (ranFor >= options.ConflictHold)
        {
            allowance.Refill();
        }

        var grant = allowance.TrySpend();

        if (grant.Granted)
        {
            log.Fail(
                $"Restarting this frame over '{name}': attempt {verdict.Attempts} of {verdict.Budget}, "
                + $"{grant.Remaining} automatic restart(s) left after this one.");

            var crossing = await reboots
                .CrossAsync(
                    new RebootRequest
                    {
                        Resource = verdict.Resource,
                        Change = why,
                        Attempt = verdict.Attempts,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (crossing.Crossing != RebootCrossing.Refused)
            {
                // Restarting is the machine on its way down; Crossed is the in-process boundary and
                // is only reachable from a container. Either way there is nothing left to decide.
                return new AgentLoopOutcome(verdict, true, null);
            }

            return Stop(journal, options, verdict, name, Worded(crossing.Detail), log);
        }

        return Stop(journal, options, verdict, name, Worded(grant.Refusal), log);
    }

    /// <summary>The bare loop name behind a ledger id.</summary>
    /// <remarks>
    /// The log reads for a person with an SSH session, and <c>agent.loop.local-origin</c> is the
    /// ledger's word rather than theirs. The prefix is still what the screen's technical block and
    /// the Fleet Manager's row carry, because there it sits beside resource ids and has to sort with
    /// them.
    /// </remarks>
    public static string NameOf(string resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return resource.StartsWith(LedgerPrefix, StringComparison.Ordinal)
            ? resource[LedgerPrefix.Length..]
            : resource;
    }

    /// <summary>A refusal that arrived without words, given some.</summary>
    private static string Worded(string? refusal) =>
        refusal is { Length: > 0 } said ? said : "this frame's restart was refused and no reason was given";

    private static AgentLoopOutcome Stop(
        ReconcileJournal journal,
        ReconcileOptions options,
        AgentLoopVerdict verdict,
        string name,
        string refusal,
        IAgentLog log)
    {
        var stopped = Refused(journal, options, verdict, refusal);

        log.Fail(
            $"'{name}' ended and this frame did not restart itself: {refusal}. It has stopped and is "
            + "waiting for a person: restart it or shut it down from its own screen, or restart it "
            + "from the Fleet Manager.");

        return new AgentLoopOutcome(stopped, false, refusal);
    }

    /// <summary>The ledger id for one loop.</summary>
    public static string ResourceFor(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return LedgerPrefix + name;
    }

    /// <summary>
    /// Records that <paramref name="name"/> ended, and says whether the frame stops now.
    /// </summary>
    /// <param name="journal">The durable ledger.</param>
    /// <param name="options">The budget and the hold this is judged against.</param>
    /// <param name="name">The loop's stable id, e.g. <c>local-origin</c>.</param>
    /// <param name="purpose">What it was expected to be doing, as a person would read it.</param>
    /// <param name="why">What actually happened, as a person would read it.</param>
    /// <param name="ranFor">How long this process ran before the loop ended.</param>
    /// <param name="notified">Whether the escalation reached the Fleet Manager.</param>
    public static AgentLoopVerdict Record(
        ReconcileJournal journal,
        ReconcileOptions options,
        string name,
        string purpose,
        string why,
        TimeSpan ranFor,
        bool notified = false)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(why);

        var resource = ResourceFor(name);
        var budget = options.AttemptBudget;
        var previous = ReconcileJournal.EntryFor(journal.Read(), resource);

        // A process that ran longer than the hold has demonstrated that whatever ended this loop is
        // not the same fault the earlier attempts were about.
        var carried = ranFor >= options.ConflictHold
            ? 0
            : ReconcileLoop.AttemptsWithin(previous, budget);

        var attempts = ReconcileLoop.AttemptsWithin(carried + 1, budget);
        var stopped = attempts >= budget;
        var escalations = stopped ? previous.Escalations + 1 : previous.Escalations;

        var delta = string.Create(
            CultureInfo.InvariantCulture,
            $"expected {purpose}, observed: {why}");

        journal.Update(state => ReconcileJournal.WithEntry(
            state,
            ReconcileJournal.EntryFor(state, resource) with
            {
                Attempts = attempts,
                Escalations = escalations,
                EscalationNotified = stopped && notified,
                Delta = delta,
                Change = why,

                // Nothing schedules another attempt: an agent that is going to be restarted is
                // restarted by systemd, and an agent that has stopped is waiting for a person. A
                // stored next-attempt time would be a promise with nothing behind it.
                NextAttemptUtc = null,
                Reversions = 0,
                HeldExpected = null,
                HeldSinceUtc = null,
            }));

        return new AgentLoopVerdict(
            resource,
            attempts,
            budget,
            stopped,
            new ResourceStatus
            {
                Name = resource,
                Kind = stopped
                    ? notified ? ResourceStatusKind.Escalated : ResourceStatusKind.Degraded
                    : ResourceStatusKind.Progressing,
                Delta = delta,
                Action = why,
                Detected = Detected,
                WhyItMatters = WhyItMatters,
                Attempts = attempts,
                AttemptBudget = budget,
                Escalations = escalations,
            });
    }
}
