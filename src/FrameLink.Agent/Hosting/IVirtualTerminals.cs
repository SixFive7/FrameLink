using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FrameLink.Agent.Hosting;

/// <summary>
/// The seat's virtual terminals — which one the panel is showing, and how to change it.
/// </summary>
/// <remarks>
/// <para>
/// A frame has one panel and more than one program that wants to draw on it. §2.7's console stage
/// writes a designed frame; <c>getty@tty1</c> writes a login prompt, and once the autologin
/// drop-in has converged that same getty is what starts the compositor. Both used to write
/// <c>/dev/tty1</c>, and the loser was whichever painted first — measured on the frame as a repair
/// screen that lasted under a second before a login prompt replaced it.
/// </para>
/// <para>
/// The fix is not to silence the getty: the physical login on <c>tty1</c> is §5.5's recovery path
/// for a frame that will not boot and cannot be reached over the network, which is why
/// <c>Conflicts=getty@tty1.service</c> was rejected. It is to give the console stage a terminal of
/// its own and bring that terminal to the front when the agent is the surface that should be seen.
/// </para>
/// <para>
/// This is the seam that makes the boundary testable without a console, the way
/// <see cref="TtyTerminal.Over"/> makes the write path testable without a tty. Neither method
/// throws: a machine with no virtual terminals answers <see langword="null"/> and
/// <see langword="false"/>, and <see cref="Stage.ScreenHandover"/> stands down and says so once.
/// </para>
/// </remarks>
public interface IVirtualTerminals
{
    /// <summary>
    /// The number of the terminal the panel is showing, or null if it cannot be read.
    /// </summary>
    /// <remarks>
    /// This is the <i>confirmation</i>, and it is separate from <see cref="Activate"/> on purpose.
    /// A successful <c>VT_ACTIVATE</c> means the kernel accepted the request, not that the switch
    /// happened — the switch completes only once the process holding the outgoing terminal has
    /// released it, which on a converged frame is a Wayland compositor dropping DRM master.
    /// Reporting the request as the outcome would be the same write-only optimism §2.4 exists to
    /// refuse, and the failure it would hide is a black panel.
    /// </remarks>
    int? Foreground();

    /// <summary>Asks the kernel to bring <paramref name="terminal"/> to the front.</summary>
    /// <returns>Whether the request was accepted. Not whether the switch has happened.</returns>
    bool Activate(int terminal);
}

/// <summary>
/// Opens a Linux console device without taking it as a controlling terminal.
/// </summary>
/// <remarks>
/// <para>
/// <b>O_NOCTTY is the whole reason this exists</b>, and it is a consequence of moving off
/// <c>tty1</c> rather than a general precaution. <c>tty_open()</c> makes a terminal the caller's
/// controlling terminal when the caller is a session leader with no controlling terminal
/// <i>and the terminal has no session</i>. A systemd service is a session leader with no
/// controlling terminal, and <c>tty1</c> always had a session — getty's — so the agent's open of it
/// never qualified. A terminal nothing else has ever opened has no session at all, so the agent
/// would have become its owner and its foreground process group, and a <c>Ctrl+C</c> typed on the
/// panel's keyboard while the repair screen was up would have sent <c>SIGINT</c> to the agent.
/// With <c>O_NOCTTY</c> the terminal has no <c>pgrp</c> and those keystrokes signal nobody.
/// </para>
/// <para>
/// The managed <see cref="FileStream"/> constructor cannot express the flag —
/// <see cref="FileStreamOptions"/> has no member for it and the runtime does not pass it — so the
/// open is done through libc, which the console stage already reaches for to measure the window.
/// Every failure answers null and the caller falls back to the managed open, which then throws the
/// honest exception rather than this one inventing one from <c>errno</c>.
/// </para>
/// </remarks>
internal static partial class ConsoleDevice
{
    private const int ReadOnly = 0x0;
    private const int WriteOnly = 0x1;

    /// <summary><c>O_NOCTTY</c>, <c>0400</c> octal — identical on x86-64 and arm64.</summary>
    private const int NoControllingTerminal = 0x100;

    /// <summary><c>O_CLOEXEC</c>, <c>02000000</c> octal — identical on x86-64 and arm64.</summary>
    private const int CloseOnExec = 0x80000;

    /// <summary>Opens <paramref name="path"/> for writing frames to, or null.</summary>
    public static SafeFileHandle? TryOpenForWriting(string path) => TryOpen(path, WriteOnly);

    /// <summary>Opens <paramref name="path"/> for issuing terminal ioctls on, or null.</summary>
    public static SafeFileHandle? TryOpenForControl(string path) => TryOpen(path, ReadOnly);

