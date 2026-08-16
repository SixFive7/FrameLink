using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace FrameLink.Agent.Local;

/// <summary>
/// The product app, <b>inside the binary</b> — §2.1.
/// </summary>
/// <remarks>
/// <para>
/// "The v1 SPA service and its on-disk git checkout are gone; the agent serves the app from its
/// own binary, so the app can never drift from the agent managing it, and the repair screen and
/// product share one local origin." Both halves of that sentence are structural here. There is no
/// path on the frame that holds the app, so there is nothing to <c>git pull</c>, nothing to fall
/// behind and nothing for a stale <c>~/FrameLink</c> to serve — and because the same server also
/// answers the repair screen, §2.7's one-origin requirement is a property of the deployment
/// rather than of the configuration.
/// </para>
/// <para>
/// <b>The asset names are normalised at read time, not at build time.</b> MSBuild's
/// <c>%(RecursiveDir)</c> carries the host's directory separator, so a binary built on Windows
/// would embed <c>app/vendor\lit-all.min.js</c> and one built in the arm64 container would embed
/// <c>app/vendor/lit-all.min.js</c>. Normalising here means the served URL is identical either
/// way, which matters because §5.2's build path is a Linux container and the test suite runs on
/// the workstation.
/// </para>
/// </remarks>
public static class EmbeddedApp
{
    /// <summary>Prefix every embedded app asset carries.</summary>
    public const string Prefix = "app/";

    /// <summary>What a request for <c>/</c> is served.</summary>
    public const string IndexPath = "index.html";

    private static readonly Lazy<IReadOnlyDictionary<string, byte[]>> Assets = new(Load);

    private static readonly Lazy<string> Build = new(Digest);

    /// <summary>
    /// A stable identifier of the app <i>this binary</i> carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It answers "did the page change", not "which version is this".</b> §2.8 updates the agent
    /// and the app travels inside it, so every release ships an <c>app/</c> whether or not a byte of
    /// it moved. Keying a refresh on <see cref="AgentBuild.Version"/> would reload the product on
    /// every agent release, including the ones that only touch a resource. This is a digest of the
    /// served bytes, so it moves when — and only when — the page a browser would load is a different
    /// page.
    /// </para>
    /// <para>
    /// The path is hashed beside the content, so a file renamed with its bytes intact, and an asset
    /// added or removed, both change the answer. Ordinal sort for the same reason the names are
    /// normalised at read time: the digest has to be identical whether the binary was built on the
    /// workstation or in §5.2's arm64 container.
    /// </para>
    /// <para>
    /// <b>SHA-256 truncated to sixteen hex characters</b>, because this is compared for equality and
    /// never for order, and the value is written into a state file and a log line a person reads.
    /// Sixty-four bits is far past what an accidental collision between two builds of one app needs.
    /// </para>
    /// </remarks>
    public static string BuildId => Build.Value;

    /// <summary>Every embedded asset path, sorted.</summary>
    public static IReadOnlyList<string> Paths
    {
        get
        {
            var names = Assets.Value.Keys.ToList();
            names.Sort(StringComparer.Ordinal);
            return names;
        }
    }

    /// <summary>The bytes of <paramref name="path"/>, or null if the app has no such asset.</summary>
    /// <remarks>
    /// A leading slash is accepted and stripped, so a caller can hand this a request target
    /// verbatim. Anything containing <c>..</c> is refused outright — the lookup is a dictionary
    /// and cannot traverse anywhere, but refusing the shape rather than relying on that keeps the
    /// guarantee legible to the next reader.
    /// </remarks>
    public static byte[]? Find(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var name = path.TrimStart('/');
        if (name.Length == 0)
        {
            name = IndexPath;
        }

        if (name.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        return Assets.Value.GetValueOrDefault(name);
    }

    /// <summary>The content type to serve <paramref name="path"/> with.</summary>
    /// <remarks>
    /// <c>text/javascript</c> and not <c>application/javascript</c>: the app loads
    /// <c>frame-app.js</c> as <c>&lt;script type="module"&gt;</c>, and a browser refuses a module
    /// whose response carries a MIME type outside the JavaScript set. Served wrongly, the page is
    /// blank with one console line — which is exactly the "broken desktop" §2.7's fallback rule
    /// exists to catch, arriving from the one place the agent itself controls.
    /// </remarks>
    public static string ContentTypeOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var dot = path.LastIndexOf('.');
        var extension = dot < 0 ? string.Empty : path[dot..];

        return extension switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".mjs" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream",
        };
    }

    private static string Digest()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (var path in Paths)
        {
            // The separator is a byte no path can contain, so "ab" + "c" and "a" + "bc" cannot
            // hash alike. Cheap here, and the alternative is a digest that is silently ambiguous
            // about which file a change was in.
            hash.AppendData(Encoding.UTF8.GetBytes(path));
            hash.AppendData([0]);
            hash.AppendData(Assets.Value[path]);
        }

        return Convert.ToHexStringLower(hash.GetCurrentHash().AsSpan(0, 8));
    }

    private static Dictionary<string, byte[]> Load()
    {
        var assembly = typeof(EmbeddedApp).Assembly;
        var assets = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var name in assembly.GetManifestResourceNames())
        {
            var normalised = name.Replace('\\', '/');
            if (!normalised.StartsWith(Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            assets[normalised[Prefix.Length..]] = buffer.ToArray();
        }

        return assets;
    }
}
