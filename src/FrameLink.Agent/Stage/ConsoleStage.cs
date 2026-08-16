using FrameLink.Agent.Hosting;
using FrameLink.Agent.State;

namespace FrameLink.Agent.Stage;

/// <summary>
/// Drives <see cref="StageRenderer"/> onto a terminal — §2.7's <b>console stage</b>.
/// </summary>
/// <remarks>
/// <para>
/// The first of §2.7's two rendering stages, and the only one M1 builds. The browser stage waits
/// for the reconciler to bring up the kiosk stack, which is M2 (§5.1); until then this is the
/// whole screen, which is exactly the situation §2.7 designed it for — before any graphical stack
/// exists, no login session, no dependencies.
/// </para>
/// <para>
/// Two things drive a repaint: the animation tick, so a wait never looks like a hang, and a status
/// change, so an adoption pressed in the Fleet Manager appears on the frame in the same second
/// rather than up to a tick later. Both funnel through one lock, because a half-written frame on a
/// terminal is visible garbage rather than a race that gets tidied up later.
/// </para>
/// <para>
/// Nothing this class does may stop the agent. §1.2.2 has a frame provisioning and self-healing
/// with the server unreachable, and a dark panel is a strictly smaller problem than that — so a
/// console that refuses its bytes demotes itself (<see cref="Visibility"/>), says so once, and the
/// other three loops carry on. This is not defensive tidiness: the mule aborted with
/// <c>status=6/ABRT</c> because a failing flush inside <see cref="Dispose"/> was the one write
/// nobody was catching, and with the restart limiter now honoured the second such crash leaves the
/// unit <c>failed</c> and the frame silently dead.
/// </para>
/// </remarks>
public sealed class ConsoleStage : IDisposable
{
    private readonly ITerminal _terminal;
    private readonly AgentStatusHub _hub;
    private readonly IAgentClock _clock;
    private readonly IAgentLog _log;
    private readonly IDisposable _subscription;
    private readonly Lock _paint = new();

    private int _tick;
    private bool _disposed;
    private bool _writable = true;

    /// <summary>Attaches the stage to <paramref name="terminal"/>.</summary>
    /// <param name="terminal">Where frames are written.</param>
    /// <param name="hub">The shared status holder.</param>
    /// <param name="clock">Source of the animation tick.</param>
    /// <param name="display">
    /// Asked, once, whether anything can actually show a picture.
    /// </param>
    /// <param name="log">Where the honest answer is recorded.</param>
    public ConsoleStage(
        ITerminal terminal,
        AgentStatusHub hub,
        IAgentClock clock,
        IDisplayProbe? display = null,
        IAgentLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(clock);

        _terminal = terminal;
        _hub = hub;
        _clock = clock;
        _log = log ?? NullLog.Instance;

        // Asked before the first paint, because the answer changes what a successful paint
        // means. A write to /dev/tty8 on a frame with no framebuffer at all may return success
        // and may fail with EIO, and neither outcome is evidence about the picture — so
        // PaintedFrames counts frames written, never frames seen.
        Visibility = (display ?? StaticDisplayProbe.Visible).Probe();
        hub.Publish(status => status with { ConsoleVisibility = Visibility });

        if (Visibility.Visible)
        {
            _log.Info($"Console stage can be seen: {Visibility.Reason} [{Visibility.Evidence}]");
        }
        else
        {
            _log.Warn(
                "Console stage attached, no display output detected — narration is not visible. "
                + $"{Visibility.Reason} [{Visibility.Evidence}]");
        }

        if (!terminal.SizeIsKnown)
        {
            _log.Warn(
                $"The console did not report its size, so the layout is being composed against "
                + $"{terminal.Columns}x{terminal.Rows} rather than a measured one.");
        }

        _subscription = hub.Subscribe(_ => Paint());
    }

    /// <summary>
    /// Whether the frames this stage writes can be seen.
    /// </summary>
    /// <remarks>
    /// Mostly not derivable from anything this class does. §2.7 bans blank screens and the stage
    /// is the mechanism, but on a stock image the panel overlay has not been applied and there is
    /// no framebuffer — measured on the mule 2026-08-15 — so a write lands nowhere, and whether it
    /// returns success or <c>EIO</c> is up to the device. The probe answers the question at
    /// construction; a write that fails afterwards is the one piece of first-hand evidence the
    /// stage does get, and it replaces the probe's answer here. Either way the honest position is
    /// the same: the stage reports its own blindness rather than assuming it away.
    /// </remarks>
    public DisplayVisibility Visibility { get; private set; }

