using System.Globalization;
using System.Text;
using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Firmware;

/// <summary>
/// <b>The window a DFU write is open for, and the durable mark it leaves while it is.</b>
/// </summary>
/// <remarks>
/// <para>
/// A firmware write is the one operation on this frame that nothing else may interrupt and that
/// nothing on this frame can undo. Three separate mechanisms need to know it is happening, and none
/// of them is in a position to ask the code doing it:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>The agent's own self-update.</b> §2.8 swaps the binary hourly and asks the process to
/// restart; <c>fl-agent.service</c> deliberately leaves <c>KillMode</c> at systemd's default
/// <c>control-group</c>, so that restart <c>SIGKILL</c>s every child in the cgroup — and
/// <c>dfu-util</c> is a child in the cgroup. This is the single most likely way a real flash gets
/// killed, it is entirely internal to this product, and nothing guarded it before.
/// </description></item>
/// <item><description>
/// <b>The reconcile loop's reboot.</b> §2.4 crosses a reboot after every Act, with no per-resource
/// opt-out and no intention of ever having one. The flash is deliberately not a resource, so no Act
/// of its own can trigger that — but some <i>other</i> resource's Act, on a pass that happens to be
/// running, would take the machine down mid-write. <c>RebootHold</c> refuses on the boundary
/// instead, which is an outcome §2.4 already has a first-class answer for.
/// </description></item>
/// <item><description>
/// <b>The bench harness's power switch.</b> <c>fl.py power</c> can cut the frame's mains, and mains
/// loss mid-write is unguardable at the device. A harness-side refusal is the only mitigation
/// available, and it needs something on the frame to read — which is what the durable marker below
/// is for. That refusal is not in this repository's agent half; it is named in decision 91 as the
/// one interlock that lives on the workstation.
/// </description></item>
/// </list>
/// <para>
/// <b>The marker is durable and is deliberately not self-clearing.</b> It is written with
/// <see cref="IStateStore.WriteSecretAtomic"/> before the device is touched and removed in a
/// <c>finally</c> afterwards, so a marker still present when a <i>new</i> agent process starts means
/// exactly one thing: a flash began and the process that began it did not live to finish it. That
/// is the state nothing can diagnose after the fact — a cgroup kill, a power cut and a crash all
/// leave the same array behind — so <see cref="Interrupted"/> latches at construction, is reported
/// loudly, and is cleared only by a person deleting the file. An agent that cleared it itself would
/// be free to start a second flash onto an array whose Upgrade partition is in an unknown state,
/// which is precisely the "retrying a partial write" that turns a recoverable board into an
/// unrecoverable one.
/// </para>
/// </remarks>
public sealed class ArrayFlashWindow
{
    /// <summary>The marker file, inside the agent's state directory.</summary>
    public const string MarkerFileName = "array-flash.inprogress";

    private readonly IStateStore _store;
    private readonly IAgentClock _clock;
    private readonly Lock _gate = new();

    private string? _detail;

    /// <summary>Creates the window and latches whether a previous flash was left unfinished.</summary>
    public ArrayFlashWindow(IStateStore store, IAgentClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);

        _store = store;
        _clock = clock;

        // Read once, here, and never re-read. The whole value of this flag is that it describes a
        // *previous* process: recomputing it later would clear itself the moment this process
        // opened and closed a window of its own.
        Interrupted = store.Exists(MarkerFileName);
        InterruptedDetail = Interrupted ? store.ReadText(MarkerFileName)?.Trim() : null;
    }

    /// <summary>A flash was in progress when the previous agent process ended.</summary>
    public bool Interrupted { get; }

    /// <summary>What that unfinished flash was doing, as it recorded itself.</summary>
    public string? InterruptedDetail { get; }

    /// <summary>Whether a flash is being performed right now, by this process.</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _detail is not null;
            }
        }
    }

    /// <summary>
    /// Why nothing may restart, reboot or cut power right now, or null when the window is shut.
    /// </summary>
    /// <remarks>
    /// A whole sentence, because it becomes a refused-reboot detail on the frame's own screen and
    /// in the operator's notification — the same requirement decision 79 puts on the reboot floor's
    /// refusal string.
    /// </remarks>
    public string? Reason
    {
        get
        {
            lock (_gate)
            {
                return _detail is null
                    ? null
                    : "the microphone unit's firmware is being written right now, and interrupting "
                        + "that is the one thing on this frame that cannot be undone (" + _detail + ")";
            }
        }
    }

    /// <summary>Opens the window and writes the marker. Disposing it closes both.</summary>
    /// <exception cref="InvalidOperationException">One is already open in this process.</exception>
    public IDisposable Open(string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);

        lock (_gate)
        {
            if (_detail is not null)
            {
                throw new InvalidOperationException("A firmware flash window is already open.");
            }

            _detail = detail;
        }

        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{_clock.UtcNow:O} {detail}\n");

        // Atomic and fsynced, because the reader that matters is the next process on a machine that
        // lost power in the middle of this.
        _store.WriteSecretAtomic(MarkerFileName, Encoding.UTF8.GetBytes(text));

        return new Scope(this);
    }

    private void Close()
    {
        lock (_gate)
        {
            _detail = null;
        }

        _store.Delete(MarkerFileName);
    }

    private sealed class Scope : IDisposable
    {
        private ArrayFlashWindow? _window;

        public Scope(ArrayFlashWindow window) => _window = window;

        public void Dispose()
        {
            Interlocked.Exchange(ref _window, null)?.Close();
        }
    }
}
