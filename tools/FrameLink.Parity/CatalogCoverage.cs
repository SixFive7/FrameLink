namespace FrameLink.Parity;

/// <summary>
/// <c>reference/resource-catalog.md</c>, read for the resource ids it enumerates.
/// </summary>
/// <remarks>
/// The document heads each block with the id in bold code, and where several resources share a
/// block, with several ids separated by <c>·</c>. Nothing else in the file is a line made
/// <i>entirely</i> of bold code spans — the ordering table spells the same ids inside rows with
/// pipes and prose around them — which is what makes the heading recognisable without a markdown
/// parser.
/// </remarks>
public static class CatalogDocument
{
    /// <summary>Path of the catalog relative to the repository root.</summary>
    public const string RelativePath = "reference/resource-catalog.md";

    /// <summary>Every resource id the catalog enumerates, in document order.</summary>
    public static IReadOnlyList<string> Ids(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        return Parse(File.ReadAllLines(Path.Combine(repositoryRoot, "reference", "resource-catalog.md")));
    }

    /// <summary>Every resource id in the given catalog lines, in order.</summary>
    public static IReadOnlyList<string> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var ids = new List<string>();

        foreach (var line in lines)
        {
            if (!line.StartsWith("**`", StringComparison.Ordinal))
            {
                continue;
            }

            var heading = new List<string>();
            var rest = line;

            while (rest.StartsWith("**`", StringComparison.Ordinal))
            {
                var close = rest.IndexOf("`**", 3, StringComparison.Ordinal);
                if (close < 0)
                {
                    break;
                }

                heading.Add(rest[3..close]);
                rest = rest[(close + 3)..].TrimStart(' ', '·');
            }

            // Only a line that is *nothing but* ids is a heading. Anything left over means this was
            // prose that happened to begin with one.
            if (rest.Length == 0)
            {
                ids.AddRange(heading);
            }
        }

        return ids;
    }
}

/// <summary>
/// Which facet, if any, holds the v1 evidence for each catalog resource.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the second half of honest coverage, and it is the half a state diff normally
/// hides.</b> The first half — inventory sections nothing compares — is visible because the
/// sections are right there in the file. This one is invisible by construction: a resource whose
/// state the capture never recorded produces no difference, no warning and no line of output, and
/// a diff that comes back empty reads exactly like parity.
/// </para>
/// <para>
/// <b>The map is total, and a test enforces it.</b> Every id the catalog enumerates matches
/// exactly one rule below, so adding a resource to the catalog turns the suite red until somebody
/// records where its v1 evidence lives — or records that there is none. That is the only
/// arrangement in which "the diff is empty" can be read as "the frame is at parity" rather than
/// "the harness was not looking".
/// </para>
/// <para>
/// <b>A resource with no v1 evidence is not unverified.</b> It is verified by the other two bars
/// of §5.1's triple bar — the checkpoint assertions the catalog writes as each resource's own
/// Verify, and the validation battery on the mule. What it cannot be is verified <i>by a state
/// diff</i>, and saying which is which is the whole job of this table.
/// </para>
/// </remarks>
public static class CatalogEvidenceMap
{
    private sealed record Rule(string Match, bool Prefix, string? Facet, string Note);

