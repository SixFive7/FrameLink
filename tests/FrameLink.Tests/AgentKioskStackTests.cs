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
        systemd.Answer("show getty@tty1.service -p ExecStart -p LoadState",
            "ExecStart=/sbin/agetty -o -p -- \\u --noclear - $TERM\nLoadState=loaded");
        processes.Answers["who "] = new ProcessResult(0, "framelink tty1         2026-08-15 12:00", string.Empty);

        var observation = await resource.ObserveAsync(None);

        Assert.False(observation.InSync);
        Assert.Contains("systemd runs", observation.Observed, StringComparison.Ordinal);
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

        systemd.Answer("show getty@tty1.service -p ExecStart -p LoadState", "ExecStart=\nLoadState=not-found");

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
    public async Task The_running_browser_may_carry_more_flags_than_the_unit_declares()
    {
        using var files = new TemporaryFiles();
        var session = new FakeUserSession();
        var processes = new RecordingProcessRunner();
        var unit = new ChromiumKioskUnitResource(files.Files, session);
        var running = new ChromiumKioskRunningResource(files.Files, processes, session, unit);

        await unit.ActAsync(None);

        // The measured trap: pkg.chromium drags in rpi-chromium-mods, which injects flags from
        // /etc/chromium.d/ at launch. The running command line is a legitimate *superset* of
        // ExecStart, so an equality compare reports drift on a healthy frame on every pass forever.
        var declared = ChromiumKioskUnitResource.ExecStartArguments(files.Read(unit.Path));
        var injected = "/usr/bin/chromium --enable-features=VaapiVideoDecoder,UsePipeWireCamera "
            + "--use-gl=egl --disable-features=UseChromeOSDirectVideoDecoder "
            + string.Join(' ', declared.Skip(1));

        processes.Answers["pgrep -a chromium"] = new ProcessResult(0, "701 " + injected, string.Empty);

        Assert.True((await running.ObserveAsync(None)).InSync);
    }

    [Fact]
    public async Task A_running_browser_missing_a_declared_flag_is_drift_and_is_named()
    {
        using var files = new TemporaryFiles();
        var session = new FakeUserSession();
        var processes = new RecordingProcessRunner();
        var unit = new ChromiumKioskUnitResource(files.Files, session);
        var running = new ChromiumKioskRunningResource(files.Files, processes, session, unit);

        await unit.ActAsync(None);

        processes.Answers["pgrep -a chromium"] = new ProcessResult(
            0,
            "701 /usr/bin/chromium --ozone-platform=wayland --kiosk http://127.0.0.1:8888/",
            string.Empty);

        var observation = await running.ObserveAsync(None);

        Assert.False(observation.InSync);
        Assert.Contains("--enable-features=UsePipeWireCamera", observation.Observed, StringComparison.Ordinal);

        await running.ActAsync(None);
        Assert.Contains("systemctl --user restart chromium-kiosk.service", session.Commands);
    }

    [Fact]
    public void Renderers_are_not_mistaken_for_the_browser_they_belong_to()
    {
        const string Listing = """
            712 /usr/bin/chromium --type=zygote --no-zygote-sandbox
            740 /usr/bin/chromium --type=renderer --enable-features=UsePipeWireCamera
            701 /usr/bin/chromium --ozone-platform=wayland --kiosk http://127.0.0.1:8888/
            """;

        var main = ChromiumKioskRunningResource.MainProcessLine(Listing);

        Assert.NotNull(main);
        Assert.DoesNotContain("--type=", main, StringComparison.Ordinal);
        Assert.Contains("--kiosk", main, StringComparison.Ordinal);
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
