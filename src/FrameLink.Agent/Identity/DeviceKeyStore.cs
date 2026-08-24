using System.Globalization;
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
    /// The key file exists but cannot be parsed, or the key this call has just written does not
    /// read back as the key it generated.
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
    /// <b>Which is exactly why the first-boot write is atomic and then read back.</b> It goes
    /// through <see cref="IStateStore.WriteSecretAtomic"/> like every other state file — staged,
    /// flushed to the card and renamed into place, so a power cut during it leaves either no key
    /// at all or the whole key and never a truncated one — and what landed is then re-read and
    /// imported before this method returns. This is the one file where failing at write time is
    /// much better than failing later, because the later failure arrives on the next boot wearing
    /// the refusal above and looking exactly like a dead frame. It is a one-time write, which
    /// makes it low probability and not low impact.
    /// </para>
    /// <para>
    /// <b>What the read-back proves, and what it does not.</b> It proves that what the filesystem
    /// holds under this name is the key that was generated, whole and with nothing after it —
    /// which catches a truncated or mangled write, a rename that landed something else, and a
    /// store that accepted the bytes and did not keep them. It cannot prove durability: the bytes
    /// may still be served out of the page cache, and whether the card returns them after a power
    /// cut is a claim only a bench test with the plug in somebody's hand can make. The
    /// <c>fsync</c> inside the atomic write is what addresses that half.
    /// </para>
    /// <para>
    /// A failed read-back leaves the unusable file exactly where it is. Deleting it would let the
    /// next boot mint a fresh identity, and choosing that would be choosing to repair a key
    /// invisibly — the one thing this class refuses to do.
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
            store.WriteSecretAtomic(KeyFileName, pkcs8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        var identity = DeviceKey.From(created);
        try
        {
            VerifyStored(store, identity);
        }
        catch
        {
            // The keypair has never left this process and nothing has been told about it, so
            // dropping it here orphans nothing. What stays behind is the file, for a person.
            identity.Dispose();
            throw;
        }

        log.Info($"Device identity generated on first boot: {identity.DeviceId}");
        return identity;
    }

    /// <summary>
    /// Reads <see cref="KeyFileName"/> back and throws unless it is exactly the key
    /// <paramref name="expected"/> was built from.
    /// </summary>
    /// <remarks>
    /// Three ways a write can be wrong and each is its own refusal: the file is not there, the
    /// bytes are not a key, or the bytes are a key other than this one. The comparison is made on
    /// the public half because that is what the device id is derived from, so two keys that agree
    /// on it agree on the identity; the private bytes are zeroed the moment the import has
    /// consumed them, exactly as the load path zeroes them.
    /// </remarks>
    private static void VerifyStored(IStateStore store, DeviceKey expected)
    {
        var path = store.PathOf(KeyFileName);
        var stored = store.ReadBytes(KeyFileName)
            ?? throw new CryptographicException(
                $"The device key just written to {path} is not there when it is read back, so the "
                + "write did not take and this frame has no identity.");

        try
        {
            using var reread = ECDsa.Create();
            int read;

            try
            {
                reread.ImportPkcs8PrivateKey(stored, out read);
            }
            catch (CryptographicException exception)
            {
                throw new CryptographicException(
                    $"The device key just written to {path} does not read back as a key "
                    + $"({exception.Message}), so the write did not survive and this frame has no "
                    + "identity.",
                    exception);
            }

            if (read != stored.Length)
            {
                throw new CryptographicException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The device key just written to {path} reads back with {stored.Length - read} bytes of trailing data, so the file is not the key that was written and this frame has no identity."));
            }

            if (!string.Equals(
                    Convert.ToBase64String(reread.ExportSubjectPublicKeyInfo()),
                    expected.PublicKeyBase64,
                    StringComparison.Ordinal))
            {
                throw new CryptographicException(
                    $"The device key just written to {path} reads back as a different key than the "
                    + "one that was generated, so the write did not take and this frame has no "
                    + "identity.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(stored);
        }
    }
}
