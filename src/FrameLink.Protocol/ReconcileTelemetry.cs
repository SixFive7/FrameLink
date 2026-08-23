namespace FrameLink.Protocol;

/// <summary>
/// §2.3's status vocabulary, spelled the way it travels on the wire.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a set of strings rather than the agent's own enum. The enum's member names are
/// a C# detail the agent is free to rename; these six tokens are contract, and the Fleet
/// Manager stores and renders them. Keeping the mapping explicit in one place on the agent side
/// is what stops a refactor from silently reshaping a stored history.
/// </para>
/// <para>
/// <b><c>halted</c> was removed rather than retired</b> (decision 66). Keeping it as a legacy
/// token would defend against version skew that cannot occur: nothing has shipped, there is one
/// frame on a bench running a binary built the same night, and §4.2's freeze covers the handshake
/// envelope and the update endpoint rather than this vocabulary. A dead token in a contract is a
/// concept a future reader has to ask about for ever.
/// </para>
/// </remarks>
public static class ResourceStatusNames
{
    /// <summary>Observed state matches desired state, verified after a boot.</summary>
    public const string InSync = "in-sync";

    /// <summary>Being acted on right now.</summary>
    public const string Progressing = "progressing";

    /// <summary>Written, but not yet proven to have survived a boot (§2.4).</summary>
    public const string AwaitingReboot = "awaiting-reboot";

    /// <summary>Attempt budget exhausted; carries the delta and attempt count (§2.5).</summary>
    public const string Degraded = "degraded";

    /// <summary>A dependency is not in sync, so this was not attempted (§2.2).</summary>
    public const string Blocked = "blocked";

    /// <summary>
    /// The operator has been notified and offered retry or a shell (§2.5). <b>Terminal.</b>
    /// </summary>
    public const string Escalated = "escalated";
}

/// <summary>What the reconciliation loop as a whole is doing.</summary>
/// <remarks>
/// §3.5 makes live reconciliation progress a first-class GUI screen, and "which resource, which
/// phase" is only half of it — an operator also has to be able to tell a frame that is waiting
/// out a backoff from one that has stopped for good.
/// </remarks>
public static class LoopStateNames
{
    /// <summary>Nothing to do; every resource is in sync.</summary>
    public const string Converged = "converged";

    /// <summary>A pass is running.</summary>
    public const string Reconciling = "reconciling";

    /// <summary>A change is written and the verifying reboot is imminent or in flight.</summary>
    public const string AwaitingReboot = "awaiting-reboot";

    /// <summary>Waiting out a per-resource backoff before the next attempt.</summary>
    public const string BackingOff = "backing-off";

    /// <summary>
    /// At least one resource gave up, so this frame has stopped reconciling and is waiting for a
    /// person (§2.5 rungs 4 and 6).
    /// </summary>
    public const string Escalated = "escalated";
}

/// <summary>Event kinds carried on the <c>events</c> channel of §4.1.</summary>
public static class DeviceEventKinds
{
    /// <summary>A resource was observed away from its desired value.</summary>
    public const string Drift = "drift";

    /// <summary>An attempt budget was exhausted and the operator has been notified (§2.5).</summary>
    public const string Escalation = "escalation";

    /// <summary>The agent started, naming whether it came back across a reboot boundary.</summary>
    public const string Boot = "boot";


    /// <summary>Every resource reached <see cref="ResourceStatusNames.InSync"/>.</summary>
    public const string Converged = "converged";

    /// <summary>
    /// Which firmware the microphone unit is running, reported rather than converged.
    /// </summary>
    /// <remarks>
    /// Not produced by the loop and not about a resource, so <see cref="DeviceEvent.Resource"/> is
    /// null on it. The array's firmware version is a fact about hardware that nothing on the frame
    /// can change without a person present (decision 90), so it travels the events channel as an
    /// observation — like <see cref="Boot"/>, and unlike <see cref="Drift"/>, which asserts that
    /// something is wrong. Nothing alerts on it.
    /// </remarks>
    public const string ArrayFirmware = "array-firmware";

    /// <summary>
    /// The frame's own screen cannot show anything, so the Fleet Manager is the only surface
    /// left (§2.7).
    /// </summary>
    /// <remarks>
    /// Measured on the mule 2026-08-15: on a stock image there is no framebuffer and no
    /// connected DRM output until the panel overlay is applied, yet writes to <c>/dev/tty1</c>
    /// succeed. Without this event a dark frame is indistinguishable from a working one.
    /// </remarks>
    public const string Display = "display";
}

