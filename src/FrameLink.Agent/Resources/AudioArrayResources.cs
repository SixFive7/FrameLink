using System.Globalization;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Agent.Resources;

/// <summary>One entry of <c>/proc/asound/cards</c>.</summary>
/// <param name="Index">The card number the kernel gave it.</param>
/// <param name="Id">The short id in brackets — <c>Array</c> for the XVF3800.</param>
/// <param name="Description">Everything after the colon.</param>
public readonly record struct AlsaCard(int Index, string Id, string Description);

/// <summary>
/// The kernel's own list of sound cards, which is the <i>effect</i> half of guide 4 step 1.
/// </summary>
/// <remarks>
/// <para>
/// Two resources write settings whose whole purpose is to decide who owns card 0 —
/// <see cref="SndUsbAudioIndexResource"/> claims it for the array, and
/// <see cref="HdmiAudioOffResource"/> removes the only other claimant — and neither can be
/// judged from the file it wrote. §2.4 is explicit that "applied" is never claimed from a
/// successful write, and this file is where both of them are actually settled.
/// </para>
/// <para>
/// <b>An absent file is not an empty one.</b> Where <c>/proc/asound</c> does not exist at all
/// the machine has no ALSA — a container, a virtual agent (§5.3), a workstation — and every
/// audio resource reports in sync against hardware it does not have, exactly as
/// <see cref="CpuGovernorResource"/> does for a machine with no cpufreq. A file that exists and
/// lists the wrong cards is a frame with broken audio, which is drift and must stay drift.
/// </para>
/// </remarks>
public static class AlsaCards
{
    /// <summary>Where the kernel publishes the card list.</summary>
    public const string CardsPath = "/proc/asound/cards";

    /// <summary>The short id the XVF3800 registers under.</summary>
    public const string ArrayId = "Array";

    /// <summary>Parses the card list; an empty list for absent or unreadable content.</summary>
    public static IReadOnlyList<AlsaCard> Parse(string? content)
    {
        var cards = new List<AlsaCard>();
        if (string.IsNullOrEmpty(content))
        {
            return cards;
        }

        foreach (var raw in content.Split('\n'))
        {
            // " 0 [Array          ]: USB-Audio - reSpeaker XVF3800 4-Mic Array". The continuation
            // line beneath each card carries no brackets and is skipped by the same test.
            var line = raw.Trim();
            var open = line.IndexOf('[', StringComparison.Ordinal);
            var close = line.IndexOf(']', StringComparison.Ordinal);

            if (open <= 0 || close <= open)
            {
                continue;
            }

            if (!int.TryParse(
                    line[..open].Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var index))
            {
                continue;
            }

            cards.Add(new AlsaCard(
                index,
                line[(open + 1)..close].Trim(),
                line[(close + 1)..].TrimStart(':', ' ').Trim()));
        }

        return cards;
    }

    /// <summary>Whether this card is one of the Pi's HDMI audio devices.</summary>
    public static bool IsHdmi(AlsaCard card) =>
        card.Id.Contains("hdmi", StringComparison.OrdinalIgnoreCase)
        || card.Description.Contains("hdmi", StringComparison.OrdinalIgnoreCase);

    /// <summary>The card at <paramref name="index"/>, or null.</summary>
    public static AlsaCard? At(IReadOnlyList<AlsaCard> cards, int index)
    {
        ArgumentNullException.ThrowIfNull(cards);

        foreach (var card in cards)
        {
            if (card.Index == index)
            {
                return card;
            }
        }

        return null;
    }
}

