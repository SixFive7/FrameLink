using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FrameLink.Agent.Hosting;

/// <summary>One touchscreen, as the kernel describes it.</summary>
/// <param name="Node">The evdev node to read, e.g. <c>/dev/input/event4</c>.</param>
/// <param name="Name">The device's own name, e.g. <c>Goodix Capacitive TouchScreen</c>.</param>
public readonly record struct TouchDevice(string Node, string Name);

/// <summary>
/// <b>What taking a finger off the panel does at the instant it is lifted</b> — the console's two
/// recovery gestures, resolved to one answer.
/// </summary>
/// <remarks>
/// An enum rather than two booleans, because the three cases are exclusive and the screen has to
/// name exactly one of them in the words under the bar. A person watching that line is reading
/// this value.
/// </remarks>
public enum TouchCommit
{
    /// <summary>
    /// Nothing at all — the finger has not been down long enough to mean anything yet, which is
    /// also the whole of how somebody changes their mind.
    /// </summary>
    Nothing,

    /// <summary>Restart the frame and try everything again.</summary>
    Restart,

    /// <summary>Switch the frame off, and it stays off until somebody is standing at it.</summary>
    Shutdown,
}

/// <summary>
/// What the frame's own screen can do about a resource that has given up — §2.7 item 9.
/// </summary>
/// <param name="Device">The evdev node being read, or null when this frame has no touchscreen.</param>
/// <param name="Hold">
/// How long a finger has to stay down for the whole gesture — the length the bar fills over, which
/// for the two-way recovery hold is the <i>shutdown</i> mark rather than the restart one.
/// </param>
/// <param name="HoldingSince">
/// When the finger now on the screen went down, or null when nothing is being held. Published
/// rather than a remaining time, so the console and the browser compose the same countdown from
/// the same snapshot and neither has to be woken to keep it moving.
/// </param>
/// <param name="RestartAt">
/// <b>The earlier of the two marks, or null when this hold means only one thing (decision 92).</b>
/// </param>
/// <remarks>
/// <para>
/// <b>Two marks on one gesture, and the second one is why the first cannot act on its own.</b> A
/// hold that restarted the frame the instant it reached three seconds could never reach ten, so
/// two hold lengths on one finger force the decision to the moment the finger comes <i>off</i>.
/// That is not a preference: it is the only arrangement in which both lengths are reachable.
/// </para>
/// <para>
/// <b>Which is also what gives the gesture a way out.</b> Nothing happens while the finger is
/// down, so the first band — everything before <paramref name="RestartAt"/> — is a real "I have
/// changed my mind": take the finger off and the frame does nothing at all. No timer takes an
/// action and no timer withdraws one; the finger decides, always.
/// </para>
/// <para>
/// <b>Null means the single-meaning hold, which is the firmware question.</b> That one fires while
/// the finger is still down, at <paramref name="Hold"/>, because it has one answer and waiting for
/// a release would leave a person holding a full bar wondering what else was wanted. So
/// <see cref="Commit"/> never commits anything for that shape, and the renderer tells the two
/// apart by this field rather than by guessing from the duration.
/// </para>
/// </remarks>
public readonly record struct TouchRetryState(
    string? Device,
    TimeSpan Hold,
    DateTimeOffset? HoldingSince,
    TimeSpan? RestartAt = null)
{
    /// <summary>A frame with no touchscreen found, which is every machine that is not a frame.</summary>
    public static TouchRetryState None { get; } = new(null, TimeSpan.FromSeconds(3), null);

    /// <summary>Whether a retry can be pressed on this frame's own screen.</summary>
    public bool Available => Device is { Length: > 0 };

    /// <summary>Whether this hold has two marks, so a release is what decides.</summary>
    public bool TwoWay => RestartAt is { Ticks: > 0 };

    /// <summary>How long the finger now on the screen has been down, or zero.</summary>
    public TimeSpan Elapsed(DateTimeOffset now) =>
        HoldingSince is not { } since || now < since ? TimeSpan.Zero : now - since;

    /// <summary>How far through the hold the finger on the screen has got, from 0 to 1.</summary>
    public double Progress(DateTimeOffset now) =>
        HoldingSince is not { } since || Hold <= TimeSpan.Zero
            ? 0
            : Math.Clamp((now - since).Ticks / (double)Hold.Ticks, 0, 1);

    /// <summary>How much longer the finger has to stay down, never below zero.</summary>
    public TimeSpan Remaining(DateTimeOffset now) =>
        HoldingSince is not { } since
            ? Hold
            : Hold - (now - since) is { Ticks: > 0 } left ? left : TimeSpan.Zero;

    /// <summary>
    /// What taking the finger off at <paramref name="now"/> would do.
    /// </summary>
    /// <remarks>
    /// <b>One place, both readers.</b> The watch asks this on the release edge to decide what to
    /// call, and the renderer asks it every repaint to decide what to promise — so the sentence
    /// under the bar and the thing that then happens are the same decision rather than two
    /// implementations of it. A hold with one mark commits nothing here on purpose: it has already
    /// acted, or it is not finished.
    /// </remarks>
    public TouchCommit Commit(DateTimeOffset now)
    {
        if (!TwoWay || HoldingSince is null)
        {
            return TouchCommit.Nothing;
        }

        var elapsed = Elapsed(now);

        return elapsed >= Hold ? TouchCommit.Shutdown
            : elapsed >= RestartAt!.Value ? TouchCommit.Restart
            : TouchCommit.Nothing;
    }
}