    private static SafeFileHandle? TryOpen(string path, int access)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            var descriptor = Open(path, access | NoControllingTerminal | CloseOnExec, 0);
            return descriptor < 0 ? null : new SafeFileHandle(descriptor, ownsHandle: true);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // No libc to ask. The managed open is the answer, and it produces the real diagnosis.
            return null;
        }
    }

    // Declared with the variadic third argument spelled out and always passed as zero. `open` is
    // `int open(const char *, int, ...)`, and naming the mode rather than relying on a two-argument
    // call keeps the signature honest on both architectures a frame is ever built for.
    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags, int mode);
}

/// <summary>
/// The kernel's virtual consoles, through <c>ioctl</c> and <c>sysfs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>No new dependency.</b> <c>chvt(1)</c> would do this, but it ships in <c>kbd</c>, which would
/// become a package the frame has to have before it can narrate — and §2.7's console stage exists
/// precisely to work "from the first second of the first boot", with no dependencies. The ioctl
/// <c>chvt</c> issues is three lines here and the agent already binds <c>ioctl</c> for
/// <c>TIOCGWINSZ</c>.
/// </para>
/// <para>
/// <b>The confirmation is a file read, not a second ioctl</b>, and that is deliberate.
/// <c>VT_WAITACTIVE</c> is the obvious partner to <c>VT_ACTIVATE</c> and it is a trap here: it
/// blocks in the kernel until the switch completes, and the switch completes only when the process
/// holding the outgoing terminal answers <c>VT_RELDISP</c>. A compositor that has wedged never
/// answers, and the ioctl never returns — an unbounded block inside the one loop whose job is to
/// keep the frame's screen honest. Reading <c>/sys/class/tty/tty0/active</c> asks the same kernel
/// the same question with a deadline the caller owns, and it goes through
/// <see cref="ISystemFiles"/>, so the whole handover can be exercised against a directory on a
/// workstation.
/// </para>
/// </remarks>
public sealed partial class LinuxVirtualTerminals : IVirtualTerminals, IDisposable
{
    /// <summary>Where the kernel publishes which console is in front.</summary>
    public const string ForegroundPath = "/sys/class/tty/tty0/active";

    /// <summary>The console multiplexer, which always resolves to whichever is in front.</summary>
    public const string ControlPath = "/dev/tty0";

    /// <summary><c>VT_ACTIVATE</c>.</summary>
    private const ulong RequestActivate = 0x5606;

    private readonly ISystemFiles _files;
    private readonly Lock _gate = new();

    private SafeFileHandle? _control;
    private bool _opened;
    private bool _disposed;

    /// <summary>Creates an instance reading and writing through <paramref name="files"/>.</summary>
    public LinuxVirtualTerminals(ISystemFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        _files = files;
    }

    /// <inheritdoc/>
    public int? Foreground() => NumberIn(_files.ReadText(ForegroundPath));

    /// <summary>
    /// The console number in a <c>tty0/active</c> reading, or null.
    /// </summary>
    /// <remarks>
    /// Tolerant of the multi-word form, because <c>/sys/class/tty/console/active</c> on this
    /// hardware reads <c>ttyAMA10 tty1</c> and the two files are one keystroke apart. A word that
    /// is not <c>tty</c> followed by a number is skipped rather than failing the read.
    /// </remarks>
    public static int? NumberIn(string? active)
    {
        if (string.IsNullOrWhiteSpace(active))
        {
            return null;
        }

        foreach (var word in active.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.StartsWith("tty", StringComparison.Ordinal)
                && int.TryParse(word.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && number > 0)
            {
                return number;
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public bool Activate(int terminal)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(terminal);

        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            if (!_opened)
            {
                _opened = true;
                _control = ConsoleDevice.TryOpenForControl(ControlPath);
            }

            if (_control is null)
            {
                return false;
            }

            try
            {
                return IoControl(_control.DangerousGetHandle(), RequestActivate, terminal) == 0;
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _control?.Dispose();
            _control = null;
        }
    }

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int IoControl(nint descriptor, ulong request, int argument);
}

/// <summary>A seat with no virtual terminals — a container, a workstation, a virtual agent (§5.3).</summary>
public sealed class NoVirtualTerminals : IVirtualTerminals
{
    /// <summary>The shared instance.</summary>
    public static NoVirtualTerminals Instance { get; } = new();

    /// <inheritdoc/>
    public int? Foreground() => null;

    /// <inheritdoc/>
    public bool Activate(int terminal) => false;
}
