using FrameLink.Protocol;

namespace FrameLink.Agent.Reconcile;

/// <summary>The status vocabulary of §2.3.</summary>
/// <remarks>
/// All seven are reachable as of M2. The paths are worth naming because several of them only
/// exist in combination: <see cref="AwaitingReboot"/> requires the journal, <see cref="Blocked"/>
/// requires the DAG, and <see cref="Escalated"/> is distinguished from <see cref="Degraded"/>
/// only by whether the notification actually reached the Fleet Manager rather than the frame's
/// offline buffer.
/// </remarks>
public enum ResourceStatusKind
{
    /// <summary>Observed state matches desired state, verified.</summary>
    InSync,

    /// <summary>Being acted on right now, or waiting out a retry backoff.</summary>
    Progressing,

    /// <summary>Written, but not yet proven to have survived a boot (§2.4).</summary>
    AwaitingReboot,

    /// <summary>Attempt budget exhausted; carries the exact delta and attempt count (§2.5).</summary>
    Degraded,

    /// <summary>A dependency is not <see cref="InSync"/>, so this was not attempted (§2.2).</summary>
    Blocked,

    /// <summary>The operator has been notified and offered retry or a shell (§2.5).</summary>
    Escalated,

    /// <summary>Escalated more than once; the agent has stopped touching it (§2.5).</summary>
    Halted,
}

/// <summary>What a single Observe found.</summary>
/// <param name="InSync">Whether observed equals desired.</param>
/// <param name="Expected">The desired value, as a person would read it.</param>
/// <param name="Observed">What is actually there.</param>
public sealed record ResourceObservation(bool InSync, string Expected, string Observed)
{
    /// <summary>Expected-versus-observed in the one form §2.5 requires everywhere.</summary>
    public string Delta => $"expected '{Expected}', observed '{Observed}'";
}

/// <summary>
/// The change a resource made, in the two registers §2.7 item 3 asks for.
/// </summary>
/// <param name="Change">The exact command or file change, verbatim.</param>
/// <param name="Gloss">
/// One plain-language sentence for the person standing in front of the frame. Written by the
/// resource because only the resource knows what its change means; a gloss synthesised by the
/// loop could only ever restate the command in different words.
/// </param>
public readonly record struct ResourceAction(string Change, string Gloss);

/// <summary>The reconciler's verdict on one resource.</summary>
public sealed record ResourceStatus
{
    /// <summary>Stable identifier of the resource.</summary>
    public required string Name { get; init; }

    /// <summary>Where it stands.</summary>
    public required ResourceStatusKind Kind { get; init; }

    /// <summary>Expected-versus-observed, present whenever the kind is not <see cref="ResourceStatusKind.InSync"/>.</summary>
    public string? Delta { get; init; }

    /// <summary>How many times the resource has been acted on since it was last in sync.</summary>
    public int Attempts { get; init; }

    /// <summary>The budget those attempts count against, for "Attempt 2 of 5" (§2.7 item 5).</summary>
    public int AttemptBudget { get; init; }

    /// <summary>The exact change that was made, for the screen's "what is being done" (§2.7).</summary>
    public string? Action { get; init; }

    /// <summary>Plain-language gloss on <see cref="Action"/>.</summary>
    public string? Gloss { get; init; }

    /// <summary>Which dependency is holding this resource up (§2.2).</summary>
    public string? BlockedBy { get; init; }

    /// <summary>How many times the operator has been notified about this resource (§2.5).</summary>
    public int Escalations { get; init; }

    /// <summary>When the backoff ends, so a pause never reads as a hang (§2.7 item 6).</summary>
    public DateTimeOffset? NextAttemptUtc { get; init; }

    /// <summary>The wire spelling of <see cref="Kind"/>.</summary>
    public string WireStatus => Kind switch
    {
        ResourceStatusKind.InSync => ResourceStatusNames.InSync,
        ResourceStatusKind.Progressing => ResourceStatusNames.Progressing,
        ResourceStatusKind.AwaitingReboot => ResourceStatusNames.AwaitingReboot,
        ResourceStatusKind.Degraded => ResourceStatusNames.Degraded,
        ResourceStatusKind.Blocked => ResourceStatusNames.Blocked,
        ResourceStatusKind.Escalated => ResourceStatusNames.Escalated,
        _ => ResourceStatusNames.Halted,
    };

    /// <summary>Turns this into the shape the Fleet Manager stores (§3.5).</summary>
    public ResourceReport ToReport() => new()
    {
        Name = Name,
        Status = WireStatus,
        Delta = Delta,
        Action = Action,
        BlockedBy = BlockedBy,
        Attempts = Attempts,
        AttemptBudget = AttemptBudget,
        Escalations = Escalations,
        NextAttemptUtc = NextAttemptUtc,
    };
}

/// <summary>
/// The resource contract of §2.3: <b>Observe → Compare → Act (only on drift) → Verify → Status</b>.
/// </summary>
/// <remarks>
/// <para>
/// Observe and Verify are deliberately the same method. §2.3 requires it, and the reason is
/// v1's governor bug: a check written against "did the write succeed" reports success while the
/// setting is quietly wrong. There is only one way to ask what is true, so a verify cannot
/// drift from an observe — and every guide CHECKPOINT becomes such a check for free, which is
/// what M3's state-diff harness consumes.
/// </para>
/// <para>
/// Compare is not a method either — it is the <see cref="ResourceObservation.InSync"/> flag the
/// observation already carries, because only the resource knows what equality means for its own
/// value.
/// </para>
/// <para>
/// There is no Reboot member and there never will be. §2.4: <b>every resource reboots, no
/// exceptions, no per-resource cleverness</b> — deciding which settings need one is exactly the
/// reasoning that produced the governor bug, so the decision is taken away from the resource
/// and lives in the loop.
/// </para>
/// </remarks>
public interface IResource
{
    /// <summary>Stable identifier, used in telemetry and on screen.</summary>
    string Name { get; }

    /// <summary>What was detected, in plain language, for §2.7's repair screen.</summary>
    string Detected { get; }

    /// <summary>Why it matters, in one short sentence.</summary>
    string WhyItMatters { get; }

    /// <summary>
    /// Ids that must be <see cref="ResourceStatusKind.InSync"/> before this is attempted (§2.2).
    /// </summary>
    /// <remarks>
    /// Empty means the resource depends only on the agent roots. Dependents of a resource that
    /// is not in sync are marked <see cref="ResourceStatusKind.Blocked"/> rather than being let
    /// loose to fail confusingly on their own.
    /// </remarks>
    IReadOnlyList<string> DependsOn => [];

    /// <summary>Reads the world. Used both to decide and, unchanged, to verify.</summary>
    ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken);

    /// <summary>Applies the desired value, returning the exact change made and its gloss.</summary>
    ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken);
}
