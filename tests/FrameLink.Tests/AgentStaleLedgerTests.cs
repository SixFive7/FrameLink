using System.Text.Json;
using FrameLink.Agent;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Tests;

/// <summary>
/// <b>What a build does with a ledger it did not write</b> — the state half of §2.8's downgrade.
/// </summary>
/// <remarks>
/// <para>
/// §2.1 keeps <c>/var/lib/fl-agent</c> through every version change, and §2.5's ladder is only a
/// ladder because of it: an attempt counter that reset at every boot could never exhaust a budget.
/// The same durability is what leaves a frame carrying rows for things the build under it no longer
/// has. Measured 2026-08-30: rolling the agent back replaced the binary and left the ledger, the
/// older build read a stopped row for a resource it had never heard of, and it correctly refused to
/// act on anything. Reverting the container tag is the documented recovery from a bad release — it
/// restored the code and not the state, and the remedy was SSH and hand-edited JSON on a frame in
/// somebody's home.
/// </para>
/// <para>
/// <b>The boundary is the interesting part, and it is not sharp.</b> An unknown id means the ledger
/// outlived the build that wrote it, and nothing more: a resource that was removed and a resource
/// that was renamed look identical from here. The tests below assert the reading that was chosen
/// with that stated — drop, keep a copy, say so loudly — and the reasoning for it lives on
/// <see cref="ReconcileJournal.DropUnknown"/>.
/// </para>
/// <para>
/// <b>Nothing here has run on hardware.</b> These drive the shipping journal and the shipping loop
/// against a real temporary directory.
/// </para>
/// </remarks>
public sealed class AgentStaleLedgerTests
{
    private static ReconcileOptions Options => new()
    {
        Countdown = TimeSpan.Zero,
        AttemptBudget = 3,
    };

