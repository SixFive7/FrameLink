using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Agent.Resources;

/// <summary>
/// Reading <c>wpctl status</c> — the one command that says what PipeWire is actually offering.
/// </summary>
/// <remarks>
/// <para>
/// Two camera resources consult it and they ask different questions of the same text, which is
/// why the parsing is here rather than inside either of them:
/// <see cref="WirePlumberCameraMonitorsResource"/> asks whether anything <i>extra</i> is being
/// surfaced, and <see cref="CameraNodeResource"/> asks whether <c>FrameLinkCam</c> is there.
/// </para>
/// <para>
/// <b>The format is transcribed from a real capture</b>, not from documentation: the
/// <c>PIPEWIRE</c> section of <c>reference/v1-state-inventory.txt</c> holds the frame's own
/// <c>wpctl status</c> output, box-drawing characters and all. Sections start at column zero
/// (<c>Audio</c>, <c>Video</c>, <c>Settings</c>), subsections are <c>├─ Sources:</c> tree
/// branches, and each entry is <c>│  *   54. Name    [vol: 1.00]</c> — an id, a name that may
/// contain spaces, and an optional trailing bracket. The parser strips the tree characters rather
/// than counting columns, so it survives the indentation changing between WirePlumber releases.
/// </para>
/// </remarks>
public static class WpctlStatus
{
    /// <summary>The section holding cameras.</summary>
    public const string Video = "Video";

    /// <summary>The subsection holding things that produce pictures.</summary>
    public const string Sources = "Sources";

    /// <summary>The subsection holding camera hardware WirePlumber found by itself.</summary>
    public const string Devices = "Devices";

    private static readonly char[] Tree = [' ', '\t', '│', '├', '└', '─', '*'];

    /// <summary>
    /// The entries under one subsection of one section, in the order <c>wpctl</c> printed them.
    /// </summary>
    /// <param name="status">Whole <c>wpctl status</c> output.</param>
    /// <param name="section">Top-level section, for example <see cref="Video"/>.</param>
    /// <param name="subsection">Branch name without its colon, for example <see cref="Sources"/>.</param>
    public static IReadOnlyList<string> Entries(string status, string section, string subsection)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(subsection);

        var entries = new List<string>();
        var inSection = false;
        var inSubsection = false;

        foreach (var raw in status.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.Length > 0 && !char.IsWhiteSpace(raw[0]))
            {
                // Column zero is a section heading — or the `PipeWire 'pipewire-0' [...]` banner,
                // which matches nothing and therefore closes whatever was open. Either way the
                // subsection state has to reset, or `Audio`'s Sources would leak into `Video`'s.
                inSection = string.Equals(raw.Trim(), section, StringComparison.Ordinal);
                inSubsection = false;
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            var content = raw.Trim(Tree);

            if (content.EndsWith(':'))
            {
                inSubsection = string.Equals(content[..^1], subsection, StringComparison.Ordinal);
                continue;
            }

            if (inSubsection && NameOf(content) is { } name)
            {
                entries.Add(name);
            }
        }

        return entries;
    }

    /// <summary>The name in <c>54. FrameLinkCam    [vol: 1.00]</c>, or null if this is not an entry.</summary>
    public static string? NameOf(string entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var digits = 0;
        while (digits < entry.Length && char.IsAsciiDigit(entry[digits]))
        {
            digits++;
        }

        if (digits == 0 || digits >= entry.Length || entry[digits] != '.')
        {
            return null;
        }

        var name = entry[(digits + 1)..].Trim();

        // The trailing `[vol: 1.00]` or `[alsa]` is a property of the entry, not part of its name.
        // Cut from the last bracket rather than the first: nothing in this build has a bracket in
        // its name, but a device that did would keep it instead of being silently truncated.
        if (name.EndsWith(']') && name.LastIndexOf('[') is > 0 and var bracket)
        {
            name = name[..bracket].Trim();
        }

        return name.Length == 0 ? null : name;
    }
}