    private static readonly Rule[] Rules =
    [
        new("agent.", true, null,
            "v2-only. The agent did not exist on the v1 frame, so no capture of it could exist. Verified "
            + "by its own Verify and by the Fleet Manager seeing the frame at all."),

        new("app.config.", true, "app.config",
            "The five fields of the app's config.json, which the capture recorded whole."),

        new("app.http.local-origin", false, "units.user.files",
            "v1 served the same 127.0.0.1:8888 origin from framelink-spa.service, whose unit file the "
            + "capture holds. On a v2 frame the agent serves it in-process, so the v1 evidence is a unit "
            + "that is expected to be absent rather than a unit that must match."),

        new("apt.", true, null,
            "The capture never read /etc/apt/apt.conf.d, so v1's periodic and unattended-upgrades "
            + "settings are not in the reference at all. Nothing here can be diffed; both resources "
            + "carry their own Observe."),

        new("audio.alsa.stored-state", false, null,
            "The stored ALSA state is in the ALSA_CARDS block below a '--- state file ---' marker, and "
            + "the capture cut it off mid-control. The live values it restores are compared in full by "
            + "the alsa.mixer facet, which is the state that matters; the file itself cannot be."),

        new("audio.mixer.", true, "alsa.mixer",
            "One key per control per channel, which is the granularity these five resources own."),

        new("audio.wireplumber.playback-volume", false, null,
            "The capture never read WirePlumber's session state — PIPEWIRE is one of the five sections "
            + "the harness declares uncovered, and ~/.local/state/wireplumber is not in the reference "
            + "at all. So v1's own answer to 'how loud does WirePlumber hold the sink' does not exist "
            + "to diff against, which is itself the finding: this resource was added in 2026-08 after "
            + "the frame was measured setting the ALSA control correctly and losing it to a second "
            + "owner nobody had captured. Verified by its own wpctl Observe on every pass."),

        new("audio.modprobe.snd-usb-audio-index", false, "modprobe.d",
            "/etc/modprobe.d/alsa-base.conf, captured whole."),

        new("audio.xvf3800.gpo-x0d31-amp-enable", false, null,
            "The array's GPO pin values were never read by the capture — only its firmware version was. "
            + "Verified by the resource's own GPO_READ_VALUES Observe on every pass."),

        new("boot.autologin.getty-tty1", false, null,
            "The capture globbed /etc/systemd/system/*.service and never descended into the .d "
            + "directories, so v1's getty@tty1 autologin drop-in is not in the reference. The probe "
            + "mirrors the capture rather than reaching further, because a probe that read more than "
            + "the capture did would report every drop-in on the frame as an extra."),

        new("boot.cmdline.", true, "boot.cmdline",
            "One token of /boot/firmware/cmdline.txt each."),

        new("boot.config.", true, "boot.config",
            "One directive of /boot/firmware/config.txt each, carrying its [section]."),

        new("camera.pipewire-node.framelink-cam", false, null,
            "The PIPEWIRE block's Video section is empty and the block is truncated, so the node this "
            + "resource creates was not captured even on the frame that had it. Verified by the "
            + "resource's own Observe against the live graph."),

        new("cpu.governor.performance", false, "system.governor-zram-tmpfs",
            "The live governor value, first line of the block."),

        new("display.dsi2-transform", false, null,
            "The compositor's output transform lives in labwc's configuration and in the running "
            + "session, and the capture read neither. Verified by wlr-randr on a live session."),

        new("eeprom.config", false, "eeprom.config",
            "The bootloader EEPROM configuration, captured whole."),

        new("gpio.button.line", false, null,
            "Nothing about the GPIO line was captured — the button is a runtime claim on a chip line, "
            + "and version2.md §5.3 records the hardware as still unsourced. Verified by the resource "
            + "reporting its own claim, and by a press during the validation battery."),

        new("identity.hostname", false, "network",
            "The first line of the NETWORK block. Expected to differ, and carrying a ledger entry that "
            + "says why: the hostname is a per-device fleet setting."),

        new("journal.storage-persistent", false, "journald",
            "The journal's effective Storage and SystemMaxUse."),

        new("kiosk.binary.pinned-release", false, "kiosk.compose",
            "v1 pinned the release as a container image tag in the compose file. v2 fetches a checksum-"
            + "verified binary instead (decisions 40 and 41), so the versions are comparable and the "
            + "mechanism is not."),

        new("kiosk.config.", true, "kiosk.compose",
            "The KIOSK_* environment block of the compose file."),

        new("kiosk.listen-address", false, "kiosk.compose",
            "v1's `ports: 127.0.0.1:3000:3000` is the loopback restriction decision 56 records as "
            + "Docker's rather than Kiosk's, which is exactly what the v2 frame no longer has."),

        new("kiosk.offline-cache.dir", false, "kiosk.compose",
            "The compose file's volume line."),

        new("kiosk.process.supervised", false, "docker",
            "v1's supervision was `restart: always` and the container's Up status, both in the DOCKER "
            + "block. v2 supervises a child process instead, so the fact is comparable and the "
            + "mechanism is not."),

        new("labwc.", true, null,
            "The capture never read ~/.config/labwc, so neither the autostart file nor rc.xml is in the "
            + "reference. Verified by each resource's own content Observe."),

        new("mount.tmp.tmpfs", false, "system.governor-zram-tmpfs",
            "The /tmp mount line, with its size option."),

        new("pkg.", true, "packages",
            "One of the 929 packages, compared by the same code the Fleet Manager computes drift with."),

        new("portal.", true, null,
            "Both are live D-Bus state — an interface published on a session bus, and a permission row "
            + "in the portal's store. The capture read neither. Verified by their own Observe against a "
            + "running session."),

        new("session.bash-profile-exec-labwc", false, null,
            "HOME_TREE records that ~/.bash_profile exists and is 118 bytes; nothing captured its "
            + "content. Verified by the resource's own content Observe."),

        new("swap.", true, "system.governor-zram-tmpfs",
            "The swap lines of the block — and the block carries none, which is why the facet's "
            + "limitation says an observed zram device is an extra rather than a match."),

        new("system.locale", false, null,
            "The capture never read /etc/default/locale or /etc/default/keyboard. A locale is a fleet "
            + "setting with no catalog default, so a v1 value would not have been the desired value "
            + "anyway. Verified by its own Observe."),

        new("system.timezone", false, null,
            "The capture never read /etc/timezone or /etc/localtime. Same reasoning as the locale: a "
            + "per-room fleet setting with no catalog default."),

        new("firmware.xvf3800.recognised", false, null,
            "There is nothing to capture: it is a gate rather than a setting, so it has no state on "
            + "any frame, v1 or v2. What it reads — USB ids, serial, BLD_MSG, the two AEC_MIC_ARRAY "
            + "commands — describes the hardware plugged in rather than anything a build put there, "
            + "and the v1 capture recorded none of it. Verified by the resource's own Observe."),

        new("firmware.xvf3800.image", false, null,
            "The capture never looked for DFU images anywhere: v1 flashed the array by hand from a "
            + "git clone under ~/xvf3800 and kept nothing pinned, so there is no v1 state for three "
            + "digest-verified files under /var/lib/fl-agent to be compared against. Verified by the "
            + "resource's own Observe, which re-hashes all three against XvfFirmwarePin on every pass."),

        new("tool.xvf-host.installed", false, null,
            "HOME_TREE lists an ~/xvf3800 directory and no more — not the binary, not its mode, not the "
            + "three .so files, not their hashes. The directory listing is itself an uncovered facet. "
            + "Verified by the resource's own test -x and pinned SHA-256 set."),

        new("unit.chromium-kiosk.running-matches-content", false, null,
            "The running process's command line was never captured; the capture read the unit file and "
            + "not /proc. This resource exists precisely because those two can disagree, so the missing "
            + "half is the half that matters. Verified by its own Observe."),

        new("unit.chromium-kiosk.content", false, "units.user.files",
            "The unit file, captured whole."),

        new("unit.framelink-camera.content", false, "units.user.files",
            "The unit file, captured whole."),

        new("unit.cpu-performance.content", false, "units.system.files",
            "The unit file, captured whole."),

        new("unit.chromium-kiosk.enabled", false, "units.user.enabled",
            "The user unit's enablement state and vendor preset."),

        new("unit.framelink-camera.enabled", false, "units.user.enabled",
            "The user unit's enablement state and vendor preset."),

        new("unit.cpu-performance.enabled", false, "units.system.enabled",
            "The system unit's enablement state and vendor preset."),

        new("unit.xdg-desktop-portal.dropin-desktop", false, "units.user.dropins",
            "The drop-in, captured whole."),

        new("user.framelink.supplementary-groups", false, "users.groups",
            "Group membership by name, which is what this resource sets."),

        new("wireplumber.conf.camera-monitors-disabled", false, "wireplumber.conf",
            "The drop-in, captured whole."),
    ];

