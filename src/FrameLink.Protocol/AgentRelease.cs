namespace FrameLink.Protocol;

/// <summary>
/// What the Fleet Manager publishes about the agent build it serves.
/// </summary>
/// <remarks>
/// <para>
/// Returned from a plain, versionless HTTPS route <b>outside the negotiated protocol</b>
/// (version2.md §4.2) and polled hourly regardless of socket state. That independence is
/// the point: the hourly out-of-band check is the primary convergence mechanism, and the
/// socket handshake merely triggers it sooner (§2.8).
/// </para>
/// <para>
/// Because the agent <i>matches</i> this version rather than taking the greater of the two,
/// reverting the container tag reverts the whole fleet within the hour. Downgrade is a
/// first-class operation, not an error.
/// </para>
/// <para>
/// <b>This shape never changes</b>, for the same reason the handshake envelope never does:
/// it is the one route an agent too old to speak the protocol must still be able to use to
/// repair itself.
/// </para>
/// </remarks>
public sealed record AgentRelease
{
    /// <summary>Frozen. Version string the fleet must converge on, e.g. <c>0.3.1+a1b2c3d</c>.</summary>
    public required string Version { get; init; }

    /// <summary>Frozen. Runtime identifier this entry describes, e.g. <c>linux-arm64</c>.</summary>
    public required string RuntimeIdentifier { get; init; }

    /// <summary>Frozen. Lowercase hex SHA-256 of the binary, verified before the swap (§2.8).</summary>
    public required string Sha256 { get; init; }

    /// <summary>Frozen. Size in bytes, so a truncated download fails before hashing.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Frozen. Absolute or server-relative URL to download the binary from.</summary>
    public required string Url { get; init; }
}
