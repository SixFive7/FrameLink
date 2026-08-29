using System.Globalization;
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

    /// <summary>
    /// The arguments a unit document's <c>ExecStart=</c> declares — continuations joined, and the
    /// executable itself excluded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written as a parser rather than as a constant because the resource that consumes it —
    /// <see cref="ChromiumKioskRunningResource"/> — has to compare the running process against
    /// <b>the unit file on disk</b>, which is the thing systemd actually started. Reading the
    /// desired constant instead would make "the file drifted and the browser is faithfully running
    /// the drifted version" report as healthy.
    /// </para>
    /// <para>
    /// <b>Dropping the executable is the measured half of this, not tidiness.</b> On Trixie
    /// <c>/usr/bin/chromium</c> is a 5,920-byte shell script whose last line is
    /// <c>exec $LIBDIR/$APPNAME $CHROMIUM_FLAGS "$@"</c>. <c>exec</c> replaces the process image, so
    /// by the time there is a process to look at, its <c>argv[0]</c> is
    /// <c>/usr/lib/chromium/chromium</c> and the path this unit declares appears <i>nowhere on the
    /// running machine</i> — measured on the mule, <c>pgrep -a chromium | grep -c
    /// '/usr/bin/chromium'</c> is 0 against 12 for the library path. A comparison that included the
    /// executable could therefore never converge: it would report the browser as carrying every
    /// declared argument except its own binary, on a frame where nothing is wrong, forever.
    /// Everything after the executable — the twelve flags and the URL — survives the <c>exec</c>
    /// unchanged, and is exactly what this comparison is for.
    /// </para>
    /// <para>
    /// Dropping <i>by position</i> rather than by matching <see cref="Browser"/> is deliberate: it
    /// also swallows systemd's <c>ExecStart=</c> prefixes (<c>-</c>, <c>@</c>, <c>+</c>, <c>!</c>),
    /// and it cannot go stale the next time Debian moves the binary.
    /// </para>
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

            var tokens = line["ExecStart=".Length..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return tokens.Length <= 1 ? [] : tokens[1..];
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
        if (await UserSessionGate.NotSettledAsync(_session, "enabled", cancellationToken).ConfigureAwait(false)
            is { } waiting)
        {
            return waiting;
        }

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
/// <b>The process is the one systemd owns, asked of systemd — never a process found by grepping
/// for a path.</b> The unit's <c>MainPID</c> is read from the user manager and its command line
/// from <c>/proc/&lt;pid&gt;/cmdline</c>. That is the whole identification, and each half of it is
/// paying for a measured failure:
/// </para>
/// <list type="bullet">
/// <item><b>The path is not a name the running machine carries.</b> On Trixie
/// <c>/usr/bin/chromium</c> is a shell script that <c>exec</c>s <c>/usr/lib/chromium/chromium</c>,
/// so a check for the declared path matches nothing — measured on the mule, 0 lines against 12 for
/// the library path. This resource reported <i>"no browser process is running"</i> on every boot,
/// forever, while the browser was up and drawing: <c>Started</c> at 15.2–15.4 s, the verdict at
/// 40.9–41.2 s, so the browser had been alive for 25.7 s each time it was declared absent. It is
/// not a race and never was, and the Act it triggered — a restart — took a working browser down
/// five times a boot and twice raced the compositor into <i>Failed to connect to Wayland
/// display</i>.</item>
/// <item><b>A path also cannot tell this unit's browser from any other.</b> A Chromium somebody
/// started over SSH carries the same binary and can carry the same flags. <c>MainPID</c> is the
/// only answer that is about <i>the unit</i>, and it comes from the process manager that started
/// it.</item>
/// </list>
/// <para>
/// <b>Two alternatives were considered and are worse.</b> Matching the unit's cgroup is equally
/// authoritative but yields the whole tree — thirteen processes on the mule — with no marker for
/// which is the main one, so the renderer-filtering guesswork would come straight back; and
/// building the path (<c>user.slice/user-1000.slice/user@1000.service/app.slice/…</c>) hard-codes a
/// layout that moves between systemd versions, which is the same class of assumption as the binary
/// path. Asserting on distinctive arguments — <c>--kiosk</c> plus the URL — survives the wrapper,
/// but it never establishes <i>whose</i> process it found, so a hand-started browser would satisfy
/// it while the unit lay dead.
/// </para>
/// <para>
/// <b>The compare is containment, never equality, and that is measured rather than defensive.</b>
/// <c>pkg.chromium</c> drags in <c>rpi-chromium-mods</c>, which injects flags from
/// <c>/etc/chromium.d/</c>, and the wrapper script adds more of its own
/// (<c>--force-renderer-accessibility</c>, <c>--enable-remote-extensions</c>, <c>--use-angle=gles</c>
/// and others, all present on the mule) — so the running command line is a legitimate
/// <b>superset</b> of <c>ExecStart</c>. An equality compare reports drift on a perfectly healthy
/// frame, on every pass, forever: the agent would restart the browser every five minutes and the
/// delta would never close, because the extra flags are put back by the launcher each time.
/// Undeclared extras are therefore not a fault, and must not be made one here.
/// </para>
/// <para>
/// <b>Renderers stopped being a hazard rather than being filtered out.</b> They were the reason the
/// old <c>pgrep -a chromium</c> reading had to skip lines carrying <c>--type=</c>: the listing is
/// the whole process tree in scheduling order. <c>MainPID</c> names one process and it is the
/// browser itself, so there is nothing to disambiguate.
/// </para>
/// <para>
/// <b>A unit that has not finished starting is not yet answerable, and that is the second half of
/// the identification.</b> <c>MainPID</c> alone is a value with no phase attached, and the two
/// windows where that bites were both measured on the mule on 2026-08-16. Around ten seconds after
/// a reboot the login session exists — so <see cref="UserSessionGate"/> lets the resource
/// through — while <c>chromium-kiosk.service</c> is still inside its three <c>ExecStartPre</c>
/// commands, which is a <c>~4.5 s</c> wait for the Wayland socket followed by a poll of the
/// agent's own origin; Chromium reached <c>Started</c> at 15.2 s. The resource read
/// <c>MainPID=0</c>, reported <i>"no browser process is running"</i>, and its Act took a browser
/// that was seconds from drawing and restarted it, three attempts in a row, to an escalation. So
/// the reading now takes <c>ActiveState</c> and <c>SubState</c> in the <i>same</i> answer as
/// <c>MainPID</c> — one <c>systemctl show</c>, because asking twice reintroduces the race against
/// the very window being closed — and a unit that is starting is
/// <see cref="ResourceObservation.Unevaluable"/>: no attempt spent, nothing acted on, nothing
/// rebooted, re-read shortly after.
/// </para>
/// <para>
/// <b>This does not gate the resource on the browser running, and it must not.</b> A resource whose
/// job is to assert that the browser is up cannot excuse itself when the browser is down. The gate
/// is on the unit having <i>finished starting</i>, which is a different question with a different
/// answer: <c>dead</c>, <c>failed</c> and the auto-restart sub-states are all fully answerable and
/// all report drift, exactly as before. What is excused is <c>condition</c>, <c>start-pre</c>,
/// <c>start</c> and <c>start-post</c> — the four sub-states in which systemd has not yet finished
/// making the process this resource is about.
/// </para>
/// <para>
/// <b>Gating on <c>SubState</c> rather than <c>ActiveState</c> is deliberate</b>, and systemd's own
/// source is why. <c>ActiveState=active</c> covers <c>running</c> and <c>exited</c> alike, and
/// <c>SERVICE_EXITED</c> is not in systemd's <c>SERVICE_STATE_WITH_MAIN_PROCESS</c> set — so a
/// <c>RemainAfterExit=</c> unit reports <c>active</c> with <c>MainPID=0</c>, and a gate written on
/// <c>ActiveState</c> would read that as a live browser with no process. <c>Type=idle</c> is worse
/// still: <c>state_translation_table_idle</c> maps <c>start-pre</c> itself to <c>UNIT_ACTIVE</c>.
/// This unit is <c>Type=simple</c> and hits neither today, and both are one edit away.
/// </para>
/// <para>
/// <b>The argv actually read is written into the observation, and that is not decoration.</b> On
/// its first live pass after the <c>MainPID</c> fix this resource reported <i>all thirteen</i>
/// declared arguments missing while naming a non-zero pid whose command line it had read
/// successfully — and the record does not say what that command line was, so the cause is
/// undetermined to this day. The leading hypothesis at the time was that <c>MainPID</c> named the
/// <c>ExecStartPre</c> process, and systemd's source <b>refutes</b> it: <c>service_set_state</c>
/// calls <c>service_unwatch_main_pid</c> on entry to any state outside
/// <c>SERVICE_STATE_WITH_MAIN_PROCESS</c>, which excludes <c>SERVICE_START_PRE</c>, and
/// <c>service_enter_start_pre</c> spawns into <c>control_pid</c> — so <c>MainPID</c> is <c>0</c>
/// there and <c>ControlPID</c> holds the <c>/bin/bash -c 'while …'</c>. One line of diagnostics
/// would have settled it in a single pass; the resource now carries that line whenever the compare
/// fails.
/// </para>
/// </remarks>
public sealed class ChromiumKioskRunningResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "unit.chromium-kiosk.running-matches-content";

    /// <summary>The unit property carrying the pid of the process systemd started.</summary>
    public const string MainPidProperty = "MainPID";

    /// <summary>The unit property carrying the coarse lifecycle state.</summary>
    public const string ActiveStateProperty = "ActiveState";

    /// <summary>The unit property carrying the fine-grained lifecycle state.</summary>
    public const string SubStateProperty = "SubState";

    /// <summary>
    /// The service sub-states in which systemd has not yet finished making the main process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Transcribed from systemd's <c>service_state_table[]</c> in <c>src/basic/unit-def.c</c> and
    /// from the <c>SERVICE_STATE_WITH_MAIN_PROCESS</c> predicate in <c>src/core/service.c</c>.
    /// <c>condition</c> and <c>start-pre</c> have no main process at all; <c>start</c> has one only
    /// for some <c>Type=</c> values, and none for <c>Type=forking</c>; <c>start-post</c> has one
    /// but the unit's own start-up is not complete.
    /// </para>
    /// <para>
    /// <b>What is deliberately absent is as important as what is here.</b> <c>auto-restart</c> and
    /// <c>auto-restart-queued</c> are <c>ActiveState=activating</c> and are <i>not</i> excused: a
    /// browser systemd is waiting to restart is a browser that is not running, which is precisely
    /// the fault this resource exists to report. Nor are the <c>stop*</c>, <c>final-*</c>,
    /// <c>dead</c>, <c>failed</c> or <c>cleaning</c> sub-states, all of which are answerable.
    /// </para>
    /// </remarks>
    public static IReadOnlySet<string> StartingSubStates { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "condition", "start-pre", "start", "start-post" };

    /// <summary>How much of a command line reaches an observation.</summary>
    /// <remarks>
    /// A delta travels to the journal, the frame's own screen and the Fleet Manager's device
    /// history, and a Chromium command line on this build runs to twenty-four arguments. Enough to
    /// identify the process beyond doubt is the requirement — <c>argv[0]</c> answers "is this even a
    /// browser" on its own — and the rest is truncated with the count kept, so the reading stays a
    /// sentence rather than a paragraph.
    /// </remarks>
    public const int DiagnosticArgumentLimit = 4;

    private readonly ISystemFiles _files;
    private readonly IUserSession _session;
    private readonly ChromiumKioskUnitResource _unit;

    /// <summary>Creates the resource.</summary>
    public ChromiumKioskRunningResource(
        ISystemFiles files,
        IUserSession session,
        ChromiumKioskUnitResource unit)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(unit);

        _files = files;
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

        var expected = $"a running browser carrying all {declared.Count} declared arguments";

        // After the ExecStart read, which takes a file and needs no session, and before the two
        // questions that do need one: which process this unit owns, and what it was started with.
        if (await UserSessionGate.NotSettledAsync(_session, expected, cancellationToken).ConfigureAwait(false)
            is { } waiting)
        {
            return waiting;
        }

        // One call, three properties. Asking for the phase in a second `systemctl show` would put a
        // gap between the two readings exactly as wide as the window this is closing, so the
        // snapshot has to be atomic — and systemd renders one `Property=value` line per property,
        // in the order asked, which is what PropertyIn reads.
        var shown = await _session
            .RunAsync(
                "systemctl",
                [
                    "--user",
                    "show",
                    ChromiumKioskUnitResource.UnitName,
                    "-p",
                    MainPidProperty,
                    "-p",
                    ActiveStateProperty,
                    "-p",
                    SubStateProperty,
                ],
                cancellationToken)
            .ConfigureAwait(false);

        if (MainPidIn(shown.StandardOutput) is not { } pid)
        {
            // Not "nothing is running" — nothing was asked successfully. The two have to stay
            // apart, or a user manager that would not answer reads as a dead browser and the Act
            // restarts something that was never observed.
            return new ResourceObservation(
                false,
                expected,
                $"the user manager did not say which process runs {ChromiumKioskUnitResource.UnitName}{Refusal(shown)}");
        }

        var subState = PropertyIn(shown.StandardOutput, SubStateProperty);
        var activeState = PropertyIn(shown.StandardOutput, ActiveStateProperty);

        if (subState is { } phase && StartingSubStates.Contains(phase))
        {
            // Not drift, and not a dead browser: the unit exists, systemd is working on it, and it
            // has not yet made the process this resource is about. Answering anything else here is
            // what restarted a browser 4.8 s from `Started`, three times, to an escalation.
            return ResourceObservation.Unevaluable(
                expected,
                $"{ChromiumKioskUnitResource.UnitName} has not finished starting "
                + $"({activeState ?? "in a state systemd did not report"}, {phase}), so which process "
                + "is the browser is not yet settled");
        }

        if (pid == 0)
        {
            return new ResourceObservation(false, expected, Absent(activeState, subState));
        }

        if (CommandLineOf(_files, pid) is not { Count: > 0 } running)
        {
            return new ResourceObservation(
                false,
                expected,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"no browser process is running (systemd names process {pid}, which has no command line to read)"));
        }

        var missing = MissingFrom(running, declared);

        // The failing branch names the process it actually read, not only what it did not find.
        // A verdict of "running without <every declared argument>" is ambiguous between a browser
        // launched wrong and a reading of something that was never the browser, and the record of
        // 2026-08-16 could not tell those apart because nothing wrote down what was read.
        return new ResourceObservation(
            missing.Count == 0,
            expected,
            missing.Count == 0
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"running as pid {pid} with all {declared.Count} declared arguments")
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"pid {pid} ({Summarise(running)}) running without {string.Join(", ", missing)}"));
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

    /// <summary>
    /// The pid in a <c>systemctl show -p MainPID</c> answer, or null when it carried none.
    /// </summary>
    /// <remarks>
    /// <b>Zero is a real reading and must not collapse into null.</b> systemd reports
    /// <c>MainPID=0</c> for a unit with no main process — stopped, failed, or still inside its
    /// <c>ExecStartPre</c> — which is "no browser process is running", a different fact from a user
    /// manager that could not be asked. Null is reserved for the second, on
    /// <see cref="ConsoleAutologinResource.ActiveStateIn"/>'s precedent: an answer that did not
    /// arrive is not evidence about the frame.
    /// </remarks>
    public static int? MainPidIn(string shown)
    {
        ArgumentNullException.ThrowIfNull(shown);

        const string Prefix = MainPidProperty + "=";

        foreach (var line in shown.Split('\n'))
        {
            if (line.StartsWith(Prefix, StringComparison.Ordinal)
                && int.TryParse(
                    line[Prefix.Length..].Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var pid)
                && pid >= 0)
            {
                return pid;
            }
        }

        return null;
    }

    /// <summary>
    /// The argument vector the kernel holds for <paramref name="pid"/>, or null when there is no
    /// such process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>/proc/&lt;pid&gt;/cmdline</c> is the kernel's own record of what a process was
    /// <c>execve</c>d with, so it is the one reading that cannot be stale and the one no wrapper
    /// script can sit in front of. Same file family and same read as
    /// <see cref="KioskChildEnvironment"/>, which takes the neighbouring <c>environ</c>.
    /// </para>
    /// <para>
    /// <b>NUL is the separator wherever the kernel wrote one</b>, and that is what makes this the
    /// exact vector rather than an approximation of it: a Chromium child carries
    /// <c>--enable-crash-reporter=,built on Debian GNU/Linux 13 (trixie)</c> on this build, and
    /// splitting a NUL-delimited block on whitespace turns that one argument into seven.
    /// </para>
    /// <para>
    /// <b>The file has a second shape, and Chromium is the reason this resource meets it.</b>
    /// <c>proc(5)</c> is explicit that <i>"if, after an <c>execve</c>, the process modifies its
    /// argv strings, those changes will show up here"</i> — the kernel serves the bytes between
    /// <c>arg_start</c> and <c>arg_end</c>, whatever the process has since put there. Chromium
    /// rewrites exactly that region: <c>set_process_title_linux.cc</c> implements
    /// <c>setproctitle</c> by overwriting the argv area with <b>one space-joined string</b> and
    /// NUL-padding the remainder, so the block a frame's kernel holds for the browser carries no
    /// NUL between two arguments at all.
    /// </para>
    /// <para>
    /// <b>Measured on the mule, 2026-08-16, from the frame's own screen.</b> With the reading fixed
    /// and the argv diagnostic in place, <c>unit.chromium-kiosk.running-matches-content</c> printed
    /// all twenty-five arguments inside one <see cref="Summarise"/> rendering whose limit is
    /// <see cref="DiagnosticArgumentLimit"/> — four — and no <i>"and N more arguments"</i> tail. A
    /// vector of twenty-five cannot render that way and a vector of one can, so the split had
    /// produced a single token holding the whole command line, and every one of the thirteen
    /// declared arguments was reported missing from a browser that was carrying all of them. The
    /// resource escalated at 3/3 and stopped the pass.
    /// </para>
    /// <para>
    /// <b>The read path was excluded first, by measurement rather than by reasoning.</b>
    /// <see cref="ISystemFiles"/> is text-oriented and <c>cmdline</c> is NUL-delimited binary, so
    /// the standing suspicion was that the NULs never survived <see cref="ISystemFiles.ReadText"/>.
    /// They do: a self-contained build reading real <c>/proc/&lt;pid&gt;/cmdline</c> on Linux
    /// returns every NUL, tokenises correctly, and keeps a space-bearing argument whole. The bytes
    /// on the frame genuinely have spaces where the separators should be, which is why the fix is
    /// here and not in the read.
    /// </para>
    /// <para>
    /// <b>So the two shapes are told apart by the kernel's own signature, not by preference.</b> An
    /// argv vector of two or more arguments always carries a NUL between them; a rewritten title
    /// never does. A single token with a space in it is therefore a title, and the space is the
    /// separator <c>setproctitle</c> put there — a title's only structure, not a second guess at a
    /// vector's. What that costs is real and is stated rather than hidden: an argument containing a
    /// space cannot be recovered from a rewritten block by anything. It costs this resource nothing
    /// today, because the crash-reporter argument belongs to a renderer and <c>MainPID</c> never
    /// names one.
    /// </para>
    /// <para>
    /// Absent and empty both answer null. A pid whose <c>cmdline</c> cannot be read is a pid with
    /// no browser behind it — a zombie, a process that exited between the two reads, an unreadable
    /// entry — and every one of those is "the process systemd named is not there to compare",
    /// which is drift and is reported as such rather than being distinguished further.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string>? CommandLineOf(ISystemFiles files, int pid)
    {
        ArgumentNullException.ThrowIfNull(files);

        var raw = files.ReadText(string.Create(CultureInfo.InvariantCulture, $"/proc/{pid}/cmdline"));

        if (raw is null)
        {
            return null;
        }

        var vector = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        return vector is [var title] && title.Contains(' ')
            ? title.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : vector;
    }

    /// <summary>
    /// Which declared arguments the running command line does not carry — containment, not
    /// equality.
    /// </summary>
    /// <remarks>
    /// Both sides are argument vectors and neither carries the executable: the declared side has it
    /// removed by <see cref="ChromiumKioskUnitResource.ExecStartArguments"/>, and the running side's
    /// <c>argv[0]</c> is simply never asked for. Arguments the unit did not declare are ignored by
    /// construction, which is the containment rule the class remarks measure.
    /// </remarks>
    public static IReadOnlyList<string> MissingFrom(
        IReadOnlyList<string> runningCommandLine,
        IReadOnlyList<string> declared)
    {
        ArgumentNullException.ThrowIfNull(runningCommandLine);
        ArgumentNullException.ThrowIfNull(declared);

        var present = new HashSet<string>(runningCommandLine, StringComparer.Ordinal);

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

    /// <summary>
    /// One <c>Property=value</c> line out of a <c>systemctl show</c> answer, or null.
    /// </summary>
    /// <remarks>
    /// Kept as a member here because this resource's callers read it, and delegating rather than
    /// holding a second body because <c>unit.fl-agent.running-matches-content</c> now asks the
    /// <i>system</i> manager for the same shape of answer. The parsing is the same either way —
    /// <c>systemctl show</c> renders one <c>Property=value</c> line per property whichever manager
    /// is answering — so what would have been coupled by sharing is nothing, and what is avoided is
    /// two readers that can drift.
    /// </remarks>
    public static string? PropertyIn(string shown, string name) => SystemdUnits.PropertyIn(shown, name);

    /// <summary>
    /// A bounded rendering of an argument vector, for the observation that reports a mismatch.
    /// </summary>
    /// <remarks>
    /// <c>argv[0]</c> always survives, because it is the argument that answers whether the process
    /// is a browser at all — <c>/usr/lib/chromium/chromium</c> and <c>/bin/bash</c> are different
    /// diagnoses with different fixes, and telling them apart is the whole reason this exists.
    /// </remarks>
    public static string Summarise(IReadOnlyList<string> commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);

        if (commandLine.Count == 0)
        {
            return "no arguments at all";
        }

        var head = string.Join(' ', commandLine.Take(DiagnosticArgumentLimit));

        return commandLine.Count <= DiagnosticArgumentLimit
            ? head
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{head} … and {commandLine.Count - DiagnosticArgumentLimit} more arguments");
    }

    /// <summary>
    /// "No browser process is running", with the phase systemd gave for it.
    /// </summary>
    /// <remarks>
    /// The bare sentence is quoted verbatim by <c>SupervisionInterlock</c> and by the tests that
    /// pin what a restart's transient looks like, so it stays the head of the string and the phase
    /// is added after it rather than folded into it.
    /// </remarks>
    private static string Absent(string? activeState, string? subState) =>
        activeState is null && subState is null
            ? "no browser process is running"
            : $"no browser process is running ({activeState ?? "state unreported"}, "
                + $"{subState ?? "sub-state unreported"})";

    /// <summary>What a refusing <c>systemctl</c> said, parenthesised, or nothing.</summary>
    private static string Refusal(ProcessResult result) =>
        result.Combined.Trim() is { Length: > 0 } text
            ? $" ({text.Split('\n')[0].Trim()})"
            : string.Empty;
}
