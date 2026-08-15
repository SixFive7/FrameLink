using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Kiosk;

/// <summary>
/// The upstream Immich Kiosk release the agent fetches, pinned.
/// </summary>
/// <remarks>
/// <para>
/// <b>§2.1, decision 41: Immich Kiosk stays upstream.</b> It is a mature product with a team behind
/// it and v2 does not reimplement it. What v2 changes is the delivery: the agent fetches the pinned
/// release, verifies its checksum and <b>supervises it as a child process</b>, which removes Docker
/// from the frame entirely — and with it the corrupt-network-store failure class that began the
/// August 2026 incident chain.
/// </para>
/// <para>
/// <b>Fetched, never vendored.</b> Immich Kiosk is AGPL-3.0. Fetching from upstream rather than
/// redistributing keeps the source-offer obligation with the publisher, off this project and off
/// every self-hoster. No byte of this release is in this repository, and the pin below is a
/// reviewable record of which bytes the agent will go and get.
/// </para>
/// <para>
/// <b>Two digests, because they answer different questions</b> — the same split
/// <c>BaseImagePin</c> makes for the base OS image. <see cref="ArchiveSha256"/> is the value
/// upstream publishes in <c>immich-kiosk_0.42.0_checksums.txt</c>, so it is what a human reviews
/// the pin against and what the download is checked against <i>before</i> anything is unpacked.
/// <see cref="BinarySha256"/> is the digest of the executable inside the archive, which is the file
/// that ends up on the frame and the file <c>kiosk.binary.pinned-release</c> observes on every
/// pass; nobody publishes it, so it is measured here. Verifying only the published one would leave
/// the installed artifact unchecked from the second boot onwards.
/// </para>
/// <para>
/// <b>Verified live 2026-08-15 @ v0.42.0</b>, and every field below was measured rather than
/// recalled: the release API answered <c>v0.42.0</c> (published 2026-08-04), the published
/// checksums file names <see cref="ArchiveSha256"/> for
/// <c>immich-kiosk_Linux_arm64.tar.gz</c>, the downloaded archive is
/// <see cref="ArchiveSizeBytes"/> bytes and hashes to that same value, and the archive holds
/// exactly three members — <c>LICENSE</c>, <c>README.md</c> and the <c>immich-kiosk</c> executable
/// at mode 0755. The executable's own Go build record reads
/// <c>-ldflags="-X main.version=0.42.0 -s -w"</c> with <c>CGO_ENABLED=0 GOOS=linux GOARCH=arm64</c>,
/// which is where "static Go binary, linux-arm64, 0.42.0" is checked rather than assumed. The
/// ledger entry <c>immich-kiosk</c> in <c>upstream-review.json</c> is the §7.1 record of that
/// review and a test ties the two together.
/// </para>
/// <para>
/// <b>§2.1 says "~7.4 MB" and both numbers here are larger than that reads.</b> The published
/// archive is 7.36 MiB, which is the ~7.4 MB the specification means; the executable inside it is
/// 17.7 MiB, because the archive is compressed. Recorded explicitly so that nobody later "corrects"
/// the binary length against the specification's figure.
/// </para>
/// </remarks>
public sealed record KioskReleasePin
{
    /// <summary>Immich Kiosk v0.42.0, verified 2026-08-15.</summary>
    public static KioskReleasePin Current { get; } = new()
    {
        Version = "0.42.0",
        AssetFileName = "immich-kiosk_Linux_arm64.tar.gz",
        ArchiveUrl = new Uri(
            "https://github.com/damongolding/immich-kiosk/releases/download/"
            + "v0.42.0/immich-kiosk_Linux_arm64.tar.gz"),
        ChecksumsUrl = new Uri(
            "https://github.com/damongolding/immich-kiosk/releases/download/"
            + "v0.42.0/immich-kiosk_0.42.0_checksums.txt"),
        ArchiveSha256 = "93476535e86dd6914b1b8e644fdc147b4770903434f0db15d0ee469e0857e423",
        ArchiveSizeBytes = 7_712_323,
        BinaryMemberName = "immich-kiosk",
        BinarySha256 = "162043f2ec65e72dae41c3b7885df4607951e1a69543a30b46d5a3dbb90ec81c",
        BinarySizeBytes = 18_546_850,
        ReviewedUtc = new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero),
    };

    /// <summary>The release, as upstream names it without the <c>v</c>.</summary>
    public required string Version { get; init; }

    /// <summary>The published asset for this frame's architecture.</summary>
    public required string AssetFileName { get; init; }

    /// <summary>Where that asset is published.</summary>
    public required Uri ArchiveUrl { get; init; }

    /// <summary>The checksums file published beside it. What a human reviews the pin against.</summary>
    public required Uri ChecksumsUrl { get; init; }

    /// <summary>The digest upstream publishes for the archive.</summary>
    public required string ArchiveSha256 { get; init; }

    /// <summary>Length of the published archive.</summary>
    public required long ArchiveSizeBytes { get; init; }

    /// <summary>Name of the executable member inside the archive.</summary>
    public required string BinaryMemberName { get; init; }

    /// <summary>Digest of that executable. What the resource observes on the frame.</summary>
    public required string BinarySha256 { get; init; }

    /// <summary>Length of that executable.</summary>
    public required long BinarySizeBytes { get; init; }

    /// <summary>When a human last checked this pin against upstream (§7.1's stamp).</summary>
    public required DateTimeOffset ReviewedUtc { get; init; }

    /// <summary>The release tag, as the GitHub API and the download path spell it.</summary>
    public string Tag => "v" + Version;

    /// <summary>The command a person runs to check this pin by hand.</summary>
    /// <remarks>
    /// Not used by anything the agent runs. It exists because §7.1 makes an upstream artifact's
    /// version and checksum "reviewable facts, not memory", and a reviewable fact needs a stated
    /// way of being re-checked by whoever next reads this file.
    /// </remarks>
    public string ReviewCommand =>
        $"curl -fsSL {ChecksumsUrl} | grep {AssetFileName} "
        + $"&& curl -fsSL {ArchiveUrl} | sha256sum";
}

