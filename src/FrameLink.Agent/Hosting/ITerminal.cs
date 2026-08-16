using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace FrameLink.Agent.Hosting;

/// <summary>
/// A character display the agent can repaint — on a frame, the virtual console the agent owns
/// (§2.7).
/// </summary>
public interface ITerminal : IDisposable
{
    /// <summary>Visible width in columns.</summary>
    int Columns { get; }

    /// <summary>Visible height in rows.</summary>
    int Rows { get; }

    /// <summary>
    /// Whether <see cref="Columns"/> and <see cref="Rows"/> were measured rather than assumed.
    /// </summary>
    /// <remarks>
    /// Load-bearing, not diagnostic. The width decides the box drawing, the wrap points and the
    /// bar lengths, so a layout composed against an invented 80×25 on a 1920-pixel panel is
    /// wrong everywhere at once — and the agent used to report that guess in the affirmative
    /// ("Console stage attached to /dev/tty1 at 80x25"), which is the same write-only optimism
    /// §2.4 exists to refuse. A fallback is still used, because §2.7 bans blank screens, but it
    /// is never again reported as a measurement.
    /// </remarks>
    bool SizeIsKnown { get; }

    /// <summary>Whether ANSI colour may be emitted.</summary>
    bool SupportsColour { get; }

    /// <summary>Writes <paramref name="text"/> verbatim, including escape sequences.</summary>
    /// <remarks>
    /// May throw: a console device is allowed to stop taking bytes at any moment, and an
    /// implementation that swallowed that would be reporting a picture it did not draw. The
    /// caller decides what to do about it — see <see cref="Stage.ConsoleStage"/>, which demotes
    /// the terminal and keeps the agent running. What must never happen is the failure reaching
    /// the process (§1.2.2).
    /// </remarks>
    void Write(string text);
}

/// <summary>
/// Classifies the exceptions a console device is allowed to fail with.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured on the mule, 2026-08-15.</b> Writing a frame to <c>/dev/tty1</c> on a machine with
/// no framebuffer and no connected DRM output answered <c>EIO</c>, which .NET surfaces as
/// <c>System.IO.IOException: Input/output error : '/dev/tty1'</c>. It arrived on the buffered
/// stream's flush during <see cref="IDisposable.Dispose"/>, went unhandled, and systemd recorded
/// the agent as <c>code=killed, status=6/ABRT</c>.
/// </para>
/// <para>
/// The list is wider than <see cref="IOException"/> because a console can go away in more ways
/// than one: a revoked or re-owned tty answers <see cref="UnauthorizedAccessException"/>, a
/// handle closed underneath the stream answers <see cref="ObjectDisposedException"/>, and a
/// device that will not take the operation at all answers
/// <see cref="NotSupportedException"/>. None of them is a reason to stop reconciling.
/// </para>
/// </remarks>
public static class TerminalFailure
{
    /// <summary>Whether <paramref name="exception"/> is the console failing rather than a bug.</summary>
    public static bool IsDeviceFailure(Exception? exception) =>
        exception is IOException or UnauthorizedAccessException or ObjectDisposedException or NotSupportedException;
}

/// <summary>
/// Writes straight to a Linux virtual console.
/// </summary>
/// <remarks>
/// <para>
/// §2.7's console stage has to work "from the first second of the first boot" — before any
/// graphical stack, with no login session and no dependencies. That rules out
/// <see cref="Console"/>: under systemd the agent's stdout is the journal, not the panel.
/// The device node is opened directly instead.
/// </para>
/// <para>
/// The size comes from <c>ioctl(TIOCGWINSZ)</c> because guessing it wrong ruins a designed
/// layout — the DSI panel's console is far wider than the 80×24 fallback. The environment
/// override in front of it is what lets the autonomy harness (§5.1 M0) render a frame at a
/// known size for comparison.
/// </para>
/// </remarks>
public sealed partial class TtyTerminal : ITerminal
{
    /// <summary>
    /// The virtual terminal §2.7's console stage owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Eight, and the number is reasoned rather than picked.</b> One is out: that is the
    /// product's, and it is where <c>getty@tty1</c> serves the physical login §5.5 leans on for a
    /// frame that will not boot and cannot be reached over the network. Two through six are out
    /// because <c>systemd-logind</c> ships <c>NAutoVTs=6</c>, which spawns an <c>autovt@</c> getty
    /// on any of them the moment somebody switches there — the same two-programs-one-screen defect
    /// this move exists to end, deferred rather than fixed — and <c>ReserveVT=6</c> keeps one on
    /// six permanently. Seven is out by convention: it is where an X server or a display manager
    /// lands, and leaving it clear costs nothing. Eight is the first free one, which is the same
    /// rule <c>startx</c> uses and therefore the least arbitrary available.
    /// </para>
    /// <para>
    /// It also has to stay inside the twelve function keys, and that is not cosmetic: an operator
    /// standing at the frame with a keyboard reaches this console with <b>Ctrl+Alt+F8</b>, which is
    /// how the narration is read when the agent has deliberately <i>not</i> taken the screen.
    /// There is no Ctrl+Alt+F13.
    /// </para>
    /// <para>
    /// The console rotation is unaffected: <c>boot.cmdline.fbcon-rotate</c> sets
    /// <c>fbcon=rotate:1</c> on the kernel command line, which rotates the framebuffer console
    /// itself and therefore every virtual terminal on it, not just the first.
    /// </para>
    /// </remarks>
    public const int AgentTerminal = 8;

