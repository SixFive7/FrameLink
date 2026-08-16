using FrameLink.Agent;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Stage;
using FrameLink.Agent.State;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// The console stage — version2.md §2.7.
/// </summary>
/// <remarks>
/// §2.7 is specific about what the repair screen renders: what was detected, why it matters, what
/// is being done, the attempt number when retrying, and the backoff state including remaining wait
/// "so a pause never looks like a hang". Each of those is a test here. The layout assertions —
/// every line exactly the terminal's width, the box closed on all four corners — exist because
/// §7.4 holds this surface to the same aesthetic bar as everything else, and a border that wanders
/// by one column is visible from across a room.
/// </remarks>
public sealed class AgentConsoleStageTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(80, 24)]
    [InlineData(160, 50)]
    [InlineData(40, 12)]
    public void Every_line_is_exactly_as_wide_as_the_terminal(int columns, int rows)
    {
        var lines = Lines(Pending(), columns, rows);

        Assert.Equal(rows, lines.Count);
        Assert.All(lines, line => Assert.Equal(columns, line.Length));
    }

    [Fact]
    public void A_terminal_smaller_than_the_layout_still_renders_a_closed_box()
    {
        var lines = Lines(Pending(), columns: 10, rows: 4);

        Assert.All(lines, line => Assert.Equal(lines[0].Length, line.Length));
        Assert.StartsWith("╭", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("╯", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void The_box_is_closed_on_all_four_corners()
    {
        var lines = Lines(Pending(), 80, 24);

        Assert.StartsWith("╭", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("╮", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("╰", lines[^1], StringComparison.Ordinal);
        Assert.EndsWith("╯", lines[^1], StringComparison.Ordinal);
        Assert.All(lines[1..^1], line => Assert.StartsWith("│", line, StringComparison.Ordinal));
        Assert.All(lines[1..^1], line => Assert.EndsWith("│", line, StringComparison.Ordinal));
    }

    [Fact]
    public void A_pending_frame_shows_what_was_detected_why_it_matters_and_what_is_being_done()
    {
        var status = Pending() with
        {
            Narration = new Narration
            {
                Detected = "The speaker volume setting is not what it should be",
                WhyItMatters = "Calls would be too quiet to hear",
                Action = "amixer -c 0 sset PCM 100%",
                ActionGloss = "Turning the speaker back up",
            },
        };

        var text = Plain(status, 80, 24);

        Assert.Contains("The speaker volume setting is not what it should be", text, StringComparison.Ordinal);
        Assert.Contains("Calls would be too quiet to hear", text, StringComparison.Ordinal);
        Assert.Contains("amixer -c 0 sset PCM 100%", text, StringComparison.Ordinal);
        Assert.Contains("Turning the speaker back up", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_headline_is_not_repeated_back_as_a_field()
    {
        var text = Plain(Pending(), 80, 24);
        var headline = Pending().Condition.Headline;

        Assert.Equal(1, CountOccurrences(text, headline[..30]));
    }

    [Theory]
    [InlineData("0.1.0+a273b319db94d7927726dfd201a62924b0560d14", "0.1.0+a273b31")]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("0.1.0+a273b31", "0.1.0+a273b31")]
    public void The_title_shortens_a_commit_hash_rather_than_slicing_it(string version, string expected)
    {
        Assert.Equal(expected, StageRenderer.ShortVersion(version));
        Assert.Contains(expected, Plain(Pending() with { AgentVersion = version }, 96, 24), StringComparison.Ordinal);
    }

    [Fact]
    public void A_pending_frame_shows_the_fingerprint_and_serial_an_operator_matches_on()
    {
        // §3.3: a pending frame displays its short fingerprint and hardware serial on screen, so
        // the operator can tell which row is which frame on the bench.
        var status = Pending() with { DeviceId = "A1B2-C3D4-E5F6-G7H8", HardwareSerial = "10000000abcd1234" };

        var text = Plain(status, 80, 24);

        Assert.Contains("A1B2-C3D4-E5F6-G7H8", text, StringComparison.Ordinal);
        Assert.Contains("10000000abcd1234", text, StringComparison.Ordinal);
        Assert.Contains("adopt it in your Fleet Manager", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_retrying_frame_shows_the_attempt_number_and_the_remaining_wait()
    {
        var status = Silent() with
        {
            Attempt = 3,
            BackoffTotal = TimeSpan.FromSeconds(8),
            BackoffEndsAt = Now.AddSeconds(5),
        };

        var text = Plain(status, 80, 24);

        Assert.Contains("Attempt 3", text, StringComparison.Ordinal);
        Assert.Contains("next try in 5s", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_countdown_actually_counts_down()
    {
        var status = Silent() with
        {
            Attempt = 2,
            BackoffTotal = TimeSpan.FromSeconds(10),
            BackoffEndsAt = Now.AddSeconds(10),
        };

        Assert.Contains("next try in 10s", Plain(status, 80, 24, Now), StringComparison.Ordinal);
        Assert.Contains("next try in 4s", Plain(status, 80, 24, Now.AddSeconds(6)), StringComparison.Ordinal);
        Assert.Contains("next try in 0s", Plain(status, 80, 24, Now.AddSeconds(30)), StringComparison.Ordinal);
    }

    [Fact]
    public void The_progress_bar_fills_as_the_wait_elapses()
    {
        var status = Silent() with
        {
            Attempt = 1,
            BackoffTotal = TimeSpan.FromSeconds(10),
            BackoffEndsAt = Now.AddSeconds(10),
        };

        var atStart = Plain(status, 80, 24, Now);
        var nearEnd = Plain(status, 80, 24, Now.AddSeconds(9));

        Assert.True(atStart.Count(c => c == '█') < nearEnd.Count(c => c == '█'));
    }

    [Fact]
    public void An_update_in_flight_shows_its_own_progress()
    {
        var status = Pending() with { UpdateProgress = 0.5 };

        var text = Plain(status, 80, 24);

        Assert.Contains("Updating", text, StringComparison.Ordinal);
        Assert.Contains("50%", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Something_is_always_moving_even_when_there_is_no_progress_to_report()
    {
        // §2.7 item 6: a pause must never look like a hang. Without an animated element, a frame
        // waiting patiently and a frame that has died are the same picture.
        var status = Pending();

        var first = Plain(status, 80, 24, Now, tick: 0);
        var second = Plain(status, 80, 24, Now, tick: 1);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void The_spinner_cycles_rather_than_running_off_its_frames()
    {
        var glyphs = Enumerable.Range(-20, 60).Select(StageRenderer.Spinner).Distinct(StringComparer.Ordinal).ToList();

        Assert.Equal(10, glyphs.Count);
    }

    [Fact]
    public void A_reconciled_resource_appears_on_screen_with_its_verdict()
    {
        var status = Green() with
        {
            Resources =
            [
                new ResourceStatus
                {
                    Name = "device-name",
                    Kind = ResourceStatusKind.Degraded,
                    Delta = "expected 'Hallway', observed ''",
                    Attempts = 1,
                },
            ],
        };

        var text = Plain(status, 100, 24);

        Assert.Contains("device-name", text, StringComparison.Ordinal);

        // In words, not in the status enum's member name (decision 70). "Degraded" and
        // "Progressing" read as the same sort of thing to somebody who has not read §2.3, and the
        // difference between them is whether anything is still going to happen.
        Assert.Contains("gave up after 1 try", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_accent_colour_follows_the_rung_and_marks_a_refusal_in_red()
    {
        var accents = new[]
        {
            HandshakeStatus.Ok,
            HandshakeStatus.Pending,
            HandshakeStatus.Blocked,
            HandshakeStatus.NotConfigured,
            HandshakeStatus.VersionMismatch,
        }
        .Select(status => StagePalette.For(DeviceStateLadder.FromHandshake(
            new HandshakeResult { Status = status, ProtocolVersion = ProtocolConstants.Version })))
        .ToList();

        Assert.Equal(StagePalette.Green, accents[0]);
        Assert.Equal(StagePalette.Blue, accents[1]);
        Assert.Equal(StagePalette.Red, accents[2]);
        Assert.Equal(StagePalette.Blue, accents[3]);
        Assert.Equal(StagePalette.Amber, accents[4]);
    }

    [Fact]
    public void Colour_is_emitted_when_the_terminal_takes_it_and_never_when_it_does_not()
    {
        var coloured = StageRenderer.Render(Pending(), Now, 0, 80, 24, colour: true);
        var plain = StageRenderer.Render(Pending(), Now, 0, 80, 24, colour: false);

        Assert.Contains("\e[38;5;", coloured, StringComparison.Ordinal);
        Assert.DoesNotContain("\e[38;5;", plain, StringComparison.Ordinal);

        // The layout is identical either way; colour is decoration, not structure.
        Assert.Equal(AnsiText.Strip(coloured), AnsiText.Strip(plain));
    }

    [Fact]
    public void The_frame_repaints_from_the_top_rather_than_scrolling()
    {
        // §2.7: "a designed terminal interface ... not log spew". A scrolling surface has a
        // history, not a layout.
        var frame = StageRenderer.Render(Pending(), Now, 0, 80, 24, colour: true);

        Assert.Contains(Ansi.Home, frame, StringComparison.Ordinal);
        Assert.Contains(Ansi.HideCursor, frame, StringComparison.Ordinal);
        Assert.EndsWith(Ansi.ClearToEnd, frame, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_narration_wraps_instead_of_bursting_the_box()
    {
        var status = Pending() with
        {
            Narration = new Narration
            {
                Detected = new string('x', 400),
                WhyItMatters = "because",
            },
        };

        var lines = Lines(status, 80, 24);

        Assert.All(lines, line => Assert.Equal(80, line.Length));
    }

    [Fact]
    public void The_stage_paints_on_every_tick_and_again_whenever_the_status_changes()
    {
        var terminal = new MemoryTerminal();
        var hub = new AgentStatusHub(AgentStatusFactory.Starting());
        var clock = new ManualClock();
        using var stage = new ConsoleStage(terminal, hub, clock) { TickInterval = TimeSpan.FromMilliseconds(10) };

        stage.Paint();
        var afterFirstPaint = terminal.Frames.Count;
        hub.Publish(status => status with { Attempt = 4 });

        Assert.Equal(afterFirstPaint + 1, terminal.Frames.Count);
        Assert.Contains("Attempt 4", AnsiText.Strip(terminal.Frames[^1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_stage_stops_painting_and_gives_the_cursor_back_when_it_is_disposed()
    {
        var terminal = new MemoryTerminal();
        var hub = new AgentStatusHub(AgentStatusFactory.Starting());
        var clock = new ManualClock();
        using var stop = new CancellationTokenSource();
        var stage = new ConsoleStage(terminal, hub, clock) { TickInterval = TimeSpan.FromMilliseconds(1) };
        clock.OnDelay = _ =>
        {
            if (stage.PaintedFrames > 5)
            {
                stop.Cancel();
            }
        };

        await stage.RunAsync(stop.Token);
        stage.Dispose();

        Assert.True(stage.PaintedFrames > 5);
        Assert.Contains(Ansi.ShowCursor, terminal.Frames[^1], StringComparison.Ordinal);
        Assert.True(terminal.IsDisposed);

        // Disposing releases the hub subscription, so a later publish paints nothing.
        var painted = terminal.Frames.Count;
        hub.Publish(status => status with { Attempt = 99 });
        Assert.Equal(painted, terminal.Frames.Count);
        Assert.Equal(0, hub.SubscriberCount);
    }

    private static AgentStatus Pending() => new()
    {
        Condition = DeviceStateLadder.FromHandshake(new HandshakeResult
        {
            Status = HandshakeStatus.Pending,
            ProtocolVersion = ProtocolConstants.Version,
        }),
        DeviceId = "A1B2-C3D4-E5F6-G7H8",
        HardwareSerial = "10000000abcd1234",
        AgentVersion = "0.1.0",
        Endpoints = [new Uri("https://framelink.example.org/")],
    };

    private static AgentStatus Green() => Pending() with
    {
        Condition = DeviceStateLadder.FromHandshake(new HandshakeResult
        {
            Status = HandshakeStatus.Ok,
            ProtocolVersion = ProtocolConstants.Version,
        }),
    };

    private static AgentStatus Silent() => Pending() with
    {
        Condition = DeviceStateLadder.NoContact(null, "connection refused"),
    };

    private static string Plain(AgentStatus status, int columns, int rows, DateTimeOffset? now = null, int tick = 0) =>
        AnsiText.Strip(StageRenderer.Render(status, now ?? Now, tick, columns, rows, colour: false));

    private static List<string> Lines(AgentStatus status, int columns, int rows) =>
        [.. Plain(status, columns, rows).Split('\n')];

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}

/// <summary>
/// A console that stops taking bytes — the crash the mule caught on 2026-08-15.
/// </summary>
/// <remarks>
/// <para>
/// The journal, verbatim: <c>Unhandled exception. System.IO.IOException: Input/output error :
/// '/dev/tty1'</c>, thrown from a buffered flush inside <c>TtyTerminal.Dispose()</c>, out through
/// the <c>using</c> in <c>AgentHost.RunAsync</c>, and recorded by systemd as
/// <c>code=killed, status=6/ABRT</c>. The frame had no framebuffer, no connected DRM output and
/// no backlight — which is what <i>every</i> frame looks like from first boot until the panel
/// overlay is applied, so this is the crash path of the exact condition the console stage exists
/// to narrate through.
/// </para>
/// <para>
/// The assertions are on the host's exit code rather than on "no exception was thrown", because
/// the outcome that mattered was a process. With <c>Restart=always</c> and a restart limiter that
/// is now honoured, a repeated abort here leaves the unit <c>failed</c> and stopped: a frame that
/// can reach neither its Fleet Manager nor a screen, silently.
/// </para>
/// </remarks>
public sealed class AgentConsoleFailureTests
{
    private const string Eio = "Input/output error : '/dev/tty1'";

    [Fact]
    public async Task A_console_that_refuses_every_write_still_lets_the_host_finish()
    {
        var terminal = new FailingTerminal { FailWrites = true };
        var log = new RecordingLog();

        var exit = await HostShapedRunAsync(terminal, log);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(1, terminal.DisposeCount);
    }

    [Fact]
    public async Task A_console_that_throws_while_being_closed_still_lets_the_host_finish()
    {
        // The mule's stack exactly: the writes were taken, and the flush that dispose does on the
        // way out was the one that answered EIO — after the host's own try/catch had gone out of
        // scope, which is why nothing caught it.
        var terminal = new FailingTerminal { FailDispose = true };
        var log = new RecordingLog();

        var exit = await HostShapedRunAsync(terminal, log);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(1, terminal.DisposeCount);
        Assert.True(terminal.WriteAttempts > 1, "the frames themselves were taken; only the close failed");
        Assert.Contains("failed while being closed", log.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_console_that_fails_on_both_write_and_close_still_lets_the_host_finish()
    {
        var terminal = new FailingTerminal { FailWrites = true, FailDispose = true };

        Assert.Equal(ExitCodes.Success, await HostShapedRunAsync(terminal, new RecordingLog()));
        Assert.Equal(1, terminal.DisposeCount);
    }

    [Fact]
    public void The_condition_is_journalled_once_rather_than_once_per_frame()
    {
        // At a 120 ms tick a per-frame version of this line is eight journal entries a second,
        // forever, on a fleet of frames that are all in this state until the overlay lands. Both
        // repaint triggers are exercised — the paint the tick makes, and the one a status change
        // makes — because both go through the same write.
        var log = new RecordingLog();
        var hub = new AgentStatusHub(AgentStatusFactory.Starting());
        using var stage = new ConsoleStage(
            new FailingTerminal { FailWrites = true },
            hub,
            new ManualClock(),
            StaticDisplayProbe.Visible,
            log);

        for (var frame = 0; frame < 40; frame++)
        {
            stage.Paint();
            hub.Publish(status => status with { Attempt = frame });
        }

        Assert.Single(log.Lines, line => line.Contains("no longer write to the console", StringComparison.Ordinal));
    }

    [Fact]
    public void A_failed_write_demotes_the_stage_and_says_so_where_the_no_display_warning_goes()
    {
        // The same two surfaces the no-display warning uses: the journal, and ConsoleVisibility on
        // the hub — which is what reaches the Fleet Manager. On a frame whose own screen is the
        // broken thing, that is the only surface left.
        var log = new RecordingLog();
        var hub = new AgentStatusHub(AgentStatusFactory.Starting());
        using var stage = new ConsoleStage(
            new FailingTerminal { FailWrites = true },
            hub,
            new ManualClock(),
            StaticDisplayProbe.Visible,
            log);

        Assert.True(stage.CanWrite);
        Assert.True(hub.Current.ConsoleVisibility!.Value.Visible);

        stage.Paint();

        Assert.False(stage.CanWrite);
        Assert.False(stage.Visibility.Visible);
        Assert.False(hub.Current.ConsoleVisibility!.Value.Visible);
        Assert.Contains(Eio, hub.Current.ConsoleVisibility!.Value.Reason, StringComparison.Ordinal);
        Assert.Contains("write=failed", hub.Current.ConsoleVisibility!.Value.Evidence, StringComparison.Ordinal);
        Assert.Single(log.Lines, line => line.StartsWith("Warn:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_console_that_has_already_refused_is_not_written_to_again()
    {
        var terminal = new FailingTerminal { FailWrites = true };
        var hub = new AgentStatusHub(AgentStatusFactory.Starting());
        var stage = new ConsoleStage(terminal, hub, new ManualClock(), StaticDisplayProbe.Visible, new RecordingLog());

        stage.Paint();
        stage.Paint();
        stage.Paint();
        hub.Publish(status => status with { Attempt = 7 });
        stage.Dispose();

        // One attempt, and nothing since — including the cursor-restoring write dispose would
        // otherwise make.
        Assert.Equal(1, terminal.WriteAttempts);
        Assert.Equal(0, stage.PaintedFrames);
        Assert.Null(stage.LastFrame);
    }

    [Fact]
    public void A_stage_whose_console_fails_is_still_disposable_twice()
    {
        var terminal = new FailingTerminal { FailWrites = true, FailDispose = true };
        var stage = new ConsoleStage(
            terminal,
            new AgentStatusHub(AgentStatusFactory.Starting()),
            new ManualClock(),
            StaticDisplayProbe.Visible,
            new RecordingLog());

        stage.Paint();
        stage.Dispose();
        stage.Dispose();

        Assert.Equal(1, terminal.DisposeCount);
    }

    [Fact]
    public void A_tty_over_a_device_that_answers_EIO_closes_without_taking_the_process_with_it()
    {
        // TtyTerminal.Dispose is the frame at the top of the mule's stack. The write still reports
        // the truth to its caller — the stage is what decides the agent survives it — but closing
        // the device must not be able to fail out of a `using`.
        var device = new ThrowingStream { FailWrites = true, FailFlush = true };
        var terminal = TtyTerminal.Over(device, 80, 25);

        Assert.Throws<IOException>(() => terminal.Write("frame"));
        terminal.Dispose();
        terminal.Dispose();

        Assert.True(device.DisposeAttempted);
    }

    [Fact]
    public async Task A_device_that_takes_the_bytes_and_fails_the_flush_is_the_same_condition()
    {
        // The whole path, unfaked apart from the device: ConsoleStage over a real TtyTerminal over
        // a stream that accepts every byte and then refuses to flush it. EIO can arrive at either
        // half, and the agent must not be able to tell the difference.
        var device = new ThrowingStream { FailFlush = true, FailDispose = true };
        var log = new RecordingLog();

        var exit = await HostShapedRunAsync(TtyTerminal.Over(device, 80, 25), log, frames: 20);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.True(device.DisposeAttempted);
        Assert.Single(log.Lines, line => line.Contains("no longer write to the console", StringComparison.Ordinal));
    }

    /// <summary>
    /// Runs a console stage the way <c>AgentHost.RunAsync</c> does, and answers with the exit code
    /// the process would have had.
    /// </summary>
    /// <remarks>
    /// The shape is load-bearing. The host's <c>try/catch</c> wraps the <c>Task.WhenAll</c> over
    /// its four loops, while the stage is a <c>using</c> whose disposal runs <i>after</i> that
    /// catch is out of scope — so an exception from dispose has nothing above it but the runtime.
    /// Reproducing that here is what makes this a regression test rather than a mood.
    /// </remarks>
    private static async Task<int> HostShapedRunAsync(ITerminal terminal, RecordingLog log, int frames = 5)
    {
        var hub = new AgentStatusHub(AgentStatusFactory.Starting());
        var clock = new ManualClock();
        using var stop = new CancellationTokenSource();
        clock.OnDelay = ticking =>
        {
            if (ticking.Delays.Count >= frames)
            {
                stop.Cancel();
            }
        };

        try
        {
            using var stage = new ConsoleStage(terminal, hub, clock, StaticDisplayProbe.Visible, log)
            {
                TickInterval = TimeSpan.FromMilliseconds(1),
            };

            try
            {
                await Task.WhenAll(stage.RunAsync(stop.Token));
            }
            catch (OperationCanceledException)
            {
                // Asked to stop.
            }
            catch (Exception loopFailure) when (loopFailure is not OutOfMemoryException and not StackOverflowException)
            {
                return ExitCodes.Unrecoverable;
            }

            return ExitCodes.Success;
        }
        catch (Exception disposeFailure) when (disposeFailure is not OutOfMemoryException and not StackOverflowException)
        {
            // Nothing in the host catches this. In the real process it was SIGABRT.
            return ExitCodes.Unrecoverable;
        }
    }

    /// <summary>A console that answers <c>EIO</c> wherever it is told to.</summary>
    private sealed class FailingTerminal : ITerminal
    {
        public int Columns => 80;

        public int Rows => 25;

        public bool SizeIsKnown => true;

        public bool SupportsColour => false;

        public bool FailWrites { get; init; }

        public bool FailDispose { get; init; }

        public int WriteAttempts { get; private set; }

        public int DisposeCount { get; private set; }

        public void Write(string text)
        {
            WriteAttempts++;
            if (FailWrites)
            {
                throw new IOException(Eio);
            }
        }

        public void Dispose()
        {
            DisposeCount++;
            if (FailDispose)
            {
                throw new IOException(Eio);
            }
        }
    }

    /// <summary>
    /// A write-only device that fails where a virtual console does.
    /// </summary>
    /// <remarks>
    /// <c>Dispose</c> flushes, because that is what <c>BufferedFileStreamStrategy</c> does and it
    /// is the only reason the mule's exception was thrown from a dispose at all.
    /// </remarks>
    private sealed class ThrowingStream : Stream
    {
        public bool FailWrites { get; init; }

        public bool FailFlush { get; init; }

        public bool FailDispose { get; init; }

        public bool DisposeAttempted { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            if (FailFlush)
            {
                throw new IOException(Eio);
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (FailWrites)
            {
                throw new IOException(Eio);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            DisposeAttempted = true;
            base.Dispose(disposing);

            if (disposing && (FailDispose || FailFlush))
            {
                throw new IOException(Eio);
            }
        }
    }
}
