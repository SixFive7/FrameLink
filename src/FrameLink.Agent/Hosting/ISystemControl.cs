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
/// <remarks>
/// A thin adapter over <see cref="IProcessRunner"/> rather than a second copy of the
/// process-spawning code. There is one place in the agent where a child process is started, so
/// the pipe-draining rule that keeps a pass from deadlocking is stated and fixed once.
/// </remarks>
public sealed class SystemdControl : ISystemControl
{
    /// <summary>The <c>systemctl</c> binary, resolved from <c>PATH</c>.</summary>
    public const string Executable = "systemctl";

    private readonly IProcessRunner _processes;

    /// <summary>Creates the adapter over <paramref name="processes"/>.</summary>
    public SystemdControl(IProcessRunner? processes = null) =>
        _processes = processes ?? HostProcessRunner.Instance;

    /// <inheritdoc/>
    public async Task<SystemControlResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        // Every command through here is systemctl, so the deadline is the same one every time and
        // belongs here rather than at sixty call sites. ProcessDeadline.Service is derived from
        // systemd's own DefaultTimeoutStartSec: a job it has not finished in 90 seconds is one it is
        // itself about to fail, so two minutes cannot fire on a job that was about to answer.
        var result = await _processes
            .RunAsync(Executable, arguments, ProcessDeadline.Service, cancellationToken)
            .ConfigureAwait(false);
        return new SystemControlResult(result.Succeeded, result.Combined.Trim());
    }
}
