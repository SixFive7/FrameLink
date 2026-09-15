namespace FrameLink.Agent.Reconcile;

/// <summary>
/// <b>A loop's own statement that it is ending on purpose.</b> The second legitimate ending,
/// beside the agent's shutdown token — and, like that one, a fact the code states rather than a
/// conclusion drawn from what happened when.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it is for, measured on a frame on 2026-08-30.</b> §2.4's verify-reboot is initiated by
/// the reconcile loop itself: the loop asks the boundary to restart the machine, the boundary
/// answers <see cref="RebootCrossing.Restarting"/>, and the loop returns so the reboot can happen.
/// At that moment the agent's shutdown token is <i>not</i> signalled — nothing is stopping the
/// agent, the machine is going down — so the host's one test for a legitimate ending said no, and
/// every one of those returns read as the reconcile loop dying. On first contact with a real frame
/// that was three verify-reboots, three counted loop deaths, and a frame escalated on
/// <c>agent.loop.reconcile attempts=3 escalations=1</c> for doing exactly what §2.4 asks of it.
/// </para>
/// <para>
/// <b>It does not exempt anything from supervision, and that is the design.</b> This is a
/// statement about <i>one ending</i>, not a property of a loop: the reconcile loop's every other
/// way of ending — a throw, a swallowed cancellation, a <c>break</c> on an unexpected state — is
/// counted exactly as it was, because none of them passes through the one line that declares this.
/// The loop that most needs watching keeps its watch; what it gains is a way to say, in advance
/// and in its own words, that this particular return was the plan.
/// </para>
/// <para>
/// <b>Why it cannot be confused with a death.</b> Three properties, and all three are needed. It
/// is declared <i>before</i> the loop returns, so the task's completion is what publishes it and
/// the reader cannot see a half-written one. It is declared on exactly one path — the one where a
/// reboot boundary has already answered that the machine is going down — rather than anywhere a
/// pass merely looks finished. And the host pairs it with
/// <see cref="TaskStatus.RanToCompletion"/>: a loop that declared a stand-down and then faulted or
/// was cancelled is a death, because the declaration is a claim about a return that then did not
/// happen. See <c>AgentHost.StoodDown</c>, which is where those two facts are read together.
/// </para>
/// <para>
/// <b>First word wins.</b> A second declaration cannot overwrite the first, so the reason a reader
/// gets is the reason the loop gave at the moment it decided — never one written over it on the
/// way out. Nothing clears it, for the same reason: this object's whole life is one process, and
/// the process it belongs to is on its way down.
/// </para>
/// <para>
/// Deliberately not the shutdown token in another coat. A token says "everything is stopping" and
/// is read by every loop; this says "this one loop is stepping aside while the rest keep running",
/// which is a different sentence and calls for a different decision from the host — settle the
/// survivors and wait for the machine, rather than wait for all fifteen to unwind.
/// </para>
/// </remarks>
public sealed class LoopStandDown
{
    private string? _because;

    /// <summary>
    /// Why this loop ended on purpose, or <see langword="null"/> — it never said so.
    /// </summary>
    /// <remarks>
    /// A whole sentence rather than a flag, because it goes straight into the log line a person
    /// with an SSH session reads, beside the name of the loop that said it.
    /// </remarks>
    public string? Because => Volatile.Read(ref _because);

    /// <summary>States that this loop is about to end, and why.</summary>
    /// <param name="because">A whole sentence, in the register the journal is read in.</param>
    public void Declare(string because)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(because);

        // First word wins: see the remarks. A no-op when something has already been declared.
        Interlocked.CompareExchange(ref _because, because, null);
    }
}
