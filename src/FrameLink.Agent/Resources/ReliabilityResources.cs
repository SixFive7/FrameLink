using System.Globalization;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Agent.Resources;

/// <summary>
/// <c>mount.tmp.tmpfs</c> — <c>/tmp</c> lives in RAM, <b>and is big enough to be useful</b>.
/// </summary>
/// <remarks>
/// <para>
/// From guide 12 step 5. Chromium's entire working profile is <c>/tmp/framelink-chromium</c>, so
/// the browser is the busiest writer on the frame and this one mount is what keeps that traffic
/// off the SD card.
/// </para>
/// <para>
/// <b>The size is half the resource, and the catalog calls it a parity trap.</b> Guide 12's
/// command is <c>findmnt … || grep … || echo 'tmpfs /tmp tmpfs defaults,noatime,size=100M 0 0'</c>,
/// and the v1 reference shows <c>/tmp</c> at <c>size=1029504k</c> — systemd's own default of half
/// of RAM, which means the guide's <c>fstab</c> fallback <i>never fired</i>. A frame that took that
/// branch would satisfy "is <c>/tmp</c> tmpfs?" while giving the browser <b>100 MB</b> to work in.
/// Same predicate, radically different frame. So the check is "tmpfs, and at least
/// <see cref="MinimumSizeKb"/>", which the systemd default passes on this hardware and the 100 MB
/// fallback fails.
/// </para>
/// <para>
/// <b>A <c>tmp.mount</c> drop-in, never an <c>/etc/fstab</c> line.</b> The catalog asks for this
/// explicitly: an <c>fstab</c> entry is turned into a unit by <c>systemd-fstab-generator</c> and
/// then competes with the <c>tmp.mount</c> systemd already ships, which is two owners for one mount
/// point. The drop-in is the same mechanism systemd uses itself, so there is only ever one.
/// </para>
/// </remarks>
public sealed class TmpfsMountResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "mount.tmp.tmpfs";

    /// <summary>The unit that mounts <c>/tmp</c>.</summary>
    public const string UnitName = "tmp.mount";

    /// <summary>Where the drop-in goes.</summary>
    public const string DropInPath = "/etc/systemd/system/tmp.mount.d/framelink.conf";

    /// <summary>The mount point.</summary>
    public const string MountPoint = "/tmp";

    /// <summary>
    /// The floor, in kB: 512 MB.
    /// </summary>
    /// <remarks>
    /// Chosen to sit between the two real values rather than to be round. systemd's default on
    /// this 2 GB frame measured <c>1029504k</c> in the v1 reference and passes comfortably; guide
    /// 12's <c>fstab</c> fallback is <c>size=100M</c> and fails, which is the whole point of having
    /// a floor. It is not a fleet setting because the desired value is "whatever systemd's default
    /// gives this machine" — a fraction of RAM, not a number — and the floor exists only to catch
    /// the frame that got the fallback instead.
    /// </remarks>
    public const long MinimumSizeKb = 524_288;

    /// <summary>
    /// The options the drop-in sets — systemd's own defaults for <c>tmp.mount</c>, written out.
    /// </summary>
    /// <remarks>
    /// A known-good literal rather than a computed string. <c>size=50%</c> is what produced the v1
    /// reference's <c>1029504k</c>, and <c>nr_inodes=1m</c> its <c>1048576</c>, so a frame that has
    /// to be repaired lands on exactly the configuration the parity reference was captured from.
    /// </remarks>
    public const string MountOptions = "mode=1777,strictatime,nosuid,nodev,size=50%,nr_inodes=1m";

    private readonly ISystemFiles _files;
    private readonly IProcessRunner _processes;
    private readonly ISystemControl _systemControl;

    /// <summary>Creates the resource.</summary>
    public TmpfsMountResource(ISystemFiles files, IProcessRunner processes, ISystemControl systemControl)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(systemControl);

        _files = files;
        _processes = processes;
        _systemControl = systemControl;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public string Detected => "This frame is writing the browser's scratch files onto its memory card, or has nowhere near enough room for them.";

    /// <inheritdoc/>
    public string WhyItMatters => "It wears the memory card out, and too little room makes the browser fail in ways nothing explains.";

    /// <summary>The exact drop-in this resource converges on.</summary>
    public static string DesiredContent() => "[Mount]\nOptions=" + MountOptions + "\n";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var expected = $"{MountPoint} on tmpfs, at least {MinimumSizeKb / 1024} MB";

        var result = await _processes
            .RunAsync("findmnt", ["-n", "-t", "tmpfs", MountPoint], cancellationToken)
            .ConfigureAwait(false);

        // `findmnt -t tmpfs` exits non-zero and prints nothing when /tmp is not a tmpfs, which is
        // the whole of the first half of this observation: no rows means the browser's scratch is
        // going to the card.
        var line = FirstLine(result.StandardOutput);
        if (!result.Succeeded || line.Length == 0)
        {
            return new ResourceObservation(false, expected, $"{MountPoint} is not on tmpfs");
        }

        var size = SizeKbOf(line);
        if (size is null)
        {
            // A tmpfs with no `size=` in its options is systemd's default expressed by omission,
            // which is the good case — the kernel applies half of RAM. Reported as such rather
            // than guessed at, so the delta never claims a number nobody measured.
            return new ResourceObservation(true, expected, $"{MountPoint} on tmpfs, size unstated (kernel default)");
        }

        var observed = $"{MountPoint} on tmpfs, {size.Value / 1024} MB";
        return new ResourceObservation(
            size.Value >= MinimumSizeKb,
            expected,
            size.Value >= MinimumSizeKb
                ? observed
                : observed + " — the fallback size, not systemd's default");
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        _files.WriteText(DropInPath, DesiredContent());

        // Both, and in this order. The drop-in fixes a `tmp.mount` that runs with the wrong
        // options; the enable fixes a frame where the unit is not wanted by any target at all,
        // which is the state an `/etc/fstab` line or a `systemctl mask` leaves behind. Neither
        // subsumes the other and both are idempotent.
        await _systemControl.RunAsync(["daemon-reload"], cancellationToken).ConfigureAwait(false);
        var enabled = await _systemControl.RunAsync(["enable", UnitName], cancellationToken).ConfigureAwait(false);

        return new ResourceAction(
            $"write {DropInPath} (Options={MountOptions}) and systemctl enable {UnitName}"
                + (enabled.Succeeded ? string.Empty : $" (refused: {enabled.Output})"),
            "Moving this frame's temporary files into memory, with enough room for the browser to work in.");
    }

    /// <summary>The <c>size=</c> option of a <c>findmnt</c> row, in kB, or null if it has none.</summary>
    /// <remarks>
    /// The row is <c>TARGET SOURCE FSTYPE OPTIONS</c>, and the options are the last field — the
    /// exact shape the v1 reference captured. Suffixes are the kernel's own
    /// (<c>k</c>/<c>m</c>/<c>g</c>, and a bare number is bytes), so a percentage or any form this
    /// does not recognise reads as "unstated" rather than as zero. Failing towards "no complaint"
    /// is right here: the only fault worth firing on is a size that is definitely too small.
    /// </remarks>
    public static long? SizeKbOf(string mountLine)
    {
        ArgumentNullException.ThrowIfNull(mountLine);

        var fields = mountLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length == 0)
        {
            return null;
        }

        foreach (var option in fields[^1].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!option.StartsWith("size=", StringComparison.Ordinal))
            {
                continue;
            }

            var value = option["size=".Length..];
            if (value.Length == 0)
            {
                return null;
            }

            var unit = value[^1];
            var digits = char.IsAsciiDigit(unit) ? value : value[..^1];

            if (!long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount))
            {
                return null;
            }

            return unit switch
            {
                'k' or 'K' => amount,
                'm' or 'M' => amount * 1024,
                'g' or 'G' => amount * 1024 * 1024,
                _ when char.IsAsciiDigit(unit) => amount / 1024,
                _ => null,
            };
        }

        return null;
    }

    private static string FirstLine(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0)
            {
                return line;
            }
        }

        return string.Empty;
    }
}

