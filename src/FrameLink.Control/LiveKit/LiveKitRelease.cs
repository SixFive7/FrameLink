using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace FrameLink.Control.LiveKit;

/// <summary>One published <c>livekit-server</c> archive, for one architecture.</summary>
/// <remarks>
/// Two digests per architecture, because they answer different questions — the same split
/// <c>KioskReleasePin</c> and <c>BaseImagePin</c> make. <see cref="ArchiveSha256"/> is the value
/// upstream publishes in <c>checksums.txt</c>, so it is what a human reviews the pin against and
/// what the download is refused on <i>before</i> anything is decompressed.
/// <see cref="BinarySha256"/> is the digest of the executable inside the archive, which is the
/// file that ends up on disk and the file the installer re-checks on every start; nobody
/// publishes it, so it is measured here.
/// </remarks>
public sealed record LiveKitAsset
{
    /// <summary>The asset as upstream names it.</summary>
    public required string FileName { get; init; }

    /// <summary>Where it is published.</summary>
    public required Uri ArchiveUrl { get; init; }

    /// <summary>The digest upstream publishes for the archive.</summary>
    public required string ArchiveSha256 { get; init; }

    /// <summary>Length of the published archive.</summary>
    public required long ArchiveSizeBytes { get; init; }

    /// <summary>Digest of the executable inside it. What the installer observes on disk.</summary>
    public required string BinarySha256 { get; init; }

    /// <summary>Length of that executable.</summary>
    public required long BinarySizeBytes { get; init; }
}

