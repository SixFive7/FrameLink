using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Resources;

/// <summary>One of the six files the reSpeaker control tool needs, and what it must hash to.</summary>
/// <param name="Name">The file name inside the pinned directory.</param>
/// <param name="Sha256">Its digest, measured rather than published — see <see cref="XvfHostReleasePin"/>.</param>
/// <param name="SizeBytes">Its exact length, which bounds the download.</param>
/// <param name="Executable">Whether the frame has to be able to run it.</param>
public readonly record struct XvfHostFile(string Name, string Sha256, long SizeBytes, bool Executable);

/// <summary>
/// Seeed's aarch64 <c>xvf_host</c> and its five sidecar files, pinned at a commit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pinned at a commit rather than at a release, because there is no release.</b> The upstream
/// repository has zero releases and zero tags — <c>GET /releases/latest</c> answers 404 — so the
/// house shape of "a published archive plus the publisher's own checksums file" is simply not
/// available here. What <i>is</i> available is stronger than a tag: a
/// <c>raw.githubusercontent.com</c> URL carrying a full commit SHA is content-addressed, so the
/// bytes behind it cannot change without the URL changing. The digests below are then the second
/// lock, and the pair of them is equivalent in strength to the LiveKit and Immich Kiosk pins —
/// weaker only in that this publisher signs nothing, which is equally true of a
/// <c>checksums.txt</c>.
/// </para>
/// <para>
/// <b>Six files, not four, and the correction matters.</b> The resource catalog used to say "the
/// binary and its three sibling <c>.so</c> files". Seeed's own <c>host_control/README.md</c> lists
/// <c>dfu_cmds.yaml</c> and <c>transport_config.yaml</c> as required members of the same directory,
/// so a verified install that covered four of them would be asserting completeness over a directory
/// it had only half looked at. The seventh file in that directory, <c>xvf_i2c_dfu</c>, is
/// deliberately absent from this pin: this product does USB DFU with <c>dfu-util</c> and never the
/// I2C path, so fetching 3.4 MB to leave it unused would be cost with no claim behind it.
/// </para>
/// <para>
/// <b>Fetched, never vendored, and that is a licence conclusion rather than a preference.</b> The
/// upstream repository carries <i>no licence file at all</i> — measured, 0 of 19 blobs at this
/// commit and 0 of 51 at today's head — so default copyright applies and nothing grants
/// redistribution. The tool also appears to be built from XMOS's <c>host_xvf_control</c>, whose
/// XCORE VOCALFUSION LICENCE forbids making the software available to a third party "on a
/// standalone basis" while expressly permitting shipping it installed on the devices. Fetching it
/// onto a frame at provision time and running it there sits inside that permission; committing
/// these bytes to a public repository would not. Appendix A decision 63 records the whole of it,
/// including why building from XMOS source is the one direction that must never be taken.
/// </para>
/// <para>
/// <b>Verified live 2026-08-16 @ commit <c>725f3846</c></b>, and every value below was measured
/// rather than recalled: each of the six raw URLs answered 200, the downloaded bytes are the
/// lengths stated and hash to the digests stated, and the same six files downloaded from today's
/// <c>master</c> head are byte-for-byte identical — the directory has not moved since 2025-07-04.
/// The ledger entry <c>xvf-host-tool</c> in <c>upstream-review.json</c> is §7.1's record of that
/// review, and a test ties the two together.
/// </para>
/// </remarks>
public sealed record XvfHostReleasePin
{
    /// <summary>The pin this build fetches, verified 2026-08-16.</summary>
    public static XvfHostReleasePin Current { get; } = new()
    {
        Owner = "respeaker",
        Repository = "reSpeaker_XVF3800_USB_4MIC_ARRAY",
        Commit = "725f38464e73477a30aba9f5c220f1cfdc66d682",
        DirectoryInRepository = "host_control/rpi_64bit",
        Files =
        [
            new XvfHostFile(
                XvfHost.Binary,
                "63f89c6672c0d89bc82d8182cb36013ac3619288780f315a2e0373fb3ed771f2",
                1_772_904,
                Executable: true),
            new XvfHostFile(
                "libcommand_map.so",
                "c1b424313e48cfe97c5cfce0530ac05fe47f818cc0fba15a9954198ef105282c",
                151_680,
                Executable: false),
            new XvfHostFile(
                "libdevice_i2c.so",
                "7acb02a6ae14c34e3291fb55ec91d681d2902b79916df9fef53b5501fc2d1b3b",
                72_568,
                Executable: false),
            new XvfHostFile(
                "libdevice_usb.so",
                "5b52ee35ef17aa287555abb112ebb1dc8497e11d43acc1bd223170d02e28eddd",
                73_312,
                Executable: false),
            new XvfHostFile(
                "dfu_cmds.yaml",
                "67f6a982567b8d23da85c5806c40344094d21071c631344a47164cd085dddba3",
                2_507,
                Executable: false),
            new XvfHostFile(
                "transport_config.yaml",
                "071f1fb87cfbdeffd3ba624713fa0745f27debfbde0544e8ac1af3863c29034d",
                30,
                Executable: false),
        ],
        ReviewedUtc = new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero),
    };

    /// <summary>The GitHub account publishing the tool.</summary>
    public required string Owner { get; init; }

    /// <summary>The repository it lives in.</summary>
    public required string Repository { get; init; }

    /// <summary>The full commit SHA every download is addressed by.</summary>
    public required string Commit { get; init; }

    /// <summary>The directory inside that commit holding the aarch64 build.</summary>
    public required string DirectoryInRepository { get; init; }

    /// <summary>Every file this build installs, with its measured digest and length.</summary>
    public required IReadOnlyList<XvfHostFile> Files { get; init; }

    /// <summary>When a human last checked this pin against upstream (§7.1's stamp).</summary>
    public required DateTimeOffset ReviewedUtc { get; init; }

    /// <summary>Where the pinned directory is served from, without a trailing slash.</summary>
    public string RawBaseUrl =>
        "https://raw.githubusercontent.com/" + Owner + "/" + Repository + "/" + Commit
        + "/" + DirectoryInRepository;

    /// <summary>Where <paramref name="file"/> is published.</summary>
    public Uri UrlOf(XvfHostFile file) => new(RawBaseUrl + "/" + file.Name);

    /// <summary>
    /// The API call the ledger's <c>github-path-commit</c> probe makes.
    /// </summary>
    /// <remarks>
    /// Carried on the pin as well as in the ledger so the two cannot describe different upstreams,
    /// and asserted equal by a test. Watching the <i>path</i> is the whole point: the repository's
    /// default branch moves for reasons that have nothing to do with this directory, and an entry
    /// that reported "moved" on every unrelated push would be a gate nobody reads.
    /// </remarks>
    public string CommitsUrl =>
        "https://api.github.com/repos/" + Owner + "/" + Repository
        + "/commits?path=" + DirectoryInRepository + "&per_page=1";

    /// <summary>The command a person runs to check this pin by hand.</summary>
    /// <remarks>
    /// Not used by anything the agent runs. §7.1 makes an upstream artifact's version and checksum
    /// "reviewable facts, not memory", and a reviewable fact needs a stated way of being re-checked
    /// by whoever next reads this file.
    /// </remarks>
    public string ReviewCommand =>
        "curl -fsSL " + CommitsUrl + " | head"
        + string.Concat(Files.Select(file => "\ncurl -fsSL " + UrlOf(file) + " | sha256sum"));
}