    [Fact]
    public void A_row_naming_something_this_build_has_no_catalog_entry_for_is_dropped()
    {
        using var store = new TemporaryStore();
        var log = new RecordingLog();
        var journal = new ReconcileJournal(store.Store, log);

        Seed(journal, "display.panel.overlay", attempts: 3, escalations: 1);
        Seed(journal, "audio.playbackVolume", attempts: 1, escalations: 0);

        var dropped = journal.DropUnknown(Known("audio.playbackVolume"), When);

        Assert.Single(dropped);
        Assert.Equal("display.panel.overlay", dropped[0].Resource);
        Assert.Equal(3, dropped[0].Attempts);

        // On the card, not only in this process: the next boot must not find it again.
        var reopened = new ReconcileJournal(store.Store, new RecordingLog()).Read();

        Assert.Single(reopened.Ledger);
        Assert.Equal("audio.playbackVolume", reopened.Ledger[0].Resource);

        // Loudly, and by name. A drop that only showed up as a frame behaving differently would be
        // the silent discard this whole thing exists to avoid.
        Assert.Contains("display.panel.overlay (attempts 3, escalations 1)", log.Transcript, StringComparison.Ordinal);
        Assert.Contains(ReconcileJournal.DroppedFileName, log.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public void What_was_dropped_is_kept_beside_the_journal_with_the_build_that_dropped_it()
    {
        // Silently discarding state is how the only record of what went wrong is lost. The copy is
        // the difference between a repair and a deletion, and the build stamp is the half that makes
        // it readable later: whoever finds it can go and look at what that build's catalog was.
        using var store = new TemporaryStore();
        var journal = new ReconcileJournal(store.Store, new RecordingLog());

        Seed(journal, "agent.loop.thermal-watch", attempts: 3, escalations: 2, delta: "expected a reading, observed: none");

        journal.DropUnknown(Known("audio.playbackVolume"), When);

        var text = store.Store.ReadText(ReconcileJournal.DroppedFileName);
        Assert.NotNull(text);

        var kept = JsonSerializer.Deserialize(text!, AgentJson.Default.DroppedLedgerRows);

        Assert.NotNull(kept);
        Assert.Equal(When, kept!.DroppedUtc);
        Assert.Equal(AgentBuild.Version, kept.Build);

        var row = Assert.Single(kept.Rows);
        Assert.Equal("agent.loop.thermal-watch", row.Resource);
        Assert.Equal(3, row.Attempts);
        Assert.Equal(2, row.Escalations);
        Assert.Equal("expected a reading, observed: none", row.Delta);
    }

    [Fact]
    public void A_ledger_this_build_understands_is_left_alone_and_nothing_is_written()
    {
        // The common case, which is every boot of every frame in the fleet. A drop that wrote its
        // evidence file on every startup would overwrite the one interesting copy with an empty one,
        // and a log line every boot is a log line nobody reads.
        using var store = new TemporaryStore();
        var log = new RecordingLog();
        var journal = new ReconcileJournal(store.Store, log);

        Seed(journal, "audio.playbackVolume", attempts: 2, escalations: 0);

        Assert.Empty(journal.DropUnknown(Known("audio.playbackVolume", "display.panel.rotation"), When));

        Assert.False(store.Store.Exists(ReconcileJournal.DroppedFileName));
        Assert.DoesNotContain("dropped", log.Transcript, StringComparison.OrdinalIgnoreCase);
        Assert.Single(journal.Read().Ledger);
    }

    [Fact]
    public void A_renamed_resource_and_a_removed_one_are_the_same_row_and_both_are_dropped()
    {
        // <b>The boundary, stated as the property rather than left to be discovered.</b> A build
        // that renamed `display.panel.overlay` to `display.panel.overlays` and a build that deleted
        // it outright leave an identical row: there is no rename map, no version on a row, and the
        // delta reads the same either way. So this cannot be a judgement about which happened, and
        // the safer reading was chosen with that said out loud — the fault is level-triggered and
        // comes back on the next pass if it is still real, while the frame held by a row nothing can
        // clear does not come back at all.
        using var store = new TemporaryStore();
        var journal = new ReconcileJournal(store.Store, new RecordingLog());

        Seed(journal, "display.panel.overlay", attempts: 3, escalations: 1);

        var dropped = journal.DropUnknown(Known("display.panel.overlays"), When);

        Assert.Single(dropped);

        // And the escalation it was carrying survives in the copy, because that is the fact somebody
        // will want when they ask why the frame stopped complaining.
        var kept = JsonSerializer.Deserialize(
            store.Store.ReadText(ReconcileJournal.DroppedFileName)!,
            AgentJson.Default.DroppedLedgerRows);

        Assert.Equal(1, kept!.Rows[0].Escalations);
    }

    [Fact]
    public void A_row_for_a_loop_this_build_still_supervises_is_kept_and_one_for_a_loop_it_lost_is_not()
    {
        // <b>Why the known set is not the catalog.</b> `agent.loop.<name>` rows are written by the
        // host's own supervision against §2.5's same budget of three, and nothing in the DAG has
        // that id — they are rendered by the walk's orphan path on purpose. A prune that read the
        // graph alone would delete the loop ladder's durable half on every boot, which is the
        // journal-wiped-before-every-boot case the restart allowance exists to *survive* rather than
        // to *rely on*, and it would throw away an escalation for a loop that still exists.
        using var store = new TemporaryStore();
        var journal = new ReconcileJournal(store.Store, new RecordingLog());

        Seed(journal, "agent.loop.reconcile", attempts: 3, escalations: 1);
        Seed(journal, "agent.loop.gpio-daemon", attempts: 3, escalations: 1);

        var dropped = journal.DropUnknown(
            AgentHost.LedgerIdsOf(new ResourceGraph([new ScriptedResource("audio.playbackVolume", "want", "want")])),
            When);

        Assert.Single(dropped);
        Assert.Equal("agent.loop.gpio-daemon", dropped[0].Resource);

        var left = journal.Read().Ledger;
        Assert.Single(left);
        Assert.Equal("agent.loop.reconcile", left[0].Resource);
    }

    [Fact]
    public async Task A_frame_stopped_by_a_row_from_another_build_reconciles_again_after_the_drop()
    {
        // The measured failure, end to end, through the shipping loop: a stopped row for a resource
        // this build's catalog does not have stops the whole frame (decision 68), and the walk can
        // neither observe it nor act on it, so nothing the frame does on its own will ever clear it.
        using var harness = new ReconcileHarness(Options, new ScriptedResource("audio.playbackVolume", "want", "have-not"));

        Seed(harness.Journal, "display.panel.overlay", attempts: 3, escalations: 1);

        Assert.True(harness.Loop.HasStopped);

        // The pass still runs and still reports — a stopped frame must not render as a dead one
        // (decision 76) — and it acts on nothing at all, which is the part that will not end.
        var stopped = await harness.PassAsync();
        Assert.Equal(PassResult.Escalated, stopped.Result);
        Assert.Equal(0, ((ScriptedResource)harness.Graph.Ordered[0]).Acts);

        // The repair the host now runs at startup, with the ids this build actually has.
        var dropped = harness.Journal.DropUnknown(AgentHost.LedgerIdsOf(harness.Graph), When);

        Assert.Single(dropped);
        Assert.False(harness.Loop.HasStopped);

        // And it converges, which is the whole claim: the frame is working again without anybody
        // reaching it over SSH.
        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.Equal(ResourceStatusKind.InSync, ReconcileHarness.StatusOf(outcome, "audio.playbackVolume").Kind);
    }

    [Fact]
    public async Task The_rows_bug_one_left_behind_are_cleared_by_a_press_on_a_build_that_still_has_that_loop()
    {
        // <b>The frames that already carry the artefact.</b> Until the fix above, every §2.4
        // verify-reboot was counted as the reconcile loop dying, and a frame that walked the ladder
        // three times is sitting on `agent.loop.reconcile attempts=3 escalations=1` right now. This
        // build still supervises a loop called `reconcile`, so that row is one it can still write
        // and still clear — which is exactly why the drop leaves it alone, and it would be wrong to
        // do otherwise: the same row written by this build is a genuine dead loop.
        //
        // What recovers such a frame is therefore the recovery §2.5 rung 3 already gives it, and the
        // thing worth asserting is that the press reaches a row no resource in the catalog owns.
        using var harness = new ReconcileHarness(Options, new ScriptedResource("audio.playbackVolume", "want", "have-not"));

        Seed(harness.Journal, "agent.loop.reconcile", attempts: 3, escalations: 1);

        Assert.Empty(harness.Journal.DropUnknown(AgentHost.LedgerIdsOf(harness.Graph), When));
        Assert.True(harness.Loop.HasStopped);

        // The button on the repair page, the hold on the panel, and the Fleet Manager's retry all
        // land here.
        var reset = harness.Loop.ResetExhaustedBudgets();

        Assert.Equal(["agent.loop.reconcile"], reset);
        Assert.False(harness.Loop.HasStopped);

        var outcome = await harness.ConvergeAsync();
        Assert.Equal(PassResult.Converged, outcome.Result);

        // The escalation count is deliberately kept by a retry, so the row is still there saying it
        // has happened before — it just no longer stops the frame.
        Assert.Equal(1, ReconcileJournal.EntryFor(harness.Journal.Read(), "agent.loop.reconcile").Escalations);
    }

    [Fact]
    public void A_frame_whose_card_will_not_keep_the_copy_still_gets_the_repair()
    {
        // The copy is a diagnostic and the drop is a repair, and the card that refuses one will
        // refuse the other — so withholding the repair would trade a lost diagnostic for a frame
        // that stays stopped, and would not even keep the row, because the journal write it is
        // protecting fails too.
        var log = new RecordingLog();
        var store = new RefusingStore();
        var journal = new ReconcileJournal(store, log);

        var dropped = journal.DropUnknown(Known("audio.playbackVolume"), When);

        Assert.Single(dropped);
        Assert.Equal("display.panel.overlay", dropped[0].Resource);
        Assert.Empty(journal.Read().Ledger);
        Assert.Contains("could not be kept", log.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ids_this_build_knows_are_its_whole_catalog_and_its_own_loops_and_nothing_else()
    {
        var graph = new ResourceGraph([
            new ScriptedResource("audio.playbackVolume", "want", "want"),
            new ScriptedResource("display.panel.rotation", "want", "want"),
        ]);

        var known = AgentHost.LedgerIdsOf(graph);

        Assert.Contains("audio.playbackVolume", known);
        Assert.Contains("display.panel.rotation", known);
        Assert.Contains("agent.loop.reconcile", known);
        Assert.Contains("agent.loop.local-origin", known);
        Assert.DoesNotContain("agent.loop.gpio-daemon", known);
        Assert.Equal(graph.Count + AgentHost.SupervisedLoops.Count, known.Count);
    }

    [Fact]
    public void The_supervised_loop_list_is_the_list_of_loops_the_agent_actually_starts()
    {
        // The second copy of a list is a liability, and this is what makes it safe. `AgentHost.RunAsync`
        // is four hundred lines of composition against real Linux surfaces and has never been
        // constructible in the suite, so a loop added to it and not to `SupervisedLoops` would leave
        // its ledger row dropped on every boot — the durable ladder gone for exactly one loop, in
        // silence.
        var host = File.ReadAllText(Path.Combine(
            GuiFreshnessTests.RepositoryRoot(), "src", "FrameLink.Agent", "AgentHost.cs"));

        var started = host
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("new(\"", StringComparison.Ordinal))
            .Select(line => line["new(\"".Length..].Split('"')[0])
            .ToList();

        Assert.Equal(AgentHost.SupervisedLoops, started);
    }

    [Fact]
    public void The_agent_drops_what_it_cannot_act_on_before_a_single_loop_has_started()
    {
        // The ordering is the whole of it: the first pass reads the ledger to decide whether this
        // frame does anything at all, and every entry in the list below starts its loop as it is
        // constructed. A repair that lands after that list is a repair that arrives a pass late,
        // every pass.
        var host = File.ReadAllText(Path.Combine(
            GuiFreshnessTests.RepositoryRoot(), "src", "FrameLink.Agent", "AgentHost.cs"));

        var repaired = host.IndexOf("journal.DropUnknown(LedgerIdsOf(catalog), _clock.UtcNow);", StringComparison.Ordinal);
        var started = host.IndexOf("var running = new List<AgentLoop>(15)", StringComparison.Ordinal);

        Assert.True(repaired > 0, "AgentHost never drops the ledger rows this build cannot act on.");
        Assert.True(started > 0, "AgentHost no longer builds its list of loops the way this test reads it.");
        Assert.True(
            repaired < started,
            "AgentHost starts its loops before it drops the ledger rows it cannot act on, so the "
            + "first reconcile pass can still read a row that is about to be removed.");
    }

    /// <summary>A fixed instant, so the kept copy is assertable.</summary>
    private static readonly DateTimeOffset When = new(2026, 8, 30, 21, 14, 0, TimeSpan.Zero);

    private static HashSet<string> Known(params string[] ids) => new(ids, StringComparer.Ordinal);

    private static void Seed(
        ReconcileJournal journal,
        string resource,
        int attempts,
        int escalations,
        string delta = "expected the value, observed: something else")
    {
        journal.Update(state => ReconcileJournal.WithEntry(
            state,
            ReconcileJournal.EntryFor(state, resource) with
            {
                Attempts = attempts,
                Escalations = escalations,
                Delta = delta,
                Change = "set it",
            }));
    }

    /// <summary>
    /// A card holding one stale row that answers reads and refuses every write — the state a full or
    /// read-only card leaves a frame in.
    /// </summary>
    private sealed class RefusingStore : IStateStore
    {
        private const string Stale =
            """
            {"ledger":[{"resource":"display.panel.overlay","attempts":3,"escalations":1}]}
            """;

        public string Root => "/var/lib/fl-agent";

        public void EnsureReady()
        {
        }

        public bool Exists(string name) => string.Equals(name, ReconcileJournal.FileName, StringComparison.Ordinal);

        public byte[]? ReadBytes(string name) => null;

        public string? ReadText(string name) => Exists(name) ? Stale : null;

        public void WriteSecretAtomic(string name, ReadOnlySpan<byte> content) => throw Refused();

        public void WriteText(string name, string content) => throw Refused();

        public void Delete(string name) => throw Refused();

        public bool TryRename(string name, string newName) => throw Refused();

        public string PathOf(string name) => $"{Root}/{name}";

        private static IOException Refused() => new("Read-only file system");
    }
}