/// <summary>
/// <c>unit.xdg-desktop-portal.dropin-desktop</c> — the portal is told which desktop it is under.
/// </summary>
/// <remarks>
/// <para>
/// From guide 6 step 2. Raspberry Pi OS ships <c>/usr/share/xdg-desktop-portal/labwc-portals.conf</c>
/// and the portal only selects it when <c>XDG_CURRENT_DESKTOP</c> contains <c>labwc</c>. The kiosk
/// starts the compositor with a bare <c>exec labwc</c> (guide 5 step 4), which exports nothing —
/// so without this drop-in the portal falls back to a degraded interface set <b>with no Camera
/// interface at all</b>, and the fault presents as a permanently black self-view rather than as an
/// error.
/// </para>
/// <para>
/// <b>A drop-in rather than a session-wide export, deliberately.</b> The portal is D-Bus-activated,
/// so on a cold boot the first thing that starts it is Chromium's own camera request — long after
/// any shell profile has run. Only a unit-level <c>Environment=</c> covers that path.
/// </para>
/// <para>
/// <b>Observe reads systemd as well as the file, and they can disagree.</b> <c>systemctl --user
/// show -p Environment</c> reports the unit's configuration whether or not the portal is currently
/// running, which is what makes this observable on a frame that has never made a call. A correct
/// file that systemd has not read yet is a real state — it is what a write without a
/// <c>daemon-reload</c> leaves behind — and the file half alone would call it healthy.
/// </para>
/// </remarks>
public sealed class PortalDesktopDropInResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "unit.xdg-desktop-portal.dropin-desktop";

    /// <summary>The portal's user unit.</summary>
    public const string UnitName = "xdg-desktop-portal.service";

    /// <summary>The desktop name the portal matches its configuration on.</summary>
    public const string Desktop = "labwc";

    private readonly ISystemFiles _files;
    private readonly IUserSession _session;

    /// <summary>Creates the resource.</summary>
    public PortalDesktopDropInResource(ISystemFiles files, IUserSession session)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(session);

        _files = files;
        _session = session;
    }

    /// <summary>The drop-in text, verbatim from guide 6 step 2 and from the v1 reference.</summary>
    public static string DesiredContent =>
        "[Service]\n"
        + "Environment=XDG_CURRENT_DESKTOP=" + Desktop + "\n";

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn =>
        [PackageResource.Prefix + "xdg-desktop-portal", ConsoleAutologinResource.ResourceName];

    /// <inheritdoc/>
    public string Detected => "The part that hands the camera to the browser has not been told what this frame is running.";

    /// <inheritdoc/>
    public string WhyItMatters => "Until it is, the browser is never offered a camera and a call shows a black square.";

    /// <summary>Where the drop-in lives for this frame's user.</summary>
    public string Path => _session.HomeDirectory + "/.config/systemd/user/" + UnitName + ".d/desktop.conf";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var path = Path;
        var actual = _files.ReadText(path);
        var expected = $"{path} setting XDG_CURRENT_DESKTOP={Desktop}, and systemd agreeing";
        var wrong = new List<string>(2);

        if (!string.Equals(
                JournalStorageResource.ShortHash(actual ?? string.Empty),
                JournalStorageResource.ShortHash(DesiredContent),
                StringComparison.Ordinal))
        {
            wrong.Add(actual is null ? $"{path} absent" : $"{path} {JournalStorageResource.ShortHash(actual)}");
        }

        var shown = await _session
            .RunAsync("systemctl", ["--user", "show", UnitName, "-p", "Environment"], cancellationToken)
            .ConfigureAwait(false);

        if (!shown.StandardOutput.Contains("XDG_CURRENT_DESKTOP=" + Desktop, StringComparison.Ordinal))
        {
            wrong.Add(shown.StandardOutput.Trim() is { Length: > 0 } line
                ? $"systemd reports {line.Split('\n')[0].Trim()}"
                : "systemd reports no environment for the portal");
        }

        return new ResourceObservation(
            wrong.Count == 0,
            expected,
            wrong.Count == 0 ? expected : string.Join("; ", wrong));
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var path = Path;
        _files.WriteText(path, DesiredContent);
        await _session.GiveToUserAsync(path, cancellationToken).ConfigureAwait(false);

        await _session.RunAsync("systemctl", ["--user", "daemon-reload"], cancellationToken).ConfigureAwait(false);

        // Restarted as well as reloaded, because a portal that is already running keeps the
        // environment it started with — and on a frame that has made one call since boot, it is
        // running. The reboot that follows would fix it anyway; doing it here means the interface
        // is there for the resource that checks for it in this same pass.
        var restarted = await _session
            .RunAsync("systemctl", ["--user", "restart", UnitName], cancellationToken)
            .ConfigureAwait(false);

        return new ResourceAction(
            $"write {path} (Environment=XDG_CURRENT_DESKTOP={Desktop}), then systemctl --user daemon-reload and restart {UnitName}"
                + (restarted.Succeeded ? string.Empty : $" (refused: {restarted.Combined})"),
            "Telling the part that hands out the camera which screen program this frame runs, so it starts offering the camera at all.");
    }
}