/// <summary>
/// <c>swap.no-file-backed</c> — nothing is swapping onto the SD card.
/// </summary>
/// <remarks>
/// <para>
/// From guide 12 step 5, and the negative half of <see cref="SwapZramResource"/>: that one asserts
/// compressed RAM swap is <i>present</i>, this one asserts that nothing else is. They are separate
/// resources because they are separate diagnoses — "there is no swap" and "swap is eating the
/// card" need different fixes — and because a frame can be in either state independently.
/// </para>
/// <para>
/// <b>The guard is a no-op on this hardware today, and it is kept anyway.</b>
/// <c>dphys-swapfile</c> is not installed in the v1 reference; it is the older Raspberry Pi OS
/// swap mechanism and exists on images that came up through it. The assertion is cheap, the
/// failure it prevents is a card worn out by writes nobody is watching, and a resource that is
/// normally already satisfied costs one <c>swapon</c> read per pass.
/// </para>
/// </remarks>
public sealed class NoFileSwapResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "swap.no-file-backed";

    /// <summary>The older SD-backed swap mechanism this resource stands down.</summary>
    public const string LegacyUnit = "dphys-swapfile";

    private readonly IProcessRunner _processes;
    private readonly ISystemControl _systemControl;

    /// <summary>Creates the resource.</summary>
    public NoFileSwapResource(IProcessRunner processes, ISystemControl systemControl)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(systemControl);

        _processes = processes;
        _systemControl = systemControl;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn => [SwapZramResource.ResourceName];

    /// <inheritdoc/>
    public string Detected => "This frame is using its memory card as extra memory.";

    /// <inheritdoc/>
    public string WhyItMatters => "That is the fastest way to wear the card out, and it makes the frame slow while it happens.";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        const string Expected = "no file-backed swap, and dphys-swapfile not enabled";

        var offenders = await FileBackedSwapAsync(cancellationToken).ConfigureAwait(false);

        var legacy = await _systemControl
            .RunAsync(["is-enabled", LegacyUnit], cancellationToken)
            .ConfigureAwait(false);

        // `is-enabled` exits non-zero for both `disabled` and `not-found`, so the exit code says
        // nothing useful here and the word it printed says everything. An empty answer is the
        // absent-unit case on a system whose systemctl said nothing at all.
        var state = legacy.Output.Trim();
        var legacyOn = state.Length > 0
            && !string.Equals(state, "disabled", StringComparison.Ordinal)
            && !string.Equals(state, "not-found", StringComparison.Ordinal)
            && !string.Equals(state, "masked", StringComparison.Ordinal);

        if (offenders.Count == 0 && !legacyOn)
        {
            return new ResourceObservation(true, Expected, Expected);
        }

        var wrong = new List<string>(2);
        if (offenders.Count > 0)
        {
            wrong.Add("file-backed swap active: " + string.Join(", ", offenders));
        }

        if (legacyOn)
        {
            wrong.Add($"{LegacyUnit} is {state}");
        }

        return new ResourceObservation(false, Expected, string.Join("; ", wrong));
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var changes = new List<string>(2);

        foreach (var offender in await FileBackedSwapAsync(cancellationToken).ConfigureAwait(false))
        {
            var off = await _processes
                .RunAsync("swapoff", [offender], cancellationToken)
                .ConfigureAwait(false);

            changes.Add(off.Succeeded ? $"swapoff {offender}" : $"swapoff {offender} (refused: {off.Combined})");
        }

        var disabled = await _systemControl
            .RunAsync(["disable", "--now", LegacyUnit], cancellationToken)
            .ConfigureAwait(false);

        // A unit that is not installed makes this fail, and that failure is not a fault — it is
        // the normal case on every image this build targets. It goes into the change text so the
        // record is honest, and the observation above is what decides whether anything is wrong.
        changes.Add(disabled.Succeeded
            ? $"systemctl disable --now {LegacyUnit}"
            : $"systemctl disable --now {LegacyUnit} (not installed, nothing to do)");

        return new ResourceAction(
            string.Join(" · ", changes),
            "Stopping this frame from using its memory card as extra memory.");
    }

    /// <summary>Every active swap device that is not compressed RAM.</summary>
    /// <remarks>
    /// The rule is by <i>type</i> and not by name: <c>swapon</c> reports <c>partition</c> for
    /// <c>/dev/zram0</c> and <c>file</c> for a swap file, so a swap file living somewhere the
    /// catalog never thought of is still caught. A device that is neither — a real disk partition
    /// on some future frame — is left alone, because this resource is about the card and guessing
    /// at hardware nobody has is how a correct frame gets a spurious repair.
    /// </remarks>
    private async Task<List<string>> FileBackedSwapAsync(CancellationToken cancellationToken)
    {
        var result = await _processes.RunAsync("swapon", ["--show"], cancellationToken).ConfigureAwait(false);
        var offenders = new List<string>();

        foreach (var raw in result.StandardOutput.Split('\n'))
        {
            var fields = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length < 2 || string.Equals(fields[0], "NAME", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(fields[1], "file", StringComparison.OrdinalIgnoreCase))
            {
                offenders.Add(fields[0]);
            }
        }

        return offenders;
    }
}

