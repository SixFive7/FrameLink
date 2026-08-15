using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Agent.Resources;

/// <summary>
/// The cloud-init seed on the boot partition, read as a competing owner rather than as an input.
/// </summary>
/// <remarks>
/// <para>
/// version2.md Appendix B item 1 recorded the hostname as cloud-init managed and silently
/// reverted; measured on the mule 2026-08-15 that did not reproduce, and
/// <see cref="HostnameResource"/> carries the disproof. What the catalog is careful to say is that
/// the disproof was about the <i>hostname</i> — cloud-init does ship a <c>timezone</c> module,
/// nobody has read this image's seed for a <c>timezone:</c> key, and "read the seed before
/// designing an Act around it" is the instruction.
/// </para>
/// <para>
/// So this is a reader and nothing else. It never decides a desired value and it is never written
/// to. Where a seed carries a directive that disagrees with the value in force, the resource says
/// so in its observed text, and a person can go and look. That is the honest position for an
/// inference nobody has tested: name it, do not act on it.
/// </para>
/// </remarks>
public static class CloudInitSeed
{
    /// <summary>The NoCloud user-data file on the boot partition.</summary>
    public const string UserDataPath = "/boot/firmware/user-data";

    /// <summary>
    /// The value of an <b>uncommented</b> top-level <c>key:</c> directive, or null.
    /// </summary>
    /// <remarks>
    /// The comment rule is the whole of it. The mule's seed carries <c>#hostname: raspberrypi</c>,
    /// and a reader that ignored the <c>#</c> would report a competing owner that is not competing
    /// with anything — which is how a measured non-event turns back into a suspicion.
    /// </remarks>
    public static string? Directive(ISystemFiles files, string key)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (files.ReadText(UserDataPath) is not { Length: > 0 } document)
        {
            return null;
        }

        foreach (var raw in document.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            // Top-level only: an indented line belongs to some other block, and a `timezone:`
            // nested under something else is not the directive being looked for.
            if (raw.Length == 0 || char.IsWhiteSpace(raw[0]) || raw[0] == '#')
            {
                continue;
            }

            var line = raw.TrimEnd();
            if (!line.StartsWith(key + ":", StringComparison.Ordinal))
            {
                continue;
            }

            var value = line[(key.Length + 1)..].Trim().Trim('"', '\'');
            return value.Length == 0 ? null : value;
        }

        return null;
    }
}