/// <summary>
/// <c>portal.permission-store.camera</c> — the answer to "Allow the camera?" is already on disk.
/// </summary>
/// <remarks>
/// <para>
/// From guide 6 step 3. The portal keeps device permissions in the flatpak permission store
/// (<c>~/.local/share/flatpak/db/devices</c>) and treats the three states differently: <c>yes</c>
/// grants silently, <c>no</c> denies silently, and <b>unset</b> pops a GTK "Allow?" dialog and
/// waits. On a wall-mounted frame with no keyboard nobody ever clicks it, so the fault presents as
/// a permanently black self-view rather than as an error — which is why this is pre-authorised
/// rather than left to first use.
/// </para>
/// <para>
/// The empty application id is correct for an unsandboxed host Chromium: that is the identifier
/// the portal uses for an application it did not start inside a sandbox.
/// </para>
/// <para>
/// <b>It survives the profile wipe.</b> <c>unit.chromium-kiosk.content</c> deletes
/// <c>/tmp/framelink-chromium</c> before every start, so anything stored inside the browser
/// profile would have to be granted again on every boot. This lives outside it.
/// </para>
/// <para>
/// <b>Observing may start the permission store, and that proves nothing.</b> The store is
/// D-Bus-activated, so the <c>Lookup</c> call can bring it up as a side effect. That is an
/// acceptable read — it is the same call the guide uses to confirm the write — but "the service
/// started" is never evidence about the permission; only the returned value is.
/// </para>
/// </remarks>
public sealed class PortalCameraPermissionResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "portal.permission-store.camera";

    /// <summary>The permission store's bus name.</summary>
    public const string BusName = "org.freedesktop.impl.portal.PermissionStore";

    /// <summary>Its object path.</summary>
    public const string ObjectPath = "/org/freedesktop/impl/portal/PermissionStore";

    /// <summary>The table device permissions live in.</summary>
    public const string Table = "devices";

    /// <summary>The permission id.</summary>
    public const string Id = "camera";

    /// <summary>The stored value, as the store prints it back.</summary>
    public const string Granted = "\"\" 1 \"yes\"";

    private readonly IUserSession _session;

    /// <summary>Creates the resource.</summary>
    public PortalCameraPermissionResource(IUserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn =>
        [PackageResource.Prefix + "xdg-desktop-portal-gtk", ConsoleAutologinResource.ResourceName];

    /// <inheritdoc/>
    public string Detected => "Nobody has told this frame it may use its own camera.";

    /// <inheritdoc/>
    public string WhyItMatters => "It waits for someone to tap 'Allow' on a screen nobody is standing at, and the picture stays black.";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var result = await _session
            .RunAsync("busctl", ["--user", "call", BusName, ObjectPath, BusName, "Lookup", "ss", Table, Id], cancellationToken)
            .ConfigureAwait(false);

        var answer = Condense(result.Combined);

        return new ResourceObservation(
            result.Succeeded && answer.Contains(Granted, StringComparison.Ordinal),
            $"the permission store answering {Granted} for {Id}",
            answer.Length == 0 ? "the permission store said nothing at all" : answer);
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        // `sbssas` is the signature: string table, bool create-if-missing, string id, string
        // application id, and an array of strings holding the one permission. The empty argument
        // between `camera` and `1` is the application id and is load-bearing — it is not an
        // omission that could be tidied away.
        var result = await _session
            .RunAsync(
                "busctl",
                ["--user", "call", BusName, ObjectPath, BusName, "SetPermission", "sbssas", Table, "true", Id, string.Empty, "1", "yes"],
                cancellationToken)
            .ConfigureAwait(false);

        return new ResourceAction(
            $"busctl --user call {BusName} {ObjectPath} {BusName} SetPermission sbssas {Table} true {Id} \"\" 1 yes"
                + (result.Succeeded ? string.Empty : $" (refused: {Condense(result.Combined)})"),
            "Recording a permanent yes for this frame's own camera, so nothing ever waits for somebody to tap 'Allow'.");
    }

    private static string Condense(string output) =>
        string.Join(' ', output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

/// <summary>
/// <c>wireplumber.conf.camera-monitors-disabled</c> — WirePlumber stops finding cameras by itself.
/// </summary>
/// <remarks>
/// <para>
/// From guide 6 step 4. One file, two keys, and the catalog keeps them as one resource because a
/// single write sets both and a content compare already names which one drifted. The two failure
/// signatures are worth recording separately even so, because they are different faults:
/// </para>
/// <list type="bullet">
/// <item><c>monitor.libcamera</c> left on gives you the stock <c>libspa-0.2-libcamera</c> node —
/// measured at a hard ~30 fps cap, advertising no framerates, rejecting sizes outside its own menu,
/// and unacquirable by Chromium above 720p.</item>
/// <item><c>monitor.v4l2</c> left on surfaces the Pi's raw CFE and ISP pipeline stages as if they
/// were cameras, and <b>Chromium hangs while probing them</b>.</item>
/// </list>
/// <para>
/// The <c>99-</c> prefix is load-bearing: fragments are read in name order and the last one wins.
/// The ALSA monitor is deliberately unmentioned, so audio is untouched by this file.
/// </para>
/// <para>
/// <b>The second half of Observe is the interesting one.</b> The catalog asks for the file hash
/// <i>and</i> <c>wpctl status</c> showing nothing camera-like besides <c>FrameLinkCam</c>, because
/// a byte-perfect fragment that WirePlumber never loaded is a real state and the hash alone calls
/// it healthy. An empty Video section satisfies this resource — guide 6 step 4 is explicit that
/// after this step there is deliberately no camera at all, and the node is
/// <see cref="CameraNodeResource"/>'s business.
/// </para>
/// <para>
/// Watch for schema drift across WirePlumber majors: the <c>wireplumber.profiles</c> form is 0.5.x,
/// and the frame runs 0.5.8.
/// </para>
/// </remarks>
public sealed class WirePlumberCameraMonitorsResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "wireplumber.conf.camera-monitors-disabled";

    /// <summary>The service the fragment is read by.</summary>
    public const string UnitName = "wireplumber.service";

    private readonly ISystemFiles _files;
    private readonly IUserSession _session;

    /// <summary>Creates the resource.</summary>
    public WirePlumberCameraMonitorsResource(ISystemFiles files, IUserSession session)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(session);

        _files = files;
        _session = session;
    }

    /// <summary>The fragment, verbatim from guide 6 step 4 and from the v1 reference.</summary>
    public static string DesiredContent =>
        "# FrameLink camera routing.\n"
        + "# Disable WirePlumber's stock camera monitors so the only camera Chromium can see is the\n"
        + "# framelink-camera PipeWire node (deploy/systemd/framelink-camera.service):\n"
        + "#  - monitor.libcamera: the stock libspa-libcamera node is hard-limited to ~30 fps, rejects\n"
        + "#    non-menu sizes, and Chromium fails to acquire it above 720p (measured).\n"
        + "#  - monitor.v4l2: without it the raw CFE/ISP V4L2 nodes would surface as bogus cameras.\n"
        + "# Audio is untouched (that is the ALSA monitor).\n"
        + "# Install to: ~/.config/wireplumber/wireplumber.conf.d/99-framelink-camera.conf\n"
        + "wireplumber.profiles = {\n"
        + "  main = {\n"
        + "    monitor.libcamera = disabled\n"
        + "    monitor.v4l2 = disabled\n"
        + "  }\n"
        + "}\n";

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn =>
        [PackageResource.Prefix + "wireplumber", ConsoleAutologinResource.ResourceName];

    /// <inheritdoc/>
    public string Detected => "This frame is still hunting for cameras on its own.";

    /// <inheritdoc/>
    public string WhyItMatters => "It finds parts that are not really cameras, and the browser freezes the moment it tries one.";

    /// <summary>Where the fragment lives for this frame's user.</summary>
    public string Path => _session.HomeDirectory + "/.config/wireplumber/wireplumber.conf.d/99-framelink-camera.conf";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var path = Path;
        var actual = _files.ReadText(path);
        var expected = $"{path} {JournalStorageResource.ShortHash(DesiredContent)}, and no camera but {CameraUnitResource.NodeDescription}";
        var wrong = new List<string>(2);

        if (!string.Equals(
                JournalStorageResource.ShortHash(actual ?? string.Empty),
                JournalStorageResource.ShortHash(DesiredContent),
                StringComparison.Ordinal))
        {
            wrong.Add(actual is null ? $"{path} absent" : $"{path} {JournalStorageResource.ShortHash(actual)}");
        }

        var status = await _session.RunAsync("wpctl", ["status"], cancellationToken).ConfigureAwait(false);

        if (status.Succeeded)
        {
            var strangers = WpctlStatus.Entries(status.StandardOutput, WpctlStatus.Video, WpctlStatus.Sources)
                .Where(source => !string.Equals(source, CameraUnitResource.NodeDescription, StringComparison.Ordinal))
                .Concat(WpctlStatus.Entries(status.StandardOutput, WpctlStatus.Video, WpctlStatus.Devices))
                .ToList();

            if (strangers.Count > 0)
            {
                wrong.Add($"PipeWire is also offering {string.Join(", ", strangers)}");
            }
        }

        return new ResourceObservation(
            wrong.Count == 0,
            expected,
            wrong.Count == 0 ? expected : string.Join("; ", wrong));
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var path = Path;
        _files.WriteText(path, DesiredContent);
        await _session.GiveToUserAsync(path, cancellationToken).ConfigureAwait(false);

        var restarted = await _session
            .RunAsync("systemctl", ["--user", "restart", UnitName], cancellationToken)
            .ConfigureAwait(false);

        return new ResourceAction(
            $"write {path} (monitor.libcamera and monitor.v4l2 disabled) and restart {UnitName}"
                + (restarted.Succeeded ? string.Empty : $" (refused: {restarted.Combined})"),
            "Telling this frame to stop looking for cameras by itself, so the only camera it offers is its own.");
    }
}