/// <summary>
/// <c>audio.modprobe.snd-usb-audio-index</c> — the array is card 0 on every boot.
/// </summary>
/// <remarks>
/// <para>
/// From guide 4 step 1. Everything downstream — <c>amixer -c 0</c>, <c>alsactl store</c>, the
/// app's capture device — assumes the array is card 0, and on a stock install the kernel numbers
/// cards in whatever order it happens to enumerate them.
/// </para>
/// <para>
/// <b>The observation is the file <i>and</i> the card list, because they can disagree, and the
/// way they disagree is the measured failure.</b> The pin forces the module to index 0
/// specifically; on a cold boot where USB enumerates slowly an HDMI sound card can take index 0
/// first, and <c>snd-usb-audio</c> then fails outright with <c>cannot find the slot for index 0
/// … error -16</c>, leaving the frame with no working audio at all. A resource that read only its
/// own line would call that in sync. <see cref="HdmiAudioOffResource"/> is the separate fix for
/// the separate cause — same symptom, different file, different owner.
/// </para>
/// </remarks>
public sealed class SndUsbAudioIndexResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "audio.modprobe.snd-usb-audio-index";

    /// <summary>The file the options line lives in. Absent on a stock image.</summary>
    public const string ConfigPath = "/etc/modprobe.d/alsa-base.conf";

    /// <summary>
    /// The line guide 4 step 1 appends, verbatim.
    /// </summary>
    /// <remarks>
    /// <c>2886:001a</c> is the retail Seeed PID and a hardware revision changes it. Confirmed
    /// byte-for-byte against the v1 reference's <c>MODPROBE_D</c> capture of this same file.
    /// </remarks>
    public const string OptionsLine = "options snd-usb-audio index=0 vid=0x2886 pid=0x001a";

    private readonly ISystemFiles _files;

    /// <summary>Creates the resource.</summary>
    public SndUsbAudioIndexResource(ISystemFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        _files = files;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public string Detected => "This frame has not been told which of its sound devices is the microphone and speaker unit.";

    /// <inheritdoc/>
    public string WhyItMatters => "If it picks the wrong one after a restart, the frame has no sound and no microphone.";

    /// <inheritdoc/>
    public ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var occurrences = CountLines(_files.ReadText(ConfigPath), OptionsLine);
        var (cardCorrect, cardText) = CardZero();

        return ValueTask.FromResult(new ResourceObservation(
            occurrences == 1 && cardCorrect,
            $"'{OptionsLine}' exactly once in {ConfigPath}, and card 0 is '{AlsaCards.ArrayId}'",
            $"{occurrences} matching line(s) in {ConfigPath}; {cardText}"));
    }

    /// <inheritdoc/>
    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Rewritten rather than appended to: the file is read, every copy of the line is removed,
        // and exactly one is put back. A duplicate is as much a fault as an absence — a
        // non-idempotent write history is the thing the count in Observe exists to catch — and
        // an append-only Act could never repair one.
        var kept = new List<string>();
        foreach (var raw in Lines(_files.ReadText(ConfigPath)))
        {
            if (!string.Equals(raw.Trim(), OptionsLine, StringComparison.Ordinal))
            {
                kept.Add(raw);
            }
        }

        kept.Add(OptionsLine);
        _files.WriteText(ConfigPath, string.Join('\n', kept) + "\n");

        return ValueTask.FromResult(new ResourceAction(
            $"write {ConfigPath} carrying '{OptionsLine}' exactly once",
            "Telling this frame that its microphone and speaker unit is always the first sound device, whatever else is plugged in."));
    }

    /// <summary>How many lines of <paramref name="content"/> are exactly <paramref name="line"/>.</summary>
    public static int CountLines(string? content, string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var count = 0;
        foreach (var raw in Lines(content))
        {
            if (string.Equals(raw.Trim(), line, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private (bool Correct, string Text) CardZero()
    {
        if (!_files.FileExists(AlsaCards.CardsPath))
        {
            return (true, "no sound hardware on this machine");
        }

        var cards = AlsaCards.Parse(_files.ReadText(AlsaCards.CardsPath));
        return AlsaCards.At(cards, 0) is { } zero
            ? (string.Equals(zero.Id, AlsaCards.ArrayId, StringComparison.Ordinal),
               $"card 0 is '{zero.Id}'")
            : (false, cards.Count == 0 ? "the kernel lists no sound cards" : "there is no card 0");
    }

    private static string[] Lines(string? content) =>
        string.IsNullOrEmpty(content)
            ? []
            : content.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
}

/// <summary>
/// <c>boot.config.dtoverlay-vc4-kms-v3d-noaudio</c> — the HDMI sound cards do not exist.
/// </summary>
/// <remarks>
/// <para>
/// From guide 4 step 1, and brick-capable: it edits <c>/boot/firmware/config.txt</c>, which is
/// why the catalog schedules it 78th of 80 and why it runs behind
/// <see cref="BootPartitionGuard"/> like every other writer of that partition. The frame's
/// display is DSI and it never uses HDMI audio, so removing the competitor for card 0 costs
/// nothing and closes the cold-boot race described under <see cref="SndUsbAudioIndexResource"/>.
/// </para>
/// <para>
/// <b>It handles the general case, and the catalog asks for that specifically.</b> Guide 4's
/// <c>sed</c> is anchored to the exact stock line, so a <c>config.txt</c> whose vc4 line carries
/// any other parameter silently does not match and the guide reports success having changed
/// nothing. This appends <c>noaudio</c> to whatever parameter list the line already has.
/// </para>
/// <para>
/// <b>It never invents the overlay line.</b> If no <c>dtoverlay=vc4-kms-v3d</c> line exists at
/// all, writing one would switch the KMS display driver on — a display change made by an audio
/// resource, which is the opposite of one differential diagnosis per resource. There is also
/// nothing to fix in that case: the overlay is what creates the HDMI sound cards, so a config
/// without it has no competitor for card 0, and the card list says so.
/// </para>
/// <para>
/// <b>One consequence of sharing the file is worth stating.</b>
/// <see cref="DisplayPanelOverlayResource"/> writes the same <c>config.txt</c> from position 3,
/// and <see cref="BootPartitionGuard"/> keeps one backup per file. A rollback of this change
/// therefore restores a <c>config.txt</c> from before the panel line as well, and the display
/// resource repairs itself on the following pass at the cost of one more reboot. That is stated
/// rather than worked around because the alternative — a backup per writer — would mean two
/// files on the boot partition claiming to be the good one.
/// </para>
/// </remarks>
public sealed class HdmiAudioOffResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "boot.config.dtoverlay-vc4-kms-v3d-noaudio";

    /// <summary>The overlay whose parameter list this resource owns one entry of.</summary>
    public const string OverlayName = "vc4-kms-v3d";

    /// <summary>The parameter that removes the HDMI sound cards at device-tree level.</summary>
    public const string Parameter = "noaudio";

    private readonly ISystemFiles _files;
    private readonly BootPartitionGuard _guard;
    private readonly IAgentLog _log;

    /// <summary>Creates the resource.</summary>
    public HdmiAudioOffResource(ISystemFiles files, BootPartitionGuard guard, IAgentLog log)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(log);

        _files = files;
        _guard = guard;
        _log = log;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public string Detected => "This frame's television socket is still offering itself as a sound device.";

    /// <inheritdoc/>
    public string WhyItMatters => "On a slow start it can take the place of the real speaker, and then the frame comes up silent.";

    /// <inheritdoc/>
    public ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Self-repair before the file is read, for the same reason the display resources do it:
        // a rollback that happened on this boot has to be visible to the compare that follows.
        var verdict = _guard.Tick(BootConfigText.ConfigPath);
        var content = _files.ReadText(BootConfigText.ConfigPath);
        var line = BootConfigText.FindOverlayLine(content, OverlayName);
        var carries = line is null || BootConfigText.OverlayHasParameter(line, Parameter);
        var (quiet, cardText) = NoHdmiCards();

        if (verdict is GuardVerdict.RolledBack or GuardVerdict.Locked && line is not null && !carries)
        {
            return ValueTask.FromResult(new ResourceObservation(
                false,
                $"'{OverlayName}' carrying '{Parameter}' in {BootConfigText.ConfigPath}",
                "the change was tried and put back automatically because the frame came back without it working; "
                    + $"{BootPartitionGuard.BackupFor(BootConfigText.ConfigPath)} holds the version before the change"));
        }

        return ValueTask.FromResult(new ResourceObservation(
            carries && quiet,
            $"every '{OverlayName}' line carrying '{Parameter}', and no HDMI sound card",
            $"{(line is null ? $"no {OverlayName} line" : $"'{line}'")}; {cardText}"));
    }

    /// <inheritdoc/>
    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var current = _files.ReadText(BootConfigText.ConfigPath);
        var line = BootConfigText.FindOverlayLine(current, OverlayName);

        if (line is null)
        {
            return ValueTask.FromResult(new ResourceAction(
                $"left {BootConfigText.ConfigPath} alone — it has no {OverlayName} line to add '{Parameter}' to",
                "This frame's start-up settings do not switch the television picture on at all, so there was nothing to change."));
        }

        if (BootConfigText.OverlayHasParameter(line, Parameter))
        {
            // The line is already right and the card list is not, so this is a fault somewhere
            // else entirely — another overlay, another module. Rewriting the same line would
            // spend a boot-partition write on a change that is already made.
            return ValueTask.FromResult(new ResourceAction(
                $"left {BootConfigText.ConfigPath} alone — '{line}' already carries '{Parameter}', so something else is creating the sound device",
                "This frame's start-up settings are already correct, so the extra sound device is coming from somewhere else."));
        }

        if (!_guard.BeginTrial(BootConfigText.ConfigPath))
        {
            return ValueTask.FromResult(new ResourceAction(
                $"refused to write {BootConfigText.ConfigPath} — this change has already been rolled back once",
                "This frame already tried this change to its start-up settings and had to undo it. It will not try again on its own."));
        }

        var replacement = line + "," + Parameter;
        var updated = BootConfigText.ReplaceLine(current, line, replacement);
        var check = BootConfigText.ValidateReplacement(current, updated, line, replacement);

        if (!check.Valid)
        {
            _log.Fail($"Refusing to write {BootConfigText.ConfigPath}: {check.Problem}");
            return ValueTask.FromResult(new ResourceAction(
                $"refused to write {BootConfigText.ConfigPath} — {check.Problem}",
                "This frame checked the change it was about to make to its start-up settings, did not like it, and left them alone."));
        }

        _files.WriteText(BootConfigText.ConfigPath, updated);

        return ValueTask.FromResult(new ResourceAction(
            $"rewrite '{line}' as '{replacement}' in {BootConfigText.ConfigPath} "
                + $"(backed up to {BootPartitionGuard.BackupFor(BootConfigText.ConfigPath)})",
            "Switching off the sound device on the television socket, so the real speaker is the only one this frame can pick."));
    }

    private (bool Quiet, string Text) NoHdmiCards()
    {
        if (!_files.FileExists(AlsaCards.CardsPath))
        {
            return (true, "no sound hardware on this machine");
        }

        var hdmi = new List<string>();
        foreach (var card in AlsaCards.Parse(_files.ReadText(AlsaCards.CardsPath)))
        {
            if (AlsaCards.IsHdmi(card))
            {
                hdmi.Add(string.Create(CultureInfo.InvariantCulture, $"card {card.Index} '{card.Id}'"));
            }
        }

        return hdmi.Count == 0
            ? (true, "no HDMI sound card")
            : (false, "HDMI sound cards present: " + string.Join(", ", hdmi));
    }
}

