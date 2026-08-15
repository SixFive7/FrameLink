using System.Globalization;

namespace FrameLink.Protocol;

/// <summary>
/// The §2.3 resource status vocabulary, as the agent writes it into its self-report.
/// </summary>
/// <remarks>
/// <see cref="HandshakeHello.AgentStatus"/> is frozen as <i>free text</i> and stays free text —
/// it is what a hopelessly broken agent uses to say something nobody anticipated, and
/// constraining it would take that away. What these constants add is a convention for the part
/// that <i>can</i> be structured: the leading word. An agent that has something to report
/// writes <c>Degraded(audio.volume, expected 75 observed 40, attempt 3)</c>, and the head of
/// that string is a vocabulary term rather than a sentence somebody happened to phrase that way.
/// </remarks>
public static class AgentResourceStatus
{
    /// <summary>Everything verified.</summary>
    public const string InSync = "InSync";

    /// <summary>A resource is being worked on.</summary>
    public const string Progressing = "Progressing";

    /// <summary>Applied, waiting for the reboot that proves it stuck (§2.4).</summary>
    public const string AwaitingReboot = "AwaitingReboot";

    /// <summary>Attempt budget exhausted; carries the expected-vs-observed delta (§2.5).</summary>
    public const string Degraded = "Degraded";

    /// <summary>Held back by a failed dependency in the DAG (§2.2).</summary>
    public const string Blocked = "Blocked";

    /// <summary>The operator has been notified (§2.5 rung 3).</summary>
    public const string Escalated = "Escalated";

    /// <summary>Given up on, deliberately (§2.5 rung 4).</summary>
    public const string Halted = "Halted";
}

/// <summary>
/// The coarse health of an agent, derived once from its free-text self-report.
/// </summary>
/// <remarks>
/// <para>
/// This exists so that the presence ladder of §3.5 is a <b>fact the server states</b> rather
/// than a guess each consumer makes. The GUI used to classify a device by string-matching
/// <see cref="HandshakeHello.AgentStatus"/> against the §2.3 vocabulary, which made a browser a
/// second consumer of a field the protocol documents as free text and the agent may reword at
/// will — and got it wrong immediately, because a real agent's self-report reads
/// <c>linux-arm64, endpoints resolved by boot file</c> and matched nothing, so every healthy
/// frame in the fleet would have rendered as <i>Online — degraded</i>.
/// </para>
/// <para>
/// The classification lives here rather than in the Fleet Manager because it is a property of
/// the vocabulary, and the vocabulary belongs to both programs: the agent writes the strings
/// with <see cref="AgentResourceStatus"/> and <see cref="Describe"/>, the server reads them
/// with <see cref="Classify"/>, and a term added to one side cannot be missed by the other.
/// </para>
/// <para><b>Frozen once shipped</b>, like everything else on the wire: values are added, never
/// renamed. An unrecognised value is <see cref="Unknown"/>, never an error.</para>
/// </remarks>
public static class AgentHealth
{
    /// <summary>The agent said nothing, or something outside the vocabulary.</summary>
    /// <remarks>
    /// Deliberately <i>not</i> a problem. An agent is under no obligation to speak the
    /// vocabulary, and treating silence as trouble is how a whole fleet ends up amber.
    /// </remarks>
    public const string Unknown = "unknown";

    /// <summary>The agent reports everything verified.</summary>
    public const string InSync = "in-sync";

    /// <summary>Busy, not broken: progressing or waiting for a verifying reboot.</summary>
    public const string Working = "working";

    /// <summary>Something is wrong and named: degraded, blocked or escalated.</summary>
    public const string Degraded = "degraded";

    /// <summary>Given up on. The operator has been told more than once (§2.5).</summary>
    public const string Halted = "halted";

    /// <summary>Composes a self-report: a vocabulary head, then free-text detail.</summary>
    /// <param name="status">One of the <see cref="AgentResourceStatus"/> terms.</param>
    /// <param name="detail">Anything the agent wants to say, in its own words.</param>
    /// <remarks>
    /// The shape is <c>Head(detail)</c> because that is what §2.5 already specifies for
    /// <c>Degraded(reason, delta, attempts)</c>, and having one shape rather than two means
    /// <see cref="Classify"/> has one thing to understand.
    /// </remarks>
    public static string Describe(string status, string? detail) =>
        string.IsNullOrWhiteSpace(detail)
            ? status
            : string.Create(CultureInfo.InvariantCulture, $"{status}({detail})");

    /// <summary>Reads the coarse health out of a free-text self-report.</summary>
    /// <param name="agentStatus">
    /// <see cref="HandshakeHello.AgentStatus"/> verbatim, or <see langword="null"/>.
    /// </param>
    /// <returns>One of the constants on this class. Never null, never throws.</returns>
    public static string Classify(string? agentStatus)
    {
        if (string.IsNullOrWhiteSpace(agentStatus))
        {
            return Unknown;
        }

        var span = agentStatus.AsSpan().Trim();
        var open = span.IndexOf('(');
        var head = (open < 0 ? span : span[..open]).Trim();

        // Ordinal-ignore-case rather than exact: the vocabulary is a contract about words, and
        // refusing to recognise `degraded` because the agent wrote it lowercase would be the
        // brittleness this whole type exists to remove.
        return
            Is(head, AgentResourceStatus.InSync) ? InSync
            : Is(head, AgentResourceStatus.Progressing) || Is(head, AgentResourceStatus.AwaitingReboot) ? Working
            : Is(head, AgentResourceStatus.Degraded)
                || Is(head, AgentResourceStatus.Blocked)
                || Is(head, AgentResourceStatus.Escalated) ? Degraded
            : Is(head, AgentResourceStatus.Halted) ? Halted
            : Unknown;

        static bool Is(ReadOnlySpan<char> head, string term) =>
            head.Equals(term, StringComparison.OrdinalIgnoreCase);
    }
}
