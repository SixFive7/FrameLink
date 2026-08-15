using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;

namespace FrameLink.Tests;

/// <summary>
/// Guide 4's audio block — the mixer values, the state file that replays them, the array
/// firmware and the two settings that decide who owns card 0.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every expected value here is transcribed from <c>reference/v1-state-inventory.txt</c></b>,
/// the frozen v1 reference of Precondition zero, and the fixtures below are that file's own
/// <c>ALSA_MIXER</c>, <c>ALSA_CARDS</c> and <c>MODPROBE_D</c> text. One test reads the reference
/// directly and compares it against the catalog, so the numbers cannot drift from the frame that
/// defines parity without a test saying so (§7.1: never asserted from memory).
/// </para>
/// <para>
/// <b>The fault this block exists to prevent has no symptom worth the name.</b> A frame with
/// <c>PCM,0</c> correct and <c>PCM,1</c> at its shipped <c>40/60</c> works perfectly and is
/// eighteen decibels too quiet, which is why several tests below assert not merely that the
/// values converge but that the two stages are separately named and separately reported.
/// </para>
/// </remarks>
public sealed class AgentAudioTests
{
    /// <summary>The v1 reference's own capture of the stereo playback stage.</summary>
    private const string Pcm0Correct = """
        Simple mixer control 'PCM',0
          Capabilities: pvolume pswitch
          Playback channels: Front Left - Front Right
          Limits: Playback 0 - 60
          Mono:
          Front Left: Playback 60 [100%] [0.00dB] [on]
          Front Right: Playback 60 [100%] [0.00dB] [on]
        """;

    /// <summary>The v1 reference's own capture of the second, mono playback stage.</summary>
    private const string Pcm1Correct = """
        Simple mixer control 'PCM',1
          Capabilities: pvolume pvolume-joined pswitch pswitch-joined
          Playback channels: Mono
          Limits: Playback 0 - 60
          Mono: Playback 60 [100%] [0.00dB] [on]
        """;

    /// <summary>
    /// The same control as a fresh array ships it: <c>40/60</c>, which guide 4 measures at −20 dB.
    /// </summary>
    /// <remarks>
    /// Constructed rather than captured — the reference is of a <i>corrected</i> frame, so the
    /// broken state exists nowhere to copy from. The value is the catalog's, the shape is the
    /// captured one.
    /// </remarks>
    private const string Pcm1Shipped = """
        Simple mixer control 'PCM',1
          Capabilities: pvolume pvolume-joined pswitch pswitch-joined
          Playback channels: Mono
          Limits: Playback 0 - 60
          Mono: Playback 40 [67%] [-20.00dB] [on]
        """;

    private const string Pcm0Muted = """
        Simple mixer control 'PCM',0
          Capabilities: pvolume pswitch
          Playback channels: Front Left - Front Right
          Limits: Playback 0 - 60
          Mono:
          Front Left: Playback 60 [100%] [0.00dB] [off]
          Front Right: Playback 60 [100%] [0.00dB] [off]
        """;

    private const string Headset0Correct = """
        Simple mixer control 'Headset',0
          Capabilities: cvolume cswitch
          Capture channels: Front Left - Front Right
          Limits: Capture 0 - 60
          Front Left: Capture 60 [100%] [0.00dB] [on]
          Front Right: Capture 60 [100%] [0.00dB] [on]
        """;

    private const string Headset1Correct = """
        Simple mixer control 'Headset',1
          Capabilities: cvolume cvolume-joined cswitch cswitch-joined
          Capture channels: Mono
          Limits: Capture 0 - 60
          Mono: Capture 60 [100%] [0.00dB] [on]
        """;

    /// <summary>The v1 reference's <c>ALSA_CARDS</c> capture.</summary>
    private const string ArrayIsCardZero = """
         0 [Array          ]: USB-Audio - reSpeaker XVF3800 4-Mic Array
                              Seeed Studio reSpeaker XVF3800 4-Mic Array at usb-xhci-hcd.0-1, high speed
        """;

    /// <summary>The measured cold-boot failure: an HDMI card took index 0 first.</summary>
    private const string HdmiTookCardZero = """
         0 [vc4hdmi0       ]: vc4-hdmi - vc4-hdmi-0
                              vc4-hdmi-0
         1 [Array          ]: USB-Audio - reSpeaker XVF3800 4-Mic Array
                              Seeed Studio reSpeaker XVF3800 4-Mic Array at usb-xhci-hcd.0-1, high speed
        """;

    [Fact]
    public void The_mixer_parser_reads_the_v1_references_own_capture()
    {
        var stereo = AlsaMixer.Parse("PCM,0", Pcm0Correct);

        Assert.Null(stereo.Failure);
        Assert.Equal(60, stereo.Maximum);
        Assert.Equal(["Front Left", "Front Right"], stereo.Channels.Select(channel => channel.Name));
        Assert.All(stereo.Channels, channel => Assert.Equal(60, channel.Value));
        Assert.All(stereo.Channels, channel => Assert.True(channel.Switch));
        Assert.All(stereo.Channels, channel => Assert.Equal("0.00dB", channel.Decibels));

        var mono = AlsaMixer.Parse("PCM,1", Pcm1Correct);

        Assert.Equal(60, mono.Maximum);
        Assert.Equal("Mono", Assert.Single(mono.Channels).Name);
        Assert.Equal(60, mono.Channels[0].Value);
    }

    [Fact]
    public void The_limits_line_is_not_mistaken_for_a_channel_at_zero()
    {
        // `Limits: Playback 0 - 60` parses as a channel called Limits sitting at 0 unless it is
        // excluded, and the effect would be a correctly-configured frame reporting itself silent
        // on every pass — drift, an act, and a reboot, forever.
        var reading = AlsaMixer.Parse("PCM,0", Pcm0Correct);

        Assert.DoesNotContain(reading.Channels, channel => channel.Name.Contains("Limits", StringComparison.Ordinal));
        Assert.DoesNotContain(reading.Channels, channel => channel.Value == 0);

        // Nor is the bare `Mono:` header above the two stereo channels.
        Assert.Equal(2, reading.Channels.Count);
    }

