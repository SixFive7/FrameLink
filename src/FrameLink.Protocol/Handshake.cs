namespace FrameLink.Protocol;

/// <summary>
/// Opening claim of identity, sent by the agent on <b>every</b> connect (version2.md §4.2),
/// not only the first.
/// </summary>
/// <remarks>
/// <b>Frozen forever</b>, along with <see cref="WireEnvelope"/>. This payload is what an
/// agent that cannot be updated still manages to send, so it deliberately carries enough
/// context — version, identity, and a free-text status — for the server to render a useful
/// row for a device it can otherwise no longer talk to.
/// <para>
/// The hello is <i>unauthenticated</i>: it is a claim, not a proof. The server answers with
/// a <see cref="HandshakeChallenge"/> and only a valid <see cref="HandshakeProof"/> binds
/// the connection to the claimed identity.
/// </para>
/// </remarks>
public sealed record HandshakeHello
{
    /// <summary>Frozen. Protocol version the agent speaks. Matched strictly by the server.</summary>
    public required int ProtocolVersion { get; init; }

    /// <summary>Frozen. Informational agent build version, e.g. <c>0.3.1+a1b2c3d</c>.</summary>
    public required string AgentVersion { get; init; }

    /// <summary>Frozen. Fingerprint of <see cref="PublicKey"/>; the device's immutable identity.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Frozen. Base64 SubjectPublicKeyInfo DER of the device's public key.</summary>
    public required string PublicKey { get; init; }

    /// <summary>Frozen. Base64 client nonce, 32 random bytes, fresh per connection.</summary>
    public required string Nonce { get; init; }

    /// <summary>Frozen. Board serial, shown beside the fingerprint for bench matching (§3.3).</summary>
    public string? HardwareSerial { get; init; }

    /// <summary>
    /// Frozen. Free-text self-report, surfaced verbatim in the Fleet Manager.
    /// </summary>
    /// <remarks>
    /// This is the field that makes a broken agent legible: an agent whose self-update has
    /// failed repeatedly says so here, and the operator sees the reason instead of an
    /// unexplained version mismatch.
    /// </remarks>
    public string? AgentStatus { get; init; }
}

/// <summary>Server's challenge. <b>Frozen forever.</b></summary>
public sealed record HandshakeChallenge
{
    /// <summary>Frozen. Base64 server nonce, 32 random bytes, fresh per connection.</summary>
    public required string Nonce { get; init; }
}

/// <summary>
/// Agent's proof of possession of the private key. <b>Frozen forever.</b>
/// </summary>
/// <remarks>
/// The signature covers <see cref="ProtocolConstants.SignatureContext"/>, both nonces and
/// the device id, so a captured proof cannot be replayed against another connection,
/// another device, or another protocol that happens to use the same key.
/// </remarks>
public sealed record HandshakeProof
{
    /// <summary>Frozen. Base64 signature over the canonical challenge bytes.</summary>
    public required string Signature { get; init; }
}

/// <summary>
/// Server's verdict, and the last frozen message of the handshake.
/// </summary>
/// <remarks>
/// Every outcome — including "I do not know you", "I am not set up" and "you are the wrong
/// version" — is an explicit, readable answer. The socket is never simply dropped, because
/// <b>rejection is an answer; silence is not</b> (§2.6): only an authoritative rejection
/// stops the product on a frame, and silence must remain distinguishable from it.
/// </remarks>
public sealed record HandshakeResult
{
    /// <summary>Frozen. One of the <see cref="HandshakeStatus"/> constants.</summary>
    public required string Status { get; init; }

    /// <summary>Frozen. Protocol version the server speaks, always populated.</summary>
    public required int ProtocolVersion { get; init; }

    /// <summary>Frozen. Agent version this server serves; the agent converges on it (§2.8).</summary>
    public string? ServedAgentVersion { get; init; }

    /// <summary>Frozen. Versionless URL to fetch the served agent binary from.</summary>
    public string? UpdateUrl { get; init; }

    /// <summary>Frozen. Operator-assigned display name, once adopted.</summary>
    public string? DeviceName { get; init; }

    /// <summary>Frozen. Human-readable elaboration, rendered on the frame when present.</summary>
    public string? Message { get; init; }
}

/// <summary>
/// The outcomes of a handshake. Frozen string constants rather than an enum, so an unknown
/// value from a newer server is reportable rather than a deserialisation failure.
/// </summary>
/// <remarks>
/// These map onto the device state ladder in version2.md §2.6. Each one is a distinct,
/// explicitly rendered state on the frame — there is no generic "error".
/// </remarks>
public static class HandshakeStatus
{
    /// <summary>Adopted and version-matched. The only status that permits the product to run.</summary>
    public const string Ok = "ok";

    /// <summary>Known, not yet adopted. The frame displays its fingerprint and waits (§3.3).</summary>
    public const string Pending = "pending";

    /// <summary>Explicitly blocked by the operator.</summary>
    public const string Blocked = "blocked";

    /// <summary>The server has no operator password configured, so it can adopt nothing (§3.2).</summary>
    public const string NotConfigured = "not-configured";

    /// <summary>Protocol or agent version differs from what this server serves (§2.8).</summary>
    public const string VersionMismatch = "version-mismatch";

    /// <summary>The proof did not verify against the claimed public key.</summary>
    public const string BadSignature = "bad-signature";

    /// <summary>
    /// The device proved who it is and has spent its own handshake budget for now (§3.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A new <i>value</i>, not a new shape. <see cref="HandshakeResult"/> and every other type
    /// in the frozen set are untouched — §4.2 grows by new kinds and new payload values, and
    /// these statuses are strings rather than an enum precisely so that one a peer has never
    /// heard of is reportable instead of fatal.
    /// </para>
    /// <para>
    /// <b>It is the one status that says nothing about the device's state</b>, and that is why
    /// it exists as its own word instead of being folded into an existing one. Pending, blocked,
    /// not-configured and version-mismatch are all answers about what the frame <i>is</i>; this
    /// one is backpressure about what the server is willing to do this minute, and the frame it
    /// is sent to may well be a perfectly healthy adopted one. An agent must therefore let it
    /// feed the reconnect backoff and must not let it overwrite what it was last authoritatively
    /// told — a green frame that is asked to slow down is still a green frame, and §2.6 forbids
    /// blanking it.
    /// </para>
    /// </remarks>
    public const string RateLimited = "rate-limited";
}
