using System.Globalization;

namespace FrameLink.Agent.Reconcile;

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