/// <summary>A source of finger-down and finger-up, opened once and drained.</summary>
/// <remarks>
/// <para>
/// <b>Drained rather than awaited, and that is what keeps the read cancellable.</b> A blocking
/// <c>read</c> on an idle evdev node parks a thread until somebody touches the screen, and nothing
/// short of a touch releases it — so a frame shutting down would leave a thread inside the kernel.
/// The node is opened non-blocking instead and asked what it has; on an idle panel that is one
/// syscall answering <c>EAGAIN</c>.
/// </para>
/// <para>
/// It reports the <i>state</i> rather than the events, because the only question above it is
/// whether a finger is down. A tap that arrives and leaves inside one poll window collapses to "up",
/// which is correct: a tap is not a hold.
/// </para>
/// </remarks>
public interface ITouchReader : IDisposable
{
    /// <summary>
    /// The touch state after draining whatever was pending, or null if nothing changed.
    /// </summary>
    /// <exception cref="IOException">The device went away.</exception>
    bool? Drain();
}

/// <summary>
/// How the agent finds the panel's touchscreen and hears it — §2.7 item 9, decision 77.
/// </summary>
/// <remarks>
/// A seam for the same reason every other Linux surface in the agent has one: there is no evdev on
/// a workstation, and the whole of the retry-at-the-frame behaviour — the hold, its progress, the
/// sentence the console prints when there is no touchscreen at all — has to be assertable without
/// a Pi.
/// </remarks>
public interface ITouchInput
{
    /// <summary>The panel's touchscreen, or null when this machine has none.</summary>
    TouchDevice? Find();

    /// <summary>Opens <paramref name="device"/> for reading, or null if it cannot be opened.</summary>
    ITouchReader? Open(TouchDevice device);
}

/// <summary>
/// The real touchscreen: <c>/proc/bus/input/devices</c> to find it, evdev to read it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by capability, not by path.</b> The measured frame answers on <c>/dev/input/event4</c>
/// with a stable alias at <c>/dev/input/by-path/platform-1f00080000.i2c-event</c>, and neither is
/// worth hard-coding: the event number moves with probe order and the by-path name is the I²C
/// address of one particular board. What identifies a touchscreen is what it can do, and the kernel
/// publishes exactly that — so this reads the capability bitmaps and takes the device that has
/// <c>INPUT_PROP_DIRECT</c>, absolute axes and <c>BTN_TOUCH</c>. A frame with a different panel, or
/// with the panel on a different bus, is found by the same rule.
/// </para>
/// <para>
/// <b>Neither <c>evtest</c> nor <c>libinput</c> is installed on a frame</b> — measured — and
/// neither becomes a dependency here. §2.7's console stage exists to work "from the first second of
/// the first boot, no login session, no dependencies", and an affordance on that screen that needed
/// a package would be offered exactly when the package might not be there yet.
/// </para>
/// <para>
/// <b>What was measured, and what was not.</b> From the frame: the device exists, its
/// <c>PROP</c> bitmap has <c>INPUT_PROP_DIRECT</c> (so it is a touchscreen and not a touchpad), its
/// <c>EV</c> bitmap has <c>EV_KEY</c> and <c>EV_ABS</c>, its <c>KEY</c> bitmap has
/// <c>BTN_TOUCH</c>, its <c>ABS</c> bitmap has <c>ABS_X</c>, <c>ABS_Y</c> and the multitouch
/// position and slot axes, <c>udev</c> tags it <c>ID_INPUT_TOUCHSCREEN=1</c>, the node opens
/// read-only cleanly, and it answers <c>EAGAIN</c> when idle. What is deliberately <i>not</i>
/// depended on anywhere here is the axes' ranges: nothing in this class reads a coordinate, so
/// nothing has to be true about them.
/// </para>
/// </remarks>
public sealed partial class EvdevTouchInput : ITouchInput
{
    /// <summary>Where the kernel lists every input device and what it can do.</summary>
    public const string DevicesPath = "/proc/bus/input/devices";

