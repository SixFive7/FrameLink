using FrameLink.Agent.Reconcile;

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

    /// <summary>Whether the product app may run (§2.6).</summary>
    public bool ProductRuns => Condition.ProductRuns;
}
