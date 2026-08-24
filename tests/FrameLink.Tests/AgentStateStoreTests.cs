using System.Text;
using System.Text.Json;
using FrameLink.Agent;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Tests;

/// <summary>
/// <b>A pulled plug may not truncate a state file, and a state file it could not read may not
/// look like a state file that was never written.</b>
/// </summary>
/// <remarks>
/// <para>
/// Two halves of one fault. <see cref="FileStateStore.WriteText"/> was a plain
/// <c>File.WriteAllText</c> while <see cref="FileStateStore.WriteSecretAtomic"/> staged, flushed
/// and renamed — so every state file that was not a secret could be truncated by a power cut. And
/// <see cref="ReconcileJournal"/> read a file it could not parse as an empty journal, which set
/// every attempt counter to zero and handed the frame a fresh budget and a fresh set of reboots.
/// Either alone is survivable; together they are §2.4's unbounded reboot loop reached through the
/// mechanism built to prevent it.
/// </para>
/// <para>
/// <b>What these tests do not prove.</b> None of them pulls power. A crash mid-write cannot be
/// produced from inside the process doing the writing, so what is asserted here is the
/// <i>mechanism</i> — that the bytes go to a sibling, that the sibling is flushed to the card, that
/// the live file is only ever reached by a rename — and the <i>file states</i> a crash leaves
/// behind, written directly onto disk: zero length, a truncated prefix, a new prefix over an old
/// tail, a stale staging file. Real durability across a real power cut is a claim about the SD
/// card and the kernel, and only a bench test with the plug in somebody's hand can make it.
/// </para>
/// </remarks>
public sealed class AgentStateStoreTests
{
    private const UnixFileMode Secret = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode Data =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

    // -----------------------------------------------------------------------------------------
    // The general write path is now the write the secrets already had
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void An_ordinary_state_file_is_staged_and_renamed_exactly_as_a_secret_is()
    {
        // The live file is never opened for writing. What proves it is that the staging path is the
        // one the mode was applied to — nothing else applies a mode to that path — and that it is
        // gone afterwards because it was renamed rather than deleted.
        using var files = new TemporaryStore();

        files.Store.WriteText("device-name", "framelink-douwe");

        var staging = files.Store.PathOf("device-name" + FileStateStore.StagingSuffix);

        Assert.Equal(Data, files.Permissions.ModeOf(staging));
        Assert.False(File.Exists(staging));
        Assert.Equal("framelink-douwe", files.Store.ReadText("device-name"));
    }

    [Fact]
    public void The_only_difference_left_between_the_two_writes_is_the_mode()
    {
        // The shared implementation, asserted as the one thing that still differs. Both stage under
        // the same suffix in the same directory; a secret lands at 0600 and ordinary state at 0640.
        using var files = new TemporaryStore();

        files.Store.WriteText("app.room", "family");
        files.Store.WriteSecretAtomic("app.livekit-token", Encoding.UTF8.GetBytes("a.b.c"));

        Assert.Equal(Data, files.Permissions.ModeOf(files.Store.PathOf("app.room")));
        Assert.Equal(Secret, files.Permissions.ModeOf(files.Store.PathOf("app.livekit-token")));

        Assert.Equal(
            Data,
            files.Permissions.ModeOf(files.Store.PathOf("app.room" + FileStateStore.StagingSuffix)));
        Assert.Equal(
            Secret,
            files.Permissions.ModeOf(
                files.Store.PathOf("app.livekit-token" + FileStateStore.StagingSuffix)));
    }

    [Fact]
    public void The_staging_file_is_always_a_sibling_so_the_rename_cannot_cross_a_filesystem()
    {
        // The same-filesystem guarantee is structural rather than checked at runtime: PathOf
        // refuses anything that could leave the root, so a staging file and its target are always
        // in one directory. On a frame that is the ext4 root; /boot/firmware is FAT32 and nothing
        // reachable through this interface can name it.
        using var files = new TemporaryStore();

        var target = files.Store.PathOf("boot-trials.json");
        var staging = files.Store.PathOf("boot-trials.json" + FileStateStore.StagingSuffix);

        Assert.Equal(Path.GetDirectoryName(target), Path.GetDirectoryName(staging));
        Assert.Equal(files.Root, Path.GetDirectoryName(target));

        Assert.Throws<ArgumentException>(() => files.Store.PathOf("../../boot/firmware/config.txt"));
        Assert.Throws<ArgumentException>(() => files.Store.PathOf("nested/name"));
    }

