using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Local;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;
using FrameLink.Agent.Supervise;

namespace FrameLink.Tests;

/// <summary>
/// The catalog's camera block — guide 6, the chain from the sensor to <c>getUserMedia()</c>.
/// </summary>
/// <remarks>
/// <para>
/// Three of these assert against <c>reference/v1-state-inventory.txt</c> rather than against a
/// constant in the test: the camera unit, the WirePlumber fragment and the portal drop-in are
/// carried across from the frame that defines parity, and the inventory is the frozen record of
/// what that frame had. Transcribing them into the test would only prove the agent agrees with the
/// test author.
/// </para>
/// <para>
/// The rest run the shipping resources against a real filesystem rooted somewhere throwaway and a
/// scripted user session, so what is under test is the resources' own parsing — of
/// <c>wpctl status</c>, of <c>busctl</c>, of <c>systemctl show</c> — rather than a stand-in that
/// agrees with them by construction.
/// </para>
/// </remarks>
public sealed class AgentCameraTests
{
    private static readonly CancellationToken None = TestContext.Current.CancellationToken;

    /// <summary>
    /// <c>wpctl status</c> as the v1 frame printed it, transcribed from the inventory's PIPEWIRE
    /// section — box-drawing characters, column widths and all.
    /// </summary>
    private const string WpctlAudioOnly =
        "PipeWire 'pipewire-0' [1.4.2, framelink@framelink-douwe, cookie:4249062001]\n"
        + " └─ Clients:\n"
        + "        33. WirePlumber                         [1.4.2, framelink@framelink-douwe, pid:989]\n"
        + "        48. gst-launch-1.0                      [1.4.2, framelink@framelink-douwe, pid:15115]\n"
        + "\n"
        + "Audio\n"
        + " ├─ Devices:\n"
        + " │      49. reSpeaker XVF3800 4-Mic Array       [alsa]\n"
        + " │  \n"
        + " ├─ Sinks:\n"
        + " │  *   53. reSpeaker XVF3800 4-Mic Array Analog Stereo [vol: 1.00]\n"
        + " │  \n"
        + " ├─ Sources:\n"
        + " │  *   54. reSpeaker XVF3800 4-Mic Array Analog Stereo [vol: 1.00]\n"
        + " │  \n"
        + " ├─ Filters:\n"
        + " │  \n"
        + " └─ Streams:\n"
        + "\n"
        + "Video\n"
        + " ├─ Devices:\n"
        + " │  \n"
        + " ├─ Sinks:\n"
        + " │  \n"
        + " ├─ Sources:\n"
        + " │  \n"
        + " ├─ Filters:\n"
        + " │  \n"
        + " └─ Streams:\n";

    /// <summary>The same output with the camera node this block creates.</summary>
    private const string WpctlWithCamera =
        "Audio\n"
        + " ├─ Sources:\n"
        + " │  *   54. reSpeaker XVF3800 4-Mic Array Analog Stereo [vol: 1.00]\n"
        + " │  \n"
        + " └─ Streams:\n"
        + "\n"
        + "Video\n"
        + " ├─ Devices:\n"
        + " │  \n"
        + " ├─ Sinks:\n"
        + " │  \n"
        + " ├─ Sources:\n"
        + " │  *   61. FrameLinkCam                        [vol: 1.00]\n"
        + " │  \n"
        + " └─ Streams:\n";

    /// <summary>The whole capture, shared with the audio block's tests.</summary>
    private const string WpctlSettled = WpctlCaptures.Settled;

    /// <summary>The same frame before WirePlumber has built anything.</summary>
    private const string WpctlUnsettled = WpctlCaptures.Unsettled;

