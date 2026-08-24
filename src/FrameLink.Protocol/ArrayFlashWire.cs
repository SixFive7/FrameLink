using System.Globalization;
using System.Text;

namespace FrameLink.Protocol;

/// <summary>
/// The named stages of one firmware write, in the order they happen.
/// </summary>
/// <remarks>
/// <para>
/// <b>Named rather than left to a percentage, because a percentage is silent for most of the
/// operation.</b> <c>dfu-util</c>'s bar reaches 100% and then the array spends time committing what
/// it received to its own flash, resets, drops off the bus and re-enumerates — and a bar that sat at
/// 100% for twenty seconds with nothing beside it is worse than no bar at all, because the person
/// reading it concludes the write has hung and reaches for the plug. That is the one thing the whole
/// feature exists to stop.
/// </para>
/// <para>
/// <b>Frozen once shipped, like everything else on the wire.</b> Values are added, never renamed and
/// never renumbered: the name is what travels, an unrecognised name renders as itself, and a
/// consumer that does not know a stage shows the frame's own word for it rather than nothing.
/// </para>
/// <para>
/// The first five are <c>dfu-util</c>'s own; the last two are this product's, and the split is
/// deliberate. Once <c>dfu-util</c> has exited there is nothing left that can report, and the frame
/// is watching the USB bus for the unit to come back — so those stages carry the frame's word rather
/// than the tool's, and are labelled as such wherever they are shown.
/// </para>
/// </remarks>
public static class ArrayFlashStages
{
    /// <summary>
    /// <c>dfu-util</c> has started: opening the unit, claiming the interface, detaching into DFU
    /// mode. Nothing has been written.
    /// </summary>
    public const string Preparing = "preparing";

    /// <summary>Bytes are going to the unit. The one stage with a real quantity behind it.</summary>
    public const string Downloading = "downloading";

    /// <summary>
    /// <c>dfuMANIFEST</c> — the unit is committing what it received to its own flash.
    /// </summary>
    /// <remarks>
    /// This is the stage the bar would otherwise sit at 100% through. Nothing observable moves
    /// during it and nothing can be measured; what it needs is a name, which is what it has.
    /// </remarks>
    public const string Manifesting = "manifesting";

    /// <summary><c>dfuIDLE</c> after the manifest — the unit says it has finished.</summary>
    public const string Settling = "settling";

    /// <summary><c>Resetting USB to switch back to Run-Time mode</c>.</summary>
    public const string Resetting = "resetting";

    /// <summary>
    /// The frame's own poll of <c>bcdDevice</c>, waiting for the unit to come back on the bus.
    /// </summary>
    public const string ReEnumerating = "re-enumerating";

    /// <summary>The frame asking the control tool for its own second reading of the version.</summary>
    public const string Verifying = "verifying";

    /// <summary>Where <paramref name="stage"/> sits in the order, for a monotonic advance.</summary>
    /// <remarks>
    /// <b>A write never goes backwards, and this is what enforces it.</b> <c>dfuIDLE</c> is printed
    /// twice — once before the download while the tool determines the device status, and once after
    /// the manifest — so a reader that simply believed each line would show a write returning to an
    /// earlier stage half way through. An unrecognised stage answers <c>-1</c> and therefore never
    /// displaces one this build knows.
    /// </remarks>
    public static int Order(string? stage) => stage switch
    {
        Preparing => 0,
        Downloading => 1,
        Manifesting => 2,
        Settling => 3,
        Resetting => 4,
        ReEnumerating => 5,
        Verifying => 6,
        _ => -1,
    };
}

/// <summary>
/// A firmware write in flight, as one frame reports it in its self-report.
/// </summary>
/// <remarks>
/// <b>Every field is optional except <see cref="Screen"/>, and that is the honest shape.</b> Only
/// <see cref="ArrayFlashStages.Downloading"/> has a quantity behind it; a frame preparing the unit,
/// manifesting, or waiting for the bus knows how long it has been waiting and nothing else. A reader
/// that insists on a percentage would have to invent one for five of the seven stages.
/// </remarks>
public sealed record ArrayFlashWireStatus
{
    /// <summary>Which firmware screen the frame is showing, in the agent's own spelling.</summary>
    public required string Screen { get; init; }

    /// <summary>One of <see cref="ArrayFlashStages"/>, or null when no write is running.</summary>
    public string? Stage { get; init; }

