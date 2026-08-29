using System.Text;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Tests;

/// <summary>
/// <b>The backstop under §2.5's ladder</b> — the operator's own question: "how can we track the
/// agent's loop is not triggering constant system reboots?"
/// </summary>
/// <remarks>
/// <para>
/// The three-attempt budget is not an answer to it on its own, because the budget lives in the
/// journal and the journal can be missing, truncated or unwritable. Every one of those reads back as
/// an empty ledger, which the ladder calls <i>attempt one of three</i> — so a frame in that state
/// restarts itself for ever with every counter reporting its first try. This file is the guard on the
/// other reading, and every assertion in it is about the same property: <b>nothing that goes wrong
/// with this file may increase the number of restarts a frame takes</b>.
/// </para>
/// <para>
/// <b>Nothing here has run on hardware.</b> These drive the shipping class against a real temporary
/// directory and against bytes chosen to model the ways a card fails.
/// </para>
/// </remarks>
public sealed class AgentRebootAllowanceTests
{
    private const int Size = 3;

    [Fact]
    public void An_allowance_that_was_never_written_is_spent_rather_than_fresh()
    {
        // The whole design in one assertion. A counter of restarts *taken* reads zero here and hands
        // the frame three more; a store of restarts *remaining* reads zero and refuses. Absence is
        // the commonest state of a file — a wiped card, a removed state directory, a build that has
        // never run — so it is the reading that has to be safe.
        using var store = new TemporaryStore();
        var allowance = new RebootAllowance(store.Store, new RecordingLog(), Size);

        Assert.Equal(0, allowance.Remaining());

        var grant = allowance.TrySpend();

        Assert.False(grant.Granted);
        Assert.Equal(0, grant.Remaining);
        Assert.Equal(RebootAllowance.Exhausted(Size), grant.Refusal);
    }

    [Fact]
    public void A_refill_grants_exactly_the_ladders_own_budget_and_no_more()
    {
        using var store = new TemporaryStore();
        var allowance = new RebootAllowance(store.Store, new RecordingLog(), Size);

        Assert.True(allowance.Refill());
        Assert.Equal(Size, allowance.Remaining());

        // Refilling twice is not cumulative. A frame that keeps demonstrating health does not
        // accumulate restarts against the day it stops being healthy.
        Assert.True(allowance.Refill());
        Assert.Equal(Size, allowance.Remaining());
    }

    [Fact]
    public void Three_spends_are_granted_and_the_fourth_is_refused()
    {
        // The operator's model, held by this mechanism alone rather than by the ladder: reboot and
        // retry automatically three times, and after the third stop and wait for a person.
        using var store = new TemporaryStore();
        var allowance = new RebootAllowance(store.Store, new RecordingLog(), Size);
        allowance.Refill();

        Assert.Equal(new RebootAllowanceGrant(true, 2, null), allowance.TrySpend());
        Assert.Equal(new RebootAllowanceGrant(true, 1, null), allowance.TrySpend());
        Assert.Equal(new RebootAllowanceGrant(true, 0, null), allowance.TrySpend());

        var fourth = allowance.TrySpend();

        Assert.False(fourth.Granted);
        Assert.Equal(RebootAllowance.Exhausted(Size), fourth.Refusal);
    }

    [Fact]
    public void A_spend_survives_the_restart_it_is_counting()
    {
        // It is durable for the same reason the attempt ledger is, and it is read from the card
        // every time rather than cached: the process that spends the last one is not the process
        // that has to refuse the next.
        using var store = new TemporaryStore();
        new RebootAllowance(store.Store, new RecordingLog(), Size).Refill();

        new RebootAllowance(store.Store, new RecordingLog(), Size).TrySpend();
        new RebootAllowance(store.Store, new RecordingLog(), Size).TrySpend();
        new RebootAllowance(store.Store, new RecordingLog(), Size).TrySpend();

        var afterReboots = new RebootAllowance(store.Store, new RecordingLog(), Size);

        Assert.Equal(0, afterReboots.Remaining());
        Assert.False(afterReboots.TrySpend().Granted);
    }

