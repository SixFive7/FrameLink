using System.Globalization;
using System.Text;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Resources;
using FrameLink.Agent.Telemetry;
using FrameLink.Protocol;

namespace FrameLink.Agent.Firmware;

/// <summary>Why a flash did not start. Every value is a refusal; none of them is an error.</summary>
public enum ArrayFlashRefusal
{
    /// <summary>Nobody has authorised one. The ordinary state of every frame in the fleet.</summary>
    NotAuthorised,

    /// <summary>This exact authorisation has already been spent. Single-use means single-use.</summary>
    AlreadyConsumed,

    /// <summary>The authorisation names a digest that is not the pinned target image.</summary>
    NotThePinnedImage,

    /// <summary>The target image is absent from this frame, or does not hash to the pin.</summary>
    ImageNotVerified,

    /// <summary>The erase image or the fallback firmware is absent, so there is no way back.</summary>
    RecoveryNotVerified,

    /// <summary><c>dfu-util</c> is not installed, so nothing could write the image anyway.</summary>
    DfuUtilMissing,

    /// <summary>No microphone unit is on the bus.</summary>
    NoArrayAttached,

    /// <summary>More than one is, and nothing here can say which one it would write.</summary>
    MoreThanOneArray,

    /// <summary>The array already runs the target firmware, so a write would change nothing.</summary>
    AlreadyAtTarget,

    /// <summary>A previous flash never finished. A person has to look before another one starts.</summary>
    PreviousFlashUnfinished,

    /// <summary>Somebody is on a call. Deferred, not spent.</summary>
    CallInProgress,

    /// <summary>A new agent binary is in place and this process is about to restart. Deferred.</summary>
    AgentRestartPending,

    /// <summary>
    /// Nobody at the frame has agreed to it yet. Deferred, not spent.
    /// </summary>
    /// <remarks>
    /// The one refusal in this enum that is waiting on a person in the room rather than on a
    /// machine. It is a deferral for exactly that reason: the authorisation stays armed, because an
    /// operator's decision to flash this frame does not expire because the household was out.
    /// </remarks>
    AwaitingLocalApproval,

    /// <summary>
    /// The unit on the bus is not one this build has been told about (<see cref="ArrayHardwareGate"/>).
    /// </summary>
    ArrayNotRecognised,
}

/// <summary>
/// One authorisation, taken apart — <c>&lt;sha256&gt;</c>, optionally <c>:&lt;ticket&gt;</c>.
/// </summary>
/// <param name="Digest">The SHA-256 of the image the operator is authorising.</param>
/// <param name="Ticket">Whatever the operator wrote after the colon. May be empty.</param>
/// <param name="UnattendedDeviceId">
/// The device id named by an operator bypass inside the ticket, or null when there is none.
/// </param>
/// <remarks>
/// <para>
/// <b>The bypass rides inside the authorisation because that is what makes it single-use</b>, and
/// adding a second mechanism would have made it something else. Single-use here is not a flag that
/// is cleared: it is the <i>whole authorisation string</i> being written to
/// <see cref="ArrayFirmwareFlash.ConsumedFileName"/> with <c>WriteSecretAtomic</c> before
/// <c>dfu-util</c> starts, and an authorisation equal to the one already recorded being refused for
/// ever after. A bypass carried inside that string is therefore spent by the same write, at the same
/// instant, with no second file, no second flag and nothing that can be left switched on — and
/// re-authorising an unattended write means writing a <i>different</i> string, which is an act
/// somebody has to perform deliberately.
/// </para>
/// <para>
/// <b>It names the device, which is what scopes it to one frame.</b> §3.4's settings are fleet
/// defaults with per-device overrides, so a bypass that was merely a word would bypass on every
/// frame the moment somebody set it fleet-wide — which is precisely the "fleet default" the operator
/// ruled out. Requiring the frame's own device id inside the token means a fleet-wide push bypasses
/// on exactly one frame and every other frame reads it, finds a name that is not its own, and asks
/// its own household anyway. A frame that ignores a bypass for that reason says so in its journal
/// rather than silently.
/// </para>
/// <para>
/// <b>The token is long and reads as a sentence on purpose.</b>
/// <see cref="ArrayFirmwareFlash.UnattendedPrefix"/> is not a flag anyone types by accident, and it
/// states the thing being accepted — that no person will be at the frame while its microphone is
/// written, and that mains loss during that write is unguardable and destroys the unit.
/// </para>
/// </remarks>
public readonly record struct ArrayFlashAuthorisation(string Digest, string Ticket, string? UnattendedDeviceId)
{
    /// <summary>Takes an authorisation apart. Never throws; a malformed value simply matches nothing.</summary>
    public static ArrayFlashAuthorisation Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var parts = value.Split(':', 2);
        var digest = parts[0].Trim();
        var ticket = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        string? unattended = null;

        foreach (var word in ticket.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            // The token alone is not a bypass. A bare prefix with nothing after it is somebody who
            // stopped typing half way through the thing that scopes it to one frame, and reading
            // that as "bypass everywhere" is the fleet-wide switch this design exists to prevent.
            if (word.StartsWith(ArrayFirmwareFlash.UnattendedPrefix, StringComparison.Ordinal)
                && word[ArrayFirmwareFlash.UnattendedPrefix.Length..] is { Length: > 0 } named)
            {
                unattended = named;
            }
        }

        return new ArrayFlashAuthorisation(digest, ticket, unattended);
    }

    /// <summary>Whether this authorisation skips the local approval on <paramref name="deviceId"/>.</summary>
    public bool BypassesLocalApproval(string deviceId) =>
        UnattendedDeviceId is { Length: > 0 } named
        && string.Equals(named, deviceId, StringComparison.Ordinal);

    /// <summary>Whether it carries a bypass meant for some other frame.</summary>
    public bool BypassNamesAnotherDevice(string deviceId) =>
        UnattendedDeviceId is { Length: > 0 } named
        && !string.Equals(named, deviceId, StringComparison.Ordinal);
}

