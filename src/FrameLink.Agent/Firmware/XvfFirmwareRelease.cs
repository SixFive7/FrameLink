using System.Globalization;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Resources;

namespace FrameLink.Agent.Firmware;

/// <summary>What one pinned DFU image is for.</summary>
public enum XvfFirmwareRole
{
    /// <summary>The version the fleet converges on. The only image anything may flash.</summary>
    Target,

    /// <summary>The version to put back by hand when a flash has gone wrong.</summary>
    Fallback,

    /// <summary>The all-<c>0xFF</c> blank image an interrupted flash is erased with.</summary>
    Recovery,
}

/// <summary>One pinned DFU image, and what it must hash to.</summary>
/// <param name="Name">The file name upstream publishes, kept unchanged on the frame.</param>
/// <param name="Directory">Which directory inside <c>xmos_firmwares/</c> it comes from.</param>
/// <param name="Commit">The full commit SHA that last touched this file, which the URL carries.</param>
/// <param name="Sha256">Its measured digest.</param>
/// <param name="SizeBytes">Its exact length, which bounds the download.</param>
/// <param name="Role">What it is for.</param>
/// <param name="Version">
/// The firmware version it carries, in <c>xvf_host</c>'s own spelling (<c>2 1 0</c>), or empty for
/// the recovery image, which is not firmware at all.
/// </param>
/// <param name="Purpose">One sentence a person reading a refusal can understand.</param>
public readonly record struct XvfFirmwareImage(
    string Name,
    string Directory,
    string Commit,
    string Sha256,
    long SizeBytes,
    XvfFirmwareRole Role,
    string Version,
    string Purpose)
{
    /// <summary>Where upstream keeps it, relative to the repository root.</summary>
    public string PathInRepository => "xmos_firmwares/" + Directory + "/" + Name;

    /// <summary>Where it sits on the frame, relative to <see cref="XvfFirmwareInstaller.TargetDirectory"/>.</summary>
    public string LocalPath => Directory + "/" + Name;
}

