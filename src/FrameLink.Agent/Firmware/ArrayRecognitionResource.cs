using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;

namespace FrameLink.Agent.Firmware;

/// <summary>
/// <c>firmware.xvf3800.recognised</c> — the microphone unit on this frame is one this build has
/// been told about, checked in the graph rather than beside it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a gate, and the graph is where the operator put it.</b> The ladder itself lives in
/// <see cref="ArrayHardwareGate"/> and is a pure function of what a frame can read; this class is
/// the half that makes it a <i>resource</i>, so an unrecognised unit is drift on a rung of §2.2's
/// DAG, escalates, and by decision 68 stops the whole pass. The frame then stops being a photo
/// frame and shows what it found until a person comes — which is the consequence the operator was
/// asked about and accepted, because a fleet that quietly declines to flash an unknown board and
/// carries on looking well is a fleet where nobody ever finds out.
/// </para>
/// <para>
/// <b>It has no Act, and it says so structurally rather than pretending.</b> §2.3's contract is
/// Observe → Compare → Act (only on drift) → Verify, and there is no command on earth that turns an
/// unrecognised microphone unit into a recognised one — the fix is a person establishing what the
/// hardware is and a maintainer adding it to <see cref="ArrayHardwareGate.Allowlist"/>, which is a
/// release. This is exactly the shape decision 90 removed <c>firmware.xvf3800.version</c> for, and
/// the objection then was not to the shape but to the <i>cost</i>: a resource that cannot act
/// spends three attempts and three reboots on its way to a conclusion it reached on the first
/// Observe. <see cref="IResource.IsGate"/> is the answer to that half — the loop takes a drifted
/// gate straight to §2.5 rung 2 with the budget declared spent, no Act, and no reboot, exactly as
/// it already does for §2.6's conflict drift. <see cref="ActAsync"/> throws, because a fake Act
/// that returned "nothing to do" would be a lie the loop would believe.
/// </para>
/// <para>
/// <b>What it depends on, and what that buys.</b> <c>tool.xvf-host.installed</c>, because the
/// ladder cannot read a build configuration without it. A frame missing the tool is therefore
/// <see cref="ResourceStatusKind.Blocked"/> behind something the reconciler will fix by itself
/// rather than escalated on something nobody needs to come out for — which is the right answer, and
/// it is why rung 5 of the ladder almost never fires here. It still fires in
/// <see cref="ArrayFirmwareFlash"/>'s pre-flight, where there is no dependency to block on and the
/// next thing that would happen is a 933 KB write.
/// </para>
/// <para>
/// <b>The pre-flight check does not go away because this exists, and that is not duplication.</b>
/// This resource says what was true at the last pass; the pre-flight says what is true in the
/// second before <c>dfu-util</c> starts, and a unit can be unplugged and another one plugged in
/// between the two. Both call the same ladder and neither owns it.
/// </para>
/// </remarks>
public sealed class ArrayRecognitionResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "firmware.xvf3800.recognised";

    private readonly ISystemFiles _files;
    private readonly XvfHost _tool;
    private readonly FleetValues _values;
    private readonly XvfFirmwarePin _pin;

    private ArrayGateRuling? _last;

    /// <summary>Creates the gate over one frame's microphone unit.</summary>
    public ArrayRecognitionResource(
        ISystemFiles files,
        XvfHost tool,
        FleetValues values,
        XvfFirmwarePin? pin = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(values);

        _files = files;
        _tool = tool;
        _values = values;
        _pin = pin ?? XvfFirmwarePin.Current;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public bool IsGate => true;

    /// <summary>What the ladder concluded on the last Observe, or null before the first.</summary>
    public ArrayGateRuling? Ruling => _last;

    /// <inheritdoc/>
    /// <remarks>
    /// The ladder's own headline once it has run, because "the microphone unit is not one this
    /// build recognises" is true of ten different problems with ten different answers and the
    /// screen has room for the one that happened.
    /// </remarks>
    public string Detected => _last is { MayWrite: false } ruling
        ? ruling.Headline
        : "This frame's microphone bar is not one this version of FrameLink has been told about.";

    /// <inheritdoc/>
    public string WhyItMatters =>
        "Until somebody has looked at it, this frame will not change anything about its microphone — "
        + "and it will not go back to showing photographs, because a frame nobody notices is a frame "
        + "nobody fixes.";

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn => [XvfHostToolResource.ResourceName];

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Everything the ladder read goes in the observed half, whole.</b> §2.5 renders the delta on
    /// the frame's own screen and in the Fleet Manager's device row, and this is the one screen in
    /// the product where density beats brevity: the person reading it is going to be asked to relay
    /// it, and a message that has been trimmed to fit is a message they have to be asked follow-up
    /// questions about. It carries the plain half they can act on and the technical block they can
    /// photograph.
    /// </remarks>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var scan = await ArrayHardwareGate
            .ReadAsync(_files, _tool, cancellationToken)
            .ConfigureAwait(false);

        // <b>A machine that publishes no USB devices at all is not a frame whose microphone is
        // unplugged, and the difference decides whether a fleet works.</b> §5.3 exercises fleet
        // behaviour with virtual agents — the same binary, linux-x64, in a container — and a gate
        // that escalated there would put every one of them into a permanent stopped state over
        // hardware they were never going to have. That is the choice `cpu.governor.performance`
        // already made and the property `A_machine_with_no_sound_hardware_reports_the_whole_block_in_sync`
        // pins. A real frame has a USB bus, so an unplugged array on one still reaches rung 1 and
        // still stops it.
        if (!scan.BusEnumerable)
        {
            _last = null;

            return new ResourceObservation(
                true,
                Expected,
                "this machine publishes no USB devices at all, so there is no microphone unit to recognise");
        }

        var ruling = ArrayHardwareGate.Judge(
            scan,
            _pin,
            _values.Find(ArrayBoardRevision.SettingKey));

        _last = ruling;

        return ruling.MayWrite
            ? new ResourceObservation(true, Expected, ruling.Found)
            : new ResourceObservation(false, Expected, ruling.Message);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Always. There is no Act.</exception>
    /// <remarks>
    /// <b>Not a no-op, and not a silent success.</b> A gate that returned "nothing to do" would tell
    /// the loop a repair had been applied, and the verify would then read the same unrecognised unit
    /// and call it a failed repair — three times, with three reboots, which is the exact cost
    /// <see cref="IResource.IsGate"/> exists to avoid. The loop never reaches this method for a
    /// gate; the throw is what makes a future change to the loop that does reach it fail loudly
    /// rather than quietly cost a household three reboots.
    /// </remarks>
    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            ResourceName + " is a gate and has no Act: no command turns an unrecognised microphone "
            + "unit into a recognised one. Support for a unit arrives as an entry on "
            + "ArrayHardwareGate.Allowlist, which is a source edit and a release.");

    /// <summary>The desired value, as a person would read it.</summary>
    private static string Expected =>
        "a microphone unit this build has been told about — "
        + string.Join("; ", ArrayHardwareGate.Allowlist.Select(profile => profile.Name));
}
