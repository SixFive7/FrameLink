using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;
using FrameLink.Agent.Stage;
using FrameLink.Agent.State;

namespace FrameLink.Tests;

/// <summary>
/// The display resources, scheduled early against §5.5's default, and §5.5's three mitigations
/// that pay for the early slot.
/// </summary>
/// <remarks>
/// The measurements these tests are written against were taken on the mule 2026-08-15: a stock
/// image has <c>dtoverlay=vc4-kms-v3d</c> only, both HDMI connectors <c>disconnected</c>, no DSI
/// connector, no <c>/dev/fb0</c>, an empty <c>/sys/class/backlight/</c>, and an active
/// <c>tty1</c> that accepts every write and shows nothing.
/// </remarks>
public sealed class AgentDisplayPanelTests
{
    private const string StockConfig =
        "# For more options and information see http://rptl.io/configtxt\n"
        + "[all]\n"
        + "dtparam=audio=on\n"
        + "camera_auto_detect=1\n"
        + "display_auto_detect=1\n"
        + "dtoverlay=vc4-kms-v3d\n";

    private const string StockCmdline =
        "console=serial0,115200 console=tty1 root=PARTUUID=f870549c-02 rootfstype=ext4 fsck.repair=yes rootwait\n";

    private static ReconcileOptions Fast => new() { Countdown = TimeSpan.Zero, AttemptBudget = 3 };

    [Fact]
    public async Task The_overlay_line_is_appended_and_nothing_else_in_config_txt_moves()
    {
        using var files = new TemporaryFiles();
        files.Seed(BootConfigText.ConfigPath, StockConfig);

        var boot = new MutableBootIdentity();
        var guard = Guard(files, boot, new RecordingLog());
        var display = new SwitchableDisplay();
        using var harness = new ReconcileHarness(
            Fast,
            boot,
            new DisplayPanelOverlayResource(files.Files, guard, display, new RecordingLog()));

        harness.Boundary.OnBoot = (_, _) =>
        {
            // The overlay took: after this boot there is a connected DSI panel.
            display.Visible = files.Read(BootConfigText.ConfigPath)!
                .Contains(DisplayPanelOverlayResource.OverlayLine, StringComparison.Ordinal);
            return Task.CompletedTask;
        };

        var outcome = await harness.ConvergeAsync();
        var written = files.Read(BootConfigText.ConfigPath)!;

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.Equal(StockConfig + DisplayPanelOverlayResource.OverlayLine + "\n", written);
        Assert.Equal(1, harness.Boot.Boots);
    }

