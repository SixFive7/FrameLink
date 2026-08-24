using System.Diagnostics;
using System.Globalization;
using FrameLink.Agent.Hosting;
using FrameLink.Protocol;

namespace FrameLink.Agent.Firmware;

/// <summary>
/// How far one firmware write has got, as the frame currently understands it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Immutable, and replaced wholesale rather than mutated.</b> It is handed between three threads
/// that must not wait on each other — the one draining <c>dfu-util</c>'s output, the one publishing
/// to the frame's screen and the Fleet Manager, and the one running the write — and a record swapped
/// by a single reference write cannot be read half-updated by any of them. There is no lock anywhere
/// on this path for the same reason: a lock is something the writer could end up waiting on.
/// </para>
/// <para>
/// <b>The total is known before <c>dfu-util</c> says anything</b>, because it is the pinned image's
/// own length, measured and recorded in the pin. So a bar is correct from the first byte rather than
/// from the tool's first printed percentage.
/// </para>
/// </remarks>
public sealed record ArrayFlashProgress
{
    /// <summary>One of <see cref="ArrayFlashStages"/>.</summary>
    public required string Stage { get; init; }

    /// <summary>The tool's own printed percentage, or null before it has printed one.</summary>
    public int? Percent { get; init; }

    /// <summary>How many bytes the tool says it has sent, or null.</summary>
    public long? BytesWritten { get; init; }

    /// <summary>The pinned image's length, from the pin rather than from the tool.</summary>
    public long BytesTotal { get; init; }

    /// <summary>How long the write has been running, measured off a monotonic clock of its own.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>
    /// The last line from <c>dfu-util</c> that moved the stage on, verbatim and trimmed.
    /// </summary>
    /// <remarks>
    /// Kept because the operator's fallback position — if a bar turns out not to be possible on real
    /// hardware — is <i>whatever the flash program says</i>, and this is that, unedited. It reaches
    /// the technical surfaces only: the frame's own screen is read by a family member and shows the
    /// stage in words.
    /// </remarks>
    public string? Line { get; init; }

    /// <summary>The fraction a bar should be filled to, or null when nothing is measurable.</summary>
    public double? Fraction =>
        !string.Equals(Stage, ArrayFlashStages.Downloading, StringComparison.Ordinal) ? null
        : BytesTotal > 0 && BytesWritten is { } written ? Math.Clamp((double)written / BytesTotal, 0, 1)
        : Percent is { } percent ? Math.Clamp(percent / 100d, 0, 1)
        : null;

    /// <summary>
    /// What changes here is worth telling somebody about.
    /// </summary>
    /// <remarks>
    /// <b>Bytes are deliberately not in it.</b> <c>dfu-util</c> rewrites its bar once per transfer
    /// block — 4 KB against a 933 KB image is 228 redraws — and publishing on each would repaint the
    /// console, re-render the page and put a message on the wire eight times a second for the whole
    /// write. The percentage moves 101 times at most and the second hand once a second, which is a
    /// bar that looks continuous to a person and costs a fraction as much. The exact byte count
    /// still rides on every update that is sent; it just does not by itself cause one.
    /// </remarks>
    public string Signature => string.Create(
        CultureInfo.InvariantCulture,
        $"{Stage}|{Percent}|{(int)Elapsed.TotalSeconds}");

    /// <summary>This progress as the self-report carries it (<see cref="ArrayFlashWire"/>).</summary>
    /// <param name="screen">The firmware screen the frame is showing, by name.</param>
    public ArrayFlashWireStatus ToWire(string screen) => new()
    {
        Screen = screen,
        Stage = Stage,
        Percent = Percent,
        BytesWritten = BytesWritten,
        BytesTotal = BytesTotal > 0 ? BytesTotal : null,
        ElapsedSeconds = (int)Elapsed.TotalSeconds,
    };
}

