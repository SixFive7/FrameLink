using System.Globalization;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Resources;

namespace FrameLink.Agent.Firmware;

/// <summary>Why an image install did not happen.</summary>
public enum XvfFirmwareInstallResult
{
    /// <summary>Every pinned image is in place and hashes to the pin.</summary>
    Installed,

    /// <summary>Every pinned image was already in place; nothing was fetched.</summary>
    AlreadyInstalled,

    /// <summary>Upstream could not be reached, or answered something unusable.</summary>
    Unreachable,

    /// <summary>A download was not the length the pin states.</summary>
    SizeMismatch,

    /// <summary>A download did not hash to the pinned digest.</summary>
    ChecksumMismatch,

    /// <summary>A staging file or a rename failed.</summary>
    WriteFailed,
}

/// <summary>
/// Puts the pinned DFU images on the card, and re-hashes them every time anybody asks.
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <see cref="XvfHostInstaller"/> and over the same
/// <see cref="VerifiedFetch"/> core, because the two artifacts come from one publisher over one URL
/// shape and a second copy of that loop would eventually be missing one of its properties. What is
/// different here is what the properties are <i>for</i>: an unverified <c>xvf_host</c> produces a
/// diagnostic that does not work, and an unverified 933 KB firmware image produces a microphone
/// array that has to be recovered by hand. <b>A DFU write of an unverified image is strictly worse
/// than no flash at all</b>, so this class exists before anything that could perform one.
/// </para>
/// <para>
/// <b>The digest is re-read from disk, never remembered</b>, and embedding the bytes makes that
/// <i>more</i> load-bearing rather than less. <see cref="VerifyAsync"/> is called twice for every
/// flash — once by the resource that keeps the images in sync, and again by the flash itself in the
/// instant before <c>dfu-util</c> starts — because a record that an install succeeded would outlive
/// the bytes it describes, and the reader that matters is the one holding the file open. An image
/// that came out of this executable has still travelled to a file on an SD card, and everything
/// that can happen to a file on an SD card can happen to it there: a truncated write, a power cut
/// between the copy and the flash, a hand staging something else over it. Neither on-disk check is
/// derivable from anything the binary knows about itself.
/// </para>
/// <para>
/// <b>Where the bytes come from is chosen per image, and the card cannot tell.</b>
/// <see cref="XvfVendoredFirmware"/> answers for whatever this build carries and
/// <see cref="VerifiedFetch"/> for the rest, but both funnel through one length bound, one digest
/// comparison and one fsync-then-rename — so an installed image means exactly what it meant before
/// anything was embedded, and a frame cannot end up with a weaker guarantee by being offline.
/// </para>
/// </remarks>
public sealed class XvfFirmwareInstaller
{
    /// <summary>Suffix of the file each download is staged into before the rename.</summary>
    public const string StagingSuffix = VerifiedFetch.StagingSuffix;

    /// <summary>Where the images go, mirroring upstream's own two directories.</summary>
    /// <remarks>
    /// Under the agent's state directory and never the login user's home, so a person staging an
    /// image by hand during an attended bench session cannot be mistaken for the agent having
    /// fetched a verified one.
    /// </remarks>
    public const string TargetDirectory = XvfHost.AgentDirectory + "/xmos_firmwares";

    private const UnixFileMode ImageMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

    private readonly ISystemFiles _files;
    private readonly IXvfHostDownload _download;
    private readonly IAgentLog _log;

    /// <summary>Creates an installer that fills the agent-owned image directory.</summary>
    public XvfFirmwareInstaller(
        ISystemFiles files,
        IXvfHostDownload download,
        IAgentLog log,
        XvfFirmwarePin? pin = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(log);

        _files = files;
        _download = download;
        _log = log;
        Pin = pin ?? XvfFirmwarePin.Current;
    }

    /// <summary>The images this installer puts in place.</summary>
    public XvfFirmwarePin Pin { get; }

    /// <summary>Where <paramref name="image"/> lives on this frame.</summary>
    public static string PathOf(XvfFirmwareImage image) => TargetDirectory + "/" + image.LocalPath;

