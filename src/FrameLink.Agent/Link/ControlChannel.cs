namespace FrameLink.Agent.Link;

/// <summary>
/// The kinds the Fleet Manager layers on the <c>control</c> channel, as this agent knows them.
/// </summary>
/// <remarks>
/// <para>
/// A deliberate mirror of the Fleet Manager's own <c>ControlWire</c>. Neither program may own
/// the definition: <c>FrameLink.Protocol</c> is frozen (§4.2) and holds only the envelope and
/// the handshake, and the agent must not reference the server assembly — a frame would then be
/// carrying the Fleet Manager's SQLite and ASP.NET dependencies inside a binary §2.1 requires
/// to be one self-contained ELF.
/// </para>
/// <para>
/// So the two sides agree by construction only for as long as something checks. That something
/// is <c>AgentControlIntegrationTests</c>, which runs this agent's real link against the real
/// server pipeline in one process. Unit tests on either side of a mirrored contract are exactly
/// the arrangement that lets both pass while the wire is broken, which is how the agent came to
/// ignore <c>ping</c> entirely and every real connection died on the server's 60-second
/// missed-pong deadline.
/// </para>
/// </remarks>
public static class ControlChannel
{
    /// <summary>Server to agent. Must be answered with <see cref="KindPong"/>.</summary>
    /// <remarks>
    /// Answering is not optional and not best-effort. §3.5 gives the server a missed-pong
    /// deadline precisely because a pulled plug leaves a half-open TCP connection that accepts
    /// writes forever; an agent that stays silent is indistinguishable from that frame and is
    /// disconnected as one.
    /// </remarks>
    public const string KindPing = "ping";

    /// <summary>Agent to server. The answer to <see cref="KindPing"/>.</summary>
    public const string KindPong = "pong";

    /// <summary>Server to agent. Effective settings for an adopted device (§3.4).</summary>
    public const string KindSettings = "settings";

    /// <summary>Property carrying the ping's sequence number.</summary>
    /// <remarks>
    /// The ping payload is read field by field rather than deserialised into a mirrored record.
    /// A newer server that adds a field to its ping must not be answered with silence by an
    /// older agent, and silence is exactly what a strict deserialisation of an unknown shape
    /// would produce here.
    /// </remarks>
    public const string SequenceProperty = "sequence";
}

/// <summary>The agent's answer to a liveness probe.</summary>
public sealed record ControlPong
{
    /// <summary>The sequence number from the ping being answered, echoed back.</summary>
    public required long Sequence { get; init; }
}
