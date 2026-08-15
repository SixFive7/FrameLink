using System.Security.Cryptography;
using FrameLink.Protocol;

namespace FrameLink.Agent.Identity;

/// <summary>
/// The device's permanent identity: a P-256 keypair that never leaves this object.
/// </summary>
/// <remarks>
/// <para>
/// §2.9 and §3.3: the keypair is generated on first boot, its fingerprint <i>is</i> the device
/// id, and it survives restarts and updates. The private half is deliberately unreachable —
/// there is no accessor for it, only <see cref="Sign"/> — so no caller can accidentally put it
/// in a log line, a telemetry frame or a screen.
/// </para>
/// </remarks>
public sealed class DeviceKey : IDisposable
{
    private readonly ECDsa _key;

    private DeviceKey(ECDsa key, string deviceId, string publicKeyBase64)
    {
        _key = key;
        DeviceId = deviceId;
        PublicKeyBase64 = publicKeyBase64;
    }

    /// <summary>The immutable device id, derived from the public key.</summary>
    public string DeviceId { get; }

    /// <summary>Base64 SubjectPublicKeyInfo, as the handshake carries it.</summary>
    public string PublicKeyBase64 { get; }

    /// <summary>The first group of the device id — enough to match a frame to a row on a bench (§3.3).</summary>
    public string ShortFingerprint => DeviceId.Split('-')[0];

    /// <summary>Adopts an existing key.</summary>
    public static DeviceKey From(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var spki = key.ExportSubjectPublicKeyInfo();
        return new DeviceKey(key, DeviceIdentity.FingerprintOf(spki), Convert.ToBase64String(spki));
    }

    /// <summary>Signs the canonical handshake challenge bytes.</summary>
    public string Sign(byte[] challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);

        return Convert.ToBase64String(_key.SignData(challenge, HashAlgorithmName.SHA256));
    }

    /// <inheritdoc/>
    public void Dispose() => _key.Dispose();
}
