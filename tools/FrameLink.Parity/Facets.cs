namespace FrameLink.Parity;

/// <summary>
/// Every facet of device state this harness knows how to compare, and how it observes each one.
/// </summary>
/// <remarks>
/// <para>
/// <b>One entry per <c>== SECTION</c> of the frozen v1 inventory, and no entry without one.</b>
/// The inventory is the parity target, so the set of things worth comparing is decided by what
/// was captured rather than by what would be tidy. <see cref="ParityFacets"/> therefore covers
/// all twenty-nine sections, including the five it cannot compare at all — those are declared
/// <see cref="FacetKinds.Uncovered"/> with the reason, which is the difference between a stated
/// limit and a silent one.
/// </para>
/// <para>
/// <b>Every probe is read-only and, with one named exception, unprivileged.</b> CLAUDE.md §1.8
/// makes inspection the default and every class of mutation a separate ask; a parity check that
/// wrote anything would be measuring a frame it had just changed. Exactly one is marked
/// <see cref="ParityFacet.Elevated"/> and is skipped unless the operator asks for it: the array
/// answers <c>VERSION</c> only to a privileged USB control transfer. Everything else a frame can be
/// asked about here is world-readable, including the app's configuration, which is read back off
/// the local origin over loopback rather than out of the agent's root-owned state directory.
/// </para>
/// <para>
/// <b>Each probe is written to emit the same shape as the block it will be compared against</b>,
/// so <see cref="FacetParser"/> reads both sides with one parser. Where the v1 capture's exact
/// command is not recoverable from the file, the probe reproduces the output shape and the facet
/// says so in its limitation.
/// </para>
/// </remarks>
public static class ParityFacets
{
    /// <summary>Every facet, in the inventory's own order.</summary>
    public static IReadOnlyList<ParityFacet> All { get; } = Build();

    /// <summary>The facet with this id, or null.</summary>
    public static ParityFacet? Find(string id) =>
        All.FirstOrDefault(facet => string.Equals(facet.Id, id, StringComparison.Ordinal));

