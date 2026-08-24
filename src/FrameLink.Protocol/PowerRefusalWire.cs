using System.Text;

namespace FrameLink.Protocol;

/// <summary>
/// The two power verbs a frame can be asked for, and can refuse.
/// </summary>
/// <remarks>
/// <b>Wire tokens, not prose.</b> The words a person reads are composed once on the frame and
/// travel whole (<see cref="PowerRefusalStatus.Why"/>); these two are what a surface switches on,
/// and they are one word each because a value inside a key-value token cannot carry a space. The
/// prose spelling of each is <see cref="Describe"/>, and it exists so the agent's journal, the
/// event trail and the device row all say "shut down" rather than three near-misses.
/// <b>Frozen once shipped</b>, like everything else here: values are added, never renamed, and an
/// unrecognised verb renders as itself.
/// </remarks>
public static class PowerVerbs
{
    /// <summary>Restart and try again — the budgets are cleared and the frame reboots.</summary>
    public const string Restart = "restart";

    /// <summary>Switch off. Nothing but a person brings the frame back.</summary>
    public const string Shutdown = "shutdown";

    /// <summary>How <paramref name="verb"/> reads inside a sentence.</summary>
    /// <remarks>
    /// A verb this build has no name for is shown as the frame spelled it, exactly as an
    /// unrecognised resource status or firmware stage is. It is a newer frame saying something,
    /// not an error.
    /// </remarks>
    public static string Describe(string? verb) => verb switch
    {
        Restart => "restart",
        Shutdown => "shut down",
        { Length: > 0 } => verb,
        _ => "change its power state",
    };
}

/// <summary>
/// A power change one frame turned down, as that frame reports it in its self-report.
/// </summary>
/// <remarks>
/// <b>The sentence is carried, never recomposed.</b> <c>FrameRecovery.RefusalLine</c> writes it on
/// the frame and this record ferries it verbatim, because the half that matters — <i>nothing has
/// been queued and nothing is waiting its turn</i> — is exactly the half a server rewording it
/// would drop. Every other refusal in this product is answered by asking again later, so a person
/// who assumes a refused shutdown is waiting its turn walks away from a frame that is still on, or
/// reaches for the plug, which is the hazard the refusal exists for.
/// </remarks>
public sealed record PowerRefusalStatus
{
    /// <summary>One of <see cref="PowerVerbs"/> — which button was refused.</summary>
    public required string Verb { get; init; }

    /// <summary>The whole refusal, in the frame's own words.</summary>
    public required string Why { get; init; }
}

/// <summary>
/// How a refused power change rides inside <see cref="HandshakeHello.AgentStatus"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One carrier, not a second one</b> — the same argument <see cref="ArrayFlashWire"/> makes, and
/// this token sits beside that one in the same field. The self-report already travels twice, in the
/// hello §4.2 puts on every connect and in the <see cref="ControlWire.KindAgentStatus"/> push
/// <c>AgentStatusReporter</c> sends whenever it changes, so a refusal that is <i>current</i> is
/// carried where every other current fact about a frame already is. The refusal's other half — that
/// it happened, and when — is a <see cref="DeviceEvent"/>, because that is history and history is
/// not this field's job.
/// </para>
/// <para>
/// <b>It is appended after the vocabulary, never inside it.</b> <see cref="AgentHealth.Classify"/>
/// reads the head of the string up to the first <c>(</c>; the token goes after the closing
/// parenthesis, in brackets, so a frame that refused a restart still classifies as whatever its
/// reconciliation loop is doing. A refusal is not a rung on §2.6's ladder — nothing has drifted, the
/// product runs, and the frame is exactly what the operator declared — so it must not be able to
/// move one.
/// </para>
/// <para>
/// <b><see cref="WhyKey"/> is the last key and runs to the closing bracket, which is the one place
/// this differs from <see cref="ArrayFlashWire"/>.</b> That token's values are all stage names and
/// numbers, so it can insist every value is one word; this one exists to carry a whole sentence, and
/// a sentence cannot be one word. Making it the tail rather than inventing a quoting rule keeps the
/// parse trivial and total. Two consequences, both deliberate: any key added later goes
/// <i>before</i> <c>why</c>, and the sentence has its brackets replaced on the way in so it can
/// never close the token early and take every field after it with it.
/// </para>
/// <para>
/// <b>Frozen once shipped.</b> Keys are added, never renamed; an unknown key is skipped rather than
/// failing the parse; and a token this build cannot read at all leaves the self-report exactly as it
/// arrived.
/// </para>
/// </remarks>
public static class PowerRefusalWire
{
    /// <summary>The token's opening word.</summary>
    public const string Marker = "power-refused";

    /// <summary>The key carrying the refusal sentence. Always last inside the token.</summary>
    public const string WhyKey = "why";

