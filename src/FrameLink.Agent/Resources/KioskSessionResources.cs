using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Agent.Resources;

/// <summary>
/// <c>swap.zram-active</c> — the compressed swap Chromium needs is live.
/// </summary>
/// <remarks>
/// <para>
/// From guide 5 step 1, re-asserted by guide 12 step 5. The catalog calls it "assert-only in
/// practice; there is no Act beyond ensuring <c>rpi-swap</c> is installed and the generator's
/// config is untouched", and that is honoured here rather than worked around: the Act starts the
/// generator's own unit and nothing else. If the unit is not there, the Act reports the refusal
/// verbatim, the resource walks §2.5's ladder and an operator is told — which is the correct
/// outcome for a frame whose stock swap has been removed, and a far better one than an agent that
/// starts writing swap configuration nobody asked it to own.
/// </para>
/// <para>
/// Why it matters on this hardware: a 2 GB Pi 5 running a compositor, Chromium and WebRTC is
/// tight, and ~2 GB of in-RAM compressed swap is the headroom that keeps the browser from being
/// OOM-killed mid-call. It is the same pressure §2.10's <c>MemAvailable</c> floor watches from the
/// other side.
/// </para>
/// </remarks>
public sealed class SwapZramResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "swap.zram-active";

    /// <summary>The device <c>systemd-zram-generator</c> creates.</summary>
    public const string Device = "/dev/zram0";

    /// <summary>The generated unit that sets the device up.</summary>
    public const string GeneratorUnit = "systemd-zram-setup@zram0.service";

    private readonly IProcessRunner _processes;
    private readonly ISystemControl _systemControl;

    /// <summary>Creates the resource.</summary>
    public SwapZramResource(IProcessRunner processes, ISystemControl systemControl)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(systemControl);

        _processes = processes;
        _systemControl = systemControl;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public string Detected => "This frame is missing the extra memory headroom it needs.";

    /// <inheritdoc/>
    public string WhyItMatters => "Without it the browser can be shut down by the system in the middle of a call.";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var result = await _processes.RunAsync("swapon", ["--show"], cancellationToken).ConfigureAwait(false);

        foreach (var line in result.StandardOutput.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length > 0 && string.Equals(fields[0], Device, StringComparison.Ordinal))
            {
                return new ResourceObservation(true, $"{Device} active as swap", line.Trim());
            }
        }

        return new ResourceObservation(
            false,
            $"{Device} active as swap",
            result.StandardOutput.Length == 0 ? "no swap at all" : $"swap without {Device}: {result.StandardOutput.Replace('\n', ' ')}");
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var result = await _systemControl
            .RunAsync(["start", GeneratorUnit], cancellationToken)
            .ConfigureAwait(false);

        return new ResourceAction(
            $"systemctl start {GeneratorUnit}" + (result.Succeeded ? string.Empty : $" (refused: {result.Output})"),
            "Switching on the spare memory this frame keeps in reserve for the browser.");
    }
}

