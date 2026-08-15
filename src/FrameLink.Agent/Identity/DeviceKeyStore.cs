using System.Security.Cryptography;
using FrameLink.Agent.Hosting;
using FrameLink.Protocol;

namespace FrameLink.Agent.Identity;

/// <summary>
/// Generates the device keypair on first boot and loads it on every boot after (§2.9).
/// </summary>
public static class DeviceKeyStore
{
    /// <summary>File name of the PKCS#8 private key inside the state store.</summary>
    public const string KeyFileName = "device.key";

    /// <summary>
    /// Returns this device's identity, creating it if the frame has never booted before.
    /// </summary>
    /// <exception cref="CryptographicException">
    /// The key file exists but cannot be parsed.
    /// </exception>
    /// <remarks>
    /// <para>
    /// A damaged key file <b>throws rather than regenerating</b>, and that is the whole point
    /// of this method. Regenerating would hand the frame a new identity, drop it out of its
    /// Fleet Manager record and land it back in the adoption queue looking like a different
    /// device — silently. §3.3 makes identity permanent; the honest response to a key that
    /// cannot be read is to stop and say so, which is also §1.2.3 (nothing is repaired
    /// invisibly).
    /// </para>
    /// <para>
    /// Nothing derived from the private key reaches <paramref name="log"/>. The fingerprint
    /// does, because it is public by construction and is what the operator matches against a
    /// row in the Fleet Manager.
    /// </para>
    /// </remarks>
    public static DeviceKey LoadOrCreate(IStateStore store, IAgentLog log)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(log);

        store.EnsureReady();

        var existing = store.ReadBytes(KeyFileName);
        if (existing is not null)
        {
            var key = ECDsa.Create();
            try
            {
                key.ImportPkcs8PrivateKey(existing, out _);
            }
            catch
            {
                key.Dispose();
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(existing);
            }

            var loaded = DeviceKey.From(key);
            log.Info($"Device identity loaded: {loaded.DeviceId}");
            return loaded;
        }

        var created = DeviceIdentity.CreateKeyPair();
        var pkcs8 = created.ExportPkcs8PrivateKey();
        try
        {
            store.WriteSecret(KeyFileName, pkcs8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        var identity = DeviceKey.From(created);
        log.Info($"Device identity generated on first boot: {identity.DeviceId}");
        return identity;
    }
}
