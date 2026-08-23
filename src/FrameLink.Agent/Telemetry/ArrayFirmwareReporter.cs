using System.Globalization;
using FrameLink.Agent.Firmware;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Resources;
using FrameLink.Protocol;

namespace FrameLink.Agent.Telemetry;

/// <summary>
/// Reads which firmware the microphone unit is running, and reports it. It never writes one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not a resource</b> (decision 90). §2.2's unit is "the smallest independently
/// verifiable setting", and §2.3's contract is <b>Observe → Compare → Act (only on drift) →
/// Verify</b>. The array's firmware version fails the second half of that and cannot be made to
/// pass it: the only Act that could converge it is a DFU write, the operator has decided this
/// product will never perform one unattended, and a resource whose Act cannot succeed is exactly
/// what decision 63 diagnosed — <i>the fault was the resource having no Act that could succeed, not
/// the ladder being wrong about a resource that cannot converge</i>. There the fix was to give it a
/// real Act. Here there is no real Act to give, so the honest move is the other one, and it is the
/// move <see cref="PackageInventoryReporter"/> already makes for the same reason: this observes and
/// reports and never acts.
/// </para>
/// <para>
/// <b>What a no-op Act would have cost, stated so nobody re-proposes it.</b> A resource that
/// observed the version, compared it against a pin and did nothing would walk §2.5's ladder — three
/// attempts, three reboots — and then escalate, and by decision 68 that escalation stops the whole
/// pass, so a frame carrying a 2.0.6 array would never converge its screen, its camera or its
/// speaker. A resource that instead reported <c>InSync</c> after doing nothing would be the v1
/// governor shape the whole contract exists to forbid: a check that reports success because the
/// write returned rather than because the world is right.
/// </para>
/// <para>
/// <b>Two independent readings, and the cheap one is the one that always works.</b>
/// <see cref="XvfArrayUsb"/> reads <c>bcdDevice</c> out of sysfs — no control tool, no root, no USB
/// control transfer, no process at all — and <c>xvf_host VERSION</c> asks the array's own control
/// interface when the tool is installed. Reporting both, and whether they agree, is worth more than
/// either alone: the descriptor answers on a frame whose tool is missing, and a disagreement
/// between them is a real diagnosis neither reading can produce by itself.
/// </para>
/// <para>
/// <b>Board revision is not among the fields, and that is a finding rather than an omission.</b> It
/// is not in the USB descriptors, and it is not in the control tool's command set either: the 177
/// commands in the pinned <c>libcommand_map.so</c> include <c>VERSION</c>, <c>BLD_MSG</c>,
/// <c>BLD_HOST</c>, <c>BLD_REPO_HASH</c>, <c>BLD_MODIFIED</c>, <c>BOOT_STATUS</c>,
/// <c>SERIAL_NUMBER</c> and <c>DFU_GETVERSION</c>, every one of which describes the <i>firmware</i>
/// or the unit and none of which describes the board. The revision is silkscreen. A fleet therefore
/// cannot know it, and any future decision that would gate on it — upstream issue #32 reports
/// firmware 2.0.10 not booting at all on a V1.1 board — has no software input to gate on.
/// </para>
/// <para>
/// <b>The cadence is the package inventory's, for the package inventory's reasons.</b> The reading
/// moves only when somebody physically flashes or swaps an array, which now happens only attended,
/// on a bench, with <c>fl-agent</c> stopped — so the moment that matters is the next agent start,
/// and a slow tick behind it catches a hot-swap. A content comparison decides whether anything is
/// sent at all, persisted in <see cref="StateFileName"/> so a reboot does not re-report an array
/// that has not changed.
/// </para>
/// </remarks>
public sealed class ArrayFirmwareReporter
{
    /// <summary>Where the last reported reading is remembered.</summary>
    public const string StateFileName = "array-firmware.state";

    /// <summary>How often the array is re-read.</summary>
    /// <remarks>
    /// Six hours, matching <see cref="PackageInventoryReporter.DefaultInterval"/>. Nothing on a
    /// running frame moves this value, so the interval is sized against the one case it exists for
    /// — an array unplugged and replaced under a running agent — rather than against any rate. It
    /// takes no fleet setting for the same reason: there is no operating condition under which a
    /// different number would be better, so a knob would be one more thing to get wrong.
    /// </remarks>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(6);

    private readonly XvfHost _tool;
    private readonly ISystemFiles _files;
    private readonly IReconcileTelemetry _telemetry;
    private readonly IStateStore _store;
    private readonly IAgentClock _clock;
    private readonly IAgentLog _log;

    /// <summary>Creates a reporter for one frame.</summary>
    public ArrayFirmwareReporter(
        XvfHost tool,
        ISystemFiles files,
        IReconcileTelemetry telemetry,
        IStateStore store,
        IAgentClock clock,
        IAgentLog log)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(log);

