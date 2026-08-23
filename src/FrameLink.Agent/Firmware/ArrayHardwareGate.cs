using System.Globalization;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Resources;

namespace FrameLink.Agent.Firmware;

/// <summary>What this build concluded about the unit it is looking at.</summary>
public enum ArrayGateVerdict
{
    /// <summary>Everything readable about this unit is something this build has been told about.</summary>
    Recognised,

    /// <summary>Nothing is on the bus, or more than one thing is.</summary>
    NoSingleArray,

    /// <summary>The control tool is not installed, so the build configuration cannot be read.</summary>
    ControlToolMissing,

    /// <summary>The tool is installed and the unit did not answer it.</summary>
    ControlSilent,

    /// <summary>The USB descriptor and the control interface report different firmware.</summary>
    ReadingsDisagree,

    /// <summary>The firmware this unit runs is not one this build has ever been told about.</summary>
    UnknownFirmware,

    /// <summary>The unit's build configuration is not the one the pinned image is built for.</summary>
    UnknownBuildConfiguration,
}

/// <summary>
/// Everything a frame can actually read about the microphone unit plugged into it.
/// </summary>
/// <param name="VendorId">USB <c>idVendor</c>, as sysfs spells it.</param>
/// <param name="ProductId">USB <c>idProduct</c>, as sysfs spells it.</param>
/// <param name="BcdDevice">The raw <c>bcdDevice</c> field, which encodes the firmware version.</param>
/// <param name="Serial">The unit's USB serial, which identifies the unit and not the design.</param>
/// <param name="DescriptorVersion">The firmware version decoded from <paramref name="BcdDevice"/>.</param>
/// <param name="ControlVersion">The firmware version the control interface reports, or null.</param>
/// <param name="BuildConfiguration">The <c>BLD_MSG</c> build profile, or null.</param>
/// <param name="BuildRepositoryHash">The <c>BLD_REPO_HASH</c> fingerprint, or null.</param>
/// <remarks>
/// <para>
/// <b>Board revision is not a field here, and that is a finding rather than an omission.</b> It is
/// not in the USB descriptors, and it is not in the control tool's command set either: all 177
/// commands in the pinned <c>libcommand_map.so</c> were enumerated, and every identity command among
/// them — <c>VERSION</c>, <c>BLD_MSG</c>, <c>BLD_HOST</c>, <c>BLD_REPO_HASH</c>,
/// <c>BLD_MODIFIED</c>, <c>BOOT_STATUS</c>, <c>SERIAL_NUMBER</c>, <c>DFU_GETVERSION</c> — describes
/// the <i>firmware</i> or the <i>unit</i>, never the board. The revision is silkscreen. So the one
/// gate a reader of upstream issue #32 would reach for first, <i>refuse to write to a V1.1 board</i>,
/// cannot be written at all, and this file does not pretend otherwise.
/// </para>
/// <para>
/// <b><c>BLD_REPO_HASH</c> is carried and never gated on</b>, for a measured reason: it is a stable,
/// reproducible per-build fingerprint that resolves to nothing anybody outside XMOS can look up —
/// <c>sw_xvf3800</c> does not exist as a public repository — and the unit on this project's own
/// frame answers <c>BLD_MODIFIED TRUE</c>, so the hash names a base commit rather than the bytes on
/// the board. It is worth recording in the event trail, because it tells two boards apart with
/// certainty; it is worth nothing as a gate, because there is no set of known-good values to
/// compare it against.
/// </para>
/// </remarks>
public readonly record struct ArrayIdentity(
    string VendorId,
    string ProductId,
    string BcdDevice,
    string Serial,
    string? DescriptorVersion,
    string? ControlVersion,
    string? BuildConfiguration,
    string? BuildRepositoryHash)
{
    /// <summary>The whole reading in one sentence, for the event trail and for a refusal.</summary>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"USB {VendorId}:{ProductId}, bcdDevice {BcdDevice} = firmware {DescriptorVersion ?? "undecodable"}, "
        + $"the control interface answers {ControlVersion ?? "nothing"}, build configuration "
        + $"{BuildConfiguration ?? "unreadable"}, build hash {BuildRepositoryHash ?? "unreadable"}, "
        + $"serial {(Serial.Length == 0 ? "(none)" : Serial)}");
}