/// <summary>
/// One resource's standing, as the Fleet Manager stores and renders it.
/// <b>Frozen once shipped.</b>
/// </summary>
public sealed record ResourceReport
{
    /// <summary>Catalog id, e.g. <c>cpu.governor.performance</c>.</summary>
    public required string Name { get; init; }

    /// <summary>One of <see cref="ResourceStatusNames"/>.</summary>
    public required string Status { get; init; }

    /// <summary>Expected-versus-observed, present whenever the status is not in sync.</summary>
    public string? Delta { get; init; }

    /// <summary>The exact change the agent last made, for §2.7 item 3.</summary>
    public string? Action { get; init; }

    /// <summary>The dependency that is not in sync, when the status is blocked.</summary>
    public string? BlockedBy { get; init; }

    /// <summary>How many times this resource has been acted on since it was last in sync.</summary>
    public required int Attempts { get; init; }

    /// <summary>The attempt budget those attempts are counted against (§2.7 item 5).</summary>
    public int AttemptBudget { get; init; }

    /// <summary>How many times the operator has been notified about this resource (§2.5).</summary>
    public int Escalations { get; init; }

    /// <summary>When the backoff expires, so a pause never looks like a hang (§2.7 item 6).</summary>
    public DateTimeOffset? NextAttemptUtc { get; init; }
}

/// <summary>
/// The whole loop's state, on the <c>telemetry</c> channel of §4.1.
/// <b>Frozen once shipped.</b>
/// </summary>
/// <remarks>
/// The field list is §3.5's sentence turned into a record: "current resource and phase, settings
/// applied, settings still drifted, reboots expected before convergence, and the per-resource
/// status list". Sent whole rather than as a diff, because a frame that has been offline for a
/// week must be able to say where it stands in one message.
/// </remarks>
public sealed record ReconcileReport
{
    /// <summary>The frame this is about.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Monotonic per-device counter, so a late-draining buffer can be ordered.</summary>
    public required long Sequence { get; init; }

    /// <summary>When the agent produced it, not when the server received it.</summary>
    public required DateTimeOffset GeneratedUtc { get; init; }

    /// <summary>One of <see cref="LoopStateNames"/>.</summary>
    public required string LoopState { get; init; }

    /// <summary>The resource being worked on, or null between passes.</summary>
    public string? CurrentResource { get; init; }

    /// <summary>Which step of §2.3's contract that resource is in.</summary>
    public string? CurrentPhase { get; init; }

    /// <summary>How many resources are verified.</summary>
    public required int InSync { get; init; }

    /// <summary>How many are known to be away from their desired value.</summary>
    public required int Drifted { get; init; }

    /// <summary>How many were not attempted because a dependency is not in sync.</summary>
    public required int Blocked { get; init; }

    /// <summary>
    /// Reboots still expected before this frame converges.
    /// </summary>
    /// <remarks>
    /// §2.4 reboots per resource with no exceptions, so this is simply the count of resources
    /// that are not yet verified — and at 40–60 s a cycle it is the only honest answer to "how
    /// long will this take", which is the first thing an operator watching a bare-metal
    /// provision wants to know.
    /// </remarks>
    public required int RebootsExpected { get; init; }

    /// <summary>Every resource in the catalog, in dependency order.</summary>
    public required IReadOnlyList<ResourceReport> Resources { get; init; }
}

/// <summary>
/// One thing that happened, on the <c>events</c> channel of §4.1.
/// <b>Frozen once shipped.</b>
/// </summary>
/// <remarks>
/// Separate from <see cref="ReconcileReport"/> because the two have different lifetimes: a report
/// is the current picture and only the latest one matters, whereas an event is history and §3.5
/// keeps a month of it. Collapsing them would either throw away the history or store a full
/// resource list per drift.
/// </remarks>
public sealed record DeviceEvent
{
    /// <summary>The frame this is about.</summary>
    public required string DeviceId { get; init; }

    /// <summary>One of <see cref="DeviceEventKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>When it happened on the frame.</summary>
    public required DateTimeOffset OccurredUtc { get; init; }

    /// <summary>The resource involved, when there was one.</summary>
    public string? Resource { get; init; }

    /// <summary>One sentence an operator can read.</summary>
    public required string Summary { get; init; }

    /// <summary>Expected-versus-observed, verbatim (§2.5).</summary>
    public string? Delta { get; init; }

    /// <summary>Attempt count at the moment of the event.</summary>
    public int Attempts { get; init; }
}
