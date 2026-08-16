using FrameLink.Agent.Hosting;
using FrameLink.Agent.Local;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;

namespace FrameLink.Tests;

/// <summary>
/// The catalog's session and kiosk stack — guides 5 and 10, the block where the frame stops
/// showing a console and starts showing the product.
/// </summary>
/// <remarks>
/// Every resource here is exercised against the shipping <see cref="HostSystemFiles"/> rooted at a
/// throwaway directory, so the bytes, the paths and the SHA-256 comparisons are the ones a frame
/// would produce. The two surfaces that cannot be pointed at a directory — the login user's
/// systemd manager and the process table — are scripted through
/// <see cref="FakeUserSession"/> and <see cref="RecordingProcessRunner"/>, which is what makes the
/// resources' own parsing the thing under test rather than a stand-in that agrees by construction.
/// </remarks>
public sealed class AgentKioskStackTests
{
    private static readonly CancellationToken None = TestContext.Current.CancellationToken;

    /// <summary>The one <c>systemctl show</c> the autologin resource makes, verbatim.</summary>
    private const string ShowGetty =
        "show getty@tty1.service -p ExecStart -p LoadState -p ActiveState -p ActiveEnterTimestampMonotonic";

    /// <summary>How the session is asked for on an OS that no longer has <c>/run/utmp</c>.</summary>
    private const string ListSessions = "loginctl list-sessions --no-legend";

    /// <summary>A getty that has the drop-in loaded and is up, active since 4.2 s into the boot.</summary>
    private const string AutologinLoaded =
        "ExecStart={ path=/sbin/agetty ; argv[]=/sbin/agetty --autologin framelink --noclear %I $TERM ; ignore_errors=yes }\n"
        + "LoadState=loaded\n"
        + "ActiveState=active\n"
        + "ActiveEnterTimestampMonotonic=4200000";

    /// <summary>What <c>loginctl list-sessions --no-legend</c> prints on a frame that logged in.</summary>
    private const string LoggedInOnTty1 = "      1 1000 framelink seat0 tty1  active no   -";

    /// <summary>
    /// The one question the running-browser resource asks the user manager, verbatim.
    /// </summary>
    /// <remarks>
    /// Three properties in one call, and the test pins that it is one call: asking for the phase
    /// separately from the pid would put a gap between them exactly as wide as the ~4.5 s
    /// <c>ExecStartPre</c> window the phase is read to close.
    /// </remarks>
    private const string ShowMainPid =
        "systemctl --user show chromium-kiosk.service -p MainPID -p ActiveState -p SubState";

    /// <summary>What the user manager prints for a unit that is up and drawing.</summary>
    private static string Running(int pid) =>
        $"MainPID={pid}\nActiveState=active\nSubState=running";

    /// <summary>
    /// The kiosk browser's command line as <c>/proc/1253/cmdline</c> carried it on the mule,
    /// 2026-08-16, with the frame healthy and drawing.
    /// </summary>
    /// <remarks>
    /// Verbatim, and being verbatim is the whole point of it. <c>argv[0]</c> is
    /// <c>/usr/lib/chromium/chromium</c> because <c>/usr/bin/chromium</c> — the path the unit
    /// declares — is a shell script that <c>exec</c>s it, and the eleven flags in front of the
    /// declared twelve come from that wrapper and from <c>rpi-chromium-mods</c>. Nothing here is
    /// constructed to make a test pass: this is what the kernel held while the resource was
    /// reporting the browser absent on every boot.
    /// </remarks>
    private const string MeasuredBrowserCommandLine =
        "/usr/lib/chromium/chromium --force-renderer-accessibility --enable-remote-extensions "
        + "--show-component-extension-options --enable-gpu-rasterization --no-default-browser-check "
        + "--disable-pings --media-router=0 --disable-dev-shm-usage --enable-remote-extensions "
        + "--load-extension --use-angle=gles --ozone-platform=wayland "
        + "--user-data-dir=/tmp/framelink-chromium --kiosk --noerrdialogs --disable-infobars "
        + "--disable-session-crashed-bubble --no-first-run "
        + "--auto-accept-camera-and-microphone-capture --enable-features=UsePipeWireCamera "
        + "--autoplay-policy=no-user-gesture-required --disable-background-timer-throttling "
        + "--disable-renderer-backgrounding http://127.0.0.1:8888/";

    [Fact]
    public async Task Autologin_writes_the_drop_in_with_the_empty_ExecStart_that_makes_it_override()
    {
        using var files = new TemporaryFiles();
        var systemd = new RecordingSystemControl();
        var processes = new RecordingProcessRunner();
        var session = new FakeUserSession();
        var resource = new ConsoleAutologinResource(files.Files, systemd, processes, session);

        await resource.ActAsync(None);

        var written = files.Read(ConsoleAutologinResource.DropInPath);

        // systemd requires the bare `ExecStart=` to clear the value inherited from the template.
        // A drop-in without it parses, looks right, and overrides nothing.
        Assert.Equal(
            "[Service]\nExecStart=\nExecStart=-/sbin/agetty --autologin framelink --noclear %I $TERM\n",
            written);
        Assert.Contains("daemon-reload", systemd.Commands);
    }

