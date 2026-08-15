namespace FrameLink.Agent.Hosting;

/// <summary>
/// The agent's persisted state directory — <c>/var/lib/fl-agent</c> on a frame.
/// </summary>
/// <remarks>
/// §2.1: "Persisted state is data, not program files ... Never touched by an update." The
/// updater swaps the binary and nothing else, so everything reachable through this interface
/// survives both a restart and a version change — which is precisely what makes the device
/// keypair a <i>permanent</i> identity (§2.9, §3.3).
/// </remarks>
public interface IStateStore
{
    /// <summary>Absolute path of the state directory.</summary>
    string Root { get; }

    /// <summary>Creates the directory if absent and restricts it to the owner.</summary>
    void EnsureReady();

    /// <summary>Whether <paramref name="name"/> exists.</summary>
    bool Exists(string name);

    /// <summary>Reads <paramref name="name"/>, or <see langword="null"/> if it does not exist.</summary>
    byte[]? ReadBytes(string name);

    /// <summary>Reads <paramref name="name"/> as UTF-8 text, or <see langword="null"/> if absent.</summary>
    string? ReadText(string name);

    /// <summary>Writes <paramref name="content"/> as an owner-only file (<c>0600</c>).</summary>
    void WriteSecret(string name, ReadOnlySpan<byte> content);

    /// <summary>
    /// Writes <paramref name="content"/> as an owner-only file (<c>0600</c>) that a power cut
    /// cannot leave half-written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same dance <see cref="Update.FileBinarySwap"/> performs on the binary, for the same
    /// reason and with the same two promises kept apart: <c>rename(2)</c> makes the replacement
    /// atomic with respect to <i>observers</i> — the file is either wholly the old content or
    /// wholly the new one — while <c>fsync</c> makes the new content <i>durable</i>. Only the pair
    /// of them survives a power cut, and a state file that exists to be read after a power cut has
    /// to survive one.
    /// </para>
    /// <para>
    /// A crash between the two steps leaves a stale <c>&lt;name&gt;.new</c> beside the real file.
    /// Nothing ever reads it and the next write truncates it, so the cost of that case is one
    /// orphaned file and no lost state.
    /// </para>
    /// </remarks>
    void WriteSecretAtomic(string name, ReadOnlySpan<byte> content);

    /// <summary>Writes <paramref name="content"/> as UTF-8 text, owner-writable (<c>0640</c>).</summary>
    void WriteText(string name, string content);

    /// <summary>Removes <paramref name="name"/> if present.</summary>
    void Delete(string name);

    /// <summary>Absolute path of <paramref name="name"/> inside the store.</summary>
    string PathOf(string name);
}

/// <summary>A state store backed by a real directory.</summary>
public sealed class FileStateStore : IStateStore
{
    /// <summary>Where the agent keeps its state on a frame (§2.1).</summary>
    public const string DefaultRoot = "/var/lib/fl-agent";

    /// <summary>Suffix of the file an atomic write stages into before renaming it into place.</summary>
    public const string StagingSuffix = ".new";

    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode SecretMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode DataMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

    private readonly IFilePermissions _permissions;

    /// <summary>Creates a store rooted at <paramref name="root"/>.</summary>
    public FileStateStore(string root, IFilePermissions permissions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(permissions);

        Root = root;
        _permissions = permissions;
    }

    /// <inheritdoc/>
    public string Root { get; }

    /// <inheritdoc/>
    public void EnsureReady()
    {
        Directory.CreateDirectory(Root);
        _permissions.Restrict(Root, DirectoryMode);
    }

    /// <inheritdoc/>
    public bool Exists(string name) => File.Exists(PathOf(name));

    /// <inheritdoc/>
    public byte[]? ReadBytes(string name)
    {
        var path = PathOf(name);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <inheritdoc/>
    public string? ReadText(string name)
    {
        var path = PathOf(name);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <inheritdoc/>
    public void WriteSecret(string name, ReadOnlySpan<byte> content)
    {
        EnsureReady();
        var path = PathOf(name);

        // Create the file empty and lock it down *before* the secret is written into it, so
        // there is no window in which the bytes exist under a wider mode.
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            _permissions.Restrict(path, SecretMode);
            stream.Write(content);
        }

        _permissions.Restrict(path, SecretMode);
    }

    /// <inheritdoc/>
    public void WriteSecretAtomic(string name, ReadOnlySpan<byte> content)
    {
        EnsureReady();

        var path = PathOf(name);
        var staging = PathOf(name + StagingSuffix);

        using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            // Locked down before the bytes exist, exactly as WriteSecret does it. The staging
            // file holds the whole secret for as long as the write takes, so it is the file that
            // has to be narrow — a 0600 target fed by a 0644 staging file is 0644 in practice.
            _permissions.Restrict(staging, SecretMode);
            stream.Write(content);

            // flushToDisk, not Flush(). The rename below is atomic for a reader; it says nothing
            // about whether the bytes have reached the card. A frame that came back to a
            // correctly named, zero-length state file would be exactly the corruption this whole
            // write exists to prevent.
            stream.Flush(flushToDisk: true);
        }

        File.Move(staging, path, overwrite: true);

        // The mode travels with the inode through rename(2), so on a frame the target is already
        // 0600 the instant it appears. This restates it on the path the file actually has — which
        // is what makes "root-only" assertable through IFilePermissions, and what covers the one
        // case where File.Move is a copy rather than a rename (a target on another filesystem,
        // impossible here only because the staging file is deliberately a sibling).
        _permissions.Restrict(path, SecretMode);
    }

    /// <inheritdoc/>
    public void WriteText(string name, string content)
    {
        EnsureReady();
        var path = PathOf(name);
        File.WriteAllText(path, content);
        _permissions.Restrict(path, DataMode);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Absent file and absent directory are both nothing to do. <c>File.Delete</c> throws
    /// <see cref="DirectoryNotFoundException"/> for the second case, which would make the very
    /// first "clear the offline buffer" on a fresh frame fail for the reason that it was
    /// already clear.
    /// </remarks>
    public void Delete(string name)
    {
        var path = PathOf(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <inheritdoc/>
    public string PathOf(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (name.Contains('/', StringComparison.Ordinal)
            || name.Contains('\\', StringComparison.Ordinal)
            || name.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{name}' is not a plain file name.", nameof(name));
        }

        return Path.Combine(Root, name);
    }
}
