using System.Security.Cryptography;
using System.Text;

namespace FrameLink.Control.Authentication;

/// <summary>
/// The Fleet Manager's entire authentication story: one operator, one very long password,
/// from an environment variable only (§3.2).
/// </summary>
/// <remarks>
/// <para>
/// No user accounts, no roles, no password file, no first-run wizard that writes a hash to
/// disk. The variable is the credential, which means the operator's Compose file is the
/// single place it lives and rotating it is a container restart.
/// </para>
/// <para>
/// <b>Unconfigured is a designed state, not an error path.</b> With the variable unset the
/// process starts normally and every surface says so: the GUI renders a page naming the
/// variable, and devices are answered <c>not-configured</c> so each frame displays
/// "connected to a Fleet Manager, but it is not set up yet". The operator is usually the
/// first person to connect a frame, so the frame becomes a diagnostic for the server.
/// </para>
/// </remarks>
public sealed class OperatorCredential
{
    /// <summary>The one variable an operator has to set. Named verbatim on the setup page.</summary>
    public const string EnvironmentVariable = "FRAMELINK_OPERATOR_PASSWORD";

    /// <summary>
    /// Shortest accepted password.
    /// </summary>
    /// <remarks>
    /// §3.2 asks for "one very long password" rather than a policy, and a length floor is the
    /// only part of that which can be checked. A password below it leaves the instance
    /// unconfigured and says why, rather than starting with a credential that cannot survive
    /// an internet-exposed login route.
    /// </remarks>
    public const int MinimumLength = 24;

    private readonly byte[]? _expectedDigest;

    private OperatorCredential(byte[]? expectedDigest, string? problem)
    {
        _expectedDigest = expectedDigest;
        Problem = problem;
    }

    /// <summary>True when a usable password is configured.</summary>
    public bool IsConfigured => _expectedDigest is not null;

    /// <summary>
    /// Why the instance is unconfigured, in a sentence fit to render, or null when configured.
    /// </summary>
    public string? Problem { get; }

    /// <summary>Reads the credential from the process environment.</summary>
    public static OperatorCredential FromEnvironment() =>
        FromValue(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>Builds a credential from an explicit value. Used by tests and by the host.</summary>
    public static OperatorCredential FromValue(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return new OperatorCredential(
                null,
                $"The environment variable {EnvironmentVariable} is not set, so this Fleet "
                + "Manager has no operator password and cannot adopt any device yet.");
        }

        if (password.Length < MinimumLength)
        {
            return new OperatorCredential(
                null,
                $"The environment variable {EnvironmentVariable} is set but its value is only "
                + $"{password.Length} characters. A Fleet Manager is reachable from the "
                + $"internet, so the password must be at least {MinimumLength} characters.");
        }

        return new OperatorCredential(SHA256.HashData(Encoding.UTF8.GetBytes(password)), null);
    }

    /// <summary>Checks a candidate password against the configured one.</summary>
    /// <remarks>
    /// Compares SHA-256 digests rather than the strings themselves, so the comparison is both
    /// fixed-time and fixed-length — a timing attack learns neither the content nor the
    /// length of the real password. An unconfigured instance accepts nothing at all; there is
    /// no implicit "no password means open".
    /// </remarks>
    public bool Verify(string? candidate)
    {
        if (_expectedDigest is null || string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        Span<byte> actual = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(candidate), actual);
        return CryptographicOperations.FixedTimeEquals(actual, _expectedDigest);
    }
}