/// <summary>One XVF3800 array, as the USB bus itself describes it.</summary>
/// <param name="Path">The bus path the kernel gave it, e.g. <c>1-1</c>.</param>
/// <param name="BcdDevice">The raw <c>bcdDevice</c> descriptor field, e.g. <c>020a</c>.</param>
/// <param name="Serial">The unit serial the array reports, or an empty string.</param>
public readonly record struct XvfArrayDevice(string Path, string BcdDevice, string Serial);

/// <summary>
/// The array's USB device descriptor, read straight out of sysfs.
/// </summary>
/// <remarks>
/// <para>
/// <b>A second, independent reading of the firmware version that costs nothing.</b> The XVF3800
/// encodes its firmware version in <c>bcdDevice</c>, so <c>/sys/bus/usb/devices/1-1/bcdDevice</c>
/// answers the same question <c>xvf_host VERSION</c> does — without the control tool, without
/// root, without a USB control transfer, and without any process at all. That matters in three
/// separate cases: a frame whose tool is missing, a frame whose array chain is blocked behind
/// something else, and any check taken while the loop is busy.
/// </para>
/// <para>
/// <b>The encoding is measured, not assumed, and it is nibble-hex rather than BCD.</b> Two arrays
/// on the bench 2026-08-20: firmware <c>2 0 6</c> reads <c>0206</c>, firmware <c>2 0 10</c> reads
/// <c>020a</c>. <c>0x0a</c> is not a valid BCD digit pair, so the field is read as major in the
/// first byte and minor and patch in the two nibbles of the second. The consequence of that shape
/// is worth stating rather than discovering: a minor or patch of 16 or more cannot be represented
/// at all, so a future <c>2.0.16</c> would be indistinguishable here from something else. Only the
/// two readings above are measured; <c>2.1.0</c> is predicted to read <c>0210</c> and has not been
/// seen.
/// </para>
/// <para>
/// <b>Nothing here can write.</b> The whole surface is <see cref="ISystemFiles.ListDirectories"/>
/// and <see cref="ISystemFiles.ReadText"/> under <c>/sys</c>, which is why this is the reading an
/// observe-only reporter is built on.
/// </para>
/// </remarks>
public static class XvfArrayUsb
{
    /// <summary>Where the kernel publishes one directory per USB device.</summary>
    public const string DevicesPath = "/sys/bus/usb/devices";

