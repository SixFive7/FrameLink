using System.Diagnostics;

namespace FrameLink.Agent.Hosting;

/// <summary>What running one command produced.</summary>
/// <param name="ExitCode">Process exit code, or -1 if it never started or was stopped unfinished.</param>
/// <param name="StandardOutput">Standard output, trimmed.</param>
/// <param name="StandardError">Standard error, trimmed.</param>
public readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>How long the command's output is allowed to be in one report before it is cut.</summary>
    private const int CommandLimit = 200;

    /// <summary>Whether the process exited zero.</summary>
    public bool Succeeded => ExitCode == 0;

    /// <summary>
    /// Whether this command was stopped for taking longer than its deadline rather than answering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A timeout is data here, not an exception, and that is the house convention rather than a
    /// preference.</b> A command that could not be started already comes back as
    /// <c>ExitCode == -1</c> with the reason in <see cref="StandardError"/>, so every caller in the
    /// agent already has a path for "it did not work and here is why" — and that path is what the
    /// reconcile pass turns into drift, an attempt and a row on the frame's own screen. Throwing
    /// would send a timeout somewhere else entirely: up out of the resource, past the ladder that
    /// renders it, and into whichever loop happened to be walking. The operator's decision was that
    /// a timeout fails the resource <i>like any other error</i>, and returning it as one is how that
    /// is true by construction instead of by a second code path.
    /// </para>
    /// <para>
    /// <b>The loops beside the pass need the opposite, and they get it explicitly.</b> They have no
    /// attempt ledger of their own, so a timeout they simply retried would repeat for ever with
    /// nothing escalating; <see cref="ProcessTimeoutException.ThrowIfTimedOut"/> is how those call
    /// sites convert this flag into the failure their supervisor already knows how to record.
    /// </para>
    /// </remarks>
    public bool TimedOut { get; init; }

    /// <summary>The deadline that was exceeded, when <see cref="TimedOut"/> is set.</summary>
    public TimeSpan? Deadline { get; init; }

    /// <summary>Output and error together, for a log line or a delta.</summary>
    public string Combined =>
        StandardError.Length == 0 ? StandardOutput
        : StandardOutput.Length == 0 ? StandardError
        : StandardOutput + "\n" + StandardError;

    /// <summary>
    /// The result of a command that ran past <paramref name="deadline"/> and was stopped.
    /// </summary>
    /// <param name="executable">The program that was started.</param>
    /// <param name="arguments">Its argument vector, for the report.</param>
    /// <param name="deadline">The deadline it exceeded.</param>
    /// <param name="standardOutput">Whatever it had written to standard output before it stopped.</param>
    /// <param name="standardError">Whatever it had written to standard error before it stopped.</param>
    /// <param name="treeKilled">
    /// Whether the kill reached the whole tree. False means the direct child was stopped but
    /// something it had started could not be found — which is said out loud rather than assumed
    /// away, because a stray child holding a device is the next person's mystery.
    /// </param>
    /// <remarks>
    /// <b>The explanation goes in <see cref="StandardError"/> on purpose.</b> Every reporting path
    /// in the agent — the resource delta, the log line, the event trail, the row on the frame's own
    /// screen — reads <see cref="Combined"/>, so putting the sentence there means a timeout arrives
    /// at all of them with no reporting site changed at all. It carries the three things somebody
    /// debugging one needs: what was run, what it was allowed to take, and whatever it managed to
    /// say first.
    /// </remarks>
    public static ProcessResult DeadlineExceeded(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan deadline,
        string standardOutput,
        string standardError,
        bool treeKilled)
    {
        var command = Command(executable, arguments);
        var stopped = treeKilled
            ? "so it and every process it had started were stopped"
            : "so it was stopped, but something it had started could not be found and may have survived it";

        var explanation = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{command} did not answer within {ProcessDeadline.Describe(deadline)}, {stopped}.");

        return new ProcessResult(
            -1,
            standardOutput.Trim(),
            standardError.Trim() is { Length: > 0 } said ? said + "\n" + explanation : explanation)
        {
            TimedOut = true,
            Deadline = deadline,
        };
    }

    /// <summary>The command as one line, short enough to sit in a delta.</summary>
    /// <remarks>
    /// Cut rather than summarised, and cut at the end, because the front of the line is the part
    /// that identifies it: <see cref="IUserSession"/> reaches its target through <c>runuser</c> and
    /// four environment assignments, so the interesting words are late — but the length is bounded
    /// because this ends up on a 10-inch panel next to a sentence a person has to read.
    /// </remarks>
    internal static string Command(string executable, IReadOnlyList<string> arguments)
    {
        var line = arguments.Count == 0 ? executable : executable + " " + string.Join(' ', arguments);
        return line.Length <= CommandLimit ? line : line[..CommandLimit] + "…";
    }
}

