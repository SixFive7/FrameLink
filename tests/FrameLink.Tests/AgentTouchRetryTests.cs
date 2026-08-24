using FrameLink.Agent.Hosting;
using FrameLink.Agent.Local;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Stage;
using FrameLink.Agent.State;

namespace FrameLink.Tests;

/// <summary>
/// <b>Retry, pressed at the frame, on the console stage</b> — version2.md §2.5 rung 5, §2.7 item 9,
/// decisions 72 and 77.
/// </summary>
/// <remarks>
/// <para>
/// Decision 72 shipped the sentence <i>"This screen has no buttons — the Try again button is in the
/// Fleet Manager"</i> because nothing in this repository had ever captured an input device from a
/// frame, so whether the panel exposed one was genuinely unknown. It has since been measured and it
/// does, which makes that sentence false in front of a person touching the screen it is printed on.
/// </para>
/// <para>
/// <b>The capture below is verbatim.</b> Every claim the discovery makes is asserted against the
/// real <c>/proc/bus/input/devices</c> from the frame, including the five devices that must
/// <i>not</i> be chosen — the reSpeaker array publishes three input devices and each HDMI port
/// publishes a CEC receiver, all of them with keys.
/// </para>
/// <para>
/// <b>Nothing here has run on hardware.</b> The parser is exercised against a real capture and the
/// state machine against a fake device; no evdev node is opened in this process.
/// </para>
/// </remarks>
public sealed class AgentTouchRetryTests
{
    /// <summary>
    /// <c>/proc/bus/input/devices</c> from the frame, 2026-08-16, verbatim.
    /// </summary>
    private const string CapturedDevices = """
        I: Bus=0019 Vendor=0001 Product=0001 Version=0100
        N: Name="pwr_button"
        P: Phys=gpio-keys/input0
        S: Sysfs=/devices/platform/pwr_button/input/input0
        U: Uniq=
        H: Handlers=kbd event0
        B: PROP=0
        B: EV=3
        B: KEY=10000000000000 0

        I: Bus=0003 Vendor=2886 Product=001a Version=0111
        N: Name="Seeed Studio reSpeaker XVF3800 4-Mic Array"
        P: Phys=usb-xhci-hcd.0-1/input5
        S: Sysfs=/devices/platform/axi/1000120000.pcie/1f00200000.usb/xhci-hcd.0/usb1/1-1/1-1:1.5/0003:2886:001A.0001/input/input1
        U: Uniq=101991441260500069
        H: Handlers=kbd leds event1
        B: PROP=0
        B: EV=20013
        B: KEY=fff 0 0 0 40 100000000000000 0 0 0
        B: MSC=10
        B: LED=80

        I: Bus=0003 Vendor=2886 Product=001a Version=0111
        N: Name="Seeed Studio reSpeaker XVF3800 4-Mic Array Consumer Control"
        P: Phys=usb-xhci-hcd.0-1/input5
        S: Sysfs=/devices/platform/axi/1000120000.pcie/1f00200000.usb/xhci-hcd.0/usb1/1-1/1-1:1.5/0003:2886:001A.0001/input/input2
        U: Uniq=101991441260500069
        H: Handlers=kbd event2
        B: PROP=0
        B: EV=13
        B: KEY=c000000000000 0
        B: MSC=10

        I: Bus=0003 Vendor=2886 Product=001a Version=0111
        N: Name="Seeed Studio reSpeaker XVF3800 4-Mic Array"
        P: Phys=usb-xhci-hcd.0-1/input5
        S: Sysfs=/devices/platform/axi/1000120000.pcie/1f00200000.usb/xhci-hcd.0/usb1/1-1/1-1:1.5/0003:2886:001A.0001/input/input3
        U: Uniq=101991441260500069
        H: Handlers=event3
        B: PROP=0
        B: EV=13
        B: KEY=1 0 0 0 0
        B: MSC=10

        I: Bus=0018 Vendor=0416 Product=2437 Version=1070
        N: Name="Goodix Capacitive TouchScreen"
        P: Phys=input/ts
        S: Sysfs=/devices/platform/axi/1000120000.pcie/1f00080000.i2c/i2c-11/11-005d/input/input4
        U: Uniq=
        H: Handlers=kbd mouse0 event4
        B: PROP=2
        B: EV=b
        B: KEY=400 0 0 0 2000000000000001 f800000000000000
        B: ABS=265800000000003

        I: Bus=001e Vendor=0000 Product=0000 Version=0001
        N: Name="vc4-hdmi-0"
        P: Phys=vc4-hdmi-0/input0
        S: Sysfs=/devices/platform/soc@107c000000/107c701400.hdmi/rc/rc0/input6
        U: Uniq=
        H: Handlers=kbd event5
        B: PROP=20
        B: EV=100017
        B: KEY=ffffc000000000 3ff 0 400000320fc200 40830c900000000 0 210300 49d2c040ec00 1e378000000000 8010000010000000
        B: REL=3
        B: MSC=10

        I: Bus=001e Vendor=0000 Product=0000 Version=0001
        N: Name="vc4-hdmi-1"
        P: Phys=vc4-hdmi-1/input0
        S: Sysfs=/devices/platform/soc@107c000000/107c706400.hdmi/rc/rc1/input7
        U: Uniq=
        H: Handlers=kbd event6
        B: PROP=20
        B: EV=100017
        B: KEY=ffffc000000000 3ff 0 400000320fc200 40830c900000000 0 210300 49d2c040ec00 1e378000000000 8010000010000000
        B: REL=3
        B: MSC=10

        """;

