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

/// <summary>How one package on one frame stands against the reviewed baseline.</summary>
/// <remarks>
/// <para>
/// Four of the five values are worth having on screen and one is not: <c>same</c> covers nine
/// hundred packages on a healthy frame and is carried as a count rather than as nine hundred
/// rows. What travels here is the disagreement.
/// </para>
/// <para>
/// <c>ahead</c> is deliberately not styled as a problem anywhere it is rendered. A frame ahead of
/// the baseline has received the security updates it is supposed to receive; the reason it is
/// shown at all is that an operator wants to know <i>which</i> frame got them and which did not.
/// </para>
/// </remarks>
public sealed record PackageDeltaView
{
    /// <summary>The package name, as dpkg spells it.</summary>
    public required string Package { get; init; }

    /// <summary>One of <c>ahead</c>, <c>behind</c>, <c>missing</c>, <c>extra</c>, <c>same</c>.</summary>
    public required string Status { get; init; }

    /// <summary>The reviewed version, absent when the package is not in the baseline.</summary>
    public string? Baseline { get; init; }

    /// <summary>The version this frame has, absent when it does not have the package.</summary>
    public string? Installed { get; init; }
}

/// <summary>How one frame's package set stands, in five numbers.</summary>
public sealed record PackageSummaryView
{
    /// <summary>The frame.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Its operator-assigned name, when it has one.</summary>
    public string? Name { get; init; }

    /// <summary>Whether a socket is open right now. Presence <i>is</i> the socket (§3.5).</summary>
    public required bool Online { get; init; }

    /// <summary>When the frame last read its own dpkg database.</summary>
    public required DateTimeOffset ObservedUtc { get; init; }

    /// <summary>The key its set is stored under. Two frames with the same one are identical.</summary>
    public required string ContentHash { get; init; }

    /// <summary>How many packages it has installed.</summary>
    public required int Installed { get; init; }

    /// <summary>How many are newer than the reviewed baseline. Expected, never a fault.</summary>
    public required int Ahead { get; init; }

    /// <summary>How many are older than it. This is the direction that means something is wrong.</summary>
    public required int Behind { get; init; }

    /// <summary>How many baseline packages this frame does not have at all.</summary>
    public required int Missing { get; init; }

    /// <summary>How many it has that the baseline never named.</summary>
    public required int Extra { get; init; }
}

/// <summary>One version of one package, and which frames are on it.</summary>
public sealed record PackageVersionGroupView
{
    /// <summary>The version, or absent when this group is the frames that do not have it.</summary>
    public string? Version { get; init; }

    /// <summary>The frames on that version, by id.</summary>
    public required IReadOnlyList<string> DeviceIds { get; init; }
}

/// <summary>One package the fleet does not agree on.</summary>
/// <remarks>
/// The screen the operator actually asked for. A homogeneous fleet produces an empty list, which
/// is the most informative possible answer and takes one line to render; a fleet that has drifted
/// produces one row per package that differs, with the frames grouped under the version they are
/// on. Neither shape is ever a table of nine hundred rows.
/// </remarks>
public sealed record PackageDisagreementView
{
    /// <summary>The package the frames differ on.</summary>
    public required string Package { get; init; }

    /// <summary>The reviewed version, when the baseline names this package.</summary>
    public string? Baseline { get; init; }

    /// <summary>Every distinct version seen across the fleet, newest first, with its frames.</summary>
    public required IReadOnlyList<PackageVersionGroupView> Versions { get; init; }
}

/// <summary>The fleet-wide package comparison.</summary>
public sealed record FleetPackagesResponse
{
    /// <summary>One row per frame that has ever reported, by name.</summary>
    public required IReadOnlyList<PackageSummaryView> Devices { get; init; }

    /// <summary>How many packages every reporting frame has at the same version.</summary>
    public required int Agreed { get; init; }

    /// <summary>How many packages the fleet disagrees on, before any cap is applied.</summary>
    public required int DisagreementTotal { get; init; }

    /// <summary>Those disagreements, capped.</summary>
    public required IReadOnlyList<PackageDisagreementView> Disagreements { get; init; }

    /// <summary>How many distinct package sets exist across the fleet. One means total agreement.</summary>
    public required int DistinctSets { get; init; }