/// <summary>
/// The XVF3800 DFU images this build pins, fetched onto every frame and verified by digest.
/// </summary>
/// <remarks>
/// <para>
/// <b>"Latest" is a pin a human moves, not a thing the fleet chases</b> (decision 91, §7.1). The
/// upstream repository has zero releases and zero tags, so there is no version number to compare
/// against and nothing that could answer "is this newer". What there is, is the same pin
/// <see cref="XvfHostReleasePin"/> uses: a <c>raw.githubusercontent.com</c> URL carrying a full
/// commit SHA, which is content-addressed, plus a measured SHA-256 as the second lock. Bumping it
/// is a source edit that goes through the <c>upstream-review.json</c> gate, and that is deliberate:
/// this is the one artifact in the build whose blast radius is a device that cannot be re-imaged
/// from the card.
/// </para>
/// <para>
/// <b>The version string is not the identity, and that is measured rather than argued.</b>
/// <c>respeaker_xvf3800_usb_dfu_firmware_v2.0.10.bin</c> has been published <i>twice under one
/// name with different bytes</i> — commit <c>17bac32a</c> hashing <c>237f762a…</c> and commit
/// <c>aeacafab</c> hashing <c>81593709…</c>, 402,246 of 933,888 bytes differing, both answering
/// <c>VERSION 2 0 10</c> and both presenting <c>bcdDevice 020a</c>. No observable on the wire or in
/// the control tool tells them apart. So every pin here names a <i>commit and a digest</i>, an
/// authorisation to flash names the <i>digest</i>, and the version string is only ever used to
/// decide whether a flash would change anything.
/// </para>
/// <para>
/// <b>Which 2.1.0, and how that was settled.</b> Upstream publishes three of them —
/// <c>v2.1.0.bin</c>, <c>v2.1.0_16k6ch.bin</c> and <c>v2.1.0_48k2ch.bin</c> — and the unsuffixed
/// name is not self-evidently the right one. Four pieces of evidence agree that it is. Seeed's own
/// wiki states it directly for the 2.0.x line: <i>"Two firmware variants are available:
/// respeaker_xvf3800_usb_dfu_firmware_v2.0.x.bin, which provides 2-channel audio, and
/// respeaker_xvf3800_usb_dfu_firmware_6chl_v2.0.x.bin, which provides 6-channel audio. Both
/// firmware versions operate at a 16 kHz sampling rate"</i> — so unsuffixed means two channels at
/// 16 kHz, and the suffix names the departure. Upstream's own 2.0.8 changelog entry <i>adds</i> the
/// <c>ua-io16-6ch-sqr</c> profile, which is the six-channel one, against a base profile the array
/// on Frame #1 reports as <c>BLD_MSG ua-io16-sqr</c>. The 2.1.0 filenames spell both departures out
/// — <c>16k6ch</c> and <c>48k2ch</c> — leaving 16 kHz and two channels as the unsuffixed build.
/// And the maintainer names a suffix's profile outright in upstream issue 19 — <i>"This v2.0.9_48k
/// firmware is indeed built with the ua-io48-sqr configuration"</i> — which is the suffix-to-profile
/// mapping stated by the person who builds them rather than inferred from a filename. A fifth
/// corroboration stood here and is <b>withdrawn as falsified</b> (measured 2026-08-24): it argued
/// that <c>v2.1.0</c> and <c>v2.1.0_48k2ch</c> differing by 30.03% of bytes, against 46.17% for
/// <c>v2.1.0_16k6ch</c>, was what a sample-rate-only difference looks like. Recomputing all 45
/// pairwise differences shows the metric does not discriminate: <c>v2.0.9</c> against
/// <c>v2.0.9_48k</c>, the maintainer-confirmed rate-only pair, differs by 44.10%, while
/// <c>v2.0.7</c> against <c>v2.0.9</c> — same profile, different version — differs by 28.31%, and
/// every pair falls between 28% and 48%. version2.md decision 91 and
/// reference/xvf3800-board-revisions.md both withdrew it; this comment was the last place still
/// citing it. The frame agrees from its own side: the v1 reference capture records the
/// array's ALSA <c>Capture Channel Map</c> with <c>count 2</c>, and PipeWire enumerating it as
/// <i>Analog Stereo</i>. Flashing a six-channel or 48 kHz build would change the frame's audio
/// topology under every mixer resource in the catalog.
/// </para>
/// <para>
/// <b>The recovery pair ships with the target, and that is not belt-and-braces.</b> An interrupted
/// or rejected flash is repaired by erasing the Upgrade partition with the all-<c>0xFF</c> image and
/// writing a known-good firmware back — from Safe Mode, by hand, with somebody's finger on the Mute
/// button. Fetching either of those <i>at the moment they are needed</i> means fetching them onto a
/// frame whose operator is already having a bad day, possibly with no network. Five megabytes on a
/// card with 107 GB free is not a cost worth thinking about. <c>4mb_all_ff.bin</c> has had exactly
/// one commit in its life, so its pin will never move; v2.0.6 is the version both of this project's
/// arrays shipped with, and the version upstream issue #32 reports booting on every board revision
/// anyone has tried.
/// </para>
/// <para>
/// <b>The target is vendored and embedded; the other two are still fetched.</b> The bytes of
/// <c>respeaker_xvf3800_usb_dfu_firmware_v2.1.0.bin</c> are in this repository under
/// <c>vendor/respeaker-xvf3800/</c> and compiled into this binary, so a frame that has an agent has
/// the firmware and can flash with no network at all — which is the whole reason the vendoring
/// happened, since the one operation on a frame that cannot be undone should not depend on a route
/// to GitHub. <see cref="XvfVendoredFirmware"/> is the accessor and
/// <c>vendor/respeaker-xvf3800/NOTICE.md</c> records where the bytes came from, unmodified, and
/// what they hash to. The v2.0.6 fallback and <c>4mb_all_ff.bin</c> are <i>not</i> vendored;
/// whether they join it is an open question the operator has not answered, and until it is answered
/// a flash still needs a network on any frame that has not already fetched them — see
/// <c>ArrayFirmwareFlash</c>'s recovery-pair pre-flight, which refuses without them.
/// </para>
/// <para>
/// <b>What has not changed is the licence position that produced "fetched, never vendored".</b> The
/// upstream repository still carries no licence file at all, which was decision 63's reasoning for
/// the control tool and was applied unchanged to these images when they were pinned. The three
/// <c>upstream-review.json</c> entries record that reasoning, the notice beside the bytes records
/// the redistribution as it now stands, and neither this file nor the accessor re-decides it.
/// Upstream's own warning that a GitHub "save as" corrupts these files is answered the same way in
/// both directions: every byte that reaches the card is hashed against the digest below, whether it
/// came off the network or out of this executable.
/// </para>
/// <para>
/// <b>Verified live 2026-08-23</b>, and every value below was measured rather than recalled: each
/// URL answered 200 at the commit named beside it, the downloaded bytes are the lengths stated and
/// hash to the digests stated, and <c>4mb_all_ff.bin</c> was checked byte by byte to be entirely
/// <c>0xFF</c>. The ledger entries <c>xvf-firmware-target</c>, <c>xvf-firmware-fallback</c> and
/// <c>xvf-firmware-recovery</c> in <c>upstream-review.json</c> are §7.1's record of that review, and
/// a test ties them to this file.
/// </para>
/// </remarks>
public sealed record XvfFirmwarePin
{
    /// <summary>The profile a FrameLink frame runs — 16 kHz, two channels, square array.</summary>
    /// <remarks>
    /// What <c>xvf_host BLD_MSG</c> answers on Frame #1. Recorded as a constant because it is the
    /// one fact that decides which of three same-version files is the right one, and a future
    /// reader looking at three filenames has no other way to tell.
    /// </remarks>
    public const string Profile = "ua-io16-sqr";

