namespace FrameLink.Control.Storage;

/// <summary>Where a device sits in the UniFi-style adoption flow of §3.3.</summary>
public enum DeviceState
{
    /// <summary>Appeared by pointing at this Fleet Manager. Receives nothing until adopted.</summary>
    Pending = 0,

    /// <summary>Bound to its keypair by the operator. The only state that receives configuration.</summary>
    Adopted = 1,

    /// <summary>Refused by the operator. Hidden from the list by default, never deleted.</summary>
    Blocked = 2,
}

/// <summary>How an adoption request was answered.</summary>
public enum DeviceAdoptionResult
{
    /// <summary>The device is now adopted, under the name that was supplied.</summary>
    Adopted = 0,

    /// <summary>This Fleet Manager has never met that device.</summary>
    Unknown = 1,

    /// <summary>The device is blocked, and blocked devices are unblocked before they are adopted.</summary>
    Blocked = 2,
}

/// <summary>
/// The outcome of an adoption attempt.
/// </summary>
/// <remarks>
/// A nullable record was not enough: "no such device" and "that one is blocked" are different
/// answers an operator needs told apart, and collapsing them into null made the second one
/// impossible to say.
/// </remarks>
public sealed record DeviceAdoption
{
    /// <summary>What happened.</summary>
    public required DeviceAdoptionResult Result { get; init; }

    /// <summary>The adopted row. Present only when <see cref="Result"/> is
    /// <see cref="DeviceAdoptionResult.Adopted"/>.</summary>
    public DeviceRecord? Record { get; init; }
}

/// <summary>
/// One row of the device table: everything the Fleet Manager knows about a frame that is
/// not a setting.
/// </summary>
/// <remarks>
/// <see cref="DeviceId"/> is the fingerprint of <see cref="PublicKey"/> and is never
/// assigned by the server (§3.3), which is what makes disaster recovery re-adoption: a
/// rebuilt server sees every configured agent reappear under the identity it already had.
/// </remarks>
public sealed record DeviceRecord
{
    /// <summary>Public-key fingerprint. The immutable identity.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Base64 SubjectPublicKeyInfo the fingerprint derives from.</summary>
    public required string PublicKey { get; init; }

    /// <summary>Adoption state.</summary>
    public required DeviceState State { get; init; }

    /// <summary>Operator-assigned name. Null until adoption assigns one.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Board serial as claimed by the agent, for bench matching (§3.3).</summary>
    public string? HardwareSerial { get; init; }

    /// <summary>Last agent build version seen on a proven handshake.</summary>
    public string? AgentVersion { get; init; }

    /// <summary>Last free-text self-report, surfaced verbatim so a broken agent stays legible.</summary>
    public string? AgentStatus { get; init; }

    /// <summary>Last protocol version claimed, so an incompatible device still renders a row.</summary>
    public int? ProtocolVersion { get; init; }

    /// <summary>When this device first proved its identity to this Fleet Manager.</summary>
    public required DateTimeOffset FirstSeenUtc { get; init; }

    /// <summary>When it last did. Doubles as the auto-expiry clock for pending rows.</summary>
    public required DateTimeOffset LastSeenUtc { get; init; }

    /// <summary>When <see cref="State"/> last changed, so "offline since" style questions work.</summary>
    public required DateTimeOffset StateChangedUtc { get; init; }

    /// <summary>Address the last proven handshake arrived from.</summary>
    public string? LastRemoteAddress { get; init; }
}

/// <summary>
/// A single proven contact from a device, folded into the device table.
/// </summary>
/// <remarks>
/// Only ever constructed after <c>DeviceIdentity.VerifyProof</c> has succeeded. An
/// unauthenticated hello is a claim (see <c>HandshakeHello</c>) and must not be allowed to
/// create or mutate a row, or an attacker could rewrite the reported version and status of
/// somebody else's adopted frame.
/// </remarks>
public sealed record DeviceContact
{
    /// <summary>Proven device identity.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Base64 SubjectPublicKeyInfo the identity was re-derived from.</summary>
    public required string PublicKey { get; init; }

    /// <summary>Protocol version the agent claimed.</summary>
    public required int ProtocolVersion { get; init; }

    /// <summary>Agent build version.</summary>
    public string? AgentVersion { get; init; }

    /// <summary>Agent self-report.</summary>
    public string? AgentStatus { get; init; }

    /// <summary>Board serial.</summary>
    public string? HardwareSerial { get; init; }

    /// <summary>Source address of the connection.</summary>
    public string? RemoteAddress { get; init; }
}
