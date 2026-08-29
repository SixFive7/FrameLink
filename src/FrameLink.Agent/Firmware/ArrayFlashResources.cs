using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;

namespace FrameLink.Agent.Firmware;

/// <summary>
/// How this frame's microphone unit stands against the image the pin names, read once.
/// </summary>
/// <param name="Attached">How many units are on the USB bus.</param>
/// <param name="Descriptor">What <c>bcdDevice</c> decodes to, or null.</param>
/// <param name="Control">What <c>VERSION</c> answered, or null when there is no tool to ask with.</param>
/// <param name="Profile">What <c>BLD_MSG</c> answered, or null when it would not read.</param>
/// <param name="OnTarget">Whether this unit is already running what a write would put on it.</param>
/// <remarks>
/// <b>One reading, three readers, and that is the point of it.</b>
/// <see cref="ArrayFlashAuthorisationResource"/> puts it in the row an operator looks at,
/// <see cref="ArrayFlashConsentResource"/> uses it to decide there is nothing to ask a household
/// about, and <see cref="ArrayFlashVerifiedResource"/> turns it into the verdict on a write that has
/// already happened. Three spellings of <i>what is this unit running</i> would eventually disagree,
/// and every direction that disagreement could go is a frame doing something nobody asked for.
/// </remarks>
public readonly record struct ArrayFlashStanding(
    int Attached,
    string? Descriptor,
    string? Control,
    string? Profile,
    bool OnTarget)
{
    /// <summary>The reading as a person would read it, for the observed half of a row.</summary>
    public string Describe() => Attached switch
    {
        0 => "no microphone unit is on this frame's USB bus",
        > 1 => Attached + " microphone units are attached, so nothing here can say which one this is about",
        _ => "the microphone unit reports firmware " + (Descriptor ?? "nothing")
            + " over USB, " + (Control ?? "nothing") + " to the control tool, and build configuration "
            + (Profile ?? "nothing"),
    };
}

/// <summary>
/// The reading every step of the firmware chain turns on, taken the two independent ways.
/// </summary>
public static class ArrayFlashChain
{
    /// <summary>Reads the attached unit and compares it against <paramref name="pin"/>'s target.</summary>
    /// <remarks>
    /// <para>
    /// <b>Both readings, because the cheap one always works and the other one is the one that
    /// disambiguates.</b> <c>bcdDevice</c> needs no tool, no root and no process at all;
    /// <c>BLD_MSG</c> needs the control tool and is the only thing that tells the three
    /// <c>v2.1.0</c> images apart, all of which answer <c>VERSION 2 1 0</c>.
    /// </para>
    /// <para>
    /// It is asked only when something is actually outstanding. On every frame in the fleet with no
    /// firmware write authorised, no step in the chain calls this at all, so the ordinary cost of the
    /// whole chain is one settings lookup and one small file read per pass.
    /// </para>
    /// </remarks>
    public static async Task<ArrayFlashStanding> ReadAsync(
        ISystemFiles files,
        XvfHost tool,
        XvfFirmwarePin pin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(pin);

        var attached = XvfArrayUsb.Attached(files);

        if (attached.Count != 1)
        {
            return new ArrayFlashStanding(attached.Count, null, null, null, OnTarget: false);
        }

        var descriptor = XvfArrayUsb.Version(attached[0].BcdDevice);
        var control = await ArrayFirmwareFlash.ControlVersionAsync(tool, cancellationToken).ConfigureAwait(false);
        var profile = await ArrayFirmwareFlash.ControlProfileAsync(tool, cancellationToken).ConfigureAwait(false);

        return new ArrayFlashStanding(
            1,
            descriptor,
            control,
            profile,
            ArrayFirmwareFlash.Landed(descriptor, control, profile, pin.Target));
    }
}