/// <summary>Why an install did not happen.</summary>
public enum XvfHostInstallResult
{
    /// <summary>Every pinned file is in place and hashes to the pin.</summary>
    Installed,

    /// <summary>Every pinned file was already in place; nothing was fetched.</summary>
    AlreadyInstalled,

    /// <summary>Upstream could not be reached, or answered something unusable.</summary>
    Unreachable,

    /// <summary>A download was not the length the pin states.</summary>
    SizeMismatch,

    /// <summary>A download did not hash to the pinned digest.</summary>
    ChecksumMismatch,

    /// <summary>A staging file or a rename failed.</summary>
    WriteFailed,
}

/// <summary>Opens a stream over an upstream URL.</summary>
/// <remarks>
/// A seam for the same reason every other outside surface has one: the agent runs for real only on
/// a frame, and the whole of the install has to be assertable on a workstation with no network.
/// </remarks>
public interface IXvfHostDownload
{
    /// <summary>Opens <paramref name="url"/> for reading, or null if it cannot be fetched.</summary>
    Task<Stream?> OpenAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>A download that never answers.</summary>
/// <remarks>
/// For a catalog built somewhere nothing may reach the network — the graph tests, and anything that
/// only wants to inspect the resource set. <c>tool.xvf-host.installed</c> then reports the fetch as
/// unreachable, which is the honest answer and the same one a frame with no route gets.
/// </remarks>
public sealed class UnreachableXvfHostDownload : IXvfHostDownload
{
    /// <summary>The shared instance.</summary>
    public static UnreachableXvfHostDownload Instance { get; } = new();

