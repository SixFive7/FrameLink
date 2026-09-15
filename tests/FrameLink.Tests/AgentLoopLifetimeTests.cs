using FrameLink.Agent;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.State;

namespace FrameLink.Tests;

/// <summary>
/// <b>How long the reconciliation loop lives</b> — version2.md §2.5, decisions 66, 68 and 75.
/// </summary>
/// <remarks>
/// <para>
/// Every other test in this suite drives <see cref="ReconcileLoop.RunPassAsync"/> one pass at a
/// time, which is what makes the ladder assertable — and which is exactly why a defect in
/// <see cref="ReconcileLoop.RunAsync"/> survived a fully green suite. The loop's own lifetime was
/// untested: nothing asked whether there would <i>be</i> a next pass.
/// </para>
/// <para>
/// The defect it did survive is the one this file exists for. <c>Escalated</c> inherited the
/// terminal slot <c>Halted</c> held before decision 66, so the loop returned on the first
/// escalation and never ran again. The agent stayed alive because <c>AgentHost</c> awaits ten loops
/// together and only one of them ended, so the socket stayed up and the Fleet Manager reported a
/// frame that was online and permanently inert — with a retry button that could not possibly work,
/// because nothing was left running to notice a reset budget.
/// </para>
/// <para>
/// <b>Nothing here has run on hardware.</b> These drive the shipping loop against a manual clock.
/// </para>
/// </remarks>
public sealed class AgentLoopLifetimeTests
{
    /// <summary>A ceiling, so a loop that will not settle fails the test rather than hanging it.</summary>
    private const int TickCeiling = 200;

    private static ReconcileOptions Options => new()
    {
        Countdown = TimeSpan.Zero,
        AttemptBudget = 3,
        InitialBackoff = TimeSpan.FromSeconds(30),
        BackoffCap = TimeSpan.FromMinutes(30),
    };

    [Fact]
    public async Task A_loop_that_throws_stops_the_agent_instead_of_waiting_for_the_other_fourteen()
    {
        // The measured stall of 2026-08-16, at the level that hid it. The reconcile loop threw; the
        // other loops ran on for the life of the frame; `Task.WhenAll` had nothing to say until all
        // of them finished, so the frame sat online, connected and inert for twenty-nine minutes
        // with the exception held inside a completed task. The forever-loops below are the whole
        // point: they are what the other fourteen are, and a wait that only reports when it ends is
        // a wait that never reports.
        using var shutdown = new CancellationTokenSource();
        var boom = new InvalidOperationException("the reconcile loop died");

        var running = new List<AgentHost.AgentLoop>
        {
            new("console-stage", "paint the screen", Task.Delay(Timeout.Infinite, shutdown.Token)),
            new("reconcile", "keep every setting as it should be", Task.FromException(boom)),
            new("control-link", "keep the connection", Task.Delay(Timeout.Infinite, shutdown.Token)),
        };

        var ended = await AgentHost.FirstToEndAsync(running, shutdown.Token);

        Assert.NotNull(ended);
        Assert.Equal("reconcile", ended!.Name);
        Assert.Contains("the reconcile loop died", AgentHost.DescribeEnd(ended), StringComparison.Ordinal);

        await shutdown.CancelAsync();
    }

    [Fact]
    public async Task A_loop_that_simply_ends_is_a_failure_too()
    {
        // <b>The change the operator asked for.</b> Reacting to a fault and to nothing else made the
        // watched loops almost as unwatched as the accept loop that was in no list at all: a
        // swallowed cancellation, a `break` on an unexpected state, or a task that completed
        // because its input closed took a whole responsibility off the frame with nothing said
        // anywhere, while every surface went on reporting a healthy agent.
        using var shutdown = new CancellationTokenSource();

        var running = new List<AgentHost.AgentLoop>
        {
            new("local-origin", "serve the app and the repair screen", Task.CompletedTask),
            new("reconcile", "keep every setting as it should be", Task.Delay(Timeout.Infinite, shutdown.Token)),
        };

        var ended = await AgentHost.FirstToEndAsync(running, shutdown.Token);

        Assert.NotNull(ended);
        Assert.Equal("local-origin", ended!.Name);
        Assert.Equal("it returned while the agent was still running", AgentHost.DescribeEnd(ended));

        await shutdown.CancelAsync();
    }