    /// <summary>How many packages the reviewed baseline names.</summary>
    public required int BaselineCount { get; init; }

    /// <summary>When a person last reviewed that baseline (§7.1).</summary>
    public required DateTimeOffset BaselineReviewedUtc { get; init; }
}

/// <summary>One package that moved on one frame between two reports.</summary>
public sealed record PackageChangeView
{
    /// <summary>The package.</summary>
    public required string Package { get; init; }

    /// <summary>One of <c>installed</c>, <c>removed</c>, <c>upgraded</c>, <c>downgraded</c>.</summary>
    public required string Change { get; init; }

    /// <summary>The version before, absent for a newly installed package.</summary>
    public string? From { get; init; }

    /// <summary>The version after, absent for a removed one.</summary>
    public string? To { get; init; }
}

/// <summary>Everything that moved on one frame at one moment.</summary>
/// <remarks>
/// One of these per report the frame sent, and the agent only reports on change — so each of
/// these is a real event with at least one package in it, not a heartbeat with an empty diff.
/// </remarks>
public sealed record PackageChangeSetView
{
    /// <summary>When the frame observed the new set.</summary>
    public required DateTimeOffset ObservedUtc { get; init; }

    /// <summary>How many packages moved, before any cap is applied.</summary>
    public required int Total { get; init; }

    /// <summary>The moves themselves, capped.</summary>
    public required IReadOnlyList<PackageChangeView> Changes { get; init; }
}

/// <summary>One frame's packages: how it stands, and what changed on it recently.</summary>
public sealed record DevicePackagesResponse
{
    /// <summary>The frame this is about.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Whether a socket is open right now.</summary>
    public required bool Online { get; init; }

    /// <summary>
    /// The five numbers, or null when this frame has never reported an inventory.
    /// </summary>
    /// <remarks>
    /// Null rather than a zeroed summary, for the reason <see cref="DeviceReconcileResponse"/>
    /// gives about a null report: "adopted a second ago and has not reported" is a real state the
    /// screen has to render, and a summary full of zeros claims a frame with no packages.
    /// </remarks>
    public PackageSummaryView? Summary { get; init; }

    /// <summary>How many packages the frame said it had, which exceeds the reported set only when
    /// the set was too large for one message.</summary>
    public int ObservedCount { get; init; }

    /// <summary>How many packages differ from the baseline in any direction.</summary>
    public int DriftTotal { get; init; }

    /// <summary>Those differences, capped, worst direction first.</summary>
    public required IReadOnlyList<PackageDeltaView> Drift { get; init; }

    /// <summary>Recent changes on this frame, newest first.</summary>
    public required IReadOnlyList<PackageChangeSetView> Recent { get; init; }

    /// <summary>How many packages the reviewed baseline names.</summary>
    public required int BaselineCount { get; init; }

    /// <summary>When a person last reviewed it (§7.1).</summary>
    public required DateTimeOffset BaselineReviewedUtc { get; init; }
}

/// <summary>What the operator asks for when generating an image (§3.9).</summary>
/// <remarks>
/// Two URLs, and that is the entire request. There is nothing here to name a device, because a
/// generated image is generic — one image serves the whole fleet, and identity is the keypair
/// each frame generates on its own first boot (§3.3, decision 17). Anyone extending this record
/// with a token, a key or a device id is undoing enrollment, not adding convenience.
/// </remarks>
public sealed record ImageRequest
{
    /// <summary>The public URL frames built from this image will dial.</summary>
    public required string ControlUrl { get; init; }

    /// <summary>An optional LAN address, tried after the public one (§4.3).</summary>
    public string? LanUrl { get; init; }
}

/// <summary>The pinned upstream base image, as the console shows it (§7.1).</summary>
public sealed record BaseImageView
{
    /// <summary>Upstream's release date.</summary>
    public required string Release { get; init; }

    /// <summary>The decompressed image's filename, which is what must be on disk.</summary>
    public required string FileName { get; init; }

    /// <summary>Where the published archive lives.</summary>
    public required string ArchiveUrl { get; init; }

    /// <summary>The digest published beside that archive.</summary>
    public required string ArchiveSha256 { get; init; }

    /// <summary>The digest of the decompressed image, which is what is verified before a build.</summary>
    public required string ImageSha256 { get; init; }

