using FrameLink.Agent.Hosting;
using FrameLink.Agent.Local;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Agent.Resources;

/// <summary>
/// <c>unit.chromium-kiosk.content</c> — the user unit that runs the frame's browser.
/// </summary>
/// <remarks>
/// <para>
/// Created in guide 5 step 5, with its <b>final</b> desired value supplied by guide 10 step 4 —
/// one resource, not two. Guide 5's interim version (placeholder URL, no readiness guard) is a
/// transitional state rather than a separate desired value, so only the final form exists here.
/// </para>
/// <para>
/// <b>What v2 changes, and what it must not.</b> The URL becomes the agent's own local origin and
/// the <c>framelink-spa.service</c> ordering disappears with the service (§2.1: the app is inside
/// the binary). Everything else is carried across verbatim, because the catalog names seven flags
/// as individually load-bearing and each of them was bought with a measured failure:
/// </para>
/// <list type="bullet">
/// <item><c>--ozone-platform=wayland</c> — Chromium's X11 default fails <i>silently</i> under labwc.</item>
/// <item><c>--user-data-dir=/tmp/framelink-chromium</c> — the profile on tmpfs, so a power cut
/// leaves no stale <c>SingletonLock</c>.</item>
/// <item><c>--auto-accept-camera-and-microphone-capture</c> — and it <b>must not</b> be combined
/// with <c>--use-fake-ui-for-media-stream</c>: silent startup crash on this build.</item>
/// <item><c>--enable-features=UsePipeWireCamera</c> — the legacy V4L2 path hangs probing the Pi's
/// internal camera nodes.</item>
/// <item><c>--autoplay-policy=no-user-gesture-required</c> — nobody taps a photo frame to start a
/// call.</item>
/// <item><c>--disable-background-timer-throttling</c> and <c>--disable-renderer-backgrounding</c>
/// — keep the tab responsive under labwc's window handling.</item>
/// </list>
/// <para>
/// The <c>rm -rf /tmp/framelink-chromium</c> pre-start is what makes an app update actually reach
/// the browser: Chromium's module cache inside the profile otherwise keeps serving stale
/// JavaScript after the app changes on disk. Under v2 the app changes when the <i>agent binary</i>
/// changes, so this line is what connects a self-update to what is on the screen. The portal
/// camera permission survives the wipe because it lives in <c>~/.local/share/flatpak/db</c>.
/// </para>
/// <para>
/// <c>127.0.0.1</c> everywhere, where v1 mixed <c>localhost</c> in <c>ExecStart</c> with
/// <c>127.0.0.1</c> in the guard. To a browser those are <b>different origins</b>, and §2.7
/// requires the repair screen and the product to share <i>one</i>.
/// </para>
/// </remarks>
public sealed class ChromiumKioskUnitResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "unit.chromium-kiosk.content";

    /// <summary>The unit's name, as the user manager knows it.</summary>
    public const string UnitName = "chromium-kiosk.service";

    /// <summary>The browser binary Trixie installs.</summary>
    public const string Browser = "/usr/bin/chromium";

    /// <summary>The camera node unit the browser waits for, from the camera block.</summary>
    public const string CameraUnitName = "framelink-camera.service";

    private readonly ISystemFiles _files;
    private readonly IUserSession _session;

    /// <summary>Creates the resource.</summary>
    public ChromiumKioskUnitResource(ISystemFiles files, IUserSession session)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(session);

        _files = files;
        _session = session;
    }

    /// <summary>The twelve flags, in the order guide 10 step 4 writes them.</summary>
    public static IReadOnlyList<string> Flags { get; } =
    [
        "--ozone-platform=wayland",
        "--user-data-dir=/tmp/framelink-chromium",
        "--kiosk",
        "--noerrdialogs",
        "--disable-infobars",
        "--disable-session-crashed-bubble",
        "--no-first-run",
        "--auto-accept-camera-and-microphone-capture",
        "--enable-features=UsePipeWireCamera",
        "--autoplay-policy=no-user-gesture-required",
        "--disable-background-timer-throttling",
        "--disable-renderer-backgrounding",
    ];

    /// <summary>The URL the browser opens — the agent's own origin (§2.1, §2.7).</summary>
    public static string Origin => "http://127.0.0.1:" + LocalOrigin.DefaultPort + "/";

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn =>
        [PackageResource.Prefix + "chromium", ConsoleAutologinResource.ResourceName];

    /// <inheritdoc/>
    public string Detected => "The instruction that opens this frame's browser is missing or wrong.";

    /// <inheritdoc/>
    public string WhyItMatters => "The browser is what shows the photos and the video call, so without it the screen stays empty.";

    /// <summary>Where the unit lives for this frame's user.</summary>
    public string Path => _session.HomeDirectory + "/.config/systemd/user/" + UnitName;

    /// <summary>The unit text this resource converges on.</summary>
    public static string DesiredContent()
    {
        var execStart = "ExecStart=" + Browser + " \\\n";
        foreach (var flag in Flags)
        {
            execStart += "  " + flag + " \\\n";
        }

        execStart += "  " + Origin + "\n";

        return "[Unit]\n"
            + "Description=Chromium Kiosk Browser\n"
            + "After=graphical-session.target " + CameraUnitName + "\n"
            + "Wants=" + CameraUnitName + "\n"
            + "Requires=graphical-session.target\n"
            + "\n"
            + "[Service]\n"
            + "Type=simple\n"
            + "Environment=\"WAYLAND_DISPLAY=" + LoginUserSession.WaylandDisplay + "\"\n"
            + "ExecStartPre=/bin/rm -rf /tmp/framelink-chromium\n"
            + "ExecStartPre=/bin/bash -c 'while [ ! -S \"/run/user/$(id -u)/${WAYLAND_DISPLAY}\" ]; do sleep 0.1; done'\n"
            + "ExecStartPre=/bin/bash -c 'until curl -sf " + Origin + " >/dev/null 2>&1; do sleep 0.3; done'\n"
            + execStart
            + "Restart=always\n"
            + "RestartSec=5\n"
            + "\n"
            + "[Install]\n"
            + "WantedBy=default.target\n";
    }

    /// <summary>The <c>ExecStart</c> arguments a unit document declares, continuations joined.</summary>
    /// <remarks>
    /// Written as a parser rather than as a constant because the resource that consumes it —
    /// <see cref="ChromiumKioskRunningResource"/> — has to compare the running process against
    /// <b>the unit file on disk</b>, which is the thing systemd actually started. Reading the
    /// desired constant instead would make "the file drifted and the browser is faithfully running
    /// the drifted version" report as healthy.
    /// </remarks>
    public static IReadOnlyList<string> ExecStartArguments(string? unitDocument)
    {
        if (string.IsNullOrEmpty(unitDocument))
        {
            return [];
        }

        var joined = unitDocument.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\\\n", " ", StringComparison.Ordinal);

        foreach (var line in joined.Split('\n'))
        {
            if (!line.StartsWith("ExecStart=", StringComparison.Ordinal))
            {
                continue;
            }

            return line["ExecStart=".Length..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return [];
    }

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
            $"{path} {JournalStorageResource.ShortHash(desired)} opening {Origin}",
            actual is null ? $"{path} absent" : $"{path} {JournalStorageResource.ShortHash(actual)}"));
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var path = Path;
        _files.WriteText(path, DesiredContent());
        await _session.GiveToUserAsync(path, cancellationToken).ConfigureAwait(false);

        // The user manager will not notice a new unit file on its own, and the enable resource
        // that depends on this one would fail against a unit systemd has never read.
        var reloaded = await _session
            .RunAsync("systemctl", ["--user", "daemon-reload"], cancellationToken)
            .ConfigureAwait(false);

        return new ResourceAction(
            $"write {path} opening {Origin} and run systemctl --user daemon-reload"
                + (reloaded.Succeeded ? string.Empty : $" (refused: {reloaded.Combined})"),
            "Writing down how this frame's browser should be opened, and pointing it at the app this frame serves itself.");
    }
}