/// <summary>
/// The upstream <c>livekit-server</c> release the Fleet Manager carries, pinned (§3.7, §7.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>§3.7: "The Fleet Manager carries <c>livekit-server</c> … and supervises it as a child
/// process."</b> Carried, and deliberately <i>fetched rather than vendored</i> — the same
/// decision <c>KioskRelease</c> records for Immich Kiosk, reached here for a different reason.
/// Immich Kiosk is fetched because AGPL source-offer obligations are better left with the
/// publisher; LiveKit is Apache-2.0 and could be redistributed freely, so what rules vendoring
/// out is arithmetic: the two Linux binaries are 53.4 MB and 49.9 MB, against a 1.35 MB agent
/// and a Fleet Manager measured in single megabytes. A 50 MB blob in git, duplicated per
/// architecture, would be by two orders of magnitude the largest thing in this repository.
/// </para>
/// <para>
/// <b>"Version coupling is a feature — the tested combination is the shipped combination."</b>
/// That is what the digests below are for. The Fleet Manager does not run whatever
/// <c>livekit-server</c> upstream is serving today; it runs exactly these bytes or it runs
/// nothing, and moving the pin is a reviewable diff plus a ledger entry plus the suite.
/// </para>
/// <para>
/// <b>Both Linux architectures, because the Fleet Manager's own platform is the operator's
/// choice.</b> §3.9 establishes that an <i>amd64</i> Fleet Manager writes an <i>arm64</i> image,
/// which says what this deployment is, not what every deployment must be — a self-hoster running
/// this container on an arm64 board is an ordinary case and there is no reason for it to be a
/// broken one. Windows and macOS assets are deliberately absent: §3.1 ships one Linux container,
/// and a pin listing artifacts nothing will ever fetch is a maintenance cost with no reader.
/// </para>
/// <para>
/// <b>Verified live 2026-08-16 @ v1.13.5</b>, and every field below was measured rather than
/// recalled. The release API answered <c>v1.13.5</c> (published 2026-07-31T06:59:38Z, not a
/// prerelease); the published <c>checksums.txt</c> names
/// <c>c020fac437b7cc9b776eef1ad5ea8af77be9acfa07602eca20a3a44930dfbc70</c> for
/// <c>livekit_1.13.5_linux_amd64.tar.gz</c> and
/// <c>332015305518765fe05bad74fc3a9d9583e635e7dd130de3c4fc563d69c550f3</c> for the arm64
/// archive, and the 18,064,127 and 16,349,520 downloaded bytes hash to exactly those. Each
/// archive holds exactly two members — <c>LICENSE</c> (11,358 bytes, the Apache License 2.0)
/// and a <c>livekit-server</c> executable at mode 0755. Both executables' Go build records read
/// <c>-ldflags="-s -w -X main.version=1.13.5 -X main.commit=3b9f118327b257301083a7c4aa46076c8012918a"</c>
/// with <c>CGO_ENABLED=0</c>, <c>GOOS=linux</c> and go1.26.5, which is where "static Go binary"
/// is checked rather than assumed, and their ELF headers read machine 62 (x86-64) and 183
/// (AArch64). The amd64 binary was then run: <c>--version</c> answers <c>1.13.5</c>, the
/// generated configuration of <see cref="LiveKitConfigFile"/> is accepted, <c>ports</c> reports
/// back exactly the ports that configuration asks for, and a token minted by
/// <see cref="LiveKitToken"/> is answered <c>200 success</c> by the running server's
/// <c>/rtc/validate</c> while an expired one is answered <c>401</c>.
/// </para>
/// </remarks>
public sealed record LiveKitReleasePin
{
    /// <summary>LiveKit v1.13.5, verified 2026-08-16.</summary>
    public static LiveKitReleasePin Current { get; } = new()
    {
        Version = "1.13.5",
        ChecksumsUrl = new Uri(
            "https://github.com/livekit/livekit/releases/download/v1.13.5/checksums.txt"),
        BinaryMemberName = "livekit-server",
        ReviewedUtc = new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero),
        Assets = new Dictionary<Architecture, LiveKitAsset>
        {
            [Architecture.X64] = new LiveKitAsset
            {
                FileName = "livekit_1.13.5_linux_amd64.tar.gz",
                ArchiveUrl = new Uri(
                    "https://github.com/livekit/livekit/releases/download/"
                    + "v1.13.5/livekit_1.13.5_linux_amd64.tar.gz"),
                ArchiveSha256 = "c020fac437b7cc9b776eef1ad5ea8af77be9acfa07602eca20a3a44930dfbc70",
                ArchiveSizeBytes = 18_064_127,
                BinarySha256 = "51a1bbe04439b33d6d7a6d6d83fdefad9b938162c341f0f07f75af03e456b49a",
                BinarySizeBytes = 53_420_194,
            },
            [Architecture.Arm64] = new LiveKitAsset
            {
                FileName = "livekit_1.13.5_linux_arm64.tar.gz",
                ArchiveUrl = new Uri(
                    "https://github.com/livekit/livekit/releases/download/"
                    + "v1.13.5/livekit_1.13.5_linux_arm64.tar.gz"),
                ArchiveSha256 = "332015305518765fe05bad74fc3a9d9583e635e7dd130de3c4fc563d69c550f3",
                ArchiveSizeBytes = 16_349_520,
                BinarySha256 = "9582c2a64f9872a690b2b5bba91464c505e15971a93c1944b752cef6094a2802",
                BinarySizeBytes = 49_938_594,
            },
        },
    };

    /// <summary>The release, as upstream names it without the <c>v</c>.</summary>
    public required string Version { get; init; }

    /// <summary>The one checksums file covering every asset. What a human reviews against.</summary>
    public required Uri ChecksumsUrl { get; init; }

    /// <summary>Name of the executable member inside every archive.</summary>
    public required string BinaryMemberName { get; init; }

    /// <summary>When a human last checked this pin against upstream (§7.1's stamp).</summary>
    public required DateTimeOffset ReviewedUtc { get; init; }

    /// <summary>The published assets, by processor architecture.</summary>
    public required IReadOnlyDictionary<Architecture, LiveKitAsset> Assets { get; init; }

    /// <summary>The release tag, as the GitHub API and the download paths spell it.</summary>
    public string Tag => "v" + Version;

    /// <summary>The asset for this process's architecture, or null if none is pinned for it.</summary>
    public LiveKitAsset? AssetFor(Architecture architecture) => Assets.GetValueOrDefault(architecture);

    /// <summary>The command a person runs to check this pin by hand.</summary>
    /// <remarks>
    /// Not used by anything the server runs. It exists because §7.1 makes an upstream artifact's
    /// version and checksum reviewable facts rather than memory, and a reviewable fact needs a
    /// stated way of being re-checked by whoever next reads this file.
    /// </remarks>
    public string ReviewCommand =>
        $"curl -fsSL {ChecksumsUrl} && curl -fsSL "
        + $"{Assets[Architecture.X64].ArchiveUrl} | sha256sum";
}

/// <summary>Why an install did not happen.</summary>
public enum LiveKitInstallResult
{
    /// <summary>The pinned binary is in place and hashes to the pin.</summary>
    Installed,

    /// <summary>The pinned binary was already in place; nothing was fetched.</summary>
    AlreadyInstalled,

    /// <summary>The pin names no asset for this machine's architecture.</summary>
    UnsupportedArchitecture,

    /// <summary>Upstream could not be reached, or answered something unusable.</summary>
    Unreachable,

    /// <summary>The download was not the length the pin states.</summary>
    ArchiveSizeMismatch,

    /// <summary>The download did not hash to the digest upstream published.</summary>
    ArchiveChecksumMismatch,

