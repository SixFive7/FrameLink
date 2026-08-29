using FrameLink.Agent.Firmware;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;
using FrameLink.Agent.Telemetry;
using FrameLink.Protocol;

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
    public async Task Every_mixer_observation_records_what_wireplumber_is_doing_and_names_its_files()
    {
        // The suspicion is confirmed — measured on the frame 2026-08-16 — but *which* WirePlumber
        // mechanism is still open, and the file names are what separates "it restored a stored
        // volume" from "it applied its own default to a route it has no stored volume for". The two
        // have different fixes, nobody has been able to list that directory by hand, so the agent
        // lists it on every reading and the answer arrives in ordinary telemetry.
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        var session = new FakeUserSession();
        session.Answers["systemctl --user is-active wireplumber.service"] = new ProcessResult(0, "active", string.Empty);
        files.Seed("/home/framelink/.local/state/wireplumber/restore-stream", "{}");

        var block = Audio(files, processes, session: session);
        var observation = await Observe(block, AudioCatalog.Pcm1VolumeResourceName);

        Assert.Contains("wireplumber active", observation.Observed, StringComparison.Ordinal);

        // Singular, and named — "1 stored device files" was what the frame reported and it says
        // nothing about which file it is.
        Assert.Contains("1 stored device file (restore-stream)", observation.Observed, StringComparison.Ordinal);

        files.Seed("/home/framelink/.local/state/wireplumber/default-profile", "{}");

        var both = await Observe(Audio(files, processes, session: new FakeUserSession
        {
            Answers = { ["systemctl --user is-active wireplumber.service"] = new ProcessResult(0, "active", string.Empty) },
        }), AudioCatalog.Pcm1VolumeResourceName);

        Assert.Contains(
            "2 stored device files (default-profile, restore-stream)",
            both.Observed,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wireplumber_that_is_not_running_is_reported_but_does_not_itself_make_the_mixer_drift()
    {
        // Two different facts, and only one of them gates. *Is there a session to ask* decides
        // whether the mixer can be answered for at all (decision 80, above). *Is WirePlumber
        // running inside it* is evidence about the second owner and rides in the observed text —
        // a frame whose session is up and whose WirePlumber is stopped has a mixer nobody is
        // fighting over, and its value is exactly as true as any other.
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
    public void The_bcd_device_descriptor_decodes_to_the_version_the_control_tool_reports()
    {
        // Measured on two arrays 2026-08-20, which is the whole of the evidence for this decode:
        // a factory board answering VERSION 2 0 6 reads 0206, and an upgraded one answering
        // VERSION 2 0 10 reads 020a. `0a` is not a valid BCD digit pair, so the field is hex per
        // nibble rather than binary-coded decimal, and the spelling produced here is xvf_host's own
        // so the two readings compare with ordinal equality and nothing else.
        Assert.Equal("2 0 6", XvfArrayUsb.Version("0206"));
        Assert.Equal("2 0 10", XvfArrayUsb.Version("020a"));
        Assert.Equal("2 0 10", XvfArrayUsb.Version("020A"));

        // The consequence of one nibble each: 2.1.0 is predicted to read 0210 and has never been
        // seen on hardware, and a minor or patch of 16 or more cannot be represented at all.
        Assert.Equal("2 1 0", XvfArrayUsb.Version("0210"));

        Assert.Null(XvfArrayUsb.Version(null));
        Assert.Null(XvfArrayUsb.Version(string.Empty));
        Assert.Null(XvfArrayUsb.Version("20a"));
        Assert.Null(XvfArrayUsb.Version("02zz"));
    }

    [Fact]
    public void The_array_is_found_by_its_vendor_and_product_ids_and_nothing_else()
    {
        using var files = new TemporaryFiles();

        // A root hub and a keyboard on the same bus, with the array between them.
        Usb(files, "1-0", "1d6b", "0003", "0615", "0000:00:14.0");
        Usb(files, "1-1", XvfArrayUsb.VendorId, XvfArrayUsb.ProductId, "0206", "101991441260500030");
        Usb(files, "1-2", "046d", "c52b", "1203", string.Empty);

        var array = Assert.Single(XvfArrayUsb.Attached(files.Files));

        Assert.Equal("1-1", array.Path);
        Assert.Equal("0206", array.BcdDevice);
        Assert.Equal("101991441260500030", array.Serial);
    }

    [Fact]
    public async Task The_reporter_names_both_readings_and_says_that_they_agree()
    {
        using var files = new TemporaryFiles();
        var processes = new RecordingProcessRunner();
        Tool(files, processes, "VERSION 2 0 6");
        Usb(files, "1-1", XvfArrayUsb.VendorId, XvfArrayUsb.ProductId, "0206", "101991441260500030");

        var reading = await Reporter(files, processes).ReadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(reading);
        Assert.Contains("bcdDevice 0206 = firmware 2 0 6", reading, StringComparison.Ordinal);
        Assert.Contains("101991441260500030", reading, StringComparison.Ordinal);
        Assert.Contains("VERSION answers 2 0 6, agreeing with the USB descriptor", reading, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_descriptor_answers_on_a_frame_that_has_no_control_tool()
    {
        // The point of the second reading: it needs no xvf_host, no root and no control transfer,
        // so a frame whose tool is missing still says which firmware its array is running.
        using var files = new TemporaryFiles();
        var processes = new RecordingProcessRunner();
        Usb(files, "1-1", XvfArrayUsb.VendorId, XvfArrayUsb.ProductId, "020a", "101991441260500069");

        var reading = await Reporter(files, processes).ReadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(reading);
        Assert.Contains("bcdDevice 020a = firmware 2 0 10", reading, StringComparison.Ordinal);
        Assert.Contains("is not installed", reading, StringComparison.Ordinal);
        Assert.Empty(processes.Commands);
    }

    [Fact]
    public async Task Two_readings_that_disagree_are_reported_as_disagreeing_rather_than_reconciled()
    {
        using var files = new TemporaryFiles();
        var processes = new RecordingProcessRunner();
        Tool(files, processes, "VERSION 2 0 10");
        Usb(files, "1-1", XvfArrayUsb.VendorId, XvfArrayUsb.ProductId, "0206", "101991441260500030");

        var reading = await Reporter(files, processes).ReadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(reading);
        Assert.Contains("which disagrees with the USB descriptor", reading, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_frame_with_no_array_says_so_and_a_machine_with_no_usb_says_nothing()
    {
        using var files = new TemporaryFiles();
        var processes = new RecordingProcessRunner();

        // No /sys/bus/usb/devices at all — a workstation or a container. Nothing to say.
        Assert.Null(await Reporter(files, processes).ReadAsync(TestContext.Current.CancellationToken));

        // The directory exists and holds no array. That is a real observation, and it is the one an
        // operator staring at a frame with no microphone unit needs.
        Usb(files, "1-0", "1d6b", "0003", "0615", "0000:00:14.0");

        var reading = await Reporter(files, processes).ReadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(reading);
        Assert.Contains("No microphone unit is attached", reading, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_reporter_publishes_once_and_stays_quiet_until_the_array_changes()
    {
        using var files = new TemporaryFiles();
        var processes = new RecordingProcessRunner();
        Tool(files, processes, "VERSION 2 0 6");
        Usb(files, "1-1", XvfArrayUsb.VendorId, XvfArrayUsb.ProductId, "0206", "101991441260500030");

        var telemetry = new NullReconcileTelemetry();
        var reporter = Reporter(files, processes, telemetry);

        Assert.True(await reporter.TickAsync(TestContext.Current.CancellationToken));
        Assert.False(await reporter.TickAsync(TestContext.Current.CancellationToken));

        var published = Assert.Single(telemetry.Events);
        Assert.Equal(DeviceEventKinds.ArrayFirmware, published.Kind);
        Assert.Null(published.Resource);
        Assert.Contains("2 0 6", published.Summary, StringComparison.Ordinal);

        // Swap the board for the other one and the frame says so on the next tick.
        Tool(files, processes, "VERSION 2 0 10");
        Usb(files, "1-1", XvfArrayUsb.VendorId, XvfArrayUsb.ProductId, "020a", "101991441260500069");

        Assert.True(await reporter.TickAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, telemetry.Events.Count);
        Assert.Contains("2 0 10", telemetry.Events[1].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_reporter_can_only_ever_read_the_array()
    {
        // The guarantee that replaces the old two-key flash interlock, and it is stronger than the
        // interlock was: there is no authorisation to withhold because there is no code path that
        // writes. VERSION is the only command this component can send.
        using var files = new TemporaryFiles();
        var processes = new RecordingProcessRunner();
        Tool(files, processes, "VERSION 2 0 6");
        Usb(files, "1-1", XvfArrayUsb.VendorId, XvfArrayUsb.ProductId, "0206", "101991441260500030");

        await Reporter(files, processes).TickAsync(TestContext.Current.CancellationToken);

        Assert.All(
            processes.Commands,
            command => Assert.EndsWith(" " + XvfHost.VersionCommand, command, StringComparison.Ordinal));
    }

    [Fact]
    public void Only_one_file_in_the_agent_can_start_a_dfu_flash()
    {
        // Decision 91's structural half, and it is the same shape decision 90's test had with the
        // conclusion reversed. That test asserted the agent could not flash at all; this one asserts
        // that everything which *can* lives in one file, so "is the dangerous path guarded" is a
        // question about one place rather than about the whole tree. The two permitted mentions are
        // the apt package that puts the program on the frame and the interlocked flash that runs it.
        var agent = Path.Combine(GuiFreshnessTests.RepositoryRoot(), "src", "FrameLink.Agent");
        var mentioning = new List<string>();

        foreach (var file in Directory.EnumerateFiles(agent, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);

            // The argument vector that writes the Upgrade partition. Exactly one file may build it,
            // and it is the one the interlocks live in.
            if (text.Contains("\"-a\", \"1\", \"-D\"", StringComparison.Ordinal))
            {
                Assert.Equal("ArrayFirmwareFlash.cs", name);
            }

            foreach (var line in text.Split('\n'))
            {
                if (line.Contains("dfu-util", StringComparison.Ordinal)
                    && !line.TrimStart().StartsWith("///", StringComparison.Ordinal)
                    && !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    mentioning.Add(name);
                }
            }
        }

        Assert.Equal(
            ["ArrayFirmwareFlash.cs", "PackageResources.cs"],
            mentioning.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_flash_writes_the_upgrade_partition_and_never_the_factory_one()
    {
        // `-a 0` is the Factory partition, which is where Safe Mode's own firmware lives and is the
        // only reason an interrupted write is recoverable at all. `-a 2` is the DataPartition, which
        // is what upstream issue #8's corruption lives in and what a reflash cannot clear. Neither
        // may ever appear: this build writes alt 1 and nothing else, and that is checked against the
        // vector rather than against a comment.
        var vector = ArrayFirmwareFlash.Arguments("/var/lib/fl-agent/xvf3800/xmos_firmwares/usb/image.bin");

        Assert.Equal(
            ["-R", "-e", "-a", "1", "-D", "/var/lib/fl-agent/xvf3800/xmos_firmwares/usb/image.bin"],
            vector);
    }

    [Fact]
    public async Task A_frame_on_the_shipping_firmware_reaches_sync_instead_of_stopping_the_pass()
    {
        // The reason this change exists. A 2.0.6 array used to drift `firmware.xvf3800.version`,
        // spend three attempts and three reboots, escalate, and stop the whole pass by decision 68
        // — leaving the screen, the camera and the speaker Blocked behind a version number nobody
        // was ever going to let the frame write. Decision 91 lets the agent write firmware again and
        // this property is unchanged, because what came back into the graph is the *images* and not
        // the version: a frame on 2.0.6 with no authorisation still converges everything else.
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        Tool(files, processes, "VERSION 2 0 6");

        var block = Audio(files, processes);

        Assert.DoesNotContain(block, resource => resource.Name == "firmware.xvf3800.version");
        Assert.True((await Observe(block, XvfAmplifierResource.ResourceName)).InSync);

        // <b>The property, asserted directly rather than by excluding a name.</b> This used to say
        // "no `firmware.*` resource other than the images", which stood in for the real rule while
        // the images were the only firmware resource there was. The rule is that a frame on the
        // shipping firmware converges — and now that the recognition gate is in the graph, saying
        // so directly is both stronger and the only honest form: 2.0.6 is a version this build has
        // been told about, so a 2.0.6 frame is recognised, in sync, and stops nothing. A gate that
        // refused the version both of this project's arrays shipped with would be caught here.
        Assert.Contains(block, resource => resource.Name == ArrayRecognitionResource.ResourceName);
        Assert.True((await Observe(block, ArrayRecognitionResource.ResourceName)).InSync);

        // <b>The property in its strongest form: this frame is done with firmware.</b> A 2.0.6 array
        // and no authorisation, and every rung of the chain reads in sync — so nothing after it in
        // the catalog is Blocked, nothing escalates, and decision 68 stops nothing. Six rungs now
        // rather than two, and the count is deliberately not asserted here: what matters is that
        // whatever the chain grows to, none of it drifts on a frame nobody has asked to do anything.
        var firmware = block
            .Where(resource => resource.Name.StartsWith("firmware.", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(firmware);

        // `firmware.xvf3800.image` is deliberately outside this. It drifts here because the fixture
        // never installed the pinned files, and it is the one firmware resource with an ordinary Act
        // that repairs itself — it converges, spends nothing and stops nothing. What must not drift
        // is everything else, because every one of those either stops the pass or writes to hardware.
        foreach (var resource in firmware.Where(
            resource => resource.Name != XvfFirmwareImageResource.ResourceName))
        {
            var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);

            Assert.True(
                observation.InSync,
                resource.Name + " drifted on a frame running the shipping firmware with nothing authorised: "
                    + observation.Delta);
        }

        // And no resource in the block converges a firmware *version*, which is the thing decision 90
        // removed and neither decision 91 nor the move of the flash into the graph put back. Three
        // shapes are permitted and the third is the new one: a gate, which has no Act at all; the
        // images resource, which is about files on the card; and `firmware.xvf3800.written`, whose
        // Act is the interlocked operation and whose claim is that no *instruction* is outstanding —
        // never that this array runs a particular firmware.
        Assert.All(
            firmware,
            resource => Assert.True(
                resource.IsGate
                    || resource.Name == XvfFirmwareImageResource.ResourceName
                    || resource.Name == ArrayFlashWriteResource.ResourceName,
                resource.Name + " converges something firmware-shaped that is neither a gate, the pinned images, "
                    + "nor the authorised write."));
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
        // Six correct files prove the tool is installed. Only the round trip proves the array is
        // plugged in, enumerated and reachable over its HID control interface, which is the half a
        // digest can never answer.
        using var fixture = new XvfHostFixture();
        fixture.SeedPinnedFiles(XvfHost.AgentDirectory);

        Assert.True((await fixture.Observe()).InSync);

        var directory = XvfHost.ToolDirectory(XvfHost.AgentDirectory);
        fixture.Processes.Answers[
            $"env -C {directory} LD_LIBRARY_PATH={directory} {directory}/xvf_host VERSION"] =
            new ProcessResult(1, string.Empty, "Device (USB)::device_init() -- No device found");

        var silent = await fixture.Observe();

        Assert.False(silent.InSync);
        Assert.Contains("the files match the pin, but the array did not answer", silent.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_frame_with_no_control_tool_fetches_the_pinned_files_and_verifies_every_one()
    {
        // Open question 3's answer (decision 63). The refusal this replaces was correct while no
        // pin existed; what makes the fetch legitimate now is that every byte is checked against a
        // digest measured from upstream before anything is put in place.
        using var fixture = new XvfHostFixture();
        fixture.ServeEverything();

        var before = await fixture.Observe();
        Assert.False(before.InSync);
        Assert.Contains(XvfHost.AgentDirectory, before.Observed, StringComparison.Ordinal);

        var action = await fixture.Act();

        Assert.Contains("fetch 6 files", action.Change, StringComparison.Ordinal);
        Assert.DoesNotContain("refused", action.Change, StringComparison.Ordinal);

        // Content-addressed: every URL carries the pinned commit, so the bytes behind it cannot be
        // changed without the pin changing.
        Assert.Equal(6, fixture.Download.Opened.Count);
        Assert.All(
            fixture.Download.Opened,
            url => Assert.Contains(fixture.Pin.Commit, url.ToString(), StringComparison.Ordinal));

        Assert.True((await fixture.Observe()).InSync);
    }

    [Fact]
    public async Task A_download_that_does_not_match_the_pin_is_refused_and_nothing_is_installed()
    {
        // The loud refusal §0.4 and §2.5 both want. A half-filled directory that looks installed is
        // the one outcome worse than an empty one.
        using var fixture = new XvfHostFixture();
        fixture.ServeEverything();
        fixture.Corrupt(XvfHost.Binary);

        var action = await fixture.Act();

        Assert.Contains($"refused: {XvfHostInstallResult.ChecksumMismatch}", action.Change, StringComparison.Ordinal);
        Assert.False(fixture.Exists(XvfHost.AgentDirectory, XvfHost.Binary));
        Assert.False(fixture.Exists(XvfHost.AgentDirectory, XvfHost.Binary + XvfHostInstaller.StagingSuffix));
        Assert.False((await fixture.Observe()).InSync);
    }

    [Fact]
    public async Task A_server_that_keeps_sending_is_cut_off_at_the_pinned_length()
    {
        // /var/lib/fl-agent is on the card the frame boots from, so a download bounded only by the
        // other end's goodwill is a card-filling bug waiting for a bad day.
        using var fixture = new XvfHostFixture();
        fixture.ServeEverything();
        fixture.Oversize(XvfHost.Binary);

        var action = await fixture.Act();

        Assert.Contains($"refused: {XvfHostInstallResult.SizeMismatch}", action.Change, StringComparison.Ordinal);
        Assert.False(fixture.Exists(XvfHost.AgentDirectory, XvfHost.Binary + XvfHostInstaller.StagingSuffix));
    }

    [Fact]
    public async Task An_upstream_that_cannot_be_reached_is_refused_rather_than_left_looking_installed()
    {
        using var fixture = new XvfHostFixture();

        var action = await fixture.Act();

        Assert.Contains($"refused: {XvfHostInstallResult.Unreachable}", action.Change, StringComparison.Ordinal);
        Assert.False((await fixture.Observe()).InSync);
    }

    [Fact]
    public async Task A_tool_that_is_present_but_not_executable_is_repaired_without_a_download()
    {
        // Guide 4 step 2's `chmod +x`, kept as the one repair that must never cost 2.1 MB. A
        // byte-perfect binary nothing can run produces exactly the silence a missing one does, so
        // it is named as its own fault rather than folded into the digest comparison.
        using var fixture = new XvfHostFixture();
        fixture.SeedPinnedFiles(XvfHost.AgentDirectory, executable: false);
        fixture.ServeEverything();

        var observation = await fixture.Observe();

        Assert.False(observation.InSync);
        Assert.Contains($"{XvfHost.Binary} is not executable", observation.Observed, StringComparison.Ordinal);

        await fixture.Act();

        Assert.Empty(fixture.Download.Opened);
        Assert.True((await fixture.Observe()).InSync);
    }

    [Fact]
    public async Task A_missing_sidecar_file_is_drift_even_though_the_binary_itself_is_right()
    {
        // The catalog used to describe four files. Seeed's own host_control/README.md lists
        // dfu_cmds.yaml and transport_config.yaml in the same directory, and a resource asserting
        // completeness over a directory it had only half looked at is the fault this catches.
        using var fixture = new XvfHostFixture();
        fixture.SeedPinnedFiles(XvfHost.AgentDirectory, except: "transport_config.yaml");

        var observation = await fixture.Observe();

        Assert.False(observation.InSync);
        Assert.Contains("transport_config.yaml is missing", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_pinned_state_survives_a_restart_because_it_is_re_read_rather_than_remembered()
    {
        // §2.4: "applied" is never claimed from a successful write. A note that an install
        // succeeded would survive a boot that the files did not, so a second resource over the same
        // filesystem — which is what a process restart or a reboot produces — has to reach the same
        // verdict by looking, and has to reach a different one when a file goes missing.
        using var fixture = new XvfHostFixture();
        fixture.ServeEverything();
        await fixture.Act();

        Assert.True((await fixture.Observe(fresh: true)).InSync);

        fixture.Remove(XvfHost.AgentDirectory, "libdevice_usb.so");

        var afterLoss = await fixture.Observe(fresh: true);

        Assert.False(afterLoss.InSync);
        Assert.Contains("libdevice_usb.so is missing", afterLoss.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_tool_is_also_found_where_guide_four_puts_it()
    {
        // A frame built by hand has the tree under ~/xvf3800, which is what the v1 reference
        // records. If those are the pinned bytes the frame is in sync where it stands and downloads
        // nothing; the observed text says which directory answered.
        using var fixture = new XvfHostFixture();
        var home = fixture.HomeRoot;

        fixture.SeedPinnedFiles(home);
        fixture.ServeEverything();

        var observation = await fixture.Observe();

        Assert.True(observation.InSync);
        Assert.Contains(XvfHost.ToolDirectory(home), observation.Observed, StringComparison.Ordinal);
        Assert.Empty(fixture.Download.Opened);
    }

    [Fact]
    public async Task A_hand_built_clone_that_is_not_the_pinned_bytes_is_replaced_by_a_verified_install()
    {
        // Guide 4's clone was never pinned, so a frame can hold any revision of it. The repair is
        // not to edit somebody's home directory: it is a verified install into the agent-owned
        // tree, which XvfHost.Root() prefers, so the next pass observes the copy this build chose.
        using var fixture = new XvfHostFixture();
        var home = fixture.HomeRoot;

        fixture.SeedPinnedFiles(home);
        fixture.Damage(home, "libcommand_map.so");
        fixture.ServeEverything();

        var before = await fixture.Observe();

        Assert.False(before.InSync);
        Assert.Contains(XvfHost.ToolDirectory(home), before.Observed, StringComparison.Ordinal);
        Assert.Contains("libcommand_map.so is a different file", before.Observed, StringComparison.Ordinal);

        await fixture.Act();
        var after = await fixture.Observe();

        Assert.True(after.InSync);
        Assert.Contains(XvfHost.ToolDirectory(XvfHost.AgentDirectory), after.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipping_pin_names_the_six_files_seeed_publish_at_one_immutable_commit()
    {
        // §7.1: a version claim is a reviewable fact, not memory. Every value below was measured on
        // 2026-08-16 by fetching the six URLs; what this asserts is the shape those measurements
        // have to keep — a full commit SHA, content-addressed URLs built from it, and one
        // executable among six files.
        var pin = XvfHostReleasePin.Current;

        Assert.Equal(6, pin.Files.Count);
        Assert.Equal(
            ["dfu_cmds.yaml", "libcommand_map.so", "libdevice_i2c.so", "libdevice_usb.so", "transport_config.yaml", "xvf_host"],
            pin.Files.Select(file => file.Name).Order(StringComparer.Ordinal));

        Assert.Equal(40, pin.Commit.Length);
        Assert.All(pin.Commit, character => Assert.True(char.IsAsciiHexDigitLower(character)));

        // xvf_i2c_dfu is the seventh file in that directory and is deliberately not fetched: this
        // build does USB DFU through dfu-util and never the I2C path.
        Assert.DoesNotContain(pin.Files, file => file.Name.Contains("i2c_dfu", StringComparison.Ordinal));

        Assert.Equal(XvfHost.Binary, Assert.Single(pin.Files, file => file.Executable).Name);

        Assert.All(pin.Files, file => Assert.Equal(64, file.Sha256.Length));
        Assert.All(pin.Files, file => Assert.True(file.SizeBytes > 0));
        Assert.All(
            pin.Files,
            file => Assert.StartsWith(
                "https://raw.githubusercontent.com/respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY/" + pin.Commit + "/",
                pin.UrlOf(file).ToString(),
                StringComparison.Ordinal));
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

        // The array's firmware, and the modprobe line, from the same capture. The version is read
        // rather than compared against a pin: decision 90 removed the pin, so what the v1 frame
        // happened to be running is now parity evidence and not a desired value.
        Assert.Equal("2 0 10", XvfHost.Version(Section(inventory, "XVF3800_FIRMWARE")));
        Assert.Contains(SndUsbAudioIndexResource.OptionsLine, Section(inventory, "MODPROBE_D"), StringComparison.Ordinal);
        Assert.Contains("0 [" + AlsaCards.ArrayId, Section(inventory, "ALSA_CARDS"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_audio_block_declares_the_dependencies_the_catalog_document_states()
    {
        using var files = new TemporaryFiles();
        var graph = DeviceCatalog.BuildGraph(AgentResourceGraphTests.Context(files));

        // Decision 90: the firmware resource is gone, and with it every edge that ran through it.
        Assert.Null(graph.Find("firmware.xvf3800.version"));

        Assert.Equal(
            [XvfHostToolResource.ResourceName],
            graph.Find(XvfAmplifierResource.ResourceName)!.DependsOn);

        // The two playback volumes each depend on the card pin and their own switch — a muted stage
        // is reported as muted rather than as a level that will not take effect. The firmware edge
        // they used to carry claimed the DAC path differs between 2.0.6 and 2.0.10, which nothing
        // in this repository ever measured, and which cost a frame its whole pass when the claim
        // could not be satisfied.
        Assert.Equal(
            [
                SndUsbAudioIndexResource.ResourceName,
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
            XvfHostToolResource.ResourceName,
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
    [Fact]
    public async Task The_mixer_cannot_be_answered_for_before_the_session_that_also_owns_it_exists()
    {
        // <b>The livelock's root, at resource scope.</b> The frame's post-boot verify runs at
        // boot+10.0-10.6 s and the login user's manager comes up 0.03-0.7 s later (decision 65), so
        // an ungated mixer verify was a coin flip on whether it read the agent's value or
        // WirePlumber's — and a verify that won *passed*, cleared the ledger, and left a frame that
        // was about to be wrong again looking entirely healthy. Section 2.4's rule is that
        // "applied" is claimed only from an observation the setting had to survive a boot for, and
        // a reading taken before the other owner of that value has started is not that observation.
        //
        // Before decision 80 every assertion below was the opposite: in sync, on a frame whose
        // speaker was about to be turned down.
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        var session = new FakeUserSession
        {
            Readiness = new SessionReadiness(
                false,
                "the login session has not started yet (/run/user/1000 does not exist)"),
        };

        var block = Audio(files, processes, session: session);

        foreach (var name in new[]
                 {
                     AudioCatalog.Pcm0VolumeResourceName,
                     AudioCatalog.Pcm1VolumeResourceName,
                     AudioCatalog.HeadsetCaptureResourceName,
                     AudioCatalog.Pcm0SwitchResourceName,
                     AudioCatalog.Pcm1SwitchResourceName,
                     WirePlumberVolumeResource.ResourceName,
                 })
        {
            var observation = await Observe(block, name);

            Assert.Equal(ObservationOutcome.Unevaluable, observation.Outcome);
            Assert.Contains("has not started yet", observation.Observed, StringComparison.Ordinal);

            // And it still says what it was hoping for, because "could not be determined" on its
            // own tells an operator nothing.
            Assert.NotEmpty(observation.Expected);
        }
    }

    [Fact]
    public async Task The_same_frame_reports_the_real_value_the_moment_the_session_is_up()
    {
        // The other half of the gate, and what stops it being a place a real fault goes to be
        // quiet: once there is a session to ask, the mixer reports exactly what it finds — which on
        // the frame 2026-08-16 was 37, twenty-three decibels down, with wireplumber running.
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        var session = new FakeUserSession
        {
            Readiness = new SessionReadiness(false, "the login session has not started yet"),
        };

        session.Answers["systemctl --user is-active wireplumber.service"] =
            new ProcessResult(0, "active", string.Empty);

        var block = Audio(files, processes, session: session);

        Assert.Equal(ObservationOutcome.Unevaluable, (await Observe(block, AudioCatalog.Pcm0VolumeResourceName)).Outcome);

        // The session starts, and WirePlumber applies its own idea of how loud the frame is.
        session.Readiness = SessionReadiness.Up;
        processes.Answers["amixer -c 0 sget PCM,0"] = new ProcessResult(
            0,
            Pcm0Correct.Replace(
                "Playback 60 [100%] [0.00dB]",
                "Playback 37 [61%] [-23.00dB]",
                StringComparison.Ordinal),
            string.Empty);

        var after = await Observe(block, AudioCatalog.Pcm0VolumeResourceName);

        Assert.Equal(ObservationOutcome.Drifted, after.Outcome);
        Assert.Contains("Front Left=37 -23.00dB", after.Observed, StringComparison.Ordinal);
        Assert.Contains("wireplumber active", after.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void The_measured_step_scale_is_one_decibel_and_it_is_what_makes_37_a_number_rather_than_noise()
    {
        // Three independent readings agree on one step per decibel: 60 = 0.00 dB in the v1
        // inventory, 40 = the -20 dB PCM,1 ships at, and 37 = -23.00 dB measured on the frame.
        Assert.Equal(1.00, WirePlumberVolumeResource.VolumeForStep(AlsaMixer.Ceiling), 3);
        Assert.Equal(-20.0, Decibels(WirePlumberVolumeResource.VolumeForStep(40)), 3);
        Assert.Equal(-23.0, Decibels(WirePlumberVolumeResource.VolumeForStep(37)), 3);

        // And here is the arithmetic the catalog records as a *hypothesis* and not as a
        // measurement: WirePlumber 0.5's device.routes.default-sink-volume default is 0.064 linear,
        // which is -23.88 dB, and the nearest step at or above that request is exactly the 37 the
        // frame reported. It is kept executable so a future reader can re-derive it rather than
        // take it on trust — and so that it is obvious this is a calculation on a documented
        // constant, with no frame in it anywhere.
        const double WirePlumberDefaultSinkVolume = 0.064;
        var requested = Decibels(WirePlumberDefaultSinkVolume);

        Assert.InRange(requested, -23.9, -23.8);
        Assert.Equal(37, (int)Math.Ceiling(requested) + AlsaMixer.Ceiling);
    }

    [Fact]
    public void Two_owners_of_one_value_agree_to_within_the_quantisation_they_share()
    {
        // An exact compare would report permanent false drift the moment either side rounded: the
        // hardware control moves in whole decibels and WirePlumber's volume is a continuous
        // fraction. Half a step is the tightest tolerance that cannot.
        Assert.True(WirePlumberVolumeResource.Agree(1.00, 1.00));
        Assert.True(WirePlumberVolumeResource.Agree(0.95, 1.00));
        Assert.False(WirePlumberVolumeResource.Agree(0.89, 1.00));

        // The measured gap is nowhere near it, which is the point.
        Assert.False(WirePlumberVolumeResource.Agree(WirePlumberVolumeResource.VolumeForStep(37), 1.00));

        // Silence and very quiet are different states, and 0 is not a decibel value.
        Assert.True(WirePlumberVolumeResource.Agree(0d, 0d));
        Assert.False(WirePlumberVolumeResource.Agree(0d, 1.00));
    }

    [Fact]
    public async Task WirePlumbers_own_volume_is_owned_repaired_and_unmuted()
    {
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        var session = new FakeUserSession();
        session.Answers["wpctl get-volume @DEFAULT_AUDIO_SINK@"] =
            new ProcessResult(0, "Volume: 0.06\n", string.Empty);

        var block = Audio(files, processes, session: session);
        var observation = await Observe(block, WirePlumberVolumeResource.ResourceName);

        Assert.False(observation.InSync);
        Assert.Contains("the sink is at 0.06", observation.Observed, StringComparison.Ordinal);
        Assert.Contains("1.00", observation.Expected, StringComparison.Ordinal);

        var action = await Find(block, WirePlumberVolumeResource.ResourceName)
            .ActAsync(TestContext.Current.CancellationToken);

        Assert.Contains("wpctl set-volume @DEFAULT_AUDIO_SINK@ 1.00", action.Change, StringComparison.Ordinal);
        Assert.Contains("wpctl set-mute @DEFAULT_AUDIO_SINK@ 0", action.Change, StringComparison.Ordinal);

        // Through the session, not as root: wpctl needs the user's bus, and a root wpctl answers
        // about a PipeWire that does not exist.
        Assert.Contains("wpctl set-volume @DEFAULT_AUDIO_SINK@ 1.00", session.Commands, StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_sink_at_the_right_level_and_muted_is_still_a_silent_frame()
    {
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        var session = new FakeUserSession();
        session.Answers["wpctl get-volume @DEFAULT_AUDIO_SINK@"] =
            new ProcessResult(0, "Volume: 1.00 [MUTED]\n", string.Empty);

        var block = Audio(files, processes, session: session);
        var observation = await Observe(block, WirePlumberVolumeResource.ResourceName);

        Assert.False(observation.InSync);
        Assert.Contains("MUTED", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wpctl_that_cannot_be_asked_is_drift_rather_than_silence()
    {
        // CameraNodeResource's rule, which this resource is exactly the kind of place to protect: a
        // local read that failed has learned something real about this machine, so it escalates on
        // the ordinary schedule instead of hiding behind "not settled yet".
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        var session = new FakeUserSession();
        session.Answers["wpctl get-volume @DEFAULT_AUDIO_SINK@"] =
            new ProcessResult(1, string.Empty, "Object 'Audio/Sink' not found\n");

        var block = Audio(files, processes, session: session);
        var observation = await Observe(block, WirePlumberVolumeResource.ResourceName);

        Assert.Equal(ObservationOutcome.Drifted, observation.Outcome);
        Assert.Contains("Object 'Audio/Sink' not found", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wireplumber_with_no_default_sink_yet_is_not_a_frame_that_is_too_quiet()
    {
        // The measured cascade's other half, and the resource that reached attempts=3 on both
        // cascade nights — one rung from giving up. `wpctl get-volume @DEFAULT_AUDIO_SINK@` answers
        // an empty string while the token translates to -1, and the empty answer read as drift, so
        // it was acted on, and §2.4 makes acting reboot, and the next boot asks just as early.
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        var session = new FakeUserSession();

        session.Answers["wpctl status"] = new ProcessResult(0, WpctlCaptures.Unsettled, string.Empty);
        session.Answers["wpctl get-volume @DEFAULT_AUDIO_SINK@"] = new ProcessResult(0, string.Empty, string.Empty);

        var block = Audio(files, processes, session: session);
        var observation = await Observe(block, WirePlumberVolumeResource.ResourceName);

        Assert.Equal(ObservationOutcome.Unevaluable, observation.Outcome);
        Assert.Contains("has not published a media graph yet", observation.Observed, StringComparison.Ordinal);
        Assert.Contains("could not be determined", observation.Delta, StringComparison.Ordinal);

        // The gate is ahead of the read, so the question is not asked at all — which is what stops
        // the Act that follows it being refused with "Translate ID error: '-1' is not a valid ID".
        Assert.DoesNotContain("wpctl get-volume @DEFAULT_AUDIO_SINK@", session.Commands, StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_built_graph_that_still_answers_nothing_is_drift_and_not_silence()
    {
        // The guard on the fix. Once WirePlumber has a device and a default sink, an empty answer
        // has learned something real about this machine and must escalate on the ordinary schedule
        // — this outcome must never become the place a real failure goes to be quiet.
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        var session = new FakeUserSession();

        session.Answers["wpctl get-volume @DEFAULT_AUDIO_SINK@"] = new ProcessResult(0, string.Empty, string.Empty);

        var block = Audio(files, processes, session: session);
        var observation = await Observe(block, WirePlumberVolumeResource.ResourceName);

        Assert.Equal(ObservationOutcome.Drifted, observation.Outcome);
        Assert.Contains("carries no volume", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_fleet_setting_moves_both_owners_of_the_speaker_level_together()
    {
        // The whole reason this is a second resource rather than a second copy of a number. Two
        // owners deriving from one setting cannot disagree about what the frame wants; two copies
        // of a desired value are two things that can, and this resource exists because two owners
        // did.
        using var files = new TemporaryFiles();
        var processes = Mixer(files, (Pcm0Correct, Pcm1Correct));
        var session = new FakeUserSession();
        session.Answers["wpctl get-volume @DEFAULT_AUDIO_SINK@"] =
            new ProcessResult(0, "Volume: 1.00\n", string.Empty);

        var values = FleetValues.From(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AudioCatalog.PlaybackVolumeKey] = "40",
        });

        var block = Audio(files, processes, values, session);

        Assert.Contains(
            "PCM,0=40",
            (await Observe(block, AudioCatalog.Pcm0VolumeResourceName)).Expected,
            StringComparison.Ordinal);

        // 40 steps is -20 dB, which is 0.10 linear. A sink still at full scale is now drift.
        var wireplumber = await Observe(block, WirePlumberVolumeResource.ResourceName);

        Assert.False(wireplumber.InSync);
        Assert.Contains("0.10", wireplumber.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stored_state_file_is_written_after_both_owners_have_agreed()
    {
        // alsactl store records whatever is live at the instant it runs, and the mixer has an owner
        // in the login session that writes it once that session is up. Ordering it explicitly is
        // what stops the persisted file being a snapshot of whichever owner happened to go last.
        using var files = new TemporaryFiles();
        var block = Audio(files, Mixer(files, (Pcm0Correct, Pcm1Correct)));

        Assert.Contains(
            WirePlumberVolumeResource.ResourceName,
            Find(block, AlsaStoredStateResource.ResourceName).DependsOn,
            StringComparer.Ordinal);

        // And it is behind the mixer values in the walk, not merely dependent on them.
        var names = block.Select(resource => resource.Name).ToList();

        Assert.True(
            names.IndexOf(WirePlumberVolumeResource.ResourceName)
                < names.IndexOf(AlsaStoredStateResource.ResourceName),
            "the stored-state resource is ordered before the second owner it has to wait for");
    }

    private static double Decibels(double volume) => 20d * Math.Log10(volume);

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
        bool executable = true)
    {
        SeedTool(files, processes, XvfHost.AgentDirectory, version, gpo, executable);
    }

    /// <summary>Puts one USB device on the bus, as sysfs publishes it.</summary>
    private static void Usb(
        TemporaryFiles files,
        string path,
        string vendor,
        string product,
        string bcd,
        string serial)
    {
        var directory = XvfArrayUsb.DevicesPath + "/" + path;

        // Trailing newline, because sysfs attributes carry one and the reader has to trim it.
        files.Seed(directory + "/idVendor", vendor + Environment.NewLine);
        files.Seed(directory + "/idProduct", product + Environment.NewLine);
        files.Seed(directory + "/bcdDevice", bcd + Environment.NewLine);
        files.Seed(directory + "/serial", serial + Environment.NewLine);
    }

    /// <summary>The observe-only reporter that replaced the firmware resource.</summary>
    private static ArrayFirmwareReporter Reporter(
        TemporaryFiles files,
        RecordingProcessRunner processes,
        NullReconcileTelemetry? telemetry = null)
    {
        var session = new FakeUserSession();

        return new ArrayFirmwareReporter(
            new XvfHost(files.Files, processes, session),
            files.Files,
            telemetry ?? new NullReconcileTelemetry(),
            files.Store,
            new ManualClock(),
            new RecordingLog())
        {
            DeviceId = "TEST-DEVICE",
        };
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
        var shell = session ?? new FakeUserSession();

        // A frame whose WirePlumber has finished starting, unless the test says otherwise. Every
        // test in this block is about a *settled* frame — a mixer value, a route volume, a stored
        // state file — so the graph MediaGraphGate reads is scripted here rather than in each of
        // them, and TryAdd leaves the one test that scripts an unbuilt graph alone.
        shell.Answers.TryAdd("wpctl status", new ProcessResult(0, WpctlCaptures.Settled, string.Empty));

        var context = AgentResourceGraphTests.Context(files) with
        {
            Processes = processes,
            Session = shell,
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

/// <summary>
/// A frame, a pin over six small files, and a download that serves exactly those files.
/// </summary>
/// <remarks>
/// <para>
/// <b>A test pin rather than the shipping one, for the same reason the Immich Kiosk fixture builds
/// its own archive:</b> the real pin names 2.1 MB of somebody else's binaries, and the licence
/// position that made a fetch the right answer at all (decision 63) is precisely that those bytes
/// are never in this repository. So the payloads here are generated, and their digests are measured
/// from the payloads — which is the same relationship the shipping pin has to upstream's bytes.
/// </para>
/// <para>
/// The binary's payload deliberately crosses the installer's 64 kB copy buffer, so the streaming
/// path is the path under test rather than a single-read shortcut.
/// </para>
/// </remarks>
internal sealed class XvfHostFixture : IDisposable
{
    private readonly TemporaryFiles _files = new();
    private readonly FakeUserSession _session = new();
    private readonly Dictionary<string, byte[]> _payloads = new(StringComparer.Ordinal);

    public XvfHostFixture()
    {
        _files.Seed(AlsaCards.CardsPath, " 0 [Array          ]: USB-Audio - reSpeaker XVF3800 4-Mic Array\n");

        var files = new List<XvfHostFile>();

        foreach (var (name, executable) in ((string Name, bool Executable)[])
            [
                (XvfHost.Binary, true),
                ("libcommand_map.so", false),
                ("libdevice_i2c.so", false),
                ("libdevice_usb.so", false),
                ("dfu_cmds.yaml", false),
                ("transport_config.yaml", false),
            ])
        {
            var payload = new byte[executable ? 200_000 : 512];
            for (var index = 0; index < payload.Length; index++)
            {
                payload[index] = (byte)((index + name.Length) % 251);
            }

            _payloads[name] = payload;
            files.Add(new XvfHostFile(
                name,
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload)),
                payload.Length,
                executable));
        }

        Pin = new XvfHostReleasePin
        {
            Owner = "respeaker",
            Repository = "reSpeaker_XVF3800_USB_4MIC_ARRAY",
            Commit = "0123456789abcdef0123456789abcdef01234567",
            DirectoryInRepository = "host_control/rpi_64bit",
            Files = files,
            ReviewedUtc = new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero),
        };

        const string Banner = "Device (USB)::device_init() -- Found device VID: 10374 PID: 26 interface: 3\n";

        foreach (var root in (string[])[XvfHost.AgentDirectory, HomeRoot])
        {
            var directory = XvfHost.ToolDirectory(root);
            Processes.Answers[
                $"env -C {directory} LD_LIBRARY_PATH={directory} {directory}/{XvfHost.Binary} {XvfHost.VersionCommand}"] =
                new ProcessResult(0, Banner + "VERSION 2 0 10", string.Empty);
        }
    }

    public XvfHostReleasePin Pin { get; }

    public RecordingProcessRunner Processes { get; } = new();

    public StubXvfHostDownload Download { get; } = new();

    public string HomeRoot => _session.HomeDirectory.TrimEnd('/') + "/" + XvfHost.HomeSubdirectory;

    /// <summary>Serves every pinned file, as upstream would.</summary>
    public void ServeEverything()
    {
        foreach (var (name, payload) in _payloads)
        {
            Download.Payloads[name] = payload;
        }
    }

    /// <summary>Serves the right length of the wrong bytes for one file.</summary>
    public void Corrupt(string name)
    {
        var payload = (byte[])_payloads[name].Clone();
        payload[^1] ^= 0xFF;
        Download.Payloads[name] = payload;
    }

    /// <summary>Serves more bytes than the pin allows for one file.</summary>
    public void Oversize(string name) => Download.Payloads[name] = new byte[_payloads[name].Length + 4096];

    /// <summary>Puts the pinned files on disk, as an already-provisioned frame would have them.</summary>
    public void SeedPinnedFiles(string root, bool executable = true, string? except = null)
    {
        foreach (var file in Pin.Files)
        {
            if (string.Equals(file.Name, except, StringComparison.Ordinal))
            {
                continue;
            }

            Write(root, file.Name, _payloads[file.Name]);

            if (file.Executable && executable)
            {
                _files.Files.SetMode(
                    XvfHost.ToolDirectory(root) + "/" + file.Name,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    /// <summary>Replaces one on-disk file with bytes that are not the pinned ones.</summary>
    public void Damage(string root, string name) => Write(root, name, [1, 2, 3, 4]);

    /// <summary>Takes one on-disk file away, as a half-wiped card would.</summary>
    public void Remove(string root, string name) =>
        _files.Files.DeleteFile(XvfHost.ToolDirectory(root) + "/" + name);

    public bool Exists(string root, string name) =>
        _files.Files.FileExists(XvfHost.ToolDirectory(root) + "/" + name);

    /// <summary>Observes through a resource built for this call, or through a shared one.</summary>
    /// <param name="fresh">
    /// True builds a new resource and a new installer over the same filesystem, which is what a
    /// process restart or a reboot leaves behind. It is how "the state survived" is asked as a
    /// question about the disk rather than about an object's memory.
    /// </param>
    public async Task<ResourceObservation> Observe(bool fresh = false) =>
        await Resource(fresh).ObserveAsync(TestContext.Current.CancellationToken);

    public async Task<ResourceAction> Act() =>
        await Resource(fresh: false).ActAsync(TestContext.Current.CancellationToken);

    public void Dispose() => _files.Dispose();

    private void Write(string root, string name, byte[] content)
    {
        var resolved = _files.Files.Resolve(XvfHost.ToolDirectory(root) + "/" + name);
        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        File.WriteAllBytes(resolved, content);
    }

    private XvfHostToolResource? _resource;

    private XvfHostToolResource Resource(bool fresh)
    {
        if (fresh)
        {
            _resource = null;
        }

        return _resource ??= new XvfHostToolResource(
            new XvfHost(_files.Files, Processes, _session),
            _files.Files,
            new XvfHostInstaller(_files.Files, Download, new RecordingLog(), Pin));
    }
}

/// <summary>Answers with bytes held in memory, keyed by file name, and records every URL asked for.</summary>
internal sealed class StubXvfHostDownload : IXvfHostDownload
{
    public Dictionary<string, byte[]> Payloads { get; } = new(StringComparer.Ordinal);

    public List<Uri> Opened { get; } = [];

    public Task<Stream?> OpenAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);

        Opened.Add(url);
        var name = url.Segments[^1];

        return Task.FromResult<Stream?>(
            Payloads.TryGetValue(name, out var payload) ? new MemoryStream(payload) : null);
    }
}
