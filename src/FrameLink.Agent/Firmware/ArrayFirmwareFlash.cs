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
/// <b>One attempt. Never a second.</b> Retrying a partial write is the documented route from a
/// recoverable board to an unrecoverable one, so there is no retry anywhere in this class: the
/// authorisation is already spent by the time <c>dfu-util</c> runs, a failure emits an event for a
/// person and nothing else, and the next flash needs a human. §2.5's attempt budget is not involved
/// at all, because the danger here is not a second attempt — it is the first one being interrupted,
/// which an attempt counter cannot see.
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
        // nothing knows how far it got; starting a second one onto that array is how a recoverable
        // board becomes an unrecoverable one. Only a person deletes this.
        if (_services.Window.Interrupted)
        {
            return await RefuseAsync(
                ArrayFlashRefusal.PreviousFlashUnfinished,
                "A previous firmware write on this frame never finished — "
                + (_services.Window.InterruptedDetail ?? "no detail was recorded")
                + ". Nothing further will be written until somebody has looked at the microphone unit and removed "
                + _services.Store.PathOf(ArrayFlashWindow.MarkerFileName) + ".",
                cancellationToken).ConfigureAwait(false);
        }

        if (_services.Values.Find(AuthorisationKey) is not { } authorisation)
        {
            return Quiet(ArrayFlashRefusal.NotAuthorised, "No firmware write is authorised on this frame.");
        }

        if (string.Equals(Consumed(), authorisation, StringComparison.Ordinal))
        {
            return Quiet(
                ArrayFlashRefusal.AlreadyConsumed,
                "This firmware authorisation has already been used. Authorising another write means writing a different value.");
        }

        var digest = authorisation.Split(':', 2)[0].Trim();
        if (!string.Equals(digest, target.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return await RefuseAsync(
                ArrayFlashRefusal.NotThePinnedImage,
                $"The firmware authorisation names sha256 {Short(digest)}, and the only image this build may write is "
                + $"{target.Name} at sha256 {Short(target.Sha256)}. Nothing was written.",
                cancellationToken).ConfigureAwait(false);
        }

        // Deferrals, before anything is spent. Both of these are ordinary and both come back on the
        // next tick with the authorisation still armed.
        if (_services.CallActive?.Invoke() == true)
        {
            return Quiet(ArrayFlashRefusal.CallInProgress, "Somebody is on a call; the firmware write is waiting.");
        }

        if (_services.RestartPending?.Invoke() == true)
        {
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

            return await RefuseAsync(refusal.Kind, refusal.Why, cancellationToken).ConfigureAwait(false);
        }

        return await FlashAsync(authorisation, cancellationToken).ConfigureAwait(false);
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

            try
            {
                await _services.Clock.DelayAsync(DefaultInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

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
            return (ArrayFlashRefusal.MoreThanOneArray,
                $"{attached.Count} microphone units are attached, and nothing here can say which one would be written.");
        }

        var running = XvfArrayUsb.Version(attached[0].BcdDevice);
        if (string.Equals(running, pin.Target.Version, StringComparison.Ordinal))
        {
            return (ArrayFlashRefusal.AlreadyAtTarget,
                $"The microphone unit already reports firmware {running}, which is the pinned target, so nothing was written.");
        }

        return null;
    }

    private async Task<ArrayFlashOutcome> FlashAsync(string authorisation, CancellationToken cancellationToken)
    {
        var pin = _services.Installer.Pin;
        var target = pin.Target;
        var path = XvfFirmwareInstaller.PathOf(target);
        var before = DescriptorVersion() ?? "unreadable";
        var started = _services.Clock.UtcNow;

        // Spent first, durably, and only then is anything started. Everything after this line may
        // die at any instant; nothing after this line may authorise a second write.
        Consume(authorisation);

        ProcessResult write;
        string? after;

        var scope = _services.Window.Open(
            $"writing {target.Name} (sha256 {Short(target.Sha256)}) to the microphone unit");
        var returned = false;

        try
        {
            _services.Log.Info(
                $"Writing {target.Name} to the microphone unit: {DfuUtil} {string.Join(' ', Arguments(path))}. "
                + $"It reported firmware {before} before this.");

            write = await _services.Processes
                .RunAsync(DfuUtil, Arguments(path), cancellationToken)
                .ConfigureAwait(false);

            // The write is over, one way or the other, from this line on.
            returned = true;

            after = await AwaitReEnumerationAsync(target.Version, cancellationToken).ConfigureAwait(false);
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
        var elapsed = _services.Clock.UtcNow - started;
        var succeeded = string.Equals(after, target.Version, StringComparison.Ordinal)
            || string.Equals(control, target.Version, StringComparison.Ordinal);

        var agreement = after is null || control is null
            ? string.Empty
            : string.Equals(after, control, StringComparison.Ordinal)
                ? " Both readings agree."
                : $" The two readings disagree: the USB descriptor says {after} and the control tool says {control}.";

        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"{(succeeded ? "Wrote" : "Tried to write")} {target.Name} (sha256 {target.Sha256}) to the microphone unit "
            + $"in {elapsed.TotalSeconds:F0} s. It reported firmware {before} before and "
            + $"{after ?? "nothing"} after, and the control tool answers {control ?? "nothing"}.{agreement}");

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

        _reported = null;
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
        if (_reported == refusal)
        {
            return new ArrayFlashOutcome(refusal, Flashed: false, Succeeded: false, why);
        }

        _reported = refusal;
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
        return new ArrayFlashOutcome(refusal, Flashed: false, Succeeded: false, why);
    }

    private static string Short(string digest) => digest.Length <= 12 ? digest : digest[..12];
}