    [Fact]
    public void A_stale_staging_file_left_by_an_interrupted_write_is_simply_overwritten()
    {
        // The file a crash between write and rename leaves behind. Nothing reads it, and the next
        // write truncates it, so the cost of that case is one orphaned file and no lost state.
        using var files = new TemporaryStore();
        files.Store.EnsureReady();

        var staging = files.Store.PathOf("endpoints.json" + FileStateStore.StagingSuffix);
        File.WriteAllText(staging, "{\"endpoints\":[{\"host\":\"half-a-fi");

        files.Store.WriteText("endpoints.json", "{\"endpoints\":[]}");

        Assert.False(File.Exists(staging));
        Assert.Equal("{\"endpoints\":[]}", files.Store.ReadText("endpoints.json"));
    }

    [Fact]
    public void A_write_that_cannot_even_start_leaves_the_last_good_file_exactly_as_it_was()
    {
        // The plain overwrite this replaced truncated the live file first, so a write that failed
        // early destroyed the previous content. Staging means a failure before the rename cannot
        // touch the target at all. The failure is simulated by putting a directory where the
        // staging file wants to be, which is the cheapest way to make one FileStream throw.
        using var files = new TemporaryStore();
        files.Store.WriteText("device-name", "framelink-douwe");

        Directory.CreateDirectory(files.Store.PathOf("device-name" + FileStateStore.StagingSuffix));

        Assert.ThrowsAny<Exception>(() => files.Store.WriteText("device-name", "framelink-jori"));
        Assert.Equal("framelink-douwe", files.Store.ReadText("device-name"));
    }

    [Fact]
    public void Routing_text_through_the_atomic_path_did_not_add_a_byte_order_mark()
    {
        // A BOM would break every consumer that reads one of these as a bare value — a systemd
        // EnvironmentFile, a shell reading device-name — and would be an easy thing to introduce by
        // swapping File.WriteAllText for an encoder with different defaults.
        using var files = new TemporaryStore();

        files.Store.WriteText("kiosk.offline-mode", "true");

        Assert.Equal("true"u8.ToArray(), files.Store.ReadBytes("kiosk.offline-mode"));
    }