    /// <summary>The virtual terminal the autologin session and its compositor own.</summary>
    public const int ProductTerminal = 1;

    /// <summary>The device node for <see cref="AgentTerminal"/>.</summary>
    public const string DefaultPath = "/dev/tty8";

    private const int FallbackColumns = 80;
    private const int FallbackRows = 24;
    private const ulong RequestGetWindowSize = 0x5413;

    private readonly Stream _stream;
    private bool _disposed;

    private TtyTerminal(Stream stream, int columns, int rows, bool measured)
    {
        _stream = stream;
        Columns = columns;
        Rows = rows;
        SizeIsKnown = measured;
    }

    /// <inheritdoc/>
    public int Columns { get; }

    /// <inheritdoc/>
    public int Rows { get; }

    /// <inheritdoc/>
    public bool SizeIsKnown { get; }

    /// <inheritdoc/>
    public bool SupportsColour => true;

    /// <summary>
    /// Opens <paramref name="path"/>, falling back to a standard-output terminal when the
    /// device is not there.
    /// </summary>
    /// <remarks>
    /// The fallback is not a convenience, it is §2.7's "hard rule against blank screens"
    /// applied to the agent's own diagnostics: a frame with no <c>/dev/tty1</c> — a container,
    /// a virtual agent (§5.3), a developer's box — still narrates, just somewhere else.
    /// </remarks>
    public static ITerminal Open(string path, IAgentLog log)
    {
        ArgumentNullException.ThrowIfNull(log);

        try
        {
            var stream = OpenDevice(path);

            var (columns, rows, measured) = MeasureWindow(stream);

            if (measured)
            {
                log.Info(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Console stage attached to {path}; it reports {columns}x{rows}."));
            }
            else
            {
                // The old wording here was "attached to /dev/tty1 at 80x25", which reported a
                // failure as a measurement. On the mule that line appeared on a machine with no
                // framebuffer and no connected output at all, and an operator reading it
                // concluded the screen was working.
                log.Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Console stage attached to {path}, but it would not say how big it is. "
                    + $"Falling back to {columns}x{rows}, so the layout may be the wrong shape."));
            }

            return new TtyTerminal(stream, columns, rows, measured);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            log.Warn($"Console stage could not open {path} ({exception.Message}); narrating on standard output instead.");
            return new StandardOutputTerminal();
        }
    }

    /// <summary>
    /// Wraps an already-open write stream at a known size.
    /// </summary>
    /// <remarks>
    /// The seam behind <see cref="Open"/>. It exists so the device-failure path — a console that
    /// answers <c>EIO</c> on write and again on the flush inside dispose — can be exercised
    /// without a tty, which is what nothing did before the mule aborted on it.
    /// </remarks>
    public static ITerminal Over(Stream stream, int columns, int rows, bool sizeIsKnown = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new TtyTerminal(stream, columns, rows, sizeIsKnown);
    }

    /// <summary>
    /// The virtual terminal number a <c>/dev/ttyN</c> path names, or null for anything else.
    /// </summary>
    /// <remarks>
    /// So that the device the stage writes to and the terminal the handover brings to the front
    /// are one decision. Overriding the path with <c>FL_TTY</c> and leaving the switch pointed at
    /// terminal eight would produce a frame that paints one console and reveals another, which is
    /// a blank screen with a perfectly healthy log beside it.
    /// </remarks>
    public static int? NumberOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        const string Prefix = "/dev/tty";

        return path.StartsWith(Prefix, StringComparison.Ordinal)
            && int.TryParse(path.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number > 0
                ? number
                : null;
    }

    /// <inheritdoc/>
    public void Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var bytes = Encoding.UTF8.GetBytes(text);
        _stream.Write(bytes, 0, bytes.Length);
        _stream.Flush();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Swallows a failing close, because disposal is cleanup and cleanup that throws takes the
    /// process with it — <see cref="TerminalFailure"/> records the abort this cost. Idempotent,
    /// so a second dispose cannot resurrect the same failure.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _stream.Dispose();
        }
        catch (Exception exception) when (TerminalFailure.IsDeviceFailure(exception))
        {
            // The descriptor is closed regardless: FileStream closes its handle in a finally,
            // so the only thing lost here is bytes that were never going to reach a screen.
        }
    }

    /// <summary>
    /// Opens the console device, without letting it become the agent's controlling terminal.
    /// </summary>
    /// <remarks>
    /// <see cref="ConsoleDevice"/> carries the whole argument for <c>O_NOCTTY</c>; the short
    /// version is that <c>tty1</c> always had getty's session and the agent's own terminal has
    /// none, so this is the one open that could have made a keystroke on the panel signal the
    /// agent. The managed open stays as the fallback, because it is the one that produces a real
    /// diagnosis when the device is simply not there — a container, a workstation, a virtual
    /// agent (§5.3) — and that diagnosis is what <see cref="Open"/> turns into the standard-output
    /// terminal.
    /// </remarks>
    private static FileStream OpenDevice(string path)
    {
        var handle = ConsoleDevice.TryOpenForWriting(path);

        if (handle is not null)
        {
            try
            {
                return new FileStream(handle, FileAccess.Write, bufferSize: 0);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                handle.Dispose();
            }
        }

        return new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Write,
                Share = FileShare.ReadWrite,
                Options = FileOptions.WriteThrough,

                // Unbuffered, and that is the fix for a crash rather than a micro-optimisation.
                // At the default 4096 the stream is a BufferedFileStreamStrategy, whose
                // FlushWrite does not clear _writePos when the underlying write throws — so a
                // frame that failed with EIO stayed in the buffer and was retried by the flush
                // inside Dispose, where nothing was catching. That is the mule's SIGABRT.
                // A console has no use for buffering anyway: every frame is one write followed
                // by one flush, and WriteThrough already refuses the OS page cache.
                // The handle-based constructor above is unbuffered for the same reason: .NET
                // wraps a stream in the buffering strategy only when bufferSize exceeds one.
                BufferSize = 0,
            });
    }

    private static (int Columns, int Rows, bool Measured) MeasureWindow(FileStream stream)
    {
        if (TryReadEnvironmentSize(out var overridden))
        {
            return (overridden.Columns, overridden.Rows, true);
        }

        if (!OperatingSystem.IsLinux())
        {
            return (FallbackColumns, FallbackRows, false);
        }

        try
        {
            var size = default(WindowSize);
            if (IoControl(stream.SafeFileHandle.DangerousGetHandle(), RequestGetWindowSize, ref size) == 0
                && size.Columns > 0
                && size.Rows > 0)
            {
                return (size.Columns, size.Rows, true);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // No libc ioctl to ask; the fallback below is the answer.
        }

        return (FallbackColumns, FallbackRows, false);
    }

    private static bool TryReadEnvironmentSize(out (int Columns, int Rows) size)
    {
        size = default;
        var columns = Environment.GetEnvironmentVariable("COLUMNS");
        var rows = Environment.GetEnvironmentVariable("LINES");

        if (!int.TryParse(columns, CultureInfo.InvariantCulture, out var parsedColumns) || parsedColumns <= 0
            || !int.TryParse(rows, CultureInfo.InvariantCulture, out var parsedRows) || parsedRows <= 0)
        {
            return false;
        }

        size = (parsedColumns, parsedRows);
        return true;
    }

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int IoControl(nint descriptor, ulong request, ref WindowSize size);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowSize
    {
        public ushort Rows;
        public ushort Columns;
        public ushort PixelWidth;
        public ushort PixelHeight;
    }
}

