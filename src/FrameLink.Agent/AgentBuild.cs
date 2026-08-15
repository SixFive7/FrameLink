using System.Reflection;
using System.Runtime.InteropServices;

namespace FrameLink.Agent;

/// <summary>Facts about this build of the agent.</summary>
/// <remarks>
/// The version is the value the hourly update check <i>matches</i> against the served release
/// (§2.8) — not compares. Nothing here ever decides that one version is newer than another,
/// because downgrade is a first-class operation: reverting the container tag reverts the fleet
/// within the hour.
/// </remarks>
public static class AgentBuild
{
    /// <summary>Informational build version, e.g. <c>0.1.0+a1b2c3d</c>.</summary>
    public static string Version { get; } =
        typeof(AgentBuild).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "0.0.0-unknown";

    /// <summary>Runtime identifier of the running process, e.g. <c>linux-arm64</c>.</summary>
    /// <remarks>
    /// Sent to the update endpoint so the Fleet Manager serves the right binary. Virtual
    /// agents (§5.3) are the same agent built <c>linux-x64</c>, so this is genuinely varying
    /// rather than a constant waiting to be inlined.
    /// </remarks>
    public static string RuntimeIdentifier { get; } = BuildRuntimeIdentifier();

    /// <summary>Absolute path of the running binary, or <see langword="null"/> if unknown.</summary>
    /// <remarks>
    /// The updater renames over <i>this</i> path rather than a compiled-in one, so an agent
    /// installed somewhere unusual still updates itself correctly.
    /// </remarks>
    public static string? ExecutablePath => Environment.ProcessPath;

    private static string BuildRuntimeIdentifier()
    {
        var platform =
            OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : "unknown";

        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            Architecture.Arm => "arm",
            Architecture.X86 => "x86",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant(),
        };

        return $"{platform}-{architecture}";
    }
}

/// <summary>Process exit codes the systemd unit and the harness both read.</summary>
public static class ExitCodes
{
    /// <summary>Ordinary shutdown, e.g. on SIGTERM.</summary>
    public const int Success = 0;

    /// <summary>The agent could not start at all; the reason is on the console and in the journal.</summary>
    public const int Unrecoverable = 1;

    /// <summary>
    /// A new binary is in place and the process is standing aside for it (§2.8).
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Success"/> so that a restart caused by an update is legible in
    /// <c>systemctl status</c> instead of looking like an unexplained exit. The unit's
    /// <c>Restart=always</c> brings the new binary up either way.
    /// </remarks>
    public const int RestartToApplyUpdate = 75;
}