/// <summary>
/// <c>firmware.xvf3800.authorised</c> — the firmware write standing on this frame is one this build
/// can carry out, and here is how the unit stands against it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The first rung of the flash in the graph, and the one that is dormant on every frame that has
/// not been asked to do anything.</b> Its desired value is <i>no firmware write is authorised here
/// that names anything but the image this build carries</i> — true of every frame in the fleet
/// almost all of the time, and true the instant an authorisation is spent. So a frame with a factory
/// array converges its screen, its camera and its speaker exactly as it did before, which is
/// decision 90's objection and the whole reason this could be decomposed at all.
/// </para>
/// <para>
/// <b>The running version compared against the pin lives here, in the observed half.</b> §2.3's
/// Observe answers <i>what is actually there</i>, and what is there is a unit reporting a firmware
/// version over USB, a version over the control interface and a build configuration — read once by
/// <see cref="ArrayFlashChain"/> and shared with the two rungs below. It is deliberately reported
/// rather than converged: nothing on this frame may write firmware because a number differs, and a
/// resource that drifted on the version is exactly the resource decision 90 removed.
/// </para>
/// <para>
/// <b>It is a gate, because the one thing it can drift on has no Act.</b> An authorisation naming a
/// digest that is not the pinned image is a setting somebody typed, in the Fleet Manager, and no
/// command on this frame changes it. <see cref="IResource.IsGate"/> takes it straight to §2.5 rung 2
/// with the budget declared spent — no Act, no reboot, one escalation — and decision 68 stops the
/// pass around it. Before this it was a refusal event nobody had to read.
/// </para>
/// </remarks>
public sealed class ArrayFlashAuthorisationResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "firmware.xvf3800.authorised";

    private readonly ISystemFiles _files;
    private readonly XvfHost _tool;
    private readonly IStateStore _store;
    private readonly FleetValues _values;
    private readonly ArrayFlashWindow? _window;
    private readonly XvfFirmwarePin _pin;

    private bool _latched;

    /// <summary>Creates the rung over one frame.</summary>
    /// <remarks>
    /// The window is optional for the reason the approval below it is: it latches a durable fact at
    /// construction and a catalog is also built where there is nothing to latch. Absent means a frame
    /// that has never had a write interrupted, which is every frame that has never had one at all.
    /// </remarks>
    public ArrayFlashAuthorisationResource(
        ISystemFiles files,
        XvfHost tool,
        IStateStore store,
        FleetValues values,
        ArrayFlashWindow? window = null,
        XvfFirmwarePin? pin = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(values);

        _files = files;
        _tool = tool;
        _store = store;
        _values = values;
        _window = window;
        _pin = pin ?? XvfFirmwarePin.Current;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public bool IsGate => true;

    /// <inheritdoc/>
    /// <remarks>
    /// Two things can stop this rung and they need different people doing different things, so the
    /// sentence follows the last Observe rather than describing whichever of them was written first.
    /// </remarks>
    public string Detected => _latched
        ? "An earlier attempt to update this frame's microphone was cut off part-way, and this frame cannot tell how "
            + "far it got."
        : "Somebody has asked this frame to put a version of the microphone's own software onto it that this frame "
            + "does not have and cannot check.";

    /// <inheritdoc/>
    public string WhyItMatters => _latched
        ? "Writing to it again without somebody looking first could turn a microphone that still works into one that "
            + "does not, so nothing further will be written until a person has been."
        : "The microphone is the one part of this frame that cannot be put right by rewriting its memory card, so it "
            + "is only ever written with software the frame has already checked byte for byte.";

    /// <inheritdoc/>
    /// <remarks>
    /// <c>firmware.xvf3800.image</c> first, because a digest means nothing until the bytes it names
    /// are on the card; and <c>firmware.xvf3800.recognised</c>, because everything below reads the
    /// unit and a unit nobody recognises is a question for a person rather than an input to a write.
    /// A frame missing either is <see cref="ResourceStatusKind.Blocked"/> behind it rather than
    /// escalated on its own account.
    /// </remarks>
    public IReadOnlyList<string> DependsOn =>
        [XvfFirmwareImageResource.ResourceName, ArrayRecognitionResource.ResourceName];

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var target = _pin.Target;

        if (ArrayFirmwareFlash.Outstanding(_values, _store) is not { } authorisation)
        {
            _latched = false;

            return new ResourceObservation(
                true,
                Expected,
                _values.Find(ArrayFirmwareFlash.AuthorisationKey) is null
                    ? "no firmware write is authorised on this frame"
                    : "the firmware write authorised on this frame has already been carried out");
        }

        // <b>The marker latch, reaching a person without spending three reboots to do it.</b>
        // ArrayFirmwareFlash refuses every write while this file is on the card and that refusal is
        // untouched — this is the same fact reported where it costs nothing. A cgroup kill, a power
        // cut and a crash all leave the same array behind, so how far a half-written partition got is
        // a state this frame cannot measure, and the right answer to a state you cannot measure is a
        // person rather than another attempt.
        //
        // It is deliberately below the dormancy check. A frame carrying the marker and no
        // authorisation is a frame nobody has asked to do anything, and stopping its screen, camera
        // and speaker over a write that is not going to be attempted would be the cost decision 90
        // objected to, arrived at by a different road. Everybody is told either way: the screen
        // beside the loop puts the interrupted message up whether or not anything is authorised.
        if (_window is { Interrupted: true })
        {
            _latched = true;

            return new ResourceObservation(
                false,
                Expected,
                "a previous firmware write on this frame never finished — "
                + (_window.InterruptedDetail ?? "no detail was recorded")
                + " — so nothing further will be written until somebody has looked at the microphone unit and "
                + "removed " + _store.PathOf(ArrayFlashWindow.MarkerFileName));
        }

        _latched = false;

        var parsed = ArrayFlashAuthorisation.Parse(authorisation);

        if (!string.Equals(parsed.Digest, target.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new ResourceObservation(
                false,
                Expected,
                $"the authorisation names sha256 {Short(parsed.Digest)}, and the only image this build may write is "
                + $"{target.Name} at sha256 {Short(target.Sha256)}");
        }

        var standing = await ArrayFlashChain
            .ReadAsync(_files, _tool, _pin, cancellationToken)
            .ConfigureAwait(false);

        return new ResourceObservation(
            true,
            Expected,
            $"a write of {target.Name} (firmware {target.Version}, build configuration {XvfFirmwarePin.Profile}) is "
            + $"authorised here, and {standing.Describe()}"
            + (standing.OnTarget ? " — which is already what would be written" : string.Empty));
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Always. There is no Act.</exception>
    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            ResourceName + " is a gate and has no Act: the authorisation is a Fleet Manager setting, and nothing on "
            + "this frame may edit the instruction it was given in order to make it satisfiable.");

    private string Expected =>
        "no firmware write authorised on this frame that names anything but " + _pin.Target.Name
        + " at sha256 " + Short(_pin.Target.Sha256);

    private static string Short(string digest) => digest.Length <= 12 ? digest : digest[..12];
}