    /// <summary><c>dfu-util</c>'s own printed percentage, or null.</summary>
    public int? Percent { get; init; }

    /// <summary>How many bytes the tool says it has sent, or null.</summary>
    public long? BytesWritten { get; init; }

    /// <summary>How many bytes the pinned image is, or null.</summary>
    public long? BytesTotal { get; init; }

    /// <summary>How long the write has been running, in whole seconds.</summary>
    public int? ElapsedSeconds { get; init; }

    /// <summary>
    /// The fraction a bar should be filled to, or null when nothing measurable is happening.
    /// </summary>
    /// <remarks>
    /// <b>Bytes first, the tool's percentage second, and null rather than a guess.</b> The byte
    /// count is the finer reading — a 933,888-byte image moves several kilobytes per printed
    /// percentage point — and the total is known from the pin before the tool says anything, so a
    /// bar can be correct from the first segment. Every other stage answers null, which is what
    /// makes a surface show the stage's name instead of a bar that is not measuring anything.
    /// </remarks>
    public double? Fraction =>
        !string.Equals(Stage, ArrayFlashStages.Downloading, StringComparison.Ordinal) ? null
        : BytesTotal is > 0 && BytesWritten is { } written
            ? Math.Clamp((double)written / BytesTotal.Value, 0, 1)
        : Percent is { } percent ? Math.Clamp(percent / 100d, 0, 1)
        : null;
}

/// <summary>
/// How a firmware write in flight rides inside <see cref="HandshakeHello.AgentStatus"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One carrier, not a second one.</b> The operator's instruction was to fold the write's progress
/// into the agent's wire status, and that field already travels twice — in the hello §4.2 puts on
/// every connect, and in the <see cref="ControlWire.KindAgentStatus"/> push
/// <c>AgentStatusReporter</c> sends whenever it changes. Adding a message kind for a progress bar
/// would have meant a second thing that can be stale, a second thing to buffer or not buffer, and a
/// second thing the Fleet Manager has to join against the first.
/// </para>
/// <para>
/// <b>It is appended after the vocabulary, never inside it.</b> <see cref="AgentHealth.Classify"/>
/// reads the head of the string up to the first <c>(</c>; the token goes after the closing
/// parenthesis, in brackets, so a frame writing firmware still classifies as whatever its
/// reconciliation loop is doing. A firmware write is not a rung on §2.6's ladder — nothing has
/// drifted, the product runs, and the frame is exactly what the operator declared — so it must not
/// be able to move one.
/// </para>
/// <para>
/// <b>Key-value rather than positional, because the field is free text a person also reads.</b> A
/// server that does not know this token shows it verbatim in the device's status column, where
/// <c>[array-flash screen=writing stage=downloading pct=41 bytes=380928/933888 t=12]</c> is a
/// sentence an operator can act on. A positional encoding would have been shorter and unreadable,
/// and would have broken the first time a field was added.
/// </para>
/// <para>
/// <b>Frozen once shipped.</b> Keys are added, never renamed; an unknown key is skipped rather than
/// failing the parse; and a token this build cannot read at all leaves the self-report exactly as it
/// arrived.
/// </para>
/// </remarks>
public static class ArrayFlashWire
{
    /// <summary>The token's opening word.</summary>
    public const string Marker = "array-flash";

    private const string ScreenKey = "screen";
    private const string StageKey = "stage";
    private const string PercentKey = "pct";
    private const string BytesKey = "bytes";
    private const string ElapsedKey = "t";

