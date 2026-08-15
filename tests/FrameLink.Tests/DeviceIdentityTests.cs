using System.Security.Cryptography;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// Device identity is the whole of the agent's authentication story, so these tests assert
/// what an attacker cannot do, not merely that the happy path returns true.
/// </summary>
public sealed class DeviceIdentityTests
{
    [Fact]
    public void Fingerprint_is_derived_from_the_key_and_stable_across_calls()
    {
        using var key = DeviceIdentity.CreateKeyPair();
        var spki = key.ExportSubjectPublicKeyInfo();

        Assert.Equal(DeviceIdentity.FingerprintOf(spki), DeviceIdentity.FingerprintOf(spki));
    }

    [Fact]
    public void Different_keys_produce_different_fingerprints()
    {
        using var first = DeviceIdentity.CreateKeyPair();
        using var second = DeviceIdentity.CreateKeyPair();

        Assert.NotEqual(
            DeviceIdentity.FingerprintOf(first.ExportSubjectPublicKeyInfo()),
            DeviceIdentity.FingerprintOf(second.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void Fingerprint_is_grouped_and_free_of_ambiguous_characters()
    {
        using var key = DeviceIdentity.CreateKeyPair();

        var fingerprint = DeviceIdentity.FingerprintOf(key.ExportSubjectPublicKeyInfo());

        // XXXX-XXXX-XXXX-XXXX, so that it can be read off a frame's screen and matched to a
        // row in the Fleet Manager without transcription errors.
        Assert.Equal(19, fingerprint.Length);
        Assert.Equal(3, fingerprint.Count(c => c == '-'));
        Assert.All(
            fingerprint.Where(c => c != '-'),
            c => Assert.True(
                "0123456789ABCDEFGHJKMNPQRSTVWXYZ".Contains(c, StringComparison.Ordinal),
                $"'{c}' is not in the Crockford alphabet"));
    }

    [Fact]
    public void Challenge_bytes_cannot_be_confused_by_shifting_a_boundary()
    {
        // Without length prefixes both of these would concatenate to the same bytes, letting
        // a signature captured for one nonce pair be replayed against another.
        var shifted = DeviceIdentity.ChallengeBytes("ab", "c", "device");
        var original = DeviceIdentity.ChallengeBytes("a", "bc", "device");

        Assert.NotEqual(shifted, original);
    }

    [Fact]
    public void A_genuine_proof_verifies()
    {
        var (key, deviceId, publicKey) = NewDevice();
        using var _ = key;
        const string ClientNonce = "client-nonce";
        const string ServerNonce = "server-nonce";

        var signature = Sign(key, ClientNonce, ServerNonce, deviceId);

        Assert.True(DeviceIdentity.VerifyProof(publicKey, deviceId, ClientNonce, ServerNonce, signature));
    }

    [Fact]
    public void A_proof_for_one_challenge_does_not_verify_against_another()
    {
        var (key, deviceId, publicKey) = NewDevice();
        using var _ = key;

        var signature = Sign(key, "client-nonce", "server-nonce", deviceId);

        // The server's nonce is fresh per connection, so a captured proof is worthless.
        Assert.False(DeviceIdentity.VerifyProof(publicKey, deviceId, "client-nonce", "a-later-nonce", signature));
    }

    [Fact]
    public void A_valid_key_cannot_claim_another_devices_identity()
    {
        // The attack this blocks: present your own keypair, sign correctly, but claim the
        // device id of a frame that is already adopted. The signature verifies against the
        // key supplied, so only re-deriving the fingerprint catches it.
        var (attackerKey, _, attackerPublicKey) = NewDevice();
        using var _ = attackerKey;
        var (victimKey, victimDeviceId, _) = NewDevice();
        victimKey.Dispose();

        var signature = Sign(attackerKey, "client-nonce", "server-nonce", victimDeviceId);

        Assert.False(DeviceIdentity.VerifyProof(
            attackerPublicKey, victimDeviceId, "client-nonce", "server-nonce", signature));
    }

    [Fact]
    public void A_signature_from_a_different_key_is_rejected()
    {
        var (key, deviceId, publicKey) = NewDevice();
        using var _ = key;
        using var other = DeviceIdentity.CreateKeyPair();

        var signature = Sign(other, "client-nonce", "server-nonce", deviceId);

        Assert.False(DeviceIdentity.VerifyProof(publicKey, deviceId, "client-nonce", "server-nonce", signature));
    }

    [Theory]
    [InlineData("not base64 at all", "AAAA")]
    [InlineData("AAAA", "not base64 at all")]
    [InlineData("", "")]
    public void Malformed_input_is_rejected_rather_than_thrown(string publicKey, string signature)
    {
        // A malformed hello arrives from the open, internet-exposed registration path, so it
        // has to be an answerable rejection rather than an exception in the accept loop.
        Assert.False(DeviceIdentity.VerifyProof(publicKey, "SOME-DEVI-CEID-HERE", "a", "b", signature));
    }

    [Fact]
    public void Nonces_are_unpredictable()
    {
        var nonces = Enumerable.Range(0, 64).Select(_ => DeviceIdentity.NewNonce()).ToList();

        Assert.Equal(nonces.Count, nonces.Distinct(StringComparer.Ordinal).Count());
    }

    private static (ECDsa Key, string DeviceId, string PublicKeyBase64) NewDevice()
    {
        var key = DeviceIdentity.CreateKeyPair();
        var spki = key.ExportSubjectPublicKeyInfo();
        return (key, DeviceIdentity.FingerprintOf(spki), Convert.ToBase64String(spki));
    }

    private static string Sign(ECDsa key, string clientNonce, string serverNonce, string deviceId) =>
        Convert.ToBase64String(key.SignData(
            DeviceIdentity.ChallengeBytes(clientNonce, serverNonce, deviceId),
            HashAlgorithmName.SHA256));
}