    /// <summary>Where the event nodes live.</summary>
    public const string NodeDirectory = "/dev/input/";

    /// <summary><c>INPUT_PROP_DIRECT</c> — a screen you touch, rather than a pad you point with.</summary>
    private const int PropertyDirect = 1;

    /// <summary><c>EV_KEY</c>.</summary>
    private const int EventKey = 0x01;

    /// <summary><c>EV_ABS</c>.</summary>
    private const int EventAbsolute = 0x03;

    /// <summary><c>BTN_TOUCH</c>, <c>0x14a</c> — "something is touching me", and the whole of what is read.</summary>
    private const int ButtonTouch = 0x14a;

    private readonly ITextFileReader _files;
    private readonly IAgentLog _log;

    /// <summary>Creates an instance reading <paramref name="files"/>.</summary>
    public EvdevTouchInput(ITextFileReader files, IAgentLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(files);

        _files = files;
        _log = log ?? NullLog.Instance;
    }

    /// <inheritdoc/>
    public TouchDevice? Find() => TouchscreenIn(_files.ReadAllTextOrNull(DevicesPath));

    /// <summary>
    /// The touchscreen in a <c>/proc/bus/input/devices</c> reading, or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Public and static because it is the only half of this class that can be asserted off a
    /// frame, and it is the half with a judgement in it. The file is blank-line-separated blocks of
    /// <c>K: value</c> lines; the capability bitmaps on the <c>B:</c> lines are space-separated
    /// 64-bit hex words written <b>most significant first</b>, so bit <i>n</i> lives in the word
    /// counted from the right.
    /// </para>
    /// <para>
    /// The reSpeaker array on the same frame publishes three input devices of its own and the two
    /// HDMI CEC receivers publish one each, all with <c>EV_KEY</c> — so the test cannot be "has
    /// keys". Requiring <c>INPUT_PROP_DIRECT</c> and absolute axes as well is what leaves exactly
    /// the panel.
    /// </para>
    /// </remarks>
    public static TouchDevice? TouchscreenIn(string? devicesText)
    {
        if (string.IsNullOrWhiteSpace(devicesText))
        {
            return null;
        }

        string? name = null;
        string? node = null;
        var direct = false;
        var keys = false;
        var axes = false;
        var touch = false;

        foreach (var raw in devicesText.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ', '\t');

            if (line.Length == 0)
            {
                if (Qualifies())
                {
                    return new TouchDevice(node!, name ?? "touchscreen");
                }

                name = null;
                node = null;
                direct = keys = axes = touch = false;
                continue;
            }

            if (line.StartsWith("N: Name=", StringComparison.Ordinal))
            {
                name = line["N: Name=".Length..].Trim('"');
            }
            else if (line.StartsWith("H: Handlers=", StringComparison.Ordinal))
            {
                node = EventNodeIn(line["H: Handlers=".Length..]);
            }
            else if (line.StartsWith("B: PROP=", StringComparison.Ordinal))
            {
                direct = HasBit(line["B: PROP=".Length..], PropertyDirect);
            }
            else if (line.StartsWith("B: EV=", StringComparison.Ordinal))
            {
                var bitmap = line["B: EV=".Length..];
                keys = HasBit(bitmap, EventKey);
                axes = HasBit(bitmap, EventAbsolute);
            }
            else if (line.StartsWith("B: KEY=", StringComparison.Ordinal))
            {
                touch = HasBit(line["B: KEY=".Length..], ButtonTouch);
            }
        }

        return Qualifies() ? new TouchDevice(node!, name ?? "touchscreen") : null;

        bool Qualifies() => direct && keys && axes && touch && node is { Length: > 0 };
    }

    /// <summary>
    /// Whether <paramref name="bitmap"/> has <paramref name="bit"/> set.
    /// </summary>
    /// <remarks>
    /// The kernel prints these most-significant word first, which is the one thing about this
    /// format that is easy to get backwards: <c>KEY=400 0 0 0 2000000000000001 f800000000000000</c>
    /// has <c>BTN_TOUCH</c> (330) in the <i>leftmost</i> word, because that word covers bits 320 to
    /// 383. Reading it the other way finds nothing and a frame silently grows no button.
    /// </remarks>
    public static bool HasBit(string bitmap, int bit)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentOutOfRangeException.ThrowIfNegative(bit);

        var words = bitmap.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var index = words.Length - 1 - (bit / 64);