/// <summary>What one look at the authorisation concluded.</summary>
/// <param name="Refusal">Why nothing was written, or null when a flash was performed.</param>
/// <param name="Flashed">Whether <c>dfu-util</c> actually ran.</param>
/// <param name="Succeeded">Whether the array came back reporting the target firmware.</param>
/// <param name="Summary">One sentence an operator can read.</param>
public readonly record struct ArrayFlashOutcome(
    ArrayFlashRefusal? Refusal,
    bool Flashed,
    bool Succeeded,
    string Summary);

/// <summary>Everything <see cref="ArrayFirmwareFlash"/> needs to do its one job.</summary>
public sealed record ArrayFlashServices
{
    /// <summary>The control tool, for the second reading of the firmware version.</summary>
    public required XvfHost Tool { get; init; }

    /// <summary>The filesystem, for sysfs and for the images.</summary>
    public required ISystemFiles Files { get; init; }

    /// <summary>How <c>dfu-util</c> is started. The only writer in this product.</summary>
    public required IProcessRunner Processes { get; init; }

    /// <summary>The pinned images and their digests.</summary>
    public required XvfFirmwareInstaller Installer { get; init; }

    /// <summary>The window that holds updates, reboots and the bench power switch off.</summary>
    public required ArrayFlashWindow Window { get; init; }

    /// <summary>
    /// The person at the frame, and the screen that asks them — the one interlock software cannot be.
    /// </summary>
    public required ArrayFlashApproval Approval { get; init; }

    /// <summary>Where the event trail goes.</summary>
    public required IReconcileTelemetry Telemetry { get; init; }

    /// <summary>Where the spent authorisation is remembered, durably.</summary>
    public required IStateStore Store { get; init; }

    /// <summary>The clock, for timing the write and for the event.</summary>
    public required IAgentClock Clock { get; init; }

    /// <summary>Where refusals are logged.</summary>
    public required IAgentLog Log { get; init; }

    /// <summary>The Fleet Manager's settings, which is where an authorisation arrives.</summary>
    public required FleetValues Values { get; init; }

    /// <summary>The frame this is about.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Whether somebody is on a call right now.</summary>
    public Func<bool>? CallActive { get; init; }

    /// <summary>Whether a new agent binary is staged and this process is about to restart.</summary>
    public Func<bool>? RestartPending { get; init; }
}

/// <summary>
/// <b>The one code path in this product that can write firmware to the microphone array</b>, and
/// every interlock that stands in front of it (decision 91).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is beside the loop and not a resource, when the operator asked for convergence.</b>
/// §2.3's contract is <i>Observe → Compare → Act (only on drift) → Verify</i>, and a resource's Act
/// must be able to succeed. A firmware-version resource's Act cannot: on a frame nobody has
/// authorised, it would drift, spend three attempts and three reboots, escalate, and by decision 68
/// stop the whole pass — so a frame carrying a factory array would never converge its screen, its
/// camera or its speaker over a number nobody had agreed to write. That is decision 90's reasoning
/// and it is still correct; what decision 91 changes is the conclusion drawn from it. The flash is
/// not made into a resource that lies. It is made into a deliberate, interlocked, single-use
/// operation that the fleet can *see* the need for — <see cref="ArrayFirmwareReporter"/> reports
/// every frame's running firmware against the pinned target — and that the images resource
/// <c>firmware.xvf3800.image</c> prepares every frame in the fleet for, unattended and with no risk.
/// Convergence is therefore: every frame carries the target image and knows whether it is running
/// it, and turning that into a write is one deliberate act per frame.
/// </para>
/// <para>
/// <b>The authorisation names a digest, not a version, and it is spent before anything starts.</b>
/// The same version string has been published twice with different bytes, so a version number
/// authorises nothing in particular — <see cref="XvfFirmwarePin"/> records the measurement. The
/// setting therefore carries the target image's SHA-256, optionally followed by <c>:</c> and any
/// ticket the operator likes; the whole string is written to
/// <see cref="ConsumedFileName"/> with <see cref="IStateStore.WriteSecretAtomic"/> <i>before</i>
/// <c>dfu-util</c> is started, and an authorisation equal to the one already recorded is refused.
/// Two properties follow that a persistent setting could not otherwise have. A crash between the
/// consume and the write cannot re-authorise, because the record is on the card and fsynced. And
/// re-authorising is an explicit act: the operator has to write a <i>different</i> string, which is
/// what the ticket is for. The old <c>audio.firmwareFlashAuthorised</c> was a plain version string
/// and re-authorised on every pass for ever.
/// </para>
/// <para>
/// <b>One attempt. Never a second.</b> There is no retry anywhere in this class: the authorisation
/// is already spent by the time <c>dfu-util</c> runs, a failure emits an event for a person and
/// nothing else, and the next flash needs a human. §2.5's attempt budget is not involved at all,
/// because the danger here is not a second attempt — it is the first one being interrupted, which an
/// attempt counter cannot see.
/// <br/><br/>
/// <b>The reason this paragraph used to give was wrong, and is corrected rather than quietly
/// dropped.</b> It said that "retrying a partial write is the documented route from a recoverable
/// board to an unrecoverable one". No such documentation exists — it was searched for — and XMOS
/// documents the opposite: <i>"Another download operation may be reattempted."</i> The narrow
/// supported claim is about the <c>all_ff</c> <i>erase</i>, not about a write. What the single-use
/// authorisation actually rests on is unchanged and never needed that sentence: an authorisation
/// that survived a crash could authorise a second write nobody decided on, and a half-written
/// partition is a state nothing on this frame can measure, so it is a state a person should look at.
/// </para>
/// <para>
/// <b>The verify is evidence, not a timer.</b> The Act does not sleep five seconds and declare
/// victory; it polls <c>/sys/bus/usb/devices/*/bcdDevice</c> until the array re-enumerates
/// reporting the target version, up to <see cref="ReEnumerationTimeout"/>, and asks the control tool
/// for its own answer as a second reading. An array that never comes back is reported as an array
/// that never came back.
/// </para>
/// <para>
/// <b>What no software can do, said plainly rather than implied.</b> Mains loss during the write is
/// unguardable at the device; the harness-side refusal named in decision 91 is a mitigation on the
/// workstation, not a guarantee. Board revision is not readable in software at all — not in the USB
/// descriptors and not in the control tool's command set — and upstream issue #32 reports the target
/// firmware not booting on a V1.1 board, so <i>no interlock in this file addresses the largest
/// single risk of the operation</i>. That is why the sequencing in decision 91 puts a rehearsed
/// Safe Mode recovery on our own hardware before any first flash, and why the recovery images ship
/// on the card whether or not they are ever wanted.
/// </para>
/// </remarks>
public sealed class ArrayFirmwareFlash
{
    /// <summary>The fleet setting an authorisation arrives in (§3.4).</summary>
    /// <remarks>
    /// Per-device by construction: §3.4's settings are fleet defaults with per-device overrides, and
    /// nothing here has a catalog default — an absent value is <see cref="ArrayFlashRefusal.NotAuthorised"/>.
    /// Setting it fleet-wide would authorise one flash on every frame that has not already spent
    /// that exact string, which is a thing an operator can do and should have to mean.
    /// </remarks>
    public const string AuthorisationKey = "audio.arrayFirmwareFlash";