    /// <summary>Seeed's vendor id, as sysfs spells it.</summary>
    public const string VendorId = "2886";

    /// <summary>The XVF3800 4-Mic Array's product id, as sysfs spells it.</summary>
    public const string ProductId = "001a";

    /// <summary>Every attached array, in bus order.</summary>
    /// <remarks>
    /// <para>
    /// An empty list has two meanings and the caller has to keep them apart: no array is plugged
    /// in, or this machine has no USB sysfs at all. <see cref="Enumerable"/> answers the second.
    /// </para>
    /// <para>
    /// <b>Directories and files are both walked, and that is not belt-and-braces.</b> Every entry
    /// under <c>/sys/bus/usb/devices</c> is a <i>symlink</i> into <c>/sys/devices</c>, and whether
    /// a symlink-to-a-directory comes back from <see cref="ISystemFiles.ListDirectories"/> or from
    /// <see cref="ISystemFiles.ListFiles"/> depends on whether the enumerator resolves
    /// <c>DT_LNK</c> — which is a runtime detail, on a filesystem no test on a workstation can
    /// reproduce. The union of the two is exhaustive by construction, because the file predicate is
    /// the negation of the directory one, so this reads the bus correctly either way. Getting it
    /// wrong in the other direction would fail <i>silently</i>: every frame would report that it has
    /// no microphone unit, which is a sentence an operator would believe.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<XvfArrayDevice> Attached(ISystemFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var arrays = new List<XvfArrayDevice>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<string>(files.ListDirectories(DevicesPath));
        entries.AddRange(files.ListFiles(DevicesPath));
        entries.Sort(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (!seen.Add(entry)
                || !Matches(files, entry, "idVendor", VendorId)
                || !Matches(files, entry, "idProduct", ProductId))
            {
                continue;
            }

            arrays.Add(new XvfArrayDevice(
                entry[(entry.LastIndexOf('/') + 1)..],
                Field(files, entry, "bcdDevice") ?? string.Empty,
                Field(files, entry, "serial") ?? string.Empty));
        }

        return arrays;
    }

    /// <summary>Whether this machine publishes USB devices at all.</summary>
    public static bool Enumerable(ISystemFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files.DirectoryExists(DevicesPath);
    }

    /// <summary>
    /// The firmware version a <c>bcdDevice</c> field carries, in <c>xvf_host</c>'s own spelling
    /// (<c>2 0 10</c>), or null if the field is not four hex digits.
    /// </summary>
    public static string? Version(string? bcdDevice)
    {
        var text = bcdDevice?.Trim();
        if (text is not { Length: 4 })
        {
            return null;
        }

        foreach (var character in text)
        {
            if (!Uri.IsHexDigit(character))
            {
                return null;
            }
        }

        var major = Convert.ToInt32(text[..2], 16);
        var minor = Convert.ToInt32(text[2].ToString(), 16);
        var patch = Convert.ToInt32(text[3].ToString(), 16);

        return string.Create(CultureInfo.InvariantCulture, $"{major} {minor} {patch}");
    }

    private static bool Matches(ISystemFiles files, string directory, string field, string expected) =>
        string.Equals(Field(files, directory, field), expected, StringComparison.OrdinalIgnoreCase);

    private static string? Field(ISystemFiles files, string directory, string name) =>
        files.ReadText(directory + "/" + name)?.Trim();
}