/// <summary>
/// <b>The one place <c>dfu-util</c>'s output is understood</b>, and a slot holding the newest
/// reading of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This runs on the thread draining the child's pipes, so everything it does is bounded.</b> A
/// scan of one short line, a couple of integer parses, and a single reference write. No lock, no
/// allocation that grows with the write, no I/O, and nothing that can wait on another thread. That
/// is not a style preference: <c>dfu-util</c>'s stdout is a pipe with a fixed kernel buffer, and a
/// reader that stops reading stops the writer — so anything slow here would reach through the pipe
/// and stall the write itself.
/// </para>
/// <para>
/// <b>Newest wins, and dropping is correct.</b> There is no queue: a reading that arrives while the
/// last one is still being published simply replaces it. A partial write is dangerous and a partial
/// report is not, so every trade-off between them resolves this way — a reader who misses the frame
/// where the bar said 41% and sees 43% instead has lost nothing.
/// </para>
/// <para>
/// <b>The shape it reads is upstream's, not ours.</b> No capture of a <c>dfu-util</c> download
/// exists anywhere in this repository, on any array, at any version —
/// <c>reference/xvf3800-upgrade-path.md</c> records that gap explicitly, and nothing here changes
/// it. What is encoded below is the published shape of <c>dfu-util</c>'s progress bar and its state
/// lines, and the parse is deliberately tolerant of everything it does not need: whitespace, the
/// bar's own fill characters, a missing byte count, a label this build has never seen, and lines it
/// cannot make sense of at all. A line it cannot read leaves the stage exactly where it was, which
/// is why an unfamiliar build of the tool degrades to <i>no bar</i> rather than to a wrong one.
/// </para>
/// </remarks>
public sealed class ArrayFlashProgressBox
{
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ArrayFlashProgress _current;
    private int _advances;

    /// <summary>Creates a box for an image of <paramref name="bytesTotal"/> bytes.</summary>
    public ArrayFlashProgressBox(long bytesTotal) =>
        _current = new ArrayFlashProgress { Stage = ArrayFlashStages.Preparing, BytesTotal = bytesTotal };

    /// <summary>The newest reading. Never null, never throws, never blocks.</summary>
    public ArrayFlashProgress Current => Volatile.Read(ref _current);

    /// <summary>How many segments have moved the reading on. For the suite.</summary>
    public int Advances => Volatile.Read(ref _advances);