    /// <summary>What a frame whose WirePlumber fragment never loaded looks like.</summary>
    private const string WpctlWithStockCamera =
        "Video\n"
        + " ├─ Devices:\n"
        + " │      44. imx708                              [libcamera]\n"
        + " │  \n"
        + " ├─ Sources:\n"
        + " │      45. imx708 (V4L2)                       [vol: 1.00]\n"
        + " │  *   61. FrameLinkCam                        [vol: 1.00]\n"
        + " │  \n"
        + " └─ Streams:\n";

    [Fact]
    public void The_camera_unit_is_byte_identical_to_the_v1_reference()
    {
        var reference = V1Files.Read("/home/framelink/.config/systemd/user/framelink-camera.service");

        Assert.Equal(reference, CameraUnitResource.DesiredContent());

        // The catalog records the hash of that file. Asserting it as well is not redundant: it is
        // the number a person can check against the inventory's KEY_FILE_HASHES block by eye,
        // without running anything.
        Assert.Equal(
            "a2c9ef326c8d53a7bf17086e786876b447a3c385e088948a19ca23c5b1e75e3e",
            Sha256(CameraUnitResource.DesiredContent()));
    }

    [Fact]
    public void The_wireplumber_fragment_is_byte_identical_to_the_v1_reference()
    {
        var reference = V1Files.Read(
            "/home/framelink/.config/wireplumber/wireplumber.conf.d/99-framelink-camera.conf");

        Assert.Equal(reference, WirePlumberCameraMonitorsResource.DesiredContent);

        // Both keys, because one resource sets two and a fragment carrying only the libcamera line
        // would still hash differently from a stale one — it is worth naming what must be in it.
        Assert.Contains("monitor.libcamera = disabled", WirePlumberCameraMonitorsResource.DesiredContent, StringComparison.Ordinal);
        Assert.Contains("monitor.v4l2 = disabled", WirePlumberCameraMonitorsResource.DesiredContent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_portal_drop_in_is_byte_identical_to_the_v1_reference()
    {
        var reference = V1Files.Read(
            "/home/framelink/.config/systemd/user/xdg-desktop-portal.service.d/desktop.conf");

        Assert.Equal(reference, PortalDesktopDropInResource.DesiredContent);
    }

    [Fact]
    public void The_video_section_of_a_real_wpctl_capture_is_read_without_confusing_it_with_audio()
    {
        // The v1 frame had a microphone array under Audio and nothing under Video. A parser that
        // loses track of the section boundary reports the array as a camera, which would make
        // `camera.pipewire-node.framelink-cam` report a healthy frame.
        Assert.Empty(WpctlStatus.Entries(WpctlAudioOnly, WpctlStatus.Video, WpctlStatus.Sources));
        Assert.Equal(
            ["reSpeaker XVF3800 4-Mic Array Analog Stereo"],
            WpctlStatus.Entries(WpctlAudioOnly, "Audio", WpctlStatus.Sources));

        Assert.Equal(
            ["FrameLinkCam"],
            WpctlStatus.Entries(WpctlWithCamera, WpctlStatus.Video, WpctlStatus.Sources));

        // A name with spaces keeps them, and the trailing bracket is not part of it.
        Assert.Equal(["imx708"], WpctlStatus.Entries(WpctlWithStockCamera, WpctlStatus.Video, WpctlStatus.Devices));
    }

    [Fact]
    public async Task The_camera_node_is_in_sync_only_when_it_is_the_one_camera()
    {
        var session = new FakeUserSession();
        var resource = new CameraNodeResource(session, NoSoundHardware());

        session.Answers["wpctl status"] = new ProcessResult(0, WpctlWithCamera, string.Empty);
        Assert.True((await resource.ObserveAsync(None)).InSync);

        session.Answers["wpctl status"] = new ProcessResult(0, WpctlAudioOnly, string.Empty);
        var missing = await resource.ObserveAsync(None);

        Assert.False(missing.InSync);
        Assert.Contains("no camera at all", missing.Observed, StringComparison.Ordinal);

        session.Answers["wpctl status"] = new ProcessResult(0, WpctlWithStockCamera, string.Empty);
        var crowded = await resource.ObserveAsync(None);

        Assert.False(crowded.InSync);
        Assert.Contains("imx708 (V4L2)", crowded.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_camera_node_restarts_the_unit_that_is_lying_about_being_healthy()
    {
        // The whole reason this resource exists: `systemctl is-active` says `active` while
        // gst-launch is hung in shutdown and the node is gone, so the Act is a restart rather than
        // anything to do with the unit file.
        var session = new FakeUserSession();
        var resource = new CameraNodeResource(session, NoSoundHardware());

        var action = await resource.ActAsync(None);

        Assert.Contains("systemctl --user restart framelink-camera.service", session.Commands);
        Assert.Contains("framelink-camera.service", action.Change, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wpctl_that_cannot_be_run_is_drift_and_not_an_unevaluable_observation()
    {
        var session = new FakeUserSession { Default = new ProcessResult(127, string.Empty, "wpctl: command not found") };
        var observation = await new CameraNodeResource(session, NoSoundHardware()).ObserveAsync(None);

        // Unevaluable is reserved for an authority off the device that did not answer. A local read
        // that failed has learned something real, and must escalate on the ordinary schedule.
        Assert.Equal(ObservationOutcome.Drifted, observation.Outcome);
        Assert.Contains("command not found", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_wireplumber_fragment_is_drifted_when_the_stock_camera_is_still_showing()
    {
        using var files = new TemporaryFiles();
        var session = new FakeUserSession();
        var resource = new WirePlumberCameraMonitorsResource(files.Files, session);

        files.Seed(resource.Path, WirePlumberCameraMonitorsResource.DesiredContent);

        // An empty Video section is the correct state after this step and before the node exists —
        // guide 6 step 4 says so outright.
        session.Answers["wpctl status"] = new ProcessResult(0, WpctlAudioOnly, string.Empty);
        Assert.True((await resource.ObserveAsync(None)).InSync);

        session.Answers["wpctl status"] = new ProcessResult(0, WpctlWithCamera, string.Empty);
        Assert.True((await resource.ObserveAsync(None)).InSync);

        // A byte-perfect fragment that WirePlumber never loaded is the fault this half of Observe
        // exists for, and the hash alone calls that frame healthy.
        session.Answers["wpctl status"] = new ProcessResult(0, WpctlWithStockCamera, string.Empty);
        var loaded = await resource.ObserveAsync(None);

        Assert.False(loaded.InSync);
        Assert.Contains("imx708", loaded.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writing_the_wireplumber_fragment_hands_it_to_the_user_and_restarts_wireplumber()
    {
        using var files = new TemporaryFiles();
        var session = new FakeUserSession();
        var resource = new WirePlumberCameraMonitorsResource(files.Files, session);

        await resource.ActAsync(None);

        Assert.Equal(WirePlumberCameraMonitorsResource.DesiredContent, files.Read(resource.Path));
        Assert.Contains(resource.Path, session.Owned);
        Assert.Contains("systemctl --user restart wireplumber.service", session.Commands);

        // The `99-` prefix is load-bearing: fragments load in name order and the last one wins.
        Assert.EndsWith("/wireplumber.conf.d/99-framelink-camera.conf", resource.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_portal_drop_in_is_drifted_when_systemd_has_not_read_it()
    {
        using var files = new TemporaryFiles();
        var session = new FakeUserSession();
        var resource = new PortalDesktopDropInResource(files.Files, session);

        files.Seed(resource.Path, PortalDesktopDropInResource.DesiredContent);

        // The file is perfect and systemd is running the portal with no environment at all — a
        // write that never got its daemon-reload, which a content compare calls healthy.
        session.Answers["systemctl --user show xdg-desktop-portal.service -p Environment"] =
            new ProcessResult(0, "Environment=", string.Empty);

        var stale = await resource.ObserveAsync(None);

        Assert.False(stale.InSync);
        Assert.Contains("systemd reports", stale.Observed, StringComparison.Ordinal);

        session.Answers["systemctl --user show xdg-desktop-portal.service -p Environment"] =
            new ProcessResult(0, "Environment=XDG_CURRENT_DESKTOP=labwc", string.Empty);

        Assert.True((await resource.ObserveAsync(None)).InSync);
    }

    [Fact]
    public async Task Writing_the_portal_drop_in_reloads_and_restarts_the_portal()
    {
        using var files = new TemporaryFiles();
        var session = new FakeUserSession();
        var resource = new PortalDesktopDropInResource(files.Files, session);

        await resource.ActAsync(None);

        Assert.Equal("[Service]\nEnvironment=XDG_CURRENT_DESKTOP=labwc\n", files.Read(resource.Path));
        Assert.Contains("systemctl --user daemon-reload", session.Commands);
        Assert.Contains("systemctl --user restart xdg-desktop-portal.service", session.Commands);

        // A drop-in, not a session-wide export, because the portal is D-Bus-activated and the
        // cold-boot path never runs a shell profile.
        Assert.EndsWith("/.config/systemd/user/xdg-desktop-portal.service.d/desktop.conf", resource.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_camera_permission_is_read_back_from_the_store_and_written_with_an_empty_application_id()
    {
        var session = new FakeUserSession();
        var resource = new PortalCameraPermissionResource(session);

        const string Lookup =
            "busctl --user call org.freedesktop.impl.portal.PermissionStore "
            + "/org/freedesktop/impl/portal/PermissionStore org.freedesktop.impl.portal.PermissionStore Lookup ss devices camera";

        session.Answers[Lookup] = new ProcessResult(0, "a{sas}v 1 \"\" 1 \"yes\" y 0", string.Empty);
        Assert.True((await resource.ObserveAsync(None)).InSync);

        // Unset is what pops the dialog nobody will ever click. It is not `no`, and it must not
        // read as one.
        session.Answers[Lookup] = new ProcessResult(1, string.Empty, "No entry for camera");
        var unset = await resource.ObserveAsync(None);

        Assert.False(unset.InSync);
        Assert.Contains("No entry for camera", unset.Observed, StringComparison.Ordinal);

        await resource.ActAsync(None);

        // The empty string between `camera` and `1` is the application id an unsandboxed host
        // Chromium is known by. An argument vector that drops it changes the call's meaning.
        Assert.Contains(
            "busctl --user call org.freedesktop.impl.portal.PermissionStore "
            + "/org/freedesktop/impl/portal/PermissionStore org.freedesktop.impl.portal.PermissionStore "
            + "SetPermission sbssas devices true camera  1 yes",
            session.Commands);
    }

    [Fact]
    public async Task The_camera_interface_is_observed_on_the_bus_and_repaired_by_restarting_the_portal()
    {
        var session = new FakeUserSession();
        var resource = new PortalCameraInterfaceResource(session);

        const string Introspect =
            "busctl --user introspect org.freedesktop.portal.Desktop /org/freedesktop/portal/desktop";

        session.Answers[Introspect] = new ProcessResult(
            0,
            "NAME                                TYPE      SIGNATURE RESULT/VALUE FLAGS\n"
            + "org.freedesktop.portal.Camera       interface -         -            -\n"
            + "org.freedesktop.portal.Email        interface -         -            -",
            string.Empty);

        Assert.True((await resource.ObserveAsync(None)).InSync);

        session.Answers[Introspect] = new ProcessResult(
            0,
            "org.freedesktop.portal.Email        interface -         -            -",
            string.Empty);

        var degraded = await resource.ObserveAsync(None);

        Assert.False(degraded.InSync);
        Assert.Contains("publishes no Camera interface", degraded.Observed, StringComparison.Ordinal);

        await resource.ActAsync(None);
        Assert.Contains("systemctl --user restart xdg-desktop-portal.service", session.Commands);
    }

    [Fact]
    public async Task The_camera_auto_detect_line_is_the_in_sync_predicate_and_the_camera_is_evidence()
    {
        using var files = new TemporaryFiles();
        var session = new FakeUserSession();
        var boot = new MutableBootIdentity();
        var guard = new BootPartitionGuard(files.Files, files.Store, boot, new ManualClock(), new RecordingLog());
        var resource = new CameraAutoDetectResource(files.Files, guard, session, new RecordingLog());

        files.Seed(BootConfigText.ConfigPath, "dtparam=audio=on\ncamera_auto_detect=1\n");
        session.Answers["wpctl status"] = new ProcessResult(0, WpctlAudioOnly, string.Empty);

        var withoutCamera = await resource.ObserveAsync(None);

        // A frame whose ribbon is loose is not a frame whose boot file is wrong. The line is what
        // this resource owns; the camera is what `camera.pipewire-node.framelink-cam` owns, and
        // folding the two together would give one hardware fault two escalation paths.
        Assert.True(withoutCamera.InSync);
        Assert.Contains("no camera is enumerating yet", withoutCamera.Observed, StringComparison.Ordinal);

        session.Answers["wpctl status"] = new ProcessResult(0, WpctlWithCamera, string.Empty);
        var withCamera = await resource.ObserveAsync(None);

        Assert.True(withCamera.InSync);
        Assert.Contains("FrameLinkCam", withCamera.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restoring_the_camera_line_backs_the_boot_file_up_and_changes_exactly_one_line()
    {
        using var files = new TemporaryFiles();
        var session = new FakeUserSession();
        var boot = new MutableBootIdentity();
        var guard = new BootPartitionGuard(files.Files, files.Store, boot, new ManualClock(), new RecordingLog());
        var resource = new CameraAutoDetectResource(files.Files, guard, session, new RecordingLog());

        const string Before = "# comment\ndtparam=audio=on\ndtoverlay=vc4-kms-v3d\n";
        files.Seed(BootConfigText.ConfigPath, Before);

        var action = await resource.ActAsync(None);

        Assert.Equal(Before + "camera_auto_detect=1\n", files.Read(BootConfigText.ConfigPath));
        Assert.Equal(Before, files.Read(BootPartitionGuard.BackupFor(BootConfigText.ConfigPath)));
        Assert.Contains("backed up to", action.Change, StringComparison.Ordinal);

        // The backup goes on the FAT32 boot partition, which is the only filesystem on this card a
        // laptop will mount when somebody has to undo this by hand.
        Assert.StartsWith("/boot/firmware/", BootPartitionGuard.BackupFor(BootConfigText.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_camera_line_that_has_already_been_rolled_back_is_not_written_again()
    {
        using var files = new TemporaryFiles();
        var session = new FakeUserSession();
        var boot = new MutableBootIdentity();
        var log = new RecordingLog();
        var guard = new BootPartitionGuard(files.Files, files.Store, boot, new ManualClock(), log) { BootLimit = 2 };
        var resource = new CameraAutoDetectResource(files.Files, guard, session, log);

        files.Seed(BootConfigText.ConfigPath, "dtparam=audio=on\n");
        session.Answers["wpctl status"] = new ProcessResult(0, WpctlAudioOnly, string.Empty);

        await resource.ActAsync(None);

        // Two boots with the change unconfirmed. The guard puts the backup back, and the resource
        // must then report the rollback rather than quietly writing the same line again.
        boot.Advance();
        files.Seed(BootConfigText.ConfigPath, "dtparam=audio=on\n");
        await resource.ObserveAsync(None);

        boot.Advance();
        var afterRollback = await resource.ObserveAsync(None);

        Assert.False(afterRollback.InSync);
        Assert.Contains("put back automatically", afterRollback.Observed, StringComparison.Ordinal);

        var refused = await resource.ActAsync(None);
        Assert.Contains("already been rolled back once", refused.Change, StringComparison.Ordinal);
    }

    [Fact]
    public void The_camera_block_depends_on_what_the_catalog_document_says_it_does()
    {
        using var files = new TemporaryFiles();
        var graph = DeviceCatalog.BuildGraph(AgentResourceGraphTests.Context(files));
        var order = graph.Ordered.Select(resource => resource.Name).ToList();

        Assert.Equal(
            ["pkg.wireplumber", ConsoleAutologinResource.ResourceName],
            graph.Find(WirePlumberCameraMonitorsResource.ResourceName)!.DependsOn);

        Assert.Equal(
            [
                "pkg.gstreamer1.0-tools",
                "pkg.gstreamer1.0-plugins-base",
                "pkg.gstreamer1.0-libcamera",
                "pkg.gstreamer1.0-pipewire",
                ConsoleAutologinResource.ResourceName,
            ],
            graph.Find(CameraUnitResource.ResourceName)!.DependsOn);

        Assert.Equal(
            [PortalDesktopDropInResource.ResourceName, "pkg.xdg-desktop-portal-gtk"],
            graph.Find(PortalCameraInterfaceResource.ResourceName)!.DependsOn);

        // The node is the last assertion in the chain, behind both the thing that produces it and
        // the thing that stops anything else being produced.
        Assert.Equal(
            [CameraUnitEnabledResource.ResourceName, WirePlumberCameraMonitorsResource.ResourceName],
            graph.Find(CameraNodeResource.ResourceName)!.DependsOn);

        Assert.True(order.IndexOf(CameraUnitResource.ResourceName) < order.IndexOf(CameraUnitEnabledResource.ResourceName));
        Assert.True(order.IndexOf(CameraUnitEnabledResource.ResourceName) < order.IndexOf(CameraNodeResource.ResourceName));
        Assert.True(order.IndexOf(PortalDesktopDropInResource.ResourceName) < order.IndexOf(PortalCameraInterfaceResource.ResourceName));

        // Nothing in the chain gates on adoption: the catalog fixes every value in it, and a
        // pending frame is entitled to a working camera the moment somebody adopts it.
        foreach (var name in new[]
        {
            WirePlumberCameraMonitorsResource.ResourceName,
            CameraUnitResource.ResourceName,
            PortalCameraPermissionResource.ResourceName,
            PortalDesktopDropInResource.ResourceName,
            CameraAutoDetectResource.ResourceName,
        })
        {
            Assert.DoesNotContain(AdoptionResource.ResourceName, graph.Find(name)!.DependsOn);
        }
    }

    [Fact]
    public void The_brick_capable_camera_line_keeps_its_scheduled_slot_at_the_end()
    {
        using var files = new TemporaryFiles();
        var graph = DeviceCatalog.BuildGraph(AgentResourceGraphTests.Context(files));
        var order = graph.Ordered.Select(resource => resource.Name).ToList();

        // §5.5 schedules brick-capable resources last and the display group is the only carve-out.
        // This one is not part of it, so it must come after the whole camera chain it belongs to
        // and after the product layer — it is 77th of 80 in the catalog's own table.
        Assert.True(order.IndexOf(CameraNodeResource.ResourceName) < order.IndexOf(CameraAutoDetectResource.ResourceName));
        Assert.True(order.IndexOf("app.config.livekit-token") < order.IndexOf(CameraAutoDetectResource.ResourceName));

        // And it is genuinely ungated, which is what lets it sit last without blocking anything.
        Assert.Empty(graph.Find(CameraAutoDetectResource.ResourceName)!.DependsOn);
    }

    [Fact]
    public void The_app_tells_the_agent_a_call_ended_so_the_recycle_has_something_to_fire_on()
    {
        var app = AgentButtonTests.Asset("frame-app.js");
        var stage = AgentButtonTests.Asset("frame-stage.js");

        // §2.10's camera recycle is event-triggered, and this is the event. Everything downstream
        // of it was already in place — the channel raises CallEnded, the supervisor queues a
        // restart, the interlock covers it — while the app was still sending `{event:'call-end'}`
        // to the GPIO daemon's WebSocket, a port that no longer exists. A chain that is correct
        // everywhere except its first link fires never, and nothing reports it.
        Assert.Contains("frameLinkStage.callEnded()", app, StringComparison.Ordinal);
        Assert.Contains(PageMessage.KindCallEnded, stage, StringComparison.Ordinal);
        Assert.DoesNotContain("'call-end'", app, StringComparison.Ordinal);
    }

    [Fact]
    public void The_per_call_camera_recycle_is_interlocked_against_resources_that_exist()
    {
        using var files = new TemporaryFiles();
        var graph = DeviceCatalog.BuildGraph(AgentResourceGraphTests.Context(files));

        // §2.10's interlock is asked about resource *names*, so an id that drifted away from the
        // catalog would silently interlock against nothing — the recycle would then race an apply
        // and produce exactly the interference that makes "which change broke it" unanswerable.
        foreach (var name in Supervisor.CameraResources)
        {
            Assert.NotNull(graph.Find(name));
        }

        Assert.Equal(CameraUnitResource.UnitName, Supervisor.CameraUnitName);
    }

    [Fact]
    public void The_default_marker_is_read_off_the_line_the_name_is_trimmed_out_of()
    {
        // `*` is one of the tree characters Entries trims away, so the two questions cannot share
        // an answer: DefaultOf has to see the raw line. `@DEFAULT_AUDIO_SINK@` resolves to exactly
        // the entry carrying that marker, which is why its absence is a readable fact rather than
        // an inference.
        Assert.Equal(
            "reSpeaker XVF3800 4-Mic Array Analog Stereo",
            WpctlStatus.DefaultOf(WpctlSettled, WpctlStatus.Audio, WpctlStatus.Sinks));

        Assert.Equal("FrameLinkCam", WpctlStatus.DefaultOf(WpctlSettled, WpctlStatus.Video, WpctlStatus.Sources));

        // The stock-camera capture has two video sources and marks one. A parser that returned the
        // first entry rather than the marked one would name imx708 (V4L2).
        Assert.Equal("FrameLinkCam", WpctlStatus.DefaultOf(WpctlWithStockCamera, WpctlStatus.Video, WpctlStatus.Sources));

        Assert.Null(WpctlStatus.DefaultOf(WpctlUnsettled, WpctlStatus.Audio, WpctlStatus.Sinks));
        Assert.Null(WpctlStatus.DefaultOf(WpctlSettled, WpctlStatus.Video, WpctlStatus.Devices));
    }

    [Fact]
    public async Task A_wireplumber_that_has_not_built_its_graph_yet_is_not_a_missing_camera()
    {
        // The measured cascade, one boot of it. `PipeWire is offering no camera at all` was the
        // frame's own delta seconds after a boot, and it is drift, so it was acted on, and §2.4
        // makes acting reboot — which starts the next boot, which asks too early again. Six reboots
        // on the worst night.
        var session = new FakeUserSession();
        var resource = new CameraNodeResource(session, WithSoundHardware());

        session.Answers["wpctl status"] = new ProcessResult(0, WpctlUnsettled, string.Empty);
        var settling = await resource.ObserveAsync(None);

        Assert.Equal(ObservationOutcome.Unevaluable, settling.Outcome);
        Assert.Contains("has not published a media graph yet", settling.Observed, StringComparison.Ordinal);

        // The delta says "could not be determined" rather than "observed", which is the half of the
        // fix that reaches the operator: the other wording sends somebody hunting a camera fault
        // that does not exist.
        Assert.Contains("could not be determined", settling.Delta, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_camera_that_is_genuinely_missing_from_a_built_graph_is_still_drift()
    {
        // The trap this gate had to avoid: a resource whose job is to assert the camera node exists
        // cannot be gated on the camera node existing, or it can never report anything. So the gate
        // reads the audio half — and a graph WirePlumber has finished building, with an array in it
        // and no camera, reports exactly what it did before.
        var session = new FakeUserSession();
        var resource = new CameraNodeResource(session, WithSoundHardware());

        session.Answers["wpctl status"] = new ProcessResult(0, WpctlAudioOnly, string.Empty);
        var missing = await resource.ObserveAsync(None);

        Assert.Equal(ObservationOutcome.Drifted, missing.Outcome);
        Assert.Contains("no camera at all", missing.Observed, StringComparison.Ordinal);

        // And a settled graph with the node in it is in sync, gate or no gate.
        session.Answers["wpctl status"] = new ProcessResult(0, WpctlSettled, string.Empty);
        Assert.True((await resource.ObserveAsync(None)).InSync);
    }

    [Fact]
    public async Task A_frame_with_no_sound_hardware_does_not_wait_behind_an_audio_fact()
    {
        // §5.3's virtual agents have no ALSA at all, so the graph's audio half is empty for ever
        // and the gate would never open. The escape is LoginUserSession.ReadinessAsync's: report
        // settled, and let the resource say what it genuinely finds.
        var session = new FakeUserSession();
        var resource = new CameraNodeResource(session, NoSoundHardware());

        session.Answers["wpctl status"] = new ProcessResult(0, WpctlUnsettled, string.Empty);
        var observation = await resource.ObserveAsync(None);

        Assert.Equal(ObservationOutcome.Drifted, observation.Outcome);
        Assert.Contains("no camera at all", observation.Observed, StringComparison.Ordinal);
    }

    /// <summary>A machine whose kernel is publishing an ALSA card.</summary>
    private static MemorySystemFiles WithSoundHardware()
    {
        var files = new MemorySystemFiles();
        files[AlsaCards.CardsPath] = " 0 [Array          ]: USB-Audio - reSpeaker XVF3800 4-Mic Array\n";
        return files;
    }

    /// <summary>A machine with no ALSA at all — §5.3's virtual agent.</summary>
    private static MemorySystemFiles NoSoundHardware() => new();

    private static string Sha256(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLower(CultureInfo.InvariantCulture);
}

/// <summary>
/// Files read back out of the frozen v1 inventory.
/// </summary>
/// <remarks>
/// The inventory dumps whole files under a <c>##### &lt;path&gt;</c> marker, so a resource that
/// claims to carry a v1 file across can be compared against the bytes rather than against a
/// transcription of them. §7.1's "never asserted from memory", applied to a unit file.
/// </remarks>
internal static class V1Files
{
    /// <summary>The captured content of <paramref name="path"/> as it was on the v1 frame.</summary>
    public static string Read(string path)
    {
        var inventory = Path.Combine(GuiFreshnessTests.RepositoryRoot(), "reference", "v1-state-inventory.txt");
        var marker = "##### " + path;
        var body = new List<string>();
        var inside = false;

        foreach (var raw in File.ReadLines(inventory))
        {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith("#####", StringComparison.Ordinal))
            {
                if (inside)
                {
                    break;
                }

                inside = string.Equals(line.TrimEnd(), marker, StringComparison.Ordinal);
                continue;
            }

            if (!inside)
            {
                continue;
            }

            if (line.StartsWith("=====", StringComparison.Ordinal))
            {
                break;
            }

            body.Add(line);
        }

        Assert.True(body.Count > 0, $"{path} is not in the v1 inventory, so there is nothing to compare against.");

        // The capture is line-based, so trailing blank lines are the separator rather than file
        // content; every file here ends with exactly one newline.
        while (body.Count > 0 && body[^1].Length == 0)
        {
            body.RemoveAt(body.Count - 1);
        }

        return string.Join('\n', body) + "\n";
    }
}