/// <summary>
/// <c>unit.framelink-camera.content</c> — the user unit that publishes the camera into PipeWire.
/// </summary>
/// <remarks>
/// <para>
/// From guide 6 step 5. <b>Every element of the pipeline is measured rather than chosen</b>, and
/// the whole line is carried across byte for byte — this text hashes to
/// <c>a2c9ef326c8d53a7bf17086e786876b447a3c385e088948a19ca23c5b1e75e3e</c>, the v1 reference hash
/// the catalog records, which is what <c>AgentCameraTests</c> asserts.
/// </para>
/// <list type="bullet">
/// <item><c>width=1920,height=1080</c> forces the IMX708's <b>full-field-of-view 2304×1296 sensor
/// mode</b>, which the ISP then scales in hardware. Asking for 900 lines or fewer selects a cropped
/// mode that behaves like a ~1.5× zoom, and scaling in software was measured at a ~51 fps
/// single-thread ceiling on this CPU.</item>
/// <item><c>queue max-size-buffers=4 leaky=downstream</c> drops the oldest frames when the consumer
/// stalls rather than back-pressuring the sensor, so the feed stays live and the pipeline never
/// wedges on a slow reader.</item>
/// <item><c>pipewiresink mode=provide sync=false</c> publishes a standalone node and forwards
/// frames as the sensor delivers them; the <c>stream-properties</c> are what make PipeWire and the
/// portal treat it as a camera rather than as an anonymous video stream.</item>
/// </list>
/// <para>
/// <b>What this unit cannot do is notice its own death.</b> <c>Restart=always</c> covers a crash
/// and nothing else: <c>gstpipewiresink</c> in PipeWire 1.4.x raises a fatal element error when a
/// consumer tears down abruptly and can then hang in shutdown, leaving the unit <c>active</c> with
/// a dead stream — and a restart policy cannot fire on a hung process. That is why
/// <see cref="CameraNodeResource"/> exists as a separate assertion and why §2.10 recycles the node
/// after every call.
/// </para>
/// </remarks>
public sealed class CameraUnitResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "unit.framelink-camera.content";

    /// <summary>The unit's name, as the user manager knows it.</summary>
    public const string UnitName = "framelink-camera.service";

    /// <summary>The node name PipeWire clients match on.</summary>
    public const string NodeName = "framelink-cam";

    /// <summary>The human-readable name Chromium shows, and the one <c>wpctl</c> lists.</summary>
    public const string NodeDescription = "FrameLinkCam";

    private readonly ISystemFiles _files;
    private readonly IUserSession _session;

    /// <summary>Creates the resource.</summary>
    public CameraUnitResource(ISystemFiles files, IUserSession session)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(session);

        _files = files;
        _session = session;
    }

    /// <summary>The unit text this resource converges on.</summary>
    public static string DesiredContent() =>
        "[Unit]\n"
        + "Description=FrameLink camera node (Pi Camera -> PipeWire, full-FoV 1080p30)\n"
        + "After=pipewire.service\n"
        + "\n"
        + "[Service]\n"
        + "Type=simple\n"
        + "ExecStart=/usr/bin/gst-launch-1.0 libcamerasrc ! video/x-raw,format=NV12,width=1920,height=1080,framerate=30/1 ! "
        + "queue max-size-buffers=4 leaky=downstream ! pipewiresink sync=false mode=provide "
        + "stream-properties=props,media.class=Video/Source,media.role=Camera,node.name=" + NodeName
        + ",node.description=" + NodeDescription + "\n"
        + "Restart=always\n"
        + "RestartSec=3\n"
        + "\n"
        + "[Install]\n"
        + "WantedBy=default.target\n";

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn =>
    [
        PackageResource.Prefix + "gstreamer1.0-tools",
        PackageResource.Prefix + "gstreamer1.0-plugins-base",
        PackageResource.Prefix + "gstreamer1.0-libcamera",
        PackageResource.Prefix + "gstreamer1.0-pipewire",
        ConsoleAutologinResource.ResourceName,
    ];

    /// <inheritdoc/>
    public string Detected => "The instruction that runs this frame's camera is missing or wrong.";

    /// <inheritdoc/>
    public string WhyItMatters => "Without it the camera never starts, so a call carries no picture of this room.";

    /// <summary>Where the unit lives for this frame's user.</summary>
    public string Path => _session.HomeDirectory + "/.config/systemd/user/" + UnitName;

    /// <inheritdoc/>
    public ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = Path;
        var desired = DesiredContent();
        var actual = _files.ReadText(path);

        var matches = string.Equals(
            JournalStorageResource.ShortHash(actual ?? string.Empty),
            JournalStorageResource.ShortHash(desired),
            StringComparison.Ordinal);

        return ValueTask.FromResult(new ResourceObservation(
            matches,
            $"{path} {JournalStorageResource.ShortHash(desired)} publishing {NodeDescription} at 1080p30",
            actual is null ? $"{path} absent" : $"{path} {JournalStorageResource.ShortHash(actual)}"));
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var path = Path;
        _files.WriteText(path, DesiredContent());
        await _session.GiveToUserAsync(path, cancellationToken).ConfigureAwait(false);

        var reloaded = await _session
            .RunAsync("systemctl", ["--user", "daemon-reload"], cancellationToken)
            .ConfigureAwait(false);

        return new ResourceAction(
            $"write {path} publishing {NodeDescription} and run systemctl --user daemon-reload"
                + (reloaded.Succeeded ? string.Empty : $" (refused: {reloaded.Combined})"),
            "Writing down how this frame should run its camera — the whole room in view, thirty pictures a second.");
    }
}

