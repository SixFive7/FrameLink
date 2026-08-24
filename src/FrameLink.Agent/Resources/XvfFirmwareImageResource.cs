using System.Globalization;
using FrameLink.Agent.Firmware;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Resources;

/// <summary>
/// <c>firmware.xvf3800.image</c> — the pinned DFU images are on this frame and hash to the pin.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a resource; the flash is not</b> (decision 91). What converges here is entirely on
/// the card: three files exist, each is exactly the pinned bytes, and the Act that repairs that is
/// something this frame can perform. It has a real Act that can succeed, which is the test decision
/// 63 set and decision 90 applied — and unlike a firmware <i>version</i>, nothing about it depends
/// on hardware anybody has to touch. A frame with a 2.0.6 array and a frame with a 2.1.0 array both
/// reach <see cref="ResourceStatusKind.InSync"/> here, because the claim is about the images and
/// not about the array.
/// </para>
/// <para>
/// <b>The target image needs no network at all; the recovery pair still does.</b> The bytes of the
/// one image anything may write are compiled into this binary (see
/// <see cref="XvfVendoredFirmware"/>), so on a frame with no route the Act still puts the target on
/// the card, verified, and then reports honestly that it could not fetch the other two. That is a
/// real asymmetry rather than a rounding error: the flash's pre-flight refuses without the recovery
/// pair, so <i>flashing</i> offline needs those two vendored as well, and whether they join the
/// target is an open question the operator has not answered. What is settled is that the image is
/// on the card before anybody wants it, and that getting it there costs no network.
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
/// <b>The recovery pair is part of the resource rather than a separate one.</b> The three images are
/// not independently useful: an array that took a bad flash needs the blank <c>4mb_all_ff.bin</c>
/// and the known-good fallback <i>together</i>, in that order, and a frame carrying one without the
/// other has a recovery route with a hole in it. §2.2's granularity rule asks for the smallest
/// independently verifiable setting, and "the images needed to flash and to undo a flash are all
/// present" is that setting — splitting it would produce three resources that are only ever right
/// or wrong at the same time, and a frame that could report two of them green while the recovery
/// route was unusable.
/// </para>
/// <para>
/// <b>No dependency edge, like the tool it sits beside.</b> A verified download depends on nothing
/// this catalog owns. In particular it does not depend on <c>pkg.dfu-util</c>: the images being on
/// the card is worth having on a frame whose apt run failed, and an edge there would leave the
/// recovery route <see cref="ResourceStatusKind.Blocked"/> at exactly the moment somebody wanted it.
/// </para>
/// <para>
/// <b>Guarded on ALSA's presence, like every other resource in the audio block.</b> A machine with
/// no <c>/proc/asound</c> — a container, a workstation, §5.3's virtual agent — has no array to
/// flash and no reason to hold six megabytes for one.
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
        "The software this frame would need to update its microphone unit, or to put it back if an update went wrong, is not on the frame.";

    /// <inheritdoc/>
    public string WhyItMatters =>
        "Without it nobody can repair the microphone unit here, and the frame would have to fetch the files at the worst possible moment.";

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
        // thing a reader wants, it stays true if the other two images are ever vendored too, and
        // an operator reading an event should not have to know which arrangement this build has.
        var change = string.Create(
            CultureInfo.InvariantCulture,
            $"put {pin.Images.Count} pinned DFU images into {XvfFirmwareInstaller.TargetDirectory} — "
            + $"{carried} out of this agent's own binary, {pin.Images.Count - carried} fetched from "
            + $"raw.githubusercontent.com/{pin.Owner}/{pin.Repository} — verifying sha256 on every byte");

        return new ResourceAction(
            result is XvfFirmwareInstallResult.Installed or XvfFirmwareInstallResult.AlreadyInstalled
                ? change
                : $"{change} (refused: {result})",
            "Putting the microphone unit's own software and the files needed to undo a bad update onto this frame, taking what the agent already carries inside itself and downloading the rest, and checking every byte arrived intact.");
    }
}
