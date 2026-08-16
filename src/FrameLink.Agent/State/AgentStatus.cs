using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;
using FrameLink.Protocol;

namespace FrameLink.Agent.State;

/// <summary>
/// The narration §2.7 requires: what was detected, why it matters, and what is being done.
/// </summary>
public sealed record Narration
{
    /// <summary>Nothing to narrate.</summary>
    public static Narration None { get; } = new();

    /// <summary>What was detected, in plain language.</summary>
    public string? Detected { get; init; }

    /// <summary>Why it matters, one short sentence.</summary>
    public string? WhyItMatters { get; init; }

    /// <summary>The exact command or change being made.</summary>
    public string? Action { get; init; }

    /// <summary>A plain-language gloss on <see cref="Action"/>.</summary>
    public string? ActionGloss { get; init; }
}

/// <summary>
/// §2.10's annotation — <b>an annotation, not a rung</b>.
/// </summary>
/// <remarks>
/// <para>
/// §2.6's rungs answer exactly one question — does the product run? — and a supervision action
/// does not change that answer, so it cannot become a rung without either duplicating
/// <c>InSync</c> or stopping the product. <b>A supervised restart while <c>InSync</c> leaves the
/// device <c>InSync</c>.</b> This record therefore sits beside <see cref="AgentStatus.Condition"/>
/// and never replaces it, and any state can carry it.
/// </para>
/// <para>
/// Below fault level it is operator-facing only — telemetry and the Fleet Manager's device row. At
/// fault level it also renders on the frame, as the small persistent overlay §2.6 gives
/// <c>NoContact</c>, because a frame visibly blinking every ten minutes is an abnormal condition
/// and §1.2 principle 3 says abnormal conditions are named on the frame's own screen. The overlay
/// does not stop the product; that is the point of it being an annotation.
/// </para>
/// </remarks>
public sealed record SupervisionAnnotation
{
    /// <summary>Which of §2.10's four behaviours last acted.</summary>
    public required string Behaviour { get; init; }

    /// <summary>When it acted.</summary>
    public required DateTimeOffset LastActionUtc { get; init; }

    /// <summary>How many times that behaviour has acted inside the fault window.</summary>
    public required int ActionsInWindow { get; init; }

    /// <summary>Whether the rate has passed <c>supervision.faultRateThreshold</c>.</summary>
    public required bool AtFaultLevel { get; init; }

    /// <summary>The measured value against its threshold, in words.</summary>
    public string? Detail { get; init; }

    /// <summary>The overlay sentence, or null below fault level.</summary>
    public string? Overlay => AtFaultLevel
        ? $"This frame keeps repairing itself ({Behaviour}, {ActionsInWindow} times recently). It is still showing your photos."
        : null;
}

/// <summary>
/// Everything the frame's screen and the Fleet Manager know about this agent right now.
/// </summary>
/// <remarks>
/// An immutable snapshot rather than a mutable object with change events on each field. The
/// console stage repaints whole frames (§2.7), so it wants one coherent picture; and a record
/// that is replaced wholesale cannot be half-updated while it is being rendered.
/// </remarks>
public sealed record AgentStatus
{
    private static readonly IReadOnlyList<Uri> NoEndpoints = [];
    private static readonly IReadOnlyList<ResourceStatus> NoResources = [];

    /// <summary>Where the device stands on §2.6's ladder.</summary>
    public required DeviceCondition Condition { get; init; }

    /// <summary>
    /// The last condition the server actually answered with, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Kept separately from <see cref="Condition"/> because §2.6 makes the product's fate on
    /// silence depend on what was true <i>before</i> the silence started.
    /// </remarks>
    public DeviceCondition? LastAuthoritative { get; init; }

    /// <summary>The device id, shown for bench matching (§3.3).</summary>
    public string DeviceId { get; init; } = "unknown";

    /// <summary>Board serial, shown beside the device id.</summary>
    public string? HardwareSerial { get; init; }

    /// <summary>This build's version.</summary>
    public string AgentVersion { get; init; } = AgentBuild.Version;

    /// <summary>The version the Fleet Manager serves, once it has said (§2.8).</summary>
    public string? ServedAgentVersion { get; init; }

    /// <summary>Endpoints being tried, in order (§4.3).</summary>
    public IReadOnlyList<Uri> Endpoints { get; init; } = NoEndpoints;

    /// <summary>The endpoint of the current or most recent attempt.</summary>
    public Uri? CurrentEndpoint { get; init; }

    /// <summary>How many consecutive connection attempts have failed.</summary>
    public int Attempt { get; init; }

