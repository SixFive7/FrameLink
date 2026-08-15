using System.Collections.Frozen;

namespace FrameLink.Control.Storage;

/// <summary>
/// The reviewed package set every frame's inventory is measured against.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it is.</b> The 929 packages installed on the v1 frame at the moment Precondition zero
/// froze it, with their exact versions, transcribed verbatim from the <c>PACKAGES</c> block of
/// <c>reference/v1-state-inventory.txt</c>. It is the definition of "the state a person looked
/// at", which is the only thing drift can be measured from.
/// </para>
/// <para>
/// <b>What it is not: a pin.</b> Nothing installs these versions, nothing downgrades to them, and
/// a frame above the baseline is not misconfigured — it has had a security update, which §4.1's
/// no-inbound-ports frame is deliberately left free to receive. Forward movement is the expected
/// direction and is reported without ever being acted on. Backward movement, and absence, are the
/// two directions that mean something is wrong.
/// </para>
/// <para>
/// <b>Why this file is the authority and the inventory file is the source.</b> The build embeds a
/// copy under <c>Storage/</c> rather than reading the reference file at runtime, because a
/// container has no repository in it; <c>ControlPackageTests</c> reads the reference file and
/// fails when the two disagree, so the copy can never quietly become the original. The same
/// arrangement §7.1 uses for the base image pin, for the same reason: the artifact is somebody
/// else's data, and changing it must be a diff somebody reviews.
/// </para>
/// </remarks>
public static class PackageBaseline
{
    /// <summary>Logical name of the embedded baseline.</summary>
    public const string ResourceName = "v1-package-baseline.txt";

    /// <summary>When a person last checked this baseline against the frame it came from.</summary>
    /// <remarks>
    /// §7.1: version claims are never asserted from memory, only verified per session and
    /// stamped. This is that stamp for the whole set at once.
    /// </remarks>
    public static readonly DateTimeOffset ReviewedUtc = new(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);

    private static readonly Lazy<FrozenDictionary<string, string>> Loaded = new(Read);

    /// <summary>Package name to the reviewed version, ordinal-keyed.</summary>
    public static FrozenDictionary<string, string> Versions => Loaded.Value;

    /// <summary>The reviewed version of one package, or null when it is not in the baseline.</summary>
    public static string? VersionOf(string package) =>
        Versions.TryGetValue(package, out var version) ? version : null;

    /// <summary>
    /// Parses the <c>name version</c> form both this file and the reference inventory are written
    /// in.
    /// </summary>
    /// <remarks>
    /// Public because the test that keeps the copy honest parses the reference file with exactly
    /// this reader; two parsers would let a difference in whitespace handling hide a difference in
    /// content.
    /// </remarks>
    public static Dictionary<string, string> Parse(string text)
    {
        var packages = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var raw in (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space <= 0)
            {
                continue;
            }

            packages[line[..space]] = line[(space + 1)..].Trim();
        }

        return packages;
    }

    private static FrozenDictionary<string, string> Read()
    {
        using var stream = typeof(PackageBaseline).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"The embedded resource '{ResourceName}' is missing from this build. "
                + "FrameLink.Control.csproj embeds it from Storage/.");

        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd()).ToFrozenDictionary(StringComparer.Ordinal);
    }
}