    [Fact]
    public async Task Shutdown_is_the_one_ending_that_is_not_a_failure_and_it_is_told_apart_explicitly()
    {
        // Every loop returns when the agent is stopping, and none of those returns may be reported
        // as a fault. The distinction is the host's own shutdown token — a fact asked about after
        // the first loop ends, rather than a race decided by which of the two happened first.
        using var shutdown = new CancellationTokenSource();

        var running = new List<AgentHost.AgentLoop>
        {
            new("console-stage", "paint the screen", Task.Delay(Timeout.Infinite, shutdown.Token)),
            new("reconcile", "keep every setting as it should be", Task.Delay(Timeout.Infinite, shutdown.Token)),
        };

        await shutdown.CancelAsync();

        Assert.Null(await AgentHost.FirstToEndAsync(running, shutdown.Token));
    }

    [Fact]
    public async Task A_verify_reboot_is_the_loop_standing_down_rather_than_the_loop_dying()
    {
        // <b>Measured on a real frame, 2026-08-30.</b> §2.4's reboot is asked for by this loop, and
        // the loop then returns so the machine can go down — with the agent's shutdown token
        // unsignalled, because nothing is stopping the agent. The host's one test for a legitimate
        // ending therefore said "death", and the frame counted a ladder attempt for doing exactly
        // what §2.4 asks of it.
        using var store = new TemporaryStore();
        using var shutdown = new CancellationTokenSource();
        var boundary = new RestartingBoundary();
        var loop = RealLoop(store.Store, boundary, new RecordingLog(), new ManualClock(), new MutableBootIdentity(),
            new ScriptedResource("panel", "want", "have-not"));

        var task = loop.RunAsync(shutdown.Token);
        await task;

        // The machine really was asked to go down: this is the reboot the loop returned to allow.
        Assert.Single(boundary.Crossings);
        Assert.Equal("panel", boundary.Crossings[0].Resource);

        var running = new List<AgentHost.AgentLoop>
        {
            new("console-stage", "paint the screen", Task.Delay(Timeout.Infinite, shutdown.Token)),
            new("reconcile", "keep every setting on this frame as it should be", task, loop.StandDown),
        };

        var ended = await AgentHost.FirstToEndAsync(running, shutdown.Token);

        // It still <i>ended</i>, and the supervision still sees it end — the fix is not an
        // exemption. What changed is that the loop now says why, in advance and in its own words.
        Assert.NotNull(ended);
        Assert.Equal("reconcile", ended!.Name);
        Assert.Equal("it returned while the agent was still running", AgentHost.DescribeEnd(ended));
        Assert.Equal(ReconcileLoop.RestartingToProveAChange, AgentHost.StoodDown(ended));

        await shutdown.CancelAsync();
    }