/// <summary>Why an install did not happen.</summary>
public enum KioskInstallResult
{
    /// <summary>The pinned binary is in place and hashes to the pin.</summary>
    Installed,

    /// <summary>The pinned binary was already in place; nothing was fetched.</summary>
    AlreadyInstalled,

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
/// A seam for the same reason every other outside surface has one: the agent runs for real only on
/// a frame, and the whole of the install has to be assertable on a workstation with no network.
/// </remarks>
public interface IKioskDownload
{
    /// <summary>Opens <paramref name="url"/> for reading, or null if it cannot be fetched.</summary>
    Task<Stream?> OpenAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>A download that never answers.</summary>
/// <remarks>
/// For a catalog built somewhere nothing may reach the network — the graph tests, and anything that
/// only wants to inspect the resource set. <c>kiosk.binary.pinned-release</c> then reports the fetch
/// as unreachable, which is the honest answer and the same one a frame with no route gets.
/// </remarks>
public sealed class UnreachableKioskDownload : IKioskDownload
{
    /// <summary>The shared instance.</summary>
    public static UnreachableKioskDownload Instance { get; } = new();

    /// <inheritdoc/>
    public Task<Stream?> OpenAsync(Uri url, CancellationToken cancellationToken) => Task.FromResult<Stream?>(null);
}

/// <summary>Fetches over plain HTTPS.</summary>
public sealed class HttpKioskDownload : IKioskDownload
{
    private readonly HttpClient _client;
    private readonly IAgentLog _log;

