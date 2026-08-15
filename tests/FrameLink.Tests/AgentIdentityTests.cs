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
        temporary.Store.WriteSecret(DeviceKeyStore.KeyFileName, "this is not a PKCS#8 key"u8);
        var before = temporary.Store.ReadBytes(DeviceKeyStore.KeyFileName)!;

        Assert.ThrowsAny<CryptographicException>(() =>
            DeviceKeyStore.LoadOrCreate(temporary.Store, NullLog.Instance));

        Assert.Equal(before, temporary.Store.ReadBytes(DeviceKeyStore.KeyFileName));
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
}