    [Fact]
    public async Task Three_verify_reboots_on_one_card_stand_down_three_times_and_count_nothing()
    {
        // The measured cascade itself: on first contact with a frame, `unit.fl-agent.content` found
        // genuine drift, acted, and crossed the reboot — three times. Three verify-reboots became
        // three counted loop deaths and the frame escalated on `agent.loop.reconcile attempts=3
        // escalations=1`, which is a frame stopped for working correctly.
        //
        // One card and one machine across three processes, which is what makes it a cascade: the
        // boot id advances because the machine really rebooted, the resource is the same object
        // because a system value survives a reboot, and each process gets its own loop and its own
        // journal object exactly as a frame does.
        using var store = new TemporaryStore();
        using var shutdown = new CancellationTokenSource();
        var boundary = new RestartingBoundary();
        var boot = new MutableBootIdentity();
        var clock = new ManualClock();

        // Put back after every verify, so each boot finds real drift again and crosses again — the
        // shape the measured frame was in. The conflict threshold is lifted out of the way because
        // decision 78's ladder is a different subject from this one.
        var resource = new ScriptedResource("unit.fl-agent.content", "want", "have-not") { PutBackAfterVerify = true };
        var options = Options with { ConflictThreshold = 10 };

        for (var process = 0; process < 3; process++)
        {
            var loop = RealLoop(store.Store, boundary, new RecordingLog(), clock, boot, options, resource);
            var task = loop.RunAsync(shutdown.Token);
            await task;

            var ended = new AgentHost.AgentLoop("reconcile", "keep every setting as it should be", task, loop.StandDown);

            Assert.Equal(ReconcileLoop.RestartingToProveAChange, AgentHost.StoodDown(ended));
            Assert.Equal(process + 1, boundary.Crossings.Count);

            // The machine goes down and comes back, which is the whole reason the loop returned.
            boot.Advance();
        }

        // <b>Nothing was counted against the loop</b>, on the card, after three of them. The ledger
        // holds what the resource is doing and not one word about the loop that was doing it.
        var journal = new ReconcileJournal(store.Store, new RecordingLog());

        Assert.DoesNotContain(
            journal.Read().Ledger,
            entry => entry.Resource.StartsWith(AgentLoopFailures.LedgerPrefix, StringComparison.Ordinal));

        // And the frame is not stopped, which is the part a household would have seen.
        var after = RealLoop(store.Store, boundary, new RecordingLog(), clock, boot, options, resource);
        Assert.False(after.HasStopped);
    }

    [Fact]
    public async Task A_loop_that_declared_a_stand_down_and_then_threw_is_a_death_anyway()
    {
        // The declaration is a claim about a return. A loop that said it was standing down and then
        // faulted made a claim about a return that never happened, and a throw is a death whatever
        // was said before it — otherwise one sentence spoken early would cover every later failure
        // of the loop that spoke it.
        using var shutdown = new CancellationTokenSource();
        var standDown = new LoopStandDown();
        standDown.Declare(ReconcileLoop.RestartingToProveAChange);

        var faulted = new AgentHost.AgentLoop(
            "reconcile",
            "keep every setting as it should be",
            Task.FromException(new InvalidOperationException("the reconcile loop died")),
            standDown);

        Assert.Null(AgentHost.StoodDown(faulted));

        var cancelled = new AgentHost.AgentLoop(
            "reconcile",
            "keep every setting as it should be",
            Task.FromCanceled(new CancellationToken(canceled: true)),
            standDown);

        Assert.Null(AgentHost.StoodDown(cancelled));

        // And the ordinary shape it is meant to cover still reads as deliberate, so the assertions
        // above are about the task's ending rather than about the declaration being ignored.
        var stood = new AgentHost.AgentLoop("reconcile", "keep every setting as it should be", Task.CompletedTask, standDown);
        Assert.Equal(ReconcileLoop.RestartingToProveAChange, AgentHost.StoodDown(stood));

        await shutdown.CancelAsync();
    }

    [Fact]
    public void A_loop_that_ends_without_declaring_anything_is_a_death_even_with_somewhere_to_declare_it()
    {
        // Carrying a stand-down is not the same as having used one. Fourteen loops carry none at
        // all; the fifteenth carries one and uses it on exactly one path.
        var silent = new AgentHost.AgentLoop("local-origin", "serve the repair screen", Task.CompletedTask, new LoopStandDown());
        Assert.Null(AgentHost.StoodDown(silent));

        var none = new AgentHost.AgentLoop("local-origin", "serve the repair screen", Task.CompletedTask);
        Assert.Null(AgentHost.StoodDown(none));

        // First word wins, so nothing later can overwrite the reason the loop gave at the moment it
        // decided.
        var standDown = new LoopStandDown();
        standDown.Declare("the first reason");
        standDown.Declare("the second reason");
        Assert.Equal("the first reason", standDown.Because);
    }

