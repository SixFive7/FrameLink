using System.Text;

namespace FrameLink.Agent.Resources;

/// <summary>Why a proposed boot-partition write was refused.</summary>
/// <param name="Valid">Whether the write may proceed.</param>
/// <param name="Problem">What is wrong, when it may not.</param>
public readonly record struct BootFileVerdict(bool Valid, string? Problem)
{
    /// <summary>A write that may proceed.</summary>
    public static BootFileVerdict Ok { get; } = new(true, null);

    /// <summary>A refusal, with the reason.</summary>
    public static BootFileVerdict Refuse(string problem) => new(false, problem);
}

/// <summary>
/// Reading, editing and — above all — <b>validating</b> the two boot-partition text files.
/// </summary>
/// <remarks>
/// <para>
/// §5.5's first mitigation is "validate before writing". These are the files that can produce a
/// device nothing remote can reach, so the check is not a formality: every proposed write is
/// compared against the file it replaces and refused unless the difference is exactly the one
/// line the catalog intended. A resource that cannot prove its own edit is minimal does not get
/// to make it.
/// </para>
/// <para>
/// The checks are deliberately structural rather than semantic. Nothing here knows what a valid
/// overlay name is — the firmware decides that, and guessing would give false confidence. What
/// it knows is what a <i>malformed</i> file looks like: a <c>cmdline.txt</c> that lost its
/// <c>root=</c>, a <c>config.txt</c> line that is neither a section, a comment nor a
/// <c>key=value</c>, an edit that changed more than it claimed.
/// </para>
/// </remarks>
public static class BootConfigText
{
    /// <summary>Where the firmware reads its configuration.</summary>
    public const string ConfigPath = "/boot/firmware/config.txt";

    /// <summary>Where the firmware reads the kernel command line.</summary>
    public const string CmdlinePath = "/boot/firmware/cmdline.txt";

    /// <summary>Whether <paramref name="content"/> already carries <paramref name="line"/>.</summary>
    public static bool HasLine(string? content, string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        foreach (var raw in content.Split('\n'))
        {
            if (string.Equals(raw.Trim('\r', ' ', '\t'), line, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Appends <paramref name="line"/> if it is not already present.</summary>
    public static string AppendLine(string? content, string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (HasLine(content, line))
        {
            return Normalise(content);
        }

        var normalised = Normalise(content);
        return normalised.Length == 0 ? line + "\n" : normalised + line + "\n";
    }

    /// <summary>
    /// Checks that <paramref name="updated"/> differs from <paramref name="original"/> by
    /// exactly the addition of <paramref name="added"/>, and that it still parses.
    /// </summary>
    public static BootFileVerdict ValidateConfig(string? original, string updated, string added)
    {
        ArgumentNullException.ThrowIfNull(updated);
        ArgumentNullException.ThrowIfNull(added);

        var before = Lines(original);
        var after = Lines(updated);

        if (after.Count != before.Count + 1)
        {
            return BootFileVerdict.Refuse(
                $"the edit would change the file by {after.Count - before.Count} lines instead of exactly one");
        }

        for (var index = 0; index < before.Count; index++)
        {
            if (!string.Equals(before[index], after[index], StringComparison.Ordinal))
            {
                return BootFileVerdict.Refuse($"the edit would also change line {index + 1}");
            }
        }

        if (!string.Equals(after[^1], added, StringComparison.Ordinal))
        {
            return BootFileVerdict.Refuse("the added line is not the one that was intended");
        }

        foreach (var raw in after)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                continue;
            }

            if (!line.Contains('=', StringComparison.Ordinal))
            {
                return BootFileVerdict.Refuse($"'{line}' is neither a section, a comment nor a key=value setting");
            }
        }

        return BootFileVerdict.Ok;
    }

    /// <summary>Reads the single kernel command line, ignoring blank lines.</summary>
    public static string ReadCmdline(string? content)
    {
        foreach (var raw in Lines(content))
        {
            var line = raw.Trim();
            if (line.Length > 0)
            {
                return line;
            }
        }

        return string.Empty;
    }

    /// <summary>Whether the command line already carries a token starting with <paramref name="prefix"/>.</summary>
    public static string? FindToken(string? content, string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        foreach (var token in ReadCmdline(content).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith(prefix, StringComparison.Ordinal))
            {
                return token;
            }
        }

        return null;
    }

    /// <summary>Appends one token to the single kernel command line.</summary>
    public static string AppendToken(string? content, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var line = ReadCmdline(content);
        return (line.Length == 0 ? token : line + " " + token) + "\n";
    }

    /// <summary>
    /// Checks a proposed command line: one line, the token added, nothing else touched, and the
    /// two parameters without which the machine will not boot still present.
    /// </summary>
    public static BootFileVerdict ValidateCmdline(string? original, string updated, string token)
    {
        ArgumentNullException.ThrowIfNull(updated);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var lines = Lines(updated).Where(line => line.Trim().Length > 0).ToList();
        if (lines.Count != 1)
        {
            // The firmware reads the first line only. A second line is silently ignored, which
            // is how a "successful" edit produces a kernel that never sees its own parameters.
            return BootFileVerdict.Refuse($"the kernel command line must be a single line, not {lines.Count}");
        }

        var before = ReadCmdline(original).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var after = ReadCmdline(updated).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // The two fatal parameters are checked before the minimality checks, because losing
        // either of them is the failure §5.5 is actually about and it deserves to be what the
        // refusal says. A minimality violation that also drops root= would otherwise be reported
        // as untidiness.
        if (!after.Any(parameter => parameter.StartsWith("root=", StringComparison.Ordinal)))
        {
            return BootFileVerdict.Refuse("the result has no root= parameter, so the machine would not boot");
        }

        if (!after.Any(parameter => parameter.StartsWith("console=", StringComparison.Ordinal)))
        {
            return BootFileVerdict.Refuse("the result has no console= parameter, so there would be nothing to narrate on");
        }

        if (after.Length != before.Length + 1 || !string.Equals(after[^1], token, StringComparison.Ordinal))
        {
            return BootFileVerdict.Refuse("the edit would change more than the one parameter it intended");
        }

        for (var index = 0; index < before.Length; index++)
        {
            if (!string.Equals(before[index], after[index], StringComparison.Ordinal))
            {
                return BootFileVerdict.Refuse($"the edit would also change '{before[index]}'");
            }
        }

        return BootFileVerdict.Ok;
    }

    private static List<string> Lines(string? content) =>
        string.IsNullOrEmpty(content)
            ? []
            : [.. content.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n')];

    private static string Normalise(string? content)
    {
        var lines = Lines(content);
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }
}