/// <summary>
/// <c>firmware.xvf3800.consent</c> — nobody's microphone is written until a person has said it may
/// be.
/// </summary>
/// <remarks>
/// <para>
/// <b>The interlock that is a person, expressed as a rung rather than as a wait.</b> Mains loss
/// during a DFU write is unguardable at the device and destroys the unit; the only mitigation that
/// exists is somebody in the room who has been told that and has agreed to it. An Act that awaited
/// that agreement would hang the pass for as long as a household was out — so this is a gate, and
/// the escalation shape does the waiting: §2.5 rung 2 with the budget declared spent, no Act and no
/// reboot, decision 68 stopping the pass around it, and §2.5 rung 6 keeping it there until a person
/// arrives. <i>Escalated</i> already means <i>stopped, waiting for a person, for as long as it
/// takes</i>, which is exactly what consent means.
/// </para>
/// <para>
/// <b>The question itself is raised beside the loop, not here, and that is forced rather than
/// chosen.</b> §2.3 requires Observe to be side-effect-free — a stopped frame sweeps it every pass
/// — and the screen has to come off the panel within seconds of a call starting, where the pass
/// interval is five minutes. So <see cref="ArrayFirmwareFlash.PrepareAsync"/> owns the panel
/// conversation on its own short cadence and this rung reads the answer. There is one question and
/// one place it is worded (<see cref="ArrayFlashVoice"/>), which is what decision 83 asks for.
/// </para>
/// <para>
/// <b>Agreeing resets this rung's budget, and without that the frame could never come back.</b> A
/// gate that has escalated is not observed again until somebody resets it (§2.5 rung 2), so a
/// household that pressed <i>yes</i> would have been agreeing to a write on a frame that had stopped
/// asking. The press is the human action rung 5 already describes, reaching the same reset path the
/// Fleet Manager's retry reaches — see <see cref="ArrayFlashApproval.Agreed"/>.
/// </para>
/// <para>
/// <b>Nothing is asked when nothing would be written.</b> A unit already running the pinned image on
/// the pinned build configuration needs no household's permission for a write that would change
/// nothing, so this rung reads in sync and lets the write below spend the authorisation and say so.
/// The check is the whole of <see cref="ArrayFirmwareFlash.Landed"/> rather than the version alone,
/// because a unit on <c>v2.1.0_48k2ch</c> answers the same version as the target and is not it.
/// </para>
/// </remarks>
public sealed class ArrayFlashConsentResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "firmware.xvf3800.consent";

    private readonly ISystemFiles _files;
    private readonly XvfHost _tool;
    private readonly IStateStore _store;
    private readonly FleetValues _values;
    private readonly ArrayFlashApproval? _approval;
    private readonly Func<string> _deviceId;
    private readonly XvfFirmwarePin _pin;

    /// <summary>Creates the rung over one frame's screen.</summary>
    /// <remarks>
    /// The approval is optional for the reason <c>gpio.button.line</c>'s claim is: it owns a live
    /// screen with a hub behind it, and a catalog is also built where there is none. Absent means
    /// nobody can agree here, which this rung reports as the refusal it would be on a frame rather
    /// than skipping itself.
    /// </remarks>
    public ArrayFlashConsentResource(
        ISystemFiles files,
        XvfHost tool,
        IStateStore store,
        FleetValues values,
        ArrayFlashApproval? approval,
        Func<string>? deviceId = null,
        XvfFirmwarePin? pin = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(values);

        _files = files;
        _tool = tool;
        _store = store;
        _values = values;
        _approval = approval;
        _deviceId = deviceId ?? (() => "unknown");
        _pin = pin ?? XvfFirmwarePin.Current;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public bool IsGate => true;

    /// <inheritdoc/>
    public string Detected =>
        "This frame has been asked to update its microphone, and nobody standing here has agreed to it yet.";

    /// <inheritdoc/>
    public string WhyItMatters =>
        "While the microphone is being written, losing power to the frame can leave it unusable — so the frame will "
        + "not start until somebody in the room has said they will leave it plugged in.";

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn => [ArrayFlashAuthorisationResource.ResourceName];

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        if (ArrayFirmwareFlash.Outstanding(_values, _store) is not { } authorisation)
        {
            return new ResourceObservation(true, Expected, "no firmware write is waiting for anybody's agreement");
        }

        var parsed = ArrayFlashAuthorisation.Parse(authorisation);

        if (parsed.BypassesLocalApproval(_deviceId()))
        {
            return new ResourceObservation(
                true,
                Expected,
                "the fleet operator authorised this write unattended for this device, accepting that "
                + string.Join(" ", ArrayFirmwareFlash.UnattendedWarning));
        }

        if (string.Equals(_approval?.ApprovedFor, authorisation, StringComparison.Ordinal))
        {
            return new ResourceObservation(true, Expected, "somebody at this frame agreed to this write on the screen");
        }

        var standing = await ArrayFlashChain
            .ReadAsync(_files, _tool, _pin, cancellationToken)
            .ConfigureAwait(false);

        if (standing.OnTarget)
        {
            return new ResourceObservation(
                true,
                Expected,
                "the microphone unit already runs what this write would put on it, so there is nothing to agree to");
        }

        return new ResourceObservation(
            false,
            Expected,
            _approval is null || !_approval.Answerable
                ? "nobody at this frame has agreed to the write, and this frame has no touchscreen for anybody to "
                    + "agree on — either give it a working panel, or authorise the write unattended for this device "
                    + "by adding '" + ArrayFirmwareFlash.UnattendedPrefix + _deviceId()
                    + "' to the authorisation's ticket"
                : "nobody standing at this frame has agreed to the write yet, and the screen is asking them to");
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Always. There is no Act.</exception>
    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            ResourceName + " is a gate and has no Act: no command on this frame produces a person's agreement, and "
            + "an Act that waited for one would hang the pass for as long as the household was out.");

    private static string Expected => "nobody's agreement outstanding on a firmware write";
}

