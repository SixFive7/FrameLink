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

    /// <summary>
    /// The POSIX mode of <paramref name="path"/>, or null if it is absent or unreadable.
    /// </summary>
    /// <remarks>
    /// A mode bit is a setting in its own right, which is why it needs a reader rather than only
    /// the writer <see cref="IFilePermissions"/> already provides. The catalog's
    /// <c>labwc.autostart.executable</c> exists because labwc <b>silently ignores</b> a
    /// non-executable autostart: perfect content plus a missing bit produces a frame that boots to
    /// a bare compositor with no rotation and no browser, and nothing logs a complaint. A resource
    /// cannot observe that without being able to read the mode back.
    /// </remarks>
    UnixFileMode? ModeOf(string path);

    /// <summary>Sets the POSIX mode of <paramref name="path"/>.</summary>
    void SetMode(string path, UnixFileMode mode);

    /// <summary>
    /// The real filesystem path this instance would touch for <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// For the one thing this interface deliberately does not offer: binary I/O. Text is the whole
    /// of what a reconciled <i>setting</i> ever is, so <see cref="ReadText"/> and
    /// <see cref="WriteText"/> are the write surface and stay that way. A pinned upstream artifact
    /// is not a setting — it is 1.8 MB of ELF that has to be streamed, hashed and renamed into
    /// place — and <c>XvfHostInstaller</c> does that with the same real file APIs
    /// <c>KioskInstaller</c> and <c>FileBinarySwap</c> use. Handing it the resolved path is what
    /// keeps a single logical path (<c>/var/lib/fl-agent/xvf3800/...</c>) meaning the same place to
    /// the installer and to <c>XvfHost</c>, on a frame and under a test root alike.
    /// </remarks>
    string Resolve(string path);
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

    /// <summary>
    /// Modes this instance was asked to set, on a filesystem that has none.
    /// </summary>
    /// <remarks>
    /// The same test affordance as <see cref="_root"/>, and it is needed for the same reason:
    /// <see cref="File.GetUnixFileMode(string)"/> and <see cref="File.SetUnixFileMode(string,
    /// UnixFileMode)"/> throw <see cref="PlatformNotSupportedException"/> on Windows, so without
    /// this a mode-bearing resource could not be exercised at all off a frame — and
    /// <c>labwc.autostart.executable</c> is precisely the resource whose whole content is a mode
    /// bit. On Linux, which is the only place a frame ever runs, this dictionary is never touched
    /// and every read and write goes to the real inode.
    /// </remarks>
    private readonly Dictionary<string, UnixFileMode> _emulatedModes = new(StringComparer.Ordinal);

    private readonly string? _root;

    /// <summary>Creates an instance rooted at <paramref name="root"/>, or at <c>/</c> when null.</summary>
    public HostSystemFiles(string? root = null) =>
        _root = string.IsNullOrEmpty(root) ? null : Path.GetFullPath(root);

    /// <summary>The instance a frame uses.</summary>
    public static HostSystemFiles Instance { get; } = new();

    /// <summary>
    /// What a newly written file is assumed to be, where the filesystem cannot say.
    /// </summary>
    /// <remarks>
    /// <c>0664</c>, matching the <c>umask 002</c> a Raspberry Pi OS login shell runs with — which
    /// is why the v1 reference shows <c>-rw-rw-r--</c> on every file the guides created and
    /// <c>775</c> on the one they chmod'd. The value matters only on Windows, and only so that a
    /// file that has never been chmod'd reads as <i>not executable</i> rather than as unknown.
    /// </remarks>
    public const UnixFileMode DefaultFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
        | UnixFileMode.OtherRead;

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
    public UnixFileMode? ModeOf(string path)
    {
        var resolved = Resolve(path);

        if (!File.Exists(resolved) && !Directory.Exists(resolved))
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            lock (_emulatedModes)
            {
                return _emulatedModes.TryGetValue(resolved, out var emulated) ? emulated : DefaultFileMode;
            }
        }

        try
        {
            return File.GetUnixFileMode(resolved);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public void SetMode(string path, UnixFileMode mode)
    {
        var resolved = Resolve(path);

        if (OperatingSystem.IsWindows())
        {
            lock (_emulatedModes)
            {
                _emulatedModes[resolved] = mode;
            }

            return;
        }

        File.SetUnixFileMode(resolved, mode);
    }

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
