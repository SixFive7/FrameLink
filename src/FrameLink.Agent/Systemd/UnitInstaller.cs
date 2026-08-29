using System.Reflection;
using System.Text;
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

    /// <summary>Suffix of the file the unit is staged into before it is renamed into place.</summary>
    public const string StagingSuffix = ".new";

    /// <summary>
    /// What <see cref="File.WriteAllTextAsync(string, string?, CancellationToken)"/> encodes
    /// with: UTF-8, no byte-order mark, and a throw rather than a substitution.
    /// </summary>
    /// <remarks>
    /// Named here so that moving this write onto the staging path changed where the bytes go and
    /// nothing whatever about what they are. The unit is committed twice and the suite compares
    /// the two copies byte for byte, so an encoder with different defaults — a preamble, a
    /// substituted character — would be a real defect and not a cosmetic one.
    /// </remarks>
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

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
        await WriteUnitAsync(unitPath, unit, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Stages the unit beside itself, flushes it to the card and renames it into place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same stage-flush-rename every state file gets, for a worse consequence.
    /// <c>File.WriteAllTextAsync</c> truncates the target and then writes it, so a power cut in
    /// between leaves a unit file that is half a unit — and a frame whose agent will not start is
    /// a frame that has lost the one thing that could have repaired it. <c>rename(2)</c> makes
    /// the replacement atomic with respect to observers, so the path is either wholly the old
    /// unit or wholly the new one; <c>fsync</c> is what makes the new one durable, and only the
    /// pair of them survives the cut.
    /// </para>
    /// <para>
    /// <b>The rename is within one directory, so it cannot cross a filesystem.</b> The staging
    /// name is the target's own path with a suffix appended, which puts the two in the same
    /// directory by construction whatever <paramref name="unitPath"/> is — so this is a true
    /// <c>rename(2)</c> rather than the copy-and-delete <c>File.Move</c> falls back to across a
    /// mount point, and neither promise is quietly lost.
    /// </para>
    /// <para>
    /// A failure before the rename leaves a stale staging file and does not touch the unit, which
    /// is the trade this shape exists to make: the next install truncates the staging file, and
    /// the unit systemd already loaded is still the whole one it had. It runs today only from
    /// <c>fl-agent install</c>, with a person at the terminal to see the exception — which is an
    /// argument about who is watching, not about what the file is left as, and it does not
    /// survive the next caller.
    /// </para>
    /// </remarks>
    private static async Task WriteUnitAsync(string unitPath, string unit, CancellationToken cancellationToken)
    {
        var staging = unitPath + StagingSuffix;

        await using (var file = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await file.WriteAsync(StrictUtf8.GetBytes(unit), cancellationToken).ConfigureAwait(false);

            // flushToDisk, not FlushAsync: the rename below says nothing about whether the bytes
            // have reached the card, and there is no async fsync to reach for.
            file.Flush(flushToDisk: true);
        }

        File.Move(staging, unitPath, overwrite: true);
    }
}
