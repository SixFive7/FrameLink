using System.Globalization;
using System.IO.Compression;

namespace FrameLink.Agent.Firmware;

/// <summary>
/// The pinned DFU images this binary carries <b>inside itself</b> — decision 91's other half.
/// </summary>
/// <remarks>
/// <para>
/// <b>A frame that has an agent has the firmware.</b> No GitHub, no Fleet Manager, no route to
/// anywhere. The one operation on a frame that cannot be undone by rewriting the card should depend
/// on nothing outside the executable performing it, and an image fetched at the moment somebody
/// wants it is an image fetched onto a frame whose day is already going badly. The bytes are
/// vendored under <c>vendor/respeaker-xvf3800/</c>, their provenance is recorded in the
/// <c>NOTICE.md</c> beside them, and the <c>&lt;EmbeddedResource&gt;</c> that compiles them in is
/// the one in <c>FrameLink.Agent.csproj</c> next to the block that embeds the product app.
/// </para>
/// <para>
/// <b>This class carries no digest of its own, and that is the point.</b> It answers "does this
/// binary contain something published under that name" and hands back a stream; whether those
/// bytes are the pinned bytes is decided by <see cref="Resources.VerifiedFetch"/> against
/// <see cref="XvfFirmwareImage.Sha256"/>, in the same line of code that decides it for a download.
/// A second digest check here would be a second answer to a question that has one, and the first
/// time the two disagreed it would be because somebody updated one of them.
/// </para>
/// <para>
/// <b>One image today, and adding the others is a <c>git add</c>.</b> The pin names three — the
/// v2.1.0 target, the v2.0.6 fallback and the all-<c>0xFF</c> erase image — and only the target is
/// vendored; whether the other two join it is an open question rather than an oversight. Nothing
/// here names a file: the csproj globs <c>*.bin</c>, this class keys on
/// <see cref="XvfFirmwareImage.Name"/>, and an image that is not carried simply falls to the
/// download path it uses today. So the day that question is answered, the change is the bytes, a
/// <c>NOTICE.md</c> row and a ledger note — not a redesign.
/// </para>
/// <para>
/// <b>The names are normalised at read time, not at build time</b>, exactly as
/// <see cref="Local.EmbeddedApp"/> does it and for the same reason: MSBuild's
/// <c>%(RecursiveDir)</c> carries the host's directory separator, so a binary built on the
/// workstation would embed <c>firmware/usb\image.bin</c> and one built in the arm64 container
/// <c>firmware/usb/image.bin</c>. Normalising on both sides means the lookup cannot depend on where
/// the binary was built — a difference that would otherwise surface only at runtime, on a frame, in
/// front of somebody waiting to flash.
/// </para>
/// <para>
/// <b>What is stored is gzip, and the name says so.</b> A managed resource is stored verbatim, so
/// embedding this image raw cost the linux-arm64 binary all 933,888 of its bytes — measured, and
/// paid by every frame in the fleet over §2.8's hourly update feed on every release. gzip -9 takes
/// the same image to 300,528. The csproj compresses on the way in, this class decompresses on the
/// way out, and the resource is named <c>&lt;file&gt;.bin.gz</c> rather than <c>&lt;file&gt;.bin</c>
/// so that nothing in the binary claims to be a DFU image while holding something else. The
/// <i>vendored</i> file is never compressed: it has to stay the bytes upstream served, because that
/// is the whole of what <c>NOTICE.md</c> claims and the only thing a reader can re-derive with
/// <c>curl</c> and <c>sha256sum</c>.
/// </para>
/// <para>
/// <b>Nothing is cached, not even decompressed.</b> The app is served on every request and is held
/// in memory; a firmware image is read at most once per install, so holding 933 KB of managed heap
/// for the life of the process would be paying continuously for something wanted once. The
/// decompression is streaming, so the whole image is never resident at all — it goes from the
/// executable's read-only data, through the decoder, into the file, 64 KB at a time.
/// </para>
/// </remarks>
public static class XvfVendoredFirmware
{
    /// <summary>Prefix every embedded firmware image carries, set by the csproj's LogicalName.</summary>
    public const string Prefix = "firmware/";

