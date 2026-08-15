using System.Text;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Identity;
using FrameLink.Agent.Link;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;
using FrameLink.Agent.State;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// <b>The agent remembers what it was last told</b> — §2.1's "last-known desired values", and the
/// two failures that came of not having them.
/// </summary>
/// <remarks>
/// <para>
/// Both were found on the mule. A power cut during a server outage put a repair screen on a frame
/// that had been green, because the last authoritative answer lived in process memory; and every
/// fleet-valued resource reverted to its catalog default across the same reboot, because
/// <see cref="FleetValues"/> could not tell "never set" from "not told yet". §2.6 forbids the
/// first in as many words — "an outage in the operator's house must never blank a frame in someone
/// else's" — and the second costs a real apply-and-reboot each way.
/// </para>
/// <para>
/// Every test here asserts something a person in the room would see: whether photos are on the
/// screen, whether the frame rebooted, what is in the file afterwards. None of them asserts how
/// the memory is wired.
/// </para>
/// </remarks>
public sealed class AgentMemoryTests
{
    private static ReconcileOptions Fast => new()
    {
        Countdown = TimeSpan.Zero,
        AttemptBudget = 3,
        EscalationLimit = 2,
    };

    [Fact]
    public async Task A_power_cut_during_an_outage_leaves_a_green_frame_showing_photos()
    {
        // The headline case, verbatim from the operator: if the state was green and there is no
        // connection to the Fleet Manager, a power failure in the home must not result in an
        // offline product.
        using var files = new TemporaryStore();

        using (var before = Process(files))
        {
            await AnswerAsync(before, AgentServerScript.Ok());
            MarkGreen(before);
            Assert.True(before.Hub.Current.ProductRuns);
        }

        // The power cut, and a Fleet Manager that is still down when the frame comes back.
        using var after = Process(files);
        await OutageAsync(after);

        Assert.True(after.Hub.Current.ProductRuns);
        Assert.Equal(DeviceState.NoContact, after.Hub.Current.Condition.State);
        Assert.Contains("carries on", after.Hub.Current.Condition.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_frame_that_was_never_adopted_comes_back_on_its_repair_screen()
    {
        // The other half of the rule, and the one that must not be softened. Silence never
        // promotes a frame; it only ever lets one carry on.
        using var files = new TemporaryStore();

        using (var before = Process(files))
        {
            await AnswerAsync(before, AgentServerScript.Pending());
            Assert.False(before.Hub.Current.ProductRuns);
        }

        using var after = Process(files);
        await OutageAsync(after);

        Assert.False(after.Hub.Current.ProductRuns);
        Assert.Equal(DeviceState.NoContact, after.Hub.Current.Condition.State);
    }

    [Fact]
    public async Task A_frame_that_has_never_spoken_to_anything_comes_back_on_its_repair_screen()
    {
        // No memory file at all: a frame straight off the card, or one whose memory was lost.
        using var files = new TemporaryStore();

        using var frame = Process(files);
        await OutageAsync(frame);

        Assert.False(frame.Hub.Current.ProductRuns);
        Assert.Null(frame.Memory.Read().Verdict);
    }

    [Fact]
    public async Task A_frame_adopted_mid_provision_comes_back_on_its_repair_screen()
    {
        // Adopted is the server's half of "fully green"; every resource verified is the frame's.
        // A frame being set up in the operator's own house has the first and not the second, and
        // §2.6 gives it a repair screen — the memory must not promote it to showing photos.
        using var files = new TemporaryStore();

        using (var before = Process(files))
        {
            await AnswerAsync(before, AgentServerScript.Ok());
        }

        using var after = Process(files);
        await OutageAsync(after);

        Assert.False(after.Hub.Current.ProductRuns);
        Assert.Equal(HandshakeStatus.Ok, after.Memory.Read().Verdict);
    }

    [Fact]
    public async Task A_frame_the_operator_blocked_stays_blocked_across_a_power_cut()
    {
        // §2.6: an authoritative answer wins, in both directions. A frame that was green and was
        // then blocked must not resurrect itself by rebooting into an outage.
        using var files = new TemporaryStore();

        using (var before = Process(files))
        {
            await AnswerAsync(before, AgentServerScript.Ok());
            MarkGreen(before);
            await AnswerAsync(before, Verdict(HandshakeStatus.Blocked));
            Assert.False(before.Hub.Current.ProductRuns);
        }

        using var after = Process(files);
        await OutageAsync(after);

        Assert.False(after.Hub.Current.ProductRuns);
        Assert.Equal(HandshakeStatus.Blocked, after.Memory.Read().Verdict);
    }

    [Fact]
    public async Task An_answer_that_arrives_after_a_resumed_boot_replaces_what_was_remembered()
    {
        // The same rule in the other direction: a frame that came back green on a memory is told
        // it is pending, and stops the product on the spot.
        using var files = new TemporaryStore();

        using (var before = Process(files))
        {
            await AnswerAsync(before, AgentServerScript.Ok());
            MarkGreen(before);
        }

        using var after = Process(files);
        Assert.True(after.Hub.Current.ProductRuns);

        await AnswerAsync(after, AgentServerScript.Pending());

        Assert.False(after.Hub.Current.ProductRuns);
        Assert.Equal(HandshakeStatus.Pending, after.Memory.Read().Verdict);
    }

    [Fact]
    public async Task A_remembered_fleet_setting_costs_no_apply_and_reboot_after_a_power_cut()
    {
        // logging.journalMaxUse has no dependsOn, so it runs during any outage. Forgetting the
        // operator's value means writing the catalog default over it and rebooting to prove that,
        // then doing the whole thing again when the server comes back.
        using var system = new TemporaryFiles();
        using var files = new TemporaryStore();
        var clock = new ManualClock();

        var first = new AgentMemory(files.Store, new RecordingLog(), clock);
        first.RememberSettings(Push(("logging.journalMaxUse", "128M")));

        using (var provisioning = new ReconcileHarness(
            Fast,
            new JournalStorageResource(system.Files, FleetValues.From(first.Settings))))
        {
            await provisioning.ConvergeAsync();
        }

        Assert.Contains("SystemMaxUse=128M", system.Read(JournalStorageResource.DropInPath)!, StringComparison.Ordinal);

        // The power cut. A new process reads the same directory, and nothing answers it.
        var second = new AgentMemory(files.Store, new RecordingLog(), clock);
        using var afterReboot = new ReconcileHarness(
            Fast,
            new JournalStorageResource(system.Files, FleetValues.From(second.Settings)));

        var outcome = await afterReboot.ConvergeAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.Empty(afterReboot.Boundary.Crossings);
        Assert.Contains("SystemMaxUse=128M", system.Read(JournalStorageResource.DropInPath)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_memory_the_same_power_cut_reverts_the_setting_and_reboots_for_it()
    {
        // The counterfactual, so the test above cannot pass for the wrong reason. This is the
        // measured behaviour before the memory existed.
        using var system = new TemporaryFiles();

        using (var provisioning = new ReconcileHarness(
            Fast,
            new JournalStorageResource(
                system.Files,
                FleetValues.From(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [JournalStorageResource.SettingKey] = "128M",
                }))))
        {
            await provisioning.ConvergeAsync();
        }

        using var afterReboot = new ReconcileHarness(Fast, new JournalStorageResource(system.Files, FleetValues.None));
        await afterReboot.ConvergeAsync();

        Assert.Single(afterReboot.Boundary.Crossings);
        Assert.Contains("SystemMaxUse=64M", system.Read(JournalStorageResource.DropInPath)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_the_Fleet_Manager_issued_is_still_known_after_a_power_cut()
    {
        // DeviceNameResource treats "not told" as unevaluable and blocks, so an agent that forgot
        // the name spent every outage unable to say whether the name on disk was right.
        using var files = new TemporaryStore();

        using (var before = Process(files))
        {
            await AnswerAsync(before, AgentServerScript.Ok(deviceName: "Hallway"));
            before.Memory.RememberDeviceName("Hallway");
        }

        using var after = Process(files);

        using var harness = new ReconcileHarness(
            Fast,
            new AdoptionResource(after.Store, () => ServerAnswer.Silence),
            new DeviceNameResource(after.Store, () => after.DeviceName));

        after.Store.WriteText(AdoptionResource.FileName, AdoptionResource.AdoptedMarker);
        var outcome = await harness.ConvergeAsync();

        Assert.Equal("Hallway", after.DeviceName);
        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.Equal("Hallway", after.Store.ReadText(DeviceNameResource.FileName));
    }

    [Fact]
    public void A_setting_the_operator_deleted_does_not_outlive_the_push_that_dropped_it()
    {
        // A push is the complete effective set, so it replaces rather than merges. Merging would
        // make a deleted setting immortal on the frame.
        using var files = new TemporaryStore();
        var memory = new AgentMemory(files.Store, new RecordingLog(), new ManualClock());

        memory.RememberSettings(Push(("power.cpuGovernor", "performance"), ("logging.journalMaxUse", "128M")));
        memory.RememberSettings(Push(("logging.journalMaxUse", "128M")));

        var reloaded = new AgentMemory(files.Store, new RecordingLog(), new ManualClock());

        Assert.Null(FleetValues.From(reloaded.Settings).Find("power.cpuGovernor"));
        Assert.Equal("128M", FleetValues.From(reloaded.Settings).Get("logging.journalMaxUse", "64M"));
    }

    [Fact]
    public void A_key_that_was_never_pushed_still_falls_back_to_the_catalog_default()
    {
        // §1.2.2 stays: a never-configured fleet still produces a fully specified frame. Memory
        // remembers what was said, it does not invent an answer for what was not.
        using var files = new TemporaryStore();
        var memory = new AgentMemory(files.Store, new RecordingLog(), new ManualClock());

        memory.RememberSettings(Push(("logging.journalMaxUse", "128M")));

        Assert.Equal(
            CpuGovernorResource.DefaultGovernor,
            FleetValues.From(memory.Settings).Get(CpuGovernorResource.SettingKey, CpuGovernorResource.DefaultGovernor));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"verdict\":\"ok\",\"settin")]
    [InlineData("not json at all")]
    [InlineData("{\"verdict\":42}")]
    [InlineData("\0\0\0\0\0\0")]
    public async Task A_corrupt_memory_starts_cleanly_at_catalog_defaults_and_not_green(string content)
    {
        // A power cut mid-write is the scenario this feature exists for, so the one thing a
        // damaged file must never do is stop the frame starting.
        using var files = new TemporaryStore();
        files.Store.WriteText(AgentMemory.FileName, content);

        using var frame = Process(files);
        await OutageAsync(frame);

        Assert.False(frame.Hub.Current.ProductRuns);
        Assert.Empty(frame.Memory.Settings);
        Assert.Null(frame.Memory.Read().Verdict);
        Assert.Equal("64M", FleetValues.From(frame.Memory.Settings).Get(JournalStorageResource.SettingKey, "64M"));
    }

    [Fact]
    public async Task A_corrupt_memory_does_not_stop_the_frame_remembering_the_next_answer()
    {
        // Degrading to ignorance has to be recoverable, or one bad write would cost every
        // subsequent power cut too.
        using var files = new TemporaryStore();
        files.Store.WriteText(AgentMemory.FileName, "{\"verdict\":\"ok\",\"settin");

        using (var damaged = Process(files))
        {
            await AnswerAsync(damaged, AgentServerScript.Ok());
            MarkGreen(damaged);
        }

        using var after = Process(files);
        await OutageAsync(after);

        Assert.True(after.Hub.Current.ProductRuns);
    }

    [Fact]
    public void The_memory_is_root_only_because_a_settings_map_may_carry_a_credential()
    {
        // §2.9. The values are dull today — an album id, a journal cap — and the file is the one
        // place an Immich API key or a LiveKit token would land the moment §3.4 grows one.
        using var files = new TemporaryStore();
        var memory = new AgentMemory(files.Store, new RecordingLog(), new ManualClock());

        memory.RememberSettings(Push(("immich.apiKey", "not-a-real-key")));

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            files.Permissions.ModeOf(files.Store.PathOf(AgentMemory.FileName)));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            files.Permissions.ModeOf(files.Root));
    }

    [Fact]
    public void No_setting_value_ever_reaches_the_log()
    {
        using var files = new TemporaryStore();
        var log = new RecordingLog();
        var memory = new AgentMemory(files.Store, log, new ManualClock());

        memory.RememberSettings(Push(("immich.apiKey", "not-a-real-key")));

        Assert.DoesNotContain("not-a-real-key", log.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public void The_memory_is_staged_and_renamed_so_a_power_cut_cannot_truncate_it()
    {
        // The live file is never opened for writing: the bytes go to a sibling, are flushed to
        // the card, and are renamed over the target in one step. What proves it here is that the
        // staging path was the one locked down and written, and that nothing of it is left behind.
        using var files = new TemporaryStore();
        var memory = new AgentMemory(files.Store, new RecordingLog(), new ManualClock());

        memory.RememberSettings(Push(("logging.journalMaxUse", "128M")));

        var staging = files.Store.PathOf(AgentMemory.FileName + FileStateStore.StagingSuffix);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, files.Permissions.ModeOf(staging));
        Assert.False(File.Exists(staging));
        Assert.Contains("128M", files.Store.ReadText(AgentMemory.FileName)!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stale_staging_file_from_an_interrupted_write_is_simply_overwritten()
    {
        using var files = new TemporaryStore();
        files.Store.EnsureReady();
        File.WriteAllText(files.Store.PathOf(AgentMemory.FileName + FileStateStore.StagingSuffix), "half a fi");

        var memory = new AgentMemory(files.Store, new RecordingLog(), new ManualClock());
        memory.RememberSettings(Push(("logging.journalMaxUse", "128M")));

        var reloaded = new AgentMemory(files.Store, new RecordingLog(), new ManualClock());

        Assert.Equal("128M", FleetValues.From(reloaded.Settings).Get("logging.journalMaxUse", "64M"));
    }

    [Fact]
    public void A_write_that_fails_leaves_the_last_good_memory_on_disk_and_does_not_throw()
    {
        using var files = new TemporaryStore();
        var good = new AgentMemory(files.Store, new RecordingLog(), new ManualClock());
        good.RememberSettings(Push(("logging.journalMaxUse", "128M")));

        var log = new RecordingLog();
        var failing = new AgentMemory(new UnwritableStore(files.Store), log, new ManualClock());
        failing.RememberSettings(Push(("logging.journalMaxUse", "512M")));

        var reloaded = new AgentMemory(files.Store, new RecordingLog(), new ManualClock());

        Assert.Equal("128M", FleetValues.From(reloaded.Settings).Get("logging.journalMaxUse", "64M"));
        Assert.Contains("Could not write the agent's memory", log.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_memory_never_records_its_own_resumed_condition_as_a_fresh_answer()
    {
        // A frame that could re-confirm its own memory would keep agreeing with itself long after
        // the Fleet Manager had stopped agreeing with it, and the timestamp would say the server
        // spoke at a moment it did not.
        using var files = new TemporaryStore();

        using (var before = Process(files))
        {
            await AnswerAsync(before, AgentServerScript.Ok());
            MarkGreen(before);
        }

        var answeredAt = new AgentMemory(files.Store, new RecordingLog(), new ManualClock()).Read().VerdictUtc;

        using var after = Process(files);
        after.Hub.Publish(status => status with { Attempt = 1 });
        await OutageAsync(after);

        var reloaded = new AgentMemory(files.Store, new RecordingLog(), new ManualClock()).Read();

        Assert.Equal(HandshakeStatus.Ok, reloaded.Verdict);
        Assert.Equal(answeredAt, reloaded.VerdictUtc);
    }

    [Fact]
    public void Nothing_remembered_expires_however_long_the_frame_has_been_offline()
    {
        // The staleness decision, stated as behaviour. §2.6's argument is that a frame keeps doing
        // the last thing it was legitimately told; the alternative to a stale value is the catalog
        // default, which is older still and which the operator did not choose. A timeout would
        // also have to be judged against a clock that, after the very power cut this exists for,
        // has not yet been corrected by NTP over the network that is down.
        using var files = new TemporaryStore();
        var clock = new ManualClock();

        var before = new AgentMemory(files.Store, new RecordingLog(), clock);
        before.RememberAnswer(DeviceStateLadder.FromHandshake(AgentServerScript.Ok()));
        before.RememberSettings(Push(("logging.journalMaxUse", "128M")));

        clock.UtcNow += TimeSpan.FromDays(400);

        var after = new AgentMemory(files.Store, new RecordingLog(), clock);

        Assert.Equal("128M", FleetValues.From(after.Settings).Get("logging.journalMaxUse", "64M"));
        Assert.NotNull(after.ResumeCondition(hasEverBeenInSync: true));
    }

    /// <summary>Starts a process over a state directory that outlives it.</summary>
    private static FrameProcess Process(TemporaryStore files) => new(files.Store);

    /// <summary>Runs the real link against a server that answers <paramref name="verdict"/>.</summary>
    private static Task AnswerAsync(FrameProcess frame, HandshakeResult verdict) =>
        LinkAsync(frame, new RecordingServer(verdict));

    /// <summary>Runs the real link against a Fleet Manager that is not there.</summary>
    private static Task OutageAsync(FrameProcess frame) =>
        LinkAsync(frame, new RecordingServer(AgentServerScript.Ok()) { Refuse = true });

    private static async Task LinkAsync(FrameProcess frame, RecordingServer server)
    {
        using var key = DeviceKey.From(DeviceIdentity.CreateKeyPair());
        using var stop = new CancellationTokenSource();
        var clock = new ManualClock();

        var link = new ControlLink(
            server,
            frame.Hub,
            key,
            clock,
            NullLog.Instance,
            () => [new Uri("https://framelink.example.org/")],
            new Backoff(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), jitter: 0));

        clock.OnDelay = _ =>
        {
            if (link.CompletedAttempts >= 1)
            {
                stop.Cancel();
            }
        };

        await link.RunAsync(stop.Token);
    }

    /// <summary>Records the frame reaching a converged pass, as <see cref="ReconcileLoop"/> does.</summary>
    private static void MarkGreen(FrameProcess frame) =>
        frame.Journal.Update(state => state with { FirstInSyncUtc = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero) });

    private static HandshakeResult Verdict(string status) => new()
    {
        Status = status,
        ProtocolVersion = ProtocolConstants.Version,
    };

    private static SettingsPush Push(params (string Key, string Value)[] values) => new()
    {
        DeviceId = "TEST-DEVI-CEID-0001",
        Revision = values.Length,
        Values = values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
    };
}

/// <summary>
/// One agent process over a state directory that outlives it.
/// </summary>
/// <remarks>
/// The startup lines of <c>AgentHost</c> that decide what a frame knows before it has spoken to
/// anything, and the subscription that keeps the memory mirroring the hub. Disposing it and
/// building another over the same store is a reboot.
/// </remarks>
internal sealed class FrameProcess : IDisposable
{
    private readonly IDisposable _remembering;

    public FrameProcess(IStateStore store)
    {
        Store = store;
        Journal = new ReconcileJournal(store, Log);
        Memory = new AgentMemory(store, Log, Clock);

        var resumed = Memory.ResumeCondition(Journal.Read().FirstInSyncUtc is not null);

        Settings = Memory.Settings;
        DeviceName = Memory.DeviceName;

        Hub = new AgentStatusHub(new AgentStatus
        {
            Condition = resumed ?? DeviceStateLadder.Starting,
            LastAuthoritative = resumed,
            DeviceId = "TEST-DEVI-CEID-0001",
        });

        _remembering = Hub.Subscribe(status =>
        {
            if (status.LastAuthoritative is { } answered)
            {
                Memory.RememberAnswer(answered);
            }
        });
    }

    public IStateStore Store { get; }

    public RecordingLog Log { get; } = new();

    public ManualClock Clock { get; } = new();

    public ReconcileJournal Journal { get; }

    public AgentMemory Memory { get; }

    public AgentStatusHub Hub { get; }

    public IReadOnlyDictionary<string, string> Settings { get; }

    public string? DeviceName { get; }

    public void Dispose() => _remembering.Dispose();
}

/// <summary>A store whose atomic writes fail, as a full or read-only card would.</summary>
internal sealed class UnwritableStore : IStateStore
{
    private readonly IStateStore _inner;

    public UnwritableStore(IStateStore inner) => _inner = inner;

    public string Root => _inner.Root;

    public void EnsureReady() => _inner.EnsureReady();

    public bool Exists(string name) => _inner.Exists(name);

    public byte[]? ReadBytes(string name) => _inner.ReadBytes(name);

    public string? ReadText(string name) => _inner.ReadText(name);

    public void WriteSecret(string name, ReadOnlySpan<byte> content) => throw new IOException("No space left on device.");

    public void WriteSecretAtomic(string name, ReadOnlySpan<byte> content) => throw new IOException("No space left on device.");

    public void WriteText(string name, string content) => throw new IOException("No space left on device.");

    public void Delete(string name) => _inner.Delete(name);

    public string PathOf(string name) => _inner.PathOf(name);
}