    /// <summary>The pin this build fetches, verified 2026-08-23.</summary>
    public static XvfFirmwarePin Current { get; } = new()
    {
        Owner = "respeaker",
        Repository = "reSpeaker_XVF3800_USB_4MIC_ARRAY",
        Images =
        [
            new XvfFirmwareImage(
                "respeaker_xvf3800_usb_dfu_firmware_v2.1.0.bin",
                "usb",
                "183ef1ca6befd592da6c4c504259335f8bb3d097",
                "60fee566253489709946a77b3fece58fbeb64ea1455279031ec84a87ca7b78d6",
                933_888,
                XvfFirmwareRole.Target,
                "2 1 0",
                "the firmware version this fleet converges on"),
            new XvfFirmwareImage(
                "respeaker_xvf3800_usb_dfu_firmware_v2.0.6.bin",
                "usb",
                "ff421c45e1624f7b27da5e7f723a58cc69b3eb34",
                "c95fd3dec7597c72a24bc7e5212e6db136144956d5569f24b518ecfc1540ef09",
                933_888,
                XvfFirmwareRole.Fallback,
                "2 0 6",
                "the version to put back by hand if a flash goes wrong"),
            new XvfFirmwareImage(
                "4mb_all_ff.bin",
                "recover",
                "0b73b3ffe908fb262a20fcff9f27f5a126f3c0a9",
                "cd3517473707d59c3d915b52a3e16213cadce80d9ffb2b4371958fb7acb51a08",
                4_194_304,
                XvfFirmwareRole.Recovery,
                string.Empty,
                "the blank image that erases a half-written partition before the fallback goes on"),
        ],
        ReviewedUtc = new DateTimeOffset(2026, 8, 23, 0, 0, 0, TimeSpan.Zero),
    };

    /// <summary>The GitHub account publishing the images.</summary>
    public required string Owner { get; init; }

    /// <summary>The repository they live in.</summary>
    public required string Repository { get; init; }

    /// <summary>Every image this build puts on a frame.</summary>
    public required IReadOnlyList<XvfFirmwareImage> Images { get; init; }

    /// <summary>When a human last checked this pin against upstream (§7.1's stamp).</summary>
    public required DateTimeOffset ReviewedUtc { get; init; }

    /// <summary>The one image anything is ever allowed to write to an array.</summary>
    public XvfFirmwareImage Target => Of(XvfFirmwareRole.Target);

    /// <summary>The known-good firmware a person puts back by hand.</summary>
    public XvfFirmwareImage Fallback => Of(XvfFirmwareRole.Fallback);

    /// <summary>The blank image that erases a half-written Upgrade partition.</summary>
    public XvfFirmwareImage Recovery => Of(XvfFirmwareRole.Recovery);

    /// <summary>The single image with this role.</summary>
    /// <exception cref="InvalidOperationException">There is not exactly one.</exception>
    public XvfFirmwareImage Of(XvfFirmwareRole role) => Images.Single(image => image.Role == role);

    /// <summary>Where <paramref name="image"/> is served from.</summary>
    public Uri UrlOf(XvfFirmwareImage image) => new(
        "https://raw.githubusercontent.com/" + Owner + "/" + Repository + "/" + image.Commit
        + "/" + image.PathInRepository);

    /// <summary>
    /// The API call the ledger's <c>github-path-commit</c> probe makes for <paramref name="image"/>.
    /// </summary>
    /// <remarks>
    /// <b>The path is the file, never the directory, and that is the whole lesson of the twice-published
    /// 2.0.10.</b> A probe on <c>xmos_firmwares/usb</c> reports "moved" every time upstream adds any
    /// firmware for any product variant — three times this year — which is a gate nobody reads. A
    /// probe on the file path reports exactly one event, and it is the one that has actually
    /// happened to us: the bytes behind a name we pin being replaced.
    /// </remarks>
    public string CommitsUrlOf(XvfFirmwareImage image) =>
        "https://api.github.com/repos/" + Owner + "/" + Repository
        + "/commits?path=" + image.PathInRepository + "&per_page=1";

    /// <summary>The command a person runs to check this pin by hand.</summary>
    public string ReviewCommand =>
        string.Join(
            '\n',
            Images.Select(image =>
                "curl -fsSL " + CommitsUrlOf(image) + " | head"
                + "\ncurl -fsSL " + UrlOf(image) + " | sha256sum"));
}

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
    /// today the target is first and it would work either way, and the day somebody reorders the
    /// pin it would silently stop working. The sort is stable, so within each group the pin's order
    /// is kept.
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