/// <summary>
/// <c>boot.autologin.getty-tty1</c> — tty1 logs the frame's user in without a password.
/// </summary>
/// <remarks>
/// <para>
/// From guide 5 step 3. <b>The whole user-unit layer hangs off this one file.</b> There is no
/// <c>loginctl enable-linger</c> anywhere in the v1 build, so the user's systemd manager exists
/// only because this autologin creates a session — and the compositor, the browser unit and the
/// camera node all live in that manager. A wrong username here does not produce a login prompt; it
/// produces a frame where every user unit below is <c>Blocked</c> and nothing on the screen
/// explains why, which is exactly why the catalog gives it its own resource and its own delta.
/// </para>
/// <para>
/// <b>Written directly, not through <c>raspi-config</c>.</b> Guide 5 uses
/// <c>raspi-config nonint do_boot_behaviour B2</c>, and the catalog names that tool as a
/// <b>competing owner</b>: any later boot-behaviour call rewrites or removes the drop-in. The
/// agent owns the file instead, so a drift caused by <c>raspi-config</c> is repaired on the next
/// pass rather than being invisible because both writers agree on the happy path.
/// </para>
/// <para>
/// <b>The empty first <c>ExecStart=</c> is load-bearing.</b> systemd requires it to clear the
/// value inherited from the template unit; a drop-in without it does not override anything, and
/// the file looks perfectly correct while doing nothing at all. That is why Observe reads the
/// <i>effective</i> <c>ExecStart</c> from systemd as well as the file — the two can disagree, and
/// when they do the file is the half that lies.
/// </para>
/// <para>
/// <b>And why <c>who</c> is the third check.</b> §2.4 forbids claiming "applied" from a successful
/// write, and the post-boot effect of this resource is a login session on tty1. A frame that boots
/// straight past the verify with the getty still starting spends at most one attempt and clears on
/// the retry thirty seconds later; a frame that never logs anyone in escalates to a person, which
/// is right, because nothing else on it will ever start.
/// </para>
/// </remarks>
public sealed class ConsoleAutologinResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "boot.autologin.getty-tty1";

    /// <summary>Where the drop-in goes.</summary>
    public const string DropInPath = "/etc/systemd/system/getty@tty1.service.d/autologin.conf";

    /// <summary>The unit the drop-in modifies.</summary>
    public const string UnitName = "getty@tty1.service";

    private readonly ISystemFiles _files;
    private readonly ISystemControl _systemControl;
    private readonly IProcessRunner _processes;
    private readonly IUserSession _session;

    /// <summary>Creates the resource.</summary>
    public ConsoleAutologinResource(
        ISystemFiles files,
        ISystemControl systemControl,
        IProcessRunner processes,
        IUserSession session)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(systemControl);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(session);

        _files = files;
        _systemControl = systemControl;
        _processes = processes;
        _session = session;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public string Detected => "This frame does not log itself in when it starts.";

    /// <inheritdoc/>
    public string WhyItMatters => "Nothing that draws on the screen can start until it does.";

    /// <summary>The drop-in text, verbatim from guide 5 step 3.</summary>
    public static string ContentFor(string user) =>
        "[Service]\n"
        + "ExecStart=\n"
        + $"ExecStart=-/sbin/agetty --autologin {user} --noclear %I $TERM\n";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var user = _session.UserName;
        var desired = ContentFor(user);
        var actual = _files.ReadText(DropInPath);

        var expected = $"{DropInPath} autologin {user}, systemd agrees, {user} on tty1";
        var wrong = new List<string>(3);

        if (!string.Equals(
                JournalStorageResource.ShortHash(actual ?? string.Empty),
                JournalStorageResource.ShortHash(desired),
                StringComparison.Ordinal))
        {
            wrong.Add(actual is null ? $"{DropInPath} absent" : $"{DropInPath} does not carry --autologin {user}");
        }

        var shown = await _systemControl
            .RunAsync(["show", UnitName, "-p", "ExecStart", "-p", "LoadState"], cancellationToken)
            .ConfigureAwait(false);

        if (shown.Output.Contains("LoadState=not-found", StringComparison.Ordinal))
        {
            // No console getty on this machine at all — a container, a virtual agent (§5.3). The
            // same shape as CpuGovernorResource finding no cpufreq policies: reporting drift would
            // put a machine that has no tty1 into a permanent repair loop over one it never had.
            return new ResourceObservation(true, expected, $"there is no {UnitName} on this machine");
        }

        if (!shown.Output.Contains($"--autologin {user}", StringComparison.Ordinal))
        {
            wrong.Add($"systemd runs {Effective(shown.Output)}");
        }

        var who = await _processes.RunAsync("who", [], cancellationToken).ConfigureAwait(false);
        if (!IsLoggedInOnTty1(who.StandardOutput, user))
        {
            wrong.Add($"nobody is logged in as {user} on tty1");
        }

        return new ResourceObservation(
            wrong.Count == 0,
            expected,
            wrong.Count == 0 ? expected : string.Join("; ", wrong));
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var user = _session.UserName;
        _files.WriteText(DropInPath, ContentFor(user));

        var reloaded = await _systemControl.RunAsync(["daemon-reload"], cancellationToken).ConfigureAwait(false);

        return new ResourceAction(
            $"write {DropInPath} (ExecStart=-/sbin/agetty --autologin {user} --noclear %I $TERM) and run systemctl daemon-reload"
                + (reloaded.Succeeded ? string.Empty : $" (refused: {reloaded.Output})"),
            $"Telling this frame to sign itself in as '{user}' as soon as it starts, so it can put something on the screen.");
    }

    /// <summary>Whether <c>who</c> shows <paramref name="user"/> holding tty1.</summary>
    public static bool IsLoggedInOnTty1(string whoOutput, string user)
    {
        ArgumentNullException.ThrowIfNull(whoOutput);

        foreach (var line in whoOutput.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length >= 2
                && string.Equals(fields[0], user, StringComparison.Ordinal)
                && string.Equals(fields[1], "tty1", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Effective(string shown)
    {
        foreach (var line in shown.Split('\n'))
        {
            if (line.StartsWith("ExecStart=", StringComparison.Ordinal))
            {
                return line.Trim();
            }
        }

        return "no ExecStart at all";
    }
}

/// <summary>
/// <c>session.bash-profile-exec-labwc</c> — the autologin shell becomes the compositor.
/// </summary>
/// <remarks>
/// <para>
/// From guide 5 step 4. Pi OS Lite has no display manager, so nothing starts a Wayland session on
/// its own; the login shell does it. <c>exec</c> rather than a plain call, so no orphan bash
/// lingers under the compositor.
/// </para>
/// <para>
/// <b>Both guards are load-bearing, and one of them protects the agent itself.</b> Without the
/// <c>"$(tty)" = "/dev/tty1"</c> test, <c>exec labwc</c> fires on <b>SSH logins</b> — which breaks
/// remote administration and, with it, the diagnostics channel §3.6 opens over that same socket.
/// The <c>-z "$WAYLAND_DISPLAY"</c> test is the re-entry guard for anything that re-sources the
/// profile inside a live session.
/// </para>
/// <para>
/// <b>Observe checks the compositor is running, not only that the file is right.</b> The catalog
/// asks for exactly that — "Verify — identical, plus <c>pgrep -x labwc</c> on a booted frame" —
/// and §2.3 makes Observe and Verify one implementation, so the process check is in both. It is
/// deliberately not §2.10 supervision: labwc has no restart policy to fail, because the thing that
/// restarts it is <c>getty@tty1</c> respawning the login that execs it, and a compositor that is
/// still absent five minutes later is a frame showing nothing, which is drift and which a reboot
/// genuinely repairs.
/// </para>
/// </remarks>
public sealed class BashProfileLabwcResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "session.bash-profile-exec-labwc";

    private readonly ISystemFiles _files;
    private readonly IProcessRunner _processes;
    private readonly IUserSession _session;

    /// <summary>Creates the resource.</summary>
    public BashProfileLabwcResource(ISystemFiles files, IProcessRunner processes, IUserSession session)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(session);

        _files = files;
        _processes = processes;
        _session = session;
    }

    /// <summary>The 118 bytes of guide 5 step 4, and the v1 reference file's exact size.</summary>
    public static string DesiredContent =>
        "[ -f ~/.profile ] && . ~/.profile\n"
        + "\n"
        + "if [ -z \"$WAYLAND_DISPLAY\" ] && [ \"$(tty)\" = \"/dev/tty1\" ]; then\n"
        + "    exec labwc\n"
        + "fi\n";

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn =>
        [PackageResource.Prefix + "labwc", ConsoleAutologinResource.ResourceName];

    /// <inheritdoc/>
    public string Detected => "This frame signs itself in but never starts the part that draws the screen.";

    /// <inheritdoc/>
    public string WhyItMatters => "Without it the frame sits at a blank text prompt forever.";

    /// <summary>Where the file lives for this frame's user.</summary>
    public string Path => _session.HomeDirectory + "/.bash_profile";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var path = Path;
        var actual = _files.ReadText(path);
        var expected = $"{path} {JournalStorageResource.ShortHash(DesiredContent)} and labwc running";
        var wrong = new List<string>(2);

        if (!string.Equals(
                JournalStorageResource.ShortHash(actual ?? string.Empty),
                JournalStorageResource.ShortHash(DesiredContent),
                StringComparison.Ordinal))
        {
            wrong.Add(actual is null ? $"{path} absent" : $"{path} {JournalStorageResource.ShortHash(actual)}");
        }

        var running = await _processes.RunAsync("pgrep", ["-x", "labwc"], cancellationToken).ConfigureAwait(false);
        if (!running.Succeeded)
        {
            wrong.Add("labwc is not running");
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

        return new ResourceAction(
            $"write {path} with the guarded 'exec labwc' block",
            "Telling this frame to start drawing on the screen the moment it signs itself in.");
    }
}

