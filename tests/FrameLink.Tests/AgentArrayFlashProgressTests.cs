using System.Diagnostics;
using FrameLink.Agent.Firmware;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Link;
using FrameLink.Agent.Stage;
using FrameLink.Agent.State;
using FrameLink.Control.Firmware;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// <b>Decision 91's blind spot, and the isolation that closes it safely.</b>
/// </summary>
/// <remarks>
/// <para>
/// The agent used to emit nothing at all between a household agreeing to a firmware write and
/// <c>dfu-util</c> returning, so for the thirty seconds to two minutes a write takes, a write in
/// progress and a frame that died mid-write were the same picture from a desk. These tests cover the
/// two halves of removing that: reading the tool's output as it arrives, and reporting it in a way
/// that <b>cannot reach the write</b>.
/// </para>
/// <para>
/// <b>The second half is the load-bearing one and it is what most of this file is about.</b> A
/// partial DFU write can leave a microphone unit unusable until somebody travels to it and recovers
/// it by hand; a partial report costs a frame of a progress bar. So every trade-off between them
/// resolves in favour of the write, and the tests below assert that in the only way that means
/// anything — by making the reporting path fail in each way it can fail (throwing, blocking for
/// ever, being cancelled) and requiring the write to complete regardless.
/// </para>
/// <para>
/// <b>The <c>dfu-util</c> output scripted here is upstream's published shape, not a capture.</b> No
/// capture of a DFU download exists anywhere in this repository, on any array, at any version —
/// <c>reference/xvf3800-upgrade-path.md</c> records that gap and nothing here closes it. What these
/// tests therefore establish is that the parser reads that shape, that it is tolerant of everything
/// around it, and — the part that survives being wrong about the shape — that output it cannot read
/// leaves the write reported as a named stage with no bar rather than as a wrong one.
/// </para>
/// </remarks>
public sealed class AgentArrayFlashProgressTests
{
    /// <summary>How long a bounded wait for an asynchronous surface is given before it fails.</summary>
    /// <remarks>
    /// Generous, because it is only ever reached when the assertion is genuinely false: the happy
    /// path completes in milliseconds, and a value tight enough to be "fast" would only make the
    /// suite flaky on a busy machine.
    /// </remarks>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>
    /// One whole <c>dfu-util</c> download, in upstream's published shape.
    /// </summary>
    /// <remarks>
    /// The bar is rewritten in place with a bare carriage return, exactly as the tool draws it, so
    /// this exercises the splitting as well as the parse. The byte counts are the pinned image's own
    /// length in the fixture, which is what a real run would carry.
    /// </remarks>
    private static IReadOnlyList<string> Transcript(long total) =>
    [
        "dfu-util 0.11\n",
        "Opening DFU capable USB device...\n",
        "Device ID 2886:0018\n",
        "Claiming USB DFU (DFU mode) Interface...\n",
        "Setting Alternate Interface #1 ...\n",
        "Determining device status...\n",
        "DFU state(2) = dfuIDLE, status(0) = No error condition is present\n",
        "Device returned transfer size 4096\n",
        "Copying data from PC to DFU device\n",
        "Download\t[                         ]   0%            0 bytes",
        "\rDownload\t[==========               ]  41%       " + (total * 41 / 100) + " bytes",
        "\rDownload\t[=========================] 100%       " + total + " bytes",
        "\nDownload done.\n",
        "DFU state(7) = dfuMANIFEST, status(0) = No error condition is present\n",
        "DFU state(2) = dfuIDLE, status(0) = No error condition is present\n",
        "Done!\n",
        "Resetting USB to switch back to Run-Time mode\n",
    ];

    // ---------------------------------------------------------------------------------------
    // Reading what the tool says
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void The_download_bar_gives_both_a_percentage_and_a_byte_count()
    {
        // Both, because the operator asked for both and they are not the same reading. The
        // percentage is what the tool printed and is what a person compares against; the byte count
        // is finer — a 933,888-byte image moves several kilobytes per printed point — and is what
        // the bar is actually filled from.
        var box = new ArrayFlashProgressBox(933_888);

        box.Read("Download\t[==========               ]  41%       382894 bytes");

        Assert.Equal(ArrayFlashStages.Downloading, box.Current.Stage);
        Assert.Equal(41, box.Current.Percent);
        Assert.Equal(382_894, box.Current.BytesWritten);
        Assert.Equal(933_888, box.Current.BytesTotal);
        Assert.Equal(382_894d / 933_888, box.Current.Fraction!.Value, 6);
    }

