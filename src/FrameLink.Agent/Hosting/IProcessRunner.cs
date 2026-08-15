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
}

/// <summary>Starts real processes.</summary>
public sealed class HostProcessRunner : IProcessRunner
{
    /// <summary>The shared instance.</summary>
    public static HostProcessRunner Instance { get; } = new();

    /// <inheritdoc/>
    public async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
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
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
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
}