    /// <summary>Where the v1 evidence for one resource lives, or null when nothing matches.</summary>
    public static CatalogEvidence? Evidence(string resource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);

        foreach (var rule in Rules)
        {
            var hit = rule.Prefix
                ? resource.StartsWith(rule.Match, StringComparison.Ordinal)
                : string.Equals(resource, rule.Match, StringComparison.Ordinal);

            if (hit)
            {
                return new CatalogEvidence { Resource = resource, Facet = rule.Facet, Note = rule.Note };
            }
        }

        return null;
    }

    /// <summary>Every rule's facet id, so a test can prove none of them is a typo.</summary>
    public static IReadOnlyList<string> ReferencedFacets { get; } =
        [.. Rules.Where(rule => rule.Facet is not null).Select(rule => rule.Facet!).Distinct(StringComparer.Ordinal)];

    /// <summary>The evidence for every resource in the catalog, in document order.</summary>
    /// <exception cref="InvalidDataException">A resource matches no rule.</exception>
    public static IReadOnlyList<CatalogEvidence> For(IEnumerable<string> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);

        var evidence = new List<CatalogEvidence>();

        foreach (var resource in resources)
        {
            evidence.Add(Evidence(resource) ?? throw new InvalidDataException(
                $"No evidence rule covers the catalog resource '{resource}'. Record where its v1 state "
                + "was captured, or record that it was not — CatalogEvidenceMap is deliberately total."));
        }

        return evidence;
    }
}