    [Fact]
    public void The_panel_is_found_by_what_it_can_do_rather_than_by_where_it_is()
    {
        // Measured on the frame: /dev/input/event4, Goodix Capacitive TouchScreen. Neither the
        // event number nor the by-path name is hard-coded anywhere — the number moves with probe
        // order and the path is one board's I2C address — so the whole of the identification is the
        // capability bitmaps the kernel publishes beside it.
        var device = EvdevTouchInput.TouchscreenIn(CapturedDevices);

        Assert.NotNull(device);
        Assert.Equal("/dev/input/event4", device.Value.Node);
        Assert.Equal("Goodix Capacitive TouchScreen", device.Value.Name);
    }

    [Fact]
    public void The_six_devices_that_are_not_a_touchscreen_are_not_chosen()
    {
        // The reSpeaker array publishes three input devices and each HDMI port publishes a CEC
        // receiver, all with EV_KEY — so "has keys" would pick the microphone. INPUT_PROP_DIRECT
        // plus absolute axes plus BTN_TOUCH is what leaves exactly the panel.
        foreach (var block in CapturedDevices.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (block.Contains("Goodix", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.Null(EvdevTouchInput.TouchscreenIn(block + "\n\n"));
        }
    }

    [Fact]
    public void A_machine_with_no_input_devices_at_all_reports_no_touchscreen()
    {
        // Every workstation this suite runs on, and every frame whose panel overlay has not been
        // applied yet — which is the first two resources of a bare provision.
        Assert.Null(EvdevTouchInput.TouchscreenIn(null));
        Assert.Null(EvdevTouchInput.TouchscreenIn(string.Empty));
    }

    [Fact]
    public void Capability_bitmaps_are_read_most_significant_word_first()
    {
        // The one thing about this format that is easy to get backwards, and getting it backwards
        // finds nothing while failing silently: the kernel prints the words in descending
        // significance, so BTN_TOUCH (0x14a = 330) lives in the *leftmost* word of a six-word KEY
        // bitmap, which covers bits 320 to 383.
        const string Key = "400 0 0 0 2000000000000001 f800000000000000";

        Assert.True(EvdevTouchInput.HasBit(Key, 0x14a));
        Assert.False(EvdevTouchInput.HasBit(Key, 0x14b));

        // PROP=2 is INPUT_PROP_DIRECT, which is the bit that tells a screen you touch from a pad
        // you point with. PROP=0 on every other device in the capture.
        Assert.True(EvdevTouchInput.HasBit("2", 1));
        Assert.False(EvdevTouchInput.HasBit("0", 1));

        // EV=b is SYN | KEY | ABS.
        Assert.True(EvdevTouchInput.HasBit("b", 0x01));
        Assert.True(EvdevTouchInput.HasBit("b", 0x03));
        Assert.False(EvdevTouchInput.HasBit("b", 0x02));

        // A bit past the end of the bitmap is absent rather than an exception.
        Assert.False(EvdevTouchInput.HasBit("b", 4096));
    }

    [Fact]
    public void The_event_node_is_taken_from_the_handlers_line_and_nothing_else_on_it()
    {
        // "kbd mouse0 event4" — the panel is also a mouse and a keyboard to the kernel, and
        // neither of those is a node this reads.
        Assert.Equal("/dev/input/event4", EvdevTouchInput.EventNodeIn("kbd mouse0 event4 "));
        Assert.Null(EvdevTouchInput.EventNodeIn("kbd mouse0 "));
        Assert.Null(EvdevTouchInput.EventNodeIn("eventful"));
    }

    [Fact]
    public void Nothing_happens_while_the_finger_is_down_however_long_it_stays_there()
    {
        // Decision 94, and the property everything else about this gesture rests on. A hold that
        // restarted the frame the instant it reached three seconds could never reach ten, so two
        // hold lengths on one finger force the decision to the release. Nothing acts before then —
        // not at three seconds, not at ten, not at thirty.
        var harness = new TouchHarness();

        harness.Down();
        harness.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(0, harness.Restarts);
        Assert.Equal(0, harness.Shutdowns);

        // And the screen is still showing the hold, because the finger is still there.
        Assert.NotNull(harness.Hub.Current.Touch.HoldingSince);
    }

    [Fact]
    public void A_tap_and_a_short_hold_are_how_somebody_changes_their_mind()
    {
        // The first band is the whole of the way out of a gesture that has no cancel button and no
        // coordinates to put one at: take the finger off before the first mark and the frame does
        // nothing at all. It is also what makes a brush past the frame or somebody wiping it clean
        // harmless, which is the accident §2.7 item 9 rejected a tap over in the first place.
        var harness = new TouchHarness();

        harness.Down();
        harness.Up();
        harness.Tick();

        Assert.Equal(0, harness.Restarts);
        Assert.Equal(0, harness.Shutdowns);

        harness.Down();
        harness.Advance(TouchRetry.RestartHold - TimeSpan.FromMilliseconds(100));
        harness.Up();
        harness.Tick();

        Assert.Equal(0, harness.Restarts);
        Assert.Equal(0, harness.Shutdowns);
    }

    [Fact]
    public void Letting_go_after_three_seconds_restarts_and_letting_go_after_ten_switches_off()
    {
        // The two verbs §2.5 rung 5 puts side by side as buttons on the browser stage, as two
        // lengths of the one gesture the console can read. Each fires once per press: a release is
        // a single edge, and the hold is forgotten at it.
        var harness = new TouchHarness();

        harness.Down();
        harness.Advance(TouchRetry.RestartHold);
        harness.Up();
        harness.Tick();

        Assert.Equal(1, harness.Restarts);
        Assert.Equal(0, harness.Shutdowns);

        harness.Down();
        harness.Advance(TouchRetry.ShutdownHold);
        harness.Up();
        harness.Tick();

        Assert.Equal(1, harness.Restarts);
        Assert.Equal(1, harness.Shutdowns);

        // Anything past the second mark is still the second mark. Nothing expires and nothing is
        // withdrawn for holding on too long — there is no third band, because a length that stopped
        // meaning what the screen said it meant would be the countdown that gives up on its own.
        harness.Down();
        harness.Advance(TimeSpan.FromMinutes(2));
        harness.Up();
        harness.Tick();

        Assert.Equal(1, harness.Restarts);
        Assert.Equal(2, harness.Shutdowns);
    }

    [Fact]
    public void A_hold_on_a_frame_that_has_not_given_up_does_nothing_at_all()
    {
        // The screen only invites a hold when there is a budget to reset (§2.7 item 9), so a hold
        // when nothing is on offer must be inert — and must not draw a progress bar either, because
        // an indicator that fills and then achieves nothing is exactly the affordance decision 72
        // refused to ship. Including at the release, which is where the two verbs now act.
        var harness = new TouchHarness(offered: false);

        harness.Down();
        harness.Advance(TouchRetry.ShutdownHold * 2);

        Assert.Null(harness.Hub.Current.Touch.HoldingSince);

        harness.Up();
        harness.Tick();

        Assert.Equal(0, harness.Restarts);
        Assert.Equal(0, harness.Shutdowns);
    }

    [Fact]
    public void The_hold_publishes_when_it_starts_and_stops_and_at_no_other_time()
    {
        // Twenty polls a second against a hub every subscriber repaints on would be twenty console
        // frames a second for a screen where nothing is happening. The band and the seconds still to
        // go are worked out by the renderer from the instant it is rendering, which is what keeps it
        // a pure function and what keeps this quiet.
        var harness = new TouchHarness();
        var atStart = harness.Publishes;

        harness.Down();
        Assert.Equal(atStart + 1, harness.Publishes);

        harness.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(atStart + 1, harness.Publishes);

        Assert.NotNull(harness.Hub.Current.Touch.HoldingSince);

        // Still one publish eight seconds in, having crossed the first mark on the way: the bar
        // moving and the words under it changing are both composed by the renderer, so crossing a
        // band costs nothing on the wire.
        harness.Advance(TimeSpan.FromSeconds(7));
        Assert.Equal(atStart + 1, harness.Publishes);

        // The release ends the hold on screen and is the one thing that acts. A bar that stayed
        // would be the animation decision 70 forbids.
        harness.Up();
        harness.Tick();

        Assert.Equal(atStart + 2, harness.Publishes);
        Assert.Null(harness.Hub.Current.Touch.HoldingSince);
        Assert.Equal(1, harness.Restarts);
    }

    [Fact]
    public void A_frame_with_no_touchscreen_publishes_that_it_has_none_and_offers_nothing()
    {
        var harness = new TouchHarness(present: false);

        Assert.False(harness.Watch.EnsureOpen());
        Assert.False(harness.Hub.Current.Touch.Available);
        Assert.Equal(0, harness.Restarts);
        Assert.Equal(0, harness.Shutdowns);
    }

    [Fact]
    public void A_touchscreen_that_goes_away_is_reported_and_looked_for_again()
    {
        // A panel unplugged, or a driver reloaded. A screen that silently stops answering the one
        // affordance it offers is precisely what a false sentence about that affordance amounts to.
        var harness = new TouchHarness();

        Assert.True(harness.Hub.Current.Touch.Available);

        harness.Reader!.Fail = true;
        harness.Tick();

        Assert.False(harness.Hub.Current.Touch.Available);

        harness.Reader!.Fail = false;
        Assert.True(harness.Watch.EnsureOpen());
        Assert.True(harness.Hub.Current.Touch.Available);
    }

    [Fact]
    public void The_console_explains_both_gestures_and_names_the_server_when_there_is_no_touchscreen()
    {
        // Decision 77, and the whole point of it: the sentences are chosen from what the agent
        // found rather than from what was assumed. Decision 94 adds the second verb, and names
        // *both* buttons in the no-touchscreen case — a sentence naming only the restart would
        // leave somebody who wanted the frame off believing there was nowhere to do it.
        Assert.Equal(
            [
                "This frame has no touchscreen, so nothing can be pressed on this screen. The buttons that "
                + "restart it and switch it off are in the Fleet Manager.",
            ],
            ReconcileVoice.TouchLines(TouchRetryState.None));

        var lines = ReconcileVoice.TouchLines(Panel);

        // Five sentences, in the order somebody who has never used a touchscreen needs them: that
        // the glass responds at all, where to look to see the frame noticed, the way out *before*
        // either verb, then the two verbs, cheapest first.
        Assert.Equal(5, lines.Count);
        Assert.Equal(
            "This screen feels your finger. Put one finger anywhere on it and keep it still. Do not tap the "
            + "screen, and do not take your finger off straight away.",
            lines[0]);
        Assert.Equal(
            "While your finger rests there a bar fills up near the bottom of this box, and the line under the "
            + "bar always says what would happen if you took your finger off at that moment. Nothing happens "
            + "while your finger is still on the screen.",
            lines[1]);
        Assert.Equal(
            "Take your finger off in the first 3 seconds and nothing happens at all. That is how you change "
            + "your mind.",
            lines[2]);
        Assert.Equal(
            "Keep your finger there for 3 seconds, then take it off: this frame restarts and tries everything "
            + "again. The screen goes dark for about a minute and then comes back on its own.",
            lines[3]);

        // The one line that has to state a cost rather than a name: nothing remote brings a frame
        // back, so the sentence says who has to walk over to it and what they have to do there.
        Assert.Equal(
            "Keep your finger there for 10 seconds instead, then take it off: this frame switches off and "
            + "stays off. Nothing can switch it on again from anywhere else — somebody has to come to this "
            + "frame, unplug it and plug it in again.",
            lines[4]);

        // And a firmware question says none of this: that screen writes its own sentences, and the
        // hold in front of the person does neither of these two things.
        Assert.Empty(ReconcileVoice.TouchLines(
            new TouchRetryState("/dev/input/event4", TimeSpan.FromSeconds(5), null)));
    }

    [Fact]
    public void A_stopped_frame_with_a_touchscreen_tells_the_person_in_front_of_it_what_to_do()
    {
        var frame = StageRenderer.Render(
            Stopped with { Touch = Panel },
            DateTimeOffset.UnixEpoch,
            tick: 0,
            160,
            40,
            colour: false);

        Assert.Contains("This screen feels your finger.", frame, StringComparison.Ordinal);
        Assert.Contains("nothing happens at all. That is how you change your mind.", frame, StringComparison.Ordinal);
        Assert.Contains("3 seconds, then take it off: this frame restarts", frame, StringComparison.Ordinal);
        Assert.Contains("10 seconds instead, then take it off: this frame switches off", frame, StringComparison.Ordinal);
        Assert.Contains("unplug it and plug it in again", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("are in the Fleet Manager", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stopped_frame_with_no_touchscreen_says_so_and_names_the_fleet_manager()
    {
        // The honest half of decision 72 survives, and is now true of the frame that prints it: a
        // frame whose panel overlay has not been applied yet really has no touchscreen, and on that
        // frame the Fleet Manager really is where both buttons are.
        var frame = StageRenderer.Render(Stopped, DateTimeOffset.UnixEpoch, tick: 0, 160, 40, colour: false);

        Assert.Contains(
            "The buttons that restart it and switch it off are in the Fleet Manager",
            frame,
            StringComparison.Ordinal);

        Assert.DoesNotContain("Keep your finger there", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hold_in_progress_says_what_letting_go_would_do_and_what_holding_on_would_do()
    {
        // Somebody holding a screen with nothing happening lets go at two seconds and concludes the
        // frame is dead. So the bar is determinate and measured against the instant being rendered
        // — a report of the person's own finger rather than the animation decision 70 forbids — and
        // the line under it always names the band the release is in *and* the next one, because
        // "nothing happens" on its own reads as a screen that has not noticed.
        var began = DateTimeOffset.UnixEpoch;
        var status = Stopped with { Touch = Panel with { HoldingSince = began } };

        string At(double seconds) =>
            StageRenderer.Render(status, began + TimeSpan.FromSeconds(seconds), 0, 160, 40, colour: false);

        Assert.Contains("nothing yet", At(1), StringComparison.Ordinal);
        Assert.Contains(
            "Take your finger off now and nothing happens. Keep it there for 2 more seconds to restart this frame.",
            At(1),
            StringComparison.Ordinal);

        // Rounded up, and never zero: the number is an instruction, so a person told "1 second" at
        // 1.5 s to go lets go at 1 s and lands back in the band they were trying to leave.
        Assert.Contains("Keep it there for 2 more seconds to restart", At(1.5), StringComparison.Ordinal);
        Assert.Contains("Keep it there for 1 more second to restart", At(2.5), StringComparison.Ordinal);
        Assert.Contains("Keep it there for 1 more second to restart", At(2.99), StringComparison.Ordinal);

        Assert.Contains("restart", At(4), StringComparison.Ordinal);
        Assert.Contains(
            "Take your finger off now and this frame restarts and tries everything again. Keep it there for "
            + "6 more seconds instead and it switches off.",
            At(4),
            StringComparison.Ordinal);

        Assert.Contains("switch off", At(11), StringComparison.Ordinal);
        Assert.Contains(
            "Take your finger off now and this frame switches off. It stays off until somebody comes to it, "
            + "unplugs it and plugs it in again.",
            At(11),
            StringComparison.Ordinal);

        // And only while a finger is actually down. A bar that survived the release would be the
        // animation decision 70 forbids, drawn on the one screen that rule was written for.
        var released = StageRenderer.Render(Stopped with { Touch = Panel }, began, 0, 160, 40, colour: false);

        Assert.DoesNotContain("Take your finger off now", released, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing yet", released, StringComparison.Ordinal);
    }

    [Fact]
    public void The_band_the_screen_promises_is_the_band_the_release_takes()
    {
        // The one property that makes the whole gesture honest: the words under the bar and the verb
        // the release calls come from the same TouchRetryState.Commit, so there is no arrangement of
        // state in which the frame does something other than what it just said it would do.
        var began = DateTimeOffset.UnixEpoch;
        var hold = Panel with { HoldingSince = began };

        Assert.Equal(TouchCommit.Nothing, hold.Commit(began));
        Assert.Equal(TouchCommit.Nothing, hold.Commit(began + TimeSpan.FromSeconds(2.999)));
        Assert.Equal(TouchCommit.Restart, hold.Commit(began + TouchRetry.RestartHold));
        Assert.Equal(TouchCommit.Restart, hold.Commit(began + TimeSpan.FromSeconds(9.999)));
        Assert.Equal(TouchCommit.Shutdown, hold.Commit(began + TouchRetry.ShutdownHold));
        Assert.Equal(TouchCommit.Shutdown, hold.Commit(began + TimeSpan.FromHours(1)));

        // A hold with one mark commits nothing on a release, whatever its length: it has already
        // acted while the finger was down, or it is not finished.
        var question = new TouchRetryState("/dev/input/event4", TimeSpan.FromSeconds(5), began);

        Assert.False(question.TwoWay);
        Assert.Equal(TouchCommit.Nothing, question.Commit(began + TimeSpan.FromMinutes(1)));

        // And a frame with nothing on the glass has nothing to commit.
        Assert.Equal(TouchCommit.Nothing, Panel.Commit(began + TimeSpan.FromMinutes(1)));

        Assert.Equal("nothing yet", ReconcileVoice.HoldBand(TouchCommit.Nothing));
        Assert.Equal("restart", ReconcileVoice.HoldBand(TouchCommit.Restart));
        Assert.Equal("switch off", ReconcileVoice.HoldBand(TouchCommit.Shutdown));
    }

    [Fact]
    public void The_bar_fills_over_the_whole_gesture_so_the_first_mark_is_visibly_not_the_end()
    {
        // The bar is the frame saying it noticed, and it fills over the *shutdown* length. Filling
        // it over three seconds and then leaving it full for seven more would tell somebody the
        // gesture was finished at exactly the moment they still had a decision to make.
        var began = DateTimeOffset.UnixEpoch;
        var hold = Panel with { HoldingSince = began };

        Assert.Equal(0.3, hold.Progress(began + TouchRetry.RestartHold), 3);
        Assert.Equal(1, hold.Progress(began + TouchRetry.ShutdownHold), 3);
        Assert.Equal(1, hold.Progress(began + TimeSpan.FromMinutes(1)), 3);
    }

    /// <summary>The measured panel, with both marks and nothing being held.</summary>
    private static TouchRetryState Panel => new(
        "/dev/input/event4",
        TouchRetry.ShutdownHold,
        null,
        TouchRetry.RestartHold);

    // ---------------------------------------------------------------------------------------
    // Decision 91: one reader, two things a hold can mean, and the precedence between them
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void A_firmware_question_outranks_the_retry_and_brings_its_own_hold()
    {
        // The frame is showing "do not unplug this — hold for five seconds to agree", and the same
        // panel is the one a three-second hold uses to retry a stopped resource. A hold must do what
        // the sentence in front of the person says, and nothing else: three seconds under a
        // five-second question must do *nothing*, because a frame that started writing firmware at
        // three would have started it before the sentence had finished asking.
        var harness = new TouchHarness();
        harness.Ask = harness.FirmwareAsk();

        harness.Down();
        harness.Advance(TimeSpan.FromSeconds(3.5));

        Assert.Equal(0, harness.Answers);
        Assert.Equal(0, harness.Restarts);

        harness.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(1, harness.Answers);

        // And never a recovery verb. The two are not both taken by one hold, however long it is
        // held — and the release, which is what the recovery pair acts on, is answered by a hold
        // that has already fired rather than by a second action.
        harness.Advance(TimeSpan.FromSeconds(20));
        harness.Up();
        harness.Tick();

        Assert.Equal(1, harness.Answers);
        Assert.Equal(0, harness.Restarts);
        Assert.Equal(0, harness.Shutdowns);
    }

    [Fact]
    public void The_hold_the_screen_counts_is_the_hold_the_question_asked_for()
    {
        // The bar drawn on the console is composed from what the watch publishes, so a five-second
        // question that published a three-second hold would count somebody's finger to full and then
        // sit there doing nothing for two seconds — which reads as a screen that has died.
        var harness = new TouchHarness();
        harness.Ask = harness.FirmwareAsk();

        harness.Down();
        harness.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(5), harness.Watch.State.Hold);

        // And it has one mark, not two, which is how the renderer tells a question from the
        // recovery pair: a question fires while the finger is down and has nothing to say about a
        // release.
        Assert.Null(harness.Watch.State.RestartAt);
        Assert.False(harness.Watch.State.TwoWay);

        harness.Up();
        harness.Ask = null;
        harness.Tick();

        Assert.Equal(TouchRetry.ShutdownHold, harness.Watch.State.Hold);
        Assert.Equal(TouchRetry.RestartHold, harness.Watch.State.RestartAt);
        Assert.True(harness.Watch.State.TwoWay);
    }

    [Fact]
    public void Letting_go_early_under_a_question_does_not_restart_the_frame_instead()
    {
        // The sequence this is written for: a firmware screen is up asking for five seconds, and
        // somebody holds for four and changes their mind. Four seconds is past the restart mark, so
        // a release that only looked at its own length would restart a frame in the middle of a
        // question it had just declined to answer — a verb nothing on that screen offered.
        var harness = new TouchHarness();
        harness.Ask = harness.FirmwareAsk();

        harness.Down();
        harness.Advance(TimeSpan.FromSeconds(4));
        harness.Up();
        harness.Tick();

        Assert.Equal(0, harness.Answers);
        Assert.Equal(0, harness.Restarts);
        Assert.Equal(0, harness.Shutdowns);
    }

    [Fact]
    public void Answering_a_question_and_then_letting_go_does_not_also_restart_the_frame()
    {
        // The other half, and the likelier of the two: the hold completes, the question is answered,
        // the screen it was on goes away — and only then does the finger come off. By that point
        // there is no ask any more, so the release is looking at a nine-second hold on a frame that
        // offers a restart at three. It must do nothing: this press has already been spent.
        var harness = new TouchHarness();
        harness.Ask = harness.FirmwareAsk();

        harness.Down();
        harness.Advance(TimeSpan.FromSeconds(5.5));

        Assert.Equal(1, harness.Answers);

        harness.Ask = null;
        harness.Advance(TimeSpan.FromSeconds(3.5));
        harness.Up();
        harness.Tick();

        Assert.Equal(1, harness.Answers);
        Assert.Equal(0, harness.Restarts);
        Assert.Equal(0, harness.Shutdowns);

        // And the next press is a fresh one, so the panel is not left inert by the answer it gave.
        harness.Down();
        harness.Advance(TouchRetry.RestartHold);
        harness.Up();
        harness.Tick();

        Assert.Equal(1, harness.Restarts);
    }

    [Fact]
    public void A_question_is_answerable_even_when_nothing_has_given_up()
    {
        // The retry is offered only when a resource has stopped, which is the ordinary state of this
        // predicate on a healthy frame — and a firmware question arrives on exactly such a frame,
        // because nothing is wrong with it. An ask that inherited the retry's condition could
        // therefore never be answered on the frames it is written for.
        var harness = new TouchHarness(offered: false);
        harness.Ask = harness.FirmwareAsk();

        harness.Down();
        harness.Advance(TimeSpan.FromSeconds(6));

        Assert.Equal(1, harness.Answers);
    }

    private static AgentStatus Stopped => new()
    {
        Condition = DeviceStateLadder.Starting,
        DeviceId = "TEST-DEVI-CEID-0001",
        Resources =
        [
            new ResourceStatus
            {
                Name = "audio.mixer.pcm-volume",
                Kind = ResourceStatusKind.Escalated,
                Delta = "expected '20', observed '0'",
                Attempts = 3,
                AttemptBudget = 3,
                Escalations = 1,
            },
        ],
        Reconcile = new ReconcileNarration
        {
            Resource = "audio.mixer.pcm-volume",
            Attempt = 3,
            AttemptBudget = 3,
            Escalations = 1,
            AdminNotified = true,
        },
    };

    /// <summary>A whole <see cref="TouchRetry"/> over a fake digitiser and a manual clock.</summary>
    private sealed class TouchHarness
    {
        /// <param name="present">Whether this machine has a touchscreen at all.</param>
        /// <param name="offered">Whether a retry is on offer — §2.7 item 9's own condition.</param>
        public TouchHarness(bool present = true, bool offered = true)
        {
            Device = present ? new TouchDevice("/dev/input/event4", "Goodix Capacitive TouchScreen") : null;
            Offered = offered;

            Hub = new AgentStatusHub(AgentStatusFactory.Starting());
            Hub.Subscribe(_ => Publishes++);

            Watch = new TouchRetry(new TouchRetryServices
            {
                Input = new FakeTouchInput(this),
                Hub = Hub,
                Clock = Clock,
                Log = Log,
                Offered = () => Offered,
                Restart = () => Restarts++,
                Shutdown = () => Shutdowns++,
                Ask = () => Ask,
            });

            Watch.EnsureOpen();
        }

        public ManualClock Clock { get; } = new();

        public RecordingLog Log { get; } = new();

        public AgentStatusHub Hub { get; }

        public TouchRetry Watch { get; }

        public FakeTouchReader? Reader { get; private set; }

        public TouchDevice? Device { get; }

        public bool Offered { get; }

        /// <summary>Something on the screen that outranks the retry, or null (decision 91).</summary>
        public TouchAsk? Ask { get; set; }

        /// <summary>How many times the shorter hold's action has been taken.</summary>
        public int Restarts { get; private set; }

        /// <summary>How many times the longer hold's action has been taken.</summary>
        public int Shutdowns { get; private set; }

        /// <summary>How many times the ask's own action has been taken.</summary>
        public int Answers { get; private set; }

        /// <summary>An ask with the firmware approval's own five-second hold.</summary>
        public TouchAsk FirmwareAsk() =>
            new("agreeing to the microphone update", TimeSpan.FromSeconds(5), () => Answers++);

        public int Publishes { get; private set; }

        public void Down() => Change(true);

        public void Up() => Change(false);

        public void Tick() => Watch.Tick();

        /// <summary>Moves time on in poll-sized steps, exactly as the real loop would.</summary>
        public void Advance(TimeSpan by)
        {
            for (var elapsed = TimeSpan.Zero; elapsed < by; elapsed += TouchRetry.PollInterval)
            {
                Clock.UtcNow += TouchRetry.PollInterval;
                Watch.Tick();
            }
        }

        private void Change(bool down)
        {
            if (Reader is not null)
            {
                Reader.Pending = down;
            }

            Watch.Tick();
        }

        private sealed class FakeTouchInput(TouchHarness harness) : ITouchInput
        {
            public TouchDevice? Find() => harness.Device;

            public ITouchReader? Open(TouchDevice device)
            {
                if (harness.Device is null)
                {
                    return null;
                }

                harness.Reader = new FakeTouchReader();
                return harness.Reader;
            }
        }
    }

    private sealed class FakeTouchReader : ITouchReader
    {
        public bool? Pending { get; set; }

        public bool Fail { get; set; }

        public bool? Drain()
        {
            if (Fail)
            {
                throw new IOException("the touchscreen went away");
            }

            var pending = Pending;
            Pending = null;
            return pending;
        }

        public void Dispose()
        {
        }
    }
}