    [Fact]
    public async Task The_loop_declares_nothing_when_the_agent_is_the_thing_that_stopped()
    {
        // Shutdown already states itself, in the token every loop holds. A second source of truth
        // about one fact is how the two come to disagree — so the reconcile loop says nothing on
        // the way out of an ordinary stop, and the host reads the token exactly as it always did.
        using var store = new TemporaryStore();
        using var shutdown = new CancellationTokenSource();
        var clock = new ManualClock();
        var loop = RealLoop(store.Store, new RestartingBoundary(), new RecordingLog(), clock, new MutableBootIdentity(),
            new ScriptedResource("panel", "want", "want"));

        clock.OnDelay = _ => shutdown.Cancel();

        await loop.RunAsync(shutdown.Token);

        Assert.Null(loop.StandDown.Because);
        Assert.Null(AgentHost.StoodDown(
            new AgentHost.AgentLoop("reconcile", "keep every setting as it should be", Task.CompletedTask, loop.StandDown)));
    }

    [Fact]
    public void The_agent_asks_whether_the_loop_stood_down_before_it_counts_a_loop_death()
    {
        // The wiring, which every test above would pass without. `AgentHost.RunAsync` is four
        // hundred lines of composition against real Linux surfaces and has never been constructible
        // in the suite, so a host that built the reconcile loop's `AgentLoop` without its
        // stand-down — or that asked about it after recording the failure — would leave the measured
        // defect exactly as it was while this file stayed green.
        var host = File.ReadAllText(Path.Combine(
            GuiFreshnessTests.RepositoryRoot(), "src", "FrameLink.Agent", "AgentHost.cs"));

        Assert.Contains("loop.RunAsync(shutdown.Token), loop.StandDown)", host, StringComparison.Ordinal);

        var asked = host.IndexOf("StoodDown(ended)", StringComparison.Ordinal);
        var counted = host.IndexOf("AgentLoopFailures.Record(", StringComparison.Ordinal);

        Assert.True(asked > 0, "AgentHost never asks whether the loop that ended had stood down.");
        Assert.True(counted > 0, "AgentHost no longer records a loop death at all.");
        Assert.True(
            asked < counted,
            "AgentHost records the loop death before it asks whether the ending was deliberate, so "
            + "every verify-reboot is still counted as the reconcile loop dying.");
    }

    [Fact]
    public async Task A_loop_that_ends_walks_the_same_ladder_as_a_resource_and_stops_the_frame_on_the_third()
    {
        // "A loop dying is a failure like any other, so it reaches the same screen with the same
        // information." That is not a new rung and not a second screen: it is a row in the ledger
        // every resource already uses, which is why ReconcileLoop.HasStopped reads it, decision 68
        // stops the pass around it, and a retry clears it.
        using var store = new TemporaryStore();
        var log = new RecordingLog();
        var journal = new ReconcileJournal(store.Store, log);
        var options = new ReconcileOptions { AttemptBudget = 3, ConflictHold = TimeSpan.FromMinutes(5) };

        var first = AgentLoopFailures.Record(
            journal, options, "local-origin", "serve the repair screen", "it returned while the agent was still running", TimeSpan.FromSeconds(10));
        var second = AgentLoopFailures.Record(
            journal, options, "local-origin", "serve the repair screen", "it returned while the agent was still running", TimeSpan.FromSeconds(10));
        var third = AgentLoopFailures.Record(
            journal, options, "local-origin", "serve the repair screen", "it returned while the agent was still running", TimeSpan.FromSeconds(10));

        Assert.False(first.Stopped);
        Assert.False(second.Stopped);
        Assert.True(third.Stopped);
        Assert.Equal(3, third.Attempts);

        // The delta is the same shape §2.5 rung 2 records for everything else: what was expected,
        // and what was found instead.
        Assert.Equal(
            "expected serve the repair screen, observed: it returned while the agent was still running",
            third.Row.Delta);

        // And it is the ledger the whole ladder reads, not a private one.
        var entry = ReconcileJournal.EntryFor(journal.Read(), "agent.loop.local-origin");
        Assert.True(ReconcileLoop.HasGivenUp(entry, options.AttemptBudget));

        // A fresh journal over the same directory sees it, which is what makes it survive the
        // restart the first two attempts cause.
        var reopened = new ReconcileJournal(store.Store, new RecordingLog());
        Assert.Equal(3, ReconcileJournal.EntryFor(reopened.Read(), "agent.loop.local-origin").Attempts);

        await Task.CompletedTask;
    }

