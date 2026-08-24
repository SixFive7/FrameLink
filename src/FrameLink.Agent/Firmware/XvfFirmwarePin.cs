#if FRAMELINK_CONTROL
namespace FrameLink.Control.Firmware;
#else
namespace FrameLink.Agent.Firmware;
#endif

/// <summary>What one pinned DFU image is for.</summary>
/// <remarks>
/// <b>One role, where there used to be three.</b> The <c>Fallback</c> and <c>Recovery</c> roles were
/// removed on 2026-08-24 with the recovery kit itself — see <see cref="XvfFirmwarePin"/>'s remarks
/// for what was believed, what was measured and why they went. The enum survives the removal because
/// it is the thing that names an image's job in three places at once: this pin, the harness's
/// cross-check parser in <c>tools/harness/flh/flash.py</c>, and the review ledger. A second image
/// joining the pin should be a data edit and a new role, not a redesign.
/// </remarks>
public enum XvfFirmwareRole
{
    /// <summary>The version the fleet converges on. The only image anything may flash.</summary>
    Target,
}

/// <summary>One pinned DFU image, and what it must hash to.</summary>
/// <param name="Name">The file name upstream publishes, kept unchanged on the frame.</param>
/// <param name="Directory">Which directory inside <c>xmos_firmwares/</c> it comes from.</param>
/// <param name="Commit">The full commit SHA that last touched this file, which the URL carries.</param>
/// <param name="Sha256">Its measured digest.</param>
/// <param name="SizeBytes">Its exact length, which bounds the download.</param>
/// <param name="Role">What it is for.</param>
/// <param name="Version">The firmware version it carries, in <c>xvf_host</c>'s own spelling (<c>2 1 0</c>).</param>
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

    /// <summary>Where it sits on the frame, relative to <c>XvfFirmwareInstaller.TargetDirectory</c>.</summary>
    public string LocalPath => Directory + "/" + Name;
}

