using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using FrameLink.Protocol;

namespace FrameLink.Control.Updates;

/// <summary>
/// The update feed: what agent build this Fleet Manager serves, per runtime identifier.
/// </summary>
/// <remarks>
/// <para>
/// §1.2 principle 4 in one class. The container is the feed — no S3, no CDN, no
/// project-operated bucket in anyone's deployment — so agent version is a function of server
/// version and the wire protocol always matches. Reverting the container tag reverts the
/// fleet within the hour, because agents <i>match</i> the served version rather than taking
/// the greater of the two.
/// </para>
/// <para>
/// The binaries are produced by a separate workstream into a directory this class only ever
/// reads. Two layouts are accepted, and the version is taken from a sidecar file when one
/// exists. When it does not, the version is derived from the content hash — which is not a
/// fallback so much as the honest form of the same rule: if the served bytes change, the
/// served version changes, and every agent converges on the new one at its next hourly check.
/// </para>
/// </remarks>
public sealed class AgentReleaseCatalog(ControlOptions options, ILogger<AgentReleaseCatalog> logger)
{
    /// <summary>The frame is a Pi 5, so this is the runtime identifier that matters (§1.1).</summary>
    public const string PrimaryRuntimeIdentifier = "linux-arm64";

    private const string BinaryName = "fl-agent";

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    /// <summary>Metadata for the runtime identifier the fleet actually runs, or null.</summary>
    public AgentRelease? TryGetDefault() => TryGet(PrimaryRuntimeIdentifier);

    /// <summary>Metadata for one runtime identifier, or null if nothing is published for it.</summary>
    /// <remarks>
    /// The SHA-256 is recomputed only when the file's length or write time changes, so the
    /// hourly poll of an entire fleet costs a stat call each rather than a rehash each.
    /// </remarks>
    public AgentRelease? TryGet(string runtimeIdentifier)
    {
        if (!IsSafeRuntimeIdentifier(runtimeIdentifier))
        {
            return null;
        }

        var path = ResolveBinaryPath(runtimeIdentifier);
        if (path is null)
        {
            return null;
        }

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return null;
        }

        if (_cache.TryGetValue(runtimeIdentifier, out var cached)
            && cached.Length == info.Length
            && cached.WriteTimeUtc == info.LastWriteTimeUtc)
        {
            return cached.Release;
        }

        string hash;
        try
        {
            using var stream = File.OpenRead(path);
            hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (IOException exception)
        {
            // A half-written binary from the build workstream is a transient state, not a
            // reason to serve wrong metadata. The next poll picks it up.
            logger.ReleaseHashFailed(exception, path);
            return null;
        }

        var release = new AgentRelease
        {
            Version = ReadVersion(path, hash),
            RuntimeIdentifier = runtimeIdentifier,
            Sha256 = hash,
            SizeBytes = info.Length,

            // Server-relative and versionless (§4.2). The one route an agent too old to speak
            // the protocol must still be able to use to repair itself.
            Url = $"/agent/binary/{runtimeIdentifier}",
        };

        _cache[runtimeIdentifier] = new CacheEntry(release, info.Length, info.LastWriteTimeUtc);
        return release;
    }

    /// <summary>Absolute path of the served binary for a runtime identifier, or null.</summary>
    public string? ResolveBinaryPath(string runtimeIdentifier)
    {
        if (!IsSafeRuntimeIdentifier(runtimeIdentifier) || string.IsNullOrEmpty(options.ReleaseDirectory))
        {
            return null;
        }

        // Layout A: build/out/<rid>/fl-agent — one directory per target.
        var nested = Path.Combine(options.ReleaseDirectory, runtimeIdentifier, BinaryName);
        if (File.Exists(nested))
        {
            return Path.GetFullPath(nested);
        }

        // Layout B: build/out/fl-agent-<rid> — flat, one file per target.
        var flat = Path.Combine(options.ReleaseDirectory, $"{BinaryName}-{runtimeIdentifier}");
        return File.Exists(flat) ? Path.GetFullPath(flat) : null;
    }

    /// <summary>
    /// Rejects anything that is not a plain runtime identifier.
    /// </summary>
    /// <remarks>
    /// The value arrives in a URL on a route with no authentication, and it is concatenated
    /// into a filesystem path. Allowing only lowercase letters, digits and hyphens is what
    /// keeps <c>/agent/binary/..%2f..%2fetc%2fpasswd</c> a 404 rather than a disclosure.
    /// </remarks>
    private static bool IsSafeRuntimeIdentifier(string value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= 32
        && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private string ReadVersion(string binaryPath, string hash)
    {
        var sidecar = binaryPath + ".version";
        if (File.Exists(sidecar))
        {
            try
            {
                var declared = File.ReadAllText(sidecar).Trim();
                if (declared.Length is > 0 and <= 128)
                {
                    return declared;
                }
            }
            catch (IOException exception)
            {
                logger.ReleaseVersionUnreadable(exception, sidecar);
            }
        }

        // Content-addressed. The served version is then a function of the served bytes by
        // construction, which is the property §2.8 needs and the only one it needs.
        return string.Create(
            CultureInfo.InvariantCulture,
            $"0.0.0+{hash[..12]}");
    }

    private sealed record CacheEntry(AgentRelease Release, long Length, DateTime WriteTimeUtc);
}