/// <summary>
/// <c>unit.chromium-kiosk.enabled</c> — the browser unit is wired into the user session's start-up.
/// </summary>
/// <remarks>
/// Separate from the content per §2.2: a unit can be byte-perfect and not enabled, which is a
/// different diagnosis with a different command. Note the redundancy the catalog asks to be kept
/// <i>deliberately</i>: in v1 the browser's actual start comes from labwc's autostart, not from
/// this enablement, and both paths exist. Either alone is sufficient, so a frame keeps its browser
/// through the loss of either one.
/// </remarks>
public sealed class ChromiumKioskEnabledResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "unit.chromium-kiosk.enabled";

    private readonly IUserSession _session;

    /// <summary>Creates the resource.</summary>
    public ChromiumKioskEnabledResource(IUserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn => [ChromiumKioskUnitResource.ResourceName];

    /// <inheritdoc/>
    public string Detected => "This frame's browser is set up but not switched on.";

    /// <inheritdoc/>
    public string WhyItMatters => "A browser that is not switched on does not come back after a restart.";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var result = await _session
            .RunAsync("systemctl", ["--user", "is-enabled", ChromiumKioskUnitResource.UnitName], cancellationToken)
            .ConfigureAwait(false);

        // `is-enabled` exits non-zero for `disabled` and `not-found` alike and puts the answer on
        // stdout in both cases, so the text is read rather than the exit code — that is what keeps
        // "switched off" and "there is no such unit" two different observed values.
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
            .RunAsync("systemctl", ["--user", "enable", ChromiumKioskUnitResource.UnitName], cancellationToken)
            .ConfigureAwait(false);

        return new ResourceAction(
            $"systemctl --user enable {ChromiumKioskUnitResource.UnitName}"
                + (result.Succeeded ? string.Empty : $" (refused: {result.Combined})"),
            "Switching on this frame's browser so it comes back every time the frame starts.");
    }

    private static string Fallback(ProcessResult result) =>
        result.StandardError.Length == 0
            ? "no answer from the user session"
            : result.StandardError.Split('\n')[0].Trim();
}