    /// <inheritdoc/>
    public Task<Stream?> OpenAsync(Uri url, CancellationToken cancellationToken) => Task.FromResult<Stream?>(null);
}

/// <summary>Fetches over plain HTTPS.</summary>
public sealed class HttpXvfHostDownload : IXvfHostDownload
{
    private readonly HttpClient _client;
    private readonly IAgentLog _log;

    /// <summary>Creates a download over <paramref name="client"/>.</summary>
    public HttpXvfHostDownload(HttpClient client, IAgentLog log)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(log);

        _client = client;
        _log = log;
    }

    /// <inheritdoc/>
    public async Task<Stream?> OpenAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // raw.githubusercontent.com serves whatever it is asked for, and a cache between here
            // and it must not be allowed to answer a content-addressed URL from anything stale.
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

            var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _log.Warn($"Fetching {url} answered {(int)response.StatusCode}.");
                response.Dispose();
                return null;
            }

            return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _log.Warn($"Fetching {url} failed: {exception.Message}");
            return null;
        }
    }
}

/// <summary>How one verified fetch ended.</summary>
public enum VerifiedFetchResult
{
    /// <summary>The bytes arrived, matched the pin and are in place.</summary>
    Installed,

    /// <summary>Upstream could not be reached, or answered something unusable.</summary>
    Unreachable,

    /// <summary>The download was not the length the pin states.</summary>
    SizeMismatch,

    /// <summary>The download did not hash to the pinned digest.</summary>
    ChecksumMismatch,
}

/// <summary>
/// <b>Fetch → verify length and SHA-256 → fsync → atomic rename.</b> One implementation, because
/// two would eventually disagree.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <see cref="XvfHostInstaller"/> when a second pinned artifact appeared —
/// <c>XvfFirmwareInstaller</c>, which puts the array's DFU images on the card. Both fetch from the
/// same publisher over the same content-addressed URL shape, and both have the same requirement:
/// nothing unverified is ever put in place, and a server that keeps sending fills no disk. A copy
/// of this loop with one of those properties quietly missing is exactly the failure the whole pin
/// exists to prevent, and the copy that would have gone missing is the firmware one — the file that
/// gets written to a device that has no second chance.
/// </para>
/// <para>
/// The two promises the tail keeps are deliberately different things. <c>rename(2)</c> is atomic
/// with respect to a <i>reader</i> — the file is wholly old or wholly new and never half — while
/// <c>fsync</c> is what makes the new content <i>durable</i>. Only the pair of them survives a power
/// cut, and both of this method's callers write to the card a frame boots from.
/// </para>
/// </remarks>
public static class VerifiedFetch
{
    /// <summary>Suffix of the file each download is staged into before the rename.</summary>
    public const string StagingSuffix = ".part";

    /// <summary>Fetches <paramref name="url"/> into <paramref name="path"/>, or refuses.</summary>
    public static async Task<VerifiedFetchResult> IntoAsync(
        ISystemFiles files,
        IXvfHostDownload download,
        IAgentLog log,
        Uri url,
        string path,
        string sha256,
        long sizeBytes,
        UnixFileMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);

        var payload = await download.OpenAsync(url, cancellationToken).ConfigureAwait(false);

        if (payload is null)
        {
            return VerifiedFetchResult.Unreachable;
        }

        var name = path[(path.LastIndexOf('/') + 1)..];
        var staging = path + StagingSuffix;
        var resolved = files.Resolve(staging);

        long written;
        string digest;

        await using (payload.ConfigureAwait(false))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            await using var target = new FileStream(resolved, FileMode.Create, FileAccess.Write, FileShare.None);
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
                if (written > sizeBytes)
                {
                    // A server that keeps sending is not serving the file the pin names, and
                    // /var/lib/fl-agent is on the card this frame boots from.
                    break;
                }

                hash.AppendData(buffer.AsSpan(0, read));
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            // flushToDisk, not Flush(). The rename below is atomic for a reader; it says nothing
            // about whether the bytes reached the card.
            target.Flush(flushToDisk: true);
            digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        if (written != sizeBytes)
        {
            log.Fail(string.Create(
                CultureInfo.InvariantCulture,
                $"{name} rejected: {written} bytes fetched from {url}, {sizeBytes} expected."));
            return Discard(files, staging, VerifiedFetchResult.SizeMismatch);
        }

