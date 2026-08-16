using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FrameLink.Parity;

/// <summary>
/// Turns a facet's text — from the frozen inventory or from a live probe — into the key/value
/// map the comparison runs over.
/// </summary>
/// <remarks>
/// <para>
/// <b>One parser, both sides, always.</b> The v1 reference and the observed frame go through
/// exactly this code, with the same facet, in the same call. Anything else — a bespoke reader for
/// the capture and another for the probe — would generate differences of its own out of nothing
/// but a whitespace convention, and they would be indistinguishable from real ones.
/// </para>
/// <para>
/// <b>Everything reduces to a map</b>, because then one differ serves every facet and there is
/// one place where "missing", "extra" and "changed" are decided. A multiset of lines becomes
/// <c>line → ×n</c>, which is not a trick: the catalog repeatedly asks for a directive to appear
/// <i>exactly once</i> (<c>grep -c</c>, not <c>grep -q</c>), so a line that appears twice really
/// is a different state from a line that appears once, and this is what makes that visible.
/// </para>
/// </remarks>
public static class FacetParser
{
    /// <summary>Marker introducing a file's content in the <c>file-set</c> shape.</summary>
    public const string FileMarker = "##### ";

    /// <summary>Parses one side of one facet.</summary>
    public static IReadOnlyDictionary<string, string> Parse(ParityFacet facet, string? text)
    {
        ArgumentNullException.ThrowIfNull(facet);

        var body = Truncate(facet, text ?? string.Empty);

        var parsed = facet.Kind switch
        {
            FacetKinds.KeyValue => KeyValue(facet, body),
            FacetKinds.LineMultiset => Multiset(Lines(body)),
            FacetKinds.TokenMultiset => Multiset(Tokens(body)),
            FacetKinds.ConfigDirectives => Multiset(Directives(body)),
            FacetKinds.FileSet => FileSet(body),
            FacetKinds.Json => Json(body),
            FacetKinds.AlsaMixer => AlsaMixer(body),
            FacetKinds.UsersGroups => UsersGroups(body),
            FacetKinds.Network => Network(body),
            FacetKinds.Packages => Packages(body),
            _ => new Dictionary<string, string>(StringComparer.Ordinal),
        };

        foreach (var ignored in facet.IgnoredKeys.Keys)
        {
            parsed.Remove(ignored);
        }

        return parsed;
    }

    private static string Truncate(ParityFacet facet, string text)
    {
        if (string.IsNullOrEmpty(facet.TruncateAt))
        {
            return text;
        }

        var index = text.IndexOf(facet.TruncateAt, StringComparison.Ordinal);
        return index < 0 ? text : text[..index];
    }