    private const string VerbKey = "verb";

    /// <summary>Appends <paramref name="refusal"/> to <paramref name="report"/>, or returns it unchanged.</summary>
    /// <param name="report">The self-report composed so far.</param>
    /// <param name="refusal">What the frame refused, or null when it is refusing nothing.</param>
    /// <remarks>
    /// A frame with no reconciliation report at all — which is every frame in the seconds after a
    /// restart — still says the one thing that matters, because the token stands on its own rather
    /// than needing something to be appended to.
    /// </remarks>
    public static string? Append(string? report, PowerRefusalStatus? refusal)
    {
        if (refusal is null)
        {
            return report;
        }

        var token = new StringBuilder(288)
            .Append('[')
            .Append(Marker)
            .Append(' ')
            .Append(VerbKey)
            .Append('=')
            .Append(OneWord(refusal.Verb))
            .Append(' ')
            .Append(WhyKey)
            .Append('=')
            .Append(Sentence(refusal.Why))
            .Append(']');

        return string.IsNullOrWhiteSpace(report) ? token.ToString() : report + " " + token;
    }

    /// <summary>Reads the token out of a self-report, or null when it carries none.</summary>
    /// <param name="agentStatus"><see cref="HandshakeHello.AgentStatus"/> verbatim, or null.</param>
    /// <returns>What the frame said it refused. Never throws.</returns>
    public static PowerRefusalStatus? Read(string? agentStatus)
    {
        if (Locate(agentStatus) is not { } span)
        {
            return null;
        }

        var body = agentStatus![(span.Open + 1 + Marker.Length)..span.Close];

        // The sentence first, because it is the tail: everything after `why=` belongs to it,
        // spaces and all, and what is left in front of it is the ordinary key-value part.
        var opening = body.IndexOf(" " + WhyKey + "=", StringComparison.Ordinal);
        var why = opening < 0 ? null : body[(opening + WhyKey.Length + 2)..].Trim();
        var head = opening < 0 ? body : body[..opening];

        string? verb = null;

        foreach (var word in head.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var split = word.IndexOf('=', StringComparison.Ordinal);
            if (split <= 0)
            {
                continue;
            }

            if (string.Equals(word[..split], VerbKey, StringComparison.Ordinal))
            {
                verb = word[(split + 1)..];
            }

            // Anything else is a newer frame saying something this build has no name for. Skipping
            // it keeps every field this build does understand, which is the whole point of a
            // key-value encoding over a positional one.
        }

        // The sentence is the payload. A token that lost it carries nothing a surface could show,
        // and inventing one here is exactly the recomposition this type exists to prevent.
        return why is { Length: > 0 }
            ? new PowerRefusalStatus { Verb = verb ?? string.Empty, Why = why }
            : null;
    }

    /// <summary>The self-report with the token taken out, for a surface that renders it separately.</summary>
    /// <remarks>
    /// The device row shows the whole string and should keep doing so; a surface that draws the
    /// refusal as its own paragraph would otherwise show the same sentence twice.
    /// </remarks>
    public static string? Without(string? agentStatus)
    {
        if (Locate(agentStatus) is not { } span)
        {
            return agentStatus;
        }

        var trimmed = (agentStatus![..span.Open] + agentStatus[(span.Close + 1)..]).Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>Where the token sits inside <paramref name="agentStatus"/>, or null.</summary>
    private static (int Open, int Close)? Locate(string? agentStatus)
    {
        if (string.IsNullOrWhiteSpace(agentStatus))
        {
            return null;
        }

        var opening = agentStatus.IndexOf("[" + Marker, StringComparison.Ordinal);
        if (opening < 0)
        {
            return null;
        }

        var closing = agentStatus.IndexOf(']', opening);
        return closing < 0 ? null : (opening, closing);
    }

    /// <summary>Strips whatever would make a value stop being one word.</summary>
    private static string OneWord(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            builder.Append(char.IsWhiteSpace(character) || character is '[' or ']' or '=' ? '-' : character);
        }

        return builder.Length == 0 ? "-" : builder.ToString();
    }

    /// <summary>
    /// Replaces whatever would close the token early, and nothing else.
    /// </summary>
    /// <remarks>
    /// Spaces stay — the whole reason this field is the tail is that it is a sentence. Brackets do
    /// not: the reader stops at the first <c>]</c> after the marker, so a sentence carrying one
    /// would truncate itself and leave a stray fragment sitting in the operator's status column.
    /// The frame's own refusal contains neither, so this guards a shape rather than an attacker —
    /// and it is what keeps that true after somebody words a future hold differently.
    /// </remarks>
    private static string Sentence(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            builder.Append(character switch
            {
                '[' => '(',
                ']' => ')',
                _ => character,
            });
        }

        return builder.ToString().Trim();
    }
}
