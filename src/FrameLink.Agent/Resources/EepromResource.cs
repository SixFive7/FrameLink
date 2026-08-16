using System.Text;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Agent.Resources;

/// <summary>
/// <c>eeprom.config</c> — the Pi 5 bootloader's own configuration.
/// </summary>
/// <remarks>
/// <para>
/// From the v1 inventory's <c>EEPROM_CONFIG</c>; set by no guide. It is in the catalog for two
/// reasons. It is parity state the state-diff harness compares, and
/// <c>rpi-eeprom-update.service</c> is <b>enabled</b> in the v1 reference — an autonomous owner
/// that can flash a newer bootloader and change this configuration without anybody asking.
/// </para>
/// <para>
/// <b>Confirmed stock, which is what makes this affordable.</b> Measured on the mule 2026-08-15:
/// <c>POWER_OFF_ON_HALT=1</c> and <c>BOOT_ORDER=0xf461</c>, matching the v1 reference. These are
/// stock-image values rather than anything a guide set, so on every frame this build targets the
/// resource observes, agrees and never acts. The Act exists for the case the catalog names — the
/// autonomous updater having moved something — and is expected never to run.
/// </para>
/// <para>
/// <b>Configuration only. The bootloader <i>version</i> is deliberately not touched.</b> Open
/// question 11 records the mule running a 2025-12-08 bootloader with 2026-05-26 available, and it
/// carries a standing instruction: <b>do not update it.</b> An EEPROM write is brick-capable, its
/// recovery is a card swap at best and a recovery-image flash at worst, and no part of v2 requires
/// current bootloader firmware. <c>rpi-eeprom-config --apply</c> re-applies the configuration onto
/// the image already installed, which is why it is the call used here and why nothing in this file
/// runs <c>rpi-eeprom-update</c>.
/// </para>
/// <para>
/// <b>Why <c>POWER_OFF_ON_HALT</c> is worth asserting rather than assuming.</b> §5.1's smart-plug
/// power-cycle harness reads a silent frame on a live relay, and with this setting a <c>halt</c>
/// genuinely cuts power — so silence has three explanations and not two: booting, hung, or stopped
/// and drawing nothing. A frame whose value moved would make the harness quietly wrong about which
/// of the three it was looking at.
/// </para>
/// <para>
/// <b>The write discipline, and the one part of it that cannot apply.</b> The content is a
/// known-good literal merged into the observed configuration; the merge is validated as minimal
/// before anything is applied; and the pre-change configuration is copied to the FAT32 boot
/// partition where a card reader can reach it, which is the only restore path that exists —
/// somebody applies it back from a working card. What §5.5's boot-count self-repair cannot do here
/// is restore: writing a file does not undo an EEPROM flash. <see cref="BootPartitionGuard"/> is
/// therefore used for the half of its job that does apply — counting boots and <i>locking</i> —
/// so an unattended frame gets exactly one flash attempt and then stops, instead of spending five
/// EEPROM writes on the §2.5 ladder. Its restore step lands on the backup file itself and is a
/// no-op by construction. This is stated rather than glossed, because a reader who assumed the
/// guard could roll an EEPROM back would be wrong about the one thing that matters here.
/// </para>
/// </remarks>
public sealed class EepromConfigResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "eeprom.config";

    /// <summary>Where the pre-change configuration is copied, on the FAT32 boot partition.</summary>
    public const string BackupPath = "/boot/firmware/eeprom-config.txt";

    /// <summary>File name of the candidate configuration inside the state store.</summary>
    public const string CandidateFileName = "eeprom-candidate.conf";

    /// <summary>The tool that reads and applies the bootloader configuration.</summary>
    public const string Executable = "rpi-eeprom-config";

    private static readonly (string Key, string Value)[] Required =
    [
        // Serial console on the bootloader's own UART: the only way to see a boot that never
        // reaches userspace, which is exactly the failure a boot-partition brick produces.
        ("BOOT_UART", "1"),

        // `halt` cuts power rather than idling. §5.1's power-cycle harness depends on it.
        ("POWER_OFF_ON_HALT", "1"),

        // SD card, then USB, then network, then restart the sequence. The stock Pi 5 order, and
        // the v1 reference's.
        ("BOOT_ORDER", "0xf461"),
    ];

    private readonly IProcessRunner _processes;
    private readonly ISystemFiles _files;
    private readonly IStateStore _store;
    private readonly BootPartitionGuard _guard;
    private readonly IAgentLog _log;

    /// <summary>Creates the resource.</summary>
    public EepromConfigResource(
        IProcessRunner processes,
        ISystemFiles files,
        IStateStore store,
        BootPartitionGuard guard,
        IAgentLog log)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(log);

        _processes = processes;
        _files = files;
        _store = store;
        _guard = guard;
        _log = log;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public string Detected => "The settings this frame's start-up chip holds are not the ones it was built with.";

    /// <inheritdoc/>
    public string WhyItMatters => "They decide where the frame boots from and whether switching it off really switches it off.";

    /// <summary>The keys this resource owns, and the values it holds them at.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> DesiredKeys { get; } =
        [.. Required.Select(entry => new KeyValuePair<string, string>(entry.Key, entry.Value))];

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        _guard.Tick(BackupPath);

        var expected = string.Join(", ", Required.Select(entry => $"{entry.Key}={entry.Value}"));
        var current = await ReadAsync(cancellationToken).ConfigureAwait(false);

        if (current is null)
        {
            return new ResourceObservation(false, expected, $"{Executable} could not be read");
        }

        var wrong = new List<string>(Required.Length);
        foreach (var (key, value) in Required)
        {
            var actual = ValueOf(current, key);
            if (!string.Equals(actual, value, StringComparison.OrdinalIgnoreCase))
            {
                wrong.Add($"{key}={actual ?? "unset"}");
            }
        }

        return new ResourceObservation(wrong.Count == 0, expected, wrong.Count == 0 ? expected : string.Join(", ", wrong));
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var current = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            // Nothing is applied on top of a configuration that could not be read. A blind write
            // here would replace whatever is actually in the EEPROM with the catalog's three keys
            // and nothing else, which on a Pi 5 is how a frame stops booting.
            return new ResourceAction(
                $"refused to apply a bootloader configuration — {Executable} could not be read first",
                "This frame could not read its start-up chip's current settings, so it has not written any.");
        }

        var proposed = Merge(current);
        var check = Validate(current, proposed);

        if (!check.Valid)
        {
            _log.Fail($"Refusing to apply a bootloader configuration: {check.Problem}");
            return new ResourceAction(
                $"refused to apply a bootloader configuration — {check.Problem}",
                "This frame checked the change it was about to make to its start-up chip, did not like it, and left it alone.");
        }

        // The backup lands before the trial opens, so the FAT32 copy exists even if everything
        // after this line fails. It is the only restore path there is: an EEPROM write cannot be
        // undone from software, and what a person does with a card reader is apply this file back.
        if (!_files.FileExists(BackupPath))
        {
            _files.WriteText(BackupPath, current);
            _log.Info($"Copied the bootloader configuration to {BackupPath} before changing it.");
        }

        if (!_guard.BeginTrial(BackupPath))
        {
            return new ResourceAction(
                "refused to apply a bootloader configuration — this change has already been tried once and did not take",
                "This frame already tried changing its start-up chip and it did not work. It will not try again on its own.");
        }

        _store.WriteText(CandidateFileName, proposed);
        var candidate = _store.PathOf(CandidateFileName);

        var result = await _processes
            .RunAsync(Executable, ["--apply", candidate], cancellationToken)
            .ConfigureAwait(false);

        return new ResourceAction(
            $"{Executable} --apply {candidate} ({string.Join(", ", Required.Select(entry => $"{entry.Key}={entry.Value}"))}, "
                + $"previous configuration kept at {BackupPath})"
                + (result.Succeeded ? string.Empty : $" — refused: {result.Combined}"),
            "Putting this frame's start-up chip back to the settings it was built with.");
    }

    /// <summary>The value of <paramref name="key"/> in a bootloader configuration, or null.</summary>
    public static string? ValueOf(string configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        foreach (var raw in Lines(configuration))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || (line[0] == '[' && line[^1] == ']'))
            {
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && string.Equals(line[..equals].Trim(), key, StringComparison.Ordinal))
            {
                return line[(equals + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// The observed configuration with the catalog's keys set, every other line preserved.
    /// </summary>
    /// <remarks>
    /// A key already present is rewritten <i>in place</i> rather than removed and re-added, so the
    /// section a key sits under is preserved — a Pi 5 configuration is sectioned, and moving a key
    /// out from under its <c>[all]</c> header would change what it applies to. A missing key is
    /// appended at the end, which is inside the last section that was declared.
    /// </remarks>
    public static string Merge(string configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var lines = Lines(configuration);

        foreach (var (key, value) in Required)
        {
            var written = false;

            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index].Trim();
                var equals = line.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0 && string.Equals(line[..equals].Trim(), key, StringComparison.Ordinal))
                {
                    lines[index] = $"{key}={value}";
                    written = true;
                    break;
                }
            }

            if (!written)
            {
                lines.Add($"{key}={value}");
            }
        }

        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Checks that the merge changed only the catalog's own keys and that the result still parses.
    /// </summary>
    /// <remarks>
    /// §5.5's "validate before writing" applied to the one file on this frame that cannot be put
    /// back. The check is structural rather than semantic — nothing here knows which bootloader
    /// settings exist, and guessing would give false confidence — so what it proves is that no line
    /// the agent does not own has moved, and that nothing in the result is neither a section, a
    /// comment nor a <c>key=value</c>.
    /// </remarks>
    public static BootFileVerdict Validate(string original, string proposed)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(proposed);

        var owned = Required.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        var before = Lines(original);
        var after = Lines(proposed);

        if (after.Count < before.Count)
        {
            return BootFileVerdict.Refuse(
                $"the change would remove {before.Count - after.Count} lines from the bootloader configuration");
        }

        for (var index = 0; index < before.Count; index++)
        {
            var was = before[index].Trim();
            var now = after[index].Trim();

            if (string.Equals(was, now, StringComparison.Ordinal))
            {
                continue;
            }

            var equals = was.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0 || !owned.Contains(was[..equals].Trim()))
            {
                return BootFileVerdict.Refuse($"the change would also rewrite '{was}'");
            }
        }

        for (var index = before.Count; index < after.Count; index++)
        {
            var added = after[index].Trim();
            var equals = added.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0 || !owned.Contains(added[..equals].Trim()))
            {
                return BootFileVerdict.Refuse($"the change would also add '{added}'");
            }
        }

        foreach (var raw in after)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                continue;
            }

            if (!line.Contains('=', StringComparison.Ordinal))
            {
                return BootFileVerdict.Refuse($"'{line}' is neither a section, a comment nor a key=value setting");
            }
        }

        foreach (var (key, value) in Required)
        {
            if (!string.Equals(ValueOf(proposed, key), value, StringComparison.Ordinal))
            {
                return BootFileVerdict.Refuse($"the result does not carry {key}={value}");
            }
        }

        return BootFileVerdict.Ok;
    }

    private async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        var result = await _processes.RunAsync(Executable, [], cancellationToken).ConfigureAwait(false);
        return result.Succeeded && result.StandardOutput.Trim().Length > 0 ? result.StandardOutput : null;
    }

    private static List<string> Lines(string content) =>
        [.. content.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n')];
}
