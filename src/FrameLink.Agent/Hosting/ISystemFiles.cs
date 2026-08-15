using System.Text;

namespace FrameLink.Agent.Hosting;

/// <summary>
/// Read and write access to the parts of the filesystem the agent does not own —
/// <c>/etc</c>, <c>/boot/firmware</c>, <c>/sys</c>, <c>/var/log</c>.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="IStateStore"/>, which is the agent's own <c>/var/lib/fl-agent</c>
/// and is never touched by an update (§2.1), and from <see cref="ITextFileReader"/>, which is
/// read-only by design because the boot partition and <c>/proc</c> were only ever consulted in
/// M1. A reconciler has to write, so it gets its own seam and its own contract.
/// </para>
/// <para>
/// <b>Every path is absolute and Linux-shaped.</b> Resources spell out the real path a frame
/// has — <c>/etc/systemd/journald.conf.d/persistent.conf</c> — and the implementation decides
/// what that means. <see cref="HostSystemFiles"/> can be rooted somewhere else, which is what
/// lets the real implementation, writing real bytes and computing real hashes, be exercised on
/// a workstation against a throwaway directory instead of being replaced by a fake.
/// </para>
/// </remarks>
public interface ISystemFiles
{
    /// <summary>Whether a file exists at <paramref name="path"/>.</summary>
    bool FileExists(string path);

    /// <summary>Whether a directory exists at <paramref name="path"/>.</summary>
    bool DirectoryExists(string path);

    /// <summary>Reads <paramref name="path"/>, or null if it is absent or unreadable.</summary>
    string? ReadText(string path);

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/>, creating parent
    /// directories, replacing whatever was there.
    /// </summary>
    /// <remarks>
    /// Whole-file replacement is the only write shape offered, and that is a §2.2 decision
    /// rather than a simplification: "one file written atomically = one resource". An
    /// append-or-patch primitive would let two resources edit the same file and disagree, which
    /// is exactly the <c>cmdline.txt</c> failure the catalog warns about.
    /// </remarks>
    void WriteText(string path, string content);

    /// <summary>Deletes <paramref name="path"/> if it is there.</summary>
    void DeleteFile(string path);

    /// <summary>Creates <paramref name="path"/> and its parents if they are absent.</summary>
    /// <remarks>
    /// A directory is a setting in its own right often enough to need its own verb.
    /// <c>Storage=persistent</c> with no <c>/var/log/journal</c> silently stays volatile, which
    /// is how the August 2026 failures left no evidence for days.
    /// </remarks>
    void EnsureDirectory(string path);

    /// <summary>Immediate subdirectory paths of <paramref name="path"/>, sorted; empty if absent.</summary>
    IReadOnlyList<string> ListDirectories(string path);

    /// <summary>Immediate file paths inside <paramref name="path"/>, sorted; empty if absent.</summary>
    IReadOnlyList<string> ListFiles(string path);
}

/// <summary>
/// The real filesystem, optionally rooted somewhere other than <c>/</c>.
/// </summary>
/// <remarks>
/// The root is not a sandbox and is not a security boundary — it is a test affordance, and the
/// production instance is rooted at <c>/</c>. Its value is that the code under test is the code
/// that ships: the same path strings, the same UTF-8 bytes, the same directory creation, the
/// same hashing. Only the prefix differs.
/// </remarks>
public sealed class HostSystemFiles : ISystemFiles
{
    private static readonly char[] LeadingSeparators = ['/', '\\'];
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string? _root;

    /// <summary>Creates an instance rooted at <paramref name="root"/>, or at <c>/</c> when null.</summary>
    public HostSystemFiles(string? root = null) =>
        _root = string.IsNullOrEmpty(root) ? null : Path.GetFullPath(root);

    /// <summary>The instance a frame uses.</summary>
    public static HostSystemFiles Instance { get; } = new();

    /// <summary>Turns an absolute Linux path into the path this instance actually touches.</summary>
    public string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (_root is null)
        {
            return path;
        }

        var relative = path.TrimStart(LeadingSeparators).Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(_root, relative);
    }

    /// <inheritdoc/>
    public bool FileExists(string path) => File.Exists(Resolve(path));

    /// <inheritdoc/>
    public bool DirectoryExists(string path) => Directory.Exists(Resolve(path));

    /// <inheritdoc/>
    public string? ReadText(string path)
    {
        var resolved = Resolve(path);

        try
        {
            return File.Exists(resolved) ? File.ReadAllText(resolved) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Unreadable and absent collapse to the same answer on purpose: every caller is
            // observing, and "there is nothing here to compare against" is the truth in both
            // cases. A resource that cannot read its own setting reports drift, which is the
            // conservative outcome — it retries and escalates rather than claiming InSync.
            return null;
        }
    }

    /// <inheritdoc/>
    public void WriteText(string path, string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var resolved = Resolve(path);
        var directory = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // No BOM. A byte-order mark in front of `[Journal]` makes systemd skip the section
        // header, which produces a file that looks correct in an editor and does nothing.
        File.WriteAllText(resolved, content, Utf8NoBom);
    }

    /// <inheritdoc/>
    public void DeleteFile(string path)
    {
        var resolved = Resolve(path);
        if (File.Exists(resolved))
        {
            File.Delete(resolved);
        }
    }

    /// <inheritdoc/>
    public void EnsureDirectory(string path) => Directory.CreateDirectory(Resolve(path));

    /// <inheritdoc/>
    public IReadOnlyList<string> ListDirectories(string path) =>
        List(path, resolved => Directory.EnumerateDirectories(resolved));

    /// <inheritdoc/>
    public IReadOnlyList<string> ListFiles(string path) =>
        List(path, resolved => Directory.EnumerateFiles(resolved));

    private List<string> List(string path, Func<string, IEnumerable<string>> enumerate)
    {
        var resolved = Resolve(path);
        if (!Directory.Exists(resolved))
        {
            return [];
        }

        try
        {
            var prefix = path.TrimEnd('/');
            var entries = enumerate(resolved)
                .Select(entry => prefix + "/" + Path.GetFileName(entry))
                .ToList();

            entries.Sort(StringComparer.Ordinal);
            return entries;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
