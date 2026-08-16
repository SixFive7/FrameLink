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
    public void A_hold_asks_for_a_retry_once_and_a_tap_never_does()
    {
        // §2.7 item 9. A tap would fire on a brush past the frame or on somebody wiping it clean,
        // and what it fires is a retry that starts a frame rebooting. Three seconds is deliberate
        // in a way a tap cannot be.
        var harness = new TouchHarness();

        harness.Down();
        harness.Advance(TimeSpan.FromSeconds(1));
        harness.Up();
        harness.Tick();

        Assert.Equal(0, harness.Retries);

        harness.Down();
        harness.Advance(TouchRetry.HoldDuration - TimeSpan.FromMilliseconds(50));
        Assert.Equal(0, harness.Retries);

        harness.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, harness.Retries);

        // Once per hold, however long the finger stays down. A retry per poll would be twenty
        // budget resets a second for as long as somebody leant on the screen.
        harness.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, harness.Retries);

        // And releasing arms it again, so a second fault can be retried without a reboot.
        harness.Up();
        harness.Tick();
        harness.Down();
        harness.Advance(TouchRetry.HoldDuration);
        Assert.Equal(2, harness.Retries);
    }

    [Fact]
    public void A_hold_on_a_frame_that_has_not_given_up_does_nothing_at_all()
    {
        // The screen only invites a hold when there is a budget to reset (§2.7 item 9), so a hold
        // when nothing is on offer must be inert — and must not draw a progress bar either, because
        // an indicator that fills and then achieves nothing is exactly the affordance decision 72
        // refused to ship.
        var harness = new TouchHarness(offered: false);

        harness.Down();
        harness.Advance(TouchRetry.HoldDuration * 2);

        Assert.Equal(0, harness.Retries);
        Assert.Null(harness.Hub.Current.Touch.HoldingSince);
    }

    [Fact]
    public void The_hold_publishes_when_it_starts_and_stops_and_at_no_other_time()
    {
        // Twenty polls a second against a hub every subscriber repaints on would be twenty console
        // frames a second for a screen where nothing is happening. The remaining seconds are worked
        // out by the renderer from the instant it is rendering, which is what keeps it a pure
        // function and what keeps this quiet.
        var harness = new TouchHarness();
        var atStart = harness.Publishes;

        harness.Down();
        Assert.Equal(atStart + 1, harness.Publishes);

        harness.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(atStart + 1, harness.Publishes);

        Assert.NotNull(harness.Hub.Current.Touch.HoldingSince);

        // Firing ends the hold on screen: the frame is about to start working again, and a bar that
        // stayed would be the animation decision 70 forbids.
        harness.Advance(TouchRetry.HoldDuration);
        Assert.Equal(1, harness.Retries);
        Assert.Null(harness.Hub.Current.Touch.HoldingSince);
    }

    [Fact]
    public void A_frame_with_no_touchscreen_publishes_that_it_has_none_and_offers_nothing()
    {
        var harness = new TouchHarness(present: false);

        Assert.False(harness.Watch.EnsureOpen());
        Assert.False(harness.Hub.Current.Touch.Available);
        Assert.Equal(0, harness.Retries);
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
    public void The_console_offers_the_hold_when_there_is_a_touchscreen_and_names_the_server_when_there_is_not()
    {
        // Decision 77, and the whole point of it: the sentence is chosen from what the agent found
        // rather than from what was assumed.
        Assert.Equal(
            "This frame has no touchscreen — the Try again button is in the Fleet Manager.",
            ReconcileVoice.RetryLine(TouchRetryState.None));

        Assert.Equal(
            "Touch the screen and hold for 3 seconds to try again.",
            ReconcileVoice.RetryLine(new TouchRetryState("/dev/input/event4", TimeSpan.FromSeconds(3), null)));
    }

    [Fact]
    public void A_stopped_frame_with_a_touchscreen_tells_the_person_in_front_of_it_what_to_do()
    {
        var frame = StageRenderer.Render(
            Stopped with { Touch = new TouchRetryState("/dev/input/event4", TimeSpan.FromSeconds(3), null) },
            DateTimeOffset.UnixEpoch,
            tick: 0,
            160,
            40,
            colour: false);

        Assert.Contains("Touch the screen and hold for 3 seconds to try again.", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("the Try again button is in the Fleet Manager", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stopped_frame_with_no_touchscreen_says_so_and_names_the_fleet_manager()
    {
        // The honest half of decision 72 survives, and is now true of the frame that prints it: a
        // frame whose panel overlay has not been applied yet really has no touchscreen, and on that
        // frame the Fleet Manager really is where the button is.
        var frame = StageRenderer.Render(Stopped, DateTimeOffset.UnixEpoch, tick: 0, 160, 40, colour: false);

        Assert.Contains("the Try again button is in the Fleet Manager", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("hold for", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hold_in_progress_is_counted_down_on_screen_rather_than_left_to_guess_at()
    {
        // Somebody holding a screen for three seconds with nothing happening lets go at two and
        // concludes the frame is dead. The bar is determinate and measured against the instant
        // being rendered, so it is a report of the person's own finger rather than the animation
        // decision 70 forbids.
        var began = DateTimeOffset.UnixEpoch;
        var status = Stopped with
        {
            Touch = new TouchRetryState("/dev/input/event4", TimeSpan.FromSeconds(3), began),
        };

        var early = StageRenderer.Render(status, began + TimeSpan.FromSeconds(1), 0, 160, 40, colour: false);
        var late = StageRenderer.Render(status, began + TimeSpan.FromSeconds(2.5), 0, 160, 40, colour: false);

        Assert.Contains("keep holding — 2s", early, StringComparison.Ordinal);
        Assert.Contains("keep holding — 1s", late, StringComparison.Ordinal);

        // And only while a finger is actually down. A bar that survived the release would be the
        // animation decision 70 forbids, drawn on the one screen that rule was written for.
        Assert.DoesNotContain(
            "keep holding",
            StageRenderer.Render(Stopped, began, 0, 160, 40, colour: false),
            StringComparison.Ordinal);
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
                Retry = () => Retries++,
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

        public int Retries { get; private set; }

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