    /// <summary>Suffix naming how the bytes are stored, since they are not the image itself.</summary>
    public const string CompressedSuffix = ".gz";

    /// <summary>Where these bytes came from, for the line the journal shows after an install.</summary>
    /// <remarks>
    /// Stands where a URL stands on the fetched path, because the journal line is read by somebody
    /// asking "where did the image on this card come from" and "the agent's own binary" is the
    /// whole answer.
    /// </remarks>
    public const string Origin = "this agent's own binary";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Carried = new(Load);

    /// <summary>Every image name this binary carries, sorted; the file names, without the prefix.</summary>
    public static IReadOnlyList<string> Names
    {
        get
        {
            var names = Carried.Value.Keys.ToList();
            names.Sort(StringComparer.Ordinal);
            return names;
        }
    }

    /// <summary>Whether this binary carries something published under <paramref name="image"/>'s name.</summary>
    /// <remarks>
    /// Says nothing about the bytes — only that a lookup would find something. It is what orders an
    /// install so that everything travelling inside the binary is placed before anything that needs
    /// a network, which is what makes the offline guarantee independent of the pin's own ordering.
    /// </remarks>
    public static bool Carries(XvfFirmwareImage image) => Carried.Value.ContainsKey(image.Name);

    /// <summary>
    /// Opens this binary's copy of <paramref name="image"/>, decompressing as it is read, or null
    /// if it carries none.
    /// </summary>
    /// <remarks>
    /// The caller owns the stream and must verify what comes out of it. A non-null return means
    /// "there are bytes here under that name", never "these are the right bytes" — and with a
    /// decoder in the way it promises even less than it did before, because a damaged blob can
    /// decode into anything at all, or into far too much, or throw
    /// <see cref="InvalidDataException"/> partway through. All three are the caller's to handle,
    /// and <see cref="Resources.VerifiedFetch"/> handles all three.
    /// </remarks>
    public static Stream? Open(XvfFirmwareImage image)
    {
        if (!Carried.Value.TryGetValue(image.Name, out var resource))
        {
            return null;
        }

        var stored = typeof(XvfVendoredFirmware).Assembly.GetManifestResourceStream(resource);

        // GZipStream owns what it wraps, so the caller's single dispose closes both.
        return stored is null ? null : new GZipStream(stored, CompressionMode.Decompress);
    }

    /// <summary>How much of the pin travels inside this binary, for an observed or expected string.</summary>
    public static string Describe(XvfFirmwarePin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);

        var carried = pin.Images.Count(Carries);
        return carried == pin.Images.Count
            ? string.Create(CultureInfo.InvariantCulture, $"all {carried} inside this binary")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{carried} of {pin.Images.Count} inside this binary, the rest fetched");
    }

    /// <summary>
    /// Image file name → manifest resource name, for every embedded image.
    /// </summary>
    /// <remarks>
    /// The key is the resource name with the prefix, any directory and the
    /// <see cref="CompressedSuffix"/> taken off, so it is exactly <see cref="XvfFirmwareImage.Name"/>
    /// and no caller has to know how the bytes are stored. Not keyed on the directory under
    /// <see cref="Prefix"/>, because the <c>usb/</c> and <c>recover/</c> split is upstream's layout
    /// rather than this binary's: the vendored directory is flat today and might mirror upstream
    /// tomorrow, and a lookup that depended on which would break on the day somebody tidied a
    /// folder. The three pinned names are distinct, and a name that somehow matched the wrong bytes
    /// would be refused by the digest check on the way to the card. A resource under this prefix
    /// that is <i>not</i> compressed is skipped rather than guessed at, so a future raw one
    /// announces itself as absent rather than as corrupt.
    /// </remarks>
    private static Dictionary<string, string> Load()
    {
        var assembly = typeof(XvfVendoredFirmware).Assembly;
        var carried = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in assembly.GetManifestResourceNames())
        {
            var normalised = name.Replace('\\', '/');
            if (!normalised.StartsWith(Prefix, StringComparison.Ordinal)
                || !normalised.EndsWith(CompressedSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            carried[normalised[(normalised.LastIndexOf('/') + 1)..^CompressedSuffix.Length]] = name;
        }

        return carried;
    }
}
