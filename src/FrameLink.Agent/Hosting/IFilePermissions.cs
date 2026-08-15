namespace FrameLink.Agent.Hosting;

/// <summary>
/// Applies POSIX modes to files the agent creates.
/// </summary>
/// <remarks>
/// This exists as a seam for exactly one reason: §2.9 says the device keypair lives in a
/// root-only file, and "root-only" is an assertable outcome only if the mode application is
/// observable. Behind the seam it is a single call; in front of it, a test on Windows can
/// prove that the private key was written <c>0600</c> and its directory <c>0700</c>.
/// </remarks>
public interface IFilePermissions
{
    /// <summary>Restricts <paramref name="path"/> to <paramref name="mode"/>.</summary>
    void Restrict(string path, UnixFileMode mode);
}

/// <summary>
/// The real implementation: <c>chmod</c> on Unix, a no-op everywhere else.
/// </summary>
/// <remarks>
/// The one OS check in the agent's file handling lives here, rather than being repeated at
/// every call site. Windows has no POSIX mode to set, and the agent only ever runs for real
/// on Linux (§1.1) — the Windows path exists so the test suite can execute.
/// </remarks>
public sealed class PosixFilePermissions : IFilePermissions
{
    /// <summary>The shared instance.</summary>
    public static PosixFilePermissions Instance { get; } = new();

    /// <inheritdoc/>
    public void Restrict(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, mode);
    }
}
