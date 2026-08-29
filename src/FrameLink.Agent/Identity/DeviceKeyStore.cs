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
    /// Where a first-boot key that failed its own read-back is kept, for a person.
    /// </summary>
    /// <remarks>
    /// A sibling of <see cref="KeyFileName"/> inside the same state directory, so setting one
    /// aside is a <c>rename(2)</c> within one filesystem and the bytes that came off the card are
    /// preserved exactly rather than copied. Nothing reads this file. It exists to be looked at.
    /// </remarks>
    public const string RejectedKeyFileName = KeyFileName + ".rejected";

    /// <summary>
    /// Returns this device's identity, creating it if the frame has never booted before.
    /// </summary>
    /// <exception cref="CryptographicException">
    /// The key file exists but cannot be parsed, or the key this call has just written does not
    /// read back as the key it generated and could not be set aside.
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
    /// <b>The two failures this method answers differently, and the line between them.</b> A key
    /// that fails its own read-back <i>seconds after being generated</i> has never been outside
    /// this process: no handshake has carried it, no Fleet Manager has recorded it, no label
    /// bears its fingerprint. Nothing anywhere refers to it, so nothing is orphaned by replacing
    /// it — and refusing to would leave a frame that will not come up until somebody drives to it
    /// and deletes a file by hand. That key is set aside as <see cref="RejectedKeyFileName"/> and
    /// a fresh identity is minted, loudly. A key that fails to <i>load</i> on a later boot is the
    /// opposite case in every respect: it may well be the identity this frame is adopted under,
    /// so it is refused, exactly as before.
    /// </para>
    /// <para>
    /// That line is drawn by the shape of the code rather than by this paragraph. This method
    /// forks once, on whether the file was there when the call began, and the two sides are
    /// different methods taking different arguments: <see cref="Load"/> is handed the bytes and
    /// <b>not the store</b>, so nothing reachable from the load path can rename, delete or
    /// rewrite anything — see its own remarks.
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

        // The one fork, and the only place either path is entered. Reaching CreateOnFirstBoot at
        // all is proof that this call found no key file, so the file its recovery sets aside can
        // only ever be one this same call has just written.
        var existing = store.ReadBytes(KeyFileName);
        return existing is not null
            ? Load(existing, log)
            : CreateOnFirstBoot(store, log);
    }

    /// <summary>
    /// Imports a key file that was already on the card when the call began, or throws.
    /// </summary>
    /// <remarks>
    /// <b>It is handed the bytes and not the store, and that is what enforces the refusal.</b> A
    /// key that fails to import here may be the identity this frame was adopted under — recorded
    /// in a Fleet Manager, printed on a label, sitting in somebody's device list — and §3.3 makes
    /// that permanent. There is no version of "recover" that is right for it, so this method is
    /// given nothing to recover with: no <see cref="IStateStore"/>, therefore no rename, no
    /// delete and no write, whatever a later edit inside it reaches for. Widening this signature
    /// is the change that would undo it, and it is a change a reader can see rather than a
    /// convention a reader has to know.
    /// </remarks>
    private static DeviceKey Load(byte[] existing, IAgentLog log)
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

    /// <summary>
    /// Mints the frame's first identity, with a single attempt at recovery behind it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One recovery per call, and it is the shape of the method that says so.</b> The second
    /// <see cref="MintAndVerify"/> is outside the <c>catch</c>, so its own failure has nowhere
    /// to be caught and leaves through the same refusal a load failure would. There is no loop
    /// here to bound and no counter to get wrong: a card that will not keep a key produces one
    /// rejected file, one further attempt, and then a frame that stops and says so.
    /// </para>
    /// <para>
    /// <b>One recovery per frame, and that is the rejected file.</b> Across boots the bound is
    /// <see cref="RejectedKeyFileName"/> itself: a second key cannot be set aside while the
    /// first one is still there, because <see cref="IStateStore.TryRename"/> will not replace
    /// it. So the evidence of the one time a frame regenerated its identity is permanent, and
    /// a frame that reaches this state twice needs a person rather than a third key.
    /// </para>
    /// </remarks>
    private static DeviceKey CreateOnFirstBoot(IStateStore store, IAgentLog log)
    {
        DeviceKey identity;

        try
        {
            identity = MintAndVerify(store);
        }
        catch (CryptographicException rejected)
        {
            if (!TrySetAsideRejectedKey(store, log, rejected))
            {
                throw;
            }

            var replacement = MintAndVerify(store);
            log.Warn(
                "Device identity generated on first boot, on the second attempt: "
                + replacement.DeviceId);
            return replacement;
        }

        log.Info($"Device identity generated on first boot: {identity.DeviceId}");
        return identity;
    }

    /// <summary>Generates a keypair, writes it atomically and proves the file holds it.</summary>
    private static DeviceKey MintAndVerify(IStateStore store)
    {
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

        return identity;
    }

    /// <summary>
    /// Moves a just-written key that failed its read-back to <see cref="RejectedKeyFileName"/>,
    /// and says whether a fresh identity may now be minted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called from exactly one place — the write-and-verify failure inside
    /// <see cref="CreateOnFirstBoot"/>, which is itself only reachable when
    /// <see cref="LoadOrCreate"/> found no key file at all. The file this moves is therefore
    /// always one the same call wrote moments earlier.
    /// </para>
    /// <para>
    /// Every answer of <see langword="false"/> leaves the frame exactly as the old behaviour left
    /// it: the caller rethrows the original refusal, the file stays where it is, and no second
    /// identity exists. The three of them are the three ways this recovery is not the right
    /// answer — there is nothing to set aside, an earlier rejection is already recorded, or the
    /// store would not move the file.
    /// </para>
    /// <para>
    /// The path is what reaches the journal and never the content, because the file being set
    /// aside is a private key whether or not it is a damaged one.
    /// </para>
    /// </remarks>
    private static bool TrySetAsideRejectedKey(
        IStateStore store,
        IAgentLog log,
        CryptographicException rejected)
    {
        var path = store.PathOf(KeyFileName);
        var rejectedPath = store.PathOf(RejectedKeyFileName);

        if (!store.Exists(KeyFileName))
        {
            // The store took the bytes and kept none of them, so there is nothing to preserve and
            // a second attempt would be a second write onto whatever swallowed the first. That is
            // the card failing rather than the key, and a new identity is not an answer to it.
            log.Fail(
                $"The device key written to {path} is not there when it is read back, so there is "
                + "nothing to set aside and no second identity has been generated.");
            return false;
        }

        if (store.Exists(RejectedKeyFileName))
        {
            // §3.3 permanence at the outer edge: one automatic regeneration in a frame's life. A
            // frame producing a second unreadable key on a first boot has a fault a third key
            // will not fix, and the first rejection is the more useful of the two to keep.
            log.Fail(
                $"{rejectedPath} already holds a key this frame set aside once before. Refusing to "
                + "overwrite it or to generate a third identity — this frame needs a person.");
            return false;
        }

        if (!store.TryRename(KeyFileName, RejectedKeyFileName))
        {
            log.Fail(
                $"{path} could not be moved to {rejectedPath}, so it has been left where it is and "
                + "no second identity has been generated.");
            return false;
        }

        log.Fail($"The device key this frame just generated did not read back: {rejected.Message}");
        log.Warn(
            "That key was never sent to a Fleet Manager and was never reported anywhere, so it has "
            + $"been moved to {rejectedPath} and a new identity is being generated in its place. "
            + "Keep that file: it is the evidence of what this card did with a write it accepted.");
        return true;
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