/// <summary>
/// <c>system.timezone</c> — the frame's clock is in the household's own time.
/// </summary>
/// <remarks>
/// <para>
/// From guide 2 step 9's Imager localisation, required as a fleet setting by §3.4. Directly
/// visible to whoever lives with the frame: the 03:00 restart window and the slideshow both run on
/// local time, so a frame an hour out blinks at the wrong hour.
/// </para>
/// <para>
/// <b>No catalog default, and the resource does nothing without a value.</b> A time zone is a
/// property of the room the frame stands in; UTC is not a sensible guess for a photo frame in
/// somebody's kitchen, and picking one would mean every unadopted frame silently disagreeing with
/// its household. So an unset <c>locale.timeZone</c> leaves the frame's existing zone alone and
/// says so, exactly as <see cref="HostnameResource"/> does for an unset name.
/// </para>
/// <para>
/// <b>Verified after a reboot like everything else</b>, and that is §2.4 rather than a suspicion
/// about this particular setting. The seed is read as a competing owner and reported, never acted
/// on — see <see cref="CloudInitSeed"/>.
/// </para>
/// </remarks>
public sealed class TimeZoneResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "system.timezone";

    /// <summary>Fleet setting carrying the zone (§3.4).</summary>
    public const string SettingKey = "locale.timeZone";

    /// <summary>The cloud-init directive that would own this value if it were set.</summary>
    public const string SeedKey = "timezone";

    private readonly ISystemFiles _files;
    private readonly IProcessRunner _processes;
    private readonly FleetValues _values;

    /// <summary>Creates the resource.</summary>
    public TimeZoneResource(ISystemFiles files, IProcessRunner processes, FleetValues values)
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
    public IReadOnlyList<string> DependsOn => [AdoptionResource.ResourceName];

    /// <inheritdoc/>
    public string Detected => "This frame's clock is not set to your part of the world.";

    /// <inheritdoc/>
    public string WhyItMatters => "The frame does things at set times of day, and they happen at the wrong hour if its clock disagrees with yours.";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var live = await LiveZoneAsync(cancellationToken).ConfigureAwait(false);
        var desired = _values.Find(SettingKey);

        if (desired is null)
        {
            return new ResourceObservation(
                true,
                "no time zone set by the Fleet Manager",
                live is { Length: > 0 } ? $"left at {live}" : "left alone");
        }

        if (!LocaleValue.IsSaneZone(desired))
        {
            // A value nothing on this frame could apply. Reported as drift rather than acted on,
            // so it escalates to the person who typed it instead of being handed to timedatectl.
            return new ResourceObservation(false, desired, $"'{desired}' is not a usable time zone name");
        }

        var seed = CloudInitSeed.Directive(_files, SeedKey);
        var observed = live ?? "unknown";

        if (seed is { Length: > 0 } && !string.Equals(seed, desired, StringComparison.Ordinal))
        {
            observed += $" (the boot-partition seed asks for {seed}; nothing here changes that)";
        }

        return new ResourceObservation(string.Equals(live, desired, StringComparison.Ordinal), desired, observed);
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var desired = _values.Find(SettingKey);

        if (desired is null || !LocaleValue.IsSaneZone(desired))
        {
            return new ResourceAction(
                $"refused to set the time zone to '{desired ?? "nothing"}'",
                "This frame was given a time zone it does not recognise, so it has left its clock alone.");
        }

        var result = await _processes
            .RunAsync("timedatectl", ["set-timezone", desired], cancellationToken)
            .ConfigureAwait(false);

        return new ResourceAction(
            $"timedatectl set-timezone {desired}" + (result.Succeeded ? string.Empty : $" (refused: {result.Combined})"),
            $"Setting this frame's clock to {desired} time.");
    }

    private async Task<string?> LiveZoneAsync(CancellationToken cancellationToken)
    {
        var result = await _processes
            .RunAsync("timedatectl", ["show", "-p", "Timezone", "--value"], cancellationToken)
            .ConfigureAwait(false);

        return result.Succeeded && result.StandardOutput.Trim() is { Length: > 0 } zone ? zone : null;
    }
}