/// <summary>
/// <c>labwc.autostart.content</c> — what the compositor does the instant it starts.
/// </summary>
/// <remarks>
/// From guide 5 step 6. Two lines: rotate the panel, and start the browser unit. The rotation is
/// here rather than in labwc's own configuration because <c>rc.xml</c> has no output-transform
/// element at all — the transform has to come from <c>wlr-randr</c> as a one-shot or from
/// <c>kanshi</c> as a daemon, and a kiosk's configuration is static. The browser start is
/// deliberately redundant with <c>unit.chromium-kiosk.enabled</c>: either path alone brings the
/// browser up, and the catalog asks for that redundancy to be preserved on purpose rather than by
/// accident.
/// </remarks>
public sealed class LabwcAutostartResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "labwc.autostart.content";

    /// <summary>Fleet setting carrying the panel rotation (§3.4).</summary>
    public const string RotationSettingKey = "display.rotation";

    /// <summary>1280×800 landscape out of an 800×1280 panel, as guide 5 measured it.</summary>
    public const string DefaultRotation = "270";

    /// <summary>The DSI connector this build's panel appears on.</summary>
    public const string OutputName = "DSI-2";

    private readonly ISystemFiles _files;
    private readonly IUserSession _session;
    private readonly FleetValues _values;

    /// <summary>Creates the resource.</summary>
    public LabwcAutostartResource(ISystemFiles files, IUserSession session, FleetValues values)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(values);

        _files = files;
        _session = session;
        _values = values;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn =>
        [PackageResource.Prefix + "labwc", PackageResource.Prefix + "wlr-randr"];

    /// <inheritdoc/>
    public string Detected => "This frame is not told to turn its picture the right way round.";

    /// <inheritdoc/>
    public string WhyItMatters => "Without it the photos come out sideways and the browser never opens.";

    /// <summary>Where the file lives for this frame's user.</summary>
    public string Path => _session.HomeDirectory + "/.config/labwc/autostart";

    /// <summary>The rotation this frame is configured for.</summary>
    public string Rotation => _values.Get(RotationSettingKey, DefaultRotation);

    /// <summary>The file content, from guide 5 step 6.</summary>
    public string DesiredContent() =>
        $"wlr-randr --output {OutputName} --transform {Rotation}\n"
        + $"systemctl --user start {ChromiumKioskUnitResource.UnitName} &\n";

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
            $"{path} rotating {OutputName} by {Rotation} and starting the browser",
            actual is null ? $"{path} absent" : $"{path} {JournalStorageResource.ShortHash(actual)}"));
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var path = Path;
        _files.WriteText(path, DesiredContent());
        await _session.GiveToUserAsync(path, cancellationToken).ConfigureAwait(false);

        return new ResourceAction(
            $"write {path} (wlr-randr --output {OutputName} --transform {Rotation}; start {ChromiumKioskUnitResource.UnitName})",
            "Telling this frame to turn its picture the right way round and open the browser as soon as the screen is ready.");
    }
}