    [Fact]
    public void Nothing_a_corrupt_file_can_contain_grants_a_restart_it_does_not_hold()
    {
        // Mutation, on the guard whose failure is invisible. Every one of these is a real thing a
        // card does to a file, and the property is one-sided: the count may come out lower than what
        // was written, never higher.
        Assert.Equal(0, RebootAllowance.Count([]));
        Assert.Equal(3, RebootAllowance.Count("###"u8));

        // Truncated by a power cut mid-write.
        Assert.Equal(1, RebootAllowance.Count("#"u8));

        // Garbled by a bad block: a byte that is no longer the token stops counting.
        Assert.Equal(1, RebootAllowance.Count([RebootAllowance.Token, 0x00, 0xFF]));

        // Zeroed by a filesystem that lost the tail of the write.
        Assert.Equal(0, RebootAllowance.Count([0x00, 0x00, 0x00]));

        // Hand-edited and saved with a trailing newline, which every editor adds.
        Assert.Equal(3, RebootAllowance.Count("###\n"u8));

        // The journal's own content, in case somebody ever points this at the wrong file. It holds
        // no tokens, so it grants nothing — a parser would have thrown or, worse, succeeded.
        Assert.Equal(0, RebootAllowance.Count(Encoding.UTF8.GetBytes("{\"ledger\":[],\"reboots\":[]}")));

        // A number written down, which is what this file deliberately is not. "3" is not three.
        Assert.Equal(0, RebootAllowance.Count("3"u8));
    }

    [Fact]
    public void A_file_full_of_rubbish_is_read_as_no_allowance_rather_than_as_an_error()
    {
        using var store = new TemporaryStore();
        store.Store.WriteText(RebootAllowance.FileName, "not a token in sight");

        var allowance = new RebootAllowance(store.Store, new RecordingLog(), Size);

        Assert.Equal(0, allowance.Remaining());
        Assert.False(allowance.TrySpend().Granted);
    }