    /// <summary>Length of the decompressed image.</summary>
    public required long ImageSizeBytes { get; init; }

    /// <summary>When a human last reviewed this pin against upstream.</summary>
    public required DateTimeOffset ReviewedUtc { get; init; }

    /// <summary>Where the image is expected on disk.</summary>
    public required string Directory { get; init; }

    /// <summary>The one command that puts it there.</summary>
    public required string PreparationCommand { get; init; }

    /// <summary>
    /// Null when a file of the right name and length is present, otherwise what is wrong.
    /// </summary>
    /// <remarks>
    /// Name and length only. The digest is checked when a build starts, because hashing 2.8 GB
    /// is not something a status route a console polls every few seconds may do.
    /// </remarks>
    public string? Problem { get; init; }
}

/// <summary>Everything the console renders for image generation (§3.9).</summary>
public sealed record ImageStatusResponse
{
    /// <summary>The pinned base image and whether it is on disk.</summary>
    public required BaseImageView Base { get; init; }

    /// <summary>One of <c>Idle</c>, <c>Running</c>, <c>Succeeded</c>, <c>Failed</c>.</summary>
    public required string State { get; init; }

    /// <summary>The step under way, or the last one attempted.</summary>
    public string? Step { get; init; }

    /// <summary>When the current or last build started.</summary>
    public DateTimeOffset? StartedUtc { get; init; }

    /// <summary>When it finished.</summary>
    public DateTimeOffset? CompletedUtc { get; init; }

    /// <summary>Why it stopped, when it did.</summary>
    public string? Problem { get; init; }

    /// <summary>The machine-readable verdict of the last build.</summary>
    public string? Result { get; init; }

    /// <summary>The image on disk, when there is one.</summary>
    public Imaging.ImageArtifact? Artifact { get; init; }

    /// <summary>Whether a file is at the artifact path right now.</summary>
    public required bool ArtifactAvailable { get; init; }
}

/// <summary>Everything the console renders about the call server (§3.7).</summary>
/// <remarks>
/// <b>The API secret is deliberately absent and must stay absent.</b> §3.7 makes the Fleet
/// Manager the owner of that secret precisely so nothing else holds it, and a browser is
/// something else. The <i>key</i> is here because it is an identifier rather than a credential —
/// it travels in the clear in every token's <c>iss</c> claim — and showing it is what lets an
/// operator confirm at a glance that a rotation happened.
/// </remarks>
public sealed record LiveKitStatusResponse
{
    /// <summary>One of <c>bundled</c>, <c>external</c>, <c>disabled</c>.</summary>
    public required string Mode { get; init; }

    /// <summary>The pinned LiveKit version this Fleet Manager carries.</summary>
    public required string Version { get; init; }

    /// <summary>Whether a frame issued a token right now could actually place a call.</summary>
    public required bool Ready { get; init; }

    /// <summary>What the bundled path is doing, or the last thing it did.</summary>
    public required string Step { get; init; }

    /// <summary>Everything an operator has to fix, in plain sentences. Empty means nothing.</summary>
    public required IReadOnlyList<string> Problems { get; init; }

    /// <summary>The signalling address frames are issued, or empty when none is configured.</summary>
    public required string Url { get; init; }

    /// <summary>The HTTP and WebSocket signalling port. The half that can ride a reverse proxy.</summary>
    public required int SignalPort { get; init; }

    /// <summary>The TCP media fallback port. Published directly; a proxy cannot carry it.</summary>
    public required int TcpMediaPort { get; init; }

    /// <summary>First UDP port of the media range.</summary>
    public required int UdpPortStart { get; init; }

    /// <summary>Last UDP port of the media range.</summary>
    public required int UdpPortEnd { get; init; }

    /// <summary>How long a freshly minted token lasts, in days.</summary>
    public required int TokenLifetimeDays { get; init; }

    /// <summary>When a human last reviewed the pinned LiveKit version against upstream (§7.1).</summary>
    public required DateTimeOffset ReviewedUtc { get; init; }

    /// <summary>The API key. An identifier, never the secret.</summary>
    public string? ApiKey { get; init; }

    /// <summary>When the signing secret was generated, which is what a rotation moves.</summary>
    public DateTimeOffset? SecretIssuedUtc { get; init; }

