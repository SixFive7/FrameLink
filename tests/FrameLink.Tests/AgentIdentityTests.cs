using System.Reflection;
using System.Security.Cryptography;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Identity;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// The device keypair: generated on first boot, permanent thereafter, never in a log
/// (version2.md §2.9, §3.3).
/// </summary>
public sealed class AgentIdentityTests
{
    [Fact]
    public void The_keypair_is_generated_on_first_boot()
    {
        using var temporary = new TemporaryStore();
        var log = new RecordingLog();

        using var identity = DeviceKeyStore.LoadOrCreate(temporary.Store, log);

        Assert.True(temporary.Store.Exists(DeviceKeyStore.KeyFileName));
        Assert.Equal(19, identity.DeviceId.Length);
    }

    [Fact]
    public void The_identity_survives_a_restart()
    {
        // §3.3 makes the fingerprint the immutable device id, which is what lets a rebuilt Fleet
        // Manager see every configured agent reappear under the same identity.
        using var temporary = new TemporaryStore();

        using var first = DeviceKeyStore.LoadOrCreate(temporary.Store, NullLog.Instance);
        using var second = DeviceKeyStore.LoadOrCreate(temporary.Store, NullLog.Instance);

        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.Equal(first.PublicKeyBase64, second.PublicKeyBase64);
    }

    [Fact]
    public void The_identity_survives_an_update()
    {
        // §2.1: persisted state is data, not program files, and an update never touches it. The
        // updater only ever renames over the binary, so the assertion is that a store written by
        // one "version" reads identically to the next.
        using var temporary = new TemporaryStore();
        string deviceId;

        using (var before = DeviceKeyStore.LoadOrCreate(temporary.Store, NullLog.Instance))
        {
            deviceId = before.DeviceId;
        }

        var replacement = new FileStateStore(temporary.Root, temporary.Permissions);
        using var after = DeviceKeyStore.LoadOrCreate(replacement, NullLog.Instance);

        Assert.Equal(deviceId, after.DeviceId);
    }