/// <summary>
/// <c>system.locale</c> — the frame's language and its keyboard layout.
/// </summary>
/// <remarks>
/// <para>
/// From guide 2 step 9, required as a fleet setting by §3.4. One resource covering both halves,
/// as the catalog has it, with the delta naming which half drifted.
/// </para>
/// <para>
/// <b>The keyboard half rests on measured evidence and the language half does not.</b> The catalog
/// is careful about this and so is the implementation. <c>console-setup.service</c> and
/// <c>keyboard-setup.service</c> are <i>enabled in the v1 reference</i> and re-apply keyboard
/// configuration at every boot from <c>/etc/default/keyboard</c> — a competing owner evidenced by
/// the inventory rather than by analogy with the disproved hostname trap. That is why the Act goes
/// through <c>localectl set-x11-keymap</c>, which writes that file, instead of setting a keymap
/// those services would put back at the next boot. The language half has no such evidence and
/// makes no such claim.
/// </para>
/// <para>
/// <b>Nothing is guessed.</b> Either half is converged only if the Fleet Manager has set it; with
/// both unset the resource reports what the frame already has and leaves it alone. A photo frame
/// switched to <c>C.UTF-8</c> because nobody configured a fleet default would be a regression
/// caused entirely by this resource existing.
/// </para>
/// </remarks>
public sealed class LocaleResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "system.locale";

    /// <summary>Fleet setting carrying the system language (§3.4).</summary>
    public const string LanguageKey = "locale.language";

    /// <summary>Fleet setting carrying the keyboard layout (§3.4).</summary>
    public const string KeyboardKey = "locale.keyboard";

    /// <summary>The file <c>console-setup</c> and <c>keyboard-setup</c> re-apply from at every boot.</summary>
    public const string KeyboardPath = "/etc/default/keyboard";

    private readonly ISystemFiles _files;
    private readonly IProcessRunner _processes;
    private readonly FleetValues _values;

    /// <summary>Creates the resource.</summary>
    public LocaleResource(ISystemFiles files, IProcessRunner processes, FleetValues values)
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
    public IReadOnlyList<string> DependsOn => [AdoptionResource.ResourceName];

    /// <inheritdoc/>
    public string Detected => "This frame is not set to your language or your keyboard.";

    /// <inheritdoc/>
    public string WhyItMatters => "Anything the frame writes on its own screen comes out in the wrong language, and a keyboard plugged into it types the wrong characters.";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var language = _values.Find(LanguageKey);
        var keyboard = _values.Find(KeyboardKey);

        var liveLanguage = await LiveLanguageAsync(cancellationToken).ConfigureAwait(false);
        var liveKeyboard = LiveKeyboard();

        if (language is null && keyboard is null)
        {
            return new ResourceObservation(
                true,
                "no language or keyboard set by the Fleet Manager",
                $"left at LANG={liveLanguage ?? "unset"}, XKBLAYOUT={liveKeyboard ?? "unset"}");
        }

        var wanted = new List<string>(2);
        var wrong = new List<string>(2);

        if (language is { Length: > 0 })
        {
            if (!LocaleValue.IsSaneLanguage(language))
            {
                return new ResourceObservation(false, language, $"'{language}' is not a usable locale name");
            }

            wanted.Add($"LANG={language}");
            if (!string.Equals(liveLanguage, language, StringComparison.Ordinal))
            {
                wrong.Add($"LANG={liveLanguage ?? "unset"}");
            }
        }

        if (keyboard is { Length: > 0 })
        {
            if (!LocaleValue.IsSaneKeyboard(keyboard))
            {
                return new ResourceObservation(false, keyboard, $"'{keyboard}' is not a usable keyboard layout");
            }

            wanted.Add($"XKBLAYOUT={keyboard}");
            if (!string.Equals(liveKeyboard, keyboard, StringComparison.Ordinal))
            {
                wrong.Add($"XKBLAYOUT={liveKeyboard ?? "unset"}");
            }
        }

        var expected = string.Join(", ", wanted);
        return new ResourceObservation(wrong.Count == 0, expected, wrong.Count == 0 ? expected : string.Join("; ", wrong));
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var changes = new List<string>(2);

        if (_values.Find(LanguageKey) is { Length: > 0 } language && LocaleValue.IsSaneLanguage(language))
        {
            var result = await _processes
                .RunAsync("localectl", ["set-locale", "LANG=" + language], cancellationToken)
                .ConfigureAwait(false);

            changes.Add($"localectl set-locale LANG={language}"
                + (result.Succeeded ? string.Empty : $" (refused: {result.Combined})"));
        }

        if (_values.Find(KeyboardKey) is { Length: > 0 } keyboard && LocaleValue.IsSaneKeyboard(keyboard))
        {
            // set-x11-keymap and not set-keymap: this is the call that rewrites
            // /etc/default/keyboard, which is the file the two enabled *-setup units re-apply from
            // at every boot. Setting the console keymap alone would be undone by them.
            var result = await _processes
                .RunAsync("localectl", ["set-x11-keymap", keyboard], cancellationToken)
                .ConfigureAwait(false);

            changes.Add($"localectl set-x11-keymap {keyboard}"
                + (result.Succeeded ? string.Empty : $" (refused: {result.Combined})"));
        }

        if (changes.Count == 0)
        {
            return new ResourceAction(
                "refused to set a language or keyboard — nothing usable was supplied",
                "This frame was given a language or keyboard it does not recognise, so it has left both alone.");
        }

        return new ResourceAction(
            string.Join(" · ", changes),
            "Setting this frame to your language and your keyboard layout.");
    }

    /// <summary>The <c>XKBLAYOUT=</c> of <c>/etc/default/keyboard</c>, or null.</summary>
    /// <remarks>
    /// Read from the file rather than from <c>localectl status</c> deliberately: the file is what
    /// the two enabled boot-time services act on, so it is the value actually in force at the next
    /// boot — and §2.4 is about what survives a boot, not about what a tool reports now.
    /// </remarks>
    public string? LiveKeyboard()
    {
        if (_files.ReadText(KeyboardPath) is not { Length: > 0 } document)
        {
            return null;
        }

        foreach (var raw in document.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("XKBLAYOUT=", StringComparison.Ordinal))
            {
                continue;
            }

            var value = line["XKBLAYOUT=".Length..].Trim().Trim('"', '\'');
            return value.Length == 0 ? null : value;
        }

        return null;
    }

    private async Task<string?> LiveLanguageAsync(CancellationToken cancellationToken)
    {
        var result = await _processes
            .RunAsync("localectl", ["status"], cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return null;
        }

        foreach (var raw in result.StandardOutput.Split('\n'))
        {
            var line = raw.Trim();
            var marker = line.IndexOf("LANG=", StringComparison.Ordinal);
            if (marker < 0)
            {
                continue;
            }

            var value = line[(marker + "LANG=".Length)..].Trim();
            var end = value.IndexOf(' ', StringComparison.Ordinal);
            if (end >= 0)
            {
                value = value[..end];
            }

            return value.Length == 0 ? null : value;
        }

        return null;
    }
}