    [Fact]
    public void An_allowance_that_cannot_be_read_is_treated_as_spent_and_says_so()
    {
        // The case that matters: the storage the backstop itself depends on has failed. It refuses,
        // which costs the frame its automatic recovery and keeps its bound — the opposite trade to
        // the one the journal makes, and deliberately so, because the journal can afford to be
        // forgiving and this cannot.
        var log = new RecordingLog();
        var allowance = new RebootAllowance(new UnreadableStore(), log, Size);

        Assert.Equal(0, allowance.Remaining());

        var grant = allowance.TrySpend();

        Assert.False(grant.Granted);
        Assert.Equal(RebootAllowance.Exhausted(Size), grant.Refusal);
        Assert.Contains("could not be read", log.Transcript, StringComparison.Ordinal);
        Assert.Contains("treated as spent", log.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public void A_restart_that_cannot_be_written_down_is_not_taken()
    {
        // A read-only root, a full card, a state directory removed under a running agent. The
        // allowance still says three, and the spend still has to be refused: a restart that cannot
        // be counted is a restart that can be taken again on the next boot, for ever, which is the
        // exact cycle this class exists to bound.
        var log = new RecordingLog();
        var store = new UnwritableStore(new string((char)RebootAllowance.Token, Size));
        var allowance = new RebootAllowance(store, log, Size);

        Assert.Equal(Size, allowance.Remaining());

        var grant = allowance.TrySpend();

        Assert.False(grant.Granted);
        Assert.Equal(RebootAllowance.NotRecorded, grant.Refusal);
        Assert.Equal(Size, grant.Remaining);
        Assert.Contains("could not spend its restart allowance", log.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public void A_write_that_reports_success_and_does_not_persist_is_caught_by_reading_it_back()
    {
        // The invisible failure. WriteText returns, nothing throws, and the bytes are not there —
        // which is what a filesystem remounted read-only under a cached handle, or a card that has
        // exhausted its spare blocks, actually looks like. Without the read-back this class would
        // report a granted spend on every boot for ever and be indistinguishable from having no
        // backstop at all.
        var log = new RecordingLog();
        var store = new SilentlyDiscardingStore(new string((char)RebootAllowance.Token, Size));
        var allowance = new RebootAllowance(store, log, Size);

        var grant = allowance.TrySpend();

        Assert.False(grant.Granted);
        Assert.Equal(RebootAllowance.NotRecorded, grant.Refusal);
        Assert.Contains("read back 3", log.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refill_that_cannot_be_written_leaves_the_allowance_where_it_was()
    {
        // The fail-safe direction, asserted rather than assumed. A refill is the only thing that
        // increases the count, so a refill that fails can only ever leave a frame with fewer
        // restarts than it should have — never more.
        var log = new RecordingLog();
        var store = new UnwritableStore(new string((char)RebootAllowance.Token, 1));
        var allowance = new RebootAllowance(store, log, Size);

        Assert.False(allowance.Refill());
        Assert.Equal(1, allowance.Remaining());
        Assert.Contains("could not refill its restart allowance", log.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public void A_budget_of_zero_means_a_frame_never_restarts_itself()
    {
        // The same thing the ladder does with the same number: AttemptBudget=0 makes the first
        // failure the exhausted one. There is no off switch here, and that is on purpose — a size
        // that meant "unbounded" would be a remotely settable way to turn the backstop off.
        using var store = new TemporaryStore();
        var allowance = new RebootAllowance(store.Store, new RecordingLog(), 0);

        Assert.True(allowance.Refill());
        Assert.Equal(0, allowance.Remaining());
        Assert.False(allowance.TrySpend().Granted);
    }

    [Fact]
    public void A_negative_budget_is_zero_rather_than_a_throw_or_an_infinity()
    {
        using var store = new TemporaryStore();
        var allowance = new RebootAllowance(store.Store, new RecordingLog(), -5);

        Assert.True(allowance.Refill());
        Assert.Equal(0, allowance.Remaining());
        Assert.False(allowance.TrySpend().Granted);
        Assert.Contains("restarted itself 0 times", RebootAllowance.Exhausted(-5), StringComparison.Ordinal);
    }

    [Fact]
    public void The_file_sits_beside_the_journal_and_is_readable_with_cat()
    {
        // Two properties a person with an SSH session depends on. It is in the state directory §2.1
        // already protects from an update, and its content is printable, so `cat` and `wc -c` are
        // the whole diagnostic.
        using var store = new TemporaryStore();
        var allowance = new RebootAllowance(store.Store, new RecordingLog(), Size);
        allowance.Refill();

        Assert.Equal(store.Store.PathOf(RebootAllowance.FileName), allowance.Path);
        Assert.Equal("###", File.ReadAllText(allowance.Path));
    }

    /// <summary>A store whose reads throw, which is a card that has stopped answering.</summary>
    private sealed class UnreadableStore : IStateStore
    {
        public string Root => "/var/lib/fl-agent";

        public void EnsureReady()
        {
        }

        public bool Exists(string name) => true;

        public byte[]? ReadBytes(string name) => throw new IOException("Input/output error");

        public string? ReadText(string name) => throw new IOException("Input/output error");

        public void WriteSecretAtomic(string name, ReadOnlySpan<byte> content)
        {
        }

        public void WriteText(string name, string content)
        {
        }

        public void Delete(string name)
        {
        }

        public bool TryRename(string name, string newName) => false;

        public string PathOf(string name) => $"{Root}/{name}";
    }

    /// <summary>A store that reads but cannot write — a read-only root, or a full card.</summary>
    private sealed class UnwritableStore : IStateStore
    {
        private readonly string _content;

        public UnwritableStore(string content) => _content = content;

        public string Root => "/var/lib/fl-agent";

        public void EnsureReady()
        {
        }

        public bool Exists(string name) => true;

        public byte[]? ReadBytes(string name) => Encoding.UTF8.GetBytes(_content);

        public string? ReadText(string name) => _content;

        public void WriteSecretAtomic(string name, ReadOnlySpan<byte> content) => throw Readonly();

        public void WriteText(string name, string content) => throw Readonly();

        public void Delete(string name) => throw Readonly();

        public bool TryRename(string name, string newName) => throw Readonly();

        public string PathOf(string name) => $"{Root}/{name}";

        private static IOException Readonly() => new("Read-only file system");
    }

    /// <summary>A store whose writes return and do not persist. The invisible failure.</summary>
    private sealed class SilentlyDiscardingStore : IStateStore
    {
        private readonly string _content;

        public SilentlyDiscardingStore(string content) => _content = content;

        public string Root => "/var/lib/fl-agent";

        public int Writes { get; private set; }

        public void EnsureReady()
        {
        }

        public bool Exists(string name) => true;

        public byte[]? ReadBytes(string name) => Encoding.UTF8.GetBytes(_content);

        public string? ReadText(string name) => _content;

        public void WriteSecretAtomic(string name, ReadOnlySpan<byte> content) => Writes++;

        public void WriteText(string name, string content) => Writes++;

        public void Delete(string name)
        {
        }

        public bool TryRename(string name, string newName) => false;

        public string PathOf(string name) => $"{Root}/{name}";
    }
}