    [Fact]
    public void The_shipped_second_stage_reads_as_minus_twenty_decibels()
    {
        var reading = AlsaMixer.Parse("PCM,1", Pcm1Shipped);
        var channel = Assert.Single(reading.Channels);

        Assert.Equal(40, channel.Value);
        Assert.Equal("-20.00dB", channel.Decibels);
    }

    [Fact]
    public void A_control_that_does_not_exist_is_a_failure_and_not_an_empty_success()
    {
        // `amixer -c 0 sget PCM,1` against an HDMI card that took index 0 prints nothing useful.
        // Reporting "no channels, therefore nothing is wrong" would be the write-only optimism
        // §2.4 exists to refuse.
        var reading = AlsaMixer.Parse("PCM,1", "Simple mixer control 'PCM',1\n");

        Assert.NotNull(reading.Failure);
        Assert.Empty(reading.Channels);
    }

    [Fact]
    public void The_card_list_parser_reads_both_the_array_and_an_hdmi_intruder()
    {
        var good = AlsaCards.Parse(ArrayIsCardZero);
        var card = Assert.Single(good);

        Assert.Equal(0, card.Index);
        Assert.Equal("Array", card.Id);
        Assert.Contains("reSpeaker XVF3800", card.Description, StringComparison.Ordinal);
        Assert.False(AlsaCards.IsHdmi(card));

        var bad = AlsaCards.Parse(HdmiTookCardZero);

        Assert.Equal(2, bad.Count);
        Assert.True(AlsaCards.IsHdmi(bad[0]));
        Assert.Equal("Array", AlsaCards.At(bad, 1)!.Value.Id);
    }

