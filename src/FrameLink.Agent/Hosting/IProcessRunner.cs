using System.Diagnostics;

namespace FrameLink.Agent.Hosting;

/// <summary>What running one command produced.</summary>
/// <param name="ExitCode">Process exit code, or -1 if it never started.</param>
/// <param name="StandardOutput">Standard output, trimmed.</param>
/// <param name="StandardError">Standard error, trimmed.</param>
public readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>Whether the process exited zero.</summary>
    public bool Succeeded => ExitCode == 0;

    /// <summary>Output and error together, for a log line or a delta.</summary>
    public string Combined =>
        StandardError.Length == 0 ? StandardOutput
        : StandardOutput.Length == 0 ? StandardError
        : StandardOutput + "\n" + StandardError;
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
}

/// <summary>Starts real processes.</summary>
public sealed class HostProcessRunner : IProcessRunner
{
    /// <summary>The shared instance.</summary>
    public static HostProcessRunner Instance { get; } = new();

    /// <inheritdoc/>
    public Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        StreamAsync(executable, arguments, onOutput: null, cancellationToken);

    /// <inheritdoc/>
    public Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        Action<string> onOutput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onOutput);
        return StreamAsync(executable, arguments, onOutput, cancellationToken);
    }

    private static async Task<ProcessResult> StreamAsync(
        string executable,
        IReadOnlyList<string> arguments,
        Action<string>? onOutput,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

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
            var standardOutput = DrainAsync(process.StandardOutput, onOutput, cancellationToken);
            var standardError = DrainAsync(process.StandardError, onOutput, cancellationToken);
            await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return new ProcessResult(
                process.ExitCode,
                standardOutput.Result.Trim(),
                standardError.Result.Trim());
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProcessResult(-1, string.Empty, exception.Message);
        }
    }

    /// <summary>
    /// Reads one of the child's streams to the end, telling <paramref name="onOutput"/> as it goes.
    /// </summary>
    /// <remarks>
    /// <b>The whole stream is still returned</b>, because the event trail carries the tool's output
    /// verbatim and that is the first thing anybody debugging a bad flash reads. Streaming is
    /// additive: what changes with a sink present is <i>when</i> the caller learns, not what it ends
    /// up with. With no sink this is the same read-to-the-end it always was.
    /// </remarks>
    private static async Task<string> DrainAsync(
        StreamReader reader,
        Action<string>? onOutput,
        CancellationToken cancellationToken)
    {
        if (onOutput is null)
        {
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var splitter = new ChildOutputSplitter(onOutput);
        var whole = new System.Text.StringBuilder(1024);
        var buffer = new char[1024];

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            whole.Append(buffer, 0, read);
            splitter.Write(buffer.AsSpan(0, read));
        }

        splitter.Flush();
        return whole.ToString();
    }
}
