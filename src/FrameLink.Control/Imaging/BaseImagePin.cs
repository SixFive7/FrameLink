using System.Security.Cryptography;

namespace FrameLink.Control.Imaging;

/// <summary>
/// The upstream Raspberry Pi OS image every generated image is built from, pinned.
/// </summary>
/// <remarks>
/// <para>
/// <b>§7.1 says "everything floats, the build freezes it", and this is the freezing.</b> The base
/// image is an upstream dependency exactly like a NuGet package, with one difference that makes
/// it stricter rather than looser: the artifact is 2.8 GB of somebody else's filesystem, it is
/// written to a card, and it boots. "Whatever the mirror serves today" is not an acceptable input
/// to that. So the URL, the published archive digest, the decompressed image's own digest and its
/// exact byte length are all recorded here, in source, where changing them is a diff a person
/// reviews — and <see cref="VerifyAsync"/> refuses to let the generator touch a file that does
/// not match.
/// </para>
/// <para>
/// Two digests rather than one, because they answer different questions. <see cref="ArchiveSha256"/>
/// is the value Raspberry Pi Ltd publishes beside the download, so it is what a human checks the
/// pin <i>against</i>. <see cref="ImageSha256"/> is the digest of the decompressed image, which is
/// the file this code actually opens, and no vendor publishes it — it is measured here. Recording
/// only the published one would mean verifying a file the generator never reads.
/// </para>
/// <para>
/// <b>The geometry constants are documentation, never inputs.</b> Real offsets are read from the
/// image's own partition table by <see cref="ImageGeometry"/> on every build. They are recorded
/// because they are what makes the pin reviewable — a future pin whose partitions have moved is a
/// visible change here rather than a surprise in a tool argument — and because they let a test
/// assert that the geometry parser derives the measured numbers from a synthetic table.
/// </para>
/// <para>
/// <b>Why the directory layout is part of the pin.</b> <see cref="ImagePlan"/> writes into
/// <c>/usr/local/bin</c>, <c>/etc/systemd/system</c> and
/// <c>/etc/systemd/system/multi-user.target.wants</c> and never creates any of them, because
/// <c>debugfs mkdir</c> on an existing directory corrupts the filesystem (see
/// <see cref="ImageToolVerdict"/>). Relying on those directories existing is only safe because
/// the image they exist in is the image whose digest is checked first. All three were confirmed
/// present in this release on 2026-08-15.
/// </para>
/// </remarks>
public sealed record BaseImagePin
{
    /// <summary>
    /// Raspberry Pi OS Lite (Trixie / Debian 13) for arm64, verified 2026-08-15 @ 2026-06-18.
    /// </summary>
    /// <remarks>
    /// Every field below was measured on 2026-08-15 against the file the URL served:
    /// the published <c>.sha256</c> sidecar matched, the archive decompressed to exactly
    /// <see cref="ImageSizeBytes"/> bytes, and the digest, the partition table and the three
    /// target directories were read back out of the result.
    /// </remarks>
    public static BaseImagePin Current { get; } = new()
    {
        Release = "2026-06-18",
        ImageFileName = "2026-06-18-raspios-trixie-arm64-lite.img",
        ArchiveFileName = "2026-06-18-raspios-trixie-arm64-lite.img.xz",
        ArchiveUrl = new Uri(
            "https://downloads.raspberrypi.com/raspios_lite_arm64/images/"
            + "raspios_lite_arm64-2026-06-19/2026-06-18-raspios-trixie-arm64-lite.img.xz"),
        ArchiveSha256 = "acff736ca7945e3b305f07cda4abdb870910e12634991da69783611756e381b3",
        ArchiveSizeBytes = 524_875_608,
        ImageSha256 = "e235fd24fc5f039c08daba7d3abc04aecc7313f979d16d2a3fdad29dd44c33a9",
        ImageSizeBytes = 2_977_955_840,
        ReviewedUtc = new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero),
        BootPartitionOffsetBytes = 8_388_608,
        BootPartitionLengthBytes = 536_870_912,
        RootPartitionOffsetBytes = 545_259_520,
        RootPartitionLengthBytes = 2_432_696_320,
    };

    /// <summary>Upstream's release date, as it names it.</summary>
    public required string Release { get; init; }

    /// <summary>Filename of the decompressed image the generator reads.</summary>
    public required string ImageFileName { get; init; }

    /// <summary>Filename of the published archive, as downloaded.</summary>
    public required string ArchiveFileName { get; init; }

    /// <summary>Where the archive is published.</summary>
    public required Uri ArchiveUrl { get; init; }

    /// <summary>The digest Raspberry Pi Ltd publishes beside the archive. What a human reviews against.</summary>
    public required string ArchiveSha256 { get; init; }

    /// <summary>Length of the published archive in bytes.</summary>
    public required long ArchiveSizeBytes { get; init; }

    /// <summary>Digest of the decompressed image. What the generator verifies.</summary>
    public required string ImageSha256 { get; init; }

    /// <summary>Length of the decompressed image in bytes.</summary>
    public required long ImageSizeBytes { get; init; }

    /// <summary>When a human last reviewed this pin against upstream (§7.1's stamp).</summary>
    public required DateTimeOffset ReviewedUtc { get; init; }

    /// <summary>Measured byte offset of the FAT boot partition. Documentation; never an input.</summary>
    public required long BootPartitionOffsetBytes { get; init; }

    /// <summary>Measured length of the FAT boot partition.</summary>
    public required long BootPartitionLengthBytes { get; init; }

    /// <summary>Measured byte offset of the ext4 root partition. Documentation; never an input.</summary>
    public required long RootPartitionOffsetBytes { get; init; }

    /// <summary>Measured length of the ext4 root partition.</summary>
    public required long RootPartitionLengthBytes { get; init; }

    /// <summary>The command that turns the published archive into the file this pin expects.</summary>
    /// <remarks>
    /// Shown to the operator when the base image is missing. Decompressing is deliberately not
    /// done in-process: <c>.xz</c> has no decoder in the base class library, so doing it here
    /// would mean adding a dependency to a Native AOT server in order to save an operator one
    /// command they run once per pin.
    /// </remarks>
    public string PreparationCommand =>
        $"curl -fLO {ArchiveUrl} && echo '{ArchiveSha256}  {ArchiveFileName}' | sha256sum -c - && xz -d {ArchiveFileName}";

    /// <summary>
    /// The cheap half of <see cref="VerifyAsync"/>: is a file of the right name and length there?
    /// </summary>
    /// <remarks>
    /// This is what a status route may call. Hashing 2.8 GB takes long enough that a console
    /// polling every few seconds would keep a disk saturated answering a question whose real
    /// answer is only needed once, at the moment a build starts — which is where
    /// <see cref="VerifyAsync"/> is called and where a mismatch actually stops something.
    /// </remarks>
    /// <returns>Null when a plausible base image is present, otherwise what is wrong.</returns>
    public string? InspectWithoutHashing(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        var info = new FileInfo(imagePath);
        if (!info.Exists)
        {
            return $"The pinned base image {ImageFileName} is not in {Path.GetDirectoryName(imagePath)}.";
        }

        return info.Length == ImageSizeBytes
            ? null
            : $"{ImageFileName} is {info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)} bytes; "
                + $"the pin expects {ImageSizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)}.";
    }

    /// <summary>
    /// Checks a candidate base image against this pin, without modifying it.
    /// </summary>
    /// <remarks>
    /// Length first, because it is a stat call that rejects a truncated download for free, and
    /// because hashing 2.8 GB to learn something a file length already proved is a minute of an
    /// operator's life. Both are required to pass; neither alone is enough.
    /// </remarks>
    /// <param name="imagePath">Path of the decompressed image on disk.</param>
    /// <param name="cancellationToken">Abandons the hash.</param>
    /// <returns>Null when the file matches the pin, otherwise why it does not.</returns>
    public async Task<string?> VerifyAsync(string imagePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        var info = new FileInfo(imagePath);
        if (!info.Exists)
        {
            return $"There is no base image at {imagePath}.";
        }

        if (info.Length != ImageSizeBytes)
        {
            return $"The base image at {imagePath} is {info.Length} bytes; the pinned "
                + $"{ImageFileName} is {ImageSizeBytes}. It is truncated, or it is a different release.";
        }

        string digest;
        await using (var stream = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            digest = Convert.ToHexStringLower(hash);
        }

        return string.Equals(digest, ImageSha256, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"The base image at {imagePath} hashes to {digest}; the pin expects {ImageSha256}. "
                + "Nothing has been written to it.";
    }
}
