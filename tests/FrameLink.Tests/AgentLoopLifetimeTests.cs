using FrameLink.Agent;
using FrameLink.Agent.Reconcile;

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
}