    /// <summary>How long the agent is waiting before the next attempt.</summary>
    public TimeSpan BackoffTotal { get; init; }

    /// <summary>When the wait ends, so remaining time can be rendered as it shrinks.</summary>
    public DateTimeOffset? BackoffEndsAt { get; init; }

    /// <summary>Whether a connection is live right now.</summary>
    public bool Connected { get; init; }

    /// <summary>Narration for the repair screen.</summary>
    public Narration Narration { get; init; } = Narration.None;

    /// <summary>
    /// The reconciliation loop's own attempt, backoff, countdown and escalation state (§2.7).
    /// </summary>
    public ReconcileNarration Reconcile { get; init; } = ReconcileNarration.None;

    /// <summary>Per-resource status list.</summary>
    public IReadOnlyList<ResourceStatus> Resources { get; init; } = NoResources;

    /// <summary>Fraction of an update download completed, when one is running.</summary>
    public double? UpdateProgress { get; init; }

    /// <summary>
    /// A new binary is in place and the process is about to stand aside for it (§2.8).
    /// </summary>
    /// <remarks>
    /// Published rather than acted on directly, because the live connection has to be told: an
    /// attempt that is happily reading a healthy socket would otherwise keep the old binary alive
    /// for as long as the Fleet Manager stayed up.
    /// </remarks>
    public bool RestartPending { get; init; }

    /// <summary>
    /// Whether the console stage can actually be seen, once it has been asked.
    /// </summary>
    /// <remarks>
    /// §2.7 bans blank screens, and on a stock image the screen is blank for reasons the agent
    /// cannot fix until the panel overlay resource lands. Decision 46 buys that back as far as it
    /// can be bought: the catalog's ordering puts <c>boot.config.dtoverlay-waveshare-panel</c>
    /// <b>3rd of 80</b>, ahead of adoption, so the dark window is three cycles rather than the
    /// seventy-five §5.5's brick-capable-last default would have cost. Three is the floor and not
    /// zero — nothing can be shown before the overlay lands — so the window still exists, and
    /// carrying the answer here is what lets the one surface that <i>is</i> reachable say so.
    /// </remarks>
    public DisplayVisibility? ConsoleVisibility { get; init; }

    /// <summary>
    /// What the frame's own screen can do about a resource that has given up (§2.7 item 9,
    /// decision 77).
    /// </summary>
    /// <remarks>
    /// Two facts, and the screen needs both: whether there is a touchscreen at all — which decides
    /// whether the console offers a retry or names the Fleet Manager instead — and when the finger
    /// currently on the screen went down, which is what the hold indicator is drawn from. The
    /// default is a frame with no touchscreen, because that is the honest answer everywhere except
    /// a frame with a panel attached, including every machine the test suite runs on.
    /// </remarks>
    public TouchRetryState Touch { get; init; } = TouchRetryState.None;

    /// <summary>
    /// Whether the reconciler currently sees drift that §2.6 says must stop the product.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §2.6: "<b>Any drift stops the product</b>, including an active call. Correctness and
    /// transparency outrank call continuity; in normal operation nothing drifts, and when it does,
    /// everyone can see why." Published by the loop rather than derived from
    /// <see cref="Resources"/>, because the loop is the only thing that knows which of the
    /// not-in-sync statuses are excused by an open supervision window (§2.10 clause 2) — and that
    /// exclusion is the whole of "a supervised restart is not drift and never triggers this rule".
    /// </para>
    /// <para>
    /// It is a separate field from <see cref="Condition"/> and not a rung on it. The ladder is
    /// about what the <i>Fleet Manager</i> has said; this is about what the frame has observed of
    /// itself, and a frame can be authoritatively adopted and locally drifted at the same instant.
    /// </para>
    /// </remarks>
    public bool Drifted { get; init; }

    /// <summary>§2.10's annotation, when supervision has acted.</summary>
    public SupervisionAnnotation? Supervision { get; init; }

    /// <summary>
    /// Who to contact about this fleet, as the Fleet Manager last said (§2.7 item 8, decision 71).
    /// </summary>
    /// <remarks>
    /// Seeded from <see cref="AgentMemory"/> at startup and replaced by each push, so the value on
    /// the screen never depends on a connection being live at the moment somebody reads it. Null
    /// means the Fleet Manager has never said, which <see cref="ReconcileVoice.ContactLine"/>
    /// renders as its own sentence rather than as silence.
    /// </remarks>
    public OperatorContact? Contact { get; init; }

    /// <summary>Whether the product app may run (§2.6).</summary>
    public bool ProductRuns => Condition.ProductRuns && !Drifted;
}