    /// <summary>Where the spent authorisation is remembered, durably.</summary>
    public const string ConsumedFileName = "array-flash.consumed";

    /// <summary>
    /// The operator's scoped bypass, written inside the authorisation's ticket as
    /// <c>&lt;prefix&gt;&lt;deviceId&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <b>A frame may be somewhere nobody can stand</b>, so the local approval has to be skippable —
    /// and every property that makes it safe to skip comes from where the token lives rather than
    /// from the token itself. It is inside the single-use authorisation, so it is spent by the same
    /// atomic write and cannot be left on. It carries a device id, so it cannot become a fleet
    /// default. It is a sentence rather than a flag, so nobody sets it without reading it. See
    /// <see cref="ArrayFlashAuthorisation"/> for the whole of that reasoning, and
    /// <see cref="UnattendedWarning"/> for the words an operator has to have accepted.
    /// </remarks>
    public const string UnattendedPrefix = "unattended-nobody-at-this-frame-i-accept-mains-loss-destroys-it=";

    /// <summary>
    /// The warnings an operator is accepting by writing <see cref="UnattendedPrefix"/> into a ticket.
    /// </summary>
    /// <remarks>
    /// Carried on the frame and emitted verbatim into the <c>array-flash</c> event of every
    /// unattended write, so the trail records not only that a frame was flashed with nobody in front
    /// of it but exactly what was being accepted when it was. It is a constant here because the
    /// agent is the thing that acts on the bypass, and a warning that lives only in the surface an
    /// operator reads can drift away from the behaviour it describes.
    /// </remarks>
    public static IReadOnlyList<string> UnattendedWarning { get; } =
    [
        "Nobody will be standing at this frame while its microphone is written.",
        "Mains loss during the write is unguardable at the device: no interlock in this product can reach it, and a "
            + "write interrupted by loss of power can leave the microphone unusable until somebody recovers it by hand.",
        "Recovery needs physical access — power the unit off, hold Mute, power it back on — so a frame nobody can "
            + "reach is a frame nobody can recover.",
        "This applies to one write on one named frame. It is spent the instant the write starts and authorises "
            + "nothing afterwards.",
    ];

    /// <summary>The program that performs the write. Named in this file and nowhere else.</summary>
    public const string DfuUtil = "dfu-util";

    /// <summary>Where <c>pkg.dfu-util</c> puts it.</summary>
    public const string DfuUtilPath = "/usr/bin/" + DfuUtil;

    /// <summary>How often the authorisation is looked for.</summary>
    /// <remarks>
    /// A tick with no authorisation reads one string and returns, so the interval is sized against
    /// how long an operator should wait after pressing the button rather than against any cost.
    /// </remarks>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

    /// <summary>How often it looks while one of its own screens is up.</summary>
    /// <remarks>
    /// The screen is the reason, not the flash. While a firmware question is covering a household's
    /// photos, every condition that would take it away again — a call starting, an update landing,
    /// the operator changing their mind — has to be noticed in seconds rather than in a minute.
    /// </remarks>
    public static readonly TimeSpan PromptInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long the array is given to come back after the write.</summary>
    /// <remarks>
    /// The write itself is reported upstream at about thirty seconds and has never been measured in
    /// this repository. Ninety seconds is three times that, and the cost of the bound being
    /// generous is a report that arrives late rather than a wrong one.
    /// </remarks>
    public static readonly TimeSpan ReEnumerationTimeout = TimeSpan.FromSeconds(90);

    /// <summary>How often the bus is re-read while waiting.</summary>
    public static readonly TimeSpan ReEnumerationPoll = TimeSpan.FromSeconds(2);

    private readonly ArrayFlashServices _services;
    private ArrayFlashRefusal? _reported;
    private string? _reportedWhy;