    /// <summary>The archive did not hold the member the pin names, or held a wrong-sized one.</summary>
    ArchiveMalformed,

    /// <summary>The extracted executable did not hash to the pinned digest.</summary>
    BinaryChecksumMismatch,

    /// <summary>A staging file or the rename failed.</summary>
    WriteFailed,
}

/// <summary>Opens a stream over an upstream URL.</summary>
/// <remarks>
/// A seam for the same reason every other outside surface in this codebase has one: the suite
/// must be able to drive a refusing upstream, a truncated download and a tampered archive on a
/// workstation with no network, and none of those are reproducible against the real GitHub.
/// </remarks>
public interface ILiveKitDownload
{
    /// <summary>Opens <paramref name="url"/> for reading, or null if it cannot be fetched.</summary>
    Task<Stream?> OpenAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>A download that never answers.</summary>
/// <remarks>
/// What a Fleet Manager with no route to GitHub gets, and what the tests use when the fetch is
/// not the thing under test. The installer then reports <see cref="LiveKitInstallResult.Unreachable"/>,
/// which is the honest answer rather than a crash.
/// </remarks>
public sealed class UnreachableLiveKitDownload : ILiveKitDownload
{
    /// <summary>The shared instance.</summary>
    public static UnreachableLiveKitDownload Instance { get; } = new();

    /// <inheritdoc/>
    public Task<Stream?> OpenAsync(Uri url, CancellationToken cancellationToken) =>
        Task.FromResult<Stream?>(null);
}

/// <summary>Fetches over plain HTTPS.</summary>
/// <remarks>
/// Owns its client rather than taking one from a factory. <c>AddHttpClient</c> brings the whole
/// options, logging and handler-lifetime machinery into a container §3.1 keeps deliberately
/// small, to manage exactly one client that makes at most two requests in the lifetime of the
/// process. A long-lived singleton has none of the socket-exhaustion problems that machinery
/// exists to solve.
/// </remarks>
public sealed class HttpLiveKitDownload(ILogger<HttpLiveKitDownload> logger) : ILiveKitDownload, IDisposable
{
    private readonly HttpClient _client = new()
    {
        // Generous, because this is a 50 MB download on whatever connection a self-hoster has,
        // and the timeout covers the whole response rather than the time to first byte.
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders =
        {
            UserAgent = { new System.Net.Http.Headers.ProductInfoHeaderValue("FrameLink-fleet-manager", "1.0") },
        },
    };

    /// <inheritdoc/>
    public void Dispose() => _client.Dispose();

    /// <inheritdoc/>
    public async Task<Stream?> OpenAsync(Uri url, CancellationToken cancellationToken)
    {
        var client = _client;
        try
        {
            var response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LiveKitFetchRefused(url.ToString(), (int)response.StatusCode);
                response.Dispose();
                return null;
            }

            return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LiveKitFetchFailed(exception, url.ToString());
            return null;
        }
    }
}

/// <summary>
/// <b>Fetch → verify SHA-256 → unpack → verify again → atomic rename.</b>
/// </summary>
/// <remarks>
/// <para>
/// Deliberately the same shape as <c>KioskInstaller</c> and <c>FileBinarySwap</c>, and for the
/// same reasons: verification strictly precedes anything being put in place, the staging files
/// live beside the target because <c>rename(2)</c> is only atomic within one filesystem, and the
/// staging file is flushed to disk before the rename because atomicity and durability are
/// different promises and only the pair survives a power cut.
/// </para>
/// <para>
/// <b>The archive is checked before it is opened, and the executable again after it is out.</b>
/// A gzip stream from an unverified source is a decompressor being fed by whoever answered the
/// URL, and a pinned checksum exists so that never happens. So the download is hashed into a
/// staging file first and refused outright on any mismatch; only then is it unpacked, and the
/// member that comes out is length- and digest-checked before it is given the executable bit.
/// Nothing unverified is ever executed and nothing unverified ever reaches the target path.
/// </para>
/// <para>
/// <b>Bounded by the pin's own length.</b> The copy stops the moment it has read more bytes than
/// the pin states, so a server that keeps sending fills no disk — and the disk in question is
/// the operator's volume, which §3.1 already shares with the SQLite database.
/// </para>
/// </remarks>
public sealed class LiveKitInstaller
{
    /// <summary>Suffix of the file the download is staged into.</summary>
    public const string ArchiveStagingSuffix = ".tar.gz.part";

    /// <summary>Suffix of the file the executable is unpacked into before the rename.</summary>
    public const string BinaryStagingSuffix = ".new";