        if (!string.Equals(digest, sha256, StringComparison.OrdinalIgnoreCase))
        {
            log.Fail($"{name} rejected: {url} does not match the pinned digest.");
            return Discard(files, staging, VerifiedFetchResult.ChecksumMismatch);
        }

        // Set before the rename so the target is never briefly unrunnable, and again after it on
        // the path the file actually has — the same pair FileStateStore.WriteSecretAtomic keeps,
        // because the mode travels with the inode through rename(2) on a frame and is recorded
        // against the path on the workstation the suite runs on.
        files.SetMode(staging, mode);
        File.Move(resolved, files.Resolve(path), overwrite: true);
        files.SetMode(path, mode);

        log.Info($"{name} installed at {path} from {url}.");
        return VerifiedFetchResult.Installed;
    }

    /// <summary>SHA-256 of a file the agent owns, or null if it is absent or unreadable.</summary>
    public static async Task<string?> DigestAsync(
        ISystemFiles files,
        IAgentLog log,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(log);

        if (!files.FileExists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                files.Resolve(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 256 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexStringLower(hash);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.Warn($"{path} could not be read: {exception.Message}");
            return null;
        }
    }

    private static VerifiedFetchResult Discard(ISystemFiles files, string staging, VerifiedFetchResult result)
    {
        try
        {
            files.DeleteFile(staging);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stale staging file is untidy and harmless: the next attempt truncates it, and it is
            // never the file anything runs.
        }

        return result;
    }
}

/// <summary>
/// <b>Fetch → verify SHA-256 → atomic rename, one file at a time.</b> The reSpeaker half of the
/// pinned-and-checksum-verified fetch §2.1 already performs for Immich Kiosk.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately the same shape as <c>KioskInstaller</c> and <c>FileBinarySwap</c>, and for the same
/// reasons: verification strictly precedes anything being put in place, each staging file lives
/// beside its target because <c>rename(2)</c> is only atomic within one filesystem, and the staging
/// file is fsynced before the rename because atomicity and durability are different promises and
/// only the pair of them survives a power cut.
/// </para>
/// <para>
/// <b>Nothing unverified is ever given the executable bit, and no partial set is ever left
/// runnable.</b> A file whose length or digest disagrees with the pin is deleted and the install
/// stops there, returning the mismatch — the loud refusal §0.4 and §2.5 both want, rather than a
/// directory that half-works. The copy also stops the moment it has read more bytes than the pin
/// states, so a server that keeps sending fills no disk; that matters more here than it does for
/// the agent's own binary, because <c>/var/lib/fl-agent</c> is on the card the frame boots from.
/// </para>
/// <para>
/// <b>The digest is what "installed" means, on every pass and not only after a fetch.</b> A note
/// recording that an install succeeded would survive a boot that the files themselves did not,
/// which is exactly the claim §2.4 refuses — so <see cref="UnverifiedAsync"/> re-hashes all six
/// files every time the resource observes, and that is also what makes Verify-after-reboot a real
/// check rather than a memory.
/// </para>
/// </remarks>
public sealed class XvfHostInstaller
{
    /// <summary>Suffix of the file each download is staged into before the rename.</summary>
    public const string StagingSuffix = VerifiedFetch.StagingSuffix;

    private const UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const UnixFileMode DataMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite
        | UnixFileMode.GroupRead
        | UnixFileMode.OtherRead;

    private readonly ISystemFiles _files;
    private readonly IXvfHostDownload _download;
    private readonly IAgentLog _log;

    /// <summary>Creates an installer that fills the agent-owned tool directory.</summary>
    public XvfHostInstaller(
        ISystemFiles files,
        IXvfHostDownload download,
        IAgentLog log,
        XvfHostReleasePin? pin = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(log);

        _files = files;
        _download = download;
        _log = log;
        Pin = pin ?? XvfHostReleasePin.Current;
    }

    /// <summary>The files this installer puts in place.</summary>
    public XvfHostReleasePin Pin { get; }

    /// <summary>Where they go — the agent-owned tree, never the login user's home.</summary>
    /// <remarks>
    /// Static because the destination is fixed by §2.1's state directory rather than by the pin: a
    /// different pin would still install here, and an installer that could be pointed elsewhere
    /// would be a second answer to a question that has one.
    /// </remarks>
    public static string TargetDirectory { get; } = XvfHost.ToolDirectory(XvfHost.AgentDirectory);