/// <summary>
/// The XVF3800 DFU image this build pins, carried on every frame and verified by digest.
/// </summary>
/// <remarks>
/// <para>
/// <b>This file is the only definition of the pin, and it is compiled into both programs.</b> It
/// used to exist three times — here, again in the Fleet Manager as <c>ArrayFlashPin</c>'s four
/// <c>Target*</c> constants, and again in <c>tools/harness/flh/flash.py</c> — with a test holding the
/// first two equal string by string. Two of those are now one: <c>FrameLink.Control.csproj</c>
/// <c>&lt;Compile Include&gt;</c>s this file across the project boundary, exactly as it already
/// <c>&lt;EmbeddedResource&gt;</c>s the agent's <c>fl-agent.service</c>, so the Fleet Manager and the
/// agent are compiled from the same bytes rather than checked against each other. The namespace is
/// the one difference: the two programs are separate Native AOT binaries that reference each other
/// not at all, and the test project references both, so one fully-qualified name in two assemblies
/// would be ambiguous at every use. <c>FRAMELINK_CONTROL</c> is defined only by the Fleet Manager's
/// project and moves this file's types into its own namespace there. The harness's copy stays, and
/// stays deliberate: it runs on a workstation with no .NET SDK, it parses this file at run time and
/// refuses to write anything when the two disagree, and a second record that <i>refuses</i> is worth
/// more than one that cannot check.
/// </para>
/// <para>
/// <b>"Latest" is a pin a human moves, not a thing the fleet chases</b> (decision 91, §7.1). The
/// upstream repository has zero releases and zero tags, so there is no version number to compare
/// against and nothing that could answer "is this newer". What there is, is the same pin
/// <c>XvfHostReleasePin</c> uses: a <c>raw.githubusercontent.com</c> URL carrying a full commit SHA,
/// which is content-addressed, plus a measured SHA-256 as the second lock. Bumping it is a source
/// edit that goes through the <c>upstream-review.json</c> gate, and that is deliberate: this is the
/// one artifact in the build whose blast radius is a device that cannot be re-imaged from the card.
/// </para>
/// <para>
/// <b>The version string is not the identity, and that is measured rather than argued.</b>
/// <c>respeaker_xvf3800_usb_dfu_firmware_v2.0.10.bin</c> has been published <i>twice under one
/// name with different bytes</i> — commit <c>17bac32a</c> hashing <c>237f762a…</c> and commit
/// <c>aeacafab</c> hashing <c>81593709…</c>, 402,246 of 933,888 bytes differing, both answering
/// <c>VERSION 2 0 10</c> and both presenting <c>bcdDevice 020a</c>. No observable on the wire or in
/// the control tool tells them apart. So the pin names a <i>commit and a digest</i>, an
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
/// every pair falls between 28% and 48%. The frame agrees from its own side: the v1 reference
/// capture records the array's ALSA <c>Capture Channel Map</c> with <c>count 2</c>, and PipeWire
/// enumerating it as <i>Analog Stereo</i>. Flashing a six-channel or 48 kHz build would change the
/// frame's audio topology under every mixer resource in the catalog.
/// </para>
/// <para>
/// <b>The recovery kit is gone, and this is the account of it rather than its absence.</b> Until
/// 2026-08-24 this pin named three images: the target, a v2.0.6 <i>fallback</i>, and Seeed's
/// 4 MiB all-<c>0xFF</c> <i>erase</i> image <c>4mb_all_ff.bin</c>. The flash refused to write
/// without both of the other two present and hashing to their pins. <b>What was believed</b> was
/// that an interrupted or rejected write is repaired by erasing the Upgrade partition and writing a
/// known-good older firmware back, so both files had to be on the card before they were wanted —
/// fetching them at the moment of need means fetching them onto a frame that is already in trouble.
/// <b>What was then measured</b>, on 2026-08-24 against XMOS's and Seeed's own sources and recorded
/// in <c>reference/xvf3800-recovery-model.md</c>, contradicts every load-bearing part of that:
/// a DFU download <i>already</i> erases the whole upgrade section before it writes
/// (<c>lib_dfu</c>: <i>"on receiving the first DFU_DNLOAD command, the device starts to erase
/// FLASH_MAX_UPGRADE_SIZE bytes of the upgrade section"</i>), so a separate erase has nothing to do;
/// Seeed's own documented recovery is <i>enter Safe Mode, flash the firmware</i> with no erase step
/// at all, and <c>all_ff</c> appears nowhere in the wiki, the DFU guide or the changelog — its
/// entire documentation is one GitHub issue comment; the failure it was published for is a
/// configuration corrupted by <c>SAVE_CONFIGURATION</c>, which the maintainer says was fixed in
/// firmware from v2.0.9 and which this repository cannot cause because it sends that command
/// nowhere; and XMOS documents the opposite of the thing the erase was kept for —
/// <i>"Another download operation may be reattempted."</i> The v2.0.6 fallback had even less behind
/// it: <c>git log -S</c> traces <c>XvfFirmwareRole.Fallback</c> to exactly one commit, whose
/// thirty-line message never mentions 2.0.6, a fallback or a recovery pair, and neither upstream nor
/// Seeed nor decision 91 ever recommended that version — the maintainer's own recovery advice tracks
/// the <i>newest</i> image. <b>So the pair went</b>, on the operator's decision: the fleet checks the
/// hardware, checks the running firmware, and writes the one target image when it is outdated. Two
/// things are given up and both are named. A second known-good image on the card would be insurance
/// against the pinned target being bad on a board nobody has met — real, unquantified, and never
/// observed here or upstream — and putting one back is now a pin bump and a release rather than a
/// visit. And the erase image was the only answer to a corrupted DataPartition, which is a failure
/// this product cannot reach. <b>What is gained is not a tidy-up</b>: the pre-flight that required
/// the pair was the one and only reason a frame with no network could not flash, because the target
/// is vendored and the other two were fetched — so removing it is what finally makes the vendoring
/// mean what it says.
/// </para>
/// <para>
/// <b>The target is vendored and embedded, and it is now the whole pin.</b> The bytes of
/// <c>respeaker_xvf3800_usb_dfu_firmware_v2.1.0.bin</c> are in this repository under
/// <c>vendor/respeaker-xvf3800/</c> and compiled into the agent binary, so a frame that has an agent
/// has the firmware and can flash with no network at all. <c>XvfVendoredFirmware</c> is the accessor
/// and <c>vendor/respeaker-xvf3800/NOTICE.md</c> records where the bytes came from, unmodified, and
/// what they hash to.
/// </para>
/// <para>
/// <b>What has not changed is the licence position that produced "fetched, never vendored".</b> The
/// upstream repository still carries no licence file at all, which was decision 63's reasoning for
/// the control tool and was applied unchanged to these images when they were pinned. The
/// <c>upstream-review.json</c> entry records that reasoning, the notice beside the bytes records
/// the redistribution as it now stands, and neither this file nor the accessor re-decides it.
/// Upstream's own warning that a GitHub "save as" corrupts these files is answered the same way in
/// both directions: every byte that reaches the card is hashed against the digest below, whether it
/// came off the network or out of this executable.
/// </para>
/// <para>
/// <b>Verified live 2026-08-23</b>, and every value below was measured rather than recalled: the URL
/// answered 200 at the commit named beside it, and the downloaded bytes are the length stated and
/// hash to the digest stated. The ledger entry <c>xvf-firmware-target</c> in
/// <c>upstream-review.json</c> is §7.1's record of that review, and a test ties it to this file.
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

    /// <summary>The pin this build carries, verified 2026-08-23.</summary>
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
        ],
        ReviewedUtc = new DateTimeOffset(2026, 8, 23, 0, 0, 0, TimeSpan.Zero),
    };

    /// <summary>The GitHub account publishing the images.</summary>
    public required string Owner { get; init; }

    /// <summary>The repository they live in.</summary>
    public required string Repository { get; init; }

    /// <summary>Every image this build puts on a frame.</summary>
    /// <remarks>
    /// A list holding one image today. It stays a list because the resource that keeps the card in
    /// step verifies <i>every</i> pinned image and the accessor counts how many of them travel
    /// inside the binary, so a second image arriving is a data edit rather than a reshape.
    /// </remarks>
    public required IReadOnlyList<XvfFirmwareImage> Images { get; init; }

    /// <summary>When a human last checked this pin against upstream (§7.1's stamp).</summary>
    public required DateTimeOffset ReviewedUtc { get; init; }

    /// <summary>The one image anything is ever allowed to write to an array.</summary>
    public XvfFirmwareImage Target => Of(XvfFirmwareRole.Target);

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