    /// <summary>Whether the file on disk is exactly the pinned bytes of <paramref name="image"/>.</summary>
    public async Task<bool> VerifyAsync(XvfFirmwareImage image, CancellationToken cancellationToken)
    {
        var digest = await VerifiedFetch
            .DigestAsync(_files, _log, PathOf(image), cancellationToken)
            .ConfigureAwait(false);

        return string.Equals(digest, image.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Which pinned images are absent or wrong, each described as a person would read it. Empty
    /// means the directory matches the pin exactly.
    /// </summary>
    public async Task<IReadOnlyList<string>> UnverifiedAsync(CancellationToken cancellationToken)
    {
        var faults = new List<string>();

        foreach (var image in Pin.Images)
        {
            var digest = await VerifiedFetch
                .DigestAsync(_files, _log, PathOf(image), cancellationToken)
                .ConfigureAwait(false);

            if (digest is null)
            {
                faults.Add($"{image.LocalPath} is missing");
            }
            else if (!string.Equals(digest, image.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                faults.Add($"{image.LocalPath} is a different file, sha256 {digest}");
            }
        }

        return faults;
    }

    /// <summary>Installs every pinned image that is not already right, from inside this binary
    /// where it can be and over the network where it cannot.</summary>
    /// <remarks>
    /// <b>Embedded first, network second, and the ordering is the guarantee.</b> Sorting on
    /// <see cref="XvfVendoredFirmware.Carries"/> rather than walking
    /// <see cref="XvfFirmwarePin.Images"/> in its own order means a frame with no route still
    /// receives everything this binary carries, whatever order the pin happens to list them in —
    /// today the pin names one image and it would work either way, and the day somebody adds a
    /// second and lists it first it would silently stop working. The sort is stable, so within each
    /// group the pin's order is kept.
    /// </remarks>
    public async Task<XvfFirmwareInstallResult> InstallAsync(CancellationToken cancellationToken)
    {
        var installed = 0;

        try
        {
            foreach (var image in Pin.Images.OrderBy(image => XvfVendoredFirmware.Carries(image) ? 0 : 1))
            {
                var path = PathOf(image);
                _files.EnsureDirectory(path[..path.LastIndexOf('/')]);

                if (await VerifyAsync(image, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                var placed = await PlaceAsync(image, path, cancellationToken).ConfigureAwait(false);

                if (placed != VerifiedFetchResult.Installed)
                {
                    return placed switch
                    {
                        VerifiedFetchResult.Unreachable => XvfFirmwareInstallResult.Unreachable,
                        VerifiedFetchResult.SizeMismatch => XvfFirmwareInstallResult.SizeMismatch,
                        _ => XvfFirmwareInstallResult.ChecksumMismatch,
                    };
                }

                installed++;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Fail($"The array firmware images could not be written: {exception.Message}");
            return XvfFirmwareInstallResult.WriteFailed;
        }

        return installed == 0 ? XvfFirmwareInstallResult.AlreadyInstalled : XvfFirmwareInstallResult.Installed;
    }

    /// <summary>How the pin reads in one line, for an observed or expected string.</summary>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Pin.Images.Count} pinned images under {TargetDirectory} ({XvfVendoredFirmware.Describe(Pin)}), target firmware {Pin.Target.Version} ({Pin.Target.Sha256[..12]})");

    /// <summary>Puts one image on the card, from this binary if it carries it and over the
    /// network otherwise.</summary>
    /// <remarks>
    /// <b>An embedded copy that fails the digest falls through to the network rather than
    /// stopping.</b> That can only mean this executable's own resource region is damaged — the
    /// suite hashes the decompressed resource against the pin at build time, so neither a wrong
    /// file nor a compressor that disagrees with the decompressor can ship — and the honest
    /// response to a damaged binary on a frame that does have a network is to fetch the image and
    /// let the loud refusal <see cref="VerifiedFetch"/> already wrote stand in the journal.
    /// Refusing outright would turn one corrupt read into a frame that cannot flash at all, which
    /// is strictly worse and equally silent.
    /// </remarks>
    private async Task<VerifiedFetchResult> PlaceAsync(
        XvfFirmwareImage image,
        string path,
        CancellationToken cancellationToken)
    {
        var embedded = XvfVendoredFirmware.Open(image);

        if (embedded is not null)
        {
            var placed = await VerifiedFetch
                .FromAsync(
                    _files,
                    _log,
                    embedded,
                    XvfVendoredFirmware.Origin,
                    path,
                    image.Sha256,
                    image.SizeBytes,
                    ImageMode,
                    cancellationToken)
                .ConfigureAwait(false);

            if (placed == VerifiedFetchResult.Installed)
            {
                return placed;
            }
        }

        return await VerifiedFetch
            .IntoAsync(
                _files,
                _download,
                _log,
                Pin.UrlOf(image),
                path,
                image.Sha256,
                image.SizeBytes,
                ImageMode,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