    /// <summary>Non-blank lines, trailing whitespace gone, internal runs squeezed to one space.</summary>
    /// <remarks>
    /// The squeeze is what lets a column-padded <c>systemctl list-unit-files</c> and a
    /// single-spaced capture of the same output compare equal. It is applied to both sides, so it
    /// can hide only differences that consist purely of alignment.
    /// </remarks>
    private static IEnumerable<string> Lines(string text)
    {
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = Squeeze(raw);
            if (line.Length > 0)
            {
                yield return line;
            }
        }
    }

    private static IEnumerable<string> Tokens(string text)
    {
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return token;
        }
    }

    /// <summary>
    /// <c>config.txt</c> directives, each prefixed with the conditional section it sits under.
    /// </summary>
    /// <remarks>
    /// The section matters and losing it would be a real hole: <c>dtoverlay=dwc2,dr_mode=host</c>
    /// under <c>[cm5]</c> and the same line under <c>[all]</c> are different configurations of the
    /// board. Comments and blank lines are dropped — they carry no setting, and the stock file is
    /// two thirds comment.
    /// </remarks>
    private static IEnumerable<string> Directives(string text)
    {
        var section = "[none]";

        foreach (var line in Lines(text))
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line;
                continue;
            }

            yield return $"{section} {line}";
        }
    }

    private static Dictionary<string, string> Multiset(IEnumerable<string> items)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            counts[item] = counts.TryGetValue(item, out var seen) ? seen + 1 : 1;
        }

        return counts.ToDictionary(
            entry => entry.Key,
            entry => "×" + entry.Value.ToString(CultureInfo.InvariantCulture),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Key/value shapes, of which the inventory has three and they are told apart per facet.
    /// </summary>
    private static Dictionary<string, string> KeyValue(ParityFacet facet, string text) => facet.Id switch
    {
        "identity" => Positional(text, ["model", "os", "kernel", "machine", "dpkgArchitecture"]),
        "eeprom.config" => Delimited(text, '='),
        "journald" => Delimited(text, '='),
        _ => Columns(text),
    };

    /// <summary>Lines whose meaning is their position, named rather than numbered.</summary>
    /// <remarks>
    /// A shorter answer than the names expect is reported as the missing keys it is, rather than
    /// throwing: a frame where <c>/proc/device-tree/model</c> is unreadable should produce one
    /// finding about the model, not lose the kernel and the architecture with it.
    /// </remarks>
    private static Dictionary<string, string> Positional(string text, IReadOnlyList<string> names)
    {
        var values = Lines(text).ToList();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = 0; index < names.Count && index < values.Count; index++)
        {
            map[names[index]] = values[index];
        }

        return map;
    }

    /// <summary><c>KEY=VALUE</c>. Section headers and anything without the delimiter are kept as bare keys.</summary>
    private static Dictionary<string, string> Delimited(string text, char delimiter)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in Lines(text))
        {
            var split = line.IndexOf(delimiter, StringComparison.Ordinal);
            if (split <= 0)
            {
                map[line] = string.Empty;
                continue;
            }

            map[line[..split].Trim()] = line[(split + 1)..].Trim();
        }

        return map;
    }

    /// <summary>First column is the key, the rest of the line is the value.</summary>
    private static Dictionary<string, string> Columns(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in Lines(text))
        {
            var split = line.IndexOf(' ', StringComparison.Ordinal);
            if (split <= 0)
            {
                map[line] = string.Empty;
                continue;
            }

            map[line[..split]] = line[(split + 1)..].Trim();
        }

        return map;
    }

    /// <summary><c>##### path</c> then the file body, to a path-to-content map.</summary>
    /// <remarks>
    /// Content is normalised to LF with trailing whitespace and trailing blank lines removed, and
    /// nothing else — a unit file that gained a directive, lost one, or had one reworded is a
    /// changed value, which is exactly what it is.
    /// </remarks>
    private static Dictionary<string, string> FileSet(string text)
    {
        var files = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        StringBuilder? current = null;

        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();

            if (line.StartsWith(FileMarker, StringComparison.Ordinal))
            {
                var path = line[FileMarker.Length..].Trim();
                if (!files.TryGetValue(path, out current))
                {
                    current = new StringBuilder();
                    files[path] = current;
                }

                continue;
            }

            current?.Append(line).Append('\n');
        }

        return files.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.ToString().TrimEnd('\n'),
            StringComparer.Ordinal);
    }

    /// <summary>A flat JSON object. A nested value is kept as its raw JSON text.</summary>
    private static Dictionary<string, string> Json(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var body = text.Trim();

        if (body.Length == 0)
        {
            return map;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                map["<not an object>"] = body;
                return map;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                map[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                    _ => property.Value.GetRawText(),
                };
            }
        }
        catch (JsonException exception)
        {
            // Not a parse failure of the harness: a frame serving something that is not JSON is a
            // real finding, and it says more as one unmistakable key than as an exception.
            map["<unparseable>"] = exception.Message;
        }

        return map;
    }

    /// <summary><c>amixer scontents</c>, one key per control per channel.</summary>
    /// <remarks>
    /// The five <c>audio.mixer.*</c> resources each own one control and one channel, so a key of
    /// <c>PCM,0 Front Left Playback</c> is the granularity a difference has to be reported at for
    /// it to name a resource. Capabilities and limits get keys of their own: a control that lost
    /// <c>pswitch</c> is a different control, whatever its volume reads.
    /// </remarks>
    private static Dictionary<string, string> AlsaMixer(string text)
    {
        const string ControlPrefix = "Simple mixer control ";
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var control = "<none>";

        foreach (var line in Lines(text))
        {
            if (line.StartsWith(ControlPrefix, StringComparison.Ordinal))
            {
                control = line[ControlPrefix.Length..].Replace("'", string.Empty, StringComparison.Ordinal).Trim();
                continue;
            }

            var split = line.IndexOf(':', StringComparison.Ordinal);
            if (split <= 0)
            {
                map[$"{control} {line}"] = string.Empty;
                continue;
            }

            var label = line[..split].Trim();
            var value = line[(split + 1)..].Trim();

            // "Front Left: Playback 60 [100%] [0.00dB] [on]" — the direction belongs to the key,
            // so a capture volume and a playback volume on the same channel never collide.
            var direction = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (direction.Length == 2
                && (string.Equals(direction[0], "Playback", StringComparison.Ordinal)
                    || string.Equals(direction[0], "Capture", StringComparison.Ordinal))
                && !string.Equals(label, "Limits", StringComparison.Ordinal)
                && !string.Equals(label, "Playback channels", StringComparison.Ordinal)
                && !string.Equals(label, "Capture channels", StringComparison.Ordinal))
            {
                map[$"{control} {label} {direction[0]}"] = direction[1];
                continue;
            }

            map[$"{control} {label}"] = value;
        }

        return map;
    }

    /// <summary><c>id</c> and <c>getent group</c>, reduced to membership by name.</summary>
    private static Dictionary<string, string> UsersGroups(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in Lines(text))
        {
            if (line.StartsWith("uid=", StringComparison.Ordinal))
            {
                foreach (var field in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var split = field.IndexOf('=', StringComparison.Ordinal);
                    if (split <= 0)
                    {
                        continue;
                    }

                    var name = field[..split];
                    var value = field[(split + 1)..];

                    if (string.Equals(name, "groups", StringComparison.Ordinal))
                    {
                        foreach (var group in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            map["member of " + NameOf(group)] = "yes";
                        }

                        continue;
                    }

                    map[name] = NameOf(value);
                }

                continue;
            }

            // "adm:x:4:framelink" — the members are the setting, the gid is allocation order.
            var fields = line.Split(':');
            if (fields.Length >= 4)
            {
                map["group " + fields[0]] = string.Join(',', fields[3].Split(',', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
            }
        }

        return map;
    }

    /// <summary><c>1000(framelink)</c> to <c>framelink</c>; a bare number stays as it is.</summary>
    private static string NameOf(string field)
    {
        var open = field.IndexOf('(', StringComparison.Ordinal);
        var close = field.LastIndexOf(')');
        return open >= 0 && close > open ? field[(open + 1)..close] : field;
    }

    /// <summary><c>hostname</c> then <c>ip -br addr</c>, addresses dropped.</summary>
    private static Dictionary<string, string> Network(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var first = true;

        foreach (var line in Lines(text))
        {
            if (first)
            {
                map["hostname"] = line;
                first = false;
                continue;
            }

            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2)
            {
                map["interface " + fields[0]] = fields[1];
            }
        }

        return map;
    }

    /// <summary><c>name version</c>, which is the shape both the capture and dpkg produce.</summary>
    private static Dictionary<string, string> Packages(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in Lines(text))
        {
            var split = line.IndexOf(' ', StringComparison.Ordinal);
            if (split > 0)
            {
                map[line[..split]] = line[(split + 1)..].Trim();
            }
        }

        return map;
    }

    private static string Squeeze(string line)
    {
        var builder = new StringBuilder(line.Length);
        var space = false;

        foreach (var character in line)
        {
            if (char.IsWhiteSpace(character))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