/// <summary>
/// <c>unit.chromium-kiosk.running-matches-content</c> — the browser that is <i>running</i> is the
/// one the unit describes.
/// </summary>
/// <remarks>
/// <para>
/// Its own resource because "unit file correct, running process stale" is the single most common
/// post-edit drift, and because guide 6 states the principle outright: <b>the command line is the
/// authoritative truth — if the flag is not here, it is not in effect, whatever a config file
/// says</b>. A frame that has had its unit rewritten and never restarted is a frame running the
/// old URL and the old camera backend while every file on it reads correctly.
/// </para>
/// <para>
/// <b>The compare is containment, never equality, and that is measured rather than defensive.</b>
/// <c>pkg.chromium</c> drags in <c>rpi-chromium-mods</c>, which injects flags from
/// <c>/etc/chromium.d/</c> at launch — so the running command line is a legitimate <b>superset</b>
/// of <c>ExecStart</c>. An equality compare reports drift on a perfectly healthy frame, on every
/// pass, forever: the agent would restart the browser every five minutes and the delta would never
/// close, because the extra flags are put back by the launcher each time.
/// </para>
/// <para>
/// <b>Renderers are excluded from the compare.</b> <c>pgrep -a chromium</c> lists the whole tree,
/// and a renderer's command line carries <c>--type=renderer</c> and none of the kiosk flags.
/// Comparing against the first line <c>pgrep</c> happens to return would report drift depending on
/// process scheduling.
/// </para>
/// </remarks>
public sealed class ChromiumKioskRunningResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "unit.chromium-kiosk.running-matches-content";

    private readonly ISystemFiles _files;
    private readonly IProcessRunner _processes;
    private readonly IUserSession _session;
    private readonly ChromiumKioskUnitResource _unit;

    /// <summary>Creates the resource.</summary>
    public ChromiumKioskRunningResource(
        ISystemFiles files,
        IProcessRunner processes,
        IUserSession session,
        ChromiumKioskUnitResource unit)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(unit);

        _files = files;
        _processes = processes;
        _session = session;
        _unit = unit;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn =>
        [ChromiumKioskUnitResource.ResourceName, ChromiumKioskEnabledResource.ResourceName];

    /// <inheritdoc/>
    public string Detected => "This frame's browser is running an out-of-date set of instructions.";

    /// <inheritdoc/>
    public string WhyItMatters => "It may be showing the wrong page, or using the camera the wrong way.";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var declared = ChromiumKioskUnitResource.ExecStartArguments(_files.ReadText(_unit.Path));

        if (declared.Count == 0)
        {
            return new ResourceObservation(
                false,
                "a running browser matching the unit",
                $"{_unit.Path} declares no ExecStart to compare against");
        }

        var listed = await _processes.RunAsync("pgrep", ["-a", "chromium"], cancellationToken).ConfigureAwait(false);
        var running = MainProcessLine(listed.StandardOutput);

        if (running is null)
        {
            return new ResourceObservation(
                false,
                "a running browser matching the unit",
                "no browser process is running");
        }

        var missing = MissingFrom(running, declared);

        return new ResourceObservation(
            missing.Count == 0,
            $"a running browser carrying all {declared.Count} declared arguments",
            missing.Count == 0
                ? $"running with all {declared.Count} declared arguments"
                : $"running without {string.Join(", ", missing)}");
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        await _session.RunAsync("systemctl", ["--user", "daemon-reload"], cancellationToken).ConfigureAwait(false);

        var restarted = await _session
            .RunAsync("systemctl", ["--user", "restart", ChromiumKioskUnitResource.UnitName], cancellationToken)
            .ConfigureAwait(false);

        return new ResourceAction(
            $"systemctl --user daemon-reload && systemctl --user restart {ChromiumKioskUnitResource.UnitName}"
                + (restarted.Succeeded ? string.Empty : $" (refused: {restarted.Combined})"),
            "Restarting this frame's browser so it picks up the instructions that were written for it.");
    }

    /// <summary>The <c>pgrep -a</c> line belonging to the main browser process, or null.</summary>
    public static string? MainProcessLine(string pgrepOutput)
    {
        ArgumentNullException.ThrowIfNull(pgrepOutput);

        foreach (var line in pgrepOutput.Split('\n'))
        {
            if (line.Contains(ChromiumKioskUnitResource.Browser, StringComparison.Ordinal)
                && !line.Contains("--type=", StringComparison.Ordinal))
            {
                return line.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Which declared arguments the running command line does not carry — containment, not
    /// equality.
    /// </summary>
    public static IReadOnlyList<string> MissingFrom(string runningCommandLine, IReadOnlyList<string> declared)
    {
        ArgumentNullException.ThrowIfNull(runningCommandLine);
        ArgumentNullException.ThrowIfNull(declared);

        var present = new HashSet<string>(
            runningCommandLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.Ordinal);

        var missing = new List<string>();
        foreach (var argument in declared)
        {
            if (!present.Contains(argument))
            {
                missing.Add(argument);
            }
        }

        return missing;
    }
}