/// <summary>
/// The fallback surface: standard output, which systemd routes into the journal.
/// </summary>
public sealed class StandardOutputTerminal : ITerminal
{
    /// <inheritdoc/>
    public int Columns { get; } = SafeWidth();

    /// <inheritdoc/>
    public int Rows { get; } = SafeHeight();

    /// <inheritdoc/>
    /// <remarks>A redirected stream has no size to report, so the 80×24 it uses is a guess.</remarks>
    public bool SizeIsKnown { get; } = !Console.IsOutputRedirected;

    /// <inheritdoc/>
    public bool SupportsColour => !Console.IsOutputRedirected;

    /// <inheritdoc/>
    public void Write(string text) => Console.Out.Write(text);

    /// <inheritdoc/>
    /// <remarks>
    /// Guarded for the same reason <see cref="TtyTerminal.Dispose"/> is. This surface is a pipe
    /// into the journal, and a pipe whose reader went away — journald being restarted mid-shutdown
    /// — fails the final flush with <see cref="IOException"/>.
    /// </remarks>
    public void Dispose()
    {
        try
        {
            Console.Out.Flush();
        }
        catch (Exception exception) when (TerminalFailure.IsDeviceFailure(exception))
        {
            // Nothing to say and nowhere left to say it.
        }
    }

    private static int SafeWidth()
    {
        try
        {
            return Console.IsOutputRedirected || Console.WindowWidth <= 0 ? 80 : Console.WindowWidth;
        }
        catch (IOException)
        {
            return 80;
        }
    }

    private static int SafeHeight()
    {
        try
        {
            return Console.IsOutputRedirected || Console.WindowHeight <= 0 ? 24 : Console.WindowHeight;
        }
        catch (IOException)
        {
            return 24;
        }
    }
}
