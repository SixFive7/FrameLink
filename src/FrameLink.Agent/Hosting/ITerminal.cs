using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace FrameLink.Agent.Hosting;

/// <summary>
/// A character display the agent can repaint — on a frame, the DSI panel's console
/// <c>/dev/tty1</c> (§2.7).
/// </summary>
public interface ITerminal : IDisposable
{
    /// <summary>Visible width in columns.</summary>
    int Columns { get; }

    /// <summary>Visible height in rows.</summary>
    int Rows { get; }

    /// <summary>Whether ANSI colour may be emitted.</summary>
    bool SupportsColour { get; }

    /// <summary>Writes <paramref name="text"/> verbatim, including escape sequences.</summary>
    void Write(string text);
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
    /// <summary>The DSI panel's default console (§2.7).</summary>
    public const string DefaultPath = "/dev/tty1";

    private const int FallbackColumns = 80;
    private const int FallbackRows = 24;
    private const ulong RequestGetWindowSize = 0x5413;

    private readonly FileStream _stream;

    private TtyTerminal(FileStream stream, int columns, int rows)
    {
        _stream = stream;
        Columns = columns;
        Rows = rows;
    }

    /// <inheritdoc/>
    public int Columns { get; }

    /// <inheritdoc/>
    public int Rows { get; }

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
            var stream = new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Write,
                    Share = FileShare.ReadWrite,
                    Options = FileOptions.WriteThrough,
                });

            var (columns, rows) = MeasureWindow(stream);
            log.Info(string.Create(CultureInfo.InvariantCulture, $"Console stage attached to {path} at {columns}x{rows}."));
            return new TtyTerminal(stream, columns, rows);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            log.Warn($"Console stage could not open {path} ({exception.Message}); narrating on standard output instead.");
            return new StandardOutputTerminal();
        }
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
    public void Dispose() => _stream.Dispose();

    private static (int Columns, int Rows) MeasureWindow(FileStream stream)
    {
        if (TryReadEnvironmentSize(out var overridden))
        {
            return overridden;
        }

        if (!OperatingSystem.IsLinux())
        {
            return (FallbackColumns, FallbackRows);
        }

        try
        {
            var size = default(WindowSize);
            if (IoControl(stream.SafeFileHandle.DangerousGetHandle(), RequestGetWindowSize, ref size) == 0
                && size.Columns > 0
                && size.Rows > 0)
            {
                return (size.Columns, size.Rows);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // No libc ioctl to ask; the fallback below is the answer.
        }

        return (FallbackColumns, FallbackRows);
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
    public bool SupportsColour => !Console.IsOutputRedirected;

    /// <inheritdoc/>
    public void Write(string text) => Console.Out.Write(text);

    /// <inheritdoc/>
    public void Dispose() => Console.Out.Flush();

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
