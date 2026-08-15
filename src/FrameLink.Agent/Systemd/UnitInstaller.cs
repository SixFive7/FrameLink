using System.Reflection;
using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Systemd;

/// <summary>
/// Writes the agent's own systemd unit, which travels inside the binary.
/// </summary>
/// <remarks>
/// §2.1: "One single Native AOT binary. <b>No supplemental program files, ever.</b>" A unit file
/// shipped alongside the binary would be exactly such a file — one more thing to copy, to keep in
/// step with the binary, and to get wrong. It is an embedded resource instead, so the unit and the
/// agent that runs under it can never disagree about the version.
/// </remarks>
public static class UnitInstaller
{
    /// <summary>Logical name of the embedded unit.</summary>
    public const string ResourceName = "fl-agent.service";

    /// <summary>Where systemd reads operator-installed units.</summary>
    public const string DefaultUnitPath = "/etc/systemd/system/fl-agent.service";

    /// <summary>Reads the embedded unit text.</summary>
    public static string ReadUnit()
    {
        using var stream = typeof(UnitInstaller).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"The embedded resource '{ResourceName}' is missing from this build.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Writes the unit and asks systemd to load and enable it.</summary>
    public static async Task<bool> InstallAsync(
        ISystemControl systemControl,
        IAgentLog log,
        string unitPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(systemControl);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(unitPath);

        var unit = ReadUnit();
        var directory = Path.GetDirectoryName(unitPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Idempotent by construction: writing identical content produces an identical file, and
        // `systemctl enable` on an already-enabled unit is a no-op.
        await File.WriteAllTextAsync(unitPath, unit, cancellationToken).ConfigureAwait(false);
        log.Info($"Wrote {unitPath}.");

        var reload = await systemControl.RunAsync(["daemon-reload"], cancellationToken).ConfigureAwait(false);
        if (!reload.Succeeded)
        {
            log.Fail($"systemctl daemon-reload failed: {reload.Output}");
            return false;
        }

        var enable = await systemControl
            .RunAsync(["enable", "--now", "fl-agent.service"], cancellationToken)
            .ConfigureAwait(false);

        if (!enable.Succeeded)
        {
            log.Fail($"systemctl enable --now fl-agent.service failed: {enable.Output}");
            return false;
        }

        log.Info("fl-agent.service is installed and running.");
        return true;
    }
}