    /// <summary>The supervised child, when this deployment has one.</summary>
    public LiveKit.LiveKitProcessState? Process { get; init; }
}

/// <summary>What happened to one frame's call token.</summary>
public sealed record CallTokenResponse
{
    /// <summary>The frame.</summary>
    public required string DeviceId { get; init; }

    /// <summary>One of <c>issued</c>, <c>already-current</c>, <c>not-configured</c>, <c>not-adopted</c>.</summary>
    public required string Outcome { get; init; }

    /// <summary>The participant identity the frame is issued.</summary>
    public string? Identity { get; init; }

    /// <summary>The room the token is good for.</summary>
    public string? Room { get; init; }

    /// <summary>When the token the frame now holds stops working.</summary>
    public DateTimeOffset? ExpiresUtc { get; init; }

    /// <summary>Why a token was minted, or why one was not.</summary>
    public required string Reason { get; init; }
}

/// <summary>What a secret rotation did.</summary>
public sealed record LiveKitRotateResponse
{
    /// <summary>How many frames were issued a token signed with the new secret.</summary>
    public required int Issued { get; init; }

    /// <summary>The API key the new secret belongs to.</summary>
    public string? ApiKey { get; init; }

    /// <summary>When the new secret was generated.</summary>
    public required DateTimeOffset RotatedUtc { get; init; }
}

/// <summary>One alert condition that is open right now (§3.5).</summary>
public sealed record AlertView
{
    /// <summary>Stable identity of the condition.</summary>
    public required string Key { get; init; }

    /// <summary>One of <c>device-offline</c>, <c>call-token-expiring</c>, <c>call-server-down</c>,
    /// <c>device-halted</c>.</summary>
    public required string Kind { get; init; }

    /// <summary><c>warning</c> or <c>critical</c>.</summary>
    public required string Severity { get; init; }

    /// <summary>One line, fit to be a notification title.</summary>
    public required string Subject { get; init; }

    /// <summary>The detail behind it, in plain sentences.</summary>
    public required string Detail { get; init; }

    /// <summary>The frame this is about, when it is about one.</summary>
    public string? DeviceId { get; init; }

    /// <summary>That frame's name, when it has one.</summary>
    public string? DeviceName { get; init; }

    /// <summary>When this Fleet Manager first observed the condition.</summary>
    public required DateTimeOffset OpenedUtc { get; init; }

    /// <summary>When it was successfully delivered, or null while delivery is still failing.</summary>
    public DateTimeOffset? NotifiedUtc { get; init; }
}

/// <summary>Everything the console renders about alerting.</summary>
/// <remarks>
/// <b>The bearer token is deliberately absent and must stay absent</b>, for the same reason
/// <c>LiveKitStatusResponse</c> omits the API secret: a browser is not a place a credential
/// belongs. The webhook URL is present because an operator diagnosing "why did nothing arrive"
/// needs to see the address this server is actually using, and because a Home Assistant webhook
/// URL is already reachable by anything on the household network.
/// </remarks>
public sealed record AlertsResponse
{
    /// <summary>The open conditions, oldest first.</summary>
    public required IReadOnlyList<AlertView> Alerts { get; init; }

    /// <summary>Whether notifications reach anywhere other than this server's log.</summary>
    public required bool DeliveryConfigured { get; init; }

    /// <summary>Where notifications are POSTed, or null for log-only.</summary>
    public string? WebhookUrl { get; init; }

    /// <summary>Anything an operator has to fix about alerting itself.</summary>
    public required IReadOnlyList<string> Problems { get; init; }

    /// <summary>How long a frame may be out of contact before it is alerted on, in minutes.</summary>
    public required int OfflineAfterMinutes { get; init; }

    /// <summary>How much life a call token must have left before it is alerted on, in days.</summary>
    public required int TokenExpiryWithinDays { get; init; }

    /// <summary>How often the rules are evaluated, in minutes.</summary>
    public required int IntervalMinutes { get; init; }
}

/// <summary>A refused request, in a shape the GUI can render.</summary>
public sealed record ApiError
{
    /// <summary>Short machine-readable code.</summary>
    public required string Error { get; init; }

    /// <summary>Sentence fit to show an operator.</summary>
    public string? Detail { get; init; }
}
