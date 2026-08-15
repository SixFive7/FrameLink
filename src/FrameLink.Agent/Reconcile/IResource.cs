namespace FrameLink.Agent.Reconcile;

/// <summary>The status vocabulary of §2.3.</summary>
/// <remarks>
/// M1 produces only <see cref="InSync"/>, <see cref="Progressing"/> and <see cref="Degraded"/>.
/// The rest are declared now because they are the vocabulary the Fleet Manager renders against,
/// and a half-declared enum would have to break the wire later. Reaching them needs the DAG,
/// the reboot-verified apply and the escalation ladder — all M2 (§5.1).
/// </remarks>
public enum ResourceStatusKind
{
    /// <summary>Observed state matches desired state, verified.</summary>
    InSync,

    /// <summary>Being acted on right now.</summary>
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
public sealed record ResourceObservation(bool InSync, string Expected, string Observed);

/// <summary>The reconciler's verdict on one resource.</summary>
public sealed record ResourceStatus
{
    /// <summary>Stable identifier of the resource.</summary>
    public required string Name { get; init; }

    /// <summary>Where it stands.</summary>
    public required ResourceStatusKind Kind { get; init; }

    /// <summary>Expected-versus-observed, present whenever the kind is not <see cref="ResourceStatusKind.InSync"/>.</summary>
    public string? Delta { get; init; }

    /// <summary>How many times the resource has been acted on.</summary>
    public int Attempts { get; init; }

    /// <summary>The exact change that was made, for the screen's "what is being done" (§2.7).</summary>
    public string? Action { get; init; }
}

/// <summary>
/// The resource contract of §2.3: <b>Observe → Compare → Act (only on drift) → Verify → Status</b>.
/// </summary>
/// <remarks>
/// <para>
/// Observe and Verify are deliberately the same method. §2.3 requires it, and the reason is
/// v1's governor bug: a check written against "did the write succeed" reports success while the
/// setting is quietly wrong. There is only one way to ask what is true, so a verify cannot
/// drift from an observe.
/// </para>
/// <para>
/// Compare is not a method either — it is the <see cref="ResourceObservation.InSync"/> flag the
/// observation already carries, because only the resource knows what equality means for its own
/// value.
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

    /// <summary>Reads the world. Used both to decide and, unchanged, to verify.</summary>
    ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken);

    /// <summary>Applies the desired value, returning the exact change made.</summary>
    ValueTask<string> ActAsync(CancellationToken cancellationToken);
}