    [Fact]
    public async Task Autologin_is_drifted_when_systemd_runs_something_other_than_the_drop_in()
    {
        using var files = new TemporaryFiles();
        var systemd = new RecordingSystemControl();
        var processes = new RecordingProcessRunner();
        var session = new FakeUserSession();
        var resource = new ConsoleAutologinResource(files.Files, systemd, processes, session);

        files.Seed(ConsoleAutologinResource.DropInPath, ConsoleAutologinResource.ContentFor("framelink"));

        // The file is perfect and systemd is still running the stock agetty — the exact state a
        // drop-in missing its empty ExecStart produces, and one a content compare calls healthy.
        systemd.Answer(ShowGetty,
            "ExecStart=/sbin/agetty -o -p -- \\u --noclear - $TERM\nLoadState=loaded\nActiveState=active");
        processes.Answers[ListSessions] = new ProcessResult(0, LoggedInOnTty1, string.Empty);

        var observation = await resource.ObserveAsync(None);

        Assert.False(observation.InSync);
        Assert.Contains("systemd runs", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Autologin_reads_the_session_from_logind_rather_than_from_who()
    {
        using var files = new TemporaryFiles();
        var systemd = new RecordingSystemControl();
        var processes = new RecordingProcessRunner();
        var resource = new ConsoleAutologinResource(files.Files, systemd, processes, new FakeUserSession());

        files.Seed(ConsoleAutologinResource.DropInPath, ConsoleAutologinResource.ContentFor("framelink"));
        systemd.Answer(ShowGetty, AutologinLoaded);
        processes.Answers[ListSessions] = new ProcessResult(0, LoggedInOnTty1, string.Empty);

        var observation = await resource.ObserveAsync(None);

        Assert.True(observation.InSync);

        // `who` is gone from this resource because it was the fragile half of an observation that
        // burned five reboots, an escalation and twelve blocked dependents on a correctly
        // configured frame. It is NOT gone because it cannot work: measured on the frame
        // afterwards, /run/utmp is absent and `who` answers anyway, exits 0 and prints a correct
        // `framelink tty1` line. That failure is not explained. What logind buys is not a fix for a
        // known cause — it is the authority that owns session state, and a session carrying both
        // the user and tty1 is the console autologin specifically.
        Assert.DoesNotContain(processes.Commands, command => command.StartsWith("who", StringComparison.Ordinal));
        Assert.Contains(ListSessions, processes.Commands);
    }

    [Fact]
    public async Task A_missing_session_records_how_long_the_getty_had_been_up()
    {
        using var files = new TemporaryFiles();
        var systemd = new RecordingSystemControl();
        var processes = new RecordingProcessRunner();
        var resource = new ConsoleAutologinResource(files.Files, systemd, processes, new FakeUserSession());

        files.Seed(ConsoleAutologinResource.DropInPath, ConsoleAutologinResource.ContentFor("framelink"));
        // Active since 4.2 s into a boot that is now 51.2 s old.
        files.Seed(ConsoleAutologinResource.UptimePath, "51.20 402.11\n");
        systemd.Answer(ShowGetty, AutologinLoaded);
        processes.Answers[ListSessions] = new ProcessResult(0, string.Empty, string.Empty);

        var drifted = await resource.ObserveAsync(None);

        // The number nobody had when this resource escalated, and the one that turned out to
        // explain it. 47 s is well past the settling window, so the absence is counted and the age
        // travels with it — which is what makes the next occurrence diagnosable from the delta
        // alone rather than from thirty boots of journal.
        Assert.False(drifted.InSync);
        Assert.Contains("getty@tty1.service active for 47s", drifted.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_getty_that_has_only_just_gone_active_is_not_drift_and_says_how_long_it_has_been_up()
    {
        using var files = new TemporaryFiles();
        var systemd = new RecordingSystemControl();
        var processes = new RecordingProcessRunner();
        var resource = new ConsoleAutologinResource(files.Files, systemd, processes, new FakeUserSession());

        files.Seed(ConsoleAutologinResource.DropInPath, ConsoleAutologinResource.ContentFor("framelink"));

        // The exact instant this resource failed five times running. Active since 4.2 s into a boot
        // that is now 10.0 s old, which is where the agent's first Observe lands: measured on the
        // mule, fl-agent reaches active 4.2-4.3 s after the getty does and the console session is
        // created 0.52-0.89 s later still. The old code called this window drift, acted, rebooted,
        // and landed in the identical window on the next boot — five times, then escalated, with
        // twelve resources blocked behind it on a frame that was logging itself in correctly.
        files.Seed(ConsoleAutologinResource.UptimePath, "10.02 60.11\n");
        systemd.Answer(ShowGetty, AutologinLoaded);
        processes.Answers[ListSessions] = new ProcessResult(0, string.Empty, string.Empty);

        var observation = await resource.ObserveAsync(None);

        Assert.True(observation.InSync);
        Assert.Contains("has been active for 5s", observation.Observed, StringComparison.Ordinal);
        Assert.Contains("has not opened a session yet", observation.Observed, StringComparison.Ordinal);

        // logind is still asked. Not counting the answer is a verdict about this instant; skipping
        // the question would make the window a place where the check simply stops existing.
        Assert.Contains(ListSessions, processes.Commands);
    }

    [Fact]
    public async Task The_settling_window_ends_and_the_same_frame_is_then_reported()
    {
        // One boundary, both sides, so the window is a threshold rather than an escape hatch. The
        // ceiling is PassInterval: at a tenth of the five-minute drift sweep, the longest a console
        // that logs nobody in can stay unreported is one pass.
        Assert.Equal(30, ConsoleAutologinResource.SettleSeconds);

        Assert.True(await Settled("33.90 200.00"), "29s inside the window is not drift");
        Assert.False(await Settled("34.90 200.00"), "30s is outside the window and is drift");

        static async Task<bool> Settled(string uptime)
        {
            using var files = new TemporaryFiles();
            var systemd = new RecordingSystemControl();
            var processes = new RecordingProcessRunner();
            var resource = new ConsoleAutologinResource(files.Files, systemd, processes, new FakeUserSession());

            files.Seed(ConsoleAutologinResource.DropInPath, ConsoleAutologinResource.ContentFor("framelink"));
            files.Seed(ConsoleAutologinResource.UptimePath, uptime + "\n");
            systemd.Answer(ShowGetty, AutologinLoaded);
            processes.Answers[ListSessions] = new ProcessResult(0, string.Empty, string.Empty);

            return (await resource.ObserveAsync(None)).InSync;
        }
    }

    [Fact]
    public async Task A_wrong_drop_in_is_still_drift_inside_the_settling_window()
    {
        using var files = new TemporaryFiles();
        var systemd = new RecordingSystemControl();
        var processes = new RecordingProcessRunner();
        var resource = new ConsoleAutologinResource(files.Files, systemd, processes, new FakeUserSession());

        // The window forgives one clause and one only. The durable pair — the bytes on disk and the
        // ExecStart systemd actually loaded — is what decides whether this console will ever log
        // anybody in, so it is checked on every observation whatever the clock says.
        files.Seed(ConsoleAutologinResource.UptimePath, "10.02 60.11\n");
        systemd.Answer(ShowGetty, AutologinLoaded);
        processes.Answers[ListSessions] = new ProcessResult(0, string.Empty, string.Empty);

        var drifted = await resource.ObserveAsync(None);

        Assert.False(drifted.InSync);
        Assert.Contains("autologin.conf absent", drifted.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_age_that_cannot_be_computed_is_not_gated()
    {
        using var files = new TemporaryFiles();
        var systemd = new RecordingSystemControl();
        var processes = new RecordingProcessRunner();
        var resource = new ConsoleAutologinResource(files.Files, systemd, processes, new FakeUserSession());

        files.Seed(ConsoleAutologinResource.DropInPath, ConsoleAutologinResource.ContentFor("framelink"));
        systemd.Answer(ShowGetty, AutologinLoaded);
        processes.Answers[ListSessions] = new ProcessResult(0, string.Empty, string.Empty);

        // No /proc/uptime, so there is no age — and therefore no evidence that this sample was
        // early. A window that opened on "cannot tell" would be a place for a real fault to go and
        // be quiet on exactly the machines least able to report it.
        var drifted = await resource.ObserveAsync(None);

        Assert.False(drifted.InSync);
        Assert.Contains("logind has no session for framelink on tty1", drifted.Observed, StringComparison.Ordinal);
        Assert.DoesNotContain("active for", drifted.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void The_getty_age_is_systemds_own_number_against_the_kernels()
    {
        Assert.Equal(47, ConsoleAutologinResource.ActiveForSeconds("ActiveEnterTimestampMonotonic=4200000", "51.20 402.11"));

        // A frame that cannot answer either half says nothing rather than claiming zero, which is
        // itself the meaningful reading: a getty that went active this instant.
        Assert.Null(ConsoleAutologinResource.ActiveForSeconds("ActiveEnterTimestampMonotonic=0", "51.20 402.11"));
        Assert.Null(ConsoleAutologinResource.ActiveForSeconds("ActiveState=active", "51.20 402.11"));
        Assert.Null(ConsoleAutologinResource.ActiveForSeconds("ActiveEnterTimestampMonotonic=4200000", null));
        Assert.Null(ConsoleAutologinResource.ActiveForSeconds("ActiveEnterTimestampMonotonic=4200000", "not a number"));
        Assert.Equal(0, ConsoleAutologinResource.ActiveForSeconds("ActiveEnterTimestampMonotonic=4200000", "4.30 9.10"));
    }

    [Fact]
    public async Task Autologin_is_drifted_when_logind_has_no_session_on_tty1()
    {
        using var files = new TemporaryFiles();
        var systemd = new RecordingSystemControl();
        var processes = new RecordingProcessRunner();
        var resource = new ConsoleAutologinResource(files.Files, systemd, processes, new FakeUserSession());

        files.Seed(ConsoleAutologinResource.DropInPath, ConsoleAutologinResource.ContentFor("framelink"));
        systemd.Answer(ShowGetty, AutologinLoaded);

        // A settled getty with nothing logged in on its terminal is the fault the third clause is
        // for: the file is right, systemd loaded it, and nothing that draws on the screen will ever
        // start. Escalating to a person is the correct end of that.
        processes.Answers[ListSessions] = new ProcessResult(0, string.Empty, string.Empty);

        var drifted = await resource.ObserveAsync(None);

        Assert.False(drifted.InSync);
        Assert.Contains("logind has no session for framelink on tty1", drifted.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_administrators_ssh_session_is_not_a_console_login()
    {
        using var files = new TemporaryFiles();
        var systemd = new RecordingSystemControl();
        var processes = new RecordingProcessRunner();
        var resource = new ConsoleAutologinResource(files.Files, systemd, processes, new FakeUserSession());

        files.Seed(ConsoleAutologinResource.DropInPath, ConsoleAutologinResource.ContentFor("framelink"));
        systemd.Answer(ShowGetty, AutologinLoaded);

        // Somebody logged in over the network to find out why the frame is dark is the same user
        // with the same uid, and logind lists them with their pty. If that counted, the resource
        // would report healthy exactly while a person was standing there proving it was not.
        processes.Answers[ListSessions] = new ProcessResult(0, "      3 1000 framelink -     pts/0", string.Empty);

        var drifted = await resource.ObserveAsync(None);

        Assert.False(drifted.InSync);
        Assert.Contains("logind has no session for framelink on tty1", drifted.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_getty_that_has_not_started_yet_is_not_drift_and_is_not_asked_about_sessions()
    {
        using var files = new TemporaryFiles();
        var systemd = new RecordingSystemControl();
        var processes = new RecordingProcessRunner();
        var resource = new ConsoleAutologinResource(files.Files, systemd, processes, new FakeUserSession());

        files.Seed(ConsoleAutologinResource.DropInPath, ConsoleAutologinResource.ContentFor("framelink"));

        // getty@.service is Type=idle, so for up to five seconds of every boot the unit is loaded,
        // correct, and has not run agetty yet. A session sampled in that window is absent for a
        // reason that says nothing about this setting — and a resource that called it drift would
        // act, reboot, and land in the identical window on the next boot, forever.
        systemd.Answer(ShowGetty, AutologinLoaded.Replace("ActiveState=active", "ActiveState=activating", StringComparison.Ordinal));

        var observation = await resource.ObserveAsync(None);

        Assert.True(observation.InSync);
        Assert.Contains("has not run its login yet", observation.Observed, StringComparison.Ordinal);
        Assert.DoesNotContain(ListSessions, processes.Commands);
    }

    [Fact]
    public async Task A_getty_that_is_not_running_is_reported_as_itself()
    {
        using var files = new TemporaryFiles();
        var systemd = new RecordingSystemControl();
        var processes = new RecordingProcessRunner();
        var resource = new ConsoleAutologinResource(files.Files, systemd, processes, new FakeUserSession());

        files.Seed(ConsoleAutologinResource.DropInPath, ConsoleAutologinResource.ContentFor("framelink"));
        systemd.Answer(ShowGetty, AutologinLoaded.Replace("ActiveState=active", "ActiveState=failed", StringComparison.Ordinal));

        var drifted = await resource.ObserveAsync(None);

        // A different diagnosis from "the login did not take", and the one that leads somewhere:
        // a getty in this state will never log anybody in, whatever the drop-in says.
        Assert.False(drifted.InSync);
        Assert.Contains("getty@tty1.service is failed", drifted.Observed, StringComparison.Ordinal);
        Assert.DoesNotContain(ListSessions, processes.Commands);
    }

    [Fact]
    public void The_session_list_is_read_by_field_because_loginctls_columns_have_moved()
    {
        // systemd ≤ 255 printed SESSION UID USER SEAT TTY; later versions append STATE and IDLE.
        // A parser pinned to a column index would call a healthy frame drifted on an OS upgrade,
        // which is the same class of fault as reading a file the OS no longer writes.
        Assert.True(ConsoleAutologinResource.HasSessionOnTty1(
            "      1 1000 framelink seat0 tty1",
            "framelink"));

        Assert.True(ConsoleAutologinResource.HasSessionOnTty1(
            "SESSION  UID USER      SEAT  TTY   STATE  IDLE SINCE\n"
            + "      1 1000 framelink seat0 tty1  active no   -\n"
            + "      3 1000 framelink -     pts/0 active no   -\n"
            + "\n2 sessions listed.",
            "framelink"));

        // The header names neither the user nor the terminal, so it can never match on its own.
        Assert.False(ConsoleAutologinResource.HasSessionOnTty1(
            "SESSION  UID USER      SEAT  TTY   STATE  IDLE SINCE",
            "framelink"));

        // Another account holding the console is not this account holding the console.
        Assert.False(ConsoleAutologinResource.HasSessionOnTty1("      1    0 root      seat0 tty1", "framelink"));
        Assert.False(ConsoleAutologinResource.HasSessionOnTty1(string.Empty, "framelink"));

        // A device path is accepted beside the bare name; nothing that is not tty1 can end in it.
        Assert.True(ConsoleAutologinResource.HasSessionOnTty1("      1 1000 framelink seat0 /dev/tty1", "framelink"));
        Assert.False(ConsoleAutologinResource.HasSessionOnTty1("      1 1000 framelink seat0 tty11", "framelink"));

        Assert.Equal("active", ConsoleAutologinResource.ActiveStateIn("LoadState=loaded\nActiveState=active"));
        Assert.Null(ConsoleAutologinResource.ActiveStateIn("LoadState=loaded"));
    }

    [Fact]
    public async Task Autologin_stands_down_on_a_machine_that_has_no_console_getty()
    {
        using var files = new TemporaryFiles();
        var systemd = new RecordingSystemControl();
        var resource = new ConsoleAutologinResource(
            files.Files,
            systemd,
            new RecordingProcessRunner(),
            new FakeUserSession());

        systemd.Answer(ShowGetty, "ExecStart=\nLoadState=not-found\nActiveState=inactive");

        var observation = await resource.ObserveAsync(None);

        // A container or a virtual agent (§5.3) has no tty1 to log anyone in on. Reporting drift
        // would put it in a permanent repair loop over hardware it never had.
        Assert.True(observation.InSync);
        Assert.Contains("no getty@tty1.service", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Autologin_follows_the_fleet_setting_for_the_user_name()
    {
        using var files = new TemporaryFiles();
        var session = new FakeUserSession { UserName = "framelink-douwe" };
        var resource = new ConsoleAutologinResource(
            files.Files,
            new RecordingSystemControl(),
            new RecordingProcessRunner(),
            session);

        await resource.ActAsync(None);

        Assert.Contains("--autologin framelink-douwe", files.Read(ConsoleAutologinResource.DropInPath), StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_fleet_setting_the_user_is_the_account_the_image_was_flashed_with()
    {
        var processes = new RecordingProcessRunner();
        processes.Answers["getent passwd 1000"] = new ProcessResult(
            0,
            "pi:x:1000:1000:,,,:/home/pi:/bin/bash",
            string.Empty);

        // The catalog makes boot.autologin.getty-tty1 converge *before* adoption, on purpose: an
        // adoption edge on the root of the user-unit layer would block the session, labwc and the
        // browser, and §2.7's browser stage would be unavailable to exactly the pending frame that
        // is supposed to be rendering its own fingerprint. That only works if there is a value to
        // converge on, and the frame reads it off itself.
        var session = new LoginUserSession(processes);

        Assert.Equal("pi", session.UserName);
        Assert.Equal("/home/pi", session.HomeDirectory);

        // Resolved once: it cannot change under a running agent, and every user-scoped resource
        // asks for it on every five-minute sweep.
        _ = session.UserName;
        Assert.Single(processes.Commands);

        // A machine with no such account still gets a name rather than an empty path.
        Assert.Equal(
            LoginUserSession.DefaultUser,
            new LoginUserSession(new RecordingProcessRunner { Default = new ProcessResult(2, string.Empty, string.Empty) })
                .UserName);
    }

    [Fact]
    public void The_home_directory_walk_stops_at_the_home_directory_itself()
    {
        // The agent writes as root, so it hands back what it created — the file and every
        // directory it had to make to reach it. The home directory is not one of those: useradd
        // made it long before the agent existed.
        Assert.Equal(
            ["/home/framelink/.config/systemd/user/chromium-kiosk.service",
             "/home/framelink/.config/systemd/user",
             "/home/framelink/.config/systemd",
             "/home/framelink/.config"],
            LoginUserSession.SelfAndAncestors(
                "/home/framelink/.config/systemd/user/chromium-kiosk.service",
                "/home/framelink"));

        Assert.Empty(LoginUserSession.SelfAndAncestors("/etc/hosts", "/home/framelink"));
    }

    [Fact]
    public void The_bash_profile_is_guide_5s_118_bytes_with_both_guards()
    {
        var content = BashProfileLabwcResource.DesiredContent;

        // The v1 reference file is 118 bytes. Matching it exactly is not nostalgia: both guards
        // are load-bearing, and the tty test is the one that keeps `exec labwc` off SSH logins —
        // which would break remote administration and the agent's own diagnostics channel.
        Assert.Equal(118, System.Text.Encoding.UTF8.GetByteCount(content));
        Assert.Contains("[ \"$(tty)\" = \"/dev/tty1\" ]", content, StringComparison.Ordinal);
        Assert.Contains("[ -z \"$WAYLAND_DISPLAY\" ]", content, StringComparison.Ordinal);
        Assert.Contains("exec labwc", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_bash_profile_is_drifted_while_the_compositor_is_not_running()
    {
        using var files = new TemporaryFiles();
        var processes = new RecordingProcessRunner();
        var session = new FakeUserSession();
        var resource = new BashProfileLabwcResource(files.Files, processes, session);

        files.Seed(resource.Path, BashProfileLabwcResource.DesiredContent);
        processes.Answers["pgrep -x labwc"] = new ProcessResult(1, string.Empty, string.Empty);

        var drifted = await resource.ObserveAsync(None);
        Assert.False(drifted.InSync);
        Assert.Contains("labwc is not running", drifted.Observed, StringComparison.Ordinal);

        // §2.4: "applied" is claimed from the post-boot observation, not from the write. Once the
        // compositor is there, the same method that found the drift verifies the fix.
        processes.Answers["pgrep -x labwc"] = new ProcessResult(0, "512", string.Empty);
        Assert.True((await resource.ObserveAsync(None)).InSync);
    }

    [Fact]
    public async Task The_bash_profile_is_handed_back_to_the_user_after_the_agent_writes_it_as_root()
    {
        using var files = new TemporaryFiles();
        var session = new FakeUserSession();
        var resource = new BashProfileLabwcResource(files.Files, new RecordingProcessRunner(), session);

        await resource.ActAsync(None);

        Assert.Equal([resource.Path], session.Owned);
    }

    [Fact]
    public async Task The_labwc_autostart_rotates_the_output_and_starts_the_browser()
    {
        using var files = new TemporaryFiles();
        var session = new FakeUserSession();
        var resource = new LabwcAutostartResource(files.Files, session, FleetValues.None);

        await resource.ActAsync(None);

        Assert.Equal(
            "wlr-randr --output DSI-2 --transform 270\nsystemctl --user start chromium-kiosk.service &\n",
            files.Read(resource.Path));

        // Both starts exist deliberately: labwc's autostart and the unit's enablement each bring
        // the browser up, and the catalog asks for that redundancy to be preserved on purpose.
        Assert.True((await resource.ObserveAsync(None)).InSync);
    }

    [Fact]
    public async Task The_labwc_autostart_takes_its_rotation_from_the_fleet_setting()
    {
        using var files = new TemporaryFiles();
        var values = FleetValues.From(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LabwcAutostartResource.RotationSettingKey] = "90",
        });

        var resource = new LabwcAutostartResource(files.Files, new FakeUserSession(), values);
        await resource.ActAsync(None);

        Assert.Contains("--transform 90", files.Read(resource.Path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_perfectly_written_autostart_without_its_mode_bit_is_drift()
    {
        using var files = new TemporaryFiles();
        var autostart = new LabwcAutostartResource(files.Files, new FakeUserSession(), FleetValues.None);
        var executable = new LabwcAutostartExecutableResource(files.Files, autostart);

        await autostart.ActAsync(None);

        // The failure this resource exists for: labwc silently ignores a non-executable autostart,
        // so the frame comes up to a bare compositor with no rotation and no browser and nothing
        // logs a complaint. The content check above passes throughout.
        Assert.True((await autostart.ObserveAsync(None)).InSync);
        Assert.False((await executable.ObserveAsync(None)).InSync);

        await executable.ActAsync(None);
        Assert.True((await executable.ObserveAsync(None)).InSync);
    }

    [Fact]
    public async Task The_touch_map_names_the_output_the_picture_is_on()
    {
        using var files = new TemporaryFiles();
        var resource = new LabwcTouchMapResource(files.Files, new FakeUserSession());

        await resource.ActAsync(None);

        // A misspelled identifier fails silently to the identity transform, so the element and the
        // output name are asserted together.
        Assert.Contains("<touch mapToOutput=\"DSI-2\"/>", files.Read(resource.Path), StringComparison.Ordinal);
        Assert.True((await resource.ObserveAsync(None)).InSync);
    }

    [Fact]
    public async Task The_display_transform_reads_what_wlr_randr_reports_not_what_was_written()
    {
        using var files = new TemporaryFiles();
        var session = new FakeUserSession();
        var autostart = new LabwcAutostartResource(files.Files, session, FleetValues.None);
        var transform = new DisplayTransformResource(session, autostart);

        await autostart.ActAsync(None);

        // The distinct diagnosis the catalog splits this out for: a correct autostart and an
        // output still on the identity transform — a renamed connector, or wlr-randr running
        // before labwc finished bringing the output up.
        session.Answers["wlr-randr "] = new ProcessResult(
            0,
            "DSI-2 \"Unknown Unknown\"\n  Position: 0,0\n  Transform: normal\n  Scale: 1.000000",
            string.Empty);

        var drifted = await transform.ObserveAsync(None);
        Assert.False(drifted.InSync);
        Assert.Contains("transform normal", drifted.Observed, StringComparison.Ordinal);

        session.Answers["wlr-randr "] = new ProcessResult(0, "DSI-2\n  Transform: 270\n", string.Empty);
        Assert.True((await transform.ObserveAsync(None)).InSync);
    }

    [Fact]
    public void The_kiosk_unit_keeps_every_flag_the_catalog_calls_load_bearing()
    {
        var content = ChromiumKioskUnitResource.DesiredContent();

        Assert.Equal(12, ChromiumKioskUnitResource.Flags.Count);

        foreach (var flag in ChromiumKioskUnitResource.Flags)
        {
            Assert.Contains(flag, content, StringComparison.Ordinal);
        }

        // Measured on this build: combining these two crashes Chromium silently at startup.
        Assert.Contains("--auto-accept-camera-and-microphone-capture", content, StringComparison.Ordinal);
        Assert.DoesNotContain("--use-fake-ui-for-media-stream", content, StringComparison.Ordinal);

        // The profile wipe is what makes an app update reach the browser at all; under v2 the app
        // changes when this binary changes, so it connects a self-update to what is on screen.
        Assert.Contains("ExecStartPre=/bin/rm -rf /tmp/framelink-chromium", content, StringComparison.Ordinal);

        // v1 opened localhost and polled 127.0.0.1. To a browser those are different origins, and
        // §2.7 requires the repair screen and the product to share one.
        Assert.Contains("http://127.0.0.1:8888/", content, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", content, StringComparison.Ordinal);

        // The v1 SPA service is gone with the checkout it served (§2.1).
        Assert.DoesNotContain("framelink-spa.service", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_kiosk_unit_reloads_the_user_manager_so_the_enable_that_follows_can_see_it()
    {
        using var files = new TemporaryFiles();
        var session = new FakeUserSession();
        var resource = new ChromiumKioskUnitResource(files.Files, session);

        await resource.ActAsync(None);

        Assert.Contains("systemctl --user daemon-reload", session.Commands);
        Assert.Equal([resource.Path], session.Owned);
        Assert.True((await resource.ObserveAsync(None)).InSync);
    }

    [Fact]
    public async Task Enablement_tells_switched_off_apart_from_no_such_unit()
    {
        var session = new FakeUserSession();
        var resource = new ChromiumKioskEnabledResource(session);

        session.Answers["systemctl --user is-enabled chromium-kiosk.service"] =
            new ProcessResult(1, "disabled", string.Empty);
        Assert.Equal("disabled", (await resource.ObserveAsync(None)).Observed);

        session.Answers["systemctl --user is-enabled chromium-kiosk.service"] =
            new ProcessResult(1, string.Empty, "Failed to get unit file state: No such file or directory");
        Assert.Contains("No such file", (await resource.ObserveAsync(None)).Observed, StringComparison.Ordinal);

        session.Answers["systemctl --user is-enabled chromium-kiosk.service"] =
            new ProcessResult(0, "enabled", string.Empty);
        Assert.True((await resource.ObserveAsync(None)).InSync);
    }

    [Fact]
    public async Task The_browser_the_wrapper_script_exec_d_is_still_this_units_browser()
    {
        using var files = new TemporaryFiles();
        var session = new FakeUserSession();
        var unit = new ChromiumKioskUnitResource(files.Files, session);
        var running = new ChromiumKioskRunningResource(files.Files, session, unit);

        await unit.ActAsync(None);

        // The failure this pins, measured across five boots of the mule: /usr/bin/chromium is a
        // 5,920-byte shell script whose last line execs /usr/lib/chromium/chromium, so the path the
        // unit declares is on no running command line at all — `pgrep -a chromium | grep -c
        // '/usr/bin/chromium'` was 0 against 12 for the library path. The resource identified the
        // browser by that path and so reported "no browser process is running" on every boot,
        // forever, while the browser was up and drawing; the restart it triggered took a working
        // browser down five times a boot. Both halves of the identification are gone: the process is
        // the one systemd names, and the comparison never mentions a binary.
        session.Answers[ShowMainPid] = new ProcessResult(0, Running(1253), string.Empty);
        SeedCommandLine(files, 1253, MeasuredBrowserCommandLine.Split(' '));

        var observation = await running.ObserveAsync(None);

        Assert.True(observation.InSync, observation.Observed);
        Assert.DoesNotContain(ChromiumKioskUnitResource.Browser, MeasuredBrowserCommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_declared_arguments_leave_out_the_binary_the_wrapper_replaces()
    {
        using var files = new TemporaryFiles();
        var unit = new ChromiumKioskUnitResource(files.Files, new FakeUserSession());

        await unit.ActAsync(None);

        // The second half of the same defect, and it has to be fixed with the first or the resource
        // only fails in a new way: with the binary left in, `declared[0]` is /usr/bin/chromium and
        // the compare would go from "no browser process is running" to "running without
        // /usr/bin/chromium" — still never converging, on the same cause.
        var declared = ChromiumKioskUnitResource.ExecStartArguments(files.Read(unit.Path));

        Assert.Equal(ChromiumKioskUnitResource.Flags.Count + 1, declared.Count);
        Assert.DoesNotContain(ChromiumKioskUnitResource.Browser, declared);
        Assert.Equal(ChromiumKioskUnitResource.Flags[0], declared[0]);
        Assert.Equal(ChromiumKioskUnitResource.Origin, declared[^1]);
    }

    [Fact]
    public async Task The_running_browser_may_carry_more_flags_than_the_unit_declares()
    {
        using var files = new TemporaryFiles();
        var session = new FakeUserSession();
        var unit = new ChromiumKioskUnitResource(files.Files, session);
        var running = new ChromiumKioskRunningResource(files.Files, session, unit);

        await unit.ActAsync(None);

        // The measured trap: pkg.chromium drags in rpi-chromium-mods, which injects flags from
        // /etc/chromium.d/ at launch. The running command line is a legitimate *superset* of
        // ExecStart, so an equality compare reports drift on a healthy frame on every pass forever.
        var declared = ChromiumKioskUnitResource.ExecStartArguments(files.Read(unit.Path));
        string[] injected =
        [
            "/usr/lib/chromium/chromium",
            "--enable-features=VaapiVideoDecoder,UsePipeWireCamera",
            "--use-gl=egl",
            "--disable-features=UseChromeOSDirectVideoDecoder",
            .. declared,
        ];

        session.Answers[ShowMainPid] = new ProcessResult(0, Running(701), string.Empty);
        SeedCommandLine(files, 701, injected);

        Assert.True((await running.ObserveAsync(None)).InSync);
    }

    [Fact]
    public async Task A_running_browser_missing_a_declared_flag_is_drift_and_is_named()
    {
        using var files = new TemporaryFiles();
        var session = new FakeUserSession();
        var unit = new ChromiumKioskUnitResource(files.Files, session);
        var running = new ChromiumKioskRunningResource(files.Files, session, unit);

        await unit.ActAsync(None);

        session.Answers[ShowMainPid] = new ProcessResult(0, Running(701), string.Empty);
        SeedCommandLine(
            files,
            701,
            "/usr/lib/chromium/chromium",
            "--ozone-platform=wayland",
            "--kiosk",
            "http://127.0.0.1:8888/");

        var observation = await running.ObserveAsync(None);

        Assert.False(observation.InSync);
        Assert.Contains("--enable-features=UsePipeWireCamera", observation.Observed, StringComparison.Ordinal);

        await running.ActAsync(None);
        Assert.Contains("systemctl --user restart chromium-kiosk.service", session.Commands);
    }

    [Fact]
    public async Task A_browser_this_unit_does_not_own_cannot_stand_in_for_it()
    {
        using var files = new TemporaryFiles();
        var session = new FakeUserSession();
        var unit = new ChromiumKioskUnitResource(files.Files, session);
        var running = new ChromiumKioskRunningResource(files.Files, session, unit);

        await unit.ActAsync(None);

        // A Chromium somebody started over SSH carries the same binary and can carry every declared
        // flag, so no reading of the process table can tell it from the unit's. MainPID can: this
        // unit has no main process, and that is the verdict — which is also the exact wording
        // SupervisionInterlock and Supervisor quote as the transient a restart produces.
        SeedCommandLine(files, 4242, MeasuredBrowserCommandLine.Split(' '));
        session.Answers[ShowMainPid] =
            new ProcessResult(0, "MainPID=0\nActiveState=inactive\nSubState=dead", string.Empty);

        var observation = await running.ObserveAsync(None);

        Assert.False(observation.InSync);
        Assert.Equal(ObservationOutcome.Drifted, observation.Outcome);

        // The sentence SupervisionInterlock and Supervisor quote stays the head of the string; the
        // phase systemd gave for it is added after, so a reader can tell a stopped unit from a
        // failed one without a second command.
        Assert.StartsWith("no browser process is running", observation.Observed, StringComparison.Ordinal);
        Assert.Contains("inactive, dead", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_unit_that_has_not_finished_starting_is_not_yet_answerable()
    {
        using var files = new TemporaryFiles();
        var session = new FakeUserSession();
        var unit = new ChromiumKioskUnitResource(files.Files, session);
        var running = new ChromiumKioskRunningResource(files.Files, session, unit);

        await unit.ActAsync(None);

        // Measured on the mule 2026-08-16. Ten seconds after a reboot the login session exists, so
        // UserSessionGate lets this resource through, while chromium-kiosk.service is still inside
        // its ~4.5 s of ExecStartPre waiting for the Wayland socket; Chromium reached `Started` at
        // 15.2 s. MainPID is 0 for that whole window — systemd's service_set_state calls
        // service_unwatch_main_pid on entry to any state outside SERVICE_STATE_WITH_MAIN_PROCESS,
        // which excludes SERVICE_START_PRE — so the resource read "no browser process is running",
        // acted, and restarted a browser that was seconds from drawing. Three attempts in a row, to
        // an escalation.
        foreach (var phase in ChromiumKioskRunningResource.StartingSubStates)
        {
            session.Answers[ShowMainPid] =
                new ProcessResult(0, $"MainPID=0\nActiveState=activating\nSubState={phase}", string.Empty);

            var observation = await running.ObserveAsync(None);

            // Unevaluable, not drift: no attempt is spent, nothing is acted on, nothing reboots.
            Assert.Equal(ObservationOutcome.Unevaluable, observation.Outcome);
            Assert.Contains("has not finished starting", observation.Observed, StringComparison.Ordinal);
            Assert.Contains(phase, observation.Observed, StringComparison.Ordinal);
            Assert.Contains("could not be determined", observation.Delta, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_browser_systemd_is_waiting_to_restart_is_still_a_missing_browser()
    {
        using var files = new TemporaryFiles();
        var session = new FakeUserSession();
        var unit = new ChromiumKioskUnitResource(files.Files, session);
        var running = new ChromiumKioskRunningResource(files.Files, session, unit);

        await unit.ActAsync(None);

        // The gate above must not widen into "the browser is not running, so never mind". These
        // four are all states in which systemd has finished trying, or has not started, and every
        // one of them is a fully answerable reading of a frame with no browser on it. auto-restart
        // is ActiveState=activating and is deliberately not excused: a browser systemd is waiting
        // to bring back is exactly the fault this resource exists to report.
        foreach (var (active, sub) in new[]
        {
            ("failed", "failed"),
            ("inactive", "dead"),
            ("activating", "auto-restart"),
            ("deactivating", "stop-sigterm"),
        })
        {
            session.Answers[ShowMainPid] =
                new ProcessResult(0, $"MainPID=0\nActiveState={active}\nSubState={sub}", string.Empty);

            var observation = await running.ObserveAsync(None);

            Assert.Equal(ObservationOutcome.Drifted, observation.Outcome);
            Assert.StartsWith("no browser process is running", observation.Observed, StringComparison.Ordinal);
            Assert.Contains(sub, observation.Observed, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_mismatch_names_the_process_it_actually_read()
    {
        using var files = new TemporaryFiles();
        var session = new FakeUserSession();
        var unit = new ChromiumKioskUnitResource(files.Files, session);
        var running = new ChromiumKioskRunningResource(files.Files, session, unit);

        await unit.ActAsync(None);

        // The undetermined defect of 2026-08-16, in the form that would have settled it. The
        // resource reported all thirteen declared arguments missing against a non-zero pid whose
        // command line it had read successfully, and wrote down nothing about what that command
        // line was — so "a browser launched wrong" and "a reading of something that was never the
        // browser" were indistinguishable in the record, and still are. argv[0] separates them.
        session.Answers[ShowMainPid] = new ProcessResult(0, Running(1253), string.Empty);
        SeedCommandLine(files, 1253, "/bin/bash", "-c", "while [ ! -S \"/run/user/1000/wayland-0\" ]; do sleep 0.1; done");

        var observation = await running.ObserveAsync(None);

        Assert.False(observation.InSync);
        Assert.Contains("pid 1253", observation.Observed, StringComparison.Ordinal);
        Assert.Contains("/bin/bash", observation.Observed, StringComparison.Ordinal);
        Assert.Contains("running without", observation.Observed, StringComparison.Ordinal);

        // Bounded, because a delta reaches the frame's own screen. A whole Chromium command line is
        // twenty-four arguments and would push everything else off it.
        SeedCommandLine(files, 1253, MeasuredBrowserCommandLine.Split(' ')[..^1]);

        var truncated = (await running.ObserveAsync(None)).Observed;

        Assert.Contains("and 20 more arguments", truncated, StringComparison.Ordinal);
        Assert.DoesNotContain("--disable-pings", truncated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_user_manager_that_will_not_answer_is_not_a_dead_browser()
    {
        using var files = new TemporaryFiles();
        var session = new FakeUserSession();
        var unit = new ChromiumKioskUnitResource(files.Files, session);
        var running = new ChromiumKioskRunningResource(files.Files, session, unit);

        await unit.ActAsync(None);

        session.Answers[ShowMainPid] = new ProcessResult(
            1,
            string.Empty,
            "Failed to connect to user scope bus: No such file or directory");

        var observation = await running.ObserveAsync(None);

        Assert.False(observation.InSync);
        Assert.DoesNotContain("no browser process is running", observation.Observed, StringComparison.Ordinal);
        Assert.Contains("Failed to connect to user scope bus", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Renderers_are_not_mistaken_for_the_browser_they_belong_to()
    {
        using var files = new TemporaryFiles();
        var session = new FakeUserSession();
        var unit = new ChromiumKioskUnitResource(files.Files, session);
        var running = new ChromiumKioskRunningResource(files.Files, session, unit);

        await unit.ActAsync(None);

        // Thirteen chromium processes were in the tree on the mule and twelve of them are not the
        // browser. Nothing here filters them: MainPID names one process, so the zygote and the
        // renderer are never candidates in the first place.
        SeedCommandLine(files, 1277, "/usr/lib/chromium/chromium", "--type=zygote", "--no-zygote-sandbox");
        SeedCommandLine(
            files,
            1352,
            "/usr/lib/chromium/chromium",
            "--type=renderer",
            "--enable-crash-reporter=,built on Debian GNU/Linux 13 (trixie)",
            "--ozone-platform=wayland");
        SeedCommandLine(files, 1253, MeasuredBrowserCommandLine.Split(' '));

        session.Answers[ShowMainPid] = new ProcessResult(0, Running(1253), string.Empty);
        Assert.True((await running.ObserveAsync(None)).InSync);

        // And if systemd ever named one, it would be drift rather than a healthy frame: a renderer
        // carries none of the kiosk flags.
        session.Answers[ShowMainPid] = new ProcessResult(0, Running(1352), string.Empty);
        var observation = await running.ObserveAsync(None);

        Assert.False(observation.InSync);
        Assert.Contains("--kiosk", observation.Observed, StringComparison.Ordinal);

        // The renderer is also what proves the NUL split: a whitespace split would turn its
        // crash-reporter argument into seven, and this file's whole claim is that the vector is the
        // kernel's rather than a rendering of it.
        var renderer = ChromiumKioskRunningResource.CommandLineOf(files.Files, 1352);

        Assert.NotNull(renderer);
        Assert.Contains("--enable-crash-reporter=,built on Debian GNU/Linux 13 (trixie)", renderer);
    }

    [Fact]
    public async Task A_kernel_shaped_command_line_reaches_the_compare_with_its_separators_intact()
    {
        using var files = new TemporaryFiles();
        var session = new FakeUserSession();
        var unit = new ChromiumKioskUnitResource(files.Files, session);

        await unit.ActAsync(None);

        // The read path that stood accused, exercised end to end and cleared. ISystemFiles is
        // text-oriented and cmdline is NUL-delimited binary, so the standing suspicion was that the
        // separators never survived ReadText. They do: raw kernel bytes are put on disk here with
        // no text writer anywhere in the seeding, and HostSystemFiles.ReadText hands CommandLineOf
        // every one of them back. Measured the same way off a frame — a self-contained build
        // reading a real /proc/<pid>/cmdline on Linux returns each NUL, tokenises correctly, and
        // keeps a space-bearing argument whole.
        var argv = MeasuredBrowserCommandLine.Split(' ');
        SeedCommandLineBytes(files, 1198, string.Join('\0', argv) + '\0');

        var declared = ChromiumKioskUnitResource.ExecStartArguments(files.Read(unit.Path));
        var read = ChromiumKioskRunningResource.CommandLineOf(files.Files, 1198);

        Assert.NotNull(read);
        Assert.Equal(argv.Length, read.Count);
        Assert.Equal(ChromiumKioskUnitResource.Flags.Count + 1, declared.Count);
        Assert.Empty(ChromiumKioskRunningResource.MissingFrom(read, declared));
    }

    [Fact]
    public async Task A_browser_that_rewrote_its_own_argv_is_still_read_as_the_vector_it_was_started_with()
    {
        using var files = new TemporaryFiles();
        var session = new FakeUserSession();
        var unit = new ChromiumKioskUnitResource(files.Files, session);
        var running = new ChromiumKioskRunningResource(files.Files, session, unit);

        await unit.ActAsync(None);

        // The shape the frame's kernel actually held on 2026-08-16, and the shape the frame's own
        // screen printed back. Chromium's set_process_title_linux.cc implements setproctitle by
        // overwriting the argv area with one space-joined string and NUL-padding the remainder, and
        // proc(5) says the kernel serves whatever is in that region — so /proc/1198/cmdline carries
        // no NUL between two arguments at all. Split on NUL alone it is a single token, no declared
        // argument is ever found inside it, and unit.chromium-kiosk.running-matches-content
        // reported all thirteen missing from a browser that was carrying all thirteen. That is what
        // escalated at 3/3, stopped the pass, left four resources at att=0/3 with the album setting
        // never written, and put a repair screen on a frame whose browser was up and drawing.
        SeedCommandLineBytes(files, 1198, MeasuredBrowserCommandLine + "\0\0\0\0");

        var declared = ChromiumKioskUnitResource.ExecStartArguments(files.Read(unit.Path));
        var read = ChromiumKioskRunningResource.CommandLineOf(files.Files, 1198);

        Assert.NotNull(read);
        Assert.Equal(MeasuredBrowserCommandLine.Split(' ').Length, read.Count);
        Assert.Empty(ChromiumKioskRunningResource.MissingFrom(read, declared));

        session.Answers[ShowMainPid] = new ProcessResult(0, Running(1198), string.Empty);

        var observation = await running.ObserveAsync(None);

        Assert.True(observation.InSync, observation.Observed);

        // And the discriminator is the kernel's own signature rather than a preference: a vector of
        // two or more always carries a NUL between them, so a lone argument is left exactly as it
        // was delimited and never taken apart looking for a separator that was never written.
        SeedCommandLineBytes(files, 4242, "/usr/lib/chromium/chromium\0");

        var lone = ChromiumKioskRunningResource.CommandLineOf(files.Files, 4242);

        Assert.NotNull(lone);
        Assert.Equal("/usr/lib/chromium/chromium", Assert.Single(lone));
    }

    /// <summary>
    /// Writes a <c>/proc/&lt;pid&gt;/cmdline</c> the way the kernel presents one: NUL between the
    /// arguments and a NUL after the last.
    /// </summary>
    private static void SeedCommandLine(TemporaryFiles files, int pid, params string[] argv) =>
        files.Seed($"/proc/{pid}/cmdline", string.Join('\0', argv) + '\0');

    /// <summary>
    /// Writes a <c>/proc/&lt;pid&gt;/cmdline</c> as the bytes themselves, with no text writer in
    /// the seeding path.
    /// </summary>
    /// <remarks>
    /// <see cref="TemporaryFiles.Seed"/> goes through <see cref="HostSystemFiles.WriteText"/>, so a
    /// test written on it proves only that the shipping writer and the shipping reader agree. The
    /// question this file has to answer is narrower — what the shipping <i>reader</i> does with
    /// bytes it did not write — so the bytes are laid down directly at the resolved path.
    /// </remarks>
    private static void SeedCommandLineBytes(TemporaryFiles files, int pid, string content)
    {
        var resolved = files.Files.Resolve($"/proc/{pid}/cmdline");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(resolved)!);
        File.WriteAllBytes(resolved, System.Text.Encoding.UTF8.GetBytes(content));
    }

    [Fact]
    public async Task Swap_reads_the_zram_row_rather_than_the_presence_of_any_swap()
    {
        var processes = new RecordingProcessRunner();
        var resource = new SwapZramResource(processes, new RecordingSystemControl());

        processes.Answers["swapon --show"] = new ProcessResult(
            0,
            "NAME       TYPE      SIZE USED PRIO\n/swapfile  file        1G   0B   -2",
            string.Empty);

        var drifted = await resource.ObserveAsync(None);
        Assert.False(drifted.InSync);

        processes.Answers["swapon --show"] = new ProcessResult(
            0,
            "NAME       TYPE      SIZE USED PRIO\n/dev/zram0 partition   2G   0B  100",
            string.Empty);

        Assert.True((await resource.ObserveAsync(None)).InSync);
    }
}
