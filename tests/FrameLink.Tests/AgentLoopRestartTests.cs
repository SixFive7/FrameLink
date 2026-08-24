using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Tests;

/// <summary>
/// <b>What a frame does about a loop that ended</b> — the operator's two decisions, and the way the
/// three protections under them compose.
/// </summary>
/// <remarks>
/// <para>
/// The first decision is that a dead loop reboots the machine rather than the process, because that
/// is what every other failure on this frame does. The second is the one that needed building: a
/// bound that holds when the ledger the bound was previously made of is gone. Every test below that
/// wipes the journal is asserting the second, and each of them also asserts that the ladder alone
/// would <i>not</i> have stopped the frame — otherwise it would be possible to pass the whole file
/// with no backstop at all.
/// </para>
/// <para>
/// <b>Nothing here has run on hardware.</b> No test in this file may reboot anything: the boundary is
/// a counter, and the one that stands in for <c>systemctl</c> is never constructed.
/// </para>
/// </remarks>
public sealed class AgentLoopRestartTests
{
    private const string Loop = "local-origin";
    private const string Resource = "agent.loop.local-origin";
    private const string Purpose = "serve the product app and the repair screen";
    private const string Why = "it returned while the agent was still running";

    /// <summary>Long enough to be forgiven; short enough to be a cascade.</summary>
    private static readonly TimeSpan Healthy = TimeSpan.FromHours(9);

    private static readonly TimeSpan Cascade = TimeSpan.FromSeconds(5);

    private static ReconcileOptions Options => new()
    {
        AttemptBudget = 3,
        ConflictHold = TimeSpan.FromMinutes(5),
    };