/// <summary>
/// The agent's one way of running an external command.
/// </summary>
/// <remarks>
/// <para>
/// <b>The argument list is always compiled into the binary.</b> §2.2 is explicit that the Fleet
/// Manager supplies values, sequencing requests and allowlisted diagnostics — <i>never logic</i>
/// — because a server-driven executor needs a DSL or shell strings and turns the agent into a
/// root remote-execution proxy. So this interface takes an executable and an argument
/// <i>vector</i>, never a command line: there is no shell, no word splitting and no place for a
/// server-supplied string to become a second command. A fleet value may only ever land in one
/// argument slot the catalog chose.
/// </para>
/// <para>
/// It exists as a seam for the same reason every other Linux surface does: the agent runs for
/// real only on a frame (§1.1), and the whole of M2 has to be assertable on a workstation.
/// </para>
/// </remarks>
public interface IProcessRunner
{
    /// <summary>Runs <paramref name="executable"/> with <paramref name="arguments"/>.</summary>
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs it, and stops it if it has not answered within <paramref name="deadline"/>.
    /// </summary>
    /// <param name="executable">The program to start.</param>
    /// <param name="arguments">Its argument vector, compiled in as always.</param>
    /// <param name="deadline">
    /// How long this command may take, from <see cref="ProcessDeadline"/> — chosen by the call site,
    /// because the call site is where the command is known. There is no infinite value and no
    /// default: an unbounded wait on an external tool is the defect this parameter exists to remove.
    /// </param>
    /// <param name="cancellationToken">The caller's token, as before, and unrelated to the deadline.</param>
    /// <remarks>
    /// <para>
    /// <b>The whole tree goes, not just the child.</b> A child that started its own children and
    /// then exited leaves them holding the write end of the pipe, so the drain never sees end-of-file
    /// and a naive timeout on the child alone never fires at all — which is the exact shape that has
    /// already cost this project a night, and the reason this is a parameter on the runner rather
    /// than a <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> at every call site.
    /// </para>
    /// <para>
    /// <b>A deadline reached is a failure, not an exception.</b> The result comes back with
    /// <see cref="ProcessResult.TimedOut"/> set, a non-zero exit, whatever output arrived before the
    /// kill, and a sentence in <see cref="ProcessResult.StandardError"/> naming the command and the
    /// deadline — so it travels every path a failed command already travels and needs no reporting
    /// site to learn about it.
    /// </para>
    /// <para>
    /// <b>The default implementation ignores the deadline, and that is right for a double.</b> Every
    /// test double answers from a dictionary in microseconds, so there is nothing for a deadline to
    /// bound; making this abstract would have made five doubles implement a clock to no purpose. A
    /// double that wants to assert <i>which</i> deadline a call site chose overrides it and records
    /// the argument.
    /// </para>
    /// </remarks>
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan deadline,
        CancellationToken cancellationToken) => RunAsync(executable, arguments, cancellationToken);

    /// <summary>
    /// Runs it, and hands each segment of its output over as it arrives rather than at the end.
    /// </summary>
    /// <param name="executable">The program to start.</param>
    /// <param name="arguments">Its argument vector, compiled in as always.</param>
    /// <param name="onOutput">
    /// Called with one line — or one carriage-return-delimited redraw of a progress bar — per
    /// segment, on the thread draining the child's pipes. <b>It must return promptly and it must not
    /// do I/O.</b> A child's stdout is a pipe with a fixed kernel buffer, so a sink that blocks
    /// blocks the child; the sink this exists for is a scan of one short line into a single-slot
    /// box, and everything that can hang runs on a task downstream of that box.
    /// </param>
    /// <param name="cancellationToken">The caller's token, exactly as for the other overload.</param>
    /// <remarks>
    /// <para>
    /// <b>Only one command in this product needs it, and it is the one that cannot be watched any
    /// other way.</b> A DFU write takes between thirty seconds and two minutes, and the agent used
    /// to emit nothing at all between a household agreeing to it and the tool returning — so a write
    /// in progress and a frame that died mid-write were indistinguishable from the console. Every
    /// other command the agent runs answers in milliseconds and is read whole.
    /// </para>
    /// <para>
    /// <b>The default implementation reports nothing, deliberately.</b> Progress is a convenience and
    /// never a contract: an implementation that has no way to stream — every test double, and any
    /// future runner over a transport that buffers — falls back to the whole-output overload and the
    /// caller simply sees a write with no bar on it. Making this abstract would have made every
    /// unrelated double implement a pipe drain.
    /// </para>
    /// </remarks>
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        Action<string> onOutput,
        CancellationToken cancellationToken) => RunAsync(executable, arguments, cancellationToken);

    /// <summary>Streams its output, and stops it if it runs past <paramref name="deadline"/>.</summary>
    /// <param name="executable">The program to start.</param>
    /// <param name="arguments">Its argument vector, compiled in as always.</param>
    /// <param name="onOutput">The sink, under the same rules as the overload without a deadline.</param>
    /// <param name="deadline">How long this command may take, from <see cref="ProcessDeadline"/>.</param>
    /// <param name="cancellationToken">The caller's token, unrelated to the deadline.</param>
    /// <remarks>
    /// <b>Progress arriving is not the same question as the deadline.</b> This one exists for the
    /// firmware write, where the sink is drawing a bar from the tool's own output — so it is tempting
    /// to bound the gap between segments instead of the whole call. It is not bounded that way,
    /// because <c>dfu-util</c>'s bar redraws are the thing a stalled USB write stops producing
    /// <i>and</i> the thing a slow-but-healthy one produces irregularly; the whole-call bound is the
    /// one that cannot be wrong about which of those it is looking at.
    /// </remarks>
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        Action<string> onOutput,
        TimeSpan deadline,
        CancellationToken cancellationToken) => RunAsync(executable, arguments, onOutput, cancellationToken);
}

