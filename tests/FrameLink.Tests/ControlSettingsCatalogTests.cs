using System.Text.RegularExpressions;

namespace FrameLink.Tests;

/// <summary>
/// <b>Every key the settings screen suggests is a key something actually reads</b> (§3.4).
/// </summary>
/// <remarks>
/// <para>
/// §3.4 makes settings "not a fixed list but a generic mechanism", and the GUI honours that: any
/// key at all can be typed, and an unrecognised one still works. What the catalog adds is a label,
/// a sentence of help and a suited input control — and, unavoidably, a <i>suggestion</i>. A
/// suggested key that nothing reads is worse than no suggestion: the operator types the name the
/// interface offered, the server stores it, the push carries it, and no resource ever asks for it.
/// Nothing fails. Nothing logs. The setting simply has no effect, for ever.
/// </para>
/// <para>
/// <b>It was not hypothetical.</b> Nine of the nineteen entries the catalog carried were read by
/// nothing: four were near-misses of a real key — <c>immich.url</c> for <c>immich.serverUrl</c>,
/// <c>audio.volume</c> for <c>audio.playbackVolume</c>, <c>slideshow.intervalSeconds</c> for
/// <c>slideshow.interval</c>, <c>locale.timezone</c> for <c>locale.timeZone</c> — and five named
/// features nothing implements.
/// </para>
/// <para>
/// <b>The check is a text search rather than a reflected list, deliberately.</b> The keys live as
/// string literals wherever the resource that reads them lives; a curated <c>AllKeys</c> array
/// would be a third place to forget, and the failure it exists to catch is precisely somebody
/// forgetting a place. Searching the source finds the literal wherever it is, which is the same
/// thing a person would do by hand, and it needs nothing but the repository — the same property
/// <c>GuiFreshnessTests</c> relies on.
/// </para>
/// </remarks>
public sealed class ControlSettingsCatalogTests
{
    [Fact]
    public void Every_catalogued_key_appears_in_the_source_of_something_that_reads_it()
    {
        var root = GuiFreshnessTests.RepositoryRoot();
        var catalog = Path.Combine(root, "src", "FrameLink.Control", "gui", "src", "lib", "settings-catalog.ts");
        var keys = CataloguedKeys(File.ReadAllText(catalog));

        Assert.NotEmpty(keys);

        // The agent reads nearly all of them; operator.name and operator.contact are read by the
        // server, which resolves them and pushes them on (decision 71). Both trees count.
        var sources = new List<string>();
        foreach (var project in new[] { "FrameLink.Agent", "FrameLink.Control" })
        {
            var directory = Path.Combine(root, "src", project);
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                sources.Add(File.ReadAllText(file));
            }
        }

        Assert.NotEmpty(sources);

        var orphans = keys
            .Where(key => !sources.Exists(source => source.Contains($"\"{key}\"", StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            orphans.Count == 0,
            $"The settings screen suggests {orphans.Count} key(s) that no source reads: "
            + $"{string.Join(", ", orphans)}. An operator who types one of these sets a value "
            + "nothing will ever ask for, and nothing anywhere says so. Either correct the key to "
            + "the one the code actually reads, or remove the entry until the feature exists.");
    }

    [Fact]
    public void The_two_keys_the_frame_shows_a_person_are_catalogued()
    {
        // §2.7 item 8, decision 71. A contact nobody can type is a contact no frame ever shows,
        // and this is the one setting whose whole value is that somebody filled it in.
        var catalog = File.ReadAllText(Path.Combine(
            GuiFreshnessTests.RepositoryRoot(),
            "src",
            "FrameLink.Control",
            "gui",
            "src",
            "lib",
            "settings-catalog.ts"));

        var keys = CataloguedKeys(catalog);

        Assert.Contains("operator.name", keys);
        Assert.Contains("operator.contact", keys);
    }

    /// <summary>The keys the catalog object declares, in file order.</summary>
    private static List<string> CataloguedKeys(string catalog)
    {
        var body = catalog[catalog.IndexOf("SETTING_CATALOG", StringComparison.Ordinal)..];

        return Regex
            .Matches(body, @"^\t'(?<key>[^']+)':\s*\{", RegexOptions.Multiline, TimeSpan.FromSeconds(5))
            .Select(match => match.Groups["key"].Value)
            .ToList();
    }
}