    /// <summary>
    /// Takes one segment of the tool's output.
    /// </summary>
    /// <param name="segment">One line, or one carriage-return-delimited redraw of the bar.</param>
    /// <remarks>
    /// <b>Never throws.</b> The caller is a pipe drain, and an exception there would end the drain,
    /// fill the pipe and stall <c>dfu-util</c> — so a segment this cannot make sense of is dropped
    /// in silence rather than reported. The tool's whole output reaches the event trail verbatim
    /// regardless of what this made of it.
    /// </remarks>
    public void Read(string? segment)
    {
        try
        {
            if (segment is null)
            {
                return;
            }

            var text = segment.Trim();
            if (text.Length == 0)
            {
                return;
            }

            var current = Volatile.Read(ref _current);

            if (ReadBar(text) is { } bar)
            {
                Advance(current, ArrayFlashStages.Downloading, text, bar.Percent, bar.Bytes);
                return;
            }

            // Substring rather than equality, and ordinal-ignore-case, because every one of these
            // lines carries a device id, a state number or a status sentence beside the word being
            // looked for, and none of those is fixed.
            if (Says(text, "Copying data from PC to DFU device"))
            {
                Advance(current, ArrayFlashStages.Downloading, text, 0, 0);
                return;
            }

            if (Says(text, "Download done") || Says(text, "File downloaded successfully"))
            {
                Advance(current, ArrayFlashStages.Downloading, text, 100, current.BytesTotal);
                return;
            }

            if (Says(text, "dfuMANIFEST"))
            {
                Advance(current, ArrayFlashStages.Manifesting, text, null, null);
                return;
            }

            // dfuIDLE is printed twice — once before the download, while the tool is determining the
            // device status, and once after the manifest. Only the second one means anything, so it
            // is read as a stage at all once the write has actually reached the manifest. The
            // monotonic guard below cannot catch this on its own: settling sorts *after* preparing,
            // so a plain reading would jump the write to its last-but-two stage before it started.
            if (Says(text, "dfuIDLE"))
            {
                if (ArrayFlashStages.Order(current.Stage) >= ArrayFlashStages.Order(ArrayFlashStages.Manifesting))
                {
                    Advance(current, ArrayFlashStages.Settling, text, null, null);
                }

                return;
            }

            if (Says(text, "Resetting USB"))
            {
                Advance(current, ArrayFlashStages.Resetting, text, null, null);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // Deliberately silent, and deliberately total. This is the drain of a pipe whose far end
            // is writing an array's flash; there is no failure here worth risking that for.
        }
    }

    /// <summary>Moves the reading on to a stage this product owns rather than the tool.</summary>
    /// <remarks>
    /// The two stages after <c>dfu-util</c> exits — waiting for the unit to come back on the bus,
    /// and asking the control tool for a second reading — are the frame's own, and they are half of
    /// why the stage is modelled at all. Without them a bar reaches 100% and then nothing happens
    /// for up to ninety seconds, which reads as a hang to the one person who must not conclude that.
    /// </remarks>
    public void Enter(string stage) => Advance(Volatile.Read(ref _current), stage, null, null, null);

    /// <summary>
    /// Waits until the reading changes or <paramref name="patience"/> passes.
    /// </summary>
    /// <returns>Whether a change woke it, as opposed to the patience running out.</returns>
    /// <remarks>
    /// The timeout is what keeps the elapsed seconds moving on the screen through a stage that
    /// prints nothing at all — the manifest, which is exactly the stage a person would otherwise
    /// watch a still bar through and conclude had hung.
    /// </remarks>
    public async Task<bool> ChangedAsync(TimeSpan patience, CancellationToken cancellationToken)
    {
        try
        {
            await Volatile.Read(ref _wake).Task.WaitAsync(patience, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static bool Says(string text, string phrase) =>
        text.Contains(phrase, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads <c>Download\t[=====] 41% 380928 bytes</c> — upstream's shape, tolerantly.
    /// </summary>
    /// <remarks>
    /// Hand-written rather than a regular expression, because this runs on a pipe drain and the
    /// cheapest possible scan is the one that cannot become the reason a write stalled. The byte
    /// count is optional: older builds of the tool draw the bar without one, and a percentage on its
    /// own is still a bar.
    /// </remarks>
    private static (int Percent, long? Bytes)? ReadBar(string text)
    {
        var open = text.IndexOf('[', StringComparison.Ordinal);
        if (open < 0)
        {
            return null;
        }

        var close = text.IndexOf(']', open);
        if (close < 0)
        {
            return null;
        }

        var index = close + 1;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        var digits = index;
        while (index < text.Length && char.IsAsciiDigit(text[index]))
        {
            index++;
        }

        if (index == digits
            || index >= text.Length
            || text[index] != '%'
            || !int.TryParse(text.AsSpan(digits, index - digits), CultureInfo.InvariantCulture, out var percent))
        {
            return null;
        }

        index++;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        var counted = index;
        while (index < text.Length && char.IsAsciiDigit(text[index]))
        {
            index++;
        }

        return index > counted
            && long.TryParse(text.AsSpan(counted, index - counted), CultureInfo.InvariantCulture, out var bytes)
                ? (percent, bytes)
                : (percent, null);
    }

    private void Advance(
        ArrayFlashProgress current,
        string stage,
        string? line,
        int? percent,
        long? bytes)
    {
        // Monotonic. A write only ever moves forward: the tool's state lines are not ordered
        // evidence on their own, and a screen that stepped back to "preparing" half way through
        // would say the write had restarted, which is the one thing it must never imply.
        if (ArrayFlashStages.Order(stage) < ArrayFlashStages.Order(current.Stage))
        {
            return;
        }

        Volatile.Write(
            ref _current,
            current with
            {
                Stage = stage,
                Percent = percent ?? current.Percent,
                BytesWritten = bytes ?? current.BytesWritten,
                Line = line ?? current.Line,
            });

        Interlocked.Increment(ref _advances);

        // Swapped rather than completed in place, so the next waiter gets a fresh task and a burst
        // of redraws collapses into one wake. Nothing here blocks and nothing here is disposable,
        // which is what keeps the drain free of anything the publisher could hold.
        Interlocked.Exchange(ref _wake, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult();
    }
}

/// <summary>
/// <b>The whole of the isolation between reporting a write and performing one</b>, in one object.
/// </summary>
/// <remarks>
/// <para>
/// The operator's instruction was explicit and it is the load-bearing requirement of the feature: a
/// dead network, a hung socket, a Fleet Manager that stopped answering or a screen that will not take
/// a repaint must leave the flash completely unaffected. Four properties enforce that, and each is a
/// structural fact rather than a promise about care taken.
/// </para>
/// <para>
/// <b>1. A different thread.</b> The write runs on the flash's own task and this runs on a task
/// started before it, and the writer never awaits this one — not at the start, not at the end, not
/// on <see cref="Dispose"/>. Every publish this makes, the very first included, happens here.
/// <c>AgentStatusHub.Publish</c> calls its subscribers <i>synchronously, on the publisher's
/// thread</i>, and one of those subscribers writes a frame to <c>/dev/tty8</c> while another sends
/// one to the browser — so a publish from the writing thread would put a blocking device write
/// between <c>dfu-util</c> and the drain of its own pipe.
/// </para>
/// <para>
/// <b>2. A different cancellation token.</b> This owns a source of its own, created here and passed
/// to nothing else. Cancelling it stops the reporting and reaches no part of the write; and there is
/// no path by which the write's token can be reached from here, because this is never given one.
/// </para>
/// <para>
/// <b>3. A different clock.</b> Elapsed time comes off a <see cref="Stopwatch"/> started here, and
/// the beat is a plain timer. Nothing on this path reads or advances <see cref="IAgentClock"/>,
/// which is what the write's re-enumeration deadline is measured against — so a reporting loop
/// cannot spend the write's patience, which is the subtlest way this could have reached through and
/// changed an outcome.
/// </para>
/// <para>
/// <b>4. No backpressure, ever.</b> There is no queue and nothing bounded to fill:
/// <see cref="ArrayFlashProgressBox"/> holds exactly one reading and the newest replaces the last.
/// A publish that hangs for the whole write costs the stale frames it was going to draw and nothing
/// else. Every publish is wrapped, so an exception from the screen, from the hub, or from anything
/// downstream of them is swallowed here rather than travelling anywhere.
/// </para>
/// <para>
/// <b>What that costs, said plainly.</b> A publish that never returns leaks this one task for the
/// life of the process, because <see cref="Dispose"/> refuses to wait for it. That is the deliberate
/// trade and it is the right way round: a partial write can destroy an array somebody has to travel
/// to recover, and an abandoned task cannot.
/// </para>
/// </remarks>
public sealed class ArrayFlashProgressPump : IDisposable
{
    /// <summary>How long a beat waits when nothing at all is arriving.</summary>
    /// <remarks>
    /// It exists for the manifest, which prints one line and then commits the image to the unit's
    /// own flash in silence. The elapsed seconds on the screen are what say that a still bar is a
    /// wait rather than a hang, and they need a beat to move.
    /// </remarks>
    public static TimeSpan DefaultBeat { get; } = TimeSpan.FromSeconds(1);

    private readonly ArrayFlashApproval _approval;
    private readonly ArrayFlashProgressBox _box;
    private readonly IAgentLog _log;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly CancellationTokenSource _stopping = new();
    private readonly long _epoch;
    private readonly TimeSpan _beat;

    private string? _published;

    private ArrayFlashProgressPump(
        ArrayFlashApproval approval,
        ArrayFlashProgressBox box,
        IAgentLog log,
        long epoch,
        TimeSpan beat)
    {
        _approval = approval;
        _box = box;
        _log = log;
        _epoch = epoch;
        _beat = beat;
    }

    /// <summary>How many frames this has actually put on the screen.</summary>
    public int Published { get; private set; }

    /// <summary>
    /// Starts reporting a write, and returns before anything has been reported.
    /// </summary>
    /// <param name="approval">The frame's screen, which is also the epoch's owner.</param>
    /// <param name="box">Where the newest reading lands.</param>
    /// <param name="log">Where a publish that failed is noted, once.</param>
    /// <param name="beat">How long a beat waits with nothing arriving.</param>
    /// <param name="attempt">
    /// Which of <see cref="ArrayFirmwareFlash.MaxAttempts"/> writes this is, counting from one.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>It takes the epoch here and publishes nothing here.</b> Claiming the screen is a lock and
    /// an increment — bounded, and safe on the writing thread — and every publish that follows, the
    /// first one included, happens on the task this starts. That is what makes it true that the
    /// writing thread never calls into the hub for the whole of the write.
    /// </para>
    /// <para>
    /// <b>The attempt number goes to the screen and not into the reading.</b> A retry restarts the
    /// bar at nothing, and a bar that resets with no words beside it says the frame has hung — which
    /// is what a person reaches for the plug over. It is carried by the screen's owner rather than by
    /// <see cref="ArrayFlashProgress"/> because the wire status is a frozen protocol contract and
    /// this is a sentence rather than a measurement.
    /// </para>
    /// </remarks>
    public static ArrayFlashProgressPump Start(
        ArrayFlashApproval approval,
        ArrayFlashProgressBox box,
        IAgentLog log,
        TimeSpan? beat = null,
        int attempt = 1)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(box);
        ArgumentNullException.ThrowIfNull(log);

        var pump = new ArrayFlashProgressPump(approval, box, log, approval.BeginWriting(attempt), beat ?? DefaultBeat);

        // Discarded on purpose. Nothing waits on this task — not the caller, not Dispose — because
        // anything that waited on it would be a way for the screen to reach the write.
        _ = Task.Run(pump.RunAsync, CancellationToken.None);

        return pump;
    }

    /// <summary>Composes the current reading and puts it on the screen, if it has changed.</summary>
    /// <returns>Whether a frame was published.</returns>
    /// <remarks>
    /// Public so the suite can drive one frame at a time with no task behind it. In the agent it is
    /// called only from the loop this class runs.
    /// </remarks>
    public bool PublishOnce()
    {
        var progress = _box.Current with { Elapsed = _elapsed.Elapsed };
        var signature = progress.Signature;

        if (string.Equals(signature, _published, StringComparison.Ordinal))
        {
            return false;
        }

        _published = signature;
        _approval.Writing(_epoch, progress);
        Published++;
        return true;
    }

    /// <summary>Stops reporting. Returns immediately, whatever the reporting path is doing.</summary>
    public void Dispose()
    {
        try
        {
            _stopping.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already stopped.
        }

        // The source is deliberately not disposed and the task deliberately not awaited. A publish
        // that has hung holds this token; disposing the source under it, or waiting for it, would
        // put the writing thread behind the very thing this exists to keep it away from.
    }

    private async Task RunAsync()
    {
        var complained = false;

        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                PublishOnce();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                // Once per write, never once per beat. A screen that cannot be painted would
                // otherwise fill the journal at a line a second for the whole write, and the journal
                // is where somebody looks to find out what happened to the array.
                if (!complained)
                {
                    complained = true;
                    _log.Warn(
                        "The firmware write is running and its progress could not be put on the screen: "
                        + exception.Message
                        + ". The write itself is unaffected — nothing on the reporting path can reach it — and its "
                        + "outcome will be reported when it finishes.");
                }
            }

            try
            {
                await _box.ChangedAsync(_beat, _stopping.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                return;
            }
        }
    }
}
