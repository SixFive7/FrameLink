using FrameLink.Protocol;

namespace FrameLink.Control;

// The shapes the operator's browser exchanges with this server.
//
// Everything in this file is server-to-browser and deliberately NOT frozen: the GUI ships in
// the same container as the server, so both halves of these contracts move together and a
// field can be added, dropped or reshaped in one commit. The wire types shared with the AGENT
// are the opposite case — they live in FrameLink.Protocol, where the two programs meet and
// nothing may move under a frame that cannot be updated. Nothing device-facing belongs here.

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

    /// <summary>
    /// The same self-report, classified into <see cref="AgentHealth"/>'s coarse vocabulary.
    /// </summary>
    /// <remarks>
    /// Beside the free text, never instead of it. §3.5's presence ladder needs to know whether
    /// a connected frame is healthy, and reading that out of a field the protocol documents as
    /// free text is a guess — one the browser used to make, and got wrong for every real agent.
    /// The classification happens once, here, against the vocabulary both programs share.
    /// </remarks>
    public required string Health { get; init; }

    /// <summary>Protocol version the agent last claimed.</summary>
    public int? ProtocolVersion { get; init; }

    /// <summary>Whether that protocol version is the one this server speaks.</summary>
    public required bool ProtocolCompatible { get; init; }

    /// <summary>First proven contact.</summary>
    public required DateTimeOffset FirstSeenUtc { get; init; }

    /// <summary>Most recent proven contact. Reads as "offline since" when not online.</summary>
    public required DateTimeOffset LastSeenUtc { get; init; }

    /// <summary>
    /// When <see cref="State"/> last changed: "adopted on", "blocked since".
    /// </summary>
    /// <remarks>
    /// Also what makes §3.5's <c>Never enrolled</c> rung reachable in its useful sense. The
    /// literal reading — no row — cannot be rendered, because a row only exists after a proven
    /// handshake. The reading an operator actually needs is <i>adopted, and not seen since</i>,
    /// which is exactly <see cref="LastSeenUtc"/> earlier than this.
    /// </remarks>
    public required DateTimeOffset StateChangedUtc { get; init; }

    /// <summary>Address the last proven handshake arrived from.</summary>
    /// <remarks>
    /// Bench-matching evidence of the same kind as <see cref="HardwareSerial"/>: it answers
    /// "is this the frame on my desk or the one in my mother's living room" without anyone
    /// having to walk to either.
    /// </remarks>
    public string? LastRemoteAddress { get; init; }
}

/// <summary>The device list.</summary>
public sealed record DeviceListResponse
{
    /// <summary>Devices, most recently seen first.</summary>
    public required IReadOnlyList<DeviceView> Devices { get; init; }

    /// <summary>Whether blocked devices were included (§3.3's "show blocked" toggle).</summary>
    public required bool IncludeBlocked { get; init; }
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

/// <summary>
/// A device's live reconciliation state (§3.5), verbatim as the frame reported it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The GUI's live reconciliation screen is out of scope for M2</b> — a later workstream owns
/// it. What exists now is the data and its shape: the report is the agent's own
/// <c>ReconcileReport</c> from <c>FrameLink.Protocol</c>, handed back unchanged, so whoever
/// builds that screen renders exactly what the frame said rather than a server's paraphrase of
/// it. The only thing added here is <see cref="Online"/>, which the server knows and the frame
/// does not.
/// </para>
/// <para>
/// <see cref="Report"/> is null for a device that has never sent one — a pending frame, or one
/// adopted a second ago. That is a real state and the screen has to render it, so it is a null
/// rather than an empty report pretending to be an observation.
/// </para>
/// </remarks>
public sealed record DeviceReconcileResponse
{
    /// <summary>The device this is about.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Whether a socket is open right now. Presence <i>is</i> the socket (§3.5).</summary>
    public required bool Online { get; init; }

    /// <summary>The latest report, or null if the frame has never sent one.</summary>
    public ReconcileReport? Report { get; init; }
}

/// <summary>A device's recent events (§4.1's <c>events</c> channel), newest first.</summary>
public sealed record DeviceEventsResponse
{
    /// <summary>The device this is about.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Events, newest first, capped by the request's limit.</summary>
    public required IReadOnlyList<DeviceEvent> Events { get; init; }
}

/// <summary>A refused request, in a shape the GUI can render.</summary>
public sealed record ApiError
{
    /// <summary>Short machine-readable code.</summary>
    public required string Error { get; init; }

    /// <summary>Sentence fit to show an operator.</summary>
    public string? Detail { get; init; }
}