    [Fact]
    public void A_bar_drawn_without_a_byte_count_still_fills_from_its_percentage()
    {
        // Older builds of the tool draw the bar with no count beside it. A percentage on its own is
        // still a bar, and refusing to read one would mean a frame with a slightly older dfu-util
        // showing no progress at all rather than slightly coarser progress.
        var box = new ArrayFlashProgressBox(0);

        box.Read("Download\t[=============            ]  55%");

        Assert.Equal(ArrayFlashStages.Downloading, box.Current.Stage);
        Assert.Equal(55, box.Current.Percent);
        Assert.Null(box.Current.BytesWritten);
        Assert.Equal(0.55, box.Current.Fraction!.Value, 6);
    }

    [Fact]
    public void The_whole_of_a_write_is_read_as_named_stages_in_order()
    {
        // The point of modelling the stages at all. The bar reaches 100% and the unit then spends
        // real time committing the image to its own flash and resetting; a screen that said nothing
        // through that is one a person concludes has hung.
        var box = new ArrayFlashProgressBox(933_888);
        var seen = new List<string>();

        foreach (var chunk in Transcript(933_888))
        {
            foreach (var segment in chunk.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                box.Read(segment);

                if (seen.Count == 0 || !string.Equals(seen[^1], box.Current.Stage, StringComparison.Ordinal))
                {
                    seen.Add(box.Current.Stage);
                }
            }
        }

        Assert.Equal(
            [
                ArrayFlashStages.Preparing,
                ArrayFlashStages.Downloading,
                ArrayFlashStages.Manifesting,
                ArrayFlashStages.Settling,
                ArrayFlashStages.Resetting,
            ],
            seen);

        Assert.Equal(100, box.Current.Percent);
        Assert.Equal(933_888, box.Current.BytesWritten);
    }

    [Fact]
    public void The_dfuIDLE_printed_before_a_download_does_not_move_the_stage()
    {
        // The one line in the transcript that appears twice and means two different things. The tool
        // prints dfuIDLE while it is determining the device status, before a single byte has moved,
        // and again after the manifest. A reader that believed the first one would jump the write to
        // its last-but-two stage before it started, and the monotonic guard cannot catch it because
        // settling genuinely sorts after preparing.
        var box = new ArrayFlashProgressBox(933_888);

        box.Read("DFU state(2) = dfuIDLE, status(0) = No error condition is present");

        Assert.Equal(ArrayFlashStages.Preparing, box.Current.Stage);
    }

    [Fact]
    public void A_stage_never_goes_backwards()
    {
        var box = new ArrayFlashProgressBox(933_888);

        box.Read("Download\t[=========================] 100%       933888 bytes");
        box.Read("DFU state(7) = dfuMANIFEST, status(0) = No error condition is present");
        box.Read("Copying data from PC to DFU device");

        // A screen that stepped back to "sending the update" half way through would be saying the
        // write had restarted, which is the one thing it must never imply about an operation nobody
        // may interrupt.
        Assert.Equal(ArrayFlashStages.Manifesting, box.Current.Stage);
    }

    [Fact]
    public void Output_this_build_cannot_read_leaves_the_stage_exactly_where_it_was()
    {
        // The property that survives being wrong about upstream's shape. Nobody here has ever
        // captured a dfu-util download, so a future version that words its bar differently is a real
        // possibility — and the outcome of that must be a write reported as a named stage with no
        // bar, never a bar showing something untrue.
        var box = new ArrayFlashProgressBox(933_888);

        box.Read("Download\t[==========               ]  41%       382894 bytes");
        var before = box.Current;

        foreach (var noise in new[]
        {
            string.Empty,
            "   ",
            "[not a bar at all]",
            "Download [] % bytes",
            "warning: something entirely new",
            new string('x', 9000),
            "Download\t[==] 999999999999999999999999% 1 bytes",
        })
        {
            box.Read(noise);
        }

        box.Read(null);

        Assert.Equal(before.Stage, box.Current.Stage);
        Assert.Equal(before.Percent, box.Current.Percent);
        Assert.Equal(before.BytesWritten, box.Current.BytesWritten);
    }

    [Fact]
    public void Only_the_download_stage_offers_a_fraction()
    {
        // Five of the seven stages have no quantity behind them, and inventing one for them is how a
        // bar comes to sit at 100% for twenty seconds. Null is the answer, and every surface is
        // required to render it as an indeterminate bar with the stage named.
        var box = new ArrayFlashProgressBox(933_888);

        box.Read("Download\t[=========================] 100%       933888 bytes");
        Assert.NotNull(box.Current.Fraction);

        box.Read("DFU state(7) = dfuMANIFEST, status(0) = No error condition is present");
        Assert.Null(box.Current.Fraction);

        box.Enter(ArrayFlashStages.ReEnumerating);
        Assert.Null(box.Current.Fraction);
        Assert.Equal(ArrayFlashStages.ReEnumerating, box.Current.Stage);
    }