    /// <summary>Appends <paramref name="flash"/> to <paramref name="report"/>, or returns it unchanged.</summary>
    /// <param name="report">The self-report <see cref="AgentHealth.ReportFor"/> composed.</param>
    /// <param name="flash">The write in flight, or null when there is none.</param>
    /// <remarks>
    /// A frame with a firmware screen up and no reconciliation report at all — which is every frame
    /// in the seconds after a restart — still says the one thing that matters, because the token
    /// stands on its own rather than needing something to be appended to.
    /// </remarks>
    public static string? Append(string? report, ArrayFlashWireStatus? flash)
    {
        if (flash is null)
        {
            return report;
        }

        var token = new StringBuilder(96);
        token.Append('[').Append(Marker);
        Pair(token, ScreenKey, flash.Screen);

        if (flash.Stage is { Length: > 0 } stage)
        {
            Pair(token, StageKey, stage);
        }

        if (flash.Percent is { } percent)
        {
            Pair(token, PercentKey, percent.ToString(CultureInfo.InvariantCulture));
        }

        if (flash.BytesWritten is { } written)
        {
            Pair(
                token,
                BytesKey,
                flash.BytesTotal is { } total
                    ? string.Create(CultureInfo.InvariantCulture, $"{written}/{total}")
                    : written.ToString(CultureInfo.InvariantCulture));
        }

        if (flash.ElapsedSeconds is { } elapsed)
        {
            Pair(token, ElapsedKey, elapsed.ToString(CultureInfo.InvariantCulture));
        }

        token.Append(']');

        return string.IsNullOrWhiteSpace(report) ? token.ToString() : report + " " + token;

        static void Pair(StringBuilder builder, string key, string value) =>
            builder.Append(' ').Append(key).Append('=').Append(Clean(value));
    }

    /// <summary>Reads the token out of a self-report, or null when it carries none.</summary>
    /// <param name="agentStatus"><see cref="HandshakeHello.AgentStatus"/> verbatim, or null.</param>
    /// <returns>What the frame said about its write. Never throws.</returns>
    public static ArrayFlashWireStatus? Read(string? agentStatus)
    {
        if (Locate(agentStatus) is not { } span)
        {
            return null;
        }

        var body = agentStatus![(span.Open + 1)..span.Close];
        string? screen = null;
        string? stage = null;
        int? percent = null;
        long? written = null;
        long? total = null;
        int? elapsed = null;

        foreach (var word in body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var split = word.IndexOf('=', StringComparison.Ordinal);
            if (split <= 0)
            {
                continue;
            }

            var key = word[..split];
            var value = word[(split + 1)..];

            switch (key)
            {
                case ScreenKey:
                    screen = value;
                    break;
                case StageKey:
                    stage = value;
                    break;
                case PercentKey when int.TryParse(value, CultureInfo.InvariantCulture, out var read):
                    percent = read;
                    break;
                case ElapsedKey when int.TryParse(value, CultureInfo.InvariantCulture, out var seconds):
                    elapsed = seconds;
                    break;
                case BytesKey:
                    var bar = value.IndexOf('/', StringComparison.Ordinal);
                    var head = bar < 0 ? value : value[..bar];

                    if (long.TryParse(head, CultureInfo.InvariantCulture, out var sent))
                    {
                        written = sent;
                    }

                    if (bar >= 0 && long.TryParse(value[(bar + 1)..], CultureInfo.InvariantCulture, out var whole))
                    {
                        total = whole;
                    }

                    break;
                default:
                    // An unknown key is a newer frame saying something this build has no name for.
                    // Skipping it keeps every field this build *does* understand, which is the whole
                    // point of a key-value encoding over a positional one.
                    break;
            }
        }

        return screen is { Length: > 0 }
            ? new ArrayFlashWireStatus
            {
                Screen = screen,
                Stage = stage,
                Percent = percent,
                BytesWritten = written,
                BytesTotal = total,
                ElapsedSeconds = elapsed,
            }
            : null;
    }

    /// <summary>The self-report with the token taken out, for a surface that renders it separately.</summary>
    /// <remarks>
    /// The device row shows the whole string and should keep doing so; the firmware screen renders
    /// the token as a bar, and showing it again as text underneath would be the same fact twice.
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

    /// <summary>
    /// Strips whatever would make a value stop being one word.
    /// </summary>
    /// <remarks>
    /// <b>Composed values are the agent's own constants, so this guards a shape rather than an
    /// attacker.</b> Every value written here comes from <see cref="ArrayFlashStages"/>, an enum
    /// name or a number — none of which contains a space or a bracket. What it costs is nothing and
    /// what it buys is that a future field carrying free text cannot silently truncate the token or
    /// close it early, which would take the fields after it with it.
    /// </remarks>
    private static string Clean(string value)
    {
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || character is '[' or ']' or '=')
            {
                return Rebuild(value);
            }
        }

        return value;

        static string Rebuild(string value)
        {
            var builder = new StringBuilder(value.Length);

            foreach (var character in value)
            {
                builder.Append(char.IsWhiteSpace(character) || character is '[' or ']' or '=' ? '-' : character);
            }

            return builder.ToString();
        }
    }
}