    // -----------------------------------------------------------------------------------------
    // An unreadable journal is a fault, and absent is not unreadable
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void A_journal_that_was_never_written_is_an_empty_journal_and_not_a_fault()
    {
        // The branch that has to keep working, and the reason this is not simply strict: a frame on
        // its first boot has no journal, has forgotten nothing, and must provision normally.
        using var files = new TemporaryStore();
        var log = new RecordingLog();

        var journal = new ReconcileJournal(files.Store, log);

        Assert.False(journal.Unreadable);
        Assert.Empty(journal.Read().Ledger);
        Assert.Empty(journal.Read().Reboots);
        Assert.DoesNotContain("Fail:", log.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zero_length_journal_is_a_fault_and_not_a_fresh_frame()
    {
        // <b>The silent case, and the one that actually bit.</b> File.WriteAllText truncates before
        // it writes, so the single most likely thing a power cut left behind was a zero-length
        // journal — and IsNullOrWhiteSpace read that as "start from nothing". No exception, no log
        // line, every attempt counter zero. ReadText returns null for absent and "" for empty, so
        // the two are distinguishable; they just were not being distinguished.
        using var files = new TemporaryStore();
        files.Store.EnsureReady();
        File.WriteAllText(files.Store.PathOf(ReconcileJournal.FileName), string.Empty);

        var log = new RecordingLog();
        var journal = new ReconcileJournal(files.Store, log);

        Assert.True(journal.Unreadable);
        Assert.Contains("Fail:", log.Transcript, StringComparison.Ordinal);
        Assert.Contains("holds nothing", log.Transcript, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("   \r\n\t ", "whitespace where a journal should be")]
    [InlineData("{\"ledger\":[{\"resource\":\"audio.mixer\",\"attempts\":2}],\"reb", "a truncated prefix")]
    [InlineData("{\"telemetrySequence\":9}\"lastBootId\":\"d6ab25f9\"}", "a new prefix over an old tail")]
    [InlineData("null", "well-formed JSON that is not a journal")]
    [InlineData("\0\0\0\0", "the nulls a card fills a short write with")]
    public void Every_shape_a_half_finished_write_leaves_behind_is_a_fault(string content, string shape)
    {
        // Written straight onto disk rather than produced by a crash: these are the file states,
        // not the event. The third is the nastiest and the reason a length check would not do — a
        // shorter new value over a longer old one leaves valid bytes at both ends and garbage in
        // the middle.
        using var files = new TemporaryStore();
        files.Store.EnsureReady();
        File.WriteAllText(files.Store.PathOf(ReconcileJournal.FileName), content);

        var log = new RecordingLog();
        var journal = new ReconcileJournal(files.Store, log);

        Assert.True(journal.Unreadable, shape);
        Assert.Contains("Fail:", log.Transcript, StringComparison.Ordinal);
        Assert.Empty(journal.Read().Ledger);
    }

    [Fact]
    public void A_journal_this_build_wrote_is_read_without_a_fault()
    {
        // The other side of the theory above: the guards must not fire on a real journal, including
        // one whose lists are absent because an older build never wrote them.
        using var files = new TemporaryStore();

        files.Store.WriteText(
            ReconcileJournal.FileName,
            """{ "lastBootId": "d6ab25f9", "telemetrySequence": 1112 }""");

        var log = new RecordingLog();
        var journal = new ReconcileJournal(files.Store, log);

        Assert.False(journal.Unreadable);
        Assert.Equal("d6ab25f9", journal.Read().LastBootId);
        Assert.DoesNotContain("Fail:", log.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public void A_journal_that_cannot_be_opened_at_all_is_a_fault_rather_than_an_exception()
    {
        // This used to throw straight out of Load, through Read, and into the reconcile loop's
        // task, where nothing was waiting to hear about it. A dying card is a fault to report, not
        // a reason the frame cannot start.
        using var files = new TemporaryStore();
        var log = new RecordingLog();

        var journal = new ReconcileJournal(new UnreadableStore(files.Store), log);

        Assert.True(journal.Unreadable);
        Assert.Empty(journal.Read().Ledger);
        Assert.Contains("could not be opened", log.Transcript, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // What the fault does, and what it deliberately does not do
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void The_bytes_it_could_not_read_are_kept_beside_the_journal()
    {
        // The next Update overwrites the journal with something this build can serialise, so
        // without this the only trace of what the frame actually found would be one log line on a
        // card whose logs may not have survived either.
        using var files = new TemporaryStore();
        files.Store.EnsureReady();
        File.WriteAllText(files.Store.PathOf(ReconcileJournal.FileName), "{ this was never json");

        var journal = new ReconcileJournal(files.Store, new RecordingLog());
        Assert.True(journal.Unreadable);

        journal.Update(state => state with { LastBootId = "a-new-boot" });

        Assert.Equal("{ this was never json", files.Store.ReadText(ReconcileJournal.UnreadableFileName));
        Assert.Contains("a-new-boot", files.Store.ReadText(ReconcileJournal.FileName)!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_frame_that_lost_its_journal_still_starts_and_still_reconciles()
    {
        // The constraint that shapes the whole fix. Nothing in Load throws, whatever the file
        // contains, because a state store that bricks a frame would be worse than the bug it was
        // fixing. Read, Update and the ledger helpers all keep working on a faulted journal.
        using var files = new TemporaryStore();
        files.Store.EnsureReady();
        File.WriteAllText(files.Store.PathOf(ReconcileJournal.FileName), " not text at all");

        var journal = new ReconcileJournal(files.Store, new RecordingLog());

        var state = journal.Read();
        Assert.NotNull(state);

        var entry = ReconcileJournal.EntryFor(state, "audio.mixer");
        Assert.Equal(0, entry.Attempts);

        var written = journal.Update(next => ReconcileJournal.WithEntry(next, entry with { Attempts = 1 }));
        Assert.Single(written.Ledger);
    }

    [Fact]
    public async Task A_frame_that_lost_its_journal_does_not_award_itself_a_reboot()
    {
        // The whole point. An empty reboot list on a faulted journal means the record was lost, not
        // that this frame has never rebooted, and the two are indistinguishable from the list
        // alone. A floor that cannot count must not permit.
        using var files = new TemporaryStore();
        files.Store.EnsureReady();
        File.WriteAllText(files.Store.PathOf(ReconcileJournal.FileName), string.Empty);

        var log = new RecordingLog();
        var journal = new ReconcileJournal(files.Store, log);
        var floor = Floor(journal, log);

        var outcome = await floor.CrossAsync(Request, TestContext.Current.CancellationToken);

        Assert.Equal(RebootCrossing.Refused, outcome.Crossing);
        Assert.Contains("could not read its own record", outcome.Detail!, StringComparison.Ordinal);
        Assert.Contains("Fail:", log.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_frame_with_a_journal_it_can_read_reboots_exactly_as_before()
    {
        // The refusal is not a new brake on ordinary provisioning: the same floor, on a journal
        // that parses, crosses on the first request and records it.
        using var files = new TemporaryStore();
        var log = new RecordingLog();
        var journal = new ReconcileJournal(files.Store, log);
        var floor = Floor(journal, log);

        var outcome = await floor.CrossAsync(Request, TestContext.Current.CancellationToken);

        Assert.Equal(RebootCrossing.Crossed, outcome.Crossing);
        Assert.Equal(1, floor.Recent());
    }

    [Fact]
    public async Task A_person_pressing_retry_gives_the_reboots_back()
    {
        // The escape hatch, and the reason the fault cannot strand a frame. The flag deliberately
        // does not clear itself on a successful write — the loop writes the journal on every pass,
        // so it would clear within a second and nothing would ever see it — which would leave a
        // frame declining to reboot until somebody reached it over SSH. The people who own these
        // frames do not have SSH; they have the retry button.
        using var files = new TemporaryStore();
        files.Store.EnsureReady();
        File.WriteAllText(files.Store.PathOf(ReconcileJournal.FileName), "not a journal");

        var log = new RecordingLog();
        var journal = new ReconcileJournal(files.Store, log);
        var floor = Floor(journal, log);

        Assert.Equal(
            RebootCrossing.Refused,
            (await floor.CrossAsync(Request, TestContext.Current.CancellationToken)).Crossing);

        floor.Forget();

        Assert.False(journal.Unreadable);
        Assert.Equal(
            RebootCrossing.Crossed,
            (await floor.CrossAsync(Request, TestContext.Current.CancellationToken)).Crossing);
    }

    [Fact]
    public void A_process_that_starts_on_a_journal_that_parses_carries_no_fault()
    {
        // The other way out, and the ordinary one: the faulted process rewrites the file, and the
        // next process reads it cleanly. The fault lasts one process lifetime, not the frame's.
        using var files = new TemporaryStore();
        files.Store.EnsureReady();
        File.WriteAllText(files.Store.PathOf(ReconcileJournal.FileName), "{ truncated");

        var faulted = new ReconcileJournal(files.Store, new RecordingLog());
        Assert.True(faulted.Unreadable);
        faulted.Update(state => state with { TelemetrySequence = 7 });

        var restarted = new ReconcileJournal(files.Store, new RecordingLog());

        Assert.False(restarted.Unreadable);
        Assert.Equal(7, restarted.Read().TelemetrySequence);
    }

    [Fact]
    public void A_journal_written_through_the_store_round_trips_through_the_atomic_path()
    {
        // Both halves in one place: the journal's own write now stages and renames like everything
        // else, and what comes back parses without a fault.
        using var files = new TemporaryStore();
        var journal = new ReconcileJournal(files.Store, new RecordingLog());

        journal.Update(state => state with { Reboots = [DateTimeOffset.UnixEpoch], LastBootId = "b1" });

        var staging = files.Store.PathOf(ReconcileJournal.FileName + FileStateStore.StagingSuffix);
        Assert.False(File.Exists(staging));
        Assert.Equal(Data, files.Permissions.ModeOf(staging));

        var reread = new ReconcileJournal(files.Store, new RecordingLog());
        Assert.False(reread.Unreadable);
        Assert.Single(reread.Read().Reboots);
    }

    [Fact]
    public void The_journal_the_store_writes_is_the_journal_the_store_reads()
    {
        // Serialisation and the atomic write agreeing, asserted on bytes rather than behaviour, so
        // that a future change to either is caught here rather than on a frame.
        using var files = new TemporaryStore();
        var journal = new ReconcileJournal(files.Store, new RecordingLog());

        var written = journal.Update(state => state with { TelemetrySequence = 42 });
        var bytes = files.Store.ReadBytes(ReconcileJournal.FileName)!;

        var parsed = JsonSerializer.Deserialize(
            Encoding.UTF8.GetString(bytes),
            AgentJson.Default.ReconcileJournalState);

        Assert.Equal(written.TelemetrySequence, parsed!.TelemetrySequence);
    }

    private static RebootRequest Request => new()
    {
        Resource = "audio.mixer",
        Change = "amixer -c 0 sset PCM,0 60",
        Attempt = 1,
    };

    private static RebootFloor Floor(ReconcileJournal journal, RecordingLog log) =>
        new(
            new InProcessRebootBoundary(new MutableBootIdentity()),
            journal,
            new ManualClock(),
            log,
            limit: 5,
            window: TimeSpan.FromHours(6));

    /// <summary>A store whose journal cannot be opened at all — a dying card, or a bad mode.</summary>
    private sealed class UnreadableStore : IStateStore
    {
        private readonly IStateStore _inner;

        public UnreadableStore(IStateStore inner) => _inner = inner;

        public string Root => _inner.Root;

        public void EnsureReady() => _inner.EnsureReady();

        public bool Exists(string name) => true;

        public byte[]? ReadBytes(string name) => throw new IOException("Input/output error");

        public string? ReadText(string name) => throw new IOException("Input/output error");

        public void WriteSecret(string name, ReadOnlySpan<byte> content) => _inner.WriteSecret(name, content);

        public void WriteSecretAtomic(string name, ReadOnlySpan<byte> content) =>
            _inner.WriteSecretAtomic(name, content);

        public void WriteText(string name, string content) => _inner.WriteText(name, content);

        public void Delete(string name) => _inner.Delete(name);

        public string PathOf(string name) => _inner.PathOf(name);
    }
}