    [Fact]
    public void The_private_key_is_written_owner_only_inside_an_owner_only_directory()
    {
        using var temporary = new TemporaryStore();

        using var identity = DeviceKeyStore.LoadOrCreate(temporary.Store, NullLog.Instance);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            temporary.Permissions.ModeOf(temporary.Store.PathOf(DeviceKeyStore.KeyFileName)));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            temporary.Permissions.ModeOf(temporary.Root));
    }

    [Fact]
    public void Nothing_derived_from_the_private_key_reaches_the_log()
    {
        using var temporary = new TemporaryStore();
        var log = new RecordingLog();

        using var identity = DeviceKeyStore.LoadOrCreate(temporary.Store, log);

        var stored = temporary.Store.ReadBytes(DeviceKeyStore.KeyFileName)!;
        Assert.DoesNotContain(Convert.ToBase64String(stored), log.Transcript, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexStringLower(stored), log.Transcript, StringComparison.OrdinalIgnoreCase);

        // The fingerprint is public by construction and is what the operator matches to a row.
        Assert.Contains(identity.DeviceId, log.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public void A_damaged_key_file_stops_the_agent_instead_of_silently_minting_a_new_identity()
    {
        // The failure this refuses: regenerating would give the frame a new device id, drop it out
        // of its Fleet Manager record and land it back in the adoption queue looking like a
        // different device — with nobody told. §3.3 makes identity permanent, so the honest answer
        // is to stop.
        using var temporary = new TemporaryStore();
        temporary.Store.WriteSecretAtomic(DeviceKeyStore.KeyFileName, "this is not a PKCS#8 key"u8);
        var before = temporary.Store.ReadBytes(DeviceKeyStore.KeyFileName)!;

        Assert.ThrowsAny<CryptographicException>(() =>
            DeviceKeyStore.LoadOrCreate(temporary.Store, NullLog.Instance));

        Assert.Equal(before, temporary.Store.ReadBytes(DeviceKeyStore.KeyFileName));

        // And nothing was set aside. The first-boot recovery moves a key it has just
        // written; a key that was already on the card when the call began is out of its
        // reach, and a rejected file appearing here would be the proof it had stopped
        // being.
        Assert.False(temporary.Store.Exists(DeviceKeyStore.RejectedKeyFileName));
    }

    [Fact]
    public void The_device_id_really_is_the_fingerprint_of_the_public_key_it_publishes()
    {
        using var temporary = new TemporaryStore();

        using var identity = DeviceKeyStore.LoadOrCreate(temporary.Store, NullLog.Instance);

        Assert.Equal(
            DeviceIdentity.FingerprintOf(Convert.FromBase64String(identity.PublicKeyBase64)),
            identity.DeviceId);
    }

    [Fact]
    public void A_signature_from_the_stored_key_verifies_as_that_device()
    {
        using var temporary = new TemporaryStore();
        using var identity = DeviceKeyStore.LoadOrCreate(temporary.Store, NullLog.Instance);
        const string ClientNonce = "client";
        const string ServerNonce = "server";

        var signature = identity.Sign(
            DeviceIdentity.ChallengeBytes(ClientNonce, ServerNonce, identity.DeviceId));

        Assert.True(DeviceIdentity.VerifyProof(
            identity.PublicKeyBase64, identity.DeviceId, ClientNonce, ServerNonce, signature));
    }

    [Fact]
    public void The_short_fingerprint_is_what_an_operator_reads_off_the_screen()
    {
        using var temporary = new TemporaryStore();

        using var identity = DeviceKeyStore.LoadOrCreate(temporary.Store, NullLog.Instance);

        Assert.Equal(4, identity.ShortFingerprint.Length);
        Assert.StartsWith(identity.ShortFingerprint, identity.DeviceId, StringComparison.Ordinal);
    }

    [Fact]
    public void The_board_serial_is_read_out_of_cpuinfo()
    {
        var files = new MemoryTextFiles();
        files.Files["/proc/cpuinfo"] =
            "processor\t: 0\nmodel name\t: Cortex-A76\nHardware\t: BCM2835\nSerial\t\t: 10000000abcd1234\nModel\t\t: Raspberry Pi 5 Model B Rev 1.0\n";

        Assert.Equal("10000000abcd1234", HardwareFacts.ReadSerial(files));
    }

    [Fact]
    public void A_board_without_a_serial_reports_nothing_rather_than_guessing()
    {
        var files = new MemoryTextFiles();
        files.Files["/proc/cpuinfo"] = "processor\t: 0\nmodel name\t: Cortex-A76\n";

        Assert.Null(HardwareFacts.ReadSerial(files));
        Assert.Null(HardwareFacts.ReadSerial(new MemoryTextFiles()));
    }

    [Fact]
    public void A_state_file_name_cannot_escape_the_state_directory()
    {
        using var temporary = new TemporaryStore();

        Assert.Throws<ArgumentException>(() => temporary.Store.PathOf("../../etc/shadow"));
        Assert.Throws<ArgumentException>(() => temporary.Store.PathOf("nested/key"));
    }

    // -----------------------------------------------------------------------------------------
    // The one-time first-boot write: atomic, and verified before it is believed
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void The_key_is_written_through_the_same_atomic_path_as_every_other_state_file()
    {
        // device.key was the last file still going through the plain overwrite after every other
        // state file moved onto stage-sync-rename. What proves it moved is that the staging path
        // is the one the mode was applied to — nothing else applies a mode to that path — and
        // that it is gone afterwards because it was renamed rather than deleted.
        using var temporary = new TemporaryStore();

        using var identity = DeviceKeyStore.LoadOrCreate(temporary.Store, NullLog.Instance);

        var staging = temporary.Store.PathOf(DeviceKeyStore.KeyFileName + FileStateStore.StagingSuffix);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            temporary.Permissions.ModeOf(staging));
        Assert.False(File.Exists(staging));
        Assert.Equal(
            Path.GetDirectoryName(temporary.Store.PathOf(DeviceKeyStore.KeyFileName)),
            Path.GetDirectoryName(staging));
    }

    [Fact]
    public void A_key_that_does_not_read_back_fails_at_write_time_rather_than_on_the_next_boot()
    {
        // The card that kept half of what it was handed. Before the read-back this returned a
        // working identity, the frame ran the whole session on a key that was already unusable,
        // and the failure arrived on the next boot as the "damaged key" refusal — which is
        // indistinguishable, from the outside, from a frame that has died.
        using var temporary = new TemporaryStore();
        var truncating = new DamagingStore(temporary.Store, bytes => bytes[..(bytes.Length / 2)]);

        var thrown = Assert.ThrowsAny<CryptographicException>(() =>
            DeviceKeyStore.LoadOrCreate(truncating, NullLog.Instance));

        Assert.Contains(DeviceKeyStore.KeyFileName, thrown.Message, StringComparison.Ordinal);

        // The same damage, on the boot after. Asserting both is what makes "rather than on the
        // next boot" a statement about *when* it is caught rather than about whether it is.
        Assert.True(temporary.Store.Exists(DeviceKeyStore.KeyFileName));
        Assert.ThrowsAny<CryptographicException>(() =>
            DeviceKeyStore.LoadOrCreate(temporary.Store, NullLog.Instance));
    }

    [Fact]
    public void A_key_that_reads_back_as_nothing_at_all_fails_the_write()
    {
        // A store that accepted the bytes and kept none of them — a full card, or a write that
        // reported success and went nowhere. There is no file to parse, so this is the branch a
        // parse alone would miss.
        using var temporary = new TemporaryStore();
        var swallowing = new DamagingStore(temporary.Store, _ => null);

        var thrown = Assert.ThrowsAny<CryptographicException>(() =>
            DeviceKeyStore.LoadOrCreate(swallowing, NullLog.Instance));

        Assert.Contains("is not there", thrown.Message, StringComparison.Ordinal);
        Assert.False(temporary.Store.Exists(DeviceKeyStore.KeyFileName));
    }

    [Fact]
    public void A_key_with_bytes_after_it_fails_the_write_even_though_it_parses()
    {
        // A shorter key written over a longer one leaves the tail of the old file behind. The
        // structure at the front still imports, so parsing alone says yes; what says no is that
        // the import did not consume the whole file.
        using var temporary = new TemporaryStore();
        var trailing = new DamagingStore(temporary.Store, bytes => [.. bytes, .. "not part of the key"u8]);

        var thrown = Assert.ThrowsAny<CryptographicException>(() =>
            DeviceKeyStore.LoadOrCreate(trailing, NullLog.Instance));

        Assert.Contains("trailing data", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_that_reads_back_as_a_different_key_fails_the_write()
    {
        // The write that landed somebody else's bytes: the file parses perfectly and the frame
        // would run under an identity nothing in this process ever generated. Comparing the
        // public half is what catches it, and the public half is what the device id is derived
        // from, so agreeing on it is agreeing on the identity.
        using var temporary = new TemporaryStore();
        using var other = DeviceIdentity.CreateKeyPair();
        var substitute = other.ExportPkcs8PrivateKey();
        var swapping = new DamagingStore(temporary.Store, _ => substitute);

        var thrown = Assert.ThrowsAny<CryptographicException>(() =>
            DeviceKeyStore.LoadOrCreate(swapping, NullLog.Instance));

        Assert.Contains("different key", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_write_says_so_without_putting_any_of_the_key_in_the_message()
    {
        // The refusal is read by a person and lands in the journal, so it carries the path and
        // nothing else. §2.9's "the private half never leaves the object" has to hold on the
        // failure path too, and a failure path is where a stray diagnostic would be added.
        using var temporary = new TemporaryStore();
        var log = new RecordingLog();
        var truncating = new DamagingStore(temporary.Store, bytes => bytes[..8]);

        var thrown = Assert.ThrowsAny<CryptographicException>(() =>
            DeviceKeyStore.LoadOrCreate(truncating, log));

        var stored = temporary.Store.ReadBytes(DeviceKeyStore.KeyFileName)!;
        Assert.DoesNotContain(Convert.ToBase64String(stored), thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexStringLower(stored), thrown.Message, StringComparison.OrdinalIgnoreCase);

        // Nothing announced an identity either: the log line that names a device id is the one
        // that says the frame has one, and it must not be written for a key that was not kept.
        Assert.DoesNotContain("generated on first boot", log.Transcript, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // A first-boot key that fails its own read-back is set aside, and only that key ever is
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void A_key_that_fails_its_own_read_back_is_set_aside_and_a_fresh_identity_is_minted()
    {
        // The frame this is for: a card that took the first write badly on the very first boot.
        // Before, that frame threw on every boot forever and needed somebody to drive to it and
        // delete a file. The key it refuses to keep was generated seconds earlier, has never been
        // sent to a Fleet Manager and appears in no record anywhere, so replacing it orphans
        // nothing — which is the entire reason this one case may be repaired and a load failure
        // may not.
        using var temporary = new TemporaryStore();
        var log = new RecordingLog();
        var attempts = 0;
        var onlyTheFirstWrite = new DamagingStore(
            temporary.Store,
            bytes => attempts++ == 0 ? bytes[..(bytes.Length / 2)] : bytes);

        using var identity = DeviceKeyStore.LoadOrCreate(onlyTheFirstWrite, log);

        // A usable identity, and the file holds exactly it.
        Assert.Equal(19, identity.DeviceId.Length);
        using var reread = ECDsa.Create();
        reread.ImportPkcs8PrivateKey(temporary.Store.ReadBytes(DeviceKeyStore.KeyFileName), out _);
        Assert.Equal(identity.PublicKeyBase64, Convert.ToBase64String(reread.ExportSubjectPublicKeyInfo()));

        // Loudly. A frame that quietly changed identity is the failure §3.3 is about, so the
        // journal carries both the refusal and the fact that a second key was minted.
        Assert.Contains("Fail:", log.Transcript, StringComparison.Ordinal);
        Assert.Contains("Warn:", log.Transcript, StringComparison.Ordinal);
        Assert.Contains(
            temporary.Store.PathOf(DeviceKeyStore.RejectedKeyFileName),
            log.Transcript,
            StringComparison.Ordinal);

        // And still nothing derived from a private key anywhere in it.
        var stored = temporary.Store.ReadBytes(DeviceKeyStore.KeyFileName)!;
        Assert.DoesNotContain(Convert.ToBase64String(stored), log.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public void The_key_that_was_set_aside_is_kept_byte_for_byte_under_a_name_nothing_reads()
    {
        // Evidence, not cleanup. Deleting the rejected key would let the frame come up and would
        // destroy the only record of what the card did with a write it said it had accepted; a
        // copy would put a second private key on the volume. A rename preserves the bytes without
        // duplicating them, which is why it is a rename.
        using var temporary = new TemporaryStore();
        var attempts = 0;
        var damaged = "not the key that was generated"u8.ToArray();
        var onlyTheFirstWrite = new DamagingStore(
            temporary.Store,
            bytes => attempts++ == 0 ? damaged : bytes);

        using var identity = DeviceKeyStore.LoadOrCreate(onlyTheFirstWrite, NullLog.Instance);

        Assert.Equal(damaged, temporary.Store.ReadBytes(DeviceKeyStore.RejectedKeyFileName));
        Assert.NotEqual(damaged, temporary.Store.ReadBytes(DeviceKeyStore.KeyFileName));
        Assert.Equal(19, identity.DeviceId.Length);
    }

    [Fact]
    public void The_rejected_key_is_a_sibling_of_the_key_so_setting_it_aside_stays_on_one_filesystem()
    {
        // The same argument the staging write rests on, and it has to hold here too: a rename
        // across a mount point is a copy and a delete, which is neither atomic nor a way to keep
        // the original bytes. PathOf refuses any name that could leave the root, so the two names
        // are always in one directory.
        using var temporary = new TemporaryStore();

        var key = temporary.Store.PathOf(DeviceKeyStore.KeyFileName);
        var rejected = temporary.Store.PathOf(DeviceKeyStore.RejectedKeyFileName);

        Assert.Equal(Path.GetDirectoryName(key), Path.GetDirectoryName(rejected));
        Assert.Equal(temporary.Root, Path.GetDirectoryName(rejected));
        Assert.StartsWith(DeviceKeyStore.KeyFileName, DeviceKeyStore.RejectedKeyFileName, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_key_that_will_not_read_back_is_refused_rather_than_overwriting_the_first()
    {
        // Exactly one recovery, and what bounds it is the shape of the code rather than a
        // counter: the second attempt is made outside the catch, so its own failure has nowhere
        // to be caught and leaves as the plain refusal. A card producing a second unreadable key
        // has a fault a third key will not fix, and the first rejection is the more useful of
        // the two to keep.
        using var temporary = new TemporaryStore();
        var log = new RecordingLog();
        var written = new List<byte[]>();
        var alwaysDamaged = new DamagingStore(
            temporary.Store,
            bytes =>
            {
                var damaged = bytes[..8];
                written.Add(damaged);
                return damaged;
            });

        Assert.ThrowsAny<CryptographicException>(() =>
            DeviceKeyStore.LoadOrCreate(alwaysDamaged, log));

        // Exactly two attempts: the first, and the one recovery it is allowed.
        Assert.Equal(2, written.Count);

        // And what was kept is the first failure, not the last one.
        Assert.Equal(written[0], temporary.Store.ReadBytes(DeviceKeyStore.RejectedKeyFileName));
        Assert.Equal(written[1], temporary.Store.ReadBytes(DeviceKeyStore.KeyFileName));

        // The refusal that leaves is the second failure said plainly, with no suggestion that
        // anything was repaired — a frame that reaches here is stopped, exactly as it was
        // before any of this existed.
        Assert.Contains("does not read back as a key", log.Transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("on the second attempt", log.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public void A_frame_that_already_has_a_rejected_key_is_given_no_second_recovery()
    {
        // How the frame reaches this: it set a key aside once, and later somebody — a person, a
        // repair script — deleted device.key, so the next boot is a first boot again. If that key
        // also fails to read back, the recovery is refused outright: the rejected file already
        // there is the record of the original failure and is worth more than a second copy of the
        // same symptom, and a frame failing this twice needs a person rather than a third key.
        using var temporary = new TemporaryStore();
        var log = new RecordingLog();
        var attempts = 0;
        temporary.Store.WriteSecretAtomic(
            DeviceKeyStore.RejectedKeyFileName, "the key this frame set aside long ago"u8);

        var alwaysDamaged = new DamagingStore(
            temporary.Store,
            bytes =>
            {
                attempts++;
                return bytes[..8];
            });

        Assert.ThrowsAny<CryptographicException>(() =>
            DeviceKeyStore.LoadOrCreate(alwaysDamaged, log));

        // One attempt, no recovery, and the original evidence untouched.
        Assert.Equal(1, attempts);
        Assert.Equal(
            "the key this frame set aside long ago"u8.ToArray(),
            temporary.Store.ReadBytes(DeviceKeyStore.RejectedKeyFileName));
        Assert.Contains(
            "already holds a key this frame set aside once before",
            log.Transcript,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_write_the_store_kept_nothing_of_sets_nothing_aside_and_mints_nothing_new()
    {
        // A store that accepted the bytes and kept none of them is the card failing rather than
        // the key, and a second write onto it would only be a second key nobody has. There is
        // also nothing left to preserve, so there is nothing here for this recovery to do.
        using var temporary = new TemporaryStore();
        var log = new RecordingLog();
        var attempts = 0;
        var swallowing = new DamagingStore(
            temporary.Store,
            _ =>
            {
                attempts++;
                return null;
            });

        Assert.ThrowsAny<CryptographicException>(() =>
            DeviceKeyStore.LoadOrCreate(swallowing, log));

        Assert.Equal(1, attempts);
        Assert.False(temporary.Store.Exists(DeviceKeyStore.RejectedKeyFileName));
        Assert.False(temporary.Store.Exists(DeviceKeyStore.KeyFileName));
        Assert.Contains("nothing to set aside", log.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_that_fails_to_load_is_refused_even_on_a_frame_that_once_set_one_aside()
    {
        // The distinction, asserted where it would actually be lost: on a frame that has already
        // been through the recovery once. A later boot finds a key it cannot read — the card
        // rotting under an identity the Fleet Manager knows, a fingerprint somebody wrote on a
        // label — and the answer is still to stop. Nothing is renamed, nothing is minted, and the
        // rejected file from the first boot is not disturbed either.
        using var temporary = new TemporaryStore();
        var attempts = 0;
        var onlyTheFirstWrite = new DamagingStore(
            temporary.Store,
            bytes => attempts++ == 0 ? bytes[..16] : bytes);

        using (var first = DeviceKeyStore.LoadOrCreate(onlyTheFirstWrite, NullLog.Instance))
        {
            Assert.Equal(19, first.DeviceId.Length);
        }

        var rejectedBefore = temporary.Store.ReadBytes(DeviceKeyStore.RejectedKeyFileName)!;

        // Now the adopted key rots on the card, exactly as a damaged key would.
        temporary.Store.WriteSecretAtomic(DeviceKeyStore.KeyFileName, "this used to be a key"u8);
        var keyBefore = temporary.Store.ReadBytes(DeviceKeyStore.KeyFileName)!;

        Assert.ThrowsAny<CryptographicException>(() =>
            DeviceKeyStore.LoadOrCreate(temporary.Store, NullLog.Instance));

        Assert.Equal(keyBefore, temporary.Store.ReadBytes(DeviceKeyStore.KeyFileName));
        Assert.Equal(rejectedBefore, temporary.Store.ReadBytes(DeviceKeyStore.RejectedKeyFileName));
    }

    [Fact]
    public void The_load_path_is_handed_the_bytes_and_not_the_store()
    {
        // The enforcement itself, asserted rather than described. What keeps the first-boot
        // recovery away from a key that merely fails to load is not a comment and not a flag: the
        // load path is its own method and is never given an IStateStore, so no edit inside it can
        // rename, delete or rewrite anything without first widening this signature — which is a
        // change a reader can see, and one this test refuses.
        var load = typeof(DeviceKeyStore).GetMethod(
            "Load",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.True(
            load is not null,
            "DeviceKeyStore.Load is gone or was renamed. It is the load path, and the reason it is "
            + "a method of its own is that it must not be able to reach the store. Find whatever "
            + "replaced it and assert the same thing about that.");

        Assert.DoesNotContain(
            typeof(IStateStore),
            load!.GetParameters().Select(parameter => parameter.ParameterType));
    }

    /// <summary>
    /// A store that keeps something other than the bytes it was handed — the card half of a bad
    /// write, which is the only half a process cannot produce by crashing itself.
    /// </summary>
    /// <remarks>
    /// The damage is applied to the write rather than to the read on purpose: what is under test
    /// is that the file <i>on disk</i> is checked, so the bytes have to genuinely reach it and
    /// genuinely come back through <see cref="FileStateStore"/>. A null from the delegate is the
    /// write that reported success and kept nothing.
    /// </remarks>
    private sealed class DamagingStore : IStateStore
    {
        private readonly IStateStore _inner;
        private readonly Func<byte[], byte[]?> _damage;

        public DamagingStore(IStateStore inner, Func<byte[], byte[]?> damage)
        {
            _inner = inner;
            _damage = damage;
        }

        public string Root => _inner.Root;

        public void EnsureReady() => _inner.EnsureReady();

        public bool Exists(string name) => _inner.Exists(name);

        public byte[]? ReadBytes(string name) => _inner.ReadBytes(name);

        public string? ReadText(string name) => _inner.ReadText(name);

        public void WriteSecretAtomic(string name, ReadOnlySpan<byte> content) =>
            Damage(name, content, _inner.WriteSecretAtomic);

        public void WriteText(string name, string content) => _inner.WriteText(name, content);

        public void Delete(string name) => _inner.Delete(name);

        public bool TryRename(string name, string newName) => _inner.TryRename(name, newName);

        public string PathOf(string name) => _inner.PathOf(name);

        private void Damage(string name, ReadOnlySpan<byte> content, WriteBytes write)
        {
            var damaged = _damage(content.ToArray());
            if (damaged is null)
            {
                return;
            }

            write(name, damaged);
        }

        private delegate void WriteBytes(string name, ReadOnlySpan<byte> content);
    }
}
