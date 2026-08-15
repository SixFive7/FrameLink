using System.Security.Cryptography;
using FrameLink.Agent.Hosting;
using FrameLink.Protocol;

namespace FrameLink.Agent.Update;

/// <summary>Why a swap did not happen.</summary>
public enum SwapResult
{
    /// <summary>The new binary is in place.</summary>
    Applied,

    /// <summary>The download was not the length the feed promised.</summary>
    SizeMismatch,

    /// <summary>The download did not hash to the digest the feed promised.</summary>
    ChecksumMismatch,

    /// <summary>The staging file or the rename failed.</summary>
    WriteFailed,
}

/// <summary>Puts a verified binary in place of the running one.</summary>
public interface IBinarySwap
{
    /// <summary>Where the binary being replaced lives.</summary>
    string TargetPath { get; }

    /// <summary>Streams, verifies and — only if verification passes — swaps.</summary>
    Task<SwapResult> ApplyAsync(Stream payload, AgentRelease release, CancellationToken cancellationToken);
}

/// <summary>
/// §2.8's updater, the half that touches the filesystem: <b>fetch → verify SHA-256 → write
/// <c>fl-agent.new</c> → atomic rename</b>.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than Velopack, and §6.2 records exactly why: Velopack applies updates by
/// spawning a helper as an ordinary child process, which under systemd lands inside the service
/// cgroup, where the default <c>KillMode=control-group</c> kills it the instant the daemon exits —
/// which is precisely when the update is being applied. Everything else about Velopack checked out
/// on Linux/arm64. The decision was narrow: keep the design, replace the mechanism.
/// </para>
/// <para>
/// So there is no child process here and never will be. The whole swap happens in-process, and the
/// only thing that happens afterwards is the process exiting into a <c>Restart=always</c> unit.
/// </para>
/// <para>
/// Verification strictly precedes the rename, and the staging file is removed on every failure
/// path. The staging file lives beside the target rather than in a temporary directory, because
/// <c>rename(2)</c> is only atomic within one filesystem — a swap staged in <c>/tmp</c> would
/// degrade into copy-then-truncate on any frame where <c>/tmp</c> is a separate mount, and a
/// half-copied <c>fl-agent</c> is an unbootable agent.
/// </para>
/// <para>
/// The staging file is fsynced before the rename. Atomicity and durability are different
/// promises, and only the pair of them survives a power cut.
/// </para>
/// </remarks>
public sealed class FileBinarySwap : IBinarySwap
{
    /// <summary>Suffix of the staging file.</summary>
    public const string StagingSuffix = ".new";

    private const UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly IFilePermissions _permissions;
    private readonly IAgentLog _log;

    /// <summary>Creates a swap that replaces <paramref name="targetPath"/>.</summary>
    public FileBinarySwap(string targetPath, IFilePermissions permissions, IAgentLog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(log);

        TargetPath = targetPath;
        _permissions = permissions;
        _log = log;
    }

    /// <inheritdoc/>
    public string TargetPath { get; }

    /// <summary>Path of the staging file.</summary>
    public string StagingPath => TargetPath + StagingSuffix;

    /// <inheritdoc/>
    public async Task<SwapResult> ApplyAsync(
        Stream payload,
        AgentRelease release,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(release);

        var staging = StagingPath;

        try
        {
            long written;
            string digest;

            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using var file = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[64 * 1024];
                written = 0;

                while (true)
                {
                    var read = await payload.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    written += read;
                    if (written > release.SizeBytes)
                    {
                        // Stop before the disk fills: a server that keeps sending is a server that
                        // is not serving the release it advertised.
                        break;
                    }

                    hash.AppendData(buffer.AsSpan(0, read));
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                // flushToDisk, not FlushAsync. rename(2) makes the swap atomic with respect to
                // *observers* — the target is never half a binary — but it says nothing about
                // durability: the new file's contents can still be sitting in the page cache when
                // the power goes. A frame that came back to a correctly named, zero-length
                // fl-agent would be exactly the unbootable agent the staging dance exists to
                // prevent, and there is no async fsync to reach for.
                file.Flush(flushToDisk: true);
                digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            if (written != release.SizeBytes)
            {
                _log.Fail($"Update rejected: {written} bytes downloaded, {release.SizeBytes} expected.");
                return Discard(staging, SwapResult.SizeMismatch);
            }

            if (!string.Equals(digest, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _log.Fail("Update rejected: the downloaded binary does not match the published checksum.");
                return Discard(staging, SwapResult.ChecksumMismatch);
            }

            _permissions.Restrict(staging, ExecutableMode);

            // rename(2): the target is either wholly the old binary or wholly the new one, with no
            // instant in between where it is neither. Nothing else here would survive a power cut.
            File.Move(staging, TargetPath, overwrite: true);

            _log.Info($"Agent binary replaced with version {release.Version}.");
            return SwapResult.Applied;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Fail($"Update could not be written: {exception.Message}");
            return Discard(staging, SwapResult.WriteFailed);
        }
    }

    private static SwapResult Discard(string staging, SwapResult result)
    {
        try
        {
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Leaving a stale staging file behind is untidy but harmless: the next attempt
            // truncates it, and it is never the file systemd starts.
        }

        return result;
    }
}