    private const UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly ILiveKitDownload _download;
    private readonly ILogger _log;

    /// <summary>Creates an installer that puts the executable at <paramref name="targetPath"/>.</summary>
    /// <param name="targetPath">Where <c>livekit-server</c> lives.</param>
    /// <param name="architecture">Which published asset to fetch.</param>
    /// <param name="download">The upstream seam.</param>
    /// <param name="log">Where refusals are recorded.</param>
    /// <param name="pin">The release to install. The current pin when omitted.</param>
    public LiveKitInstaller(
        string targetPath,
        Architecture architecture,
        ILiveKitDownload download,
        ILogger log,
        LiveKitReleasePin? pin = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(log);

        TargetPath = targetPath;
        Architecture = architecture;
        Pin = pin ?? LiveKitReleasePin.Current;
        _download = download;
        _log = log;
    }

    /// <summary>Where the executable lives.</summary>
    public string TargetPath { get; }

    /// <summary>The architecture whose asset this installer fetches.</summary>
    public Architecture Architecture { get; }

    /// <summary>The release this installer puts in place.</summary>
    public LiveKitReleasePin Pin { get; }

    /// <summary>The pinned asset for <see cref="Architecture"/>, or null.</summary>
    public LiveKitAsset? Asset => Pin.AssetFor(Architecture);