/// <summary>
/// <b>Refuse to write firmware to a unit this build cannot recognise, and say so loudly.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>The worry this answers is future hardware, and the operator is right to have it.</b> The
/// pinned image is a single build for a single audio topology on a single product; a unit bought in
/// two years' time may be a different revision, a different profile or a different product wearing
/// the same vendor and product id. Writing 933 KB of firmware into a device on the hope that it is
/// the one this build was tested against is exactly the operation with no undo, so this class asks
/// the opposite question: <i>is everything I can read about this unit something I have been told
/// about?</i> — and refuses when the answer is no, rather than proceeding hopefully.
/// </para>
/// <para>
/// <b>What it gates on, and why each one is worth a refusal.</b>
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Vendor and product</b> — <c>2886:001a</c>. Already the selector <see cref="XvfArrayUsb"/> uses
/// to find an array at all, restated here so the gate is a complete statement of the identity rather
/// than a set of extra checks on top of an assumption made elsewhere.
/// </description></item>
/// <item><description>
/// <b>Exactly one unit</b>. The control tool has no device selector — its USB backend opens whichever
/// array enumerates first — so with two attached, every reading below describes an unknown one of
/// them.
/// </description></item>
/// <item><description>
/// <b>The two firmware readings must agree.</b> The USB descriptor's <c>bcdDevice</c> and the control
/// interface's <c>VERSION</c> are independent routes to the same fact, and this build reads both
/// anyway. A unit on which they disagree is a unit this build cannot describe, and the honest answer
/// to <i>which one is true</i> is that nothing here knows.
/// </description></item>
/// <item><description>
/// <b>The running firmware must be one this build has been told about</b>
/// (<see cref="KnownFirmware"/>). This is the closest thing to a hardware gate that exists, and it
/// is deliberately indirect: a unit running firmware nobody here has ever seen is evidence of a unit
/// outside what this build was written against, even though the evidence is about software.
/// </description></item>
/// <item><description>
/// <b>The build configuration must be the one the pinned image is built for</b> —
/// <see cref="XvfFirmwarePin.Profile"/>, <c>ua-io16-sqr</c>: two channels, 16 kHz, square array.
/// This is the strongest real gate in the list. Upstream publishes six-channel and 48 kHz builds
/// under names one character apart, and writing the two-channel image onto a unit configured for six
/// changes the frame's audio topology underneath every mixer resource in the catalog. It is also the
/// one field that would catch a genuinely different product wearing the same USB ids.
/// </description></item>
/// </list>
/// <para>
/// <b>An unreadable identity is a refusal, not a shrug.</b> If the control tool is missing, or the
/// unit does not answer it, the build configuration cannot be read — and writing without it is
/// precisely the hopeful proceeding this class exists to stop. The tool is an ordinary resource
/// (<c>tool.xvf-host.installed</c>) that a converged frame has, so this refusal names something the
/// reconciler can fix.
/// </para>
/// <para>
/// <b>What the gate cannot see, stated rather than implied.</b> Board revision, at all — see
/// <see cref="ArrayIdentity"/>. Whether the unit in front of it is healthy: a board can report the
/// right ids, the right version and the right profile and still be a unit somebody has half-bricked.
/// And whether a <i>future</i> firmware version is safe: <see cref="KnownFirmware"/> is a list a
/// human edits, so it can only ever say what has already been established, which is the property
/// that makes it a gate rather than a guess.
/// </para>
/// </remarks>
public static class ArrayHardwareGate
{
    /// <summary>The <c>BLD_MSG</c> command — the build configuration the firmware was built for.</summary>
    public const string BuildConfigurationCommand = "BLD_MSG";

    /// <summary>The <c>BLD_REPO_HASH</c> command — a per-build fingerprint that names no build.</summary>
    public const string BuildHashCommand = "BLD_REPO_HASH";

    /// <summary>
    /// Every firmware version this build has been told about, in <c>xvf_host</c>'s own spelling.
    /// </summary>
    /// <remarks>
    /// <b>Observed or pinned, and nothing else.</b> <c>2 0 6</c> is the version both of this
    /// project's arrays shipped with; <c>2 0 10</c> is what Frame #1's array reports and has been
    /// read from it twice; <c>2 1 0</c> is the pinned target. Upstream publishes others and they are
    /// deliberately absent: a version nobody here has seen is exactly the case this gate exists to
    /// refuse, and adding one is a source edit somebody has to mean, in the same shape as bumping
    /// the pin itself.
    /// </remarks>
    public static IReadOnlyList<string> KnownFirmware { get; } = ["2 0 6", "2 0 10", "2 1 0"];

    /// <summary>Reads everything this frame can read about the attached unit.</summary>
    /// <remarks>
    /// Two process starts on a path that runs at most once in a frame's life, against a device that
    /// is about to be written to. The descriptor half needs no tool, no root and no process at all,
    /// so a frame with no control tool still produces a partial identity — which
    /// <see cref="Judge"/> then refuses on, rather than this method inventing the missing fields.
    /// </remarks>
    public static async Task<ArrayIdentity?> ReadAsync(
        ISystemFiles files,
        XvfHost tool,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(tool);

        var attached = XvfArrayUsb.Attached(files);
        if (attached.Count != 1)
        {
            return null;
        }

        var device = attached[0];
        string? version = null;
        string? configuration = null;
        string? hash = null;

        if (tool.Root() is { } root)
        {
            var reported = await tool
                .RunAsync(root, [XvfHost.VersionCommand], cancellationToken)
                .ConfigureAwait(false);

            version = XvfHost.Version(reported.StandardOutput) ?? XvfHost.Version(reported.Combined);
            configuration = await FieldAsync(tool, root, BuildConfigurationCommand, cancellationToken)
                .ConfigureAwait(false);
            hash = await FieldAsync(tool, root, BuildHashCommand, cancellationToken).ConfigureAwait(false);
        }

        return new ArrayIdentity(
            XvfArrayUsb.VendorId,
            XvfArrayUsb.ProductId,
            device.BcdDevice.Trim(),
            device.Serial.Trim(),
            XvfArrayUsb.Version(device.BcdDevice),
            version,
            configuration,
            hash);
    }