    [Fact]
    public async Task Attempts_one_and_two_reboot_the_frame_and_the_third_holds_the_screen()
    {
        // The operator's model, unchanged: reboot and retry automatically, then stop and say so. What
        // changed is which thing restarts — the machine now, rather than the process, so that a loop
        // dying is treated identically to every other failure on this frame.
        using var store = new TemporaryStore();
        var log = new RecordingLog();
        var boundary = new CountingBoundary();
        var options = Options;

        var first = await BootAsync(store, options, boundary, log, Healthy);
        var second = await BootAsync(store, options, boundary, log, Cascade);
        var third = await BootAsync(store, options, boundary, log, Cascade);

        Assert.True(first.Outcome.Restarting);
        Assert.True(second.Outcome.Restarting);
        Assert.False(third.Outcome.Restarting);

        Assert.Equal(2, boundary.Crossings.Count);
        Assert.Equal(Resource, boundary.Crossings[0].Resource);
        Assert.Equal(Why, boundary.Crossings[0].Change);
        Assert.Equal(1, boundary.Crossings[0].Attempt);
        Assert.Equal(2, boundary.Crossings[1].Attempt);

        // The third is the stopped screen, with no refusal to explain: the ladder simply ran out, and
        // nothing was refused because nothing was asked for.
        Assert.True(third.Outcome.Verdict.Stopped);
        Assert.Null(third.Outcome.Refusal);
        Assert.Equal(3, third.Outcome.Verdict.Attempts);

        // One restart is left unspent when the ladder stops first, which is the layering working: the
        // backstop is a bound on the ladder and never the thing that decides an ordinary cascade.
        Assert.Equal(1, new RebootAllowance(store.Store, log, options.AttemptBudget).Remaining());
        Assert.Contains("waiting for a person", log.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_journal_wiped_before_every_boot_still_stops_the_frame_after_three_restarts()
    {
        // <b>The operator's own question.</b> A ledger that reads back empty makes every loop death
        // look like the first one, so the ladder grants three more restarts every time and the frame
        // reboots for ever. This project has already produced a 55-boot cascade from one number being
        // zero.
        using var store = new TemporaryStore();
        var log = new RecordingLog();
        var boundary = new CountingBoundary();
        var options = Options;

        // The first death is on a frame that had been up for hours, which is what fills the
        // allowance. Everything after it is the cascade.
        var boots = new List<Boot>
        {
            await BootAsync(store, options, boundary, log, Healthy),
        };

        for (var boot = 0; boot < 9; boot++)
        {
            // Absent, which is the one branch ReconcileJournal is still allowed to read as an empty
            // journal — correctly, because a first boot has nothing to have forgotten. A wiped state
            // directory, a re-flashed card and anything that tidies /var/lib all look like this, and
            // none of them is a fault the file's reader can see. A journal that is there and will not
            // parse is a different case and is now caught before it gets here.
            store.Store.Delete(ReconcileJournal.FileName);
            boots.Add(await BootAsync(store, options, boundary, log, Cascade));
        }

        // <b>The ladder never once stopped it</b>, and that is the half of this that had to be true
        // for the backstop to be worth building. Every death reads as attempt one of three, ten
        // times over, and on the ladder alone this frame would still be rebooting.
        Assert.All(boots, boot => Assert.Equal(1, boot.Ladder.Attempts));
        Assert.All(boots, boot => Assert.False(boot.Ladder.Stopped));

        // And the frame still stopped after three.
        Assert.Equal(3, boundary.Crossings.Count);
        Assert.Equal(3, boots.Count(boot => boot.Outcome.Restarting));

        var stopped = boots[^1].Outcome;
        Assert.False(stopped.Restarting);
        Assert.True(stopped.Verdict.Stopped);
        Assert.Equal(RebootAllowance.Exhausted(options.AttemptBudget), stopped.Refusal);
    }

    [Fact]
    public async Task A_frame_that_cannot_write_its_allowance_does_not_restart_at_all()
    {
        // The failure mode that defeats every durable counter at once, and the one no reader of the
        // journal can detect: the file reads perfectly and will not take a write. Nothing throws on
        // the way in, so ReconcileJournal.Unreadable stays false and decision 79's floor is satisfied
        // — while the ladder is stuck on attempt one for ever and the floor's list of reboots can
        // never grow. This one refuses, because a restart it cannot count is a restart it can take
        // again on the next boot for ever.
        var log = new RecordingLog();
        var boundary = new CountingBoundary();
        var options = Options;
        var journal = new ReconcileJournal(new ReadOnlyStore(), log);

        var verdict = AgentLoopFailures.Record(journal, options, Loop, Purpose, Why, Healthy);
        Assert.False(verdict.Stopped);

        var outcome = await AgentLoopFailures.RestartOrStopAsync(
            journal,
            options,
            new RebootAllowance(new ReadOnlyStore(), log, options.AttemptBudget),
            boundary,
            verdict,
            Healthy,
            Why,
            log,
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Restarting);
        Assert.Empty(boundary.Crossings);
        Assert.True(outcome.Verdict.Stopped);
        Assert.Equal(RebootAllowance.NotRecorded, outcome.Refusal);
        Assert.Contains("will not take a restart it cannot count", outcome.Verdict.Row.Delta ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_restart_ends_the_ladder_where_it_stands_and_says_both_things()
    {
        // A frame whose restart cannot be taken has no automatic retry left to wait for: this process
        // is parked and the loop is not coming back. Reporting "attempt 1 of 3, reconciling" would
        // offer a repair that will never arrive instead of the two buttons that would fix it, which
        // is decision 75's failure. So the ledger reads as a give-up, which is what every surface
        // downstream already knows how to render.
        using var store = new TemporaryStore();
        var log = new RecordingLog();
        var options = Options;
        var journal = new ReconcileJournal(store.Store, log);
        var allowance = new RebootAllowance(store.Store, log, options.AttemptBudget);
        allowance.Refill();

        var verdict = AgentLoopFailures.Record(journal, options, Loop, Purpose, Why, Healthy);

        var outcome = await AgentLoopFailures.RestartOrStopAsync(
            journal,
            options,
            allowance,
            new CountingBoundary { Refuse = "the microphone update is being written to the unit" },
            verdict,
            Healthy,
            Why,
            log,
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Restarting);
        Assert.True(outcome.Verdict.Stopped);
        Assert.Equal(options.AttemptBudget, outcome.Verdict.Attempts);

        // Both facts survive to the screen and the notification: which loop stopped, and why the
        // frame did not restart itself over it. Either one alone leaves a reader with half a story.
        Assert.Equal(
            "expected serve the product app and the repair screen, observed: it returned while the "
            + "agent was still running; the microphone update is being written to the unit",
            outcome.Verdict.Row.Delta);

        // And it is the ledger the whole ladder reads, not a private one — so ReconcileLoop stops
        // the pass around it, the orphan path renders it, and a retry clears it.
        var entry = ReconcileJournal.EntryFor(journal.Read(), Resource);
        Assert.True(ReconcileLoop.HasGivenUp(entry, options.AttemptBudget));
        Assert.Equal(1, entry.Escalations);
        Assert.False(entry.EscalationNotified);
        Assert.Null(entry.NextAttemptUtc);
    }

    [Fact]
    public async Task Decision_79s_floor_refuses_a_loops_restart_exactly_as_it_refuses_a_resources()
    {
        // The third layer, and the reason a loop death needed no new vocabulary at the boundary: it
        // crosses the same chain a resource's reboot crosses, so the floor counts it and refuses past
        // its limit whatever the ladder or the allowance say. The allowance is deliberately left full
        // here, so the only thing that can stop the second restart is the floor.
        using var store = new TemporaryStore();
        var log = new RecordingLog();
        var clock = new ManualClock();
        var options = Options;
        var journal = new ReconcileJournal(store.Store, log);
        var counting = new CountingBoundary();
        var floor = new RebootFloor(counting, journal, clock, log, limit: 1, window: TimeSpan.FromHours(6));

        var allowance = new RebootAllowance(store.Store, log, options.AttemptBudget);
        allowance.Refill();

        var first = await AgentLoopFailures.RestartOrStopAsync(
            journal, options, allowance, floor,
            AgentLoopFailures.Record(journal, options, Loop, Purpose, Why, Healthy),
            Healthy, Why, log, TestContext.Current.CancellationToken);

        Assert.True(first.Restarting);
        Assert.Equal(1, floor.Recent());

        var second = await AgentLoopFailures.RestartOrStopAsync(
            journal, options, allowance, floor,
            AgentLoopFailures.Record(journal, options, Loop, Purpose, Why, Cascade),
            Cascade, Why, log, TestContext.Current.CancellationToken);

        Assert.False(second.Restarting);
        Assert.Single(counting.Crossings);
        Assert.True(second.Verdict.Stopped);
        Assert.Contains("stopped rebooting", second.Refusal ?? string.Empty, StringComparison.Ordinal);

        // A person pressing retry grants a fresh window, which decision 79's own remarks promise and
        // which nothing in the agent had ever called. Without it the button would be visibly
        // powerless on exactly the frame it was pressed for.
        floor.Forget();
        Assert.Equal(0, floor.Recent());
    }

    [Fact]
    public async Task A_frame_that_has_never_run_for_the_hold_stops_instead_of_restarting()
    {
        // The cost of "absence means exhausted", stated as a property rather than left to be
        // discovered. A frame whose loop has never once survived the hold has no allowance to spend,
        // so it holds the screen on the first death instead of power-cycling itself three times
        // first. That is the right way round: a loop that dies in two seconds every time will not be
        // fixed by a reboot, and the alternative rule — "no file here means a fresh three" — is
        // precisely the reading the whole backstop exists to make impossible.
        using var store = new TemporaryStore();
        var log = new RecordingLog();
        var boundary = new CountingBoundary();
        var options = Options;

        var boot = await BootAsync(store, options, boundary, log, Cascade);

        Assert.False(boot.Outcome.Restarting);
        Assert.Empty(boundary.Crossings);
        Assert.True(boot.Outcome.Verdict.Stopped);
        Assert.Equal(RebootAllowance.Exhausted(options.AttemptBudget), boot.Outcome.Refusal);
    }

    [Fact]
    public async Task An_isolated_loop_death_years_apart_always_gets_its_restart()
    {
        // The other side of the same rule, and the reason the refill test is the ladder's own. Three
        // unrelated deaths on a frame that has been working the whole time must not add up to a
        // stopped frame — so a process that ran longer than the hold refills the allowance and resets
        // the ladder, and the two agree because they are asking the same question.
        using var store = new TemporaryStore();
        var log = new RecordingLog();
        var boundary = new CountingBoundary();
        var options = Options;

        for (var year = 0; year < 5; year++)
        {
            var boot = await BootAsync(store, options, boundary, log, Healthy);

            Assert.True(boot.Outcome.Restarting);
            Assert.Equal(1, boot.Outcome.Verdict.Attempts);
        }

        Assert.Equal(5, boundary.Crossings.Count);
    }

    [Fact]
    public void The_log_names_the_loop_rather_than_its_ledger_id()
    {
        // Two registers, kept apart. `agent.loop.local-origin` sorts beside resource ids on the
        // screen's technical block and in the Fleet Manager's row, and is the wrong thing to put in
        // a sentence a person reads in the journal.
        Assert.Equal("local-origin", AgentLoopFailures.NameOf(Resource));
        Assert.Equal("local-origin", AgentLoopFailures.NameOf("local-origin"));
        Assert.Equal("audio.playbackVolume", AgentLoopFailures.NameOf("audio.playbackVolume"));
    }

    /// <summary>One process's worth of it: what the ladder said, and what the frame then did.</summary>
    /// <remarks>
    /// Both halves are returned because the interesting assertions are about the gap between them.
    /// The ladder's own verdict is what a corrupt journal makes wrong, and the outcome is what has to
    /// be right anyway.
    /// </remarks>
    private readonly record struct Boot(AgentLoopVerdict Ladder, AgentLoopOutcome Outcome);

    private static async Task<Boot> BootAsync(
        TemporaryStore store,
        ReconcileOptions options,
        IRebootBoundary boundary,
        IAgentLog log,
        TimeSpan ranFor)
    {
        // A fresh journal per boot, because a fresh process is what a frame gets: the cached state in
        // one of these is per-object, and a test that reused it would be asserting against a memory
        // the real agent does not have.
        var journal = new ReconcileJournal(store.Store, log);
        var allowance = new RebootAllowance(store.Store, log, options.AttemptBudget);

        var verdict = AgentLoopFailures.Record(journal, options, Loop, Purpose, Why, ranFor);

        var outcome = await AgentLoopFailures.RestartOrStopAsync(
            journal,
            options,
            allowance,
            boundary,
            verdict,
            ranFor,
            Why,
            log,
            TestContext.Current.CancellationToken);

        return new Boot(verdict, outcome);
    }

    /// <summary>
    /// A boundary that counts and never restarts anything.
    /// </summary>
    /// <remarks>
    /// No test in this file may take a machine down, so nothing here reaches <c>systemctl</c>. It
    /// returns <see cref="RebootCrossing.Restarting"/> rather than <c>Crossed</c> because that is
    /// what a frame's boundary returns, and the caller's branch on it is what is being asserted.
    /// </remarks>
    private sealed class CountingBoundary : IRebootBoundary
    {
        public List<RebootRequest> Crossings { get; } = [];

        /// <summary>When set, every crossing is refused with this sentence.</summary>
        public string? Refuse { get; init; }

        public Task<RebootOutcome> CrossAsync(RebootRequest request, CancellationToken cancellationToken)
        {
            if (Refuse is { Length: > 0 } reason)
            {
                return Task.FromResult(new RebootOutcome(RebootCrossing.Refused, reason));
            }

            Crossings.Add(request);
            return Task.FromResult(new RebootOutcome(RebootCrossing.Restarting));
        }
    }

    /// <summary>A card that answers reads and refuses every write.</summary>
    private sealed class ReadOnlyStore : IStateStore
    {
        public string Root => "/var/lib/fl-agent";

        public void EnsureReady()
        {
        }

        public bool Exists(string name) => string.Equals(name, RebootAllowance.FileName, StringComparison.Ordinal);

        // The allowance file is still there and still says three, written back when the card would
        // still take a write. The journal reads as nothing, which is the ladder-defeating half of
        // this failure: it cannot be written either, so it can never accumulate an attempt.
        public byte[]? ReadBytes(string name) =>
            Exists(name) ? [RebootAllowance.Token, RebootAllowance.Token, RebootAllowance.Token] : null;

        public string? ReadText(string name) => null;

        public void WriteSecret(string name, ReadOnlySpan<byte> content) => throw Refused();

        public void WriteSecretAtomic(string name, ReadOnlySpan<byte> content) => throw Refused();

        public void WriteText(string name, string content) => throw Refused();

        public void Delete(string name) => throw Refused();

        public string PathOf(string name) => $"{Root}/{name}";

        private static IOException Refused() => new("Read-only file system");
    }
}