    [Fact]
    public async Task Config_txt_is_backed_up_where_a_card_reader_can_find_it()
    {
        // §5.5's "keep and restore backups". The boot partition is FAT32, so a person who pulls
        // the card and puts it in any laptop can see this file; a backup under /var/lib would sit
        // on an ext4 root that Windows and macOS will not mount.
        using var files = new TemporaryFiles();
        files.Seed(BootConfigText.ConfigPath, StockConfig);

        await new DisplayPanelOverlayResource(
                files.Files,
                Guard(files, new MutableBootIdentity(), new RecordingLog()),
                new SwitchableDisplay(),
                new RecordingLog())
            .ActAsync(TestContext.Current.CancellationToken);

        Assert.Equal(StockConfig, files.Read(BootPartitionGuard.BackupFor(BootConfigText.ConfigPath)));
        Assert.StartsWith("/boot/firmware/", BootPartitionGuard.BackupFor(BootConfigText.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_panel_line_that_lights_nothing_is_rolled_back_after_its_boot_budget()
    {
        // §5.5's boot-count self-repair, and the reason the display probe is folded into Observe:
        // a line that is written and lights nothing keeps the trial open, so the budget runs out
        // and the backup goes back rather than the frame retrying into the same wall forever.
        using var files = new TemporaryFiles();
        files.Seed(BootConfigText.ConfigPath, StockConfig);

        var boot = new MutableBootIdentity();
        var log = new RecordingLog();
        var guard = Guard(files, boot, log);
        var resource = new DisplayPanelOverlayResource(files.Files, guard, new SwitchableDisplay(), log);

        await resource.ActAsync(TestContext.Current.CancellationToken);
        Assert.Contains(DisplayPanelOverlayResource.OverlayLine, files.Read(BootConfigText.ConfigPath)!, StringComparison.Ordinal);

        // Two boots with the panel still dark.
        boot.Advance();
        await resource.ObserveAsync(TestContext.Current.CancellationToken);
        boot.Advance();
        await resource.ObserveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(StockConfig, files.Read(BootConfigText.ConfigPath));
        Assert.Contains("Putting", log.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rolled_back_change_is_never_reapplied_unattended()
    {
        using var files = new TemporaryFiles();
        files.Seed(BootConfigText.ConfigPath, StockConfig);

        var boot = new MutableBootIdentity();
        var guard = Guard(files, boot, new RecordingLog());
        var resource = new DisplayPanelOverlayResource(files.Files, guard, new SwitchableDisplay(), new RecordingLog());

        await resource.ActAsync(TestContext.Current.CancellationToken);
        boot.Advance();
        await resource.ObserveAsync(TestContext.Current.CancellationToken);
        boot.Advance();
        await resource.ObserveAsync(TestContext.Current.CancellationToken);

        var second = await resource.ActAsync(TestContext.Current.CancellationToken);

        Assert.Contains("refused", second.Change, StringComparison.Ordinal);
        Assert.Equal(StockConfig, files.Read(BootConfigText.ConfigPath));
    }

    [Fact]
    public async Task A_rolled_back_display_reports_drift_that_names_the_backup()
    {
        using var files = new TemporaryFiles();
        files.Seed(BootConfigText.ConfigPath, StockConfig);

        var boot = new MutableBootIdentity();
        var guard = Guard(files, boot, new RecordingLog());
        var resource = new DisplayPanelOverlayResource(files.Files, guard, new SwitchableDisplay(), new RecordingLog());

        await resource.ActAsync(TestContext.Current.CancellationToken);
        boot.Advance();
        await resource.ObserveAsync(TestContext.Current.CancellationToken);
        boot.Advance();
        var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);

        Assert.False(observation.InSync);
        Assert.Contains("put back automatically", observation.Observed, StringComparison.Ordinal);
        Assert.Contains(BootPartitionGuard.BackupFor(BootConfigText.ConfigPath), observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_rotation_is_verified_against_the_command_line_the_kernel_actually_got()
    {
        // Not against the file that was written. §2.4: the file is what was written,
        // /proc/cmdline is what took effect, and comparing only the first is the write-only check.
        using var files = new TemporaryFiles();
        files.Seed(BootConfigText.CmdlinePath, StockCmdline);
        files.Seed(ConsoleRotationResource.ProcCmdlinePath, StockCmdline);

        var guard = Guard(files, new MutableBootIdentity(), new RecordingLog());
        var resource = new ConsoleRotationResource(files.Files, guard, new RecordingLog());

        await resource.ActAsync(TestContext.Current.CancellationToken);
        var beforeBoot = await resource.ObserveAsync(TestContext.Current.CancellationToken);

        // The file is right and the running kernel has not been restarted yet.
        Assert.False(beforeBoot.InSync);
        Assert.Contains("/proc/cmdline=absent", beforeBoot.Observed, StringComparison.Ordinal);

        files.Seed(ConsoleRotationResource.ProcCmdlinePath, files.Read(BootConfigText.CmdlinePath)!);
        var afterBoot = await resource.ObserveAsync(TestContext.Current.CancellationToken);

        Assert.True(afterBoot.InSync);
    }

    [Fact]
    public async Task A_rotation_somebody_set_by_hand_is_left_alone()
    {
        // Guide 3's own guard is `grep -q 'fbcon=rotate:' || sed`, which deliberately leaves a
        // different rotation alone — somebody who changed 1 to 3 because their panel was upside
        // down meant it.
        using var files = new TemporaryFiles();
        files.Seed(BootConfigText.CmdlinePath, StockCmdline.TrimEnd('\n') + " fbcon=rotate:3\n");

        var action = await new ConsoleRotationResource(
                files.Files,
                Guard(files, new MutableBootIdentity(), new RecordingLog()),
                new RecordingLog())
            .ActAsync(TestContext.Current.CancellationToken);

        Assert.Contains("alone", action.Change, StringComparison.Ordinal);
        Assert.Contains("fbcon=rotate:3", files.Read(BootConfigText.CmdlinePath)!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_command_line_edit_that_would_lose_root_is_refused()
    {
        // §5.5's "validate before writing", applied to the parameter without which the machine
        // does not boot at all.
        var verdict = BootConfigText.ValidateCmdline(
            StockCmdline,
            "console=tty1 rootwait fbcon=rotate:1\n",
            ConsoleRotationResource.RotateToken);

        Assert.False(verdict.Valid);
        Assert.Contains("root=", verdict.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_command_line_that_became_two_lines_is_refused()
    {
        // The firmware reads the first line only, so a second line is silently ignored — a
        // "successful" edit producing a kernel that never sees its own parameters.
        var verdict = BootConfigText.ValidateCmdline(
            StockCmdline,
            StockCmdline.TrimEnd('\n') + "\nfbcon=rotate:1\n",
            ConsoleRotationResource.RotateToken);

        Assert.False(verdict.Valid);
        Assert.Contains("single line", verdict.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_config_edit_that_changes_more_than_one_line_is_refused()
    {
        var verdict = BootConfigText.ValidateConfig(
            StockConfig,
            StockConfig.Replace("dtparam=audio=on", "dtparam=audio=off", StringComparison.Ordinal)
                + DisplayPanelOverlayResource.OverlayLine + "\n",
            DisplayPanelOverlayResource.OverlayLine);

        Assert.False(verdict.Valid);
        Assert.Contains("also change", verdict.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_config_file_that_would_stop_parsing_is_refused()
    {
        var verdict = BootConfigText.ValidateConfig(
            StockConfig + "this is not a setting\n",
            StockConfig + "this is not a setting\n" + DisplayPanelOverlayResource.OverlayLine + "\n",
            DisplayPanelOverlayResource.OverlayLine);

        Assert.False(verdict.Valid);
        Assert.Contains("neither a section", verdict.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_correct_single_line_edit_passes_both_validators()
    {
        Assert.True(BootConfigText.ValidateConfig(
            StockConfig,
            BootConfigText.AppendLine(StockConfig, DisplayPanelOverlayResource.OverlayLine),
            DisplayPanelOverlayResource.OverlayLine).Valid);

        Assert.True(BootConfigText.ValidateCmdline(
            StockCmdline,
            BootConfigText.AppendToken(StockCmdline, ConsoleRotationResource.RotateToken),
            ConsoleRotationResource.RotateToken).Valid);
    }

    private static BootPartitionGuard Guard(TemporaryFiles files, MutableBootIdentity boot, RecordingLog log) =>
        new(files.Files, files.Store, boot, new ManualClock(), log);

    private sealed class SwitchableDisplay : IDisplayProbe
    {
        public bool Visible { get; set; }

        public DisplayVisibility Probe() => Visible
            ? new DisplayVisibility(true, "A display is connected on card1-DSI-1.", "drm=[card1-DSI-1=connected]")
            : new DisplayVisibility(false, "Nothing on this frame can show a picture yet.", "drm=[]");
    }
}

/// <summary>
/// Whether the console stage can be seen — the failure that a successful write hides.
/// </summary>
public sealed class AgentDisplayProbeTests
{
    [Fact]
    public void A_stock_image_with_no_framebuffer_and_no_connected_output_is_reported_dark()
    {
        // The mule, verbatim: two disconnected HDMI connectors, no DSI connector, no /dev/fb0,
        // empty /sys/class/backlight.
        using var files = new TemporaryFiles();
        files.Seed(SysfsDisplayProbe.DrmPath + "/card1-HDMI-A-1/status", "disconnected\n");
        files.Seed(SysfsDisplayProbe.DrmPath + "/card1-HDMI-A-2/status", "disconnected\n");

        var verdict = new SysfsDisplayProbe(files.Files).Probe();

        Assert.False(verdict.Visible);
        Assert.Contains("no framebuffer", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("card1-HDMI-A-1=disconnected", verdict.Evidence, StringComparison.Ordinal);
        Assert.Contains("/dev/fb0=absent", verdict.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void A_connected_dsi_panel_is_reported_visible_and_named()
    {
        using var files = new TemporaryFiles();
        files.Seed(SysfsDisplayProbe.DrmPath + "/card1-DSI-1/status", "connected\n");
        files.Seed(SysfsDisplayProbe.DrmPath + "/card1-HDMI-A-1/status", "disconnected\n");

        var verdict = new SysfsDisplayProbe(files.Files).Probe();

        Assert.True(verdict.Visible);
        Assert.Contains("card1-DSI-1", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_framebuffer_with_no_connected_connector_still_counts_as_visible()
    {
        // Weaker evidence, and worth believing: reporting a working frame as blind would teach an
        // operator to ignore the warning.
        using var files = new TemporaryFiles();
        files.Seed(SysfsDisplayProbe.FramebufferPath, string.Empty);

        Assert.True(new SysfsDisplayProbe(files.Files).Probe().Visible);
    }

    [Fact]
    public void The_stage_says_plainly_that_its_narration_is_not_visible()
    {
        // "Console stage attached to /dev/tty1 at 80x25" was the old line, and it appeared on a
        // machine where nothing was rendered anywhere. An operator reading it concluded the
        // screen was working.
        using var files = new TemporaryFiles();
        files.Seed(SysfsDisplayProbe.DrmPath + "/card1-HDMI-A-1/status", "disconnected\n");

        var log = new RecordingLog();
        var hub = new AgentStatusHub(AgentStatusFactory.Starting());
        using var stage = new ConsoleStage(
            new MemoryTerminal(),
            hub,
            new ManualClock(),
            new SysfsDisplayProbe(files.Files),
            log);

        Assert.False(stage.Visibility.Visible);
        Assert.Contains("narration is not visible", log.Transcript, StringComparison.Ordinal);
        Assert.False(hub.Current.ConsoleVisibility!.Value.Visible);
    }

    [Fact]
    public void A_terminal_that_will_not_say_how_big_it_is_says_so_instead_of_inventing_a_size()
    {
        // 80x25 was the fallback geometry reported as a measurement. The width decides the box
        // drawing, the wrap points and the bar lengths, so a guess is wrong everywhere at once.
        var log = new RecordingLog();
        using var stage = new ConsoleStage(
            new MemoryTerminal { SizeIsKnown = false },
            new AgentStatusHub(AgentStatusFactory.Starting()),
            new ManualClock(),
            StaticDisplayProbe.Visible,
            log);

        Assert.Contains("did not report its size", log.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stage_that_can_be_seen_says_that_too_rather_than_staying_silent()
    {
        var log = new RecordingLog();
        using var stage = new ConsoleStage(
            new MemoryTerminal(),
            new AgentStatusHub(AgentStatusFactory.Starting()),
            new ManualClock(),
            StaticDisplayProbe.Visible,
            log);

        Assert.Contains("can be seen", log.Transcript, StringComparison.Ordinal);
    }
}
