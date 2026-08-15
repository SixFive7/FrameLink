using System.Text.Json;

namespace FrameLink.Protocol;

/// <summary>
/// The single outer envelope carrying every message in both directions, on the
/// handshake and on all logical channels alike.
/// </summary>
/// <remarks>
/// <para>
/// <b>This shape is frozen forever</b> (version2.md §4.2). No member is ever added,
/// removed, renamed or retyped, in any protocol version, for any reason.
/// </para>
/// <para>
/// The freeze is what makes incompatibility <i>legible</i>. A hopelessly outdated agent
/// — one whose self-update has been failing for a month — can still parse this envelope,
/// still say who it is, and still report why it is stuck. Evolving the envelope would
/// turn that into a silent dead socket, which is the one failure mode the whole design
/// is built to avoid.
/// </para>
/// <para>
/// Protocol versions evolve by adding new <see cref="Kind"/> values and new
/// <see cref="Payload"/> shapes. Never by touching this type.
/// </para>
/// </remarks>
public sealed record WireEnvelope
{
    /// <summary>Frozen. Always <see cref="ProtocolConstants.Magic"/>.</summary>
    /// <remarks>
    /// Present so that a wrong endpoint — a captive portal, a stray proxy, an unrelated
    /// WebSocket service — is rejected as "not a FrameLink server" rather than as a
    /// confusing deserialisation failure.
    /// </remarks>
    public required string Magic { get; init; }

    /// <summary>Frozen. The message discriminator, e.g. <c>hello</c>, <c>result</c>.</summary>
    /// <remarks>A string rather than an enum: an unknown value from a newer peer must be
    /// reportable, not a deserialisation exception.</remarks>
    public required string Kind { get; init; }

    /// <summary>Frozen. Logical channel, or <see langword="null"/> for handshake traffic.</summary>
    /// <remarks>One of <see cref="ProtocolConstants.ChannelTelemetry"/>,
    /// <see cref="ProtocolConstants.ChannelEvents"/>,
    /// <see cref="ProtocolConstants.ChannelControl"/>,
    /// <see cref="ProtocolConstants.ChannelShell"/>.</remarks>
    public string? Channel { get; init; }

    /// <summary>Frozen. Correlates a reply with its request, or <see langword="null"/>.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Frozen. The message body, interpreted according to <see cref="Kind"/>.</summary>
    public JsonElement Payload { get; init; }
}

/// <summary>Wire constants shared by both programs.</summary>
public static class ProtocolConstants
{
    /// <summary>Frozen. Identifies the wire format itself, independent of version.</summary>
    public const string Magic = "framelink";

    /// <summary>
    /// The negotiated protocol version. Matching is strict (version2.md §4.2): a mismatch
    /// triggers an immediate agent update rather than a compatibility shim, so the agent
    /// never implements two dialects.
    /// </summary>
    public const int Version = 1;

    /// <summary>Domain separator for handshake signatures; prevents cross-protocol replay.</summary>
    public const string SignatureContext = "framelink-handshake-v1";

    /// <summary>Loop state, per-resource status, counts. Agent to server.</summary>
    public const string ChannelTelemetry = "telemetry";

    /// <summary>Drift, escalation, boot. Agent to server.</summary>
    public const string ChannelEvents = "events";

    /// <summary>Reconcile now, retry resource, maintenance mode, open shell. Server to agent.</summary>
    public const string ChannelControl = "control";

    /// <summary>Only live while a remote shell session is open (version2.md §3.6).</summary>
    public const string ChannelShell = "shell";
}