/// <summary>
/// <c>firmware.xvf3800.written</c> — every firmware write this frame has been authorised to make has
/// been made.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the resource decision 90 said could not exist, and the difference is what it claims.</b>
/// A resource over the firmware <i>version</i> has no Act that can succeed: on a frame nobody has
/// authorised it drifts for ever, spends three attempts and three reboots, escalates, and stops the
/// pass over a number nobody was going to let it write. This one claims something else — that the
/// operator's instruction has been carried out — and that always has an Act that can succeed,
/// because carrying it out is one interlocked run of <see cref="ArrayFirmwareFlash"/> and because a
/// frame with no instruction has already satisfied it.
/// </para>
/// <para>
/// <b>What it deliberately does not claim is that the write worked.</b> The authorisation is spent
/// before <c>dfu-util</c> starts, so this rung reads in sync afterwards whatever the array came back
/// as — which would be the v1 governor shape if it were the last word, and it is not:
/// <see cref="ArrayFlashVerifiedResource"/> below is, and it looks at the unit rather than at the
/// record. The split is the honest one, and it is the two halves the operator asked for: the write,
/// then the verification of it.
/// </para>
/// <para>
/// <b>Every interlock stays in the Act, unchanged.</b> The single-use authorisation spent atomically
/// before the process starts, the durable marker that latches an interrupted write absolutely, the
/// three attempts inside the one operation, the update stand-down, the reboot hold and the last
/// pre-flight in the second before <c>dfu-util</c> runs — all of it is
/// <see cref="ArrayFirmwareFlash.TickAsync"/>, which this calls and does not reimplement. A resource
/// says what was true at the last pass; that pre-flight says what is true now, and a unit can be
/// swapped between the two.
/// </para>
/// <para>
/// <b>Deferrals are in sync rather than drift, and the reason only became visible here.</b> §2.4
/// crosses a reboot after every Act, so a rung that reported a call in progress as drift would act,
/// be refused, and reboot the frame — in the middle of the call it was deferring for. Waiting is not
/// drift, and <see cref="ArrayFirmwareFlash.Deferral"/> is the one definition of what waiting means.
/// </para>
/// </remarks>
public sealed class ArrayFlashWriteResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "firmware.xvf3800.written";

    private readonly IStateStore _store;
    private readonly FleetValues _values;
    private readonly Func<ArrayFirmwareFlash?> _flash;

    /// <summary>Creates the rung over one frame's flash.</summary>
    /// <remarks>
    /// A delegate rather than the operation itself, because the operation needs the supervisor's view
    /// of whether somebody is on a call and the update service's view of whether this process is
    /// about to restart — both of which are built after the catalog. Null is a machine that has no
    /// flash at all, where this rung is permanently in sync and says so.
    /// </remarks>
    public ArrayFlashWriteResource(IStateStore store, FleetValues values, Func<ArrayFirmwareFlash?>? flash = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(values);

        _store = store;
        _values = values;
        _flash = flash ?? (() => null);
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public string Detected =>
        "This frame was asked to update its microphone's own software and has not managed to.";

    /// <inheritdoc/>
    public string WhyItMatters =>
        "Until it is done the microphone keeps running the software it came with, and the frame will keep saying so "
        + "rather than quietly leaving it.";

    /// <inheritdoc/>
    /// <remarks>
    /// The consent rung, and the package that provides the program that does the writing. Naming
    /// <c>pkg.dfu-util</c> here is what turns the pre-flight's <c>DfuUtilMissing</c> from an
    /// escalation into <see cref="ResourceStatusKind.Blocked"/> behind something the reconciler
    /// installs by itself.
    /// </remarks>
    public IReadOnlyList<string> DependsOn =>
        [ArrayFlashConsentResource.ResourceName, PackageResource.Prefix + ArrayFirmwareFlash.DfuUtil];

    /// <inheritdoc/>
    public ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        if (ArrayFirmwareFlash.Outstanding(_values, _store) is not { } authorisation)
        {
            return ValueTask.FromResult(new ResourceObservation(
                true,
                Expected,
                _values.Find(ArrayFirmwareFlash.AuthorisationKey) is null
                    ? "no firmware write is outstanding on this frame"
                    : "the firmware write authorised on this frame has been carried out"));
        }

        if (_flash() is not { } flash)
        {
            return ValueTask.FromResult(new ResourceObservation(
                true,
                Expected,
                "nothing on this machine can write firmware, so no authorised write is outstanding here"));
        }

        return ValueTask.FromResult(flash.Deferral() is { } deferred
            ? new ResourceObservation(true, Expected, deferred.Why)
            : new ResourceObservation(
                false,
                Expected,
                "a firmware write is authorised on this frame and has not been carried out ("
                + ArrayFlashAuthorisation.Parse(authorisation).Ticket + ")"));
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        if (_flash() is not { } flash)
        {
            throw new InvalidOperationException(
                ResourceName + " was acted on with no firmware operation behind it. Observe reports in sync in that "
                + "case, so reaching here means the loop acted on a resource it had not observed as drifted.");
        }

        var outcome = await flash.TickAsync(cancellationToken).ConfigureAwait(false);

        return new ResourceAction(
            outcome.Summary,
            "Writing the microphone's own software, which somebody has agreed to and which must not be interrupted "
            + "once it has started.");
    }

    private static string Expected => "no firmware write outstanding on this frame";
}