    /// <summary>Creates the flash for one frame.</summary>
    public ArrayFirmwareFlash(ArrayFlashServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>The argument vector the write uses, for <paramref name="path"/>.</summary>
    /// <remarks>
    /// Upstream's own documented flow, unchanged: <c>-e</c> detaches the device out of run-time
    /// mode into DFU mode, <c>-a 1</c> targets the <b>Upgrade</b> partition and never the Factory
    /// one Safe Mode lives in, <c>-D</c> downloads, and <c>-R</c> resets afterwards so the array
    /// re-enumerates as an audio device. It is a vector rather than a command line, so there is no
    /// shell and no place a fleet value could become a second command; the only value that varies is
    /// the path, and it is the path of a file this process has just re-hashed against the pin.
    /// </remarks>
    public static IReadOnlyList<string> Arguments(string path) => ["-R", "-e", "-a", "1", "-D", path];

    /// <summary>Looks once for an authorisation, and performs the flash if everything permits it.</summary>
    public async Task<ArrayFlashOutcome> TickAsync(CancellationToken cancellationToken)
    {
        var pin = _services.Installer.Pin;
        var target = pin.Target;

        // First, and unconditionally. A marker left by a previous process means a write began and
        // nothing on this frame knows how far it got — and an unattended second write onto an
        // unknown state is a decision nobody made. Only a person deletes this.
        if (_services.Window.Interrupted)
        {
            // The same durable evidence, put on the screen the person is standing in front of. An
            // array that is not on the bus at all is a board that did not come back, and the way
            // back is a gesture somebody performs with their hands — so it is spelled out on the
            // panel, which is the surface that still works when the microphone does not.
            var attached = XvfArrayUsb.Attached(_services.Files);
            _services.Approval.Interrupted(attached.Count > 0);

            // Shown, and then bounded by the same call that bounds every other completed screen.
            // A frame whose marker only a person can remove would otherwise cover a household's
            // photos for ever — and permanently for a frame with no touchscreen, which cannot even
            // be told to put it away. It comes back after the rest, so the condition is named
            // repeatedly rather than once, which is what §1.2 principle 3 asks for.
            _services.Approval.Withdraw();

            return await RefuseAsync(
                ArrayFlashRefusal.PreviousFlashUnfinished,
                "A previous firmware write on this frame never finished — "
                + (_services.Window.InterruptedDetail ?? "no detail was recorded")
                + (attached.Count > 0
                    ? ". A microphone unit is still on the bus, so it may be well — this frame cannot tell."
                    : ". No microphone unit is on the bus at all, so the frame is showing the Safe Mode recovery "
                        + "gesture on its own screen.")
                + " Nothing further will be written until somebody has looked at the microphone unit and removed "
                + _services.Store.PathOf(ArrayFlashWindow.MarkerFileName) + ".",
                cancellationToken).ConfigureAwait(false);
        }

        if (_services.Values.Find(AuthorisationKey) is not { } authorisation)
        {
            _services.Approval.Withdraw();
            return Quiet(ArrayFlashRefusal.NotAuthorised, "No firmware write is authorised on this frame.");
        }

        if (string.Equals(Consumed(), authorisation, StringComparison.Ordinal))
        {
            _services.Approval.Withdraw();
            return Quiet(
                ArrayFlashRefusal.AlreadyConsumed,
                "This firmware authorisation has already been used. Authorising another write means writing a different value.");
        }

        var parsed = ArrayFlashAuthorisation.Parse(authorisation);
        if (!string.Equals(parsed.Digest, target.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            _services.Approval.Withdraw();
            return await RefuseAsync(
                ArrayFlashRefusal.NotThePinnedImage,
                $"The firmware authorisation names sha256 {Short(parsed.Digest)}, and the only image this build may write is "
                + $"{target.Name} at sha256 {Short(target.Sha256)}. Nothing was written.",
                cancellationToken).ConfigureAwait(false);
        }

        // Deferrals, before anything is spent. Both of these are ordinary and both come back on the
        // next tick with the authorisation still armed. Both also take the screen back: a firmware
        // question must never be sitting on top of a call somebody has just started.
        if (_services.CallActive?.Invoke() == true)
        {
            _services.Approval.Withdraw();
            return Quiet(ArrayFlashRefusal.CallInProgress, "Somebody is on a call; the firmware write is waiting.");
        }

        if (_services.RestartPending?.Invoke() == true)
        {
            _services.Approval.Withdraw();
            return Quiet(
                ArrayFlashRefusal.AgentRestartPending,
                "A new agent version is in place and this process is about to restart; the firmware write is waiting.");
        }

        if (await PreflightAsync(cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            // AlreadyAtTarget spends the authorisation and every other pre-flight refusal does not.
            // A frame that is already running the target has done what was asked, so leaving the
            // authorisation armed would let a later array swap be flashed by nobody's decision; a
            // frame missing an image or a tool has done nothing, and the operator's intent still
            // stands once the missing thing arrives.
            if (refusal.Kind == ArrayFlashRefusal.AlreadyAtTarget)
            {
                Consume(authorisation);
            }

            // Every pre-flight refusal takes this class's screen off the panel and none of them
            // puts one up. An unrecognised unit is narrated by `firmware.xvf3800.recognised`, which
            // is a rung of the graph and escalates — so the full-screen message is the reconciler's
            // stopped-frame narration, composed from the same ruling this refusal carries. A second
            // screen of this class's own would be decision 83's two surfaces disagreeing, and it
            // would be built on an ask window that is being removed.
            _services.Approval.Withdraw();

            return await RefuseAsync(refusal.Kind, refusal.Why, cancellationToken).ConfigureAwait(false);
        }

        // Last, and deliberately last. Everything above is a machine answering a machine, and none
        // of it needs a person; asking a household to stand by a frame that then refuses for a
        // missing image would teach them that the question means nothing. From here everything is
        // ready and the only thing left is whether somebody has said the write may start.
        if (await ApprovedAsync(authorisation, parsed, cancellationToken).ConfigureAwait(false) is { } waiting)
        {
            return waiting;
        }

        return await FlashAsync(authorisation, parsed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <b>The interlock that is a person</b> — null when the write may start, a refusal when not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mains loss during a DFU write is unguardable at the device and destroys the unit; the only
    /// mitigation that exists is somebody in the room who has been told that and has agreed to it.
    /// So this is the last gate and it is the one that cannot be satisfied by the frame itself: it
    /// puts <see cref="ArrayFlashVoice.Asking"/> on the panel and waits, and the authorisation stays
    /// armed for as long as it waits.
    /// </para>
    /// <para>
    /// <b>The operator's bypass is checked first and is scoped to this device.</b> A bypass naming
    /// another frame is not a bypass here — it is ignored, said out loud in the journal, and the
    /// household is asked exactly as it would have been — which is what stops a fleet-wide setting
    /// from silently skipping the local step everywhere.
    /// </para>
    /// </remarks>
    private async Task<ArrayFlashOutcome?> ApprovedAsync(
        string authorisation,
        ArrayFlashAuthorisation parsed,
        CancellationToken cancellationToken)
    {
        if (parsed.BypassesLocalApproval(_services.DeviceId))
        {
            _services.Approval.Approve(
                authorisation,
                "the fleet operator authorised it unattended for this device, accepting that "
                + string.Join(" ", UnattendedWarning));
            return null;
        }

        if (parsed.BypassNamesAnotherDevice(_services.DeviceId))
        {
            _services.Log.Warn(
                $"The firmware authorisation carries an unattended bypass for {parsed.UnattendedDeviceId}, which is "
                + $"not this frame ({_services.DeviceId}). The bypass is being ignored and somebody at this frame "
                + "will be asked, exactly as they would have been without it.");
        }

        if (string.Equals(_services.Approval.ApprovedFor, authorisation, StringComparison.Ordinal))
        {
            return null;
        }

        var asking = _services.Approval.Ask(authorisation);

        return await RefuseAsync(
            ArrayFlashRefusal.AwaitingLocalApproval,
            asking
                ? _services.Approval.Answerable
                    ? "A firmware write is authorised on this frame and is waiting for somebody standing at it to "
                        + "agree to it on the screen. Mains loss during the write is the one hazard nothing in this "
                        + "product can guard against, so the write does not start until a person has said they will "
                        + "not take the power away."
                    : "A firmware write is authorised on this frame, and this frame has no touchscreen — so nobody "
                        + "can agree to it here and nothing will be written. Either give the frame a working panel, "
                        + "or authorise the write unattended for this device by adding '"
                        + UnattendedPrefix + _services.DeviceId + "' to the authorisation's ticket, which accepts "
                        + "that " + string.Join(" ", UnattendedWarning)
                : "A firmware write is authorised on this frame and nobody at the frame has agreed to it. The screen "
                    + "has gone back to the product for now and will ask again later; the authorisation is still armed.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes this class's own screen off the panel when something else needs it, and nothing else.
    /// </summary>
    /// <remarks>
    /// <b>The cheap half of a tick, and the only half worth running quickly.</b> A firmware question
    /// covers whatever the panel was showing, and the one thing it may never cover is somebody's
    /// conversation — so between full ticks this runs on a short cadence and does exactly one thing.
    /// A full tick would re-hash six megabytes of pinned images and start three control-tool
    /// processes against the device the reconciler is also reading, every few seconds, for as long
    /// as a household took to answer a question.
    /// </remarks>
    /// <returns>Whether a screen was taken away.</returns>
    public bool StandDown()
    {
        if (_services.Approval.Prompt is null)
        {
            return false;
        }

        if (_services.CallActive?.Invoke() == true || _services.RestartPending?.Invoke() == true)
        {
            _services.Approval.Withdraw();
            return true;
        }

        // Nothing needs the panel back, so the other cheap thing worth doing is making sure the
        // screen still describes the frame it is on: the touchscreen is found by a watch of its own
        // and the operator's contact details arrive over the link, so either can land after a
        // screen has gone up.
        _services.Approval.Refresh();
        return false;
    }

    /// <summary>Ticks once, then on the interval, until asked to stop.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                // A failed tick costs one look at a setting. Taking the process down over it would
                // cost the frame its product, and the authorisation is still there next time.
                _services.Log.Warn($"An array firmware tick failed and was skipped: {exception.Message}");
            }

            // One long sleep on a frame with nothing to say — which is every frame, nearly always —
            // and a series of short ones while a screen of this class's own is up. What the short
            // ones buy is not a faster flash: it is a question that comes off the panel in seconds
            // when a call starts, rather than in up to a minute.
            var waited = TimeSpan.Zero;

            while (waited < DefaultInterval)
            {
                var slice = _services.Approval.Prompt is null ? DefaultInterval - waited : PromptInterval;

                try
                {
                    await _services.Clock.DelayAsync(slice, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                waited += slice;
                StandDown();
            }
        }
    }

    /// <summary>Everything the last pre-flight could read about the attached unit, or null.</summary>
    public ArrayIdentity? Identity { get; private set; }

    /// <summary>What the hardware gate concluded on the last pre-flight that reached it.</summary>
    public ArrayGateVerdict? Verdict { get; private set; }

    /// <summary>The whole of that conclusion — both halves of the message it composed.</summary>
    public ArrayGateRuling? Ruling { get; private set; }

    /// <summary>The firmware version the array reports over USB, or null.</summary>
    /// <remarks>
    /// The descriptor reading, which needs no tool, no root and no process. It is the pre-flight's
    /// idempotency check and half of the post-flash verify.
    /// </remarks>
    public string? DescriptorVersion()
    {
        var attached = XvfArrayUsb.Attached(_services.Files);
        return attached.Count == 1 ? XvfArrayUsb.Version(attached[0].BcdDevice) : null;
    }

    private async Task<(ArrayFlashRefusal Kind, string Why)?> PreflightAsync(CancellationToken cancellationToken)
    {
        var installer = _services.Installer;
        var pin = installer.Pin;

        if (!_services.Files.FileExists(DfuUtilPath))
        {
            return (ArrayFlashRefusal.DfuUtilMissing,
                $"{DfuUtilPath} is not installed on this frame, so nothing could write the image.");
        }

        // Re-hashed here and not trusted from the resource that installed it. A record that the
        // image was verified once would outlive the bytes it describes, and the reader that matters
        // is the one about to hand the file to dfu-util.
        if (!await installer.VerifyAsync(pin.Target, cancellationToken).ConfigureAwait(false))
        {
            return (ArrayFlashRefusal.ImageNotVerified,
                $"{XvfFirmwareInstaller.PathOf(pin.Target)} is missing or does not match the pinned digest, "
                + "and an unverified image must never be written to an array.");
        }

        // The way back has to be on the card before the way forward is taken. Fetching either of
        // these at the moment they are needed means fetching them onto a frame whose array is
        // already in trouble, possibly with no network.
        foreach (var image in new[] { pin.Recovery, pin.Fallback })
        {
            if (!await installer.VerifyAsync(image, cancellationToken).ConfigureAwait(false))
            {
                return (ArrayFlashRefusal.RecoveryNotVerified,
                    $"{XvfFirmwareInstaller.PathOf(image)} — {image.Purpose} — is missing or does not match the "
                    + "pinned digest, so this frame has no proven way back from a bad write.");
            }
        }

        var attached = XvfArrayUsb.Attached(_services.Files);

        if (attached.Count == 0)
        {
            return (ArrayFlashRefusal.NoArrayAttached,
                $"No {XvfArrayUsb.VendorId}:{XvfArrayUsb.ProductId} device is on this frame's USB bus.");
        }

        if (attached.Count > 1)
        {
            // Named, not counted. "More than one is attached" is a fact nobody can act on; the bus
            // path and the serial of each say which cable to pull.
            return (ArrayFlashRefusal.MoreThanOneArray,
                $"{attached.Count} microphone units are attached, and nothing here can say which one would be "
                + $"written: {ArrayHardwareGate.DescribeAttached(attached)}. Unplug every one except the unit this "
                + "frame is meant to use.");
        }

        // Read before the idempotency check rather than after it, which is a change from the
        // ordering this method used to have and is a correctness fix rather than a tidy-up.
        //
        // <b>"Already at target" cannot be concluded from the version, because the version does not
        // identify the image.</b> Upstream publishes v2.1.0, v2.1.0_16k6ch and v2.1.0_48k2ch, and
        // all three answer VERSION 2 1 0 — the collision is measured, not theorised: issues #22 and
        // #24 read VERSION 2 0 8 off the six-channel build. A frame carrying the 48 kHz variant
        // therefore used to be told "already on the pinned target, nothing was written", spend its
        // authorisation, and report convergence, while its echo canceller silently never converged
        // (issue #31, measured: AEC_AECCONVERGED reads 0 at every system delay on the 48 kHz
        // builds). Nothing in ALSA, PipeWire or the mixer would have said so.
        //
        // So the claim now needs the build configuration as well, and a unit whose profile cannot
        // be read reaches the gate instead — where the refusal names the missing tool and does not
        // spend, which is the right answer to "I cannot tell whether this is converged".
        var scan = await ArrayHardwareGate
            .ReadAsync(_services.Files, _services.Tool, cancellationToken)
            .ConfigureAwait(false);

        var running = XvfArrayUsb.Version(attached[0].BcdDevice);

        if (string.Equals(running, pin.Target.Version, StringComparison.Ordinal)
            && scan.Identity is { BuildConfiguration: { } profile }
            && string.Equals(profile, XvfFirmwarePin.Profile, StringComparison.Ordinal))
        {
            Identity = scan.Identity;

            return (ArrayFlashRefusal.AlreadyAtTarget,
                $"The microphone unit already reports firmware {running} on build configuration {profile}, which is "
                + "the pinned target, so nothing was written.");
        }

        // Everything below is the hardware gate, and it is still last: an unrecognised unit that is
        // genuinely already on the target has had nothing written to it either way, and letting
        // that case reach the spend above is what keeps decision 91's "a later array swap cannot be
        // flashed by nobody's decision" true. A gate refusal does not spend, so a frame whose unit
        // is not the target's image keeps its operator's intent armed until somebody has looked.

        // The revision nobody's software can read, read from the only place it can be: a value a
        // person typed. It is a veto and never a permission — ArrayBoardRevision holds the whole of
        // that decision, and it is the one thing in the ladder the operator has not settled.
        var ruling = ArrayHardwareGate.Judge(
            scan,
            pin,
            _services.Values.Find(ArrayBoardRevision.SettingKey));

        Identity = scan.Identity;
        Ruling = ruling;
        Verdict = ruling.Verdict;

        if (!ruling.MayWrite)
        {
            return (ArrayFlashRefusal.ArrayNotRecognised, ArrayHardwareGate.Explain(ruling));
        }

        return null;
    }

    private async Task<ArrayFlashOutcome> FlashAsync(
        string authorisation,
        ArrayFlashAuthorisation parsed,
        CancellationToken cancellationToken)
    {
        var pin = _services.Installer.Pin;
        var target = pin.Target;
        var path = XvfFirmwareInstaller.PathOf(target);
        var before = DescriptorVersion() ?? "unreadable";
        var started = _services.Clock.UtcNow;
        var unattended = parsed.BypassesLocalApproval(_services.DeviceId);

        // Spent first, durably, and only then is anything started. Everything after this line may
        // die at any instant; nothing after this line may authorise a second write. The whole
        // authorisation string goes in, which is what makes the operator's unattended bypass
        // single-use by the same act rather than by a mechanism of its own.
        Consume(authorisation);

        ProcessResult write;
        string? after;

        var scope = _services.Window.Open(
            $"writing {target.Name} (sha256 {Short(target.Sha256)}) to the microphone unit");
        var returned = false;

        // On the panel for the whole of the write, however it came to be agreed to. An unattended
        // write means nobody was standing there when it started — not that nobody will walk past
        // while it runs, and the person who does needs the same sentence the approver read.
        //
        // <b>Every word of it is drawn by a task of its own, and this thread draws none of them.</b>
        // The panel's publish reaches AgentStatusHub, which calls its subscribers synchronously —
        // one writes a frame to /dev/tty8 and another sends one to the browser — so a screen this
        // thread painted would sit between dfu-util and the drain of its own pipe. Claiming the
        // screen is an interlocked increment and returns; everything after it belongs to the pump.
        var progress = new ArrayFlashProgressBox(target.SizeBytes);
        using var pump = ArrayFlashProgressPump.Start(_services.Approval, progress, _services.Log);

        try
        {
            _services.Log.Info(
                $"Writing {target.Name} to the microphone unit: {DfuUtil} {string.Join(' ', Arguments(path))}. "
                + $"It reported firmware {before} before this.");

            // <b>The sink is the box and nothing else, and that is the load-bearing line.</b> It is
            // called on the thread draining the child's pipes, where a block would fill the pipe and
            // stall the write; it does a scan of one short line and one reference write, it cannot
            // throw, and it waits on nothing. Everything that can hang — the screen, the browser
            // channel, the socket to the Fleet Manager — is downstream of it on the pump's task,
            // which this thread never awaits and shares no token, no clock and no lock with.
            write = await _services.Processes
                .RunAsync(DfuUtil, Arguments(path), progress.Read, cancellationToken)
                .ConfigureAwait(false);

            // The write is over, one way or the other, from this line on.
            returned = true;

            // The stage nothing else could report. dfu-util has exited, so its output has stopped,
            // and what happens now is this frame watching the USB bus for up to ninety seconds. A
            // bar that reached 100% and then said nothing for that long is how a person concludes a
            // frame has hung, and what they reach for when they conclude it is the plug.
            progress.Enter(ArrayFlashStages.ReEnumerating);

            after = await AwaitReEnumerationAsync(target.Version, cancellationToken).ConfigureAwait(false);

            progress.Enter(ArrayFlashStages.Verifying);
        }
        finally
        {
            // <b>Closed only if `dfu-util` actually returned, and that asymmetry is the whole point
            // of the marker.</b> A plain `using` would clear it on the one path it exists for: the
            // agent's token being cancelled mid-write leaves `HostProcessRunner` abandoning — not
            // killing — the child, and systemd is about to take the whole cgroup down with it. A
            // marker cleared there would let the next process start a second write onto an array
            // whose Upgrade partition is in an unknown state. Left behind, it stops every later
            // flash until a person has looked. The in-process window stays open too, so this
            // process also stops updating and stops rebooting for whatever life it has left.
            if (returned)
            {
                scope.Dispose();
            }
        }

        var control = await ControlVersionAsync(cancellationToken).ConfigureAwait(false);
        var profile = await ControlProfileAsync(cancellationToken).ConfigureAwait(false);
        var elapsed = _services.Clock.UtcNow - started;

        // <b>The version alone cannot say a write landed, so the profile is read as well.</b> The
        // three v2.1.0 images upstream publishes all answer VERSION 2 1 0, so a unit that came back
        // on some other build of the same version would report exactly what a success reports. A
        // profile that reads and disagrees is therefore a failure whatever the version says; a
        // profile that will not read is not, because a unit that has only just re-enumerated may
        // simply not be answering its control interface yet, and calling a good write bad would
        // send somebody to a frame that is well.
        var succeeded =
            (string.Equals(after, target.Version, StringComparison.Ordinal)
                || string.Equals(control, target.Version, StringComparison.Ordinal))
            && (profile is null || string.Equals(profile, XvfFirmwarePin.Profile, StringComparison.Ordinal));

        var agreement = after is null || control is null
            ? string.Empty
            : string.Equals(after, control, StringComparison.Ordinal)
                ? " Both readings agree."
                : $" The two readings disagree: the USB descriptor says {after} and the control tool says {control}."
                    + " That has happened at this publisher before and is not by itself evidence that this unit is "
                    + "damaged: upstream issue #29 reports a file named v1.0.7 whose device answers 1.0.5, and it is "
                    + "still unanswered. Report it rather than assuming the board is broken.";

        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"{(succeeded ? "Wrote" : "Tried to write")} {target.Name} (sha256 {target.Sha256}) to the microphone unit "
            + $"in {elapsed.TotalSeconds:F0} s. It reported firmware {before} before and "
            + $"{after ?? "nothing"} after, the control tool answers {control ?? "nothing"}, and its build "
            + $"configuration reads {profile ?? "nothing"} against the pinned {XvfFirmwarePin.Profile}.{agreement}");

        // Who agreed to it is part of the trail, not a detail of how it was started. Six months
        // later "was anybody standing there?" is the first question anybody asks about a unit that
        // came back wrong, and an event that does not answer it makes the answer unknowable.
        summary += unattended
            ? " Nobody at the frame was asked: the fleet operator authorised this write unattended for this device, "
                + "accepting that " + string.Join(" ", UnattendedWarning)
            : " Somebody standing at the frame agreed to it on the screen before it started.";

        if (Identity is { } identity)
        {
            summary += " The unit this was written to reads as: " + identity.Describe() + ".";
        }

        // How far the tool got, in the trail rather than only on a screen nobody may still be
        // looking at. It matters most on the write that did not work: "it stopped at 62% of 933,888
        // bytes, at the download" is the difference between a unit whose Upgrade partition is half
        // written and one that took the whole image and would not come back, and those two need
        // different things doing to them.
        if (progress.Current is { BytesWritten: > 0 } reading)
        {
            summary += string.Create(
                CultureInfo.InvariantCulture,
                $" {DfuUtil} last reported {reading.BytesWritten} of {target.SizeBytes} bytes sent, at the "
                + $"'{reading.Stage}' stage.");
        }

        if (!succeeded)
        {
            summary += " The write did not produce the pinned firmware. Nothing further will be attempted on this "
                + "frame without a new authorisation, and somebody has to look at the unit.";
            _services.Log.Fail(summary);
        }
        else
        {
            _services.Log.Info(summary);
        }

        await _services.Telemetry.EventAsync(
            new DeviceEvent
            {
                DeviceId = _services.DeviceId,
                Kind = DeviceEventKinds.ArrayFlash,
                OccurredUtc = _services.Clock.UtcNow,
                Summary = summary,

                // The tool's own output, verbatim and whole. This is the one record of what the
                // device said while it was being written, and it is the first thing anybody
                // debugging a bad flash will want.
                Delta = write.Combined,
                Attempts = 1,
            },
            cancellationToken).ConfigureAwait(false);

        // Stopped before the outcome goes up, and stopped by a call that returns whatever the
        // reporting path is doing. The `using` above is the guarantee it happens at all; this is the
        // ordering, so that the last thing the panel is asked to draw is the outcome rather than one
        // more frame of a write that has finished. Disposing twice is a no-op by construction.
        pump.Dispose();

        // The screen the person who stood guard is owed: whether it worked, that they may unplug
        // the frame again, and something to press. It stays until somebody presses it or until the
        // linger runs out, because an unattended frame has nobody to press anything.
        //
        // <b>Guarded, because this is the one publish left on the writing thread.</b> The write is
        // over and its outcome is already in the journal and on its way to the Fleet Manager by the
        // time this runs, so a screen that cannot be painted has nothing left to spoil — but an
        // exception out of a hub subscriber here would leave the tick reporting a failure for a
        // write that worked, which is the same lie in the other direction. Reporting never decides
        // an outcome, at either end of the operation.
        try
        {
            _services.Approval.Finished(succeeded);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            _services.Log.Warn(
                "The firmware write finished and its outcome could not be put on the frame's screen: "
                + exception.Message
                + ". The outcome above is what happened; only the screen failed.");
        }

        _reported = null;
        _reportedWhy = null;
        return new ArrayFlashOutcome(null, Flashed: true, succeeded, summary);
    }

    /// <summary>Waits for the array to come back reporting <paramref name="version"/>.</summary>
    private async Task<string?> AwaitReEnumerationAsync(string version, CancellationToken cancellationToken)
    {
        var deadline = _services.Clock.UtcNow + ReEnumerationTimeout;
        string? seen = null;

        while (true)
        {
            seen = DescriptorVersion() ?? seen;

            if (string.Equals(seen, version, StringComparison.Ordinal) || _services.Clock.UtcNow >= deadline)
            {
                return seen;
            }

            await _services.Clock.DelayAsync(ReEnumerationPoll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The build configuration the unit reports now, which is what says <i>which</i> image landed.
    /// </summary>
    private async Task<string?> ControlProfileAsync(CancellationToken cancellationToken)
    {
        if (_services.Tool.Root() is not { } root)
        {
            return null;
        }

        var reply = await _services.Tool
            .RunAsync(root, [ArrayHardwareGate.BuildConfigurationCommand], cancellationToken)
            .ConfigureAwait(false);

        return ArrayHardwareGate.Field(reply.StandardOutput, ArrayHardwareGate.BuildConfigurationCommand)
            ?? ArrayHardwareGate.Field(reply.Combined, ArrayHardwareGate.BuildConfigurationCommand);
    }

    /// <summary>The control interface's own answer, when there is a tool to ask it with.</summary>
    private async Task<string?> ControlVersionAsync(CancellationToken cancellationToken)
    {
        if (_services.Tool.Root() is not { } root)
        {
            return null;
        }

        var reply = await _services.Tool
            .RunAsync(root, [XvfHost.VersionCommand], cancellationToken)
            .ConfigureAwait(false);

        return XvfHost.Version(reply.StandardOutput) ?? XvfHost.Version(reply.Combined);
    }

    private string? Consumed() => _services.Store.ReadText(ConsumedFileName)?.Trim();

    private void Consume(string authorisation) =>
        _services.Store.WriteSecretAtomic(ConsumedFileName, Encoding.UTF8.GetBytes(authorisation + "\n"));

    /// <summary>A refusal worth telling somebody about, reported once per change.</summary>
    private async Task<ArrayFlashOutcome> RefuseAsync(
        ArrayFlashRefusal refusal,
        string why,
        CancellationToken cancellationToken)
    {
        // The pair, not the kind. One refusal kind can carry materially different sentences — the
        // local approval is waiting on a person, or on a frame that has no touchscreen for one to
        // use — and gating on the kind alone would send whichever arrived first and silently drop
        // the one that told the operator what to do about it.
        if (_reported == refusal && string.Equals(_reportedWhy, why, StringComparison.Ordinal))
        {
            return new ArrayFlashOutcome(refusal, Flashed: false, Succeeded: false, why);
        }

        _reported = refusal;
        _reportedWhy = why;
        _services.Log.Warn(why);

        await _services.Telemetry.EventAsync(
            new DeviceEvent
            {
                DeviceId = _services.DeviceId,
                Kind = DeviceEventKinds.ArrayFlash,
                OccurredUtc = _services.Clock.UtcNow,
                Summary = why,
                Delta = "expected 'a firmware write', observed 'refused: " + refusal + "'",
            },
            cancellationToken).ConfigureAwait(false);

        return new ArrayFlashOutcome(refusal, Flashed: false, Succeeded: false, why);
    }

    /// <summary>A refusal nobody needs telling about, because it is the ordinary state.</summary>
    private ArrayFlashOutcome Quiet(ArrayFlashRefusal refusal, string why)
    {
        _reported = refusal;
        _reportedWhy = why;
        return new ArrayFlashOutcome(refusal, Flashed: false, Succeeded: false, why);
    }

    private static string Short(string digest) => digest.Length <= 12 ? digest : digest[..12];
}