    private static IReadOnlyList<ParityFacet> Build() =>
    [
        new ParityFacet
        {
            Id = "identity",
            Section = "IDENTITY",
            Title = "Board model, distribution, kernel and architecture",
            Kind = FacetKinds.KeyValue,
            Probe =
                "tr -d '\\0' < /proc/device-tree/model; echo; . /etc/os-release; "
                + "printf '%s\\n' \"$PRETTY_NAME\"; uname -r; uname -m; dpkg --print-architecture",
            VersionKeys = ["kernel"],
        },
        new ParityFacet
        {
            Id = "boot.cmdline",
            Section = "KERNEL_CMDLINE",
            Title = "The single line of /boot/firmware/cmdline.txt, token by token",
            Kind = FacetKinds.TokenMultiset,
            Probe = "cat /boot/firmware/cmdline.txt",
        },
        new ParityFacet
        {
            Id = "boot.config",
            Section = "BOOT_CONFIG",
            Title = "/boot/firmware/config.txt directives, each carrying its [section]",
            Kind = FacetKinds.ConfigDirectives,
            Probe = "cat /boot/firmware/config.txt",
        },
        new ParityFacet
        {
            Id = "eeprom.config",
            Section = "EEPROM_CONFIG",
            Title = "Bootloader EEPROM configuration",
            Kind = FacetKinds.KeyValue,
            Probe = "rpi-eeprom-config",
        },
        new ParityFacet
        {
            Id = "packages",
            Section = "PACKAGES",
            Title = "Every installed dpkg package and its version",
            Kind = FacetKinds.Packages,
            Probe =
                "dpkg-query -W -f='${db:Status-Status} ${binary:Package} ${Version}\\n' "
                + "| awk '$1==\"installed\" {print $2\" \"$3}' | LC_ALL=C sort",
        },
        new ParityFacet
        {
            Id = "apt.sources",
            Section = "APT_SOURCES",
            Title = "Apt source files and the deb822 stanzas in them",
            Kind = FacetKinds.LineMultiset,
            Coverage = FacetCoverage.Partial,
            Limitation =
                "The capture lists every file in /etc/apt/sources.list.d/ and then the contents of "
                + "the .sources files only — v1's docker.list is named and its body is absent, which "
                + "is how a one-line legacy source behaves under `cat *.sources`. The probe "
                + "reproduces that exactly, so a legacy .list file is compared by its name and not "
                + "by its content.",
            Probe = "LC_ALL=C ls -1 /etc/apt/sources.list.d/; cat /etc/apt/sources.list.d/*.sources 2>/dev/null; true",
        },
        new ParityFacet
        {
            Id = "units.system.enabled",
            Section = "SYSTEM_UNITS_ENABLED",
            Title = "System units enabled at boot, with their vendor preset",
            Kind = FacetKinds.KeyValue,
            Probe = "systemctl list-unit-files --state=enabled --no-legend --no-pager | LC_ALL=C sort",
        },
        new ParityFacet
        {
            Id = "units.user.enabled",
            Section = "USER_UNITS_ENABLED",
            Title = "The login user's enabled units, with their vendor preset",
            Kind = FacetKinds.KeyValue,
            Probe =
                "XDG_RUNTIME_DIR=/run/user/$(id -u) systemctl --user list-unit-files --state=enabled "
                + "--no-legend --no-pager | LC_ALL=C sort",
        },
        new ParityFacet
        {
            Id = "units.user.files",
            Section = "USER_UNIT_FILES",
            Title = "The content of every unit file in the login user's systemd directory",
            Kind = FacetKinds.FileSet,
            Probe =
                "for f in \"$HOME\"/.config/systemd/user/*.service \"$HOME\"/.config/systemd/user/*.timer; "
                + "do [ -f \"$f\" ] || continue; echo \"##### $f\"; cat \"$f\"; done",
        },
        new ParityFacet
        {
            Id = "units.user.dropins",
            Section = "USER_UNIT_DROPINS",
            Title = "Drop-ins under the login user's systemd directory",
            Kind = FacetKinds.FileSet,
            Probe =
                "for f in \"$HOME\"/.config/systemd/user/*.d/*.conf; do [ -f \"$f\" ] || continue; "
                + "echo \"##### $f\"; cat \"$f\"; done",
        },
        new ParityFacet
        {
            Id = "units.system.files",
            Section = "SYSTEM_UNIT_FILES",
            Title = "The content of every unit file written into /etc/systemd/system",
            Kind = FacetKinds.FileSet,
            Probe =
                "for f in /etc/systemd/system/*.service /etc/systemd/system/*.timer; "
                + "do [ -f \"$f\" ] || continue; echo \"##### $f\"; cat \"$f\"; done",
        },
        new ParityFacet
        {
            Id = "system.dropins",
            Section = "SYSTEM_DROPINS",
            Title = "Presence of the two /etc/systemd configuration files the capture read",
            Kind = FacetKinds.FileSet,
            Coverage = FacetCoverage.Partial,
            Limitation =
                "Presence only, never content. The captured block cuts /etc/systemd/sleep.conf off "
                + "mid-file — it ends on 'Entries in this file show the compile time defaults. Local "
                + "configuration' with the rest of the stock file missing — so byte equality against "
                + "it would report a difference the capture invented. journald's effective values are "
                + "compared in full by the `journald` facet, which is where they matter.",
            Probe =
                "for f in /etc/systemd/journald.conf /etc/systemd/sleep.conf; do [ -f \"$f\" ] || continue; "
                + "echo \"##### $f\"; cat \"$f\"; done",
        },
        new ParityFacet
        {
            Id = "modprobe.d",
            Section = "MODPROBE_D",
            Title = "Every /etc/modprobe.d file and its content",
            Kind = FacetKinds.FileSet,
            Probe =
                "for f in /etc/modprobe.d/*.conf; do [ -f \"$f\" ] || continue; echo \"##### $f\"; "
                + "cat \"$f\"; done",
        },
        new ParityFacet
        {
            Id = "alsa.mixer",
            Section = "ALSA_MIXER",
            Title = "Every simple mixer control on card 0, channel by channel",
            Kind = FacetKinds.AlsaMixer,
            Probe = "amixer -c 0 scontents",
        },
        new ParityFacet
        {
            Id = "alsa.cards",
            Section = "ALSA_CARDS",
            Title = "The ALSA card list",
            Kind = FacetKinds.LineMultiset,
            TruncateAt = "--- state file ---",
            Coverage = FacetCoverage.Partial,
            Limitation =
                "The card list only. Everything below the capture's '--- state file ---' marker is an "
                + "alsactl dump the capture cut off mid-control, so comparing it would report every "
                + "control below the cut as missing. The mixer values that dump would have carried are "
                + "compared in full by the `alsa.mixer` facet.",
            Probe = "cat /proc/asound/cards",
        },
        new ParityFacet
        {
            Id = "audio.xvf3800.firmware",
            Section = "XVF3800_FIRMWARE",
            Title = "The firmware version the microphone array reports over USB",
            Kind = FacetKinds.KeyValue,
            Elevated = true,
            Coverage = FacetCoverage.Partial,
            Limitation =
                "Needs root: xvf_host issues a privileged USB control transfer. The collector is "
                + "unprivileged by default, so this facet reports not-collected unless "
                + "`fl.py parity --elevate` is given. The probe looks in the agent's own "
                + "/var/lib/fl-agent/xvf3800 first and v1's ~/xvf3800/host_control/rpi_64bit second. "
                + "No catalog resource maps to this facet any more: decision 90 took the firmware "
                + "version out of the resource graph, so what the v1 frame was running is parity "
                + "evidence for a person to read rather than a value any frame will act on. The "
                + "comparison is kept because a frame that quietly changed firmware is worth "
                + "noticing; the unprivileged half of the same reading, /sys/.../bcdDevice, is what "
                + "the agent itself reports and needs no elevation at all.",
            Probe =
                "for d in /var/lib/fl-agent/xvf3800 \"$HOME\"/xvf3800/host_control/rpi_64bit; "
                + "do [ -x \"$d/xvf_host\" ] || continue; (cd \"$d\" && ./xvf_host VERSION) && break; done",
        },
        new ParityFacet
        {
            Id = "pipewire.graph",
            Section = "PIPEWIRE",
            Title = "The live PipeWire object graph",
            Kind = FacetKinds.Uncovered,
            Coverage = FacetCoverage.None,
            Limitation =
                "Nothing in the block survives a reboot: every line carries a per-session object id, "
                + "a pid and a cookie, and the client list is whatever happened to be connected in the "
                + "second the capture ran — including the capture's own wpctl. The block is also "
                + "truncated, ending inside the Video section. What it was evidence for — the array "
                + "present as one ALSA device with a sink and a source — is asserted by the "
                + "`alsa.cards` and `alsa.mixer` facets and by the agent's own camera and audio "
                + "resources on every pass.",
        },
        new ParityFacet
        {
            Id = "wireplumber.conf",
            Section = "WIREPLUMBER_CONF",
            Title = "WirePlumber configuration drop-ins in the login user's home",
            Kind = FacetKinds.FileSet,
            Probe =
                "for f in \"$HOME\"/.config/wireplumber/wireplumber.conf.d/*.conf; do [ -f \"$f\" ] || "
                + "continue; echo \"##### $f\"; cat \"$f\"; done",
        },
        new ParityFacet
        {
            Id = "camera.probe",
            Section = "CAMERA",
            Title = "The camera probe the capture ran",
            Kind = FacetKinds.Uncovered,
            Coverage = FacetCoverage.None,
            Limitation =
                "The captured block is the error message 'bash: line 1: libcamera-hello: command not "
                + "found'. It records that the tool was absent from the v1 frame, not any camera "
                + "state, so there is no v1 value to be at parity with. The camera is covered instead "
                + "by the catalog's camera.pipewire-node.framelink-cam and the guide-6 resources, "
                + "which are checkpoint assertions rather than a state diff.",
        },
        new ParityFacet
        {
            Id = "docker",
            Section = "DOCKER",
            Title = "Docker, its running containers and its daemon configuration",
            Kind = FacetKinds.LineMultiset,
            Probe =
                "command -v docker >/dev/null 2>&1 && docker version --format "
                + "'Docker version {{.Server.Version}}, build {{.Server.GitCommit}}' 2>/dev/null; "
                + "docker ps --format '{{.Names}} | {{.Image}} | {{.Status}}' 2>/dev/null; "
                + "cat /etc/docker/daemon.json 2>/dev/null; true",
        },
        new ParityFacet
        {
            Id = "kiosk.compose",
            Section = "IMMICH_KIOSK_COMPOSE",
            Title = "The Immich Kiosk compose file",
            Kind = FacetKinds.LineMultiset,
            Probe = "cat \"$HOME\"/immich-kiosk/docker-compose.yml 2>/dev/null; true",
        },
        new ParityFacet
        {
            Id = "app.config",
            Section = "APP_CONFIG",
            Title = "The product app's configuration, as the local origin serves it",
            Kind = FacetKinds.Json,
            Probe =
                "curl -sS --max-time 5 http://127.0.0.1:8888/config.json | sed -E "
                + "'s/(\"(token|apiKey|api_key|secret|password)\"[[:space:]]*:[[:space:]]*\")[^\"]*\"/"
                + "\\1<REDACTED>\"/g'",
        },
        new ParityFacet
        {
            Id = "app.git",
            Section = "APP_GIT",
            Title = "The commit of the app repository cloned onto the v1 frame",
            Kind = FacetKinds.Uncovered,
            Coverage = FacetCoverage.None,
            Limitation =
                "There is nothing on a v2 frame this could be compared against. Decision 39 has the "
                + "agent embed and serve the product app out of its own binary, so no clone exists and "
                + "no commit is checked out. What the app *is* on a v2 frame is decided by the agent "
                + "version, which the agent.version resource reconciles and reports.",
        },
        new ParityFacet
        {
            Id = "system.governor-zram-tmpfs",
            Section = "GOVERNOR_ZRAM_TMPFS",
            Title = "CPU governor, the /tmp and / mounts, and any swap device",
            Kind = FacetKinds.LineMultiset,
            Coverage = FacetCoverage.Partial,
            Limitation =
                "The captured block carries a governor line and two mount lines and no swap line at "
                + "all, so it cannot be told from the file whether the capture asked about swap and "
                + "found none, or never asked. The probe asks, which means a zram device — which "
                + "swap.zram-active is supposed to produce on a v2 frame — appears as an extra line "
                + "and is explained by a ledger entry rather than assumed.",
            Probe =
                "cat /sys/devices/system/cpu/cpufreq/policy0/scaling_governor; "
                + "findmnt -n -o TARGET,SOURCE,FSTYPE,OPTIONS /tmp /; "
                + "swapon --show=NAME,TYPE,SIZE --noheadings; true",
        },
        new ParityFacet
        {
            Id = "journald",
            Section = "JOURNALD",
            Title = "The journal's effective storage and size cap",
            Kind = FacetKinds.KeyValue,
            IgnoredKeys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["[Journal]"] =
                    "The section header of an ini file, not a setting. It carries no value to compare.",
                ["File path"] =
                    "The path of whichever journal file was open when the capture ran. It contains the "
                    + "machine id and a per-file sequence, both of which change on every reflash and "
                    + "neither of which any resource sets. That a journal file exists at all is "
                    + "asserted by journal.storage-persistent's own Observe.",
            },
            Probe =
                "systemd-analyze cat-config systemd/journald.conf | grep -E '^(Storage|SystemMaxUse)=' "
                + "| LC_ALL=C sort || true",
        },
        new ParityFacet
        {
            Id = "users.groups",
            Section = "USERS_GROUPS",
            Title = "The login user's supplementary groups, by name",
            Kind = FacetKinds.UsersGroups,
            IgnoredKeys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["<numeric ids>"] =
                    "Group and user ids are allocation order on the image that created them, not "
                    + "policy — docker's 985 exists because docker was installed at that moment. What "
                    + "the catalog's user.framelink.supplementary-groups sets is membership by name, "
                    + "so names are compared and numbers are dropped.",
            },
            Probe =
                "id; getent group | LC_ALL=C awk -F: -v u=\"$(id -un)\" "
                + "'$1==u || $4 ~ (\"(^|,)\" u \"(,|$)\") {print $1\":x:\"$3\":\"$4}'",
        },
        new ParityFacet
        {
            Id = "network",
            Section = "NETWORK",
            Title = "The frame's hostname and its network interfaces",
            Kind = FacetKinds.Network,
            Coverage = FacetCoverage.Partial,
            IgnoredKeys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["<addresses>"] =
                    "IPv4 and IPv6 addresses are handed out by the household router and differ "
                    + "between two frames that are both correct, so they cannot be a parity criterion. "
                    + "CLAUDE.md §2.3 also requires them garbled in anything committed.",
            },
            Limitation =
                "Interface names and link states only. The hostname is compared and is expected to "
                + "differ — it is a per-device fleet setting (identity.hostname), which is why it has "
                + "a ledger entry rather than being ignored.",
            Probe = "hostname; ip -br addr",
        },
        new ParityFacet
        {
            Id = "files.hashes",
            Section = "KEY_FILE_HASHES",
            Title = "SHA-256 of the seven files the capture singled out",
            Kind = FacetKinds.Uncovered,
            Coverage = FacetCoverage.None,
            Limitation =
                "Every path in this block has its content compared byte for byte by another facet — "
                + "config.txt by `boot.config`, cmdline.txt by `boot.cmdline`, alsa-base.conf by "
                + "`modprobe.d`, the four user units by `units.user.files`. A hash comparison would "
                + "restate those same differences as opaque hex and could never say which line moved. "
                + "A test asserts every path here is carried by one of those facets, so a file added "
                + "to this block cannot slip through uncompared.",
        },
        new ParityFacet
        {
            Id = "home.tree",
            Section = "HOME_TREE",
            Title = "A directory listing of the login user's home",
            Kind = FacetKinds.Uncovered,
            Coverage = FacetCoverage.None,
            Limitation =
                "Bench debris rather than provisioned state: six screenshots, four ad-hoc shell "
                + "scripts, a firmware blob, a backup of an expired token, a bash history and the "
                + "caches of everything that ran. No catalog resource creates any of it and v2 "
                + "provisions none of it, so an empty home on a v2 frame is correct rather than a "
                + "gap. The three paths under it that *are* provisioned — the systemd user "
                + "directory, the wireplumber drop-in directory and the kiosk directory — have "
                + "facets of their own.",
        },
    ];
}