/// <summary>
/// Seeed's host-side control tool, wherever this frame keeps it.
/// </summary>
/// <remarks>
/// <para>
/// The XVF3800 exposes two things over one USB cable: the audio interface ALSA sees as card 0,
/// and a USB HID control interface speaking XMOS's command/response protocol. ALSA's mixer
/// cannot reach the second one at all, so the firmware version, the speaker amplifier and the
/// mute button are only readable through this binary.
/// </para>
/// <para>
/// <b>The working directory is part of the contract, not an incidental <c>cd</c>.</b> The binary
/// loads its sibling <c>.so</c> files relative to where it is run from, which is why guide 4
/// wraps every call in a subshell. §2.2 forbids a shell anywhere in the agent, so the call is
/// made through <c>env</c> as a program — <c>env -C &lt;dir&gt; LD_LIBRARY_PATH=&lt;dir&gt;
/// &lt;dir&gt;/xvf_host …</c> — which is an argument vector with no word splitting in it, the
/// same device <see cref="LoginUserSession"/> already uses. Both the working directory and the
/// loader path are set because which of the two the binary actually needs is a property of how
/// Seeed linked it, and setting both costs nothing.
/// </para>
/// <para>
/// <b>Two candidate directories, in order.</b> The agent-owned path under
/// <c>/var/lib/fl-agent</c> is where <see cref="XvfHostInstaller"/> puts the pinned files;
/// <c>~/xvf3800</c> is where guide 4's <c>git clone</c> puts them, which is what a frame built by
/// hand has and what the v1 reference records. Finding a binary is not the same as finding the
/// <i>pinned</i> one — <see cref="XvfHostToolResource"/> is where the digests decide which.
/// </para>
/// </remarks>
public sealed class XvfHost
{
    /// <summary>The binary's file name.</summary>
    public const string Binary = "xvf_host";

    /// <summary>Where a pinned, agent-installed copy would live.</summary>
    public const string AgentDirectory = "/var/lib/fl-agent/xvf3800";

    /// <summary>The path inside the reSpeaker tree that holds the aarch64 build.</summary>
    public const string ToolSubdirectory = "host_control/rpi_64bit";

    /// <summary>Where guide 4's clone puts the tree, relative to the login user's home.</summary>
    public const string HomeSubdirectory = "xvf3800";

    /// <summary>Where the DFU images sit inside the same tree, for a person who has come to flash one.</summary>
    /// <remarks>
    /// Nothing in the agent reads this. It is kept as the one recorded fact about where upstream
    /// puts the images, because decision 90's whole point is that the flash is an attended
    /// operation and the operator performing it should not have to re-derive the path. The
    /// installer fetches the six <c>host_control</c> files and never anything from here.
    /// </remarks>
    public const string FirmwareSubdirectory = "xmos_firmwares/usb";

    /// <summary>The device-management command that reports the running firmware.</summary>
    public const string VersionCommand = "VERSION";

    /// <summary>Reads all five addressable GPO pins in one call.</summary>
    public const string GpoReadCommand = "GPO_READ_VALUES";

    /// <summary>Sets one GPO pin by its XMOS port number.</summary>
    public const string GpoWriteCommand = "GPO_WRITE_VALUE";

    /// <summary>The banner a working HID round trip prints first.</summary>
    public const string DeviceBanner = "Found device";

    private readonly ISystemFiles _files;
    private readonly IProcessRunner _processes;
    private readonly IUserSession _session;

    /// <summary>Creates a view over the tool.</summary>
    public XvfHost(ISystemFiles files, IProcessRunner processes, IUserSession session)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(session);

        _files = files;
        _processes = processes;
        _session = session;
    }

    /// <summary>The tree roots this frame is searched for, most preferred first.</summary>
    public IReadOnlyList<string> Roots =>
        [AgentDirectory, _session.HomeDirectory.TrimEnd('/') + "/" + HomeSubdirectory];

    /// <summary>The first root that holds the binary, or null.</summary>
    public string? Root()
    {
        foreach (var root in Roots)
        {
            if (_files.FileExists(ToolPath(root)))
            {
                return root;
            }
        }

        return null;
    }

    /// <summary>The directory the binary lives in, under <paramref name="root"/>.</summary>
    public static string ToolDirectory(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return root.TrimEnd('/') + "/" + ToolSubdirectory;
    }

    /// <summary>The binary itself, under <paramref name="root"/>.</summary>
    public static string ToolPath(string root) => ToolDirectory(root) + "/" + Binary;

    /// <summary>
    /// One conversation with the array at a time, for the whole process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The device is a singleton and the tool knows it.</b> <c>xvf_host</c> has no device
    /// selector — its USB backend enumerates, opens whichever array comes first and claims HID
    /// interface 3 — so a second invocation overlapping the first does not talk to a second device,
    /// it loses the claim. The loser reads as <i>the array did not answer</i>, which is drift, which
    /// costs an attempt and a reboot for a frame whose array was working the whole time.
    /// </para>
    /// <para>
    /// One gate rather than one per instance, because the thing being serialised is the device and
    /// not the object: the reconcile loop builds its own <see cref="XvfHost"/> inside the audio
    /// block and <c>ArrayFirmwareReporter</c> builds another beside it, and an instance field would
    /// serialise each of them against itself only. It is the process boundary that matters, because
    /// there is exactly one agent process per frame and exactly one array per agent.
    /// </para>
    /// <para>
    /// The wait is unbounded on purpose. The only thing that can hold it long is an
    /// <c>xvf_host</c> that hangs, and <c>HostProcessRunner</c> already awaits that with no timeout
    /// wherever it is called from — so a hung tool wedges the caller today, with or without this
    /// gate, and a bounded wait here would buy nothing except a second way to report a working
    /// array as absent.
    /// </para>
    /// </remarks>
    private static readonly SemaphoreSlim Conversation = new(1, 1);

    /// <summary>Runs one command against the array, from the binary's own directory.</summary>
    public async Task<ProcessResult> RunAsync(
        string root,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var directory = ToolDirectory(root);
        var vector = new List<string>(arguments.Count + 4)
        {
            "-C",
            directory,
            "LD_LIBRARY_PATH=" + directory,
            directory + "/" + Binary,
        };

        vector.AddRange(arguments);

        await Conversation.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await _processes
                .RunAsync("env", vector, ProcessDeadline.Array, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Conversation.Release();
        }
    }

    /// <summary>Whether the HID control interface answered at all.</summary>
    public static bool Answered(ProcessResult result) =>
        result.Combined.Contains(DeviceBanner, StringComparison.Ordinal);

    /// <summary>
    /// The firmware version from a <c>VERSION</c> reply, in the tool's own spelling
    /// (<c>2 0 10</c>), or null.
    /// </summary>
    public static string? Version(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(VersionCommand, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = line[VersionCommand.Length..].Trim();
            if (rest.Length > 0)
            {
                return string.Join(' ', rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            }
        }

        return null;
    }

    /// <summary>
    /// The five GPO values from a <c>GPO_READ_VALUES</c> reply, or null.
    /// </summary>
    /// <remarks>
    /// The order is fixed by the firmware and documented by Seeed: <c>X0D11, X0D30, X0D31,
    /// X0D33, X0D39</c>. Parsed from the last line that is five integers, with or without the
    /// command name in front of them, because the reply's exact shape is captured nowhere this
    /// build can read.
    /// </remarks>
    public static IReadOnlyList<int>? GpoValues(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        IReadOnlyList<int>? found = null;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(GpoReadCommand, StringComparison.Ordinal))
            {
                line = line[GpoReadCommand.Length..].Trim();
            }

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length != GpoPins.Count)
            {
                continue;
            }

            var values = new int[GpoPins.Count];
            var parsed = true;

            for (var index = 0; index < tokens.Length; index++)
            {
                if (!int.TryParse(tokens[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[index]))
                {
                    parsed = false;
                    break;
                }
            }

            if (parsed)
            {
                found = values;
            }
        }

        return found;
    }

    /// <summary>The pins <see cref="GpoReadCommand"/> reports, in its fixed order.</summary>
    public static IReadOnlyList<string> GpoPins { get; } = ["X0D11", "X0D30", "X0D31", "X0D33", "X0D39"];
}

