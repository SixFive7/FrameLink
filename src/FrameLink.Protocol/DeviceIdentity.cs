using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace FrameLink.Protocol;

/// <summary>
/// The device keypair, its fingerprint, and the canonical bytes both sides sign.
/// </summary>
/// <remarks>
/// <para>
/// <b>ECDSA P-256.</b> Ed25519 would be the conventional choice for a device identity, but
/// .NET 10's BCL does not implement it — the asymmetric signature families available are
/// ECDSA, RSA, ML-DSA and SLH-DSA. Adding a native dependency (NSec, BouncyCastle) to a
/// program whose entire delivery format is a single self-contained AOT binary is a poor
/// trade for a marginally smaller key, so identity uses the built-in NIST P-256 curve:
/// 91-byte public key, 64-byte signature, present on every platform, AOT-clean.
/// </para>
/// <para>
/// This type is deliberately free of file and OS concerns. Where the private key is stored,
/// and with what permissions, is the agent's business (version2.md §2.9).
/// </para>
/// </remarks>
public static class DeviceIdentity
{
    /// <summary>Bytes of the fingerprint digest that survive into the device id.</summary>
    /// <remarks>
    /// 10 bytes is 80 bits of digest truncated to 50 bits of rendered identifier — far beyond
    /// collision range for any real fleet, while staying short enough that an operator can
    /// read it off a frame's screen and match it to a row (§3.3).
    /// </remarks>
    private const int FingerprintBytes = 10;

    /// <summary>
    /// Crockford Base32: no <c>I</c>, <c>L</c>, <c>O</c> or <c>U</c>, so no character pair can
    /// be misread when the fingerprint is transcribed from a screen.
    /// </summary>
    private const string CrockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Creates a fresh device keypair.</summary>
    public static ECDsa CreateKeyPair() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>
    /// Derives the stable device id from a public key, as
    /// <c>XXXX-XXXX-XXXX-XXXX</c>.
    /// </summary>
    /// <param name="subjectPublicKeyInfo">DER-encoded SubjectPublicKeyInfo.</param>
    /// <remarks>
    /// Derived from the key rather than assigned by the server, so identity survives a
    /// rebuilt Fleet Manager: every configured agent reappears under the same id and
    /// recovery is simply re-adoption (§3.3).
    /// </remarks>
    public static string FingerprintOf(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(subjectPublicKeyInfo, digest);
        return FormatCrockford(digest[..FingerprintBytes]);
    }

    /// <summary>
    /// Builds the exact bytes covered by a <see cref="HandshakeProof"/> signature.
    /// </summary>
    /// <remarks>
    /// Length-prefixing every component makes the encoding unambiguous: without it, a
    /// splitting attack could shift bytes between the nonces and produce a different tuple
    /// with identical signed bytes. The context string keeps a device key usable only for
    /// this protocol.
    /// </remarks>
    public static byte[] ChallengeBytes(string clientNonce, string serverNonce, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(clientNonce);
        ArgumentNullException.ThrowIfNull(serverNonce);
        ArgumentNullException.ThrowIfNull(deviceId);

        var buffer = new ArrayBufferWriter<byte>();
        AppendPart(buffer, ProtocolConstants.SignatureContext);
        AppendPart(buffer, clientNonce);
        AppendPart(buffer, serverNonce);
        AppendPart(buffer, deviceId);
        return buffer.WrittenSpan.ToArray();

        static void AppendPart(ArrayBufferWriter<byte> writer, string value)
        {
            var byteCount = Encoding.UTF8.GetByteCount(value);
            var span = writer.GetSpan(sizeof(int) + byteCount);
            BitConverter.TryWriteBytes(span, byteCount);
            Encoding.UTF8.GetBytes(value, span[sizeof(int)..]);
            writer.Advance(sizeof(int) + byteCount);
        }
    }

    /// <summary>Generates a fresh 32-byte nonce, base64 encoded.</summary>
    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Verifies a handshake proof against the public key the peer claimed.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> only if the key parses, the device id genuinely derives from
    /// it, and the signature covers the expected challenge bytes.
    /// </returns>
    /// <remarks>
    /// Re-deriving the fingerprint is the step that matters: without it a peer could present
    /// any valid keypair while claiming another device's id, and the signature would verify
    /// perfectly against the key it supplied.
    /// </remarks>
    public static bool VerifyProof(
        string publicKeyBase64,
        string deviceId,
        string clientNonce,
        string serverNonce,
        string signatureBase64)
    {
        byte[] spki;
        byte[] signature;
        try
        {
            spki = Convert.FromBase64String(publicKeyBase64);
            signature = Convert.FromBase64String(signatureBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        if (!string.Equals(FingerprintOf(spki), deviceId, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out _);
            var challenge = ChallengeBytes(clientNonce, serverNonce, deviceId);
            return key.VerifyData(challenge, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static string FormatCrockford(ReadOnlySpan<byte> data)
    {
        // 10 bytes -> 80 bits -> 16 base32 characters, rendered in groups of four.
        var chars = new char[16 + 3];
        var bitBuffer = 0UL;
        var bitCount = 0;
        var position = 0;

        foreach (var b in data)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bitCount += 8;
            while (bitCount >= 5)
            {
                bitCount -= 5;
                if (position is 4 or 9 or 14)
                {
                    chars[position++] = '-';
                }

                chars[position++] = CrockfordAlphabet[(int)((bitBuffer >> bitCount) & 0x1F)];
            }
        }

        return new string(chars);
    }
}