        return index >= 0
            && index < words.Length
            && ulong.TryParse(words[index], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var word)
            && (word & (1UL << (bit % 64))) != 0;
    }

    /// <summary>The <c>eventN</c> node in a <c>Handlers=</c> value, as a device path, or null.</summary>
    public static string? EventNodeIn(string handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        foreach (var handler in handlers.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (handler.StartsWith("event", StringComparison.Ordinal)
                && handler.Length > 5
                && int.TryParse(handler.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return NodeDirectory + handler;
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public ITouchReader? Open(TouchDevice device)
    {
        if (!OperatingSystem.IsLinux() || device.Node is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            var descriptor = OpenDevice(device.Node, ReadOnly | NonBlocking | CloseOnExec, 0);
            if (descriptor < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                _log.Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The touchscreen at {device.Node} would not open (errno {errno}); there is no retry button on this frame's own screen."));
                return null;
            }

            return new EvdevReader(new SafeFileHandle(descriptor, ownsHandle: true), descriptor);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // No libc to ask. Nothing else in the agent can read evdev either, so the honest
            // outcome is a frame that says its screen has no touch rather than one that pretends.
            _log.Warn($"The touchscreen could not be opened ({exception.Message}); this frame offers no retry on its own screen.");
            return null;
        }
    }

    private const int ReadOnly = 0x0;

    /// <summary><c>O_NONBLOCK</c>, <c>04000</c> octal — identical on x86-64 and arm64.</summary>
    private const int NonBlocking = 0x800;

    /// <summary><c>O_CLOEXEC</c>, <c>02000000</c> octal — identical on x86-64 and arm64.</summary>
    private const int CloseOnExec = 0x80000;

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenDevice(string path, int flags, int mode);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static partial nint ReadDevice(int descriptor, ref byte buffer, nuint count);

    /// <summary>One open evdev node, read non-blocking.</summary>
    private sealed class EvdevReader : ITouchReader
    {
        /// <summary>
        /// <c>sizeof(struct input_event)</c> on 64-bit Linux: two 8-byte time fields, then
        /// <c>__u16 type</c>, <c>__u16 code</c> and <c>__s32 value</c>.
        /// </summary>
        private const int EventSize = 24;

        private const int TypeOffset = 16;
        private const int CodeOffset = 18;
        private const int ValueOffset = 20;

        /// <summary><c>EV_KEY</c>.</summary>
        private const ushort TypeKey = 0x01;

        /// <summary><c>BTN_TOUCH</c>.</summary>
        private const ushort CodeTouch = 0x14a;

        /// <summary><c>EAGAIN</c> — nothing pending, which is the ordinary answer.</summary>
        private const int WouldBlock = 11;

        private readonly SafeFileHandle _handle;
        private readonly int _descriptor;
        private readonly byte[] _buffer = new byte[EventSize * 32];
        private bool _disposed;

        public EvdevReader(SafeFileHandle handle, int descriptor)
        {
            _handle = handle;
            _descriptor = descriptor;
        }

        public bool? Drain()
        {
            bool? state = null;

            while (!_disposed)
            {
                // The raw descriptor is used rather than the SafeFileHandle's, because this is the
                // one place a p/invoke needs it and the handle is only ever closed by Dispose on
                // the same loop that calls this.
                var read = ReadDevice(_descriptor, ref MemoryMarshal.GetReference(_buffer.AsSpan()), (nuint)_buffer.Length);

                if (read <= 0)
                {
                    var error = Marshal.GetLastPInvokeError();

                    // EAGAIN is the idle answer and is not a fault. Anything else is the device
                    // going away — a panel unplugged, a driver unloaded — which the caller has to
                    // hear about, because the alternative is a screen that silently stops
                    // responding to the only affordance it offers.
                    return read < 0 && error != WouldBlock
                        ? throw new IOException(string.Create(
                            CultureInfo.InvariantCulture,
                            $"reading the touchscreen failed with errno {error}"))
                        : state;
                }

                for (var offset = 0; offset + EventSize <= read; offset += EventSize)
                {
                    var span = _buffer.AsSpan(offset, EventSize);

                    if (BitConverter.ToUInt16(span[TypeOffset..]) != TypeKey
                        || BitConverter.ToUInt16(span[CodeOffset..]) != CodeTouch)
                    {
                        continue;
                    }

                    // 0 is up, 1 is down, 2 is autorepeat — which a touchscreen does not emit and
                    // which would mean "still down" if it did.
                    state = BitConverter.ToInt32(span[ValueOffset..]) != 0;
                }

                // A short read means the kernel had nothing more to give, so there is no point
                // asking again for an EAGAIN.
                if (read < _buffer.Length)
                {
                    return state;
                }
            }

            return state;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _handle.Dispose();
        }
    }
}
