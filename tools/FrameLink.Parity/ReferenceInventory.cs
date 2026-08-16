namespace FrameLink.Parity;

/// <summary>
/// <c>reference/v1-state-inventory.txt</c>, read as the blocks it is.
/// </summary>
/// <remarks>
/// <para>
/// <b>A captured artifact, not a designed schema.</b> Somebody ran a script against the working
/// v1 frame the day before it became the mule, and what came back is what there is: banner-
/// separated blocks, a couple of them truncated mid-file, one holding an error message where a
/// command was missing. Nothing may be normalised on the way in — the file is the parity target
/// and the moment this reader starts tidying it, the thing being compared stops being the thing
/// that was captured.
/// </para>
/// <para>
/// So this class does exactly one thing: it splits the file into <c>== NAME</c> blocks and hands
/// back the text between the banners. Every interpretation happens in <see cref="FacetParser"/>,
/// where the same code also reads the live frame's side.
/// </para>
/// </remarks>
public static class ReferenceInventory
{
    /// <summary>Path of the inventory relative to the repository root.</summary>
    public const string RelativePath = "reference/v1-state-inventory.txt";

    private const string BannerRule = "======";
    private const string SectionPrefix = "== ";

    /// <summary>Reads the inventory at the repository root and splits it into blocks.</summary>
    public static IReadOnlyDictionary<string, string> Load(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var path = Path.Combine(repositoryRoot, "reference", "v1-state-inventory.txt");
        return Parse(File.ReadAllText(path));
    }

    /// <summary>Splits inventory text into section name to block body.</summary>
    /// <remarks>
    /// The banner rules above and below a section name are dropped; everything else between one
    /// section name and the next is the block, trailing blank lines removed. A duplicate section
    /// name would be a corrupt capture and throws rather than silently keeping one of the two.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var blocks = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;

        foreach (var raw in (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();

            if (line.StartsWith(BannerRule, StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith(SectionPrefix, StringComparison.Ordinal))
            {
                var name = line[SectionPrefix.Length..].Trim();
                if (blocks.ContainsKey(name))
                {
                    throw new InvalidDataException(
                        $"The inventory carries two '{name}' sections. One of them would be silently lost.");
                }

                current = [];
                blocks[name] = current;
                continue;
            }

            if (current is null)
            {
                // The file's own prose header, above the first banner.
                continue;
            }

            current.Add(line);
        }

        return blocks.ToDictionary(
            entry => entry.Key,
            entry => string.Join('\n', Trim(entry.Value)),
            StringComparer.Ordinal);
    }

    private static List<string> Trim(List<string> lines)
    {
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        while (lines.Count > 0 && lines[0].Length == 0)
        {
            lines.RemoveAt(0);
        }

        return lines;
    }
}
