namespace FrameLink.Agent.Resources;

/// <summary>
/// What the Fleet Manager has actually said to this frame — §2.6's <b>rejection is an answer;
/// silence is not</b>, as a value a resource can hold.
/// </summary>
/// <remarks>
/// <para>
/// It is three states rather than two because a boolean can only carry the answer, never whether
/// there <i>was</i> one. Every consumer of a two-state adoption flag has to invent a meaning for
/// "not adopted" when nothing has answered, and the meaning it invents is "the server said no" —
/// which is the false diagnosis this type exists to make unrepresentable.
/// </para>
/// <para>
/// A resource asked to converge on a value it has not been given must return
/// <see cref="Reconcile.ResourceObservation.Unevaluable"/> rather than reporting drift.
/// </para>
/// </remarks>
public enum ServerAnswer
{
    /// <summary>
    /// Nothing. The Fleet Manager has not answered this frame since the agent started.
    /// </summary>
    /// <remarks>
    /// Not "no". A frame in this state may be adopted, blocked, or unknown to a server that was
    /// rebuilt an hour ago, and it cannot tell which — so it concludes nothing, changes nothing
    /// and reboots for nothing. The device sits on §2.6's <c>NoContact</c> rung meanwhile, which
    /// is the rung silence already has.
    /// </remarks>
    Silence = 0,

    /// <summary>
    /// It answered, and the answer was not "you are adopted" — pending, blocked, not-configured
    /// or a refused key.
    /// </summary>
    /// <remarks>
    /// A real answer, and it fails the adoption resource exactly as it always has: the frame acts
    /// on it, cannot verify it, and walks §2.5's ladder to <c>Escalated</c> if it keeps not being
    /// adopted. Nothing about this path changes.
    /// </remarks>
    Rejected = 1,

    /// <summary>It answered "you are adopted".</summary>
    Adopted = 2,
}