/// <summary>
/// <c>labwc.autostart.executable</c> — the mode bit without which labwc ignores the file.
/// </summary>
/// <remarks>
/// Its own resource because the guide names the distinct failure and it is a nasty one: labwc
/// <b>silently ignores</b> a non-executable autostart. Perfect content plus a missing mode bit
/// produces a frame that boots to a bare compositor with no rotation and no browser, and nothing
/// anywhere logs a complaint. A content check alone reports that frame as healthy.
/// </remarks>
public sealed class LabwcAutostartExecutableResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "labwc.autostart.executable";

    /// <summary>The v1 reference mode, and what <c>chmod +x</c> under <c>umask 002</c> produces.</summary>
    public const UnixFileMode DesiredMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly ISystemFiles _files;
    private readonly LabwcAutostartResource _autostart;

    /// <summary>Creates the resource.</summary>
    public LabwcAutostartExecutableResource(ISystemFiles files, LabwcAutostartResource autostart)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(autostart);

        _files = files;
        _autostart = autostart;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn => [LabwcAutostartResource.ResourceName];

    /// <inheritdoc/>
    public string Detected => "The instruction that starts this frame's screen is there but is not allowed to run.";

    /// <inheritdoc/>
    public string WhyItMatters => "The frame comes up with an empty screen and no message about why.";

    /// <inheritdoc/>
    public ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = _autostart.Path;
        var mode = _files.ModeOf(path);

        return ValueTask.FromResult(new ResourceObservation(
            mode is { } present && present.HasFlag(UnixFileMode.UserExecute),
            $"{path} executable by its owner",
            mode is null ? $"{path} absent" : $"{path} mode {Octal(mode.Value)}"));
    }

    /// <inheritdoc/>
    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = _autostart.Path;
        _files.SetMode(path, DesiredMode);

        return ValueTask.FromResult(new ResourceAction(
            $"chmod {Octal(DesiredMode)} {path}",
            "Allowing the instruction that starts this frame's screen to actually run."));
    }

    /// <summary>The mode as the three digits a person reads in <c>ls -l</c> output.</summary>
    public static string Octal(UnixFileMode mode) => Convert.ToString((int)mode & 0x1FF, 8).PadLeft(3, '0');
}