    [Fact]
    public async Task The_hidden_second_stage_is_its_own_resource_with_its_own_delta()
    {
        // The whole reason the catalog splits them: a frame with the obvious control correct and
        // the hidden one at its shipped default is fully functional and merely quiet. One
        // resource reports in sync, the other names the exact control that is wrong.
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Shipped));
        var block = Audio(files, processes);

        var stereo = await Observe(block, AudioCatalog.Pcm0VolumeResourceName);
        var mono = await Observe(block, AudioCatalog.Pcm1VolumeResourceName);

        Assert.True(stereo.InSync);
        Assert.False(mono.InSync);
        Assert.Contains("PCM,1", mono.Delta, StringComparison.Ordinal);
        Assert.Contains("40", mono.Delta, StringComparison.Ordinal);
        Assert.Contains("-20.00dB", mono.Delta, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Both_playback_stages_at_sixty_is_the_converged_state()
    {
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        var block = Audio(files, processes);

        Assert.True((await Observe(block, AudioCatalog.Pcm0VolumeResourceName)).InSync);
        Assert.True((await Observe(block, AudioCatalog.Pcm1VolumeResourceName)).InSync);
        Assert.True((await Observe(block, AudioCatalog.Pcm0SwitchResourceName)).InSync);
        Assert.True((await Observe(block, AudioCatalog.Pcm1SwitchResourceName)).InSync);
        Assert.True((await Observe(block, AudioCatalog.HeadsetCaptureResourceName)).InSync);
    }

    [Fact]
    public async Task Setting_the_hidden_stage_runs_the_command_guide_four_runs()
    {
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Shipped));
        var block = Audio(files, processes);

        var action = await Find(block, AudioCatalog.Pcm1VolumeResourceName).ActAsync(TestContext.Current.CancellationToken);

        Assert.Contains("amixer -c 0 sset PCM,1 60", action.Change, StringComparison.Ordinal);
        Assert.Contains("amixer -c 0 sset PCM,1 60", processes.Commands);
    }

    [Fact]
    public async Task A_fleet_value_above_zero_decibels_is_clamped_rather_than_obeyed()
    {
        // Guide 4: "do not push software gain above 0 dB anywhere in the chain — beyond digital
        // full scale there is no loudness left, only clipping". 60 is 0 dB on this array, so a
        // Fleet Manager cannot make a frame distort by typing a bigger number.
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        var values = FleetValues.From(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AudioCatalog.PlaybackVolumeKey] = "95",
        });

        var block = Audio(files, processes, values);
        var observation = await Observe(block, AudioCatalog.Pcm0VolumeResourceName);

        Assert.True(observation.InSync);
        Assert.Contains("PCM,0=60", observation.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_lower_fleet_value_is_obeyed_because_only_the_ceiling_is_fixed()
    {
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        var values = FleetValues.From(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AudioCatalog.PlaybackVolumeKey] = "45",
        });

        var block = Audio(files, processes, values);
        var observation = await Observe(block, AudioCatalog.Pcm0VolumeResourceName);

        Assert.False(observation.InSync);
        Assert.Contains("PCM,0=45", observation.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_muted_stage_is_a_different_diagnosis_from_a_turned_down_one()
    {
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Muted, Pcm1Correct));
        var block = Audio(files, processes);

        var muted = await Observe(block, AudioCatalog.Pcm0SwitchResourceName);

        Assert.False(muted.InSync);
        Assert.Contains("MUTED", muted.Delta, StringComparison.Ordinal);

        var action = await Find(block, AudioCatalog.Pcm0SwitchResourceName).ActAsync(TestContext.Current.CancellationToken);

        Assert.Contains("amixer -c 0 sset PCM,0 unmute", action.Change, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_capture_resource_covers_both_headset_indices_and_their_switches()
    {
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));

        // Open question 8 keeps the two capture indices as one resource until measurement says
        // otherwise, so both have to be read and both have to be set.
        processes.Answers["amixer -c 0 sget Headset,1"] = new ProcessResult(
            0,
            Headset1Correct.Replace("Capture 60 [100%] [0.00dB] [on]", "Capture 12 [20%] [-32.00dB] [on]", StringComparison.Ordinal),
            string.Empty);

        var block = Audio(files, processes);
        var observation = await Observe(block, AudioCatalog.HeadsetCaptureResourceName);

        Assert.False(observation.InSync);
        Assert.Contains("Headset,1", observation.Delta, StringComparison.Ordinal);

        var action = await Find(block, AudioCatalog.HeadsetCaptureResourceName).ActAsync(TestContext.Current.CancellationToken);

        Assert.Contains("amixer -c 0 sset Headset,0 60", action.Change, StringComparison.Ordinal);
        Assert.Contains("amixer -c 0 sset Headset,1 60", action.Change, StringComparison.Ordinal);
        Assert.Contains("amixer -c 0 sset Headset,1 cap", action.Change, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_mixer_observation_records_what_wireplumber_is_doing()
    {
        // The catalog's strongest untested suspicion: alsa-restore applies asound.state early in
        // boot, then the session starts and WirePlumber applies its own stored per-device volume.
        // Nobody has measured it, so the agent records both facts on every reading and lets the
        // answer fall out of ordinary telemetry.
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        var session = new FakeUserSession();
        session.Answers["systemctl --user is-active wireplumber.service"] = new ProcessResult(0, "active", string.Empty);
        files.Seed("/home/framelink/.local/state/wireplumber/restore-stream", "{}");

        var block = Audio(files, processes, session: session);
        var observation = await Observe(block, AudioCatalog.Pcm1VolumeResourceName);

        Assert.Contains("wireplumber active", observation.Observed, StringComparison.Ordinal);
        Assert.Contains("1 stored device files", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_that_has_not_started_is_reported_but_does_not_itself_make_the_mixer_drift()
    {
        // The predicate stays on the value this resource owns. A resource that refused to conclude
        // until the session was up would act — and therefore reboot — on a frame whose session is
        // broken, spending §2.5's ladder on a fault it cannot fix. The same choice
        // DisplayPanelOverlayResource made about the display probe.
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        var session = new FakeUserSession();
        session.Answers["systemctl --user is-active wireplumber.service"] = new ProcessResult(3, "inactive", string.Empty);

        var block = Audio(files, processes, session: session);
        var observation = await Observe(block, AudioCatalog.Pcm1VolumeResourceName);

        Assert.True(observation.InSync);
        Assert.Contains("wireplumber inactive", observation.Observed, StringComparison.Ordinal);
        Assert.Contains("no stored device state", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_value_changed_after_boot_is_ordinary_drift_whatever_changed_it()
    {
        // Which is what makes "observe after the session is up" correct under either answer to the
        // WirePlumber question: if something reverts the mixer, the next pass sees it, names the
        // control, and carries the evidence about who else was running.
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        var session = new FakeUserSession();
        session.Answers["systemctl --user is-active wireplumber.service"] = new ProcessResult(0, "active", string.Empty);

        var block = Audio(files, processes, session: session);
        Assert.True((await Observe(block, AudioCatalog.Pcm0VolumeResourceName)).InSync);

        processes.Answers["amixer -c 0 sget PCM,0"] = new ProcessResult(
            0,
            Pcm0Correct.Replace("Playback 60 [100%] [0.00dB]", "Playback 22 [37%] [-19.00dB]", StringComparison.Ordinal),
            string.Empty);

        var after = await Observe(block, AudioCatalog.Pcm0VolumeResourceName);

        Assert.False(after.InSync);
        Assert.Contains("wireplumber active", after.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stored_state_parser_reads_the_v1_references_own_control_blocks()
    {
        // Verbatim from reference/v1-state-inventory.txt's ALSA_CARDS state-file capture, which
        // stops part-way through control.4 — so the two blocks below are all of that file the
        // frozen reference actually holds.
        const string Captured = """
            state.Array {
            	control.1 {
            		iface PCM
            		name 'Playback Channel Map'
            		value.0 3
            		value.1 4
            		comment {
            			access 'read volatile'
            			type INTEGER
            			count 2
            			range '0 - 36'
            		}
            	}
            	control.3 {
            		iface MIXER
            		name 'PCM Playback Switch'
            		value.0 true
            		value.1 true
            		comment {
            			access 'read write'
            			type BOOLEAN
            			count 2
            		}
            	}
            }
            """;

        var controls = AsoundState.Parse(Captured, "Array");

        Assert.Equal(2, controls.Count);
        Assert.Equal("Playback Channel Map", controls[0].Name);
        Assert.Equal(["3", "4"], controls[0].Values);

        var playback = AsoundState.Find(controls, "PCM Playback Switch", 0);

        Assert.NotNull(playback);
        Assert.Equal(["true", "true"], playback.Value.Values);

        // The comment block describes the control rather than holding its value, so none of its
        // keys may leak into it: `count 2` becoming a value is how a switch reads as a volume.
        Assert.DoesNotContain("2", controls[0].Values);
    }

    [Fact]
    public void The_stored_state_parser_reads_an_index_and_keeps_the_two_stages_apart()
    {
        var controls = AsoundState.Parse(StoredState("60", "60"), "Array");

        Assert.NotNull(AsoundState.Find(controls, "PCM Playback Volume", 0));
        Assert.NotNull(AsoundState.Find(controls, "PCM Playback Volume", 1));
        Assert.Null(AsoundState.Find(controls, "PCM Playback Volume", 2));
        Assert.Empty(AsoundState.Parse(StoredState("60", "60"), "SomeOtherCard"));
    }

    [Fact]
    public async Task The_stored_state_is_in_sync_when_it_holds_the_validated_levels()
    {
        using var files = new TemporaryFiles();
        files.Seed(AsoundState.StatePath, StoredState("60", "60"));

        var block = Audio(files, Mixer(files, (Pcm0Correct, Pcm1Correct)));
        var observation = await Observe(block, AlsaStoredStateResource.ResourceName);

        Assert.True(observation.InSync);
    }

    [Fact]
    public async Task A_stored_state_holding_the_shipped_hidden_level_is_drift_naming_that_control()
    {
        // The fault this resource exists for: the running mixer is right, so every live check
        // passes, and the next reboot brings the frame back quiet.
        using var files = new TemporaryFiles();
        files.Seed(AsoundState.StatePath, StoredState("60", "40"));

        var block = Audio(files, Mixer(files, (Pcm0Correct, Pcm1Correct)));
        var observation = await Observe(block, AlsaStoredStateResource.ResourceName);

        Assert.False(observation.InSync);
        Assert.Contains("'PCM Playback Volume' index 1 is stored as 40, not 60", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_stored_control_reports_what_the_file_does_hold()
    {
        // The v1 reference's capture of asound.state is truncated before the volume controls, so
        // their exact stored spelling is not in the frozen reference. The first escalation
        // therefore has to carry the real names rather than only the ones that were looked for.
        using var files = new TemporaryFiles();
        files.Seed(AsoundState.StatePath, """
            state.Array {
            	control.3 {
            		iface MIXER
            		name 'PCM Playback Switch'
            		value.0 true
            		value.1 true
            	}
            }
            """);

        var block = Audio(files, Mixer(files, (Pcm0Correct, Pcm1Correct)));
        var observation = await Observe(block, AlsaStoredStateResource.ResourceName);

        Assert.False(observation.InSync);
        Assert.Contains("is not in the file", observation.Observed, StringComparison.Ordinal);
        Assert.Contains("'PCM Playback Switch'[0]", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_absent_stored_state_file_is_drift_and_alsactl_store_is_the_fix()
    {
        using var files = new TemporaryFiles();
        files.Seed(AlsaCards.CardsPath, ArrayIsCardZero);

        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        var block = Audio(files, processes);
        var observation = await Observe(block, AlsaStoredStateResource.ResourceName);

        Assert.False(observation.InSync);
        Assert.Contains("does not exist", observation.Observed, StringComparison.Ordinal);

        var action = await Find(block, AlsaStoredStateResource.ResourceName).ActAsync(TestContext.Current.CancellationToken);

        Assert.Equal("alsactl store", action.Change);
        Assert.Contains("alsactl store", processes.Commands);
    }

    [Fact]
    public void The_firmware_version_parser_reads_the_tools_own_replies()
    {
        Assert.Equal("2 0 10", XvfHost.Version("Device (USB)::device_init() -- Found device VID: 10374 PID: 26 interface: 3\nVERSION 2 0 10\n"));
        Assert.Equal("2 0 6", XvfHost.Version("VERSION 2 0 6"));
        Assert.Null(XvfHost.Version("Device (USB)::device_init() -- No device found\n"));
    }

    [Fact]
    public void The_gpo_parser_reads_five_values_with_or_without_the_command_name()
    {
        Assert.Equal([0, 0, 0, 1, 0], XvfHost.GpoValues("GPO_READ_VALUES 0 0 0 1 0")!);
        Assert.Equal([0, 1, 0, 1, 0], XvfHost.GpoValues("Found device\n0 1 0 1 0\n")!);
        Assert.Null(XvfHost.GpoValues("Device (USB)::device_init() -- No device found\n"));
    }

    [Fact]
    public async Task The_array_firmware_is_in_sync_at_the_version_the_v1_reference_records()
    {
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        Tool(files, processes, "VERSION 2 0 10");

        var observation = await Observe(Audio(files, processes), XvfFirmwareResource.ResourceName);

        Assert.True(observation.InSync);
        Assert.Equal("2 0 10", observation.Observed);
    }

    [Fact]
    public async Task Shipping_firmware_is_drift_because_the_volume_path_differs()
    {
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        Tool(files, processes, "VERSION 2 0 6");

        var observation = await Observe(Audio(files, processes), XvfFirmwareResource.ResourceName);

        Assert.False(observation.InSync);
        Assert.Equal("expected '2 0 10', observed '2 0 6'", observation.Delta);
    }

    [Fact]
    public async Task An_unauthorised_frame_starts_no_process_at_all_when_the_firmware_is_wrong()
    {
        // The guarantee the operator asked for: a DFU flash can brick the mic array, and ordinary
        // convergence must never perform one as a side effect. The assertion is not that dfu-util
        // was not *successful* — it is that nothing ran.
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        Tool(files, processes, "VERSION 2 0 6", withFirmwareImage: true);

        var block = Audio(files, processes);
        processes.Commands.Clear();

        var action = await Find(block, XvfFirmwareResource.ResourceName).ActAsync(TestContext.Current.CancellationToken);

        Assert.Empty(processes.Commands);
        Assert.DoesNotContain("dfu-util -R", processes.Commands);
        Assert.Contains("refused to flash", action.Change, StringComparison.Ordinal);
        Assert.Contains(XvfFirmwareResource.AuthorisationKey, action.Change, StringComparison.Ordinal);

        // The refusal still names the exact command, because §2.5 carries the change text into the
        // operator's notification — the escalation *is* the request for permission.
        Assert.Contains("dfu-util -R -e -a 1 -D", action.Change, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unadopted_frame_cannot_be_authorised_because_it_has_no_settings_at_all()
    {
        // §3.3 gives a pending device nothing — no configuration, no token, no commands — so the
        // authorisation defaulting to absent means a frame nobody has adopted cannot flash even
        // in principle.
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        Tool(files, processes, "VERSION 2 0 6", withFirmwareImage: true);

        var block = Audio(files, processes, FleetValues.None);
        processes.Commands.Clear();

        await Find(block, XvfFirmwareResource.ResourceName).ActAsync(TestContext.Current.CancellationToken);

        Assert.Empty(processes.Commands);
    }

    [Fact]
    public async Task An_authorisation_for_a_different_version_does_not_authorise_this_flash()
    {
        // The setting carries the version rather than a boolean, so a switch left on cannot
        // silently authorise a different flash the day the pin moves.
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        Tool(files, processes, "VERSION 2 0 6", withFirmwareImage: true);

        foreach (var claimed in new[] { "true", "yes", "2.0.7", "1" })
        {
            var values = FleetValues.From(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [XvfFirmwareResource.AuthorisationKey] = claimed,
            });

            var block = Audio(files, processes, values);
            processes.Commands.Clear();

            await Find(block, XvfFirmwareResource.ResourceName).ActAsync(TestContext.Current.CancellationToken);

            Assert.Empty(processes.Commands);
        }
    }

    [Fact]
    public async Task An_authorised_flash_runs_dfu_util_with_guide_fours_own_arguments()
    {
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        Tool(files, processes, "VERSION 2 0 6", withFirmwareImage: true);

        var values = FleetValues.From(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [XvfFirmwareResource.AuthorisationKey] = XvfFirmwareResource.PinnedVersion,
        });

        var clock = new ManualClock();
        var block = Audio(files, processes, values, clock: clock);
        processes.Commands.Clear();

        var action = await Find(block, XvfFirmwareResource.ResourceName).ActAsync(TestContext.Current.CancellationToken);

        var image = XvfHost.FirmwarePath(XvfHost.AgentDirectory, XvfFirmwareResource.PinnedVersion);

        Assert.Contains($"dfu-util -R -e -a 1 -D {image}", processes.Commands);
        Assert.Contains("2.0.10", action.Gloss, StringComparison.Ordinal);

        // The settle delay is part of the Act, not of Verify: the array re-enumerates on USB and
        // a version read issued into that window answers for a device that is not there yet.
        Assert.Contains(XvfFirmwareResource.Settle, clock.Delays);
    }

    [Fact]
    public async Task An_authorised_flash_with_no_image_on_the_frame_still_refuses()
    {
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        Tool(files, processes, "VERSION 2 0 6");

        var values = FleetValues.From(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [XvfFirmwareResource.AuthorisationKey] = XvfFirmwareResource.PinnedVersion,
        });

        var block = Audio(files, processes, values);
        processes.Commands.Clear();

        var action = await Find(block, XvfFirmwareResource.ResourceName).ActAsync(TestContext.Current.CancellationToken);

        Assert.Empty(processes.Commands);
        Assert.Contains("is not on this frame", action.Change, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_amplifier_pin_is_the_third_of_five_and_the_other_two_are_telemetry()
    {
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        Tool(files, processes, "VERSION 2 0 10", gpo: "GPO_READ_VALUES 0 0 0 1 0");

        var observation = await Observe(Audio(files, processes), XvfAmplifierResource.ResourceName);

        Assert.True(observation.InSync);
        Assert.Contains("X0D31=0", observation.Observed, StringComparison.Ordinal);
        Assert.Contains("mute button X0D30=0", observation.Observed, StringComparison.Ordinal);
        Assert.Contains("LED ring X0D33=1", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_disabled_amplifier_is_drift_and_the_write_is_the_guides_own_command()
    {
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        Tool(files, processes, "VERSION 2 0 10", gpo: "GPO_READ_VALUES 0 0 1 1 0");

        var block = Audio(files, processes);
        var observation = await Observe(block, XvfAmplifierResource.ResourceName);

        Assert.False(observation.InSync);

        var action = await Find(block, XvfAmplifierResource.ResourceName).ActAsync(TestContext.Current.CancellationToken);

        Assert.Contains("GPO_WRITE_VALUE 31 0", action.Change, StringComparison.Ordinal);
        Assert.Contains(processes.Commands, command => command.Contains("GPO_WRITE_VALUE 31 0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_pressed_mute_button_is_reported_without_being_treated_as_drift()
    {
        // X0D30 is the hardware Mute button. It is not agent-settable, so it cannot be a resource
        // — but a pressed one means mic capture is silent while everything else reports healthy,
        // which is exactly the sentence an operator needs in front of them.
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        Tool(files, processes, "VERSION 2 0 10", gpo: "GPO_READ_VALUES 0 1 0 1 0");

        var observation = await Observe(Audio(files, processes), XvfAmplifierResource.ResourceName);

        Assert.True(observation.InSync);
        Assert.Contains("mute button X0D30=1", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_control_tool_is_in_sync_only_when_the_array_answers_it()
    {
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        Tool(files, processes, "VERSION 2 0 10");

        Assert.True((await Observe(Audio(files, processes), XvfHostToolResource.ResourceName)).InSync);

        // Present, executable, and the array does not answer — a different fault from a missing
        // binary, and the delta has to say which.
        var directory = XvfHost.ToolDirectory(XvfHost.AgentDirectory);
        processes.Answers[$"env -C {directory} LD_LIBRARY_PATH={directory} {directory}/xvf_host VERSION"] =
            new ProcessResult(1, string.Empty, "Device (USB)::device_init() -- No device found");

        var silent = await Observe(Audio(files, processes), XvfHostToolResource.ResourceName);

        Assert.False(silent.InSync);
        Assert.Contains("did not answer", silent.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_frame_with_no_control_tool_is_refused_rather_than_sent_to_a_guessed_url()
    {
        // Open question 3 is open: the catalog's reading is a pinned, checksum-verified upstream
        // fetch, and no pin has been chosen. Inventing one would be the fabrication §0.4 forbids,
        // so the resource escalates carrying the reason instead.
        using var files = new TemporaryFiles();
        files.Seed(AlsaCards.CardsPath, ArrayIsCardZero);

        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        var block = Audio(files, processes);

        var observation = await Observe(block, XvfHostToolResource.ResourceName);

        Assert.False(observation.InSync);
        Assert.Contains(XvfHost.AgentDirectory, observation.Observed, StringComparison.Ordinal);

        var action = await Find(block, XvfHostToolResource.ResourceName).ActAsync(TestContext.Current.CancellationToken);

        Assert.Contains("no pinned upstream source", action.Change, StringComparison.Ordinal);
        Assert.Contains("open question 3", action.Change, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tool_that_is_present_but_not_executable_is_repaired_rather_than_refused()
    {
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        Tool(files, processes, "VERSION 2 0 10", executable: false);

        var block = Audio(files, processes);
        var observation = await Observe(block, XvfHostToolResource.ResourceName);

        Assert.False(observation.InSync);
        Assert.Contains("not executable", observation.Observed, StringComparison.Ordinal);

        var action = await Find(block, XvfHostToolResource.ResourceName).ActAsync(TestContext.Current.CancellationToken);

        Assert.Contains("chmod +x", action.Change, StringComparison.Ordinal);
        Assert.True((await Observe(Audio(files, processes), XvfHostToolResource.ResourceName)).InSync);
    }

    [Fact]
    public async Task The_tool_is_also_found_where_guide_four_puts_it()
    {
        // A frame built by hand has the tree under ~/xvf3800, which is what the v1 reference
        // records. Finding it is not the same as installing it, and the observed text says which
        // directory answered.
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        var session = new FakeUserSession();
        var root = session.HomeDirectory + "/" + XvfHost.HomeSubdirectory;

        SeedTool(files, processes, root, "VERSION 2 0 10", "GPO_READ_VALUES 0 0 0 1 0", executable: true);

        var observation = await Observe(Audio(files, processes, session: session), XvfHostToolResource.ResourceName);

        Assert.True(observation.InSync);
        Assert.Contains(XvfHost.ToolDirectory(root), observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_card_pin_wants_the_line_once_and_the_array_at_index_zero()
    {
        using var files = new TemporaryFiles();
        files.Seed(AlsaCards.CardsPath, ArrayIsCardZero);

        var resource = new SndUsbAudioIndexResource(files.Files);

        var missing = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(missing.InSync);

        await resource.ActAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            SndUsbAudioIndexResource.OptionsLine + "\n",
            files.Read(SndUsbAudioIndexResource.ConfigPath));
        Assert.True((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);
    }

    [Fact]
    public async Task A_duplicated_options_line_is_drift_and_the_act_collapses_it()
    {
        // A non-idempotent write history is a real fault with a real fix, and an append-only Act
        // could never repair one.
        using var files = new TemporaryFiles();
        files.Seed(AlsaCards.CardsPath, ArrayIsCardZero);
        files.Seed(
            SndUsbAudioIndexResource.ConfigPath,
            "blacklist 8192cu\n" + SndUsbAudioIndexResource.OptionsLine + "\n" + SndUsbAudioIndexResource.OptionsLine + "\n");

        var resource = new SndUsbAudioIndexResource(files.Files);

        Assert.False((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);

        await resource.ActAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            "blacklist 8192cu\n" + SndUsbAudioIndexResource.OptionsLine + "\n",
            files.Read(SndUsbAudioIndexResource.ConfigPath));
    }

    [Fact]
    public async Task The_card_pin_is_drift_when_an_hdmi_card_took_index_zero()
    {
        // The measured cold-boot failure: the pinned module cannot load at all, and a resource
        // that read only its own file would call that in sync.
        using var files = new TemporaryFiles();
        files.Seed(AlsaCards.CardsPath, HdmiTookCardZero);
        files.Seed(SndUsbAudioIndexResource.ConfigPath, SndUsbAudioIndexResource.OptionsLine + "\n");

        var observation = await new SndUsbAudioIndexResource(files.Files)
            .ObserveAsync(TestContext.Current.CancellationToken);

        Assert.False(observation.InSync);
        Assert.Contains("card 0 is 'vc4hdmi0'", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_hdmi_audio_resource_appends_noaudio_and_keeps_the_parameters_it_finds()
    {
        // Guide 4's sed is anchored to the exact stock line, so a config.txt whose vc4 line
        // carries any other parameter silently does not match. The catalog asks for the general
        // case, and the general case must not drop what is already there.
        using var files = new TemporaryFiles();
        files.Seed(AlsaCards.CardsPath, HdmiTookCardZero);
        files.Seed(BootConfigText.ConfigPath, "[all]\ndtparam=audio=on\ndtoverlay=vc4-kms-v3d,cma-256\n");

        var resource = Hdmi(files);

        Assert.False((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);

        var action = await resource.ActAsync(TestContext.Current.CancellationToken);

        Assert.Contains("dtoverlay=vc4-kms-v3d,cma-256,noaudio", files.Read(BootConfigText.ConfigPath)!, StringComparison.Ordinal);
        Assert.Contains("cma-256", action.Change, StringComparison.Ordinal);
        Assert.Contains("dtparam=audio=on", files.Read(BootConfigText.ConfigPath)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_hdmi_audio_resource_is_in_sync_once_the_cards_are_gone()
    {
        using var files = new TemporaryFiles();
        files.Seed(AlsaCards.CardsPath, ArrayIsCardZero);
        files.Seed(BootConfigText.ConfigPath, "dtoverlay=vc4-kms-v3d,noaudio\n");

        Assert.True((await Hdmi(files).ObserveAsync(TestContext.Current.CancellationToken)).InSync);
    }

    [Fact]
    public async Task The_hdmi_audio_resource_never_invents_a_display_overlay()
    {
        // Writing dtoverlay=vc4-kms-v3d where there is none would switch the KMS display driver
        // on — a display change made by an audio resource.
        using var files = new TemporaryFiles();
        files.Seed(AlsaCards.CardsPath, HdmiTookCardZero);
        files.Seed(BootConfigText.ConfigPath, "[all]\ndtparam=audio=on\n");

        var action = await Hdmi(files).ActAsync(TestContext.Current.CancellationToken);

        Assert.Equal("[all]\ndtparam=audio=on\n", files.Read(BootConfigText.ConfigPath));
        Assert.Contains("no vc4-kms-v3d line", action.Change, StringComparison.Ordinal);
    }

    [Fact]
    public void A_replacement_that_would_change_two_lines_is_refused_before_it_is_written()
    {
        const string Original = "dtoverlay=vc4-kms-v3d\ndtparam=audio=on\n";

        Assert.True(BootConfigText
            .ValidateReplacement(Original, "dtoverlay=vc4-kms-v3d,noaudio\ndtparam=audio=on\n", "dtoverlay=vc4-kms-v3d", "dtoverlay=vc4-kms-v3d,noaudio")
            .Valid);

        Assert.False(BootConfigText
            .ValidateReplacement(Original, "dtoverlay=vc4-kms-v3d,noaudio\ndtparam=audio=off\n", "dtoverlay=vc4-kms-v3d", "dtoverlay=vc4-kms-v3d,noaudio")
            .Valid);

        // A rewrite is the one shape where a careless edit drops a line rather than adding a
        // wrong one, so a shorter result is refused too.
        Assert.False(BootConfigText
            .ValidateReplacement(Original, "dtoverlay=vc4-kms-v3d,noaudio\n", "dtoverlay=vc4-kms-v3d", "dtoverlay=vc4-kms-v3d,noaudio")
            .Valid);
    }

    [Fact]
    public void An_overlay_name_is_matched_whole_and_never_by_prefix()
    {
        const string Content = "dtoverlay=vc4-kms-v3d-pi5\ndtoverlay=vc4-kms-v3d,noaudio\n";

        Assert.Equal("dtoverlay=vc4-kms-v3d,noaudio", BootConfigText.FindOverlayLine(Content, "vc4-kms-v3d"));
        Assert.True(BootConfigText.OverlayHasParameter("dtoverlay=vc4-kms-v3d,noaudio", "noaudio"));
        Assert.False(BootConfigText.OverlayHasParameter("dtoverlay=vc4-kms-v3d,cma-256", "noaudio"));
        Assert.True(BootConfigText.OverlayHasParameter("dtoverlay=x,noaudio=1", "noaudio"));
    }

    [Fact]
    public async Task A_machine_with_no_sound_hardware_reports_the_whole_block_in_sync()
    {
        // §5.3 exercises fleet behaviour with virtual agents — the same binary, linux-x64, in a
        // container. Reporting drift there would put them into a permanent repair loop over
        // hardware they do not have, which is the choice cpu.governor.performance already made.
        using var files = new TemporaryFiles();
        var block = Audio(files, new RecordingProcessRunner());

        foreach (var resource in block)
        {
            var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);
            Assert.True(observation.InSync, $"{resource.Name}: {observation.Delta}");
        }
    }

    [Fact]
    public void The_catalog_values_still_agree_with_the_frozen_v1_reference()
    {
        // §7.1: never asserted from memory. This reads reference/v1-state-inventory.txt — the
        // capture from the running v1 frame — and holds the catalog to it. The ALSA_MIXER section
        // is the single best reason that inventory exists.
        var inventory = File.ReadAllText(
            Path.Combine(GuiFreshnessTests.RepositoryRoot(), "reference", "v1-state-inventory.txt"));

        var mixer = Section(inventory, "ALSA_MIXER");

        foreach (var (control, channels) in new[]
        {
            ("'PCM',0", new[] { "Front Left", "Front Right" }),
            ("'PCM',1", ["Mono"]),
            ("'Headset',0", ["Front Left", "Front Right"]),
            ("'Headset',1", ["Mono"]),
        })
        {
            Assert.Contains("Simple mixer control " + control, mixer, StringComparison.Ordinal);
            Assert.Equal(channels.Length, channels.Length);
        }

        // Every control the block owns was captured at 60, which is 0.00 dB, against a limit of
        // 60 — so the catalog default and the ceiling are both the reference's own numbers.
        var reference = AlsaMixer.Parse("PCM,0", mixer);

        Assert.Equal(60, reference.Maximum);
        Assert.All(reference.Channels, channel => Assert.Equal(AlsaMixer.Ceiling, channel.Value));
        Assert.All(reference.Channels, channel => Assert.Equal("0.00dB", channel.Decibels));
        Assert.All(reference.Channels, channel => Assert.True(channel.Switch));
        Assert.Equal(6, reference.Channels.Count);
        Assert.Equal(AudioCatalog.DefaultLevel, AlsaMixer.Ceiling.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // The array's firmware, and the modprobe line, from the same capture.
        Assert.Equal(XvfFirmwareResource.PinnedReply, XvfHost.Version(Section(inventory, "XVF3800_FIRMWARE")));
        Assert.Contains(SndUsbAudioIndexResource.OptionsLine, Section(inventory, "MODPROBE_D"), StringComparison.Ordinal);
        Assert.Contains("0 [" + AlsaCards.ArrayId, Section(inventory, "ALSA_CARDS"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_audio_block_declares_the_dependencies_the_catalog_document_states()
    {
        using var files = new TemporaryFiles();
        var graph = DeviceCatalog.BuildGraph(AgentResourceGraphTests.Context(files));

        Assert.Equal(
            [XvfHostToolResource.ResourceName, "pkg.dfu-util"],
            graph.Find(XvfFirmwareResource.ResourceName)!.DependsOn);

        Assert.Equal(
            [XvfFirmwareResource.ResourceName],
            graph.Find(XvfAmplifierResource.ResourceName)!.DependsOn);

        // The two playback volumes each depend on the card pin, the firmware whose DAC path they
        // are validated against, and their own switch — a muted stage is reported as muted rather
        // than as a level that will not take effect.
        Assert.Equal(
            [
                SndUsbAudioIndexResource.ResourceName,
                XvfFirmwareResource.ResourceName,
                AudioCatalog.Pcm1SwitchResourceName,
            ],
            graph.Find(AudioCatalog.Pcm1VolumeResourceName)!.DependsOn);

        // Capture depends on the card pin alone, per the catalog.
        Assert.Equal(
            [SndUsbAudioIndexResource.ResourceName],
            graph.Find(AudioCatalog.HeadsetCaptureResourceName)!.DependsOn);

        // "dependsOn — every audio.mixer.* resource".
        var stored = graph.Find(AlsaStoredStateResource.ResourceName)!.DependsOn;

        Assert.Contains(AudioCatalog.Pcm0VolumeResourceName, stored);
        Assert.Contains(AudioCatalog.Pcm1VolumeResourceName, stored);
        Assert.Contains(AudioCatalog.HeadsetCaptureResourceName, stored);
        Assert.Contains(AudioCatalog.Pcm0SwitchResourceName, stored);
        Assert.Contains(AudioCatalog.Pcm1SwitchResourceName, stored);

        // No adoption edge anywhere in the block: every value has a catalog default that is
        // correct on an unadopted frame, so none of them has to guess.
        foreach (var name in new[]
        {
            AudioCatalog.Pcm0VolumeResourceName,
            AudioCatalog.Pcm1VolumeResourceName,
            AudioCatalog.HeadsetCaptureResourceName,
            XvfFirmwareResource.ResourceName,
            SndUsbAudioIndexResource.ResourceName,
            HdmiAudioOffResource.ResourceName,
        })
        {
            Assert.DoesNotContain(AdoptionResource.ResourceName, graph.Find(name)!.DependsOn);
        }
    }

    [Fact]
    public void The_mixer_is_read_after_the_session_that_could_change_it_has_started()
    {
        // Not a wait inside Observe — an ordering. WirePlumber belongs to the login session, so
        // every mixer resource is ordered behind the resource that brings that session up, and a
        // reading taken by the loop is a reading taken after the second owner has had its say.
        using var files = new TemporaryFiles();
        var graph = DeviceCatalog.BuildGraph(AgentResourceGraphTests.Context(files));
        var order = graph.Ordered.Select(resource => resource.Name).ToList();

        foreach (var name in new[]
        {
            AudioCatalog.Pcm0VolumeResourceName,
            AudioCatalog.Pcm1VolumeResourceName,
            AudioCatalog.HeadsetCaptureResourceName,
            AlsaStoredStateResource.ResourceName,
        })
        {
            Assert.True(
                order.IndexOf(ConsoleAutologinResource.ResourceName) < order.IndexOf(name),
                $"{name} must be ordered after the session it shares the mixer with");
            Assert.True(
                order.IndexOf(ChromiumKioskEnabledResource.ResourceName) < order.IndexOf(name),
                $"{name} must be ordered after the kiosk stack");
        }

        // And the stored state is last of the block: it captures what the live controls hold, so
        // it has to run once they are all right.
        Assert.True(order.IndexOf(AudioCatalog.Pcm1VolumeResourceName) < order.IndexOf(AlsaStoredStateResource.ResourceName));
    }

    private static HdmiAudioOffResource Hdmi(TemporaryFiles files) =>
        new(
            files.Files,
            new BootPartitionGuard(files.Files, files.Store, new MutableBootIdentity(), new ManualClock(), new RecordingLog()),
            new RecordingLog());

    /// <summary>A card 0 that answers <c>amixer</c> with the two playback stages given.</summary>
    private static RecordingProcessRunner Mixer(TemporaryFiles files, (string Pcm0, string Pcm1) stages)
    {
        files.Seed(AlsaCards.CardsPath, ArrayIsCardZero);

        var processes = new RecordingProcessRunner();
        processes.Answers["amixer -c 0 sget PCM,0"] = new ProcessResult(0, stages.Pcm0, string.Empty);
        processes.Answers["amixer -c 0 sget PCM,1"] = new ProcessResult(0, stages.Pcm1, string.Empty);
        processes.Answers["amixer -c 0 sget Headset,0"] = new ProcessResult(0, Headset0Correct, string.Empty);
        processes.Answers["amixer -c 0 sget Headset,1"] = new ProcessResult(0, Headset1Correct, string.Empty);

        return processes;
    }

    /// <summary>Puts the control tool where the agent would install it.</summary>
    private static void Tool(
        TemporaryFiles files,
        RecordingProcessRunner processes,
        string version,
        string gpo = "GPO_READ_VALUES 0 0 0 1 0",
        bool executable = true,
        bool withFirmwareImage = false)
    {
        SeedTool(files, processes, XvfHost.AgentDirectory, version, gpo, executable);

        if (withFirmwareImage)
        {
            files.Seed(XvfHost.FirmwarePath(XvfHost.AgentDirectory, XvfFirmwareResource.PinnedVersion), "not a real image");
        }
    }

    private static void SeedTool(
        TemporaryFiles files,
        RecordingProcessRunner processes,
        string root,
        string version,
        string gpo,
        bool executable)
    {
        var directory = XvfHost.ToolDirectory(root);

        files.Seed(directory + "/" + XvfHost.Binary, "#!/bin/false\n");
        files.Seed(directory + "/libdevice_usb.so", "elf");
        files.Seed(directory + "/libcommand.so", "elf");
        files.Seed(directory + "/libutils.so", "elf");

        if (executable)
        {
            files.Files.SetMode(
                directory + "/" + XvfHost.Binary,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        const string Banner = "Device (USB)::device_init() -- Found device VID: 10374 PID: 26 interface: 3\n";
        var prefix = $"env -C {directory} LD_LIBRARY_PATH={directory} {directory}/{XvfHost.Binary} ";

        processes.Answers[prefix + XvfHost.VersionCommand] = new ProcessResult(0, Banner + version, string.Empty);
        processes.Answers[prefix + XvfHost.GpoReadCommand] = new ProcessResult(0, Banner + gpo, string.Empty);
    }

    private static IReadOnlyList<IResource> Audio(
        TemporaryFiles files,
        IProcessRunner processes,
        FleetValues? values = null,
        FakeUserSession? session = null,
        ManualClock? clock = null)
    {
        var context = AgentResourceGraphTests.Context(files) with
        {
            Processes = processes,
            Session = session ?? new FakeUserSession(),
            Values = values ?? FleetValues.None,
            Clock = clock ?? new ManualClock(),
        };

        return AudioCatalog.Build(context);
    }

    private static IResource Find(IReadOnlyList<IResource> block, string name) =>
        block.Single(resource => string.Equals(resource.Name, name, StringComparison.Ordinal));

    private static async Task<ResourceObservation> Observe(IReadOnlyList<IResource> block, string name) =>
        await Find(block, name).ObserveAsync(TestContext.Current.CancellationToken);

    /// <summary>
    /// An <c>asound.state</c> holding the block's controls at the levels given.
    /// </summary>
    /// <remarks>
    /// Constructed, and it has to be: the v1 reference's capture of this file is truncated
    /// part-way through <c>control.4</c>, so the volume controls' stored spelling is not in the
    /// frozen reference. The shape — and <c>control.3</c> and <c>control.4</c> themselves — are
    /// the reference's own.
    /// </remarks>
    private static string StoredState(string pcm0, string pcm1) => $$"""
        state.Array {
        	control.3 {
        		iface MIXER
        		name 'PCM Playback Switch'
        		value.0 true
        		value.1 true
        	}
        	control.4 {
        		iface MIXER
        		name 'PCM Playback Switch'
        		index 1
        		value true
        	}
        	control.5 {
        		iface MIXER
        		name 'PCM Playback Volume'
        		value.0 {{pcm0}}
        		value.1 {{pcm0}}
        		comment {
        			access 'read write'
        			type INTEGER
        			count 2
        			range '0 - 60'
        		}
        	}
        	control.6 {
        		iface MIXER
        		name 'PCM Playback Volume'
        		index 1
        		value {{pcm1}}
        	}
        	control.7 {
        		iface MIXER
        		name 'Headset Capture Switch'
        		value.0 true
        		value.1 true
        	}
        	control.8 {
        		iface MIXER
        		name 'Headset Capture Switch'
        		index 1
        		value true
        	}
        	control.9 {
        		iface MIXER
        		name 'Headset Capture Volume'
        		value.0 60
        		value.1 60
        	}
        	control.10 {
        		iface MIXER
        		name 'Headset Capture Volume'
        		index 1
        		value 60
        	}
        }
        """;

    /// <summary>One <c>== NAME</c> section of the v1 state inventory.</summary>
    private static string Section(string inventory, string name)
    {
        var marker = "== " + name + "\n";
        var start = inventory.IndexOf(marker, StringComparison.Ordinal);

        Assert.True(start >= 0, $"The v1 reference has no {name} section.");

        var body = inventory[(start + marker.Length)..];
        var end = body.IndexOf("\n====", StringComparison.Ordinal);

        return end < 0 ? body : body[..end];
    }
}