/// <summary>
/// <c>unit.framelink-camera.enabled</c> — the camera unit is wired into the session's start-up.
/// </summary>
/// <remarks>
/// Separate from the content per §2.2: a byte-perfect unit that is not enabled is a different
/// diagnosis with a different command. Unlike the browser, the camera has no second path that also
/// starts it — labwc's autostart starts Chromium, and nothing starts this — so the symlink is the
/// only reason the camera exists after a reboot.
/// </remarks>
public sealed class CameraUnitEnabledResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "unit.framelink-camera.enabled";

    private readonly IUserSession _session;

    /// <summary>Creates the resource.</summary>
    public CameraUnitEnabledResource(IUserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn => [CameraUnitResource.ResourceName];

    /// <inheritdoc/>
    public string Detected => "This frame's camera is set up but not switched on.";

    /// <inheritdoc/>
    public string WhyItMatters => "A camera that is not switched on does not come back after a restart.";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var result = await _session
            .RunAsync("systemctl", ["--user", "is-enabled", CameraUnitResource.UnitName], cancellationToken)
            .ConfigureAwait(false);

        // `is-enabled` exits non-zero for `disabled` and `not-found` alike and puts the answer on
        // stdout in both cases, so the text is read rather than the exit code.
        var observed = result.StandardOutput.Trim();
        observed = observed.Length == 0
            ? Fallback(result)
            : observed.Split('\n')[0].Trim();

        return new ResourceObservation(
            string.Equals(observed, "enabled", StringComparison.Ordinal),
            "enabled",
            observed);
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var result = await _session
            .RunAsync("systemctl", ["--user", "enable", CameraUnitResource.UnitName], cancellationToken)
            .ConfigureAwait(false);

        return new ResourceAction(
            $"systemctl --user enable {CameraUnitResource.UnitName}"
                + (result.Succeeded ? string.Empty : $" (refused: {result.Combined})"),
            "Switching this frame's camera on, so it comes back every time the frame starts.");
    }

    private static string Fallback(ProcessResult result) =>
        result.StandardError.Length == 0
            ? "no answer from the user session"
            : result.StandardError.Split('\n')[0].Trim();
}