/// <summary>
/// <c>apt.auto-upgrades-enabled</c> — the two <c>APT::Periodic</c> switches.
/// </summary>
/// <remarks>
/// <para>
/// From guide 12 step 6. <b>The guide's own route is unusable by the agent</b>:
/// <c>dpkg-reconfigure -plow unattended-upgrades</c> is a full-screen interactive dialog. The
/// catalog resolves that by writing <c>20auto-upgrades</c> directly, which is what the dialog does
/// anyway.
/// </para>
/// <para>
/// <b>This is the on/off switch for the feature, and the package is not.</b> Turning security
/// updates off leaves <c>unattended-upgrades</c> installed with both switches at <c>0</c>, which
/// the catalog states as the intended shape: the package is inert with the switches off, and
/// making the <i>package</i> the toggle would mean a purge-and-reinstall — an apt transaction and,
/// under §2.4, a reboot — to change two characters. It also keeps one diagnosis per resource:
/// "the machinery is missing" and "the machinery is switched off" are different faults.
/// </para>
/// <para>
/// <b>Observed through <c>apt-config dump</c>, not through the file.</b> apt merges every file in
/// <c>apt.conf.d</c>, so another file can override this one and a <c>cat</c> would report a
/// setting that is not in force. The effective value is the only one worth comparing — the same
/// reasoning that makes the autologin drop-in read systemd's effective <c>ExecStart</c>.
/// </para>
/// </remarks>
public sealed class AptAutoUpgradesResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "apt.auto-upgrades-enabled";

    /// <summary>Fleet setting: whether the frame takes Debian's security updates (§3.4).</summary>
    public const string SettingKey = "updates.osSecurityAuto";

    /// <summary>Where the switches are written.</summary>
    public const string ConfigPath = "/etc/apt/apt.conf.d/20auto-upgrades";

    /// <summary>The switch that refreshes the package lists.</summary>
    public const string UpdateListsKey = "APT::Periodic::Update-Package-Lists";

    /// <summary>The switch that runs the unattended upgrade.</summary>
    public const string UnattendedUpgradeKey = "APT::Periodic::Unattended-Upgrade";

    private readonly ISystemFiles _files;
    private readonly IProcessRunner _processes;
    private readonly FleetValues _values;

    /// <summary>Creates the resource.</summary>
    public AptAutoUpgradesResource(ISystemFiles files, IProcessRunner processes, FleetValues values)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(values);

        _files = files;
        _processes = processes;
        _values = values;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn => [PackageResource.Prefix + "unattended-upgrades"];

    /// <inheritdoc/>
    public string Detected => "This frame is not set to install its own security fixes.";

    /// <inheritdoc/>
    public string WhyItMatters => "Nobody logs in to this frame, so if it does not fix itself nobody will.";

    /// <summary>Whether the operator wants automatic security updates. On unless switched off.</summary>
    /// <remarks>
    /// <para>
    /// The default is correct on an unadopted frame — a frame nobody has adopted still wants
    /// security fixes — so this resource declares no adoption edge, per the catalog's
    /// <c>dependsOn</c> rule.
    /// </para>
    /// <para>
    /// Four spellings of "off" are accepted and everything unrecognised means on, which is the
    /// safe direction to be wrong in: a mistyped setting must never leave a frame nobody logs in
    /// to without security fixes, and the failure of a typo in the other direction is only that
    /// the operator's intent is visibly not applied.
    /// </para>
    /// </remarks>
    public bool Enabled => _values.Find(SettingKey) is not { } configured
        || !(string.Equals(configured, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(configured, "0", StringComparison.Ordinal)
            || string.Equals(configured, "off", StringComparison.OrdinalIgnoreCase)
            || string.Equals(configured, "no", StringComparison.OrdinalIgnoreCase));

    /// <summary>The exact file this resource converges on.</summary>
    public string DesiredContent()
    {
        var value = Enabled ? "1" : "0";
        return $"{UpdateListsKey} \"{value}\";\n{UnattendedUpgradeKey} \"{value}\";\n";
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var want = Enabled ? "1" : "0";
        var expected = $"{UpdateListsKey}={want}, {UnattendedUpgradeKey}={want}";

        var dump = await AptConfig.DumpAsync(_processes, cancellationToken).ConfigureAwait(false);
        var lists = AptConfig.Value(dump, UpdateListsKey);
        var upgrade = AptConfig.Value(dump, UnattendedUpgradeKey);

        var observed = $"{UpdateListsKey}={lists ?? "unset"}, {UnattendedUpgradeKey}={upgrade ?? "unset"}";

        return new ResourceObservation(
            string.Equals(lists, want, StringComparison.Ordinal) && string.Equals(upgrade, want, StringComparison.Ordinal),
            expected,
            observed);
    }

    /// <inheritdoc/>
    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _files.WriteText(ConfigPath, DesiredContent());

        return ValueTask.FromResult(new ResourceAction(
            $"write {ConfigPath} with both APT::Periodic switches at {(Enabled ? "1" : "0")}",
            Enabled
                ? "Telling this frame to fetch and install security fixes on its own."
                : "Switching off this frame's automatic security fixes, because you asked for that."));
    }
}