    /// <summary>Whether the terminal is still taking bytes.</summary>
    /// <remarks>
    /// False once a write has failed. The demotion is for the life of the process on purpose: a
    /// console that answers <c>EIO</c> is not coming back without the panel overlay, and the
    /// overlay only takes at a reboot — so retrying every 120 ms would buy nothing and cost the
    /// journal a line per frame.
    /// </remarks>
    public bool CanWrite
    {
        get
        {
            lock (_paint)
            {
                return _writable;
            }
        }
    }

    /// <summary>How often the animation advances.</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromMilliseconds(120);

    /// <summary>How many frames have been painted.</summary>
    public int PaintedFrames { get; private set; }

    /// <summary>The most recent frame, as it was written.</summary>
    public string? LastFrame { get; private set; }

    /// <summary>Paints one frame immediately.</summary>
    /// <remarks>
    /// Never throws on account of the terminal. A write that fails demotes the stage instead, and
    /// the reporting happens outside the paint lock — the publish that carries it re-enters
    /// <see cref="Paint"/> through the hub subscription, which then finds
    /// <see cref="CanWrite"/> false and returns.
    /// </remarks>
    public void Paint()
    {
        string? failure = null;

        lock (_paint)
        {
            if (_disposed || !_writable)
            {
                return;
            }

            var frame = StageRenderer.Render(
                _hub.Current,
                _clock.UtcNow,
                _tick,
                _terminal.Columns,
                _terminal.Rows,
                _terminal.SupportsColour);

            try
            {
                _terminal.Write(frame);
            }
            catch (Exception exception) when (TerminalFailure.IsDeviceFailure(exception))
            {
                _writable = false;
                failure = exception.Message;
            }

            if (failure is null)
            {
                LastFrame = frame;
                PaintedFrames++;
            }
        }

        if (failure is not null)
        {
            ReportUnwritable(failure);
        }
    }

    /// <summary>Repaints on the animation tick until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Paint();

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!CanWrite)
            {
                // Nothing left to animate. Returning frees the tick rather than waking every
                // 120 ms to re-check a verdict that cannot change, and the host awaits this
                // alongside three loops that are still doing real work.
                return;
            }

            try
            {
                await _clock.DelayAsync(TickInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Interlocked.Increment(ref _tick);
            Paint();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        bool writable;

        lock (_paint)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            writable = _writable;
        }

        _subscription.Dispose();

        if (writable)
        {
            try
            {
                _terminal.Write(Ansi.ShowCursor + Ansi.Reset + "\n");
            }
            catch (Exception exception) when (TerminalFailure.IsDeviceFailure(exception))
            {
                // The panel going away during shutdown is not worth failing over.
            }
        }

        try
        {
            _terminal.Dispose();
        }
        catch (Exception exception) when (TerminalFailure.IsDeviceFailure(exception))
        {
            // The mule's SIGABRT, verbatim: TtyTerminal.Dispose -> buffered flush -> EIO, thrown
            // out of a `using` in AgentHost where no catch existed. TtyTerminal no longer buffers
            // and no longer rethrows, so this guard is for every other ITerminal — closing a
            // console is cleanup, and cleanup is never worth a process.
            _log.Warn($"The console failed while being closed ({exception.Message}); the agent is stopping anyway.");
        }
    }

    /// <summary>
    /// Records, once, that the console has stopped taking output.
    /// </summary>
    /// <remarks>
    /// Reported through the channel the no-display warning already uses — a journal line plus
    /// <see cref="AgentStatus.ConsoleVisibility"/> — so the condition reaches the Fleet Manager,
    /// which on a frame whose screen is the broken thing is the only surface left (§1.2.3).
    /// Once, because a per-frame version of this line is eight journal entries a second.
    /// </remarks>
    private void ReportUnwritable(string detail)
    {
        Visibility = new DisplayVisibility(
            false,
            "The console stopped accepting output, so nothing is being narrated on this frame: "
            + detail,
            $"{Visibility.Evidence}; write=failed ({detail})");

        _log.Warn(
            "Console stage can no longer write to the console — narration has stopped. "
            + $"{detail}. Nothing further will be written to it and this is said once rather than "
            + $"once per frame; the agent keeps reconciling. [{Visibility.Evidence}]");

        _hub.Publish(status => status with { ConsoleVisibility = Visibility });
    }
}