        _tool = tool;
        _files = files;
        _telemetry = telemetry;
        _store = store;
        _clock = clock;
        _log = log;
    }

    /// <summary>The frame this reports for.</summary>
    public required string DeviceId { get; init; }

    /// <summary>The whole reading, as one sentence, or null on a machine with no USB sysfs.</summary>
    /// <remarks>
    /// Null and "no unit attached" are deliberately different answers. A workstation or container
    /// running the agent has no <c>/sys/bus/usb/devices</c> and has nothing to say; a frame with
    /// that directory and no <c>2886:001a</c> inside it has said something an operator wants to
    /// hear, which is that this frame has no microphone unit plugged into it.
    /// </remarks>
    public async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!XvfArrayUsb.Enumerable(_files))
        {
            return null;
        }

        var attached = XvfArrayUsb.Attached(_files);
        if (attached.Count == 0)
        {
            return "No microphone unit is attached to this frame: the USB bus lists no "
                + XvfArrayUsb.VendorId + ":" + XvfArrayUsb.ProductId + " device.";
        }

        var descriptors = new List<string>(attached.Count);
        foreach (var array in attached)
        {
            var decoded = XvfArrayUsb.Version(array.BcdDevice);
            var firmware = decoded is null ? string.Empty : " = firmware " + decoded;
            var serial = array.Serial.Length == 0 ? "(none)" : array.Serial;

            descriptors.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"USB {array.Path} bcdDevice {array.BcdDevice}{firmware}, serial {serial}"));
        }

        var text = attached.Count == 1
            ? "The microphone unit reports " + descriptors[0] + "."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{attached.Count} microphone units are attached: {string.Join("; ", descriptors)}.");

        return text
            + " " + await ControlAsync(attached, cancellationToken).ConfigureAwait(false)
            + " " + AgainstTarget(attached);
    }

    /// <summary>How this frame stands against the firmware the fleet converges on.</summary>
    /// <remarks>
    /// <para>
    /// <b>This sentence is what "the fleet converges on the latest" is made of</b> (decision 91).
    /// Nothing on a frame decides to write firmware on its own, so the convergence property has to
    /// be delivered somewhere else: every frame says, unprompted and on every change, which firmware
    /// it runs and whether that is the pinned target. The Fleet Manager can then answer "which
    /// frames are behind" for the whole fleet from data it already stores, and an operator turns
    /// that into a write one deliberate authorisation at a time.
    /// </para>
    /// <para>
    /// It is deliberately a <i>sentence in a report</i> and not a status. §2.6's ladder answers one
    /// question — does the product run? — and a frame on 2.0.6 runs the product perfectly well.
    /// Making this a device state would stop a working frame over a number, which is the exact
    /// failure decision 90 removed.
    /// </para>
    /// </remarks>
    private static string AgainstTarget(IReadOnlyList<XvfArrayDevice> attached)
    {
        var target = XvfFirmwarePin.Current.Target;
        var running = attached.Count == 1 ? XvfArrayUsb.Version(attached[0].BcdDevice) : null;

        if (running is null)
        {
            return $"The firmware this fleet converges on is {target.Version}; this frame's reading could not be decoded.";
        }

        return string.Equals(running, target.Version, StringComparison.Ordinal)
            ? $"That is the firmware this fleet converges on ({target.Version})."
            : $"The firmware this fleet converges on is {target.Version}, so this unit is not on it.";
    }

    /// <summary>Reads the array, and reports if what it says has changed.</summary>
    /// <returns>True when an event was handed to telemetry.</returns>
    public async Task<bool> TickAsync(CancellationToken cancellationToken)
    {
        if (await ReadAsync(cancellationToken).ConfigureAwait(false) is not { } reading)
        {
            return false;
        }

        if (string.Equals(_store.ReadText(StateFileName)?.Trim(), reading, StringComparison.Ordinal))
        {
            return false;
        }

        await _telemetry.EventAsync(
            new DeviceEvent
            {
                DeviceId = DeviceId,
                Kind = DeviceEventKinds.ArrayFirmware,
                OccurredUtc = _clock.UtcNow,
                Summary = reading,
            },
            cancellationToken).ConfigureAwait(false);

        // Advanced on handing over rather than on delivery, exactly as the package inventory's hash
        // is: an offline frame's event goes to the bounded on-disk buffer and drains on reconnect,
        // and what must never happen is the record advancing over something neither sent nor stored.
        _store.WriteText(StateFileName, reading + "\n");
        _log.Info(reading);
        return true;
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
                // Reporting is observation, so a failed tick costs visibility and nothing else.
                // Taking the process down over it would cost the frame its product.
                _log.Warn($"An array firmware tick failed and was skipped: {exception.Message}");
            }

            try
            {
                await _clock.DelayAsync(DefaultInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>The control interface's own answer, when there is a tool to ask it with.</summary>
    /// <remarks>
    /// <c>VERSION</c> is the only command this class can name, and it is a read. The write commands
    /// — <c>GPO_WRITE_VALUE</c>, and <c>dfu-util</c> outside the tool entirely — appear nowhere in
    /// this file, which is what makes "observe-only" a property of the code rather than a claim
    /// about it. The call goes through <see cref="XvfHost"/>, so it is serialised against the
    /// reconcile loop's own reads of the same device.
    /// </remarks>
    private async Task<string> ControlAsync(
        IReadOnlyList<XvfArrayDevice> attached,
        CancellationToken cancellationToken)
    {
        if (_tool.Root() is not { } root)
        {
            return XvfHost.Binary + " is not installed, so the control interface was not asked.";
        }

        if (attached.Count > 1)
        {
            // xvf_host has no device selector, so with two arrays attached its answer describes
            // whichever one enumeration handed it and says nothing about which that was. Declining
            // to ask is the only honest reading available.
            return XvfHost.Binary + " was not asked: it cannot say which of these units it reached.";
        }

        var reply = await _tool
            .RunAsync(root, [XvfHost.VersionCommand], cancellationToken)
            .ConfigureAwait(false);

        if ((XvfHost.Version(reply.StandardOutput) ?? XvfHost.Version(reply.Combined)) is not { } reported)
        {
            return XvfHost.Binary + " did not report a version.";
        }

        var decoded = XvfArrayUsb.Version(attached[0].BcdDevice);
        var agreement = decoded is null
            ? string.Empty
            : string.Equals(reported, decoded, StringComparison.Ordinal)
                ? ", agreeing with the USB descriptor"
                : ", which disagrees with the USB descriptor";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{XvfHost.Binary} {XvfHost.VersionCommand} answers {reported}{agreement}.");
    }
}