/// <summary>Starts real processes.</summary>
public sealed class HostProcessRunner : IProcessRunner
{
    /// <summary>The shared instance.</summary>
    public static HostProcessRunner Instance { get; } = new();

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Transitional, and the deadline it picks is the most generous real one there is.</b> Every
    /// call site in the agent is being moved to the overload that states its own deadline; until the
    /// last of them has, this routes through the same enforcement with
    /// <see cref="ProcessDeadline.PackageChange"/>, which is the bound <c>apt full-upgrade</c> itself
    /// gets. Nothing healthy on a frame reaches an hour, so this cannot cut a working command off —
    /// what it does is make sure no path is unbounded even by omission while the migration is
    /// half-done.
    /// </remarks>
    public Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        StreamAsync(executable, arguments, onOutput: null, ProcessDeadline.PackageChange, cancellationToken);

    /// <inheritdoc/>
    public Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan deadline,
        CancellationToken cancellationToken) =>
        StreamAsync(executable, arguments, onOutput: null, deadline, cancellationToken);

    /// <inheritdoc/>
    /// <remarks>Transitional, for the reason given on the overload with neither a sink nor a deadline.</remarks>
    public Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        Action<string> onOutput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onOutput);
        return StreamAsync(executable, arguments, onOutput, ProcessDeadline.PackageChange, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        Action<string> onOutput,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onOutput);
        return StreamAsync(executable, arguments, onOutput, deadline, cancellationToken);
    }

    private static async Task<ProcessResult> StreamAsync(
        string executable,
        IReadOnlyList<string> arguments,
        Action<string>? onOutput,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        // Timeout.InfiniteTimeSpan is -1 milliseconds and lands here as a negative, which is the
        // point: there is deliberately no way to spell "wait for ever" through this seam any more.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deadline, TimeSpan.Zero);

        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return new ProcessResult(-1, string.Empty, $"{executable} did not start.");
            }

            // Both streams are read before the wait. Draining only one of them deadlocks the
            // moment a command fills the other pipe's buffer, which on a frame means a
            // reconciliation pass that never returns — and a hung pass is worse than a failed
            // one, because nothing on the screen ever changes to say so.
            //
            // Both are also handed to the same sink when there is one, because which of them a tool
            // draws its progress bar on is the tool's business: dfu-util has published it on stdout
            // and on stderr in different builds, and a reader that picked one would work on a frame
            // and report nothing on the next one. A sink told about both may see two segments race;
            // the box behind it keeps the newest and drops the other, which is the correct outcome
            // for a report and would be the wrong one for anything that mattered.
            var standardOutput = new ChildStream(process.StandardOutput, onOutput, cancellationToken);
            var standardError = new ChildStream(process.StandardError, onOutput, cancellationToken);
            var drained = Task.WhenAll(standardOutput.Reading, standardError.Reading);
            var finished = FinishAsync(process, drained, cancellationToken);

            // <b>The deadline is a race, not a token, and that is the whole of why it works.</b>
            // Cancelling a read on a pipe does not reliably interrupt a read that is already
            // blocked — on Unix a non-seekable FileStream checks the token before it starts the
            // read and not after — so a deadline expressed only as a CancellationToken would be a
            // deadline that fires on paper and hangs in practice. Racing the whole of the wait
            // against a timer means the return does not depend on end-of-file arriving at all,
            // which is the one property that survives an orphaned grandchild holding the pipe open
            // for ever.
            var timer = Task.Delay(deadline, cancellationToken);

            if (await Task.WhenAny(finished, timer).ConfigureAwait(false) == finished)
            {
                await finished.ConfigureAwait(false);

                return new ProcessResult(
                    process.ExitCode,
                    standardOutput.Text.Trim(),
                    standardError.Text.Trim());
            }

            // The timer won. It may have won because it was cancelled rather than because it
            // elapsed, and those are opposite meanings: a cancelled agent is shutting down and owes
            // its caller the same OperationCanceledException it always did.
            cancellationToken.ThrowIfCancellationRequested();

            // Kill the tree, then give the pipes a moment. Whether they close is the honest test of
            // whether the kill reached everything: the write end stays open for exactly as long as
            // some process still holds it, so a drain that completes here proves nothing survived,
            // and one that does not proves something did. Assuming either would be a guess.
            var killed = KillTree(process);
            var closed = await CompletedWithinAsync(drained, ProcessDeadline.KillGrace).ConfigureAwait(false);

            if (!closed)
            {
                // Abandoned, so its fault has to be marked observed here or it surfaces minutes
                // later as an UnobservedTaskException attached to nothing — disposing the process
                // below closes the streams underneath a read that is still running.
                Observe(drained);
            }

            return ProcessResult.DeadlineExceeded(
                executable,
                arguments,
                deadline,
                standardOutput.Text,
                standardError.Text,
                treeKilled: killed && closed);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProcessResult(-1, string.Empty, exception.Message);
        }
    }

    /// <summary>Both pipes at end-of-file and the process reaped, in that order.</summary>
    private static async Task FinishAsync(Process process, Task drained, CancellationToken cancellationToken)
    {
        await drained.ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether <paramref name="task"/> finished inside <paramref name="window"/>.</summary>
    /// <remarks>
    /// Faults count as finished: the question is whether the wait is over, and a read that threw
    /// because its stream went away is over. The fault itself is the caller's to observe.
    /// </remarks>
    private static async Task<bool> CompletedWithinAsync(Task task, TimeSpan window)
    {
        if (task.IsCompleted)
        {
            return true;
        }

        using var timer = new CancellationTokenSource(window);

        try
        {
            await task.WaitAsync(timer.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return !task.IsCompleted ? false : true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return true;
        }
    }

    /// <summary>
    /// Kills <paramref name="process"/> and everything it started, and says whether that was possible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The tree, because the child alone is not the hazard.</b> <c>apt</c> is
    /// <c>env DEBIAN_FRONTEND=noninteractive apt-get …</c>, and every user-scope command is
    /// <c>runuser -u … -- env … systemctl --user …</c>: the process the agent holds a handle to is a
    /// wrapper, and the thing that actually hangs is one or two levels below it. Killing the wrapper
    /// leaves the tool running and holding the pipe, which is a hang with extra steps.
    /// </para>
    /// <para>
    /// <b>Reachability is a platform fact and not a promise this makes.</b> The tree is built by
    /// following recorded parents, so it reaches a grandchild whose parent is still alive — measured
    /// here. A grandchild whose parent has <i>already exited</i> is reachable on Windows, where the
    /// parent's id stays in the child's record, and not on Linux, where the kernel reparents an
    /// orphan to init and the link the walk needs is gone. That difference is why the deadline does
    /// not depend on this succeeding: the caller is answered on time either way, and the result says
    /// which happened rather than claiming the good case.
    /// </para>
    /// </remarks>
    private static bool KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            try
            {
                process.Kill();
            }
            catch (Exception inner) when (inner is not OutOfMemoryException and not StackOverflowException)
            {
                // Nothing left to try, and nothing worth raising: the caller is about to be told
                // the command did not answer, which is the fact that matters either way.
            }

            return false;
        }
    }

    /// <summary>Marks an abandoned task's fault observed without waiting for it.</summary>
    private static void Observe(Task task) =>
        _ = task.ContinueWith(
            static finished => _ = finished.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// One of the child's streams, read to the end and readable before it gets there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The whole stream is still returned</b>, because the event trail carries the tool's output
    /// verbatim and that is the first thing anybody debugging a bad flash reads. Streaming to a sink
    /// is additive: what changes with a sink present is <i>when</i> the caller learns, not what it
    /// ends up with.
    /// </para>
    /// <para>
    /// <b>Why this is a class holding a buffer rather than a <c>Task&lt;string&gt;</c> returning
    /// one.</b> A task's result exists only once the task is finished, and the case this whole file
    /// is about is the one where it never finishes. The point of a deadline is to report what a
    /// command <i>had</i> said before it was stopped, so the text has to be readable from another
    /// thread while the read is still in flight — which is what <see cref="Text"/> is for, and why
    /// the no-sink path buffers now instead of calling <c>ReadToEndAsync</c>.
    /// </para>
    /// </remarks>
    private sealed class ChildStream
    {
        private readonly System.Text.StringBuilder _whole = new(1024);
        private readonly Lock _gate = new();

        /// <summary>Starts reading <paramref name="reader"/> immediately.</summary>
        public ChildStream(StreamReader reader, Action<string>? onOutput, CancellationToken cancellationToken) =>
            Reading = DrainAsync(reader, onOutput, cancellationToken);

        /// <summary>The read, which completes at end-of-file.</summary>
        public Task Reading { get; }

        /// <summary>Everything read so far, whether or not <see cref="Reading"/> has finished.</summary>
        public string Text
        {
            get
            {
                lock (_gate)
                {
                    return _whole.ToString();
                }
            }
        }

        private async Task DrainAsync(
            StreamReader reader,
            Action<string>? onOutput,
            CancellationToken cancellationToken)
        {
            var splitter = onOutput is null ? null : new ChildOutputSplitter(onOutput);
            var buffer = new char[1024];

            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                lock (_gate)
                {
                    _whole.Append(buffer, 0, read);
                }

                // Outside the lock: the sink runs on this thread by contract, and holding the lock
                // across a caller's callback would let a slow sink block the timeout path's read of
                // the partial output — the one read that must never wait on the child.
                splitter?.Write(buffer.AsSpan(0, read));
            }

            splitter?.Flush();
        }
    }
}
