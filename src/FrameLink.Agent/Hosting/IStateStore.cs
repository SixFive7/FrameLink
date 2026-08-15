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
    public void WriteText(string name, string content)
    {
        EnsureReady();
        var path = PathOf(name);
        File.WriteAllText(path, content);
        _permissions.Restrict(path, DataMode);
    }

    /// <inheritdoc/>
    public void Delete(string name) => File.Delete(PathOf(name));

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
