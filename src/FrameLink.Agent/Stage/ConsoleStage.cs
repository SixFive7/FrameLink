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
/// </remarks>
public sealed class ConsoleStage : IDisposable
{
    private readonly ITerminal _terminal;
    private readonly AgentStatusHub _hub;
    private readonly IAgentClock _clock;
    private readonly IDisposable _subscription;
    private readonly Lock _paint = new();

    private int _tick;
    private bool _disposed;

    /// <summary>Attaches the stage to <paramref name="terminal"/>.</summary>
    public ConsoleStage(ITerminal terminal, AgentStatusHub hub, IAgentClock clock)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(clock);

        _terminal = terminal;
        _hub = hub;
        _clock = clock;
        _subscription = hub.Subscribe(_ => Paint());
    }

    /// <summary>How often the animation advances.</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromMilliseconds(120);

    /// <summary>How many frames have been painted.</summary>
    public int PaintedFrames { get; private set; }

    /// <summary>The most recent frame, as it was written.</summary>
    public string? LastFrame { get; private set; }

    /// <summary>Paints one frame immediately.</summary>
    public void Paint()
    {
        lock (_paint)
        {
            if (_disposed)
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

            _terminal.Write(frame);
            LastFrame = frame;
            PaintedFrames++;
        }
    }

    /// <summary>Repaints on the animation tick until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Paint();

        while (!cancellationToken.IsCancellationRequested)
        {
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
        lock (_paint)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _subscription.Dispose();

        try
        {
            _terminal.Write(Ansi.ShowCursor + Ansi.Reset + "\n");
        }
        catch (IOException)
        {
            // The panel going away during shutdown is not worth failing over.
        }

        _terminal.Dispose();
    }
}
