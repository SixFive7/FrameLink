namespace FrameLink.Agent.Discovery;

/// <summary>
/// §4.3's persisted <b>endpoint list</b> — public URL first, optional LAN address second,
/// tried in order.
/// </summary>
/// <remarks>
/// <para>
/// The ordering is the entire reason this is a list rather than a URL. A frame built on the
/// operator's bench and then shipped to another household must keep working: the public URL
/// reaches the Fleet Manager from anywhere, so it leads. The LAN address is the fallback that
/// keeps a frame in the operator's own house working when hairpin NAT does not.
/// </para>
/// <para>
/// Once written this is never recomputed. §4.3 is "find a candidate endpoint → enroll →
/// persist → <b>never rediscover</b>", so a frame that has been told where it belongs cannot
/// later be talked into belonging somewhere else by whatever is shouting on the local network.
/// </para>
/// </remarks>
public sealed record ControlEndpoints
{
    /// <summary>The endpoints, most preferred first.</summary>
    public required IReadOnlyList<Uri> Endpoints { get; init; }

    /// <summary>Which candidate source produced them.</summary>
    public required string DiscoveredBy { get; init; }

    /// <summary>When they were first resolved.</summary>
    public required DateTimeOffset DiscoveredAt { get; init; }
}
