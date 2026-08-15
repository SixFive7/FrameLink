using System.Diagnostics;

namespace FrameLink.Agent.Hosting;

/// <summary>The result of asking the init system to do something.</summary>
/// <param name="Succeeded">Whether the command exited zero.</param>
/// <param name="Output">Combined standard output and standard error, trimmed.</param>
public readonly record struct SystemControlResult(bool Succeeded, string Output);

/// <summary>
/// The agent's narrow window onto systemd.
/// </summary>
/// <remarks>
/// Narrow on purpose: the agent installs and enables its own unit and nothing else. Everything
/// broader — unit content and enablement as reconciled resources — is M2 (§5.1), and the
/// catalog's authority rule (§2.2, "static logic, dynamic values") forbids this ever becoming
/// a general command channel.
/// </remarks>
public interface ISystemControl
{
    /// <summary>Runs <c>systemctl</c> with <paramref name="arguments"/>.</summary>
    Task<SystemControlResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

/// <summary>Invokes the real <c>systemctl</c>.</summary>
public sealed class SystemdControl : ISystemControl
{
    private const string Executable = "systemctl";

    /// <inheritdoc/>
    public async Task<SystemControlResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var start = new ProcessStartInfo(Executable)
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
                return new SystemControlResult(false, $"{Executable} did not start.");
            }

            var standardOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var standardError = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return new SystemControlResult(
                process.ExitCode == 0,
                (standardOutput + standardError).Trim());
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new SystemControlResult(false, exception.Message);
        }
    }
}