    // ---------------------------------------------------------------------------------------
    // The drain: a progress bar is not a sequence of lines
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void A_bar_rewritten_with_carriage_returns_arrives_as_it_is_drawn()
    {
        // The whole reason the splitter exists. dfu-util draws one bar and rewrites it in place with
        // a bare \r for every transfer block, so a reader waiting for \n would receive the entire
        // download as one enormous line at the moment the write finished — which is exactly the
        // "nothing at all until it is over" this feature exists to remove.
        var segments = new List<string>();
        var splitter = new ChildOutputSplitter(segments.Add);

        splitter.Write("Download\t[    ]   0% 0 bytes\rDownload\t[==  ]  50% 466944 bytes");
        splitter.Write("\rDownload\t[====] 100% 933888 bytes\n");
        splitter.Flush();

        Assert.Equal(3, segments.Count);
        Assert.EndsWith("100% 933888 bytes", segments[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_sink_that_throws_on_every_segment_does_not_stop_the_drain()
    {
        // An exception out of the sink would end the drain, fill the child's pipe and stall the
        // program writing an array's flash. There is no diagnostic worth that, so the sink's failure
        // is swallowed and the next segment is delivered as if nothing had happened.
        var offered = 0;
        var splitter = new ChildOutputSplitter(_ =>
        {
            offered++;
            throw new InvalidOperationException("the reporting path is broken");
        });

        splitter.Write("one\ntwo\nthree\n");
        splitter.Flush();

        Assert.Equal(3, offered);
        Assert.Equal(3, splitter.Segments);
    }

    [Fact]
    public void A_child_that_writes_without_a_separator_does_not_grow_the_buffer_without_bound()
    {
        var segments = new List<string>();
        var splitter = new ChildOutputSplitter(segments.Add);

        splitter.Write(new string('x', 20_000));
        splitter.Flush();

        Assert.True(segments.Count >= 4);
        Assert.All(segments, segment => Assert.True(segment.Length <= 4096));
    }

    // ---------------------------------------------------------------------------------------
    // The wire: one carrier, and it does not move the health classification
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void A_write_in_flight_survives_the_round_trip_through_the_self_report()
    {
        var report = ArrayFlashWire.Append(
            AgentHealth.ReportFor(LoopStateNames.Converged, "linux-arm64, endpoints resolved by boot file"),
            new ArrayFlashWireStatus
            {
                Screen = "writing",
                Stage = ArrayFlashStages.Downloading,
                Percent = 41,
                BytesWritten = 382_894,
                BytesTotal = 933_888,
                ElapsedSeconds = 12,
            });

        var read = ArrayFlashWire.Read(report);

        Assert.NotNull(read);
        Assert.Equal("writing", read.Screen);
        Assert.Equal(ArrayFlashStages.Downloading, read.Stage);
        Assert.Equal(41, read.Percent);
        Assert.Equal(382_894, read.BytesWritten);
        Assert.Equal(933_888, read.BytesTotal);
        Assert.Equal(12, read.ElapsedSeconds);

        // The report the operator already had is still whole and still first, so a console that
        // renders the raw column shows the frame's reconciliation state and the write beside it.
        Assert.StartsWith("InSync(linux-arm64, endpoints resolved by boot file)", report, StringComparison.Ordinal);
        Assert.Equal(
            "InSync(linux-arm64, endpoints resolved by boot file)",
            ArrayFlashWire.Without(report));
    }

    [Fact]
    public void A_frame_writing_firmware_still_classifies_as_whatever_its_loop_is_doing()
    {
        // A firmware write is not a rung on §2.6's ladder: nothing has drifted, the product runs,
        // and the frame is exactly what the operator declared. Putting the token inside the
        // vocabulary's parenthesis would have been the easy shape and would have made a converged
        // frame read as degraded — or worse, as unknown — for the two minutes of a write.
        foreach (var state in new[] { LoopStateNames.Converged, LoopStateNames.Reconciling, LoopStateNames.Escalated })
        {
            var plain = AgentHealth.ReportFor(state, "linux-arm64");
            var withWrite = ArrayFlashWire.Append(
                plain,
                new ArrayFlashWireStatus { Screen = "writing", Stage = ArrayFlashStages.Manifesting });

            Assert.Equal(AgentHealth.Classify(plain), AgentHealth.Classify(withWrite));
        }
    }

    [Fact]
    public void A_self_report_with_no_token_in_it_is_returned_exactly_as_it_arrived()
    {
        Assert.Null(ArrayFlashWire.Read(null));
        Assert.Null(ArrayFlashWire.Read("InSync(linux-arm64)"));
        Assert.Equal("InSync(linux-arm64)", ArrayFlashWire.Without("InSync(linux-arm64)"));
        Assert.Null(ArrayFlashWire.Without(null));
        Assert.Equal("InSync(linux-arm64)", ArrayFlashWire.Append("InSync(linux-arm64)", null));
    }

    [Fact]
    public void A_token_from_a_newer_frame_keeps_every_field_this_build_does_understand()
    {
        // Frozen once shipped means keys are added, never renamed — so the failure mode being
        // asserted here is the one that will actually happen: a frame running a newer agent sending
        // a field this build has never heard of. Skipping it and keeping the rest is what makes that
        // a partial reading rather than no reading.
        var read = ArrayFlashWire.Read(
            "InSync(x) [array-flash screen=writing stage=downloading pct=41 sectors=12 bytes=1/2 t=9 odd]");

        Assert.NotNull(read);
        Assert.Equal(ArrayFlashStages.Downloading, read.Stage);
        Assert.Equal(41, read.Percent);
        Assert.Equal(1, read.BytesWritten);
        Assert.Equal(2, read.BytesTotal);
        Assert.Equal(9, read.ElapsedSeconds);
    }

    [Fact]
    public void A_malformed_token_reads_as_nothing_rather_than_as_a_broken_write()
    {
        Assert.Null(ArrayFlashWire.Read("InSync(x) [array-flash"));
        Assert.Null(ArrayFlashWire.Read("InSync(x) [array-flash stage=downloading]"));
        Assert.Null(ArrayFlashWire.Read("InSync(x) [array-flash screen= pct=nonsense]"));
    }

    [Fact]
    public void The_reporter_folds_the_frames_firmware_screen_into_what_it_would_say()
    {
        using var uplink = new AgentUplink();
        var hub = new AgentStatusHub(AgentStatusFactory.Green());
        using var reporter = new AgentStatusReporter(hub, uplink, NullLog.Instance, "AAAA-BBBB-CCCC-DDDD", "linux-arm64");

        Assert.Null(ArrayFlashWire.Read(reporter.Current));

        hub.Publish(status => status with
        {
            ArrayFlash = ArrayFlashVoice.Writing(new ArrayFlashProgress
            {
                Stage = ArrayFlashStages.Downloading,
                Percent = 41,
                BytesWritten = 382_894,
                BytesTotal = 933_888,
                Elapsed = TimeSpan.FromSeconds(12),
            }),
        });

        var live = ArrayFlashWire.Read(reporter.Current);

        Assert.NotNull(live);
        Assert.Equal("writing", live.Screen);
        Assert.Equal(41, live.Percent);
        Assert.Equal(12, live.ElapsedSeconds);

        // Every firmware screen, not only a write. A frame asking its household to agree is a state
        // an operator was blind to between one event and the next as well.
        hub.Publish(status => status with { ArrayFlash = ArrayFlashVoice.Asking(true, null) });
        Assert.Equal("asking", ArrayFlashWire.Read(reporter.Current)!.Screen);

        hub.Publish(status => status with { ArrayFlash = null });
        Assert.Null(ArrayFlashWire.Read(reporter.Current));
    }

    // ---------------------------------------------------------------------------------------
    // What the frame's own screen says
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Every_stage_of_a_write_has_a_sentence_a_family_member_can_read()
    {
        // The register rule, asserted rather than trusted. The only mitigation that exists for mains
        // loss during a DFU write is a person in the room who understands what they have been told,
        // and dfuMANIFEST is not a thing anybody outside this repository has ever heard of.
        foreach (var stage in new[]
        {
            ArrayFlashStages.Preparing,
            ArrayFlashStages.Downloading,
            ArrayFlashStages.Manifesting,
            ArrayFlashStages.Settling,
            ArrayFlashStages.Resetting,
            ArrayFlashStages.ReEnumerating,
            ArrayFlashStages.Verifying,
        })
        {
            var line = ArrayFlashVoice.StageLine(new ArrayFlashProgress { Stage = stage, BytesTotal = 933_888 });

            Assert.NotEmpty(line);
            Assert.DoesNotContain("dfu", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("enumerat", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("firmware", line, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_writing_screen_leads_with_the_stage_and_keeps_its_warning()
    {
        var screen = ArrayFlashVoice.Writing(new ArrayFlashProgress
        {
            Stage = ArrayFlashStages.Downloading,
            Percent = 41,
            BytesWritten = 382_894,
            BytesTotal = 933_888,
        });

        Assert.Equal(ArrayFlashPhase.Writing, screen.Phase);
        Assert.Contains("41%", screen.Lines[0], StringComparison.Ordinal);

        // The warning is the reason the screen exists and progress does not displace it.
        Assert.Contains(screen.Lines, line => line.Contains("break the microphone", StringComparison.Ordinal));

        // Still no affordance. A write in progress is the one screen in this product with nothing a
        // person may usefully do, and a button would invite the interruption it exists to prevent.
        Assert.Null(screen.Affordance);
    }

    [Fact]
    public void A_screen_whose_only_change_is_the_progress_is_still_a_new_screen()
    {
        // The signature is what decides whether anything repaints, and ArrayFlashPrompt's own record
        // equality cannot see into a list. A signature blind to the progress would have meant a bar
        // that never moved after the first frame.
        var first = ArrayFlashVoice.Writing(new ArrayFlashProgress
        {
            Stage = ArrayFlashStages.Downloading,
            Percent = 41,
            BytesTotal = 933_888,
        });

        var second = first with
        {
            Progress = first.Progress! with { Percent = 42 },
        };

        Assert.NotEqual(first.Signature, second.Signature);
    }

    [Fact]
    public void The_console_draws_a_determinate_bar_only_while_bytes_are_moving()
    {
        var downloading = Console(new ArrayFlashProgress
        {
            Stage = ArrayFlashStages.Downloading,
            Percent = 41,
            BytesWritten = 382_894,
            BytesTotal = 933_888,
        });

        Assert.Contains("Updating", downloading, StringComparison.Ordinal);
        Assert.Contains('█', downloading);
        Assert.Contains("41%", downloading, StringComparison.Ordinal);
        Assert.Contains("382,894 of 933,888 bytes", downloading, StringComparison.Ordinal);

        // The manifest has nothing to measure. §2.7 item 6 already has the answer for that — a bar
        // whose highlight travels — and using a determinate bar frozen at 100% instead would say the
        // write had finished when it had not.
        var manifesting = Console(new ArrayFlashProgress
        {
            Stage = ArrayFlashStages.Manifesting,
            Percent = 100,
            BytesWritten = 933_888,
            BytesTotal = 933_888,
            Elapsed = TimeSpan.FromSeconds(14),
        });

        Assert.DoesNotContain("100%", manifesting, StringComparison.Ordinal);
        Assert.Contains("14s so far", manifesting, StringComparison.Ordinal);

        static string Console(ArrayFlashProgress progress)
        {
            var status = AgentStatusFactory.Green() with { ArrayFlash = ArrayFlashVoice.Writing(progress) };

            return StageRenderer.Render(
                status,
                new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero),
                tick: 3,
                columns: 120,
                rows: 40,
                colour: false);
        }
    }

    // ---------------------------------------------------------------------------------------
    // The isolation, proved by breaking the reporting path in each way it can break
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_screen_that_blocks_for_the_whole_write_does_not_hold_the_write_up()
    {
        // <b>The load-bearing test of the whole feature.</b> AgentStatusHub calls its subscribers
        // synchronously on the publisher's thread, and on a frame one of those subscribers writes a
        // frame to /dev/tty8 while another sends one to the browser — so a screen that will not take
        // a repaint is a thread that never returns. Before this work the write's own task published
        // that screen; it now takes an interlocked epoch and nothing else, and every frame is drawn
        // by a task it never waits for.
        //
        // <b>The screen is wedged before the write starts, and by a publish of the test's own.</b>
        // That ordering is the whole point and it is what this test used to get wrong. It used to
        // start the write first and then assert that *something* had blocked, which left the
        // reporting path's first publish racing the write: on a busy machine the pump's task is
        // scheduled late, the write runs to completion without a single frame being drawn, and the
        // only thread that ever enters the subscriber is the write's own — inside
        // ArrayFlashApproval.Finished, after dfu-util has returned and the unit has come back. Both
        // of the old assertions were satisfied by that run: something had blocked, and the tick had
        // not completed because the writer itself was the thing stuck in the screen. A green from
        // that run said nothing about isolation, because no screen was ever asked to paint while
        // the write was in flight.
        //
        // Wedging first removes the race in the only way that keeps the meaning: from the line
        // below until the test lets go, every publish anybody makes — the pump's, the write's, a
        // touchscreen watch's — enters a subscriber that does not return. The write then has to get
        // from nothing to "the unit is back on the pinned firmware" through that, and the signals
        // below are what say it did.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise();
        fixture.Processes.Output.Clear();
        fixture.Processes.Output.AddRange(Transcript(fixture.Pin.Target.SizeBytes));

        using var gate = new ManualResetEventSlim(false);
        using var wedged = new ManualResetEventSlim(false);
        using var wrote = new ManualResetEventSlim(false);

        using var subscription = fixture.Hub.Subscribe(_ =>
        {
            wedged.Set();
            gate.Wait(Patience);
        });

        var token = TestContext.Current.CancellationToken;

        // A repaint that never returns, of the test's own making, so that the screen is provably
        // wedged before the write has done anything at all. It has to be on a task of its own
        // because the hub calls subscribers on the publisher's thread — publishing from here would
        // wedge the test rather than the screen.
        var wedge = Task.Run(() => fixture.Hub.Publish(status => status), token);
        Assert.True(wedged.Wait(Patience, token), "the screen should have wedged before the write began");

        // Read on the writing thread, the moment dfu-util has returned and the unit has
        // re-enumerated, rather than polled from here. Polling is what made the old assertions
        // snapshots of whatever the scheduler had got round to; this is the write itself saying
        // where it got to, and it can only say it once.
        string? cameBackOn = null;
        fixture.AfterWrite = () =>
        {
            cameBackOn = fixture.Running();
            wrote.Set();
        };

        var flash = fixture.Flash();
        var tick = Task.Run(() => flash.TickAsync(token), token);

        Assert.True(
            wrote.Wait(Patience, token),
            "the write should have run to completion with the screen still wedged");

        // The screen was still wedged when that happened: nothing releases the gate but the line
        // below, and the write reported itself finished before this test reached it. So the whole of
        // the write — claiming the screen, dfu-util, the unit coming back — happened while a
        // subscriber the hub had called had not returned.
        Assert.False(gate.IsSet, "nothing had released the screen while the write was running");
        Assert.Equal(fixture.Pin.Target.Version, cameBackOn);

        gate.Set();
        await wedge;

        var outcome = await tick;

        Assert.True(outcome.Flashed);
        Assert.True(outcome.Succeeded);
        Assert.Equal(fixture.Authorisation, fixture.Consumed);

        // Read after the write rather than during it, deliberately: Commands is an ordinary List and
        // enumerating it from here while the writing thread appends to it is a data race of the
        // test's own making, which is what the old polled version of this assertion was.
        Assert.Contains(fixture.Processes.Commands, Wrote);
    }

    [Fact]
    public async Task A_screen_that_throws_on_every_frame_changes_nothing_about_the_write()
    {
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise();
        fixture.Processes.Output.Clear();
        fixture.Processes.Output.AddRange(Transcript(fixture.Pin.Target.SizeBytes));

        var thrown = 0;

        using var subscription = fixture.Hub.Subscribe(_ =>
        {
            Interlocked.Increment(ref thrown);
            throw new IOException("/dev/tty8: input/output error");
        });

        var outcome = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        // The exception is swallowed where it happens and travels nowhere: the write ran, the array
        // came back, the authorisation is spent and the trail is complete. A reporting failure is
        // not a flash failure and must never be recorded as one.
        Assert.True(outcome.Flashed);
        Assert.True(outcome.Succeeded);
        Assert.True(Volatile.Read(ref thrown) > 0, "the reporting path should have been exercised");
        Assert.Equal(fixture.Authorisation, fixture.Consumed);
        Assert.Contains(fixture.Telemetry.Events, moment => moment.Summary.StartsWith("Wrote ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reporting_never_spends_the_writes_own_patience()
    {
        // The subtlest way the reporting path could have reached through and changed an outcome. The
        // re-enumeration deadline is measured against IAgentClock, and the suite's clock advances
        // whenever anything waits on it — so a reporting loop that beat on the injected clock would
        // race the write's ninety seconds away and report an array that never came back.
        //
        // The pump therefore has a Stopwatch and a plain timer of its own and touches neither the
        // agent's clock nor its cancellation token, which is what this asserts.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise();
        fixture.Processes.Output.Clear();
        fixture.Processes.Output.AddRange(Transcript(fixture.Pin.Target.SizeBytes));

        var started = fixture.Clock.UtcNow;

        var outcome = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.Empty(fixture.Clock.Delays);
        Assert.Equal(started, fixture.Clock.UtcNow);
    }

    [Fact]
    public async Task A_progress_frame_that_arrives_late_cannot_paint_over_the_outcome()
    {
        // The pump is deliberately never waited for, so a frame it had already composed can arrive
        // after the write has finished. Without the epoch that frame would draw "please do not
        // unplug this frame" over a screen that had just told somebody they may.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();

        var approval = fixture.Approval;
        var epoch = approval.BeginWriting();

        approval.Writing(epoch, new ArrayFlashProgress { Stage = ArrayFlashStages.Downloading, Percent = 41 });
        Assert.Equal(ArrayFlashPhase.Writing, fixture.Screen?.Phase);

        approval.Finished(succeeded: true);
        Assert.Equal(ArrayFlashPhase.Succeeded, fixture.Screen?.Phase);

        approval.Writing(epoch, new ArrayFlashProgress { Stage = ArrayFlashStages.Downloading, Percent = 42 });

        Assert.Equal(ArrayFlashPhase.Succeeded, fixture.Screen?.Phase);
    }

    [Fact]
    public void The_pump_stops_without_waiting_for_a_publish_that_has_hung()
    {
        // Disposal is the other half of "the writer never waits on the reporting path". A publish
        // that has hung is holding the pump's own thread; a Dispose that joined it would put the
        // writing thread behind exactly the thing this design keeps it away from. The cost is one
        // abandoned task for the life of the process, which is the right way round: a partial write
        // can destroy hardware somebody has to travel to, and a leaked task cannot.
        var hub = new AgentStatusHub(AgentStatusFactory.Green());
        var approval = new ArrayFlashApproval(hub, new ManualClock(), new RecordingLog());
        var box = new ArrayFlashProgressBox(933_888);

        using var gate = new ManualResetEventSlim(false);
        using var blocked = new ManualResetEventSlim(false);

        using var subscription = hub.Subscribe(_ =>
        {
            blocked.Set();
            gate.Wait(Patience);
        });

        var pump = ArrayFlashProgressPump.Start(approval, box, new RecordingLog(), TimeSpan.FromMilliseconds(10));

        Assert.True(
            blocked.Wait(Patience, TestContext.Current.CancellationToken),
            "the pump should have tried to publish");

        var stopwatch = Stopwatch.StartNew();
        pump.Dispose();
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Dispose waited {stopwatch.Elapsed} on a publish that had not returned.");

        gate.Set();
    }

    [Fact]
    public void The_pump_publishes_only_what_has_changed()
    {
        // Dropping under load is correct behaviour rather than a bug, and this is the cheap half of
        // it: dfu-util rewrites its bar once per 4 KB transfer block, which is 228 redraws of a
        // 933 KB image. Publishing on each would repaint the console, re-render the page and put a
        // message on the wire eight times a second for the whole write.
        //
        // <b>The elapsed second is held still, and it has to be.</b> A frame is worth repainting
        // when the stage, the percentage or the elapsed whole second has moved — the second is in
        // there on purpose, because it is the only thing that moves through the manifest. So "a
        // finer byte count does not cause a repaint" is only true *within one second*, and a test
        // that let the pump's own stopwatch run was asserting that four statements execute inside
        // the same second. On a loaded machine they do not: this test failed 4 times in 30 runs with
        // the thread pool starved, every time on the assertion below, because a second had turned
        // over rather than because anything about the reading had changed.
        var hub = new AgentStatusHub(AgentStatusFactory.Green());
        var approval = new ArrayFlashApproval(hub, new ManualClock(), new RecordingLog());
        var box = new ArrayFlashProgressBox(933_888);
        var elapsed = TimeSpan.FromSeconds(7);
        var pump = ArrayFlashProgressPump.Start(
            approval,
            box,
            new RecordingLog(),
            TimeSpan.FromMinutes(5),
            elapsed: () => elapsed);

        try
        {
            Assert.True(pump.PublishOnce() || pump.Published > 0);

            box.Read("Download\t[==========               ]  41%       382894 bytes");
            Assert.True(pump.PublishOnce());
            Assert.Equal(41, hub.Current.ArrayFlash?.Progress?.Percent);

            // A finer byte count at the same percentage is a real reading and is kept — it simply
            // does not by itself cause a repaint.
            box.Read("Download\t[==========               ]  41%       386990 bytes");
            Assert.False(pump.PublishOnce());
            Assert.Equal(382_894, hub.Current.ArrayFlash?.Progress?.BytesWritten);
            Assert.Equal(386_990, box.Current.BytesWritten);

            box.Read("Download\t[===========              ]  42%       392086 bytes");
            Assert.True(pump.PublishOnce());
            Assert.Equal(392_086, hub.Current.ArrayFlash?.Progress?.BytesWritten);

            // And the other half of the same rule, which the wall-clock version of this test could
            // never state: with nothing at all arriving, the second hand moving on is by itself
            // worth a frame. That is what keeps a still bar reading as a wait rather than a hang
            // through the manifest, and the finer byte count above rides along on it.
            elapsed = TimeSpan.FromSeconds(8);
            Assert.True(pump.PublishOnce());
            Assert.Equal(8, hub.Current.ArrayFlash?.Progress?.Elapsed.TotalSeconds);
            Assert.Equal(392_086, hub.Current.ArrayFlash?.Progress?.BytesWritten);
        }
        finally
        {
            pump.Dispose();
        }
    }

    [Fact]
    public async Task A_write_reports_its_stages_while_it_is_running()
    {
        // The feature itself, end to end and through the production wiring: the fixture's dfu-util
        // hands its output to the same splitter a real pipe drain uses, the box parses it, the pump
        // publishes it, and the frame's screen and the self-report both carry it — all while the
        // tool has not yet exited.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise();
        fixture.Processes.Output.Clear();
        fixture.Processes.Output.AddRange(Transcript(fixture.Pin.Target.SizeBytes));

        using var uplink = new AgentUplink();
        using var reporter = new AgentStatusReporter(
            fixture.Hub,
            uplink,
            NullLog.Instance,
            "TEST-DEVICE",
            "linux-arm64");

        string? duringWrite = null;

        // <b>Waited for by the whole of what is asserted below, not by a part of it.</b> The box
        // reaches its last reading synchronously — the fixture hands the transcript over before it
        // lets the tool return — but the *screen* is repainted by the pump, one frame per change,
        // and there are four changes after the bar first reads 100%. Waiting on the percentage alone
        // therefore accepted whichever of those five frames had landed, and on a loaded machine it
        // caught an earlier one: this failed with "manifesting" where "resetting" was expected,
        // once in 25 runs with the thread pool starved. The stage is part of the condition now,
        // which is deterministic rather than tolerant — the box's last reading is fixed, and every
        // change wakes the pump, so the frame being waited for is one the pump is bound to draw.
        fixture.Processes.Draining = async () =>
        {
            await WaitForAsync(
                () => fixture.Screen?.Progress is { Percent: 100, Stage: ArrayFlashStages.Resetting },
                "the frame's screen to show the write at the tool's last reading");

            duringWrite = reporter.Current;
        };

        var outcome = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);

        var live = ArrayFlashWire.Read(duringWrite);
        Assert.NotNull(live);
        Assert.Equal("writing", live.Screen);
        Assert.Equal(100, live.Percent);

        // The tool had printed its reset line but had not yet exited, and the frame was already
        // saying so — which is the whole of what used to be missing. Deterministic here because the
        // fixture delivers the whole transcript before it lets the tool return.
        Assert.Equal(ArrayFlashStages.Resetting, live.Stage);
        Assert.Equal(fixture.Pin.Target.SizeBytes, live.BytesWritten);
        Assert.Equal(fixture.Pin.Target.SizeBytes, live.BytesTotal);

        // And the trail keeps the tool's last reading, which is what tells an operator how far a
        // write that did *not* work actually got.
        Assert.Contains(
            fixture.Telemetry.Events,
            moment => moment.Summary.Contains("bytes sent, at the", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------
    // What the Fleet Manager makes of it
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void A_frame_that_is_writing_now_reads_as_writing_rather_than_as_its_last_event()
    {
        var live = ArrayFlashWire.Read(
            "InSync(linux-arm64) [array-flash screen=writing stage=downloading pct=41 bytes=382894/933888 t=12]");

        var reading = ArrayFlashReading.From([], authorisation: null, live);

        Assert.Equal(ArrayFlashPhases.Writing, reading.Phase);
        Assert.Equal(41, reading.Progress?.Percent);
        Assert.Equal(382_894d / 933_888, reading.Progress!.Fraction!.Value, 6);
        Assert.Contains("sending the image to the unit", reading.Detail, StringComparison.Ordinal);
        Assert.Contains("382,894 of 933,888 bytes", reading.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stage_the_server_has_never_heard_of_is_shown_as_the_frame_sent_it()
    {
        var live = ArrayFlashWire.Read("InSync(x) [array-flash screen=writing stage=polishing]");
        var reading = ArrayFlashReading.From([], authorisation: null, live);

        Assert.Equal(ArrayFlashPhases.Writing, reading.Phase);
        Assert.Contains("polishing", reading.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_live_screen_that_is_not_a_write_leaves_the_event_trail_in_charge()
    {
        // Everything else the frame can be doing already has an event carrying the frame's own
        // sentence, and a screen name is a poorer version of the same fact. The one state the trail
        // could not describe was a write in flight, and that is the only one taken from the socket.
        var refused = new DeviceEvent
        {
            DeviceId = "TEST-DEVICE",
            Kind = DeviceEventKinds.ArrayFlash,
            OccurredUtc = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero),
            Summary = "A firmware write is authorised on this frame and is waiting for somebody standing at it.",
            Delta = ArrayFlashReading.RefusalDeltaPrefix + "AwaitingLocalApproval'",
        };

        var reading = ArrayFlashReading.From(
            [refused],
            authorisation: null,
            ArrayFlashWire.Read("InSync(x) [array-flash screen=asking]"));

        Assert.Equal(ArrayFlashPhases.AwaitingHousehold, reading.Phase);
        Assert.Equal(refused.Summary, reading.Detail);
        Assert.Null(reading.Progress);
    }

    private static bool Wrote(string command) =>
        command.StartsWith(ArrayFirmwareFlash.DfuUtil + " ", StringComparison.Ordinal);

    /// <summary>Waits for <paramref name="condition"/>, failing with what it was waiting for.</summary>
    /// <remarks>
    /// Polled rather than signalled because what is being waited on is a task nothing may wait on by
    /// design — the whole point of the pump is that no caller holds a handle it can join.
    /// </remarks>
    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = Stopwatch.StartNew();

        while (deadline.Elapsed < Patience)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Timed out after {Patience} waiting for {what}.");
    }
}