/// <summary>
/// <c>boot.cmdline.wifi-regdom</c> — the 802.11 regulatory domain, on the kernel command line.
/// </summary>
/// <remarks>
/// <para>
/// From the v1 inventory's <c>KERNEL_CMDLINE</c> (<c>cfg80211.ieee80211_regdom=NL</c>), seeded at
/// flash time and named by no guide. Low operational importance on a wired frame, high parity
/// importance — and it is the one member of the <c>locale.*</c> family with legal consequences,
/// which is why it is the one that must never be guessed.
/// </para>
/// <para>
/// <b>There is deliberately no catalog default.</b> A regulatory domain is a property of the
/// country the frame is standing in. <c>NL</c> is this operator's own and not a universal, and the
/// only value that would be safe everywhere — <c>00</c> — is the most restrictive one rather than
/// a correct one. So the resource declares <c>agent.adoption</c> and converges nothing until a
/// value arrives, and a frame that was flashed with one keeps it.
/// </para>
/// <para>
/// <b>Brick-capable, and it shares its file with a resource seventy-five positions earlier.</b>
/// <c>cmdline.txt</c> must stay one line and <c>boot.cmdline.fbcon-rotate</c> writes it from the
/// head of the order, so both go through <see cref="BootConfigText.SetToken"/> — one line-aware editor
/// that reads the file it is about to change and rebuilds the whole line from it. The rest of
/// §5.5's discipline is <see cref="BootPartitionGuard"/>: a known-good literal, a minimality
/// check before the write, a FAT32 backup a card reader can reach, and boot-count self-repair.
/// </para>
/// <para>
/// <c>iw reg get</c> is carried in the observed text and is not part of the in-sync predicate. The
/// wireless interface is <c>DOWN</c> in the v1 reference and a frame on Ethernet may have no
/// wireless hardware in use at all, so a resource that required the regulatory domain to be
/// <i>live</i> would escalate forever over something no software can fix — the same reasoning that
/// keeps the panel probe out of the display resource's predicate.
/// </para>
/// </remarks>
public sealed class WifiRegulatoryDomainResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "boot.cmdline.wifi-regdom";

    /// <summary>Fleet setting carrying the ISO country code (§3.4).</summary>
    public const string SettingKey = "locale.wifiCountry";

    /// <summary>The kernel parameter's prefix.</summary>
    public const string TokenPrefix = "cfg80211.ieee80211_regdom=";

    private readonly ISystemFiles _files;
    private readonly BootPartitionGuard _guard;
    private readonly IProcessRunner _processes;
    private readonly FleetValues _values;
    private readonly IAgentLog _log;

    /// <summary>Creates the resource.</summary>
    public WifiRegulatoryDomainResource(
        ISystemFiles files,
        BootPartitionGuard guard,
        IProcessRunner processes,
        FleetValues values,
        IAgentLog log)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(log);

        _files = files;
        _guard = guard;
        _processes = processes;
        _values = values;
        _log = log;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn => [AdoptionResource.ResourceName];

    /// <inheritdoc/>
    public string Detected => "This frame does not know which country's radio rules it has to follow.";

    /// <inheritdoc/>
    public string WhyItMatters => "Wireless channels and transmit power are set by law and differ by country.";

    /// <summary>The country code the Fleet Manager set, or null.</summary>
    public string? Desired => _values.Find(SettingKey)?.ToUpperInvariant();

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        // Self-repair before anything reads the file (§5.5), exactly as the display group does it.
        var verdict = _guard.Tick(BootConfigText.CmdlinePath);

        var onDisk = BootConfigText.FindToken(_files.ReadText(BootConfigText.CmdlinePath), TokenPrefix);
        var inForce = BootConfigText.FindToken(_files.ReadText(ConsoleRotationResource.ProcCmdlinePath), TokenPrefix);
        var desired = Desired;

        if (desired is null)
        {
            return new ResourceObservation(
                true,
                "no regulatory domain set by the Fleet Manager",
                onDisk is null ? "left unset" : $"left at {onDisk}");
        }

        if (!LocaleValue.IsSaneCountry(desired))
        {
            return new ResourceObservation(false, desired, $"'{desired}' is not a two-letter country code");
        }

        var token = TokenPrefix + desired;
        var correct = string.Equals(onDisk, token, StringComparison.Ordinal)
            && string.Equals(inForce, token, StringComparison.Ordinal);

        if (correct)
        {
            _guard.Confirm(BootConfigText.CmdlinePath);
        }

        if (verdict is GuardVerdict.RolledBack or GuardVerdict.Locked && !string.Equals(onDisk, token, StringComparison.Ordinal))
        {
            return new ResourceObservation(
                false,
                token,
                "the regulatory domain was tried and put back automatically; "
                    + $"{BootPartitionGuard.BackupFor(BootConfigText.CmdlinePath)} holds the version before the change");
        }

        var live = await LiveDomainAsync(cancellationToken).ConfigureAwait(false);

        return new ResourceObservation(
            correct,
            $"{token} in {BootConfigText.CmdlinePath} and in {ConsoleRotationResource.ProcCmdlinePath}",
            $"{BootConfigText.CmdlinePath}={onDisk ?? "absent"}, "
                + $"{ConsoleRotationResource.ProcCmdlinePath}={inForce ?? "absent"}"
                + (live is null ? string.Empty : $" [iw reg get: {live}]"));
    }

    /// <inheritdoc/>
    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Desired is not { Length: > 0 } desired || !LocaleValue.IsSaneCountry(desired))
        {
            return ValueTask.FromResult(new ResourceAction(
                $"refused to write a regulatory domain of '{Desired ?? "nothing"}' to {BootConfigText.CmdlinePath}",
                "This frame was given a country code it does not recognise, so it has not touched its start-up settings."));
        }

        if (!_guard.BeginTrial(BootConfigText.CmdlinePath))
        {
            return ValueTask.FromResult(new ResourceAction(
                $"refused to write {BootConfigText.CmdlinePath} — this change has already been rolled back once",
                "This frame already tried setting its radio country and had to undo it. It will not try again on its own."));
        }

        // Read here and not at Observe time. The rotation resource writes this same single line
        // from position 2, so re-serialising from an earlier read is precisely how the late writer
        // would delete the early writer's parameter.
        var current = _files.ReadText(BootConfigText.CmdlinePath);
        var token = TokenPrefix + desired;
        var updated = BootConfigText.SetToken(current, TokenPrefix, token);
        var check = BootConfigText.ValidateCmdlineToken(current, updated, TokenPrefix, token);

        if (!check.Valid)
        {
            _log.Fail($"Refusing to write {BootConfigText.CmdlinePath}: {check.Problem}");
            return ValueTask.FromResult(new ResourceAction(
                $"refused to write {BootConfigText.CmdlinePath} — {check.Problem}",
                "This frame checked the change it was about to make to its start-up settings, did not like it, and left them alone."));
        }

        _files.WriteText(BootConfigText.CmdlinePath, updated);

        return ValueTask.FromResult(new ResourceAction(
            $"set '{token}' in {BootConfigText.CmdlinePath} "
                + $"(backed up to {BootPartitionGuard.BackupFor(BootConfigText.CmdlinePath)})",
            $"Telling this frame's wireless to follow {desired}'s radio rules."));
    }

    private async Task<string?> LiveDomainAsync(CancellationToken cancellationToken)
    {
        var result = await _processes.RunAsync("iw", ["reg", "get"], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null;
        }

        foreach (var raw in result.StandardOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("country ", StringComparison.Ordinal))
            {
                return line;
            }
        }

        return null;
    }
}