/// <summary>
/// <c>camera.pipewire-node.framelink-cam</c> — <b>the resource that exists because the unit lies</b>.
/// </summary>
/// <remarks>
/// <para>
/// From guide 6 step 5's LOOK FOR and CHECKPOINT. Guides 6 and 11 both record the measured bug:
/// <c>gstpipewiresink</c> in PipeWire 1.4.x — Trixie ships 1.4.2 — posts a fatal element error when
/// a consumer tears down abruptly, and <c>gst-launch</c> can then hang in shutdown. The unit keeps
/// reporting <c>active</c> while the camera is dead, and <c>Restart=always</c> cannot fire on a
/// hung process. <b>Unit-active and node-present are therefore different diagnoses</b> and have to
/// be separately observable, which is exactly what this resource is.
/// </para>
/// <para>
/// Upstream guards the path in PipeWire ≥ 1.6.0. When the OS carries that, this resource stays —
/// it is still the right assertion — and the per-call recycle §2.10 inherited from guide 11 is what
/// gets switched off, by setting.
/// </para>
/// <para>
/// <b>Its Act is the same command supervision runs, and that is not a duplication.</b> §2.10's
/// interlock names this resource precisely so the two never race: a recycle opens a window over it,
/// and the reconciler holds a lock while applying. What differs is the trigger — the recycle is
/// prophylactic and fires on every call-end whether or not anything is wrong, while this fires only
/// when the node is actually missing.
/// </para>
/// <para>
/// An <c>imx708</c> device or extra V4L2 sources appearing alongside it means the WirePlumber
/// fragment did not load, which is <see cref="WirePlumberCameraMonitorsResource"/>'s delta rather
/// than this one's — this resource is blocked behind it, so that fault is reported once, in the
/// place that can fix it.
/// </para>
/// </remarks>
public sealed class CameraNodeResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "camera.pipewire-node.framelink-cam";

    private readonly IUserSession _session;

    /// <summary>Creates the resource.</summary>
    public CameraNodeResource(IUserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn =>
        [CameraUnitEnabledResource.ResourceName, WirePlumberCameraMonitorsResource.ResourceName];

    /// <inheritdoc/>
    public string Detected => "This frame is not offering its camera to anything.";

    /// <inheritdoc/>
    public string WhyItMatters => "In a call the other side sees a black square instead of this room.";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var status = await _session.RunAsync("wpctl", ["status"], cancellationToken).ConfigureAwait(false);
        var expected = $"exactly one camera, {CameraUnitResource.NodeDescription}";

        if (!status.Succeeded)
        {
            // A local read that failed has learned something real about this machine, so it is
            // drift and not Unevaluable — that outcome is reserved for an authority off the device
            // that did not answer, and must never become the place a real failure goes to be quiet.
            return new ResourceObservation(
                false,
                expected,
                status.Combined.Length == 0 ? "wpctl said nothing" : $"wpctl failed: {status.Combined.Replace('\n', ' ')}");
        }

        var sources = WpctlStatus.Entries(status.StandardOutput, WpctlStatus.Video, WpctlStatus.Sources);
        var present = sources.Contains(CameraUnitResource.NodeDescription, StringComparer.Ordinal);

        return new ResourceObservation(
            present && sources.Count == 1,
            expected,
            sources.Count == 0
                ? "PipeWire is offering no camera at all"
                : $"PipeWire is offering {string.Join(", ", sources)}");
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var restarted = await _session
            .RunAsync("systemctl", ["--user", "restart", CameraUnitResource.UnitName], cancellationToken)
            .ConfigureAwait(false);

        return new ResourceAction(
            $"systemctl --user restart {CameraUnitResource.UnitName}"
                + (restarted.Succeeded ? string.Empty : $" (refused: {restarted.Combined})"),
            "Starting this frame's camera over, because it is running but has stopped offering a picture.");
    }
}

