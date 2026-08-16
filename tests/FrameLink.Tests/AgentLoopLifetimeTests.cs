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