/// <summary>
/// <c>tool.xvf-host.installed</c> — the control tool is present and the array answers it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Observe and Act are both complete, and the pin is what closed the gap.</b> This resource
/// used to find the tool and refuse to install one, because the catalog claimed a "pinned
/// SHA-256 set" that did not exist anywhere in this repository and inventing a URL would have
/// been the fabrication §0.4 forbids. Open question 3 is now answered (decision 63): the six
/// files are pinned at a commit SHA, fetched from content-addressed
/// <c>raw.githubusercontent.com</c> URLs and verified against measured digests before anything
/// is put in place. <see cref="XvfHostReleasePin"/> holds the whole of it.
/// </para>
/// <para>
/// <b>Six files, and Observe hashes all of them on every pass.</b> The catalog used to say four —
/// the binary and three <c>.so</c> files — and Seeed's own <c>host_control/README.md</c> lists
/// <c>dfu_cmds.yaml</c> and <c>transport_config.yaml</c> in the same directory. Counting shared
/// libraries was the best structural check available while no digests existed; it is strictly
/// weaker than hashing the set, so it is gone. Hashing rather than remembering is also what makes
/// §2.4's Verify real: a note saying an install succeeded would survive a boot the files did not.
/// </para>
/// <para>
/// <b>The live round trip stays, and it is the half a digest cannot answer.</b> Six correct files
/// prove the tool is installed; only <c>xvf_host VERSION</c> answering its <c>Found device</c>
/// banner proves the array is plugged in, enumerated and reachable over its HID control interface.
/// That is what survives a reboot as a claim about the whole path rather than about the disk.
/// </para>
/// <para>
/// <b>Both roots are still searched, and the pin decides which one is in sync.</b> A frame built
/// by hand carries guide 4's <c>git clone</c> under <c>~/xvf3800</c>; if those bytes are the
/// pinned bytes the frame is in sync where it stands and downloads nothing. If they are not, the
/// repair is a verified install into <c>/var/lib/fl-agent/xvf3800</c>, which
/// <see cref="XvfHost.Root"/> prefers, so the next pass observes the agent-owned copy.
/// </para>
/// <para>
/// <b>It is the root of the array chain</b>, so a frame with no tool leaves
/// <see cref="XvfAmplifierResource"/> <c>Blocked(dependency)</c> — visibly waiting on this one
/// thing rather than failing on its own. That is §2.2's DAG doing its job, and it is why a
/// refusal here has to be loud and has to name which file was wrong. The chain behind it is one
/// resource shorter since decision 90: <c>firmware.xvf3800.version</c> sat between the two, and
/// the playback mixer resources hung off that, so a frame that could not fetch six files used to
/// leave both speaker volumes blocked behind a firmware version nobody was going to write.
/// </para>
/// </remarks>
public sealed class XvfHostToolResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "tool.xvf-host.installed";

    private readonly XvfHost _tool;
    private readonly ISystemFiles _files;
    private readonly XvfHostInstaller _installer;

    /// <summary>Creates the resource.</summary>
    public XvfHostToolResource(XvfHost tool, ISystemFiles files, XvfHostInstaller installer)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(installer);

        _tool = tool;
        _files = files;
        _installer = installer;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public string Detected => "The program this frame uses to talk to its microphone and speaker unit is missing.";

    /// <inheritdoc/>
    public string WhyItMatters => "Without it the frame cannot switch the speaker on or check the unit's own software.";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        if (!_files.FileExists(AlsaCards.CardsPath))
        {
            return new ResourceObservation(true, "a working control tool", "no sound hardware on this machine");
        }

        var pin = _installer.Pin;
        var expected = string.Create(
            CultureInfo.InvariantCulture,
            $"the {pin.Files.Count} files pinned at {pin.Commit[..12]}, {XvfHost.Binary} executable, and the array answering it");

        if (_tool.Root() is not { } root)
        {
            return new ResourceObservation(
                false,
                expected,
                $"{XvfHost.Binary} is in none of: {string.Join(", ", _tool.Roots)}");
        }

        var directory = XvfHost.ToolDirectory(root);
        var faults = await _installer.UnverifiedAsync(directory, cancellationToken).ConfigureAwait(false);

        if (faults.Count > 0)
        {
            return new ResourceObservation(false, expected, $"{directory}: {string.Join("; ", faults)}");
        }

        var reply = await _tool.RunAsync(root, [XvfHost.VersionCommand], cancellationToken).ConfigureAwait(false);

        return XvfHost.Answered(reply)
            ? new ResourceObservation(
                true,
                expected,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{directory}: all {pin.Files.Count} files match the pin, and the array answered — {Firmware(reply)}"))
            : new ResourceObservation(
                false,
                expected,
                $"{directory}: the files match the pin, but the array did not answer — {Trim(reply.Combined)}");
    }

    /// <summary>
    /// What the array said it is running, from both readings, for the observed text.
    /// </summary>
    /// <remarks>
    /// <b>Information, never a comparison.</b> This resource asserts that the tool is installed and
    /// that the array answers it; which firmware answered is a fact about the hardware that nothing
    /// on the frame converges, so it travels here the same way the Mute button and the LED rail
    /// travel in <see cref="XvfAmplifierResource"/>'s observed text — reported, never compared.
    /// Since the reply is already in hand, naming the version costs one string, and it is the one
    /// thing an operator reading "the array answered" immediately wants to know. The reading that
    /// reaches the Fleet Manager on a converged frame is <c>ArrayFirmwareReporter</c>'s, because a
    /// resource that is in sync publishes no observed text at all.
    /// </remarks>
    private string Firmware(ProcessResult reply)
    {
        var reported = XvfHost.Version(reply.StandardOutput) ?? XvfHost.Version(reply.Combined);
        var descriptor = XvfArrayUsb.Attached(_files) is [var array, ..] ? array.BcdDevice : null;
        var decoded = XvfArrayUsb.Version(descriptor);

        var control = reported is null ? "no version in the reply" : $"{XvfHost.VersionCommand} {reported}";
        var usb = descriptor is null or ""
            ? "no USB descriptor"
            : decoded is null ? $"bcdDevice {descriptor}" : $"bcdDevice {descriptor} = {decoded}";

        var agreement = reported is not null && decoded is not null
            ? string.Equals(reported, decoded, StringComparison.Ordinal) ? ", agreeing" : ", disagreeing"
            : string.Empty;

        return $"firmware {control}, {usb}{agreement}";
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var pin = _installer.Pin;
        var result = await _installer.InstallAsync(cancellationToken).ConfigureAwait(false);

        var change = string.Create(
            CultureInfo.InvariantCulture,
            $"fetch {pin.Files.Count} files from {pin.RawBaseUrl}, verify sha256, install into {XvfHostInstaller.TargetDirectory}");

        return new ResourceAction(
            result is XvfHostInstallResult.Installed or XvfHostInstallResult.AlreadyInstalled
                ? change
                : $"{change} (refused: {result})",
            "Downloading the program this frame uses to talk to its microphone and speaker unit, and checking every file, byte for byte, that it arrived intact.");
    }

    private static string Trim(string output)
    {
        var line = output.Replace('\n', ' ').Trim();
        return line.Length <= 120 ? line : line[..120] + "…";
    }
}