/// <summary>
/// <c>firmware.xvf3800.verified</c> — every firmware write this frame has made produced the firmware
/// it was supposed to.
/// </summary>
/// <remarks>
/// <para>
/// <b>The post-write verification, and it is an Observe because that is all it can honestly be.</b>
/// §2.3 makes Observe and Verify one method, so the rung above proves its own change across §2.4's
/// reboot — and what that proves is that the instruction was carried out, not that the array came
/// back well. This rung asks the other question, of the unit rather than of the record, and it is a
/// different question because the authorisation is single-use: a write that completed and did not
/// produce the pinned firmware has spent everything it had.
/// </para>
/// <para>
/// <b>Vacuous on every frame that has never written, which is what keeps it out of decision 90's
/// way.</b> A frame carrying a factory array and no spent authorisation has made no write, so there
/// is no write to verify and this reads in sync. It is not a resource over the firmware version, and
/// it must never become one.
/// </para>
/// <para>
/// <b>A gate, because there is no second write to try.</b> The authorisation that bought the write is
/// spent, so nothing on this frame can act on a unit that came back wrong — which is precisely
/// <see cref="IResource.IsGate"/>'s case, and it takes the frame to a stopped state with a person's
/// name on it rather than through three reboots to the same conclusion. The words the write itself
/// used were already this: <i>nothing further will be attempted on this frame without a new
/// authorisation, and somebody has to look at the unit</i>. This is that sentence made into a rung.
/// </para>
/// <para>
/// <b>It also catches an array swapped in afterwards</b>, which is deliberate rather than incidental:
/// a frame that once wrote firmware and now carries a unit that is not on it is a frame somebody has
/// changed, and decision 91's rule that a later array swap cannot be flashed by nobody's decision
/// means the frame cannot put that right by itself. Saying so is the whole of what it can do.
/// </para>
/// </remarks>
public sealed class ArrayFlashVerifiedResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "firmware.xvf3800.verified";

    private readonly ISystemFiles _files;
    private readonly XvfHost _tool;
    private readonly IStateStore _store;
    private readonly XvfFirmwarePin _pin;

    /// <summary>Creates the rung over one frame.</summary>
    public ArrayFlashVerifiedResource(
        ISystemFiles files,
        XvfHost tool,
        IStateStore store,
        XvfFirmwarePin? pin = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(store);

        _files = files;
        _tool = tool;
        _store = store;
        _pin = pin ?? XvfFirmwarePin.Current;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public bool IsGate => true;

    /// <inheritdoc/>
    public string Detected =>
        "This frame updated its microphone, and the microphone did not come back running what it was given.";

    /// <inheritdoc/>
    public string WhyItMatters =>
        "Nothing further will be written here without somebody authorising it again, so the microphone needs a person "
        + "to look at it before this frame can go any further.";

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn => [ArrayFlashWriteResource.ResourceName];

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        if (ArrayFirmwareFlash.Spent(_store) is null)
        {
            return new ResourceObservation(true, Expected, "no firmware has ever been written on this frame");
        }

        var standing = await ArrayFlashChain
            .ReadAsync(_files, _tool, _pin, cancellationToken)
            .ConfigureAwait(false);

        return new ResourceObservation(standing.OnTarget, Expected, standing.Describe());
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Always. There is no Act.</exception>
    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            ResourceName + " is a gate and has no Act: the authorisation that bought the write is spent, so there is "
            + "no second write to try, and writing again would be a decision nobody made.");

    private string Expected =>
        "a microphone unit running firmware " + _pin.Target.Version + " on build configuration "
        + XvfFirmwarePin.Profile + ", or a frame that has never written one";
}