/// <summary>
/// <c>apt.unattended-upgrades.allowed-origins</c> — <i>which</i> updates install themselves.
/// </summary>
/// <remarks>
/// <para>
/// Guide 12 step 6 never touches this, because Debian's shipped default is already security-only.
/// version2.md Appendix B item 4 nonetheless requires it as a reconciled resource so the policy is
/// visible and centrally changeable rather than an inherited default nobody can see.
/// </para>
/// <para>
/// <b>The fleet setting names a policy; it never supplies one.</b> §2.2 is explicit that the Fleet
/// Manager supplies values and never logic, and an origins pattern is a rule apt executes against
/// the archive — closer to logic than to a value, and it lands in a file that decides what gets
/// installed on the frame as root. So <c>updates.osUpgradePolicy</c> selects from
/// <see cref="Policies"/>, which is compiled in, and an unrecognised name falls back to
/// <see cref="SecurityOnly"/> rather than to nothing. Falling back to the safe end matters: a
/// typo must never widen what installs itself.
/// </para>
/// <para>
/// <b><c>#clear</c> is why this works at all.</b> apt.conf lists <i>append</i>, so a second file
/// declaring <c>Origins-Pattern</c> would produce the union of itself and Debian's
/// <c>50unattended-upgrades</c> — the opposite of restricting anything. The file therefore clears
/// the list before declaring it, and the file name sorts after <c>50unattended-upgrades</c> so the
/// clear happens second.
/// </para>
/// <para>
/// The catalog also lists <c>unattended-upgrade --dry-run -d</c> as a policy readback. It is not
/// implemented: it resolves the archive over the network, so it would turn an Observe into a
/// minutes-long operation that fails when the internet does, and <c>apt-config dump</c> already
/// reports the effective merged value. Named rather than dropped.
/// </para>
/// </remarks>
public sealed class UnattendedUpgradesPolicyResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "apt.unattended-upgrades.allowed-origins";

    /// <summary>Fleet setting: which policy applies (§3.4).</summary>
    public const string SettingKey = "updates.osUpgradePolicy";

    /// <summary>Policy name: Debian security updates and nothing else.</summary>
    public const string SecurityOnly = "security";

    /// <summary>Policy name: security updates plus the ordinary stable and vendor archives.</summary>
    public const string AllArchives = "all";

    /// <summary>Where the policy is written.</summary>
    public const string ConfigPath = "/etc/apt/apt.conf.d/51framelink-unattended-upgrades";

    /// <summary>The apt configuration list this resource owns.</summary>
    public const string OriginsKey = "Unattended-Upgrade::Origins-Pattern";

    private static readonly string[] SecurityPatterns =
    [
        "origin=Debian,codename=${distro_codename},label=Debian-Security",
        "origin=Debian,codename=${distro_codename}-security,label=Debian-Security",
    ];

    private static readonly string[] AllPatterns =
    [
        "origin=Debian,codename=${distro_codename},label=Debian-Security",
        "origin=Debian,codename=${distro_codename}-security,label=Debian-Security",
        "origin=Debian,codename=${distro_codename},label=Debian",
        "origin=Raspberry Pi Foundation,codename=${distro_codename},label=Raspberry Pi Foundation",
    ];

    private readonly ISystemFiles _files;
    private readonly IProcessRunner _processes;
    private readonly FleetValues _values;

    /// <summary>Creates the resource.</summary>
    public UnattendedUpgradesPolicyResource(ISystemFiles files, IProcessRunner processes, FleetValues values)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(values);

        _files = files;
        _processes = processes;
        _values = values;
    }

    /// <summary>The policies an operator may name, and the patterns each one means.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Policies { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [SecurityOnly] = SecurityPatterns,
            [AllArchives] = AllPatterns,
        };

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn => [PackageResource.Prefix + "unattended-upgrades"];

    /// <inheritdoc/>
    public string Detected => "This frame is not being clear about which updates it installs by itself.";

    /// <inheritdoc/>
    public string WhyItMatters => "Only security fixes should arrive unattended; anything wider can change how the frame behaves overnight.";

    /// <summary>The policy in force, falling back to the narrow one.</summary>
    public string Policy => _values.Find(SettingKey) is { Length: > 0 } named && Policies.ContainsKey(named)
        ? named
        : SecurityOnly;

    /// <summary>The patterns the policy in force means.</summary>
    public IReadOnlyList<string> DesiredPatterns => Policies[Policy];

    /// <summary>The exact file this resource converges on.</summary>
    public string DesiredContent()
    {
        var lines = new List<string>(DesiredPatterns.Count + 3)
        {
            $"#clear {OriginsKey};",
            OriginsKey + " {",
        };

        foreach (var pattern in DesiredPatterns)
        {
            lines.Add($"        \"{pattern}\";");
        }

        lines.Add("};");
        return string.Join('\n', lines) + "\n";
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var expected = $"{Policy}: {string.Join(" · ", DesiredPatterns)}";

        var dump = await AptConfig.DumpAsync(_processes, cancellationToken).ConfigureAwait(false);
        var actual = AptConfig.List(dump, OriginsKey);

        if (actual.Count == 0)
        {
            return new ResourceObservation(false, expected, "no origins pattern is in force");
        }

        var matches = actual.Count == DesiredPatterns.Count;
        for (var index = 0; matches && index < actual.Count; index++)
        {
            matches = string.Equals(DesiredPatterns[index], actual[index], StringComparison.Ordinal);
        }

        return new ResourceObservation(matches, expected, string.Join(" · ", actual));
    }

    /// <inheritdoc/>
    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _files.WriteText(ConfigPath, DesiredContent());

        return ValueTask.FromResult(new ResourceAction(
            $"write {ConfigPath} with the '{Policy}' policy: {string.Join(", ", DesiredPatterns)}",
            Policy is SecurityOnly
                ? "Limiting what this frame installs by itself to security fixes only."
                : "Letting this frame install ordinary updates by itself as well as security fixes, because you asked for that."));
    }
}

