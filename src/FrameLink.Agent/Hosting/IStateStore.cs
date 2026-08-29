using System.Text;

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

    /// <summary>
    /// Writes <paramref name="content"/> as UTF-8 text, owner-writable and group-readable
    /// (<c>0640</c>), that a power cut cannot leave half-written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Staged, flushed and renamed exactly as <see cref="WriteSecretAtomic"/> does it, and for the
    /// same reason: everything reachable through this interface exists to be read <i>after</i> the
    /// power cut. The only difference left between the two is the mode the result carries, so a
    /// caller chooses between them on who may read the file and never on whether the write is safe.
    /// </para>
    /// <para>
    /// <b>Why this is not just the journal's problem.</b> A plain overwrite interrupted mid-write
    /// leaves a truncated file, and every reader of a truncated state file has to decide what to
    /// do about it. The one that decided wrong — a journal read as empty, handing the frame a
    /// fresh attempt budget and three more reboots — is fixed in its own right, but so is every
    /// other file here, because the hole was in the write and not in the reader.
    /// </para>
    /// </remarks>
    void WriteText(string name, string content);

    /// <summary>Removes <paramref name="name"/> if present.</summary>
    void Delete(string name);

    /// <summary>
    /// Renames <paramref name="name"/> to <paramref name="newName"/> without ever replacing an
    /// existing file, and says whether it happened.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the rename took place; <see langword="false"/> when
    /// <paramref name="name"/> is not there, or when <paramref name="newName"/> already is.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The one operation here that moves a file instead of writing one, and it exists so a file
    /// can be set aside <i>as evidence</i> — the same bytes, under a name nothing reads. Reading
    /// it and writing it back somewhere else would do neither job: it duplicates the content it
    /// is meant to preserve, and it leaves an instant where both names hold it or neither does.
    /// </para>
    /// <para>
    /// <b>Refusing to overwrite is the whole point of the method, not a convenience on it.</b>
    /// Something already sitting under <paramref name="newName"/> is the record of an earlier
    /// failure, and a later failure is never worth more than the first one. An answer of
    /// <see langword="false"/> is therefore a real outcome a caller has to handle out loud, not
    /// an error to retry past.
    /// </para>
    /// </remarks>
    bool TryRename(string name, string newName);

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

    /// <summary>
    /// What <see cref="File.WriteAllText(string, string?)"/> encodes with: UTF-8, no byte-order
    /// mark, and a throw rather than a substitution for text that cannot be encoded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Spelled out here so that routing <see cref="WriteText"/> through the atomic path changed
    /// where the bytes go and nothing whatever about what they are. The throwing fallback is the
    /// load-bearing half: <see cref="Encoding.UTF8"/>, the static anybody would reach for instead,
    /// substitutes <c>U+FFFD</c> and would quietly turn a value that cannot be encoded into a value
    /// that was written, where every caller of this method has always had it throw.
    /// </para>
    /// <para>
    /// <b>The no-byte-order-mark argument documents the intent and does not enforce it.</b>
    /// <see cref="Encoding.GetBytes(string)"/> never writes a preamble whatever that flag says, so
    /// what actually keeps a mark out of these files is that the bytes go straight to the stream
    /// rather than through a <see cref="StreamWriter"/>. Worth saying because a mark would break
    /// every consumer that reads one of these as a bare value — a systemd <c>EnvironmentFile</c>, a
    /// shell reading <c>device-name</c> — and because it is why the test for it asserts the bytes
    /// on disk instead of trusting the flag.
    /// </para>
    /// </remarks>
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

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
    public void WriteSecretAtomic(string name, ReadOnlySpan<byte> content) =>
        WriteAtomic(name, content, SecretMode);

    /// <inheritdoc/>
    public void WriteText(string name, string content) =>
        WriteAtomic(name, StrictUtf8.GetBytes(content), DataMode);

    /// <summary>
    /// Stages <paramref name="content"/> beside <paramref name="name"/>, flushes it to the card
    /// and renames it into place, leaving the result at <paramref name="mode"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One implementation, because there was only ever one correct way to do this.</b> The
    /// secret write had the whole dance and the text write had none of it, which put a
    /// truncatable file under every state name that was not a secret — and the mode is the only
    /// thing that ever actually differed between them. Passing it in is what let the two collapse
    /// into this, rather than a second copy of the staging-and-rename logic drifting beside the
    /// first.
    /// </para>
    /// <para>
    /// <b>The rename cannot cross a filesystem, by construction.</b> <see cref="PathOf"/> refuses
    /// any name containing a separator or <c>..</c>, so the staging file is always a direct
    /// sibling of its target inside <see cref="Root"/> — one directory, therefore one filesystem,
    /// therefore a true <c>rename(2)</c> rather than the copy-and-delete <c>File.Move</c> falls
    /// back to across a mount point. That matters on a frame specifically: <c>/boot/firmware</c>
    /// is FAT32 while <see cref="DefaultRoot"/> is on the ext4 root, and a staging file written to
    /// one and renamed onto the other would be neither atomic nor durable. Nothing here can
    /// address <c>/boot/firmware</c> at all, which is why nothing here has to handle it.
    /// </para>
    /// </remarks>
    private void WriteAtomic(string name, ReadOnlySpan<byte> content, UnixFileMode mode)
    {
        EnsureReady();

        var path = PathOf(name);
        var staging = PathOf(name + StagingSuffix);

        using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            // Locked down before the bytes exist, so there is no instant in which they are on
            // the card under a wider mode. The staging file holds the whole content for as long
            // as the write takes, so it is the file that has to carry the mode — a 0600 target
            // fed by a 0644 staging file is 0644 in practice. That is load-bearing for a secret
            // and merely correct for ordinary state, which is the reason one implementation can
            // serve both.
            _permissions.Restrict(staging, mode);
            stream.Write(content);

            // flushToDisk, not Flush(). The rename below is atomic for a reader; it says nothing
            // about whether the bytes have reached the card. A frame that came back to a
            // correctly named, zero-length state file would be exactly the corruption this whole
            // write exists to prevent.
            stream.Flush(flushToDisk: true);
        }

        File.Move(staging, path, overwrite: true);

        // The mode travels with the inode through rename(2), so on a frame the target already has
        // it the instant it appears. This restates it on the path the file actually has, which is
        // what makes the mode assertable through IFilePermissions.
        _permissions.Restrict(path, mode);
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
    /// <remarks>
    /// <para>
    /// Both names go through <see cref="PathOf"/>, so both are direct children of
    /// <see cref="Root"/> — one directory, therefore one filesystem, therefore the
    /// <c>rename(2)</c> this needs rather than the copy-and-delete <c>File.Move</c> falls back to
    /// across a mount point. It is the same argument the staging write rests on, and it holds
    /// here for the same reason: nothing reachable through this interface can name a path outside
    /// the root.
    /// </para>
    /// <para>
    /// The existence check and the move cannot be made one operation from managed code, so the
    /// platform is asked to refuse as well: <c>overwrite: false</c> means a target that appeared
    /// in between costs this call an exception rather than costing the operator the file. The
    /// check is what turns the ordinary case into an answer; the flag is what makes the answer
    /// safe.
    /// </para>
    /// </remarks>
    public bool TryRename(string name, string newName)
    {
        var from = PathOf(name);
        var to = PathOf(newName);

        if (!File.Exists(from) || File.Exists(to))
        {
            return false;
        }

        File.Move(from, to, overwrite: false);
        return true;
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