    /// <summary>Whether this build may write the pinned image to the unit it just read.</summary>
    public static ArrayGateVerdict Judge(ArrayIdentity? identity, XvfFirmwarePin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);

        if (identity is not { } unit)
        {
            return ArrayGateVerdict.NoSingleArray;
        }

        if (!string.Equals(unit.VendorId, XvfArrayUsb.VendorId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(unit.ProductId, XvfArrayUsb.ProductId, StringComparison.OrdinalIgnoreCase))
        {
            return ArrayGateVerdict.NoSingleArray;
        }

        if (unit.ControlVersion is null && unit.BuildConfiguration is null && unit.BuildRepositoryHash is null)
        {
            return ArrayGateVerdict.ControlToolMissing;
        }

        if (unit.ControlVersion is null || unit.BuildConfiguration is null)
        {
            return ArrayGateVerdict.ControlSilent;
        }

        if (unit.DescriptorVersion is not { } descriptor
            || !string.Equals(descriptor, unit.ControlVersion, StringComparison.Ordinal))
        {
            return ArrayGateVerdict.ReadingsDisagree;
        }

        if (!KnownFirmware.Contains(descriptor, StringComparer.Ordinal))
        {
            return ArrayGateVerdict.UnknownFirmware;
        }

        return string.Equals(unit.BuildConfiguration, XvfFirmwarePin.Profile, StringComparison.Ordinal)
            ? ArrayGateVerdict.Recognised
            : ArrayGateVerdict.UnknownBuildConfiguration;
    }

    /// <summary>Why a verdict refused, in a sentence an operator can act on.</summary>
    public static string Explain(ArrayGateVerdict verdict, ArrayIdentity? identity)
    {
        var reading = identity is { } unit
            ? " What this frame can read about it: " + unit.Describe() + "."
            : string.Empty;

        var tail = " This build will not write firmware to a unit it cannot recognise, and board revision is not "
            + "readable in software at all — not in the USB descriptors and not in the control tool's command set — "
            + "so no gate can be written on it.";

        return verdict switch
        {
            ArrayGateVerdict.Recognised =>
                "The microphone unit is one this build recognises." + reading,
            ArrayGateVerdict.NoSingleArray =>
                "There is not exactly one recognisable microphone unit on this frame's USB bus, so nothing here can "
                + "say what would be written to." + reading,
            ArrayGateVerdict.ControlToolMissing =>
                "The microphone unit's control tool is not installed on this frame, so its build configuration cannot "
                + "be read — and writing firmware without knowing which build a unit is configured for is exactly what "
                + "this refusal exists to prevent." + reading + tail,
            ArrayGateVerdict.ControlSilent =>
                "The microphone unit did not answer its control interface, so its build configuration could not be "
                + "read." + reading + tail,
            ArrayGateVerdict.ReadingsDisagree =>
                "The microphone unit's two firmware readings disagree — the USB descriptor and the control interface "
                + "report different versions — so nothing here can say which firmware it is actually running."
                + reading + tail,
            ArrayGateVerdict.UnknownFirmware =>
                "The microphone unit reports a firmware version this build has never been told about. The versions it "
                + "knows are " + string.Join(", ", KnownFirmware) + "." + reading + tail,
            _ =>
                "The microphone unit reports a build configuration this build has never been told about. The pinned "
                + "image is built for " + XvfFirmwarePin.Profile + " — two channels at 16 kHz on a square array — and "
                + "writing it to a unit configured for anything else would change this frame's audio topology "
                + "underneath every mixer setting on it." + reading + tail,
        };
    }

    /// <summary>
    /// One <c>NAME value</c> field out of a control-tool reply.
    /// </summary>
    /// <remarks>
    /// <b>The NUL padding is the whole reason this is not <c>XvfHost.Version</c>.</b> <c>BLD_MSG</c>,
    /// <c>BLD_HOST</c> and <c>BLD_MODIFIED</c> arrive padded to fixed widths — 39, 28 and 2 NULs
    /// respectively, measured on this project's own array — and the tool prints them raw, so they
    /// look like trailing spaces and are not. <c>string.Split</c> on whitespace does not remove
    /// them, and a value carrying its padding compares unequal to the same value read anywhere else,
    /// which would make this gate refuse every unit it was pointed at.
    /// </remarks>
    public static string? Field(string output, string command)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim('\0', '\r', ' ', '\t');

            if (!line.StartsWith(command, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = line[command.Length..].Trim('\0', '\r', ' ', '\t');
            if (rest.Length > 0)
            {
                return rest;
            }
        }

        return null;
    }

    private static async Task<string?> FieldAsync(
        XvfHost tool,
        string root,
        string command,
        CancellationToken cancellationToken)
    {
        var reply = await tool.RunAsync(root, [command], cancellationToken).ConfigureAwait(false);
        return Field(reply.StandardOutput, command) ?? Field(reply.Combined, command);
    }
}