    /// <summary>
    /// Which pinned files are absent, wrong, or unrunnable in <paramref name="directory"/>, each
    /// described as a person would read it. Empty means the directory matches the pin exactly.
    /// </summary>
    /// <remarks>
    /// Takes a directory rather than assuming <see cref="TargetDirectory"/> because a frame built by
    /// hand has guide 4's clone under <c>~/xvf3800</c>, and the honest question about that tree is
    /// the same one: are these the pinned bytes? If they are, the frame is in sync where it stands
    /// and nothing is downloaded; if they are not, the repair is a verified install into the
    /// agent-owned directory, which <c>XvfHost.Root()</c> then prefers.
    /// </remarks>
    public async Task<IReadOnlyList<string>> UnverifiedAsync(string directory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var faults = new List<string>();

        foreach (var file in Pin.Files)
        {
            var path = directory.TrimEnd('/') + "/" + file.Name;
            var digest = await DigestAsync(path, cancellationToken).ConfigureAwait(false);

            if (digest is null)
            {
                faults.Add($"{file.Name} is missing");
                continue;
            }

            if (!string.Equals(digest, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                faults.Add($"{file.Name} is a different file, sha256 {digest}");
                continue;
            }

            if (file.Executable && !IsExecutable(path))
            {
                faults.Add($"{file.Name} is not executable");
            }
        }

        return faults;
    }

    /// <summary>Fetches, verifies and installs every pinned file that is not already right.</summary>
    public async Task<XvfHostInstallResult> InstallAsync(CancellationToken cancellationToken)
    {
        var fetched = 0;

        try
        {
            _files.EnsureDirectory(TargetDirectory);

            foreach (var file in Pin.Files)
            {
                var path = TargetDirectory + "/" + file.Name;
                var mode = file.Executable ? ExecutableMode : DataMode;
                var digest = await DigestAsync(path, cancellationToken).ConfigureAwait(false);

                if (string.Equals(digest, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    // The bytes are right and only the mode is not — guide 4 step 2's `chmod +x`,
                    // and the one repair that must never cost a download.
                    if (file.Executable && !IsExecutable(path))
                    {
                        _files.SetMode(path, mode);
                        fetched++;
                    }

                    continue;
                }

                var result = await FetchAsync(file, path, mode, cancellationToken).ConfigureAwait(false);
                if (result != XvfHostInstallResult.Installed)
                {
                    return result;
                }

                fetched++;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Fail($"The reSpeaker control tool could not be written: {exception.Message}");
            return XvfHostInstallResult.WriteFailed;
        }

        return fetched == 0 ? XvfHostInstallResult.AlreadyInstalled : XvfHostInstallResult.Installed;
    }

    /// <summary>Streams one file to a sibling staging path and checks it against the pin.</summary>
    /// <remarks>
    /// The loop itself lives in <see cref="VerifiedFetch"/>, shared with the DFU image installer.
    /// What stays here is the mapping from a generic refusal to this resource's own vocabulary, so
    /// the delta an operator reads still names the thing that was being installed.
    /// </remarks>
    private async Task<XvfHostInstallResult> FetchAsync(
        XvfHostFile file,
        string path,
        UnixFileMode mode,
        CancellationToken cancellationToken)
    {
        var result = await VerifiedFetch
            .IntoAsync(
                _files,
                _download,
                _log,
                Pin.UrlOf(file),
                path,
                file.Sha256,
                file.SizeBytes,
                mode,
                cancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            VerifiedFetchResult.Installed => XvfHostInstallResult.Installed,
            VerifiedFetchResult.Unreachable => XvfHostInstallResult.Unreachable,
            VerifiedFetchResult.SizeMismatch => XvfHostInstallResult.SizeMismatch,
            _ => XvfHostInstallResult.ChecksumMismatch,
        };
    }

    /// <summary>SHA-256 of a file the agent owns, or null if it is absent or unreadable.</summary>
    private Task<string?> DigestAsync(string path, CancellationToken cancellationToken) =>
        VerifiedFetch.DigestAsync(_files, _log, path, cancellationToken);

    /// <summary>Whether the file carries a bit that makes it runnable.</summary>
    /// <remarks>
    /// Its own check rather than a detail of the digest comparison, because they are different
    /// faults with the same appearance: a byte-perfect <c>xvf_host</c> that cannot be executed
    /// produces exactly the silence a missing one does.
    /// </remarks>
    private bool IsExecutable(string path) =>
        _files.ModeOf(path) is { } mode
        && (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    private XvfHostInstallResult Discard(string staging, XvfHostInstallResult result)
    {
        try
        {
            _files.DeleteFile(staging);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stale staging file is untidy and harmless: the next attempt truncates it, and it is
            // never the file anything runs.
        }

        return result;
    }
}