/// <summary>
/// <c>labwc.rc-xml.touch-map</c> — taps land where the picture is, not where the panel is.
/// </summary>
/// <remarks>
/// <para>
/// From guide 5 step 7. With <c>mapToOutput</c>, wlroots re-maps touch coordinates to whatever
/// transform the output carries, so no calibration matrix and no Waveshare-specific device
/// matching is needed. A misspelled output identifier fails <b>silently</b> to the identity
/// transform — the file parses, the compositor starts, and every tap lands ninety degrees away.
/// </para>
/// <para>
/// <b>Verify genuinely differs from Observe here, and the catalog says so.</b> The file is
/// observable post-boot; whether taps land on the right pixels is only observable by a human
/// touching the screen, because wlroots offers no readback of the applied touch mapping. This
/// resource verifies the file and nothing more, and correct touch geometry stays a
/// human-confirmed checkpoint. It is also the resource behind §2.7 item 4: the repair screen's
/// "Reboot now" button is the one place v1's touch shield must not block input.
/// </para>
/// </remarks>
public sealed class LabwcTouchMapResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "labwc.rc-xml.touch-map";

    private readonly ISystemFiles _files;
    private readonly IUserSession _session;

    /// <summary>Creates the resource.</summary>
    public LabwcTouchMapResource(ISystemFiles files, IUserSession session)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(session);

        _files = files;
        _session = session;
    }

    /// <summary>The file content, verbatim from guide 5 step 7.</summary>
    public static string DesiredContent =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
        + "<labwc_config>\n"
        + $"  <touch mapToOutput=\"{LabwcAutostartResource.OutputName}\"/>\n"
        + "</labwc_config>\n";

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn => [PackageResource.Prefix + "labwc"];

    /// <inheritdoc/>
    public string Detected => "Touches on this frame's screen are not tied to the picture.";

    /// <inheritdoc/>
    public string WhyItMatters => "Taps land in the wrong place, so nobody can press anything on screen.";

    /// <summary>Where the file lives for this frame's user.</summary>
    public string Path => _session.HomeDirectory + "/.config/labwc/rc.xml";

    /// <inheritdoc/>
    public ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = Path;
        var actual = _files.ReadText(path);

        var matches = string.Equals(
            JournalStorageResource.ShortHash(actual ?? string.Empty),
            JournalStorageResource.ShortHash(DesiredContent),
            StringComparison.Ordinal);

        return ValueTask.FromResult(new ResourceObservation(
            matches,
            $"{path} mapping touch to {LabwcAutostartResource.OutputName}",
            actual is null ? $"{path} absent" : $"{path} {JournalStorageResource.ShortHash(actual)}"));
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var path = Path;
        _files.WriteText(path, DesiredContent);
        await _session.GiveToUserAsync(path, cancellationToken).ConfigureAwait(false);

        return new ResourceAction(
            $"write {path} with <touch mapToOutput=\"{LabwcAutostartResource.OutputName}\"/>",
            "Tying taps on the glass to the picture on the screen, so pressing something presses what you see.");
    }
}

