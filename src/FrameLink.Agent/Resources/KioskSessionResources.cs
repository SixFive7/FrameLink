using System.Globalization;
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
/// <b>And why the session is the third check.</b> §2.4 forbids claiming "applied" from a successful
/// write, and the post-boot effect of this resource is a login session on tty1. A frame that never
/// logs anyone in escalates to a person, which is right, because nothing else on it will ever
/// start.
/// </para>
/// <para>
/// <b>That session is read from logind, and it used to be read from <c>who</c>. The failure that
/// caused the change is not explained, and this paragraph says so rather than guessing a second
/// time.</b> On the first full provision this resource burned all five attempts, rebooted the frame
/// five times, escalated, and left <b>twelve</b> resources <c>Blocked</c> behind it — while the
/// drop-in was byte-correct, <c>systemd</c> had loaded it, and the only failing clause was
/// <i>"nobody is logged in as framelink on tty1"</i>. What was then measured on the frame:
/// <c>/run/utmp</c> is <b>absent</b> on Debian 13, and <c>who</c> <b>answers anyway</b>, exits 0,
/// and correctly prints a <c>framelink tty1</c> line — Debian's <c>who</c> has another source,
/// evidently logind. So the tidy explanation, that <c>who</c> reads a file this OS no longer has
/// and therefore can never answer, is <b>false</b>, and it was written into this file once already.
/// </para>
/// <para>
/// <b>What the evidence does support.</b> The verifies followed genuine reboots — the loop reaches
/// that message only when the boot id changed. The configuration that was in place for the last two
/// verifies is byte-identical to the one serving a working console session today, so the file is not
/// the difference. The environment is not the difference either: <c>systemctl</c> is launched by the
/// same <see cref="HostProcessRunner"/> from the same <c>PATH</c> in the same process, and its
/// clause <i>passed</i> on the very passes where this one failed, so a <c>/usr/bin</c> binary
/// demonstrably resolved. And the agent starts alongside the console getty rather than after it —
/// <c>agetty</c> at pid 918 against <c>fl-agent</c> at pid 930 on one boot, the getty at pid 906 on
/// another — while this verify is the first thing the loop does on a new boot, and the retry
/// backoffs are spent rebooting for other resources, so every sample this resource ever took landed
/// seconds after a boot.
/// </para>
/// <para>
/// <b>It is explained now, and it was the first candidate: every sample was taken before the login
/// had happened.</b> The frame's journal is persistent (guide 12), so the five failing boots were
/// still on the card and each one carries the agent's own verdict and the login beside it on the
/// same monotonic clock. Attempt 4, boot <c>-17</c>: the agent wrote <i>"did not survive the
/// reboot — nobody is logged in as framelink on tty1"</i> at <c>10.018670</c>, and
/// <c>login[921]: pam_unix(login:session): session opened for user framelink</c> landed at
/// <c>10.358098</c>, with <c>systemd-logind: New session 1 of user framelink</c> at
/// <c>10.370673</c>. The verdict was <b>352 ms early</b>. Attempt 5, boot <c>-13</c>: verdict at
/// <c>11.188142</c>, login at <c>11.631500</c>, session at <c>11.660292</c> — <b>472 ms early</b>.
/// </para>
/// <para>
/// It is not a coincidence of two boots. Across the thirty boots the card still holds,
/// <c>fl-agent.service</c> reaches <i>active</i> at 9.7–11.3 s and the console session is created
/// 0.52–0.89 s later, on <b>every single boot</b> — including the one this frame is running now.
/// The agent's first Observe is the first thing a pass does, within ~100–250 ms of the process
/// starting, so it lands inside that gap every time. That is why the resource failed on all five
/// attempts rather than on one, and why a frame whose console autologin has worked perfectly the
/// whole time burned five reboots, an escalation and twelve blocked dependents.
/// </para>
/// <para>
/// The other two candidates are retired by the same evidence. "The console genuinely was not
/// producing a session in that window" is <b>excluded</b>: the journal shows the session being
/// created 350–470 ms after each verdict, on the very boots that failed. "<c>who</c> never ran and
/// returned empty" is no longer <i>needed</i> — there was genuinely no session to find at that
/// instant, so a <c>who</c> that ran correctly would have printed exactly what was recorded. It is
/// not disproven, because the two are indistinguishable in the log; it simply explains nothing that
/// the timing does not already explain.
/// </para>
/// <para>
/// <b>Why logind is still the right observable, independent of all of that.</b> It is the authority
/// that owns session state rather than a reporting tool layered over it; it distinguishes <i>this</i>
/// login on tty1 from an administrator's SSH session, which <c>user@1000.service</c> being active
/// cannot; and a failed <c>loginctl</c> is now reported as a tool that would not answer rather than
/// as an absent session, which is the distinction the old code could not draw.
/// </para>
/// <para>
/// <b>The <c>ActiveState</c> gate, and why it was on the wrong side of the window.</b> A login
/// session is a runtime fact, and a runtime fact sampled at the wrong instant is exactly how a
/// correct configuration gets reported as drift. So the same <c>systemctl show</c> that reads the
/// effective <c>ExecStart</c> also reads <c>ActiveState</c>: while the unit is still
/// <c>activating</c> — which it genuinely is for up to five seconds of every boot, because
/// <c>getty@.service</c> is <c>Type=idle</c> — the absence of a session is not a verdict about
/// anything and is not counted. A getty that is <c>inactive</c> or <c>failed</c> is reported as
/// itself, because that will never log anybody in.
/// </para>
/// <para>
/// What that gate did not cover is the window the measurement found, and it is the larger one.
/// <c>Started getty@tty1.service</c> and <c>ActiveEnterTimestampMonotonic</c> agree to the
/// microsecond, so the unit is <i>active</i> from the moment <c>agetty</c> is exec'd — and
/// <c>agetty</c> then takes <b>4.77, 4.77, 4.89 and 4.94 seconds</b> on the four boots sampled in
/// full before <c>login</c> opens the PAM session it execs into. The agent starts 4.2–4.3 s after
/// the getty goes active, which is inside that window on every boot. So the whole failure happened
/// with <c>ActiveState=active</c>, three quarters of the way through a settling period the gate
/// said nothing about.
/// </para>
/// <para>
/// <b><see cref="SettleSeconds"/> covers it, and the number is the measurement's rather than a
/// taste.</b> When the durable pair is right, the getty is active, and no session has appeared yet,
/// the absence is counted only once the getty has been active longer than that — which is the same
/// verdict the <c>activating</c> branch already reaches, for the same reason, one state later.
/// Nothing sleeps and nothing retries: the pass carries on immediately, and the loop is
/// level-triggered, so a console that genuinely logs nobody in is asked again on the next drift
/// sweep, by which time the getty has been active for minutes and the window cannot reach it. The
/// delta still carries <b>how long the getty had been active</b> when no session was found, from
/// the unit's own <c>ActiveEnterTimestampMonotonic</c> against <c>/proc/uptime</c>, because that is
/// the number that made this diagnosable and it is the number that will make the next one
/// diagnosable too.
/// </para>
/// <para>
/// An age that cannot be computed is <b>not</b> gated. Both halves come from the machine, and a
/// frame that cannot answer either has given no evidence that its sample was early — so the
/// absence is counted exactly as it was before this window existed, which is the direction that
/// fails towards a visible escalation rather than towards silence.
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

    /// <summary>The terminal that unit owns, as <c>loginctl</c> spells it.</summary>
    public const string Tty = "tty1";

    /// <summary>Where the kernel publishes how long this boot has lasted.</summary>
    public const string UptimePath = "/proc/uptime";

    /// <summary>
    /// How long an active getty is allowed to have no session on it before that counts (§2.6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Thirty, from two numbers rather than from taste.</b> The floor is the measurement: on the
    /// mule, <c>agetty</c> takes 4.77–4.94 s from the unit going active to <c>login</c> opening the
    /// PAM session, four samples with 0.18 s of spread. Thirty is six times the slowest of those,
    /// so it is not a threshold fitted to the sample it came from.
    /// </para>
    /// <para>
    /// The ceiling is <see cref="Reconcile.ReconcileOptions.PassInterval"/>, five minutes: this
    /// window has to be small against it, or a console that logs nobody in could hide inside the
    /// window across successive sweeps instead of being caught by the next one. At a tenth of the
    /// sweep, the longest a real fault can stay unreported is one pass.
    /// </para>
    /// <para>
    /// It buys the wrong answer in one direction only, and it is the survivable one. A frame whose
    /// console is genuinely broken reads healthy for the first thirty seconds of each boot and is
    /// then reported — the durable pair is still checked on every single observation, and that pair
    /// is what decides whether the console logs anybody in. The opposite error is the one this
    /// resource has already paid for: five reboots, an escalation and twelve blocked dependents on
    /// a frame that was logging itself in correctly the whole time.
    /// </para>
    /// </remarks>
    public const long SettleSeconds = 30;

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

        var expected = $"{DropInPath} autologin {user}, systemd agrees, a logind session for {user} on {Tty}";
        var wrong = new List<string>(3);

        if (!string.Equals(
                JournalStorageResource.ShortHash(actual ?? string.Empty),
                JournalStorageResource.ShortHash(desired),
                StringComparison.Ordinal))
        {
            wrong.Add(actual is null ? $"{DropInPath} absent" : $"{DropInPath} does not carry --autologin {user}");
        }

        var shown = await _systemControl
            .RunAsync(
                ["show", UnitName, "-p", "ExecStart", "-p", "LoadState", "-p", "ActiveState", "-p", "ActiveEnterTimestampMonotonic"],
                cancellationToken)
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

        var state = ActiveStateIn(shown.Output);
        string? settling = null;

        if (state is null or "activating" or "reloading")
        {
            // getty@.service is Type=idle, so on every boot there is a window — up to five seconds
            // — in which the unit exists, is correct, and has not run agetty yet. There is no
            // session to find in that window and its absence says nothing, so it is not counted.
            // What carries the verdict instead is the pair above, and they are the durable pair: a
            // file that survived the boot and the value systemd actually loaded from it. Nothing
            // hides here — the loop is level-triggered, so a frame whose login is genuinely broken
            // is asked again on the next pass, by which time the unit has certainly settled.
            return new ResourceObservation(
                wrong.Count == 0,
                expected,
                wrong.Count == 0
                    ? $"{DropInPath} autologin {user}, systemd agrees, {UnitName} is {state ?? "in a state systemd did not report"} and has not run its login yet"
                    : string.Join("; ", wrong));
        }

        if (!string.Equals(state, "active", StringComparison.Ordinal))
        {
            // Not a missing session — a missing getty. It will never log anybody in, and that is a
            // different diagnosis from a login that was attempted and did not take.
            wrong.Add($"{UnitName} is {state}");
        }
        else
        {
            var sessions = await _processes
                .RunAsync("loginctl", ["list-sessions", "--no-legend"], cancellationToken)
                .ConfigureAwait(false);

            if (!HasSessionOnTty1(sessions.StandardOutput, user))
            {
                var age = ActiveForSeconds(shown.Output, _files.ReadText(UptimePath));

                if (!sessions.Succeeded)
                {
                    // A tool that would not answer is not an absent session, and never was.
                    wrong.Add($"logind would not say which sessions exist ({Summarise(sessions)})");
                }
                else if (age is { } seconds && seconds < SettleSeconds)
                {
                    // Inside the settling window the measurement found: the getty is active
                    // because agetty has been exec'd, and agetty is still on its way to login.
                    // Sampled here the absence says nothing about the setting, exactly as it says
                    // nothing while the unit is still activating — so it is recorded and not
                    // counted, and the durable pair above carries the verdict.
                    settling = string.Create(
                        CultureInfo.InvariantCulture,
                        $"{DropInPath} autologin {user}, systemd agrees, {UnitName} has been active for {seconds}s and its login has not opened a session yet");
                }
                else
                {
                    // Past the window, or with no age to judge it by. This is the fault the third
                    // clause exists for: nothing that draws on the screen will ever start.
                    wrong.Add($"logind has no session for {user} on {Tty}{Age(age)}");
                }
            }
        }

        return new ResourceObservation(
            wrong.Count == 0,
            expected,
            wrong.Count > 0 ? string.Join("; ", wrong) : settling ?? expected);
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

    /// <summary>
    /// Whether <c>loginctl list-sessions</c> shows <paramref name="user"/> holding
    /// <see cref="Tty"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matched on <i>fields</i> rather than on column positions, because the columns have moved:
    /// <c>SESSION UID USER SEAT TTY</c> grew <c>STATE</c> and <c>IDLE</c> in later systemd, and a
    /// parser pinned to index 4 would start reporting a healthy frame as drifted on an OS upgrade —
    /// which is the same class of fault this whole change exists to remove. A line qualifies when
    /// it carries the user name and <see cref="Tty"/> as whole fields.
    /// </para>
    /// <para>
    /// That pair is also what makes the check specific. An SSH session for the same user is listed
    /// with its pty (<c>pts/0</c>) and never matches, so an administrator logged in over the
    /// network cannot make a frame whose console autologin is broken look healthy — which is
    /// exactly when somebody would be logged in to find out why.
    /// </para>
    /// </remarks>
    public static bool HasSessionOnTty1(string sessions, string user)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        foreach (var line in sessions.Split('\n'))
        {
            var named = false;
            var onTty = false;

            foreach (var field in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                named = named || string.Equals(field, user, StringComparison.Ordinal);

                // The bare name is what loginctl prints. The device path is accepted beside it
                // because a field ending in /tty1 cannot be anything except tty1, so the widening
                // admits no false positive — and the failure it insures against is the one this
                // resource has already paid five reboots for once.
                onTty = onTty
                    || string.Equals(field, Tty, StringComparison.Ordinal)
                    || field.EndsWith("/" + Tty, StringComparison.Ordinal);
            }

            if (named && onTty)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The <c>ActiveState</c> in a <c>systemctl show</c> answer, or null when it was not reported.
    /// </summary>
    /// <remarks>
    /// Null is a real answer and is treated as "not settled" rather than as a fault: the only way
    /// to reach it is <c>systemctl show</c> failing to answer at all, and a frame in that condition
    /// has already failed the <c>ExecStart</c> clause above, which is a better diagnosis than
    /// anything this line could add.
    /// </remarks>
    public static string? ActiveStateIn(string shown)
    {
        ArgumentNullException.ThrowIfNull(shown);

        return Value(shown, "ActiveState");
    }

    /// <summary>
    /// How many seconds the unit had been active, from its own activation timestamp against
    /// <c>/proc/uptime</c> — or null when either number is missing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both are the kernel's and systemd's own numbers rather than anything the agent times, so
    /// they survive the reboot every resource takes and mean the same thing on the far side of it.
    /// They are not the same clock — <c>/proc/uptime</c> counts suspended time and systemd's
    /// monotonic does not — which on a frame that never suspends is a distinction without a
    /// difference, and this value is only ever read to the nearest second.
    /// </para>
    /// <para>
    /// Null rather than zero for "cannot tell", on <see cref="Supervise.ProcMemoryProbe"/>'s
    /// precedent: zero is a real and meaningful reading here — a unit that went active this instant
    /// is the whole thing worth distinguishing — so it must not double as the absence of one.
    /// </para>
    /// </remarks>
    public static long? ActiveForSeconds(string shown, string? uptime)
    {
        ArgumentNullException.ThrowIfNull(shown);

        if (Value(shown, "ActiveEnterTimestampMonotonic") is not { } stamp
            || !long.TryParse(stamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds)
            || microseconds <= 0)
        {
            return null;
        }

        var first = uptime?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (first is not { Length: > 0 }
            || !double.TryParse(first[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var booted))
        {
            return null;
        }

        var seconds = (long)(booted - (microseconds / 1_000_000.0));

        return seconds < 0 ? null : seconds;
    }

    /// <summary>The age clause of the delta, or nothing when it could not be computed.</summary>
    /// <remarks>
    /// Takes the value rather than recomputing it, because the same number decides whether the
    /// absence is counted at all (<see cref="SettleSeconds"/>). Reading <c>/proc/uptime</c> a
    /// second time to print what a first read had already judged is how a delta ends up
    /// contradicting the verdict it accompanies.
    /// </remarks>
    private static string Age(long? seconds) =>
        seconds is { } value
            ? string.Create(CultureInfo.InvariantCulture, $" ({UnitName} active for {value}s)")
            : string.Empty;

    private static string? Value(string shown, string name)
    {
        var prefix = name + "=";

        foreach (var line in shown.Split('\n'))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line[prefix.Length..].Trim();
            }
        }

        return null;
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

    private static string Summarise(ProcessResult result) =>
        result.Combined.Length == 0 ? "no answer" : result.Combined.Replace('\n', ' ').Trim();
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

        // The durable half is checked on every observation whatever the session is doing, exactly as
        // boot.autologin.getty-tty1's window forgives one clause and one only: a .bash_profile with
        // the wrong bytes will never start a compositor, and that is true ten seconds into a boot as
        // much as ten minutes in. Only the runtime half is gated, and only when the file is right —
        // so a real fault still escalates and a frame mid-boot no longer does.
        if (wrong.Count == 0
            && await UserSessionGate.NotSettledAsync(_session, expected, cancellationToken).ConfigureAwait(false)
                is { } waiting)
        {
            return waiting;
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

        // wlr-randr needs the compositor, and the compositor needs the session. Without one it
        // reports no transform at all, which this resource would otherwise read as the panel being
        // the wrong way round.
        if (await UserSessionGate
                .NotSettledAsync(_session, $"{LabwcAutostartResource.OutputName} transform {rotation}", cancellationToken)
                .ConfigureAwait(false) is { } waiting)
        {
            return waiting;
        }

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