/// <summary>
/// <c>audio.xvf3800.gpo-x0d31-amp-enable</c> — the speaker amplifier is switched on.
/// </summary>
/// <remarks>
/// <para>
/// From guide 4 step 4. <c>X0D31</c> is active-low, so <c>0</c> means the amplifier is enabled.
/// <b>Both firmware levels this project has seen boot it low</b> — measured on two arrays on the
/// bench 2026-08-20, a factory <c>2 0 6</c> board and an upgraded <c>2 0 10</c> board, each
/// reading <c>GPO_READ_VALUES 0 0 0 1 0</c> on a frame whose agent had been stopped before the
/// array was attached. So the Act does not run on any array this project owns. It is still its own
/// resource because it is independently verifiable and a future firmware could default
/// differently, which is precisely the class of change §2.2's granularity rule exists for — and
/// because Observe reads the pin rather than assuming it, the resource is worth exactly as much on
/// a board that boots it high.
/// </para>
/// <para>
/// <b>The same readback carries two diagnostics that are not settings.</b> The second value is
/// <c>X0D30</c>, the hardware Mute button — a <c>1</c> there means somebody pressed it and mic
/// capture is silent while everything else reports healthy — and the fourth is <c>X0D33</c>, the
/// LED ring rail. Neither is agent-settable, so neither is a resource; both travel in the
/// observed text, where they reach telemetry and the repair screen without pretending to be
/// something the loop can converge.
/// </para>
/// <para>
/// <b>What upstream issue #18 does and does not say about this write, because somebody will ask.</b>
/// That issue — <i>"Multiple issues after LED/GPO commands"</i>, opened 2026-05-18, still open,
/// <b>zero comments and no maintainer response</b>, fetched verbatim rather than summarised — is
/// the only report in existence associating <c>GPO_WRITE_VALUE</c> with a damaged array, and it
/// does not isolate it. The reporter used <c>LED_EFFECT</c>, <c>led_color</c>, <c>led_speed</c>,
/// <c>led_brightness</c>, <c>GPO_WRITE_VALUE</c>, <c>CLEAR_CONFIGURATION</c>,
/// <c>SAVE_CONFIGURATION</c> and repeated DFU reflashes, on firmware 2.0.5, 2.0.6 and 2.0.7 —
/// <b>every one of them older than the 2.0.9 in which upstream says the <c>SAVE_CONFIGURATION</c>
/// corruption of issue #8 was fixed</b>. His device also still enumerates, still answers
/// <c>VERSION 2 0 7</c> and still plays audio, so it is a device with a wrong DSP and codec
/// configuration rather than a brick; he says so himself in asking how to reset the codec, and
/// notes that a DFU reflash does not clear it — which is the DataPartition, the partition neither
/// this agent nor its flash path ever writes. And his own <c>GPO_READ_VALUES</c> reads
/// <c>0 0 0 1 0</c>, the same five values both of our healthy arrays read, so <c>X0D31=0</c> is
/// not the damaged state.
/// </para>
/// <para>
/// <b>Three properties keep this resource on the safe side of that report, and they are structural
/// rather than incidental.</b> The agent issues <c>VERSION</c>, <c>GPO_READ_VALUES</c> and
/// <c>GPO_WRITE_VALUE</c> and <i>nothing else</i>: <c>SAVE_CONFIGURATION</c> appears nowhere in
/// this repository outside guide 4's prose, so a GPO write here is volatile and cannot reach the
/// partition that survives a reflash. The Act runs only on drift (§2.3), so a board that boots the
/// pin low is never written to at all. And the write is one pin to one value, not the LED and
/// configuration traffic the issue actually describes.
/// </para>
/// </remarks>
public sealed class XvfAmplifierResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "audio.xvf3800.gpo-x0d31-amp-enable";

    /// <summary>The XMOS port number of the amplifier pin.</summary>
    public const string AmplifierPin = "31";

    /// <summary>Its position in the fixed five-value readback order.</summary>
    public const int AmplifierIndex = 2;

    /// <summary>The hardware Mute button's position in the same readback.</summary>
    public const int MuteIndex = 1;

    /// <summary>The LED ring rail's position in the same readback.</summary>
    public const int LedIndex = 3;

    private readonly XvfHost _tool;
    private readonly ISystemFiles _files;

    /// <summary>Creates the resource.</summary>
    public XvfAmplifierResource(XvfHost tool, ISystemFiles files)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(files);

        _tool = tool;
        _files = files;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    /// <remarks>
    /// The control tool, and nothing else. This edge used to run through
    /// <c>firmware.xvf3800.version</c>, which left this resource — and the whole mixer block behind
    /// it — <see cref="ResourceStatusKind.Blocked"/> on a frame whose array ran a firmware the
    /// catalog had not pinned. Decision 90 took that resource out of the graph, and the pin it
    /// carried with it: the amplifier is read and written the same way on every firmware level
    /// this project has measured, so there was never a real dependency here, only a scheduling one.
    /// </remarks>
    public IReadOnlyList<string> DependsOn => [XvfHostToolResource.ResourceName];

    /// <inheritdoc/>
    public string Detected => "The amplifier inside this frame's speaker is switched off.";

    /// <inheritdoc/>
    public string WhyItMatters => "Nothing comes out of the speaker at all while it is off, however loud the frame is set.";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        if (!_files.FileExists(AlsaCards.CardsPath))
        {
            return new ResourceObservation(true, "X0D31=0", "no sound hardware on this machine");
        }

        if (_tool.Root() is not { } root)
        {
            return new ResourceObservation(false, "X0D31=0", $"{XvfHost.Binary} is not installed, so the pins cannot be read");
        }

        var reply = await _tool.RunAsync(root, [XvfHost.GpoReadCommand], cancellationToken).ConfigureAwait(false);

        if (XvfHost.GpoValues(reply.Combined) is not { } values)
        {
            return new ResourceObservation(false, "X0D31=0", $"the array did not report its pins — {reply.Combined}");
        }

        return new ResourceObservation(
            values[AmplifierIndex] == 0,
            "X0D31=0 (amplifier enabled)",
            string.Create(
                CultureInfo.InvariantCulture,
                $"X0D31={values[AmplifierIndex]}, mute button X0D30={values[MuteIndex]}, LED ring X0D33={values[LedIndex]}"));
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        if (_tool.Root() is not { } root)
        {
            return new ResourceAction(
                $"could not switch the amplifier on — {XvfHost.Binary} is not installed",
                "This frame cannot reach its speaker's amplifier because the program that talks to it is missing.");
        }

        var result = await _tool
            .RunAsync(root, [XvfHost.GpoWriteCommand, AmplifierPin, "0"], cancellationToken)
            .ConfigureAwait(false);

        return new ResourceAction(
            $"{XvfHost.Binary} {XvfHost.GpoWriteCommand} {AmplifierPin} 0"
                + (result.Succeeded ? string.Empty : $" (refused: {result.Combined})"),
            "Switching the amplifier inside this frame's speaker on.");
    }
}