/// <summary>
/// <c>portal.camera-interface-published</c> — the interface Chromium asks through is on the bus.
/// </summary>
/// <remarks>
/// <para>
/// From guide 6 step 6. Separate from the drop-in because a correct drop-in with a missing
/// interface is a real, distinct fault: the GTK backend is not installed, or the portal started
/// before the drop-in was written. Without the interface <c>getUserMedia()</c> hangs, and the frame
/// shows a black self-view with nothing in any log to explain it.
/// </para>
/// <para>
/// <b>The caveat the implementer has to know:</b> the portal is D-Bus-activated, so on a frame that
/// has not made a call since boot it is legitimately <c>inactive</c>, and <c>busctl introspect</c>
/// <b>starts it as a side effect of observing</b>. That is an acceptable read — there is no other
/// way to ask — but it means "the portal is running" is never evidence of anything. Only the
/// interface list is.
/// </para>
/// </remarks>
public sealed class PortalCameraInterfaceResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "portal.camera-interface-published";

    /// <summary>The portal's bus name.</summary>
    public const string BusName = "org.freedesktop.portal.Desktop";

    /// <summary>Its object path.</summary>
    public const string ObjectPath = "/org/freedesktop/portal/desktop";

    /// <summary>The interface that must be published there.</summary>
    public const string Interface = "org.freedesktop.portal.Camera";

    private readonly IUserSession _session;

    /// <summary>Creates the resource.</summary>
    public PortalCameraInterfaceResource(IUserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn =>
        [PortalDesktopDropInResource.ResourceName, PackageResource.Prefix + "xdg-desktop-portal-gtk"];

    /// <inheritdoc/>
    public string Detected => "The way the browser asks for the camera is not being offered.";

    /// <inheritdoc/>
    public string WhyItMatters => "A call opens with a black square where this room should be, and nothing says why.";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var result = await _session
            .RunAsync("busctl", ["--user", "introspect", BusName, ObjectPath], cancellationToken)
            .ConfigureAwait(false);

        var published = false;
        foreach (var line in result.StandardOutput.Split('\n'))
        {
            if (line.Contains(Interface, StringComparison.Ordinal)
                && line.Contains("interface", StringComparison.OrdinalIgnoreCase))
            {
                published = true;
                break;
            }
        }

        return new ResourceObservation(
            published,
            $"{Interface} published at {ObjectPath}",
            published
                ? $"{Interface} is published"
                : result.Succeeded
                    ? $"the portal answers at {ObjectPath} and publishes no Camera interface"
                    : $"the portal did not answer: {Condense(result.Combined)}");
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var restarted = await _session
            .RunAsync("systemctl", ["--user", "restart", PortalDesktopDropInResource.UnitName], cancellationToken)
            .ConfigureAwait(false);

        return new ResourceAction(
            $"systemctl --user restart {PortalDesktopDropInResource.UnitName}"
                + (restarted.Succeeded ? string.Empty : $" (refused: {restarted.Combined})"),
            "Starting the part that hands out the camera over again, so it offers the camera the browser asks for.");
    }

    private static string Condense(string output) =>
        output.Length == 0 ? "nothing at all" : output.Replace('\n', ' ').Trim();
}