    [Fact]
    public void A_process_that_ran_long_enough_forgives_what_came_before_it()
    {
        // Three unrelated loop deaths spread over a year must not stop a frame that has been
        // working the whole time. The question is decision 78's question — did this hold long
        // enough to forgive what came before? — so it is answered with decision 78's number rather
        // than with a new one.
        using var store = new TemporaryStore();
        var journal = new ReconcileJournal(store.Store, new RecordingLog());
        var options = new ReconcileOptions { AttemptBudget = 3, ConflictHold = TimeSpan.FromMinutes(5) };

        AgentLoopFailures.Record(journal, options, "supervision", "keep the product running", "it returned", TimeSpan.FromSeconds(5));
        AgentLoopFailures.Record(journal, options, "supervision", "keep the product running", "it returned", TimeSpan.FromSeconds(5));

        var forgiven = AgentLoopFailures.Record(
            journal, options, "supervision", "keep the product running", "it returned", TimeSpan.FromHours(9));

        Assert.False(forgiven.Stopped);
        Assert.Equal(1, forgiven.Attempts);
    }

    [Fact]
    public void Only_the_two_results_that_mean_this_process_is_going_away_end_the_loop()
    {
        // Decision 75, stated as the property rather than as a list. Restarting is the machine
        // going down to prove a change and Cancelled is the agent shutting down; in both cases
        // there is no next pass to schedule. Everything else has one.
        Assert.True(ReconcileLoop.EndsTheLoop(PassResult.Restarting));
        Assert.True(ReconcileLoop.EndsTheLoop(PassResult.Cancelled));

        // The one that matters. Escalated is §2.5 rung 3 — the rung that exists so an operator can
        // press retry — so a loop that ends on it deletes the recovery path the rung was built for.
        Assert.False(ReconcileLoop.EndsTheLoop(PassResult.Escalated));

        Assert.False(ReconcileLoop.EndsTheLoop(PassResult.Converged));
        Assert.False(ReconcileLoop.EndsTheLoop(PassResult.Pending));
        Assert.False(ReconcileLoop.EndsTheLoop(PassResult.Rebooted));
    }

    [Fact]
    public async Task The_loop_survives_an_escalation_and_picks_up_a_retry_on_a_later_tick()
    {
        // The whole failure, end to end, driven through the loop's own driver rather than through
        // one pass at a time. A retry forces nothing: it clears the budget and returns, so the next
        // tick is the only thing that can act on it. If there is no next tick, there is no retry.
        var resource = new ScriptedResource("broken", "want", "have-not") { ActHasNoEffect = true };
        using var harness = new ReconcileHarness(Options, resource) { Telemetry = { Connected = true } };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var ticks = 0;
        var actsWhenRetried = -1;
        var passesWhenRetried = 0;

        harness.Clock.OnDelay = _ =>
        {
            if (++ticks >= TickCeiling)
            {
                stop.Cancel();
                return;
            }

            if (actsWhenRetried < 0)
            {
                if (!harness.Loop.HasStopped)
                {
                    return;
                }

                // The frame has given up. This is the operator pressing retry — the same call the
                // Fleet Manager's retry and the frame's own button both make.
                actsWhenRetried = resource.Acts;
                passesWhenRetried = harness.Loop.Passes;
                harness.Loop.ResetExhaustedBudgets();
                return;
            }

            if (resource.Acts > actsWhenRetried)
            {
                stop.Cancel();
            }
        };

        await harness.Loop.RunAsync(stop.Token);

        Assert.True(
            actsWhenRetried >= 0,
            "the loop never reached a tick with the frame stopped, so it returned on the escalation "
            + "instead of scheduling another pass");

        Assert.True(
            stop.IsCancellationRequested,
            "RunAsync returned on its own. A pass that ends Escalated must schedule another pass: "
            + "Escalated is the rung an operator retries from, not a terminal state.");

        Assert.True(ticks < TickCeiling, $"the loop ran {ticks} ticks without acting on the retry");

        Assert.True(
            harness.Loop.Passes > passesWhenRetried,
            $"no pass ran after the retry ({harness.Loop.Passes} passes, retry was at {passesWhenRetried})");

        Assert.True(
            resource.Acts > actsWhenRetried,
            $"the retry cleared the budget and nothing ever acted on it ({resource.Acts} acts, "
            + $"{actsWhenRetried} when the retry was pressed)");
    }

