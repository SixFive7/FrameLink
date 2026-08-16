namespace FrameLink.Control.Storage;

/// <summary>
/// The API key and secret every call token in this fleet is signed with (§3.2, §3.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a setting, and the distinction is load-bearing.</b> Everything in
/// <see cref="ISettingsStore"/> is a value the Fleet Manager <i>pushes to frames</i>; this is the
/// one credential that must never leave the server. §3.7 makes the Fleet Manager the owner of
/// the API secret precisely so a frame holds a token and nothing else, which is the property
/// that makes rotation possible at all — a fleet where every frame knew the secret could not be
/// rotated without touching every frame.
/// </para>
/// <para>
/// So it lives in its own store with no route that returns it and no path into a settings frame.
/// The only thing that reads it is the token minter.
/// </para>
/// </remarks>
public interface ILiveKitStore
{
    /// <summary>The stored credential, or null if none has been generated yet.</summary>
    Task<LiveKitCredential?> FindAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns the stored credential, generating one on first call (§3.2's "generated
    /// automatically").
    /// </summary>
    /// <remarks>
    /// Atomic: the insert is conditional and the read-back is inside the same transaction, so two
    /// callers racing at start-up cannot end up having signed tokens with two different secrets.
    /// </remarks>
    Task<LiveKitCredential> EnsureAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the secret, keeping the key, and returns what is now stored.
    /// </summary>
    /// <remarks>
    /// Every token signed with the old secret stops verifying the instant LiveKit reloads, which
    /// is the point: this is the revocation mechanism. The caller is responsible for re-minting
    /// the fleet's tokens and restarting the server afterwards, in that order.
    /// </remarks>
    Task<LiveKitCredential> RotateSecretAsync(CancellationToken cancellationToken);
}

/// <summary>An API key and the secret that signs tokens for it.</summary>
/// <param name="Key">The API key. An identifier, not a secret — it travels in every token's <c>iss</c>.</param>
/// <param name="Secret">The signing secret. Never leaves this process except into the config file.</param>
/// <param name="IssuedUtc">When this secret was generated, which is what a rotation moves.</param>
public sealed record LiveKitCredential(string Key, string Secret, DateTimeOffset IssuedUtc);