/// <summary>
/// Shape checks for the three <c>locale.*</c> values, applied before anything is handed to a tool.
/// </summary>
/// <remarks>
/// <para>
/// Not sanitisation — <see cref="IProcessRunner"/> takes an argument vector and there is no shell,
/// so a fleet value can never become a second command. These exist because two of the three land
/// somewhere a wrong value is expensive: <c>locale.wifiCountry</c> is written into
/// <c>cmdline.txt</c>, which is brick-capable, and a nonsense value there costs a card swap to
/// undo. Refusing early turns that into an escalation to the person who typed it.
/// </para>
/// <para>
/// They are deliberately structural rather than exhaustive. Nothing here knows the list of valid
/// zone names — the system does, and guessing would give false confidence — so the checks describe
/// what a <i>malformed</i> value looks like and let <c>timedatectl</c> reject the rest.
/// </para>
/// </remarks>
public static class LocaleValue
{
    /// <summary>Whether a string is plausibly an IANA zone name such as <c>Europe/Amsterdam</c>.</summary>
    public static bool IsSaneZone(string? value) =>
        Shaped(value, 64, character => char.IsAsciiLetterOrDigit(character) || character is '/' or '_' or '-' or '+')
        && !value!.StartsWith('/')
        && !value.EndsWith('/')
        && !value.Contains("..", StringComparison.Ordinal);

    /// <summary>Whether a string is plausibly a locale name such as <c>en_GB.UTF-8</c>.</summary>
    public static bool IsSaneLanguage(string? value) =>
        Shaped(value, 32, character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-' or '@');

    /// <summary>Whether a string is plausibly an X11 layout such as <c>gb</c> or <c>us,de</c>.</summary>
    public static bool IsSaneKeyboard(string? value) =>
        Shaped(value, 32, character => char.IsAsciiLetterOrDigit(character) || character is ',' or '_' or '-');

    /// <summary>Whether a string is a two-letter ISO country code, or the world domain <c>00</c>.</summary>
    public static bool IsSaneCountry(string? value) =>
        value is { Length: 2 }
        && (string.Equals(value, "00", StringComparison.Ordinal)
            || (char.IsAsciiLetterUpper(value[0]) && char.IsAsciiLetterUpper(value[1])));

    private static bool Shaped(string? value, int maximumLength, Func<char, bool> permitted)
    {
        if (value is not { Length: > 0 } || value.Length > maximumLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!permitted(character))
            {
                return false;
            }
        }

        return true;
    }
}
