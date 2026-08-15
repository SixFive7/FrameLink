namespace FrameLink.Control;

/// <summary>
/// Kinds and channels this milestone layers on top of the frozen envelope.
/// </summary>
/// <remarks>
/// <c>WireEnvelope</c> is frozen forever, and the way a protocol version grows is by adding
/// new <c>Kind</c> values and new payload shapes — never by touching the envelope. These are
/// those additions for M1. They live here rather than in <c>FrameLink.Protocol</c> because
/// that project is the frozen contract and is not modified; promoting a kind into it is a
/// deliberate act once both programs agree on it.
/// </remarks>
public static class ControlWire
{
    /// <summary>Server to agent, on the control channel. Answered with <see cref="KindPong"/>.</summary>
    public const string KindPing = "ping";

    /// <summary>Agent to server, on the control channel.</summary>
    public const string KindPong = "pong";

    /// <summary>Server to agent, on the control channel. Carries <see cref="SettingsPush"/>.</summary>
    public const string KindSettings = "settings";
}

/// <summary>
/// Server-to-agent liveness probe (§3.5).
/// </summary>
/// <remarks>
/// An application-level ping rather than a WebSocket control frame, because the thing that
/// has to be observable is the <i>answer</i>. A pulled plug leaves a half-open TCP connection
/// that accepts writes forever; only a reply within a deadline proves the frame is still
/// there, and only an application-level exchange gives the deadline somewhere to live.
/// </remarks>
public sealed record AgentPing
{
    /// <summary>Monotonic per-connection counter, echoed back in the pong.</summary>
    public required long Sequence { get; init; }

    /// <summary>When the server sent it.</summary>
    public required DateTimeOffset SentUtc { get; init; }
}

/// <summary>Agent-to-server answer to <see cref="AgentPing"/>.</summary>
public sealed record AgentPong
{
    /// <summary>The sequence number from the ping being answered.</summary>
    public required long Sequence { get; init; }
}

/// <summary>
/// The effective settings pushed to an adopted device on connect and after any change (§3.4).
/// </summary>
/// <remarks>
/// Only ever sent to a device whose handshake answered <c>ok</c>. A pending device receives
/// nothing (§3.3), and configuration is the largest part of that nothing.
/// </remarks>
public sealed record SettingsPush
{
    /// <summary>Device the values were resolved for.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Settings revision, so the agent can ignore a repeat of what it already has.</summary>
    public required long Revision { get; init; }

    /// <summary>Fleet defaults with per-device overrides applied.</summary>
    public required IReadOnlyDictionary<string, string> Values { get; init; }
}

/// <summary>What the GUI needs to render the first-run state (§3.2).</summary>
public sealed record SetupStatus
{
    /// <summary>False when no usable operator password is configured.</summary>
    public required bool Configured { get; init; }

    /// <summary>The environment variable that carries the password, named verbatim.</summary>
    public required string Variable { get; init; }

    /// <summary>Why the instance is unconfigured, or null.</summary>
    public string? Problem { get; init; }

    /// <summary>A copyable Docker Compose fragment that fixes it.</summary>
    public string? ComposeExample { get; init; }
}

/// <summary>Operator login body.</summary>
public sealed record LoginRequest
{
    /// <summary>The password from the environment variable.</summary>
    public required string Password { get; init; }
}

/// <summary>Operator login result.</summary>
public sealed record LoginResponse
{
    /// <summary>Session token. Also set as an HttpOnly cookie.</summary>
    public required string Token { get; init; }

    /// <summary>When the session stops working.</summary>
    public required DateTimeOffset ExpiresUtc { get; init; }
}

/// <summary>One row of the device list.</summary>
public sealed record DeviceView
{
    /// <summary>Public-key fingerprint, shown on the frame for bench matching.</summary>
    public required string DeviceId { get; init; }

    /// <summary>One of <c>pending</c>, <c>adopted</c>, <c>blocked</c>.</summary>
    public required string State { get; init; }

    /// <summary>Whether a socket is currently open. Presence <i>is</i> the socket (§3.5).</summary>
    public required bool Online { get; init; }

    /// <summary>Operator-assigned name, once adopted.</summary>
    public string? Name { get; init; }

    /// <summary>Board serial as claimed by the agent.</summary>
    public string? HardwareSerial { get; init; }

    /// <summary>Agent build version from the last proven handshake.</summary>
    public string? AgentVersion { get; init; }

    /// <summary>The agent's free-text self-report, verbatim.</summary>
    public string? AgentStatus { get; init; }

    /// <summary>Protocol version the agent last claimed.</summary>
    public int? ProtocolVersion { get; init; }

    /// <summary>Whether that protocol version is the one this server speaks.</summary>
    public required bool ProtocolCompatible { get; init; }

    /// <summary>First proven contact.</summary>
    public required DateTimeOffset FirstSeenUtc { get; init; }

    /// <summary>Most recent proven contact. Reads as "offline since" when not online.</summary>
    public required DateTimeOffset LastSeenUtc { get; init; }
}

/// <summary>The device list.</summary>
public sealed record DeviceListResponse
{
    /// <summary>Devices, most recently seen first.</summary>
    public required IReadOnlyList<DeviceView> Devices { get; init; }

    /// <summary>Whether blocked devices were included (§3.3's "show blocked" toggle).</summary>
    public required bool IncludeBlocked { get; init; }
}

/// <summary>Adoption body.</summary>
public sealed record AdoptRequest
{
    /// <summary>Operator-assigned display name. Optional.</summary>
    public string? Name { get; init; }
}

/// <summary>Body of a settings write.</summary>
public sealed record SettingValueRequest
{
    /// <summary>The value to store. Opaque to the Fleet Manager (§3.4).</summary>
    public required string Value { get; init; }
}

/// <summary>The fleet defaults.</summary>
public sealed record FleetSettingsResponse
{
    /// <summary>Current settings revision.</summary>
    public required long Revision { get; init; }

    /// <summary>Fleet-wide default values.</summary>
    public required IReadOnlyDictionary<string, string> Values { get; init; }
}

/// <summary>Fleet defaults, per-device overrides and the effective result, side by side.</summary>
public sealed record DeviceSettingsResponse
{
    /// <summary>Device the view is for.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Current settings revision.</summary>
    public required long Revision { get; init; }

    /// <summary>Fleet-wide defaults.</summary>
    public required IReadOnlyDictionary<string, string> FleetDefaults { get; init; }

    /// <summary>Values overridden for this device.</summary>
    public required IReadOnlyDictionary<string, string> Overrides { get; init; }

    /// <summary>What the device actually receives. Empty unless the device is adopted.</summary>
    public required IReadOnlyDictionary<string, string> Effective { get; init; }
}

/// <summary>A refused request, in a shape the GUI can render.</summary>
public sealed record ApiError
{
    /// <summary>Short machine-readable code.</summary>
    public required string Error { get; init; }

    /// <summary>Sentence fit to show an operator.</summary>
    public string? Detail { get; init; }
}
