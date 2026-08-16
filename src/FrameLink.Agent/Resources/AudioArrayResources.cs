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
/// why the catalog schedules it 77th of 79 and why it runs behind
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

    /// <summary>Where the DFU images sit inside the same tree.</summary>
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

    /// <summary>The DFU image for <paramref name="version"/>, under <paramref name="root"/>.</summary>
    public static string FirmwarePath(string root, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        return root.TrimEnd('/') + "/" + FirmwareSubdirectory
            + "/respeaker_xvf3800_usb_dfu_firmware_v" + version + ".bin";
    }

    /// <summary>Runs one command against the array, from the binary's own directory.</summary>
    public Task<ProcessResult> RunAsync(
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

        return _processes.RunAsync("env", vector, cancellationToken);
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
/// <c>firmware.xvf3800.version</c> and, through it, the playback mixer resources
/// <c>Blocked(dependency)</c> — visibly waiting on this one thing rather than each failing on
/// its own. That is §2.2's DAG doing its job, and it is why a refusal here has to be loud and
/// has to name which file was wrong.
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
                    $"{directory}: all {pin.Files.Count} files match the pin, and the array answered"))
            : new ResourceObservation(
                false,
                expected,
                $"{directory}: the files match the pin, but the array did not answer — {Trim(reply.Combined)}");
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
/// <c>firmware.xvf3800.version</c> — the array runs the firmware this build was validated
/// against.
/// </summary>
/// <remarks>
/// <para>
/// From guide 4 step 3. <b>Load-bearing for the mixer resources below it:</b> 2.0.6-era and
/// 2.0.10 firmware expose and default the DAC volume path differently, which is why the catalog
/// makes <c>audio.mixer.*</c> depend on this and why open question 2 places the flash just ahead
/// of the audio block rather than in §5.5's last phase. The v1 reference's
/// <c>XVF3800_FIRMWARE</c> capture reads <c>VERSION 2 0 10</c>, so the pin below is the parity
/// value and not a preference.
/// </para>
/// <para>
/// <b>Brick-capable, and it does not flash unless an operator has said so for this device and
/// this version.</b> A DFU write can leave the mic array unusable; recovery is physical — hold
/// Mute while re-plugging power for Safe Mode, then reflash — so §5.5's "schedule brick-capable
/// resources last" is only half the mitigation. The other half is here: <see cref="ActAsync"/>
/// reads <see cref="AuthorisationKey"/> and refuses unless it holds exactly
/// <see cref="PinnedVersion"/>. Three properties make that a guarantee rather than a check:
/// </para>
/// <list type="number">
/// <item><description>
/// The default is <b>no authorisation</b>, so a frame nobody has configured cannot flash. §3.3
/// gives a pending device no settings at all, which means an unadopted frame cannot be
/// authorised even in principle.
/// </description></item>
/// <item><description>
/// The authorisation carries the <i>version</i>, not a boolean. A switch left on would silently
/// authorise a different flash the day this pin moves; a version has to be re-typed to mean
/// something new.
/// </description></item>
/// <item><description>
/// <c>dfu-util</c> is named in exactly one private method, which takes a
/// <see cref="FlashAuthorisation"/> that only the check can construct. There is no path from an
/// ordinary convergence pass to a flash that does not go through it, and a test asserts that an
/// unauthorised Act starts no process at all.
/// </description></item>
/// </list>
/// <para>
/// A frame whose array is on the wrong firmware therefore walks §2.5's ladder and reaches the
/// operator carrying the exact command it would have run — the escalation <i>is</i> the request
/// for permission.
/// </para>
/// </remarks>
public sealed class XvfFirmwareResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "firmware.xvf3800.version";

    /// <summary>The version this build is validated against, as a person writes it.</summary>
    public const string PinnedVersion = "2.0.10";

    /// <summary>The same version in the tool's own spelling.</summary>
    public const string PinnedReply = "2 0 10";

    /// <summary>
    /// Fleet setting (§3.4) that authorises one DFU flash, by version.
    /// </summary>
    /// <remarks>
    /// Deliberately not in the catalog's value-source list for this resource, which reads "fixed
    /// by the catalog". The <i>version</i> is fixed by the catalog; what this setting carries is
    /// an operator's permission to perform an irreversible-in-practice write on a device they can
    /// physically reach, which is not a value the catalog can hold on their behalf.
    /// </remarks>
    public const string AuthorisationKey = "audio.firmwareFlashAuthorised";

    /// <summary>How long the array is given to re-enumerate on USB after a flash.</summary>
    public static TimeSpan Settle { get; } = TimeSpan.FromSeconds(5);

    private readonly XvfHost _tool;
    private readonly ISystemFiles _files;
    private readonly IProcessRunner _processes;
    private readonly FleetValues _values;
    private readonly IAgentClock _clock;

    /// <summary>Creates the resource.</summary>
    public XvfFirmwareResource(
        XvfHost tool,
        ISystemFiles files,
        IProcessRunner processes,
        FleetValues values,
        IAgentClock clock)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(clock);

        _tool = tool;
        _files = files;
        _processes = processes;
        _values = values;
        _clock = clock;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn =>
        [XvfHostToolResource.ResourceName, PackageResource.Prefix + "dfu-util"];

    /// <inheritdoc/>
    public string Detected => "This frame's microphone and speaker unit is running a different version of its own software than the frame expects.";

    /// <inheritdoc/>
    public string WhyItMatters => "The speaker's volume settings behave differently between versions, so the frame can end up much too quiet.";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        if (!_files.FileExists(AlsaCards.CardsPath))
        {
            return new ResourceObservation(true, PinnedReply, "no sound hardware on this machine");
        }

        if (_tool.Root() is not { } root)
        {
            return new ResourceObservation(false, PinnedReply, $"{XvfHost.Binary} is not installed, so the array cannot be asked");
        }

        var reply = await _tool.RunAsync(root, [XvfHost.VersionCommand], cancellationToken).ConfigureAwait(false);
        var version = XvfHost.Version(reply.StandardOutput) ?? XvfHost.Version(reply.Combined);

        return new ResourceObservation(
            string.Equals(version, PinnedReply, StringComparison.Ordinal),
            PinnedReply,
            version ?? $"the array did not report a version — {reply.Combined}");
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Nothing above this line has started a process, and nothing below it does either unless
        // Authorise() hands back a value only it can make.
        if (Authorise() is not { } authorisation)
        {
            return Refusal();
        }

        return await FlashAsync(authorisation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>An operator's permission to flash one image. Constructed only by the check.</summary>
    private readonly record struct FlashAuthorisation(string Version, string ImagePath);

    private FlashAuthorisation? Authorise()
    {
        if (!string.Equals(_values.Find(AuthorisationKey), PinnedVersion, StringComparison.Ordinal))
        {
            return null;
        }

        if (_tool.Root() is not { } root)
        {
            return null;
        }

        var image = XvfHost.FirmwarePath(root, PinnedVersion);
        return _files.FileExists(image) ? new FlashAuthorisation(PinnedVersion, image) : null;
    }

    private ResourceAction Refusal()
    {
        var authorised = _values.Find(AuthorisationKey);
        var image = _tool.Root() is { } root ? XvfHost.FirmwarePath(root, PinnedVersion) : "<no tool directory>";

        var why = !string.Equals(authorised, PinnedVersion, StringComparison.Ordinal)
            ? $"no operator has authorised it — set {AuthorisationKey} to '{PinnedVersion}' for this device"
            : $"the image {image} is not on this frame";

        return new ResourceAction(
            $"refused to flash the array to {PinnedVersion}: {why}. The command it would run is: "
                + $"dfu-util -R -e -a 1 -D {image}",
            "This frame has not been given permission to update its microphone and speaker unit, so it has left it alone and asked instead.");
    }

    private async ValueTask<ResourceAction> FlashAsync(
        FlashAuthorisation authorisation,
        CancellationToken cancellationToken)
    {
        // The only place in the agent that names dfu-util. `-a 1` is the array's DFU Upgrade
        // partition, `-e` detaches it into DFU mode, `-D` supplies the image and `-R` resets it
        // back into normal operation afterwards.
        var arguments = new[] { "-R", "-e", "-a", "1", "-D", authorisation.ImagePath };
        var result = await _processes.RunAsync("dfu-util", arguments, cancellationToken).ConfigureAwait(false);

        // Part of the Act, not of Verify: the array re-enumerates on USB and a version read
        // issued into that window answers for a device that is not there yet.
        await _clock.DelayAsync(Settle, cancellationToken).ConfigureAwait(false);

        return new ResourceAction(
            $"dfu-util {string.Join(' ', arguments)}"
                + (result.Succeeded ? string.Empty : $" (refused: {result.Combined})"),
            $"Updating the microphone and speaker unit to version {authorisation.Version}, which this frame's sound settings were tested against.");
    }
}

/// <summary>
/// <c>audio.xvf3800.gpo-x0d31-amp-enable</c> — the speaker amplifier is switched on.
/// </summary>
/// <remarks>
/// <para>
/// From guide 4 step 4. <c>X0D31</c> is active-low, so <c>0</c> means the amplifier is enabled,
/// and firmware 2.0.10 boots it low — which makes the Act normally a no-op. It is still its own
/// resource because it is independently verifiable and a future firmware could default
/// differently, which is precisely the class of change §2.2's granularity rule exists for.
/// </para>
/// <para>
/// <b>The same readback carries two diagnostics that are not settings.</b> The second value is
/// <c>X0D30</c>, the hardware Mute button — a <c>1</c> there means somebody pressed it and mic
/// capture is silent while everything else reports healthy — and the fourth is <c>X0D33</c>, the
/// LED ring rail. Neither is agent-settable, so neither is a resource; both travel in the
/// observed text, where they reach telemetry and the repair screen without pretending to be
/// something the loop can converge.
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
    public IReadOnlyList<string> DependsOn => [XvfFirmwareResource.ResourceName];

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