    [Fact]
    public async Task A_frame_that_has_given_up_keeps_reporting_rather_than_going_quiet()
    {
        // The half that is invisible from the frame and obvious from the Fleet Manager. A loop that
        // ended on the escalation produced no further telemetry at all — measured, as a journal
        // sequence frozen at the server's last report — so a stopped frame and a dead agent looked
        // identical from the one surface that was still reachable.
        var resource = new ScriptedResource("broken", "want", "have-not") { ActHasNoEffect = true };
        using var harness = new ReconcileHarness(Options, resource) { Telemetry = { Connected = true } };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var ticks = 0;
        var reportsWhenStopped = 0;

        harness.Clock.OnDelay = _ =>
        {
            if (++ticks >= TickCeiling)
            {
                stop.Cancel();
                return;
            }

            if (!harness.Loop.HasStopped)
            {
                return;
            }

            if (reportsWhenStopped == 0)
            {
                reportsWhenStopped = harness.Telemetry.Reports.Count;
            }
            else if (harness.Telemetry.Reports.Count > reportsWhenStopped)
            {
                stop.Cancel();
            }
        };

        await harness.Loop.RunAsync(stop.Token);

        Assert.True(stop.IsCancellationRequested, "RunAsync returned on its own after the escalation");
        Assert.True(
            harness.Telemetry.Reports.Count > reportsWhenStopped,
            "a stopped frame published nothing further, so it is indistinguishable from a dead one");

        // And it is still stopped while it says so — reporting is not the same as reconciling.
        Assert.True(harness.Loop.HasStopped);
        Assert.Equal(
            FrameLink.Protocol.LoopStateNames.Escalated,
            harness.Telemetry.Reports[^1].LoopState);
    }

    /// <summary>
    /// A loop over <paramref name="store"/> whose reboot boundary is the caller's.
    /// </summary>
    /// <remarks>
    /// Not <see cref="ReconcileHarness"/>, and the difference is the whole point: that harness
    /// crosses the boundary <i>inside the process</i> and answers <see cref="RebootCrossing.Crossed"/>,
    /// which is the one answer a frame never gives. §2.4's return-so-the-machine-can-go-down path is
    /// only reachable through a boundary that answers <see cref="RebootCrossing.Restarting"/>.
    /// </remarks>
    private static ReconcileLoop RealLoop(
        IStateStore store,
        IRebootBoundary reboots,
        IAgentLog log,
        ManualClock clock,
        MutableBootIdentity boot,
        params IResource[] resources) =>
        RealLoop(store, reboots, log, clock, boot, Options, resources);

    private static ReconcileLoop RealLoop(
        IStateStore store,
        IRebootBoundary reboots,
        IAgentLog log,
        ManualClock clock,
        MutableBootIdentity boot,
        ReconcileOptions options,
        params IResource[] resources) =>
        new(new ReconcileServices
        {
            Graph = new ResourceGraph(resources),
            Journal = new ReconcileJournal(store, log),
            Boot = boot,
            Reboots = reboots,
            Countdown = new RebootCountdown(clock),
            Telemetry = new RecordingTelemetry(),
            Hub = new AgentStatusHub(AgentStatusFactory.Starting()),
            Clock = clock,
            Log = log,
            Options = options,
        })
        {
            DeviceId = "TEST-DEVI-CEID-0001",
        };

    /// <summary>
    /// The boundary a frame actually has: it takes the request, reports that the machine is going
    /// down, and restarts nothing.
    /// </summary>
    private sealed class RestartingBoundary : IRebootBoundary
    {
        public List<RebootRequest> Crossings { get; } = [];

        public Task<RebootOutcome> CrossAsync(RebootRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            Crossings.Add(request);
            return Task.FromResult(new RebootOutcome(RebootCrossing.Restarting));
        }
    }
}
