using System.Globalization;
using FrameLink.Agent.Firmware;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Resources;

/// <summary>
/// <c>firmware.xvf3800.image</c> — the pinned DFU image is on this frame and hashes to the pin.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a resource; the flash is not</b> (decision 91). What converges here is entirely on
/// the card: every pinned file exists, each is exactly the pinned bytes, and the Act that repairs
/// that is something this frame can perform. It has a real Act that can succeed, which is the test decision
/// 63 set and decision 90 applied — and unlike a firmware <i>version</i>, nothing about it depends
/// on hardware anybody has to touch. A frame with a 2.0.6 array and a frame with a 2.1.0 array both
/// reach <see cref="ResourceStatusKind.InSync"/> here, because the claim is about the images and
/// not about the array.
/// </para>
/// <para>
/// <b>Nothing here needs a network any more, and that is new.</b> The bytes of the one image
/// anything may write are compiled into this binary (see <see cref="XvfVendoredFirmware"/>), so on a
/// frame with no route at all the Act puts the whole pin on the card, verified, and reports
/// <see cref="XvfFirmwareInstallResult.Installed"/> rather than <c>Unreachable</c>. Until 2026-08-24
/// it could not: two more images were pinned — a v2.0.6 fallback and Seeed's all-<c>0xFF</c> erase
/// image — neither was vendored, and the flash refused without both, so an offline frame reliably
/// held the target and reliably could not use it. Both went with that pre-flight;
/// <see cref="XvfFirmwarePin"/> carries the account. The fetch path is kept, because a future pin
/// may name something this binary does not carry.
/// </para>
/// <para>
/// <b>It is the prerequisite for every safe flash, which is why it is in the graph and comes
/// first.</b> A DFU write of an unverified 933 KB file is strictly worse than no flash at all: a
/// truncated download would be pushed onto the array with nothing complaining, and the array is the
/// one component on this frame that cannot be repaired by rewriting the card. So the ordinary
/// convergence loop puts a digest-verified image on every frame in the fleet, unattended and with
/// no risk, and the interlocked flash beside the loop refuses to run unless it finds one.
/// </para>
/// <para>
/// <b>It converges the whole pin rather than one named file, and that survives the pin shrinking to
/// one.</b> This used to hold three images and the reasoning was that they were not independently
/// useful — the erase image and the fallback firmware were a recovery route with a hole in it if
/// either was missing. That pair is gone, so the argument no longer has to be made; the shape stays
/// because §2.2's granularity rule asks for the smallest independently verifiable setting and
/// "every image this build might write is present and correct" is still that setting, whether the
/// pin names one file or four.
/// </para>
/// <para>
/// <b>No dependency edge, like the tool it sits beside.</b> A verified download depends on nothing
/// this catalog owns. In particular it does not depend on <c>pkg.dfu-util</c>: the image being on
/// the card is worth having on a frame whose apt run failed, and an edge there would leave it
/// <see cref="ResourceStatusKind.Blocked"/> at exactly the moment somebody wanted to write it.
/// </para>
/// <para>
/// <b>Guarded on ALSA's presence, like every other resource in the audio block.</b> A machine with
/// no <c>/proc/asound</c> — a container, a workstation, §5.3's virtual agent — has no array to
/// flash and no reason to hold a megabyte for one.
/// </para>
/// </remarks>
public sealed class XvfFirmwareImageResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "firmware.xvf3800.image";

    private readonly ISystemFiles _files;
    private readonly XvfFirmwareInstaller _installer;

    /// <summary>Creates the resource.</summary>
    public XvfFirmwareImageResource(ISystemFiles files, XvfFirmwareInstaller installer)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(installer);

        _files = files;
        _installer = installer;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public string Detected =>
        "The software this frame would need to update its microphone unit is not on the frame.";

    /// <inheritdoc/>
    public string WhyItMatters =>
        "Without it nobody can update the microphone unit here, and the frame would have to fetch the file at the worst possible moment.";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var expected = _installer.Describe();

        if (!_files.FileExists(AlsaCards.CardsPath))
        {
            return new ResourceObservation(true, expected, "no sound hardware on this machine");
        }

        var faults = await _installer.UnverifiedAsync(cancellationToken).ConfigureAwait(false);

        return faults.Count == 0
            ? new ResourceObservation(true, expected, "every pinned image is present and matches its digest")
            : new ResourceObservation(false, expected, string.Join("; ", faults));
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var pin = _installer.Pin;
        var result = await _installer.InstallAsync(cancellationToken).ConfigureAwait(false);

        var carried = pin.Images.Count(XvfVendoredFirmware.Carries);

        // One sentence with both counts in it rather than a branch per case: the ratio is the
        // thing a reader wants, it stays true whether the pin names one image or four and whether
        // this binary carries all of them or some, and an operator reading an event should not have
        // to know which arrangement this build has.
        var change = string.Create(
            CultureInfo.InvariantCulture,
            $"put {pin.Images.Count} pinned DFU images into {XvfFirmwareInstaller.TargetDirectory} — "
            + $"{carried} out of this agent's own binary, {pin.Images.Count - carried} fetched from "
            + $"raw.githubusercontent.com/{pin.Owner}/{pin.Repository} — verifying sha256 on every byte");

        return new ResourceAction(
            result is XvfFirmwareInstallResult.Installed or XvfFirmwareInstallResult.AlreadyInstalled
                ? change
                : $"{change} (refused: {result})",
            "Putting the microphone unit's own software onto this frame, taking what the agent already carries inside itself and downloading anything it does not, and checking every byte arrived intact.");
    }
}