    /// <summary>Creates a download over <paramref name="client"/>.</summary>
    public HttpKioskDownload(HttpClient client, IAgentLog log)
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
            var response = await _client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
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

/// <summary>
/// <b>Fetch → verify SHA-256 → unpack → verify again → atomic rename.</b> The Immich Kiosk half of
/// §2.1's "fetches the pinned release, verifies its checksum".
/// </summary>
/// <remarks>
/// <para>
/// Deliberately the same shape as <c>FileBinarySwap</c>, which does this for the agent's own
/// binary, and for the same reasons: verification strictly precedes anything being put in place,
/// the staging files live beside the target because <c>rename(2)</c> is only atomic within one
/// filesystem, and the staging file is fsynced before the rename because atomicity and durability
/// are different promises and only the pair of them survives a power cut.
/// </para>
/// <para>
/// <b>The archive is checked before it is opened, and the executable again after it is out.</b>
/// A gzip stream from an unverified source is a decompressor being fed by whoever answered the
/// URL, and the whole point of a pinned checksum is that this never happens. So the download is
/// hashed to a staging file first and the archive is refused outright on any mismatch; only then
/// is it unpacked, and the member that comes out is length- and digest-checked against the pin
/// before it is given the executable bit. Nothing unverified is ever executed, and nothing
/// unverified ever reaches the target path.
/// </para>
/// <para>
/// <b>Bounded by the pin's own length.</b> The copy stops the moment it has read more bytes than
/// the pin states, so a server that keeps sending fills no disk. That is the same guard the agent
/// updater uses, and it matters more here: <c>/var/lib/fl-agent</c> is on the SD card the frame
/// boots from.
/// </para>
/// </remarks>
public sealed class KioskInstaller
{
    /// <summary>Suffix of the file the download is staged into.</summary>
    public const string ArchiveStagingSuffix = ".tar.gz.part";

    /// <summary>Suffix of the file the executable is unpacked into before the rename.</summary>
    public const string BinaryStagingSuffix = ".new";

    private const UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly IKioskDownload _download;
    private readonly IFilePermissions _permissions;
    private readonly IAgentLog _log;

    /// <summary>Creates an installer that puts the executable at <paramref name="targetPath"/>.</summary>
    public KioskInstaller(
        string targetPath,
        IKioskDownload download,
        IFilePermissions permissions,
        IAgentLog log,
        KioskReleasePin? pin = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(log);

        TargetPath = targetPath;
        Pin = pin ?? KioskReleasePin.Current;
        _download = download;
        _permissions = permissions;
        _log = log;
    }

    /// <summary>Where the executable lives on the frame.</summary>
    public string TargetPath { get; }

    /// <summary>The release this installer puts in place.</summary>
    public KioskReleasePin Pin { get; }

    /// <summary>Digest of whatever is at <see cref="TargetPath"/>, or null if nothing is.</summary>
    /// <remarks>
    /// This is <c>kiosk.binary.pinned-release</c>'s Observe, and it is a real hash of the real file
    /// rather than a note the installer left behind. §2.4's reason applies exactly: "applied" is
    /// never claimed from a successful write, only from an observation after the setting had to
    /// survive a boot — and a recorded "I installed it" would survive a boot while the file it
    /// claims to describe would not.
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
            _log.Warn($"The Immich Kiosk binary at {TargetPath} could not be read: {exception.Message}");
            return null;
        }
    }

    /// <summary>Whether the pinned executable is already in place, mode included.</summary>
    public async Task<bool> IsInstalledAsync(CancellationToken cancellationToken)
    {
        var digest = await InstalledDigestAsync(cancellationToken).ConfigureAwait(false);

        return string.Equals(digest, Pin.BinarySha256, StringComparison.OrdinalIgnoreCase)
            && IsExecutable(TargetPath);
    }

    /// <summary>Fetches, verifies and installs the pinned release.</summary>
    public async Task<KioskInstallResult> InstallAsync(CancellationToken cancellationToken)
    {
        if (await IsInstalledAsync(cancellationToken).ConfigureAwait(false))
        {
            return KioskInstallResult.AlreadyInstalled;
        }

        var archive = TargetPath + ArchiveStagingSuffix;

        try
        {
            var fetched = await FetchArchiveAsync(archive, cancellationToken).ConfigureAwait(false);
            if (fetched != KioskInstallResult.Installed)
            {
                return Discard(archive, fetched);
            }

            var unpacked = await UnpackAsync(archive, cancellationToken).ConfigureAwait(false);

            Discard(archive, KioskInstallResult.Installed);
            return unpacked;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Fail($"The Immich Kiosk release could not be written: {exception.Message}");
            return Discard(archive, KioskInstallResult.WriteFailed);
        }
    }