/// <summary>
/// Reading apt's <i>effective</i> configuration, which is the only version worth comparing.
/// </summary>
/// <remarks>
/// <c>apt-config dump</c> merges every file under <c>/etc/apt/apt.conf.d</c> in name order and
/// prints the result as <c>Key "value";</c> lines, with list members as repeated
/// <c>Key::</c> entries. A frame can therefore have a perfectly correct file whose value is
/// overridden by a later one, and only this view can see that.
/// </remarks>
public static class AptConfig
{
    /// <summary>Runs <c>apt-config dump</c>, or returns empty if it cannot be run.</summary>
    /// <remarks>
    /// An unavailable <c>apt-config</c> yields no values, which every caller reads as drift. That
    /// is the conservative direction: a resource that cannot see apt's configuration escalates to
    /// a person rather than reporting a frame it could not inspect as correct.
    /// </remarks>
    public static async Task<string> DumpAsync(IProcessRunner processes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processes);

        var result = await processes.RunAsync("apt-config", ["dump"], cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput : string.Empty;
    }

    /// <summary>The scalar value of <paramref name="key"/>, or null.</summary>
    public static string? Value(string dump, string key)
    {
        foreach (var (name, value) in Entries(dump))
        {
            if (string.Equals(name, key, StringComparison.Ordinal))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>The members of the list at <paramref name="key"/>, in order.</summary>
    /// <remarks>
    /// apt stores a list as unnamed children, and <c>apt-config dump</c> renders them by repeating
    /// the parent followed by a bare <c>::</c>. The parent itself is also printed, with an empty
    /// value, and is skipped. A member is matched on the <c>key::</c> prefix with nothing further
    /// nested beneath it, so a genuine named child — <c>key::Something</c> — is not mistaken for a
    /// list entry.
    /// </remarks>
    public static IReadOnlyList<string> List(string dump, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var member = key + "::";
        var values = new List<string>();

        foreach (var (name, value) in Entries(dump))
        {
            if (value.Length == 0 || !name.StartsWith(member, StringComparison.Ordinal))
            {
                continue;
            }

            if (!name[member.Length..].Contains("::", StringComparison.Ordinal))
            {
                values.Add(value);
            }
        }

        return values;
    }

    /// <summary>Every <c>Key "value";</c> pair in a dump, unquoted.</summary>
    private static IEnumerable<(string Key, string Value)> Entries(string dump)
    {
        ArgumentNullException.ThrowIfNull(dump);

        foreach (var raw in dump.Split('\n'))
        {
            var line = raw.Trim();
            var quote = line.IndexOf('"', StringComparison.Ordinal);
            if (quote <= 0)
            {
                continue;
            }

            var close = line.LastIndexOf('"');
            if (close <= quote)
            {
                continue;
            }

            yield return (line[..quote].Trim(), line[(quote + 1)..close]);
        }
    }
}