    /// <summary>Digest of whatever is at <see cref="TargetPath"/>, or null if nothing is.</summary>
    /// <remarks>
    /// A real hash of the real file rather than a note the installer left behind, for the reason
    /// §2.4 gives about never claiming "applied" from a successful write: a recorded "I installed
    /// it" would survive a restart while the file it claims to describe would not.
    /// </remarks>
    public async Task<string?> InstalledDigestAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(TargetPath))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                TargetPath,
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
            _log.LiveKitBinaryUnreadable(exception, TargetPath);
            return null;
        }
    }

    /// <summary>Whether the pinned executable is already in place, mode included.</summary>
    public async Task<bool> IsInstalledAsync(CancellationToken cancellationToken)
    {
        if (Asset is not { } asset)
        {
            return false;
        }

        var digest = await InstalledDigestAsync(cancellationToken).ConfigureAwait(false);

        return string.Equals(digest, asset.BinarySha256, StringComparison.OrdinalIgnoreCase)
            && IsExecutable(TargetPath);
    }

    /// <summary>Fetches, verifies and installs the pinned release.</summary>
    public async Task<LiveKitInstallResult> InstallAsync(CancellationToken cancellationToken)
    {
        if (Asset is not { } asset)
        {
            return LiveKitInstallResult.UnsupportedArchitecture;
        }

        if (await IsInstalledAsync(cancellationToken).ConfigureAwait(false))
        {
            return LiveKitInstallResult.AlreadyInstalled;
        }

        var archive = TargetPath + ArchiveStagingSuffix;

        try
        {
            var fetched = await FetchArchiveAsync(asset, archive, cancellationToken).ConfigureAwait(false);
            if (fetched != LiveKitInstallResult.Installed)
            {
                return Discard(archive, fetched);
            }

            var unpacked = await UnpackAsync(asset, archive, cancellationToken).ConfigureAwait(false);

            Discard(archive, LiveKitInstallResult.Installed);
            return unpacked;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.LiveKitInstallFailed(exception, Pin.Version);
            return Discard(archive, LiveKitInstallResult.WriteFailed);
        }
    }

    /// <summary>Streams the archive to <paramref name="staging"/> and checks it against the pin.</summary>
    private async Task<LiveKitInstallResult> FetchArchiveAsync(
        LiveKitAsset asset,
        string staging,
        CancellationToken cancellationToken)
    {
        var payload = await _download.OpenAsync(asset.ArchiveUrl, cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            return LiveKitInstallResult.Unreachable;
        }

        long written;
        string digest;

        await using (payload.ConfigureAwait(false))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(staging) ?? ".");

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
                if (written > asset.ArchiveSizeBytes)
                {
                    // A server that keeps sending is not serving the release the pin names, and
                    // this volume is the one the fleet database is on.
                    break;
                }

                hash.AppendData(buffer.AsSpan(0, read));
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            file.Flush(flushToDisk: true);
            digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        if (written != asset.ArchiveSizeBytes)
        {
            _log.LiveKitArchiveWrongLength(Pin.Version, written, asset.ArchiveSizeBytes);
            return LiveKitInstallResult.ArchiveSizeMismatch;
        }

        if (!string.Equals(digest, asset.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            _log.LiveKitArchiveRejected(Pin.Version, asset.FileName);
            return LiveKitInstallResult.ArchiveChecksumMismatch;
        }

        return LiveKitInstallResult.Installed;
    }

    /// <summary>Takes the executable out of a <i>verified</i> archive and puts it in place.</summary>
    private async Task<LiveKitInstallResult> UnpackAsync(
        LiveKitAsset asset,
        string archive,
        CancellationToken cancellationToken)
    {
        var staging = TargetPath + BinaryStagingSuffix;

        try
        {
            long written = 0;
            string? digest = null;

            await using (var compressed = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var gzip = new GZipStream(compressed, CompressionMode.Decompress))
            await using (var reader = new TarReader(gzip))
            {
                while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false)
                    is { } entry)
                {
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                        || !string.Equals(entry.Name, Pin.BinaryMemberName, StringComparison.Ordinal)
                        || entry.DataStream is not { } data)
                    {
                        // The archive's other member is LICENSE, and it is not wanted on disk:
                        // the licence this project has to honour is recorded in
                        // THIRD-PARTY-NOTICES.md rather than shipped as a loose file nothing reads.
                        continue;
                    }

                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    await using var file = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None);
                    var buffer = new byte[64 * 1024];

                    while (true)
                    {
                        var read = await data.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        written += read;
                        if (written > asset.BinarySizeBytes)
                        {
                            break;
                        }

                        hash.AppendData(buffer.AsSpan(0, read));
                        await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }

                    file.Flush(flushToDisk: true);
                    digest = Convert.ToHexStringLower(hash.GetHashAndReset());
                    break;
                }
            }

            if (digest is null || written != asset.BinarySizeBytes)
            {
                _log.LiveKitArchiveMalformed(Pin.Version, Pin.BinaryMemberName, asset.BinarySizeBytes);
                return Discard(staging, LiveKitInstallResult.ArchiveMalformed);
            }

            if (!string.Equals(digest, asset.BinarySha256, StringComparison.OrdinalIgnoreCase))
            {
                _log.LiveKitBinaryRejected(Pin.Version);
                return Discard(staging, LiveKitInstallResult.BinaryChecksumMismatch);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(staging, ExecutableMode);
            }

            File.Move(staging, TargetPath, overwrite: true);

            _log.LiveKitInstalled(Pin.Version, TargetPath);
            return LiveKitInstallResult.Installed;
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            // A verified archive that will not decompress is a pin whose digest and whose contents
            // disagree, which is a review failure rather than a network one — so it is named as
            // malformed rather than left to read as an outage.
            _log.LiveKitArchiveUnreadable(Pin.Version, exception.Message);
            return Discard(staging, LiveKitInstallResult.ArchiveMalformed);
        }
    }

    /// <summary>Whether the file carries the bit that makes it startable.</summary>
    /// <remarks>
    /// Its own check rather than a detail of the digest comparison, because they are different
    /// faults with the same appearance: a byte-perfect binary without the execute bit fails to
    /// start exactly as a missing one does. Windows has no such bit and never runs the bundled
    /// server, so the digest carries the whole answer there.
    /// </remarks>
    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            return (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static LiveKitInstallResult Discard(string staging, LiveKitInstallResult result)
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
            // A stale staging file is untidy and harmless: the next attempt truncates it, and it
            // is never the file anything starts.
        }

        return result;
    }

    /// <summary>The install result, as a sentence fit to show an operator.</summary>
    public static string Describe(LiveKitInstallResult result, string version) => result switch
    {
        LiveKitInstallResult.Installed => $"LiveKit {version} installed.",
        LiveKitInstallResult.AlreadyInstalled => $"LiveKit {version} was already in place.",
        LiveKitInstallResult.UnsupportedArchitecture => string.Create(
            CultureInfo.InvariantCulture,
            $"LiveKit {version} publishes no build for this machine's architecture "
            + $"({RuntimeInformation.OSArchitecture}), so the bundled call server cannot run here."),
        LiveKitInstallResult.Unreachable =>
            $"LiveKit {version} could not be downloaded — this server has no route to GitHub.",
        LiveKitInstallResult.ArchiveSizeMismatch =>
            $"LiveKit {version} was refused: the download was not the length the pin states.",
        LiveKitInstallResult.ArchiveChecksumMismatch =>
            $"LiveKit {version} was refused: the download does not match the checksum upstream published.",
        LiveKitInstallResult.ArchiveMalformed =>
            $"LiveKit {version} was refused: the archive does not hold the executable the pin names.",
        LiveKitInstallResult.BinaryChecksumMismatch =>
            $"LiveKit {version} was refused: the unpacked executable does not match the pinned digest.",
        _ => $"LiveKit {version} could not be written to this server's data directory.",
    };
}