/// <summary>
/// <c>boot.config.camera-auto-detect</c> — the firmware loads the camera's overlay at boot.
/// </summary>
/// <remarks>
/// <para>
/// From guide 6 step 5's LOOK FOR — "recheck … the <c>camera_auto_detect=1</c> line in
/// <c>/boot/firmware/config.txt</c>" — and from the v1 reference, which carries the line. It is a
/// stock-image default that no guide writes, and it is a resource anyway because it has a real
/// observation and a real Act: without it the firmware loads no camera overlay, libcamera
/// enumerates nothing, and every layer above reports its own symptom while the cause sits in a file
/// nobody looked at.
/// </para>
/// <para>
/// <b>Brick-capable, and scheduled last for it.</b> §5.5 puts <c>/boot/firmware</c> writes at the
/// end of the order and the catalog schedules this one 76th of 79. The display group is the one
/// carve-out from that rule and this is not part of it — a dark panel makes §2.7's narration
/// worthless, while a camera that is missing for another twenty minutes of provisioning costs
/// nothing. It keeps every mitigation the rule attaches: the content is a known-good literal, the
/// edit is validated as minimal before it is written, the previous <c>config.txt</c> is copied to
/// the FAT32 boot partition where a card reader can find it, and
/// <see cref="BootPartitionGuard"/> puts it back if the frame comes back twice without it.
/// </para>
/// <para>
/// <b>What closes the trial is the line surviving a boot, not a camera appearing.</b> That differs
/// from <see cref="DisplayPanelOverlayResource"/> deliberately, and the difference is where the
/// evidence comes from. The display's probe is a local sysfs read that always answers; camera
/// enumeration can only be read through the user session, which is legitimately absent while the
/// session is starting — so a transient "no camera" would burn the boot budget and roll back a line
/// the frame needs. The enumeration is still reported, in the observed text, because the catalog
/// asks for it and because a person reading the delta wants both halves; and a camera that never
/// appears has its own resource, <see cref="CameraNodeResource"/>, with its own delta and its own
/// Act. One hardware fault, one escalation path.
/// </para>
/// </remarks>
public sealed class CameraAutoDetectResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "boot.config.camera-auto-detect";

    /// <summary>The line the firmware reads.</summary>
    public const string ConfigLine = "camera_auto_detect=1";

    private readonly ISystemFiles _files;
    private readonly BootPartitionGuard _guard;
    private readonly IUserSession _session;
    private readonly IAgentLog _log;

    /// <summary>Creates the resource.</summary>
    /// <param name="files">The boot partition.</param>
    /// <param name="guard">Backup, validation and boot-count self-repair (§5.5).</param>
    /// <param name="session">Where <c>wpctl</c> is asked whether a camera turned up.</param>
    /// <param name="log">Where a refused write is recorded.</param>
    public CameraAutoDetectResource(
        ISystemFiles files,
        BootPartitionGuard guard,
        IUserSession session,
        IAgentLog log)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(log);

        _files = files;
        _guard = guard;
        _session = session;
        _log = log;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public string Detected => "This frame is not set to look for its camera when it starts.";

    /// <inheritdoc/>
    public string WhyItMatters => "Until it is, the camera might as well not be plugged in — nothing on the frame can see it.";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        // Self-repair first, before anything reads the file (§5.5). A rollback that happened on
        // this boot must be visible to the compare that follows it.
        var verdict = _guard.Tick(BootConfigText.ConfigPath);
        var present = BootConfigText.HasLine(_files.ReadText(BootConfigText.ConfigPath), ConfigLine);

        if (present)
        {
            _guard.Confirm(BootConfigText.ConfigPath);
        }

        if (verdict is GuardVerdict.RolledBack or GuardVerdict.Locked && !present)
        {
            return new ResourceObservation(
                false,
                $"{BootConfigText.ConfigPath} contains '{ConfigLine}'",
                "the camera setting was tried and put back automatically because the frame came back without it; "
                    + $"{BootPartitionGuard.BackupFor(BootConfigText.ConfigPath)} holds the version before the change");
        }

        var status = await _session.RunAsync("wpctl", ["status"], cancellationToken).ConfigureAwait(false);
        var cameras = status.Succeeded
            ? WpctlStatus.Entries(status.StandardOutput, WpctlStatus.Video, WpctlStatus.Sources)
            : [];

        var evidence = cameras.Count > 0
            ? $"a camera is enumerating ({string.Join(", ", cameras)})"
            : "no camera is enumerating yet";

        return new ResourceObservation(
            present,
            $"{BootConfigText.ConfigPath} contains '{ConfigLine}'",
            present ? $"the line is present; {evidence}" : $"the line is absent; {evidence}");
    }

    /// <inheritdoc/>
    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var current = _files.ReadText(BootConfigText.ConfigPath);

        if (!_guard.BeginTrial(BootConfigText.ConfigPath))
        {
            return ValueTask.FromResult(new ResourceAction(
                $"refused to write {BootConfigText.ConfigPath} — this change has already been rolled back once",
                "This frame already tried switching its camera on and had to undo it. It will not try again on its own."));
        }

        var updated = BootConfigText.AppendLine(current, ConfigLine);
        var check = BootConfigText.ValidateConfig(current, updated, ConfigLine);

        if (!check.Valid)
        {
            _log.Fail($"Refusing to write {BootConfigText.ConfigPath}: {check.Problem}");
            return ValueTask.FromResult(new ResourceAction(
                $"refused to write {BootConfigText.ConfigPath} — {check.Problem}",
                "This frame checked the change it was about to make to its start-up settings, did not like it, and left them alone."));
        }

        _files.WriteText(BootConfigText.ConfigPath, updated);

        return ValueTask.FromResult(new ResourceAction(
            $"append '{ConfigLine}' to {BootConfigText.ConfigPath} "
                + $"(backed up to {BootPartitionGuard.BackupFor(BootConfigText.ConfigPath)})",
            "Telling this frame to look for its camera when it starts up."));
    }
}