    /// <summary>Streams the archive to <paramref name="staging"/> and checks it against the pin.</summary>
    private async Task<KioskInstallResult> FetchArchiveAsync(string staging, CancellationToken cancellationToken)
    {
        var payload = await _download.OpenAsync(Pin.ArchiveUrl, cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            return KioskInstallResult.Unreachable;
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
                if (written > Pin.ArchiveSizeBytes)
                {
                    // A server that keeps sending is not serving the release the pin names, and
                    // /var/lib/fl-agent is on the card this frame boots from.
                    break;
                }

                hash.AppendData(buffer.AsSpan(0, read));
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            file.Flush(flushToDisk: true);
            digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        if (written != Pin.ArchiveSizeBytes)
        {
            _log.Fail(string.Create(
                CultureInfo.InvariantCulture,
                $"Immich Kiosk {Pin.Version} rejected: {written} bytes fetched, {Pin.ArchiveSizeBytes} expected."));
            return KioskInstallResult.ArchiveSizeMismatch;
        }

        if (!string.Equals(digest, Pin.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            _log.Fail($"Immich Kiosk {Pin.Version} rejected: the download does not match the published checksum.");
            return KioskInstallResult.ArchiveChecksumMismatch;
        }

        return KioskInstallResult.Installed;
    }

    /// <summary>Takes the executable out of a <i>verified</i> archive and puts it in place.</summary>
    private async Task<KioskInstallResult> UnpackAsync(string archive, CancellationToken cancellationToken)
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
                        // LICENSE and README.md travel in the same archive and are not wanted on
                        // the frame: §2.1 allows no supplemental program files, and the licence
                        // this project has to honour is recorded in THIRD-PARTY-NOTICES.md rather
                        // than shipped as a loose file nothing reads.
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
                        if (written > Pin.BinarySizeBytes)
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

            if (digest is null || written != Pin.BinarySizeBytes)
            {
                _log.Fail(
                    $"Immich Kiosk {Pin.Version} rejected: the archive does not hold a "
                    + $"{Pin.BinarySizeBytes}-byte '{Pin.BinaryMemberName}'.");
                return Discard(staging, KioskInstallResult.ArchiveMalformed);
            }

            if (!string.Equals(digest, Pin.BinarySha256, StringComparison.OrdinalIgnoreCase))
            {
                _log.Fail($"Immich Kiosk {Pin.Version} rejected: the unpacked binary does not match the pinned digest.");
                return Discard(staging, KioskInstallResult.BinaryChecksumMismatch);
            }

            _permissions.Restrict(staging, ExecutableMode);
            File.Move(staging, TargetPath, overwrite: true);

            _log.Info($"Immich Kiosk {Pin.Version} installed at {TargetPath}.");
            return KioskInstallResult.Installed;
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            // A verified archive that will not decompress is a pin whose digest and whose contents
            // disagree, which is a review failure rather than a network one — so it is named as
            // malformed rather than left to read as an outage.
            _log.Fail($"Immich Kiosk {Pin.Version} rejected: the archive could not be read — {exception.Message}");
            return Discard(staging, KioskInstallResult.ArchiveMalformed);
        }
    }

    /// <summary>Whether the file carries the bit that makes it startable.</summary>
    /// <remarks>
    /// Its own check rather than a detail of the digest comparison, because they are different
    /// faults with the same appearance: a frame whose binary is byte-perfect and not executable
    /// shows exactly the blank screen a frame with no binary at all does. The Windows branch is the
    /// same one <c>PosixFilePermissions</c> takes — the agent runs for real only on Linux (§1.1) and
    /// the suite runs here, so the bit is checked where it exists and the digest carries the rest.
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

    private static KioskInstallResult Discard(string staging, KioskInstallResult result)
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
            // A stale staging file is untidy and harmless: the next attempt truncates it, and it is
            // never the file anything starts.
        }

        return result;
    }
}