/// <summary>
/// <c>display.dsi2-transform</c> — the output is <i>actually</i> rotated.
/// </summary>
/// <remarks>
/// <para>
/// The post-boot effect of <see cref="LabwcAutostartResource"/>, and a separate resource for
/// exactly the reason §2.2's second granularity sub-rule gives: the setting and its effect can
/// disagree. A correct autostart with <c>Transform: normal</c> is a distinct diagnosis — a renamed
/// output, or <c>wlr-randr</c> running before labwc had finished bringing the output up — and it
/// has its own fix, which is why this one is independently actionable as a <c>wlr-randr</c>
/// one-shot.
/// </para>
/// <para>
/// It needs a live Wayland session, which on a booted frame always exists; when it does not, that
/// is <see cref="BashProfileLabwcResource"/> failing and the DAG marks this <c>Blocked</c> behind
/// it rather than letting it fail confusingly on its own.
/// </para>
/// </remarks>
public sealed class DisplayTransformResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "display.dsi2-transform";

    private readonly IUserSession _session;
    private readonly LabwcAutostartResource _autostart;

    /// <summary>Creates the resource.</summary>
    public DisplayTransformResource(IUserSession session, LabwcAutostartResource autostart)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(autostart);

        _session = session;
        _autostart = autostart;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn =>
    [
        LabwcAutostartResource.ResourceName,
        LabwcAutostartExecutableResource.ResourceName,
        BashProfileLabwcResource.ResourceName,
    ];

    /// <inheritdoc/>
    public string Detected => "This frame's picture is the wrong way round.";

    /// <inheritdoc/>
    public string WhyItMatters => "The photos and the video call appear sideways on the screen.";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var rotation = _autostart.Rotation;
        var result = await _session.RunAsync("wlr-randr", [], cancellationToken).ConfigureAwait(false);
        var observed = TransformIn(result.StandardOutput);

        return new ResourceObservation(
            string.Equals(observed, rotation, StringComparison.Ordinal),
            $"{LabwcAutostartResource.OutputName} transform {rotation}",
            observed is null
                ? $"wlr-randr reported no transform ({Summarise(result)})"
                : $"{LabwcAutostartResource.OutputName} transform {observed}");
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var rotation = _autostart.Rotation;
        var result = await _session
            .RunAsync("wlr-randr", ["--output", LabwcAutostartResource.OutputName, "--transform", rotation], cancellationToken)
            .ConfigureAwait(false);

        return new ResourceAction(
            $"wlr-randr --output {LabwcAutostartResource.OutputName} --transform {rotation}"
                + (result.Succeeded ? string.Empty : $" (refused: {Summarise(result)})"),
            "Turning this frame's picture the right way round now — the restart afterwards is what proves it stays that way.");
    }

    /// <summary>The transform <c>wlr-randr</c> reports, or null if it reported none.</summary>
    public static string? TransformIn(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Transform:", StringComparison.Ordinal))
            {
                return trimmed["Transform:".Length..].Trim();
            }
        }

        return null;
    }

    private static string Summarise(ProcessResult result) =>
        result.Combined.Length == 0 ? "no output" : result.Combined.Replace('\n', ' ');
}
