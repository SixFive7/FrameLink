using FrameLink.Agent;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Link;
using FrameLink.Agent.Local;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Stage;
using FrameLink.Agent.State;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// <b>The operator's specification, as a suite</b>: "If something fails the system reboots and
/// tries again. Fully automatic. Max amount of tries = 3. After attempt 3 the system does NOT
/// reboot automatically anymore. Instead it shows it has tried operation X 3 times together with
/// all the support information for operation x. Then there are 2 buttons the user can manually
/// press. Shutdown -> stops everything. Or reboot -> forces a new retry. The last one (the reboot)
/// can also be triggered from the fleet manager given the agent is connected."
/// </summary>
/// <remarks>
/// <para>
/// Every sentence of that is a test here, in order: the three automatic tries and their three
/// reboots, the count surviving the reboots it causes, the screen after the third, the two buttons,
/// and what each of them does. The Fleet Manager's half is in <c>ControlRetryTests</c>, because
/// that half is a route and a socket rather than a screen.
/// </para>
/// <para>
/// <b>Nothing here has run on hardware.</b> The reboot boundary crosses in-process and the init
/// system is a recording double; what is asserted is the decision the agent takes, not the frame
/// coming back up.
/// </para>
/// </remarks>
public sealed class AgentRepairLadderTests
{
    private static ReconcileOptions Options => new()
    {
        Countdown = TimeSpan.Zero,
        AttemptBudget = 3,
        InitialBackoff = TimeSpan.FromSeconds(30),
        BackoffCap = TimeSpan.FromMinutes(30),
    };

    /// <summary>A green answer from the Fleet Manager — adopted, on the served version.</summary>
    private static DeviceCondition Green => DeviceStateLadder.FromHandshake(new HandshakeResult
    {
        Status = HandshakeStatus.Ok,
        ProtocolVersion = ProtocolConstants.Version,
        ServedAgentVersion = AgentBuild.Version,
    });

    private static ScriptedResource Broken() =>
        new("audio.mixer.pcm-volume", "20", "0") { ActHasNoEffect = true };

    // -----------------------------------------------------------------------------------------
    // "If something fails the system reboots and tries again. Max amount of tries = 3."
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Three_tries_take_three_reboots_and_the_fourth_never_happens()
    {
        var resource = Broken();
        using var harness = new ReconcileHarness(Options, resource) { Telemetry = { Connected = true } };

        // Attempt 1: act, reboot to check, find it still wrong.
        var first = await harness.PassAsync();
        Assert.Equal(1, resource.Acts);
        Assert.Single(harness.Boundary.Crossings);
        Assert.False(harness.Loop.HasStopped);
        Assert.NotNull(first.NextAttemptUtc);

        // Attempt 2, after the wait the loop itself chose.
        harness.Clock.UtcNow = first.NextAttemptUtc!.Value;
        var second = await harness.PassAsync();
        Assert.Equal(2, resource.Acts);
        Assert.Equal(2, harness.Boundary.Crossings.Count);
        Assert.False(harness.Loop.HasStopped);

        // Attempt 3 — and this is the last one that touches the machine.
        harness.Clock.UtcNow = second.NextAttemptUtc!.Value;
        var third = await harness.PassAsync();
        Assert.Equal(3, resource.Acts);
        Assert.Equal(3, harness.Boundary.Crossings.Count);
        Assert.Equal(PassResult.Escalated, third.Result);
        Assert.True(harness.Loop.HasStopped);

        // "After attempt 3 the system does NOT reboot automatically anymore." Ten more passes, an
        // hour of clock, and nothing is acted on and nothing is restarted.
        for (var pass = 0; pass < 10; pass++)
        {
            harness.Clock.UtcNow += TimeSpan.FromMinutes(6);
            var later = await harness.PassAsync();
            Assert.Equal(PassResult.Escalated, later.Result);
        }

        Assert.Equal(3, resource.Acts);
        Assert.Equal(3, harness.Boundary.Crossings.Count);
    }

    [Fact]
    public async Task Nothing_on_a_stopped_frame_is_waiting_for_a_timer()
    {
        // The operator: "No timers on that screen. It sits until somebody acts, locally or from the
        // Fleet Manager." Three things would break that, and all three are asserted: a scheduled
        // next attempt, a countdown, and a backoff the screen would draw as a wait.
        var resource = Broken();
        using var harness = new ReconcileHarness(Options, resource) { Telemetry = { Connected = true } };

        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Escalated, outcome.Result);
        Assert.Null(outcome.NextAttemptUtc);
        Assert.Null(ReconcileHarness.StatusOf(outcome, resource.Name).NextAttemptUtc);

        var status = harness.Hub.Current;
        Assert.Null(status.Reconcile.Countdown);
        Assert.Null(status.Reconcile.BackoffEndsAt);
        Assert.Equal(TimeSpan.Zero, status.Reconcile.BackoffTotal);

        // And the message the page is sent carries no countdown either, so nothing there animates.
        var frame = BrowserStage.Compose(status, harness.Clock.UtcNow);
        Assert.Null(frame.CountdownSeconds);
        Assert.Null(frame.ProgressLine);
    }

    // -----------------------------------------------------------------------------------------
    // "Max amount of tries = 3" has to mean three across the reboots, not three per boot
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_attempt_count_survives_the_very_reboots_the_retry_causes()
    {
        // <b>The property the whole specification rests on.</b> Every attempt reboots, so if the
        // count did not reach the card and come back, "three attempts" would mean nothing at all —
        // the frame would try once, restart, forget, and repeat for ever. Every other test in this
        // suite crosses the boundary inside one process, where the journal's in-memory cache would
        // hide exactly that.
        //
        // So this is three separate loops, three separate journals, three separate boot ids, over
        // one state directory: a frame that really went down between attempts.
        var options = Options;

        using var first = new ReconcileHarness(options, Broken()) { Telemetry = { Connected = true } };
        var one = await first.PassAsync();
        Assert.Single(first.Boundary.Crossings);
        Assert.Equal(1, Ledger(first).Attempts);

        using var second = ReconcileHarness.Rebooted(first, options, Broken());
        second.Telemetry.Connected = true;
        second.Clock.UtcNow = one.NextAttemptUtc!.Value;
        var two = await second.PassAsync();
        Assert.Equal(2, Ledger(second).Attempts);
        Assert.False(second.Loop.HasStopped);

        using var third = ReconcileHarness.Rebooted(second, options, Broken());
        third.Telemetry.Connected = true;
        third.Clock.UtcNow = two.NextAttemptUtc!.Value;
        var three = await third.PassAsync();

        // Three attempts in total, not three per boot: the third process is the one that gives up.
        Assert.Equal(3, Ledger(third).Attempts);
        Assert.Equal(PassResult.Escalated, three.Result);
        Assert.True(third.Loop.HasStopped);

        // And a fourth process, reading nothing but the file, comes back already stopped — which is
        // what stops a frame acting on every boot for ever.
        using var fourth = ReconcileHarness.Rebooted(third, options, Broken());
        Assert.True(fourth.Loop.HasStopped);
        var later = await fourth.PassAsync();
        Assert.Equal(PassResult.Escalated, later.Result);
        Assert.Empty(fourth.Boundary.Crossings);

        static ResourceLedgerEntry Ledger(ReconcileHarness harness) =>
            ReconcileJournal.EntryFor(harness.Journal.Read(), "audio.mixer.pcm-volume");
    }

    // -----------------------------------------------------------------------------------------
    // "it shows it has tried operation X 3 times together with all the support information"
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_stopped_screen_carries_both_halves_for_the_resource_that_stopped()
    {
        var resource = Broken();
        using var harness = new ReconcileHarness(Options, resource) { Telemetry = { Connected = true } };

        await harness.ConvergeAsync();

        var status = harness.Hub.Current with
        {
            HardwareSerial = "10000000abcdef12",
            Contact = new OperatorContact
            {
                Name = "Jori",
                Contact = "06 12 34 56 78",
                UpdatedUtc = harness.Clock.UtcNow,
            },
        };

        // The plain half: how many times, and what was tried, in words a household can use. What
        // was detected and why it matters are on the screen too — from the live narration, which
        // this process still holds because it never actually went down — and SupportPlain
        // deliberately does not repeat them, which is the whole of the suppression below.
        var plain = ReconcileVoice.SupportPlain(status);
        Assert.Contains(
            "It tried 3 times, restarting the frame each time to check, and it will not try again on its own.",
            plain);
        Assert.Contains(plain, said => said.StartsWith("What it tried: ", StringComparison.Ordinal));
        Assert.DoesNotContain("The audio.mixer.pcm-volume value is wrong.", plain);

        // The technical half: the block somebody photographs. Every value rendered, never
        // re-derived — the delta is the string the ladder recorded.
        var technical = ReconcileVoice.SupportTechnical(status);
        Assert.Contains("resource: audio.mixer.pcm-volume", technical);
        Assert.Contains("tried: 3 of 3", technical);
        Assert.Contains("delta: expected '20', observed '0'", technical);
        Assert.Contains("reported: yes, the Fleet Manager has it", technical);
        Assert.Contains("device: TEST-DEVI-CEID-0001", technical);
        Assert.Contains("serial: 10000000abcdef12", technical);
        Assert.Contains(technical, said => said.StartsWith("last change: ", StringComparison.Ordinal));

        // Both halves reach both surfaces, from the one composition, so the console and the page
        // cannot say different things about the same frame.
        var frame = StageRenderer.Render(status, harness.Clock.UtcNow, tick: 0, 160, 60, colour: false);
        Assert.Contains("resource: audio.mixer.pcm-volume", frame, StringComparison.Ordinal);
        Assert.Contains(ReconcileVoice.TechnicalHeading, frame, StringComparison.Ordinal);
        Assert.Contains("Ask Jori — 06 12 34 56 78.", frame, StringComparison.Ordinal);

        var page = BrowserStage.Compose(status, harness.Clock.UtcNow);
        Assert.Equal(plain, page.SupportPlain);
        Assert.Equal(technical, page.SupportTechnical);
        Assert.Equal(ReconcileVoice.TechnicalHeading, page.TechnicalHeading);

        // And the sentences appear once each rather than twice, which is the defect measured on
        // 2026-08-16 with the roles reversed: the same claim at 30 px and again at 20 px under it.
        Assert.Equal(1, Occurrences(frame, "The audio.mixer.pcm-volume value is wrong."));

        static int Occurrences(string text, string value)
        {
            var found = 0;
            var at = text.IndexOf(value, StringComparison.Ordinal);

            while (at >= 0)
            {
                found++;
                at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal);
            }

            return found;
        }
    }

    [Fact]
    public async Task The_plain_half_survives_the_reboot_that_produced_the_failure()
    {
        // <b>The gap this closes, and it is the ordinary case rather than an edge.</b> Attempt 3
        // writes its change and the machine goes down; the process that comes back is the one that
        // verifies, fails and gives up — and it has published no narration at all, because nothing
        // in it ever acted. So the two sentences §2.7 wrote for the person in the room were missing
        // from precisely the screen they were written for, while the delta and the attempt count
        // were there, because those are durable and the sentences were not.
        //
        // They are on the row now, put there by the pass that gives up and by every later pass, so
        // a frame that has been switched off and on again still says what it was trying to do.
        var options = Options;

        using var before = new ReconcileHarness(options, Broken()) { Telemetry = { Connected = true } };
        await before.ConvergeAsync();
        Assert.True(before.Loop.HasStopped);

        using var after = ReconcileHarness.Rebooted(before, options, Broken());
        after.Telemetry.Connected = true;
        await after.PassAsync();

        var status = after.Hub.Current;

        Assert.Empty(status.Narration.Detected ?? string.Empty);

        var plain = ReconcileVoice.SupportPlain(status);
        Assert.Contains("The audio.mixer.pcm-volume value is wrong.", plain);
        Assert.Contains("Because this is a test.", plain);
        Assert.Contains(
            "It tried 3 times, restarting the frame each time to check, and it will not try again on its own.",
            plain);

        // The gloss is durable too, and for the same reason: it was written by a process that no
        // longer exists.
        Assert.Contains(plain, said => said.StartsWith("What it tried: ", StringComparison.Ordinal));

        // And the frame still says it on its own screen after the restart.
        var frame = StageRenderer.Render(status, after.Clock.UtcNow, tick: 0, 160, 60, colour: false);
        Assert.Contains("The audio.mixer.pcm-volume value is wrong.", frame, StringComparison.Ordinal);
        Assert.Contains("delta: expected '20', observed '0'", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void A_frame_that_is_working_is_sent_neither_half_and_neither_button()
    {
        // The screen belongs to the agent only when something is not green. A converged frame that
        // was sent a support block would be a frame whose page could render a failure it does not
        // have.
        var working = new AgentStatus { Condition = Green };

        Assert.Empty(ReconcileVoice.SupportPlain(working));
        Assert.Empty(ReconcileVoice.SupportTechnical(working));

        var page = BrowserStage.Compose(working, DateTimeOffset.UnixEpoch);
        Assert.Null(page.SupportPlain);
        Assert.Null(page.SupportTechnical);
        Assert.False(page.CanRetry);
        Assert.False(page.CanShutdown);
        Assert.Null(page.RestartLabel);
        Assert.Null(page.ShutdownLabel);
    }

    [Fact]
    public async Task A_gate_does_not_claim_it_tried_three_times()
    {
        // <b>Gates do not reboot, and must not say they did.</b> A gate reaches rung 2 with the
        // budget *declared* spent — no Act, no reboot — so a screen rendering the count as tries
        // told a household the frame had restarted three times over a microphone unit it had read
        // exactly once. What the person needs to know is the opposite: that waiting is no use,
        // because the frame cannot fix this at all.
        var gate = new GateResource("firmware.xvf3800.recognised");
        using var harness = new ReconcileHarness(Options, gate) { Telemetry = { Connected = true } };

        var outcome = await harness.PassAsync();

        Assert.Equal(0, gate.Acts);
        Assert.Empty(harness.Boundary.Crossings);

        var row = ReconcileHarness.StatusOf(outcome, gate.Name);
        Assert.False(row.Attempted);

        var status = harness.Hub.Current;
        Assert.Contains(
            "The frame did not try to put this right, because there is nothing it could do about it. "
            + "Somebody has to look at it.",
            ReconcileVoice.SupportPlain(status));

        Assert.DoesNotContain("failed after 3 tries", ReconcileVoice.StoppedLine(status));
        Assert.Contains("cannot be put right by this frame", ReconcileVoice.StoppedLine(status));
        Assert.Contains(
            ReconcileVoice.SupportTechnical(status),
            said => said.StartsWith("tried: nothing was attempted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_frame_something_else_is_fighting_still_says_what_it_tried()
    {
        // §2.6's conflict drift is the one give-up whose change *worked* — applied, verified across
        // a reboot, and put back afterwards by a second owner. It is worth its own test because the
        // plain half has to be right about a frame whose repair is not failing at all: what the
        // person needs is not "the value is wrong" but "something else keeps changing it back",
        // which is the difference between hunting a setting that will not apply and finding the
        // other owner.
        //
        // There is deliberately no "what it tried" line here. The change succeeded, so the ledger's
        // own success write (Held) rebuilt the entry and dropped it — and inventing one would mean
        // keeping a record of a command specifically in the case where it worked.
        var resource = new ScriptedResource("audio.mixer.pcm-volume", "20", "0") { PutBackAfterVerify = true };
        using var harness = new ReconcileHarness(Options, resource) { Telemetry = { Connected = true } };

        var outcome = await harness.ConvergeAsync(limit: 30);

        Assert.Equal(PassResult.Escalated, outcome.Result);
        Assert.True(harness.Loop.HasStopped);

        // Durable, so the screen still has it after the frame has been switched off and on.
        using var after = ReconcileHarness.Rebooted(harness, Options, resource);
        after.Telemetry.Connected = true;
        await after.PassAsync();

        var plain = ReconcileVoice.SupportPlain(after.Hub.Current);
        Assert.Contains("The audio.mixer.pcm-volume value is wrong.", plain);
        Assert.Contains(
            "It tried 3 times, restarting the frame each time to check, and it will not try again on its own.",
            plain);

        // And the delta says what a person needs in order to look in the right place: not "the
        // value is wrong" but "something else keeps changing it back".
        Assert.Contains(
            ReconcileVoice.SupportTechnical(after.Hub.Current),
            said => said.Contains("put back every time", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------------------------
    // "Then there are 2 buttons the user can manually press"
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Both_buttons_are_offered_exactly_when_the_frame_has_stopped()
    {
        var stopped = new AgentStatus
        {
            Condition = Green,
            Drifted = true,
            Resources =
            [
                new ResourceStatus
                {
                    Name = "audio.mixer.pcm-volume",
                    Kind = ResourceStatusKind.Escalated,
                    Delta = "expected '20', observed '0'",
                    Attempts = 3,
                    AttemptBudget = 3,
                    Escalations = 1,
                },
            ],
        };

        var page = BrowserStage.Compose(stopped, DateTimeOffset.UnixEpoch);

        Assert.True(page.CanRetry);
        Assert.True(page.CanShutdown);
        Assert.Equal("Restart and try again", page.RestartLabel);
        Assert.Equal("Shut down", page.ShutdownLabel);
    }

    [Fact]
    public void The_page_asking_to_restart_or_to_shut_down_reaches_the_agent_as_two_different_things()
    {
        var channel = new LocalChannel();
        var restarts = 0;
        var shutdowns = 0;
        var retries = 0;

        channel.RestartRequested += () => restarts++;
        channel.ShutdownRequested += () => shutdowns++;
        channel.RetryRequested += () => retries++;

        channel.Receive(new PageMessage { Kind = PageMessage.KindRestart }, DateTimeOffset.UnixEpoch);
        channel.Receive(new PageMessage { Kind = PageMessage.KindShutdown }, DateTimeOffset.UnixEpoch);

        Assert.Equal(1, restarts);
        Assert.Equal(1, shutdowns);

        // The old kind still means what it always meant, because a page can outlive the agent that
        // served it and a stale one pressing the old button must still clear the budget.
        channel.Receive(new PageMessage { Kind = PageMessage.KindRetry }, DateTimeOffset.UnixEpoch);
        Assert.Equal(1, retries);
        Assert.Equal(1, restarts);
    }

    [Fact]
    public async Task Restart_resets_the_budget_before_it_reboots_and_shutdown_does_neither()
    {
        // "Shutdown -> stops everything. Or reboot -> forces a new retry." The ordering inside the
        // restart is the design: the reset is journalled first, so a frame that goes down between
        // the two comes back with a fresh budget rather than spending a reboot to learn nothing.
        var systemControl = new RecordingSystemControl();
        var resets = 0;

        var recovery = new FrameRecovery(new FrameRecoveryServices
        {
            ResetBudgets = () =>
            {
                resets++;
                Assert.Empty(systemControl.Commands);
                return ["audio.mixer.pcm-volume"];
            },
            SystemControl = systemControl,
            Log = new RecordingLog(),
        });

        Assert.True(await recovery.RestartAsync("Somebody at the frame", TestContext.Current.CancellationToken));
        Assert.Equal(1, resets);
        Assert.Equal(["reboot"], systemControl.Commands);

        Assert.True(await recovery.ShutdownAsync("Somebody at the frame", TestContext.Current.CancellationToken));

        // A frame that is switched off has not been told to try again: clearing the ledger here
        // would mean a household that decided to stop found it mid-provision when it came back.
        Assert.Equal(1, resets);
        Assert.Equal(["reboot", "poweroff"], systemControl.Commands);
    }

    [Fact]
    public async Task Neither_button_may_interrupt_a_firmware_write()
    {
        // Decision 91, from the other side. A reboot in the middle of a write leaves the microphone
        // unit unbootable; a power-off leaves it that way with no process left to finish.
        var systemControl = new RecordingSystemControl();
        var recovery = new FrameRecovery(new FrameRecoveryServices
        {
            ResetBudgets = () => throw new InvalidOperationException("nothing may be reset while a write is running"),
            SystemControl = systemControl,
            Log = new RecordingLog(),
            Held = () => "a firmware write is running on the microphone unit",
        });

        Assert.False(await recovery.RestartAsync("Somebody at the frame", TestContext.Current.CancellationToken));
        Assert.False(await recovery.ShutdownAsync("The Fleet Manager", TestContext.Current.CancellationToken));

        Assert.Empty(systemControl.Commands);

        // A refusal that only says no explains nothing a person can act on, and the person it has
        // to satisfy may be about to reach for the plug — which is the exact hazard the refusal is
        // protecting the microphone unit from. So it says why, that nothing is queued, what to wait
        // for, and not to unplug it.
        Assert.Equal(
            "Not now — a firmware write is running on the microphone unit. Nothing has been queued and "
            + "nothing is waiting its turn: wait until this frame's own screen says the microphone update "
            + "has finished, then ask again. Do not unplug it in the meantime.",
            recovery.LastRefusal);

        Assert.Equal(recovery.LastRefusal, FrameRecovery.RefusalLine("a firmware write is running on the microphone unit"));
    }

    [Fact]
    public async Task The_refusal_names_the_write_the_frame_is_actually_doing()
    {
        // The clause comes from ArrayFlashWindow.Reason, which names the image being written, so the
        // sentence a person reads is about this frame's own write rather than a generic one. Both
        // verbs are refused in the same words, because they are refused by the same predicate for
        // the same reason.
        var systemControl = new RecordingSystemControl();
        var log = new RecordingLog();

        const string Held = "the microphone unit's firmware is being written right now, and interrupting that is "
            + "the one thing on this frame that cannot be undone (writing 2.1.0)";

        var recovery = new FrameRecovery(new FrameRecoveryServices
        {
            ResetBudgets = () => [],
            SystemControl = systemControl,
            Log = log,
            Held = () => Held,
        });

        Assert.False(await recovery.ShutdownAsync("The Fleet Manager", TestContext.Current.CancellationToken));

        Assert.Contains("writing 2.1.0", recovery.LastRefusal!, StringComparison.Ordinal);
        Assert.Contains("Do not unplug it in the meantime.", recovery.LastRefusal!, StringComparison.Ordinal);
        Assert.Contains(log.Lines, line => line.Contains("Nothing has been queued", StringComparison.Ordinal));

        // And it clears the moment the window shuts, so a stale sentence cannot outlive the write.
        var free = new FrameRecovery(new FrameRecoveryServices
        {
            ResetBudgets = () => [],
            SystemControl = systemControl,
            Log = log,
            Held = () => null,
        });

        Assert.True(await free.ShutdownAsync("The Fleet Manager", TestContext.Current.CancellationToken));
        Assert.Null(free.LastRefusal);
    }

    // -----------------------------------------------------------------------------------------
    // "It reaches the frame's own journal and nowhere else." Decision 94's gap: the HTTP call the
    // operator made has already answered 200 — the bytes left down a live socket, and the socket
    // closing is what a delivered shutdown looks like — so a refusal cannot come back down it. Two
    // surfaces answer two different questions instead, and one FrameRefusal feeds both.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_refused_press_reaches_the_event_trail_and_the_device_row_in_the_same_words()
    {
        // The event is the record of what happened and when; the self-report is the answer to "why
        // is this frame not doing what I asked, right now". Both are projections of the one
        // FrameRefusal composed at the press, which is what makes it impossible for the trail and
        // the row to say different things about the same refusal.
        const string Held = "the microphone unit's firmware is being written right now, and interrupting that is "
            + "the one thing on this frame that cannot be undone (writing 2.1.0)";

        var reported = new List<FrameRefusal>();

        var recovery = new FrameRecovery(new FrameRecoveryServices
        {
            ResetBudgets = () => [],
            SystemControl = new RecordingSystemControl(),
            Log = new RecordingLog(),
            Held = () => Held,
            OnRefused = (refusal, _) =>
            {
                reported.Add(refusal);
                return ValueTask.CompletedTask;
            },
        });

        Assert.False(await recovery.ShutdownAsync("The Fleet Manager", TestContext.Current.CancellationToken));

        var refusal = Assert.Single(reported);
        Assert.Equal(PowerVerbs.Shutdown, refusal.Verb);
        Assert.Equal(FrameRecovery.RefusalLine(Held), refusal.Line);
        Assert.Same(refusal, recovery.Refusal);

        // The trail's half. Who asked and which button, then the frame's own sentence in full: six
        // months later "did anybody press it, and which one" is the first question anybody asks
        // about a frame that stayed on, and the refusal alone cannot answer it.
        var moment = refusal.ToEvent("TEST-DEVI-CEID-0001", DateTimeOffset.UnixEpoch);

        Assert.Equal(DeviceEventKinds.PowerRefused, moment.Kind);
        Assert.Equal("TEST-DEVI-CEID-0001", moment.DeviceId);
        Assert.Null(moment.Resource);
        Assert.Equal(0, moment.Attempts);
        Assert.StartsWith(
            "The Fleet Manager asked this frame to shut down and it was refused. ",
            moment.Summary,
            StringComparison.Ordinal);
        Assert.Equal($"expected 'shutdown', observed 'refused: {FrameRefusal.HeldBy}'", moment.Delta);

        // The row's half, carried inside the self-report the Fleet Manager already stores. The
        // load-bearing clause survives the trip verbatim.
        var report = PowerRefusalWire.Append(
            AgentHealth.ReportFor(LoopStateNames.Converged, "linux-arm64"),
            refusal.Wire);

        var read = PowerRefusalWire.Read(report);

        Assert.NotNull(read);
        Assert.Equal(PowerVerbs.Shutdown, read.Verb);
        Assert.Equal(refusal.Line, read.Why);
        Assert.Contains("Nothing has been queued and nothing is waiting its turn", read.Why, StringComparison.Ordinal);
        Assert.Contains("Do not unplug it in the meantime.", read.Why, StringComparison.Ordinal);
        Assert.Contains("writing 2.1.0", read.Why, StringComparison.Ordinal);

        // And the trail and the row carry the same sentence, not two renderings of one idea.
        Assert.EndsWith(read.Why, moment.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_row_stops_saying_a_frame_is_refusing_the_moment_the_write_finishes()
    {
        // A refusal is a moment; the part of it that is a *state* is true for exactly as long as
        // the firmware write it names. The sentence tells the reader to wait until the frame's own
        // screen says the update has finished and then ask again — so a row still reporting a
        // refusal after that would be telling somebody to wait for something that already happened.
        var window = "the microphone unit's firmware is being written right now (writing 2.1.0)";

        using var uplink = new AgentUplink();
        var hub = new AgentStatusHub(AgentStatusFactory.Green());

        var recovery = new FrameRecovery(new FrameRecoveryServices
        {
            ResetBudgets = () => [],
            SystemControl = new RecordingSystemControl(),
            Log = new RecordingLog(),
            Held = () => window,
        });

        using var reporter = new AgentStatusReporter(
            hub,
            uplink,
            NullLog.Instance,
            "AAAA-BBBB-CCCC-DDDD",
            "linux-arm64",
            () => recovery.Refusal?.Wire);

        Assert.Null(PowerRefusalWire.Read(reporter.Current));

        Assert.False(await recovery.RestartAsync("Somebody at the frame", TestContext.Current.CancellationToken));

        var refusing = PowerRefusalWire.Read(reporter.Current);
        Assert.NotNull(refusing);
        Assert.Equal(PowerVerbs.Restart, refusing.Verb);

        // The write finishes: ArrayFlashWindow.Reason answers null, and nothing else happens.
        // Nothing is notified, nothing is cleared by hand, and no second press is needed.
        window = null!;

        Assert.Null(recovery.Refusal);
        Assert.Null(PowerRefusalWire.Read(reporter.Current));

        // What is left is exactly the self-report the frame would have sent had nobody pressed
        // anything: the refusal moved the vocabulary neither on the way in nor on the way out.
        Assert.Equal(
            AgentHealth.ReportFor(hub.Current.Reconcile.LoopState, "linux-arm64"),
            reporter.Current);
    }

    [Fact]
    public async Task A_press_that_goes_through_reports_nothing_and_leaves_no_refusal_behind()
    {
        // Two failures in one: an event filed for a press that was carried out would put a refusal
        // in the trail of a frame that did as it was told, and a refusal left standing after a
        // successful press would leave the row blaming a write that is over.
        string? held = "a firmware write is running on the microphone unit";
        var reported = 0;

        var recovery = new FrameRecovery(new FrameRecoveryServices
        {
            ResetBudgets = () => [],
            SystemControl = new RecordingSystemControl(),
            Log = new RecordingLog(),
            Held = () => held,
            OnRefused = (_, _) =>
            {
                reported++;
                return ValueTask.CompletedTask;
            },
        });

        Assert.False(await recovery.RestartAsync("Somebody at the frame", TestContext.Current.CancellationToken));
        Assert.Equal(1, reported);

        // A second press while the same write runs is a second moment and a second event: somebody
        // pressed twice, and the trail is what says so.
        Assert.False(await recovery.ShutdownAsync("The Fleet Manager", TestContext.Current.CancellationToken));
        Assert.Equal(2, reported);

        held = null;

        Assert.True(await recovery.RestartAsync("Somebody at the frame", TestContext.Current.CancellationToken));
        Assert.Equal(2, reported);
        Assert.Null(recovery.Refusal);
        Assert.Null(recovery.LastRefusal);

        // Both verbs, because both file their own event and either one filing on the way through
        // would put a refusal in the trail of a frame that did exactly as it was told.
        Assert.True(await recovery.ShutdownAsync("The Fleet Manager", TestContext.Current.CancellationToken));
        Assert.Equal(2, reported);
    }

    [Fact]
    public async Task The_next_firmware_write_does_not_bring_back_the_last_ones_refusal()
    {
        // The failure this closes is quiet and would look exactly like a bug in the frame: an
        // operator opens the fleet list during a *second* write and finds a refusal for a press
        // nobody made, naming a write that finished an hour ago. A refusal hidden behind the hold
        // rather than dropped by it does precisely that, because the hold comes back — three times
        // inside one operation, and again on the next authorisation.
        string? held = "a firmware write is running on the microphone unit (attempt 1 of 3)";

        var recovery = new FrameRecovery(new FrameRecoveryServices
        {
            ResetBudgets = () => [],
            SystemControl = new RecordingSystemControl(),
            Log = new RecordingLog(),
            Held = () => held,
        });

        Assert.False(await recovery.ShutdownAsync("The Fleet Manager", TestContext.Current.CancellationToken));
        Assert.NotNull(recovery.Refusal);

        // The write ends. Nobody presses anything, and nobody tells this object anything.
        held = null;
        Assert.Null(recovery.Refusal);

        // A second write opens a second window. Nothing was refused during it.
        held = "a firmware write is running on the microphone unit (attempt 2 of 3)";
        Assert.Null(recovery.Refusal);
    }

    [Fact]
    public async Task A_reporting_failure_costs_a_line_in_the_trail_and_nothing_else()
    {
        // Reporting is downstream of the interlock in every sense — the press is already turned
        // down and the journal already has it. These run from a channel event and from an inbound
        // control message, neither of which has anywhere to put an exception, so a fault escaping
        // here would be the silently-dead button this whole screen exists to prevent.
        var systemControl = new RecordingSystemControl();
        var log = new RecordingLog();

        var recovery = new FrameRecovery(new FrameRecoveryServices
        {
            ResetBudgets = () => [],
            SystemControl = systemControl,
            Log = log,
            Held = () => "a firmware write is running on the microphone unit",
            OnRefused = (_, _) => throw new InvalidOperationException("the outbox is wedged"),
        });

        Assert.False(await recovery.ShutdownAsync("The Fleet Manager", TestContext.Current.CancellationToken));

        Assert.Empty(systemControl.Commands);
        Assert.NotNull(recovery.Refusal);
        Assert.Contains(log.Lines, line => line.Contains("Nothing has been queued", StringComparison.Ordinal));
        Assert.Contains(
            log.Lines,
            line => line.Contains("could not be reported to the Fleet Manager", StringComparison.Ordinal));
    }

    [Fact]
    public void A_refusal_rides_beside_the_firmware_token_and_moves_no_health()
    {
        // A refused press is not a rung on §2.6's ladder: nothing has drifted, the product runs,
        // and the frame is exactly what the operator declared. Both tokens sit after the closing
        // parenthesis for that reason, and each has to stay readable with the other present.
        var flash = new ArrayFlashWireStatus { Screen = "writing", Stage = ArrayFlashStages.Manifesting };
        var refusal = new PowerRefusalStatus
        {
            Verb = PowerVerbs.Restart,
            Why = FrameRecovery.RefusalLine("a firmware write is running on the microphone unit"),
        };

        foreach (var state in new[] { LoopStateNames.Converged, LoopStateNames.Reconciling, LoopStateNames.Escalated })
        {
            var plain = AgentHealth.ReportFor(state, "linux-arm64");
            var both = PowerRefusalWire.Append(ArrayFlashWire.Append(plain, flash), refusal);

            Assert.Equal(AgentHealth.Classify(plain), AgentHealth.Classify(both));

            Assert.Equal(ArrayFlashStages.Manifesting, ArrayFlashWire.Read(both)?.Stage);
            Assert.Equal(refusal.Why, PowerRefusalWire.Read(both)?.Why);

            // And each comes out of the string without taking the other with it.
            Assert.Equal(plain, PowerRefusalWire.Without(ArrayFlashWire.Without(both)));
        }
    }

    [Fact]
    public void A_self_report_carrying_no_refusal_is_returned_exactly_as_it_arrived()
    {
        Assert.Null(PowerRefusalWire.Read(null));
        Assert.Null(PowerRefusalWire.Read("InSync(linux-arm64)"));
        Assert.Null(PowerRefusalWire.Without(null));
        Assert.Equal("InSync(linux-arm64)", PowerRefusalWire.Without("InSync(linux-arm64)"));
        Assert.Equal("InSync(linux-arm64)", PowerRefusalWire.Append("InSync(linux-arm64)", null));

        // A token with no sentence in it carries nothing a surface could show, and inventing one
        // here is exactly the recomposition the wire type exists to prevent.
        Assert.Null(PowerRefusalWire.Read("InSync(x) [power-refused verb=restart]"));
        Assert.Null(PowerRefusalWire.Read("InSync(x) [power-refused"));

        // A frame with nothing else to say still says the one thing that matters.
        Assert.Equal(
            "[power-refused verb=restart why=Not now.]",
            PowerRefusalWire.Append(null, new PowerRefusalStatus { Verb = PowerVerbs.Restart, Why = "Not now." }));
    }

    [Fact]
    public void The_sentence_is_the_last_field_and_cannot_close_the_token_early()
    {
        // `why` runs to the closing bracket, which is what lets it carry a sentence rather than one
        // word — so a sentence carrying a bracket of its own would truncate itself and leave a
        // fragment sitting in the operator's status column. The frame's own refusal contains none,
        // so this guards the shape rather than an attacker.
        var report = PowerRefusalWire.Append(
            "InSync(x)",
            new PowerRefusalStatus { Verb = "shut down", Why = "Not now — [held] a write. Wait." });

        var read = PowerRefusalWire.Read(report);

        Assert.NotNull(read);
        Assert.Equal("Not now — (held) a write. Wait.", read.Why);

        // The verb is one word by the time it is on the wire, and it survives as one.
        Assert.Equal("shut-down", read.Verb);
        Assert.Equal("InSync(x)", PowerRefusalWire.Without(report));

        // A key this build has never heard of is a newer frame saying something; it is skipped, and
        // everything this build does understand still reads.
        var newer = PowerRefusalWire.Read("InSync(x) [power-refused verb=restart at=99 why=Not now. Wait.]");

        Assert.NotNull(newer);
        Assert.Equal(PowerVerbs.Restart, newer.Verb);
        Assert.Equal("Not now. Wait.", newer.Why);
    }

    [Fact]
    public async Task A_refused_power_change_is_recorded_rather_than_retried()
    {
        // The one failure a person watches happen: they pressed a button and the frame stayed
        // exactly as it was. Nothing here retries — the next press is the retry — but the reason
        // has to exist somewhere a person can be told it.
        var systemControl = new RecordingSystemControl { Succeed = false };
        var log = new RecordingLog();

        var recovery = new FrameRecovery(new FrameRecoveryServices
        {
            ResetBudgets = () => [],
            SystemControl = systemControl,
            Log = log,
        });

        Assert.False(await recovery.RestartAsync("Somebody at the frame", TestContext.Current.CancellationToken));
        Assert.Single(systemControl.Commands);
        Assert.Contains(log.Lines, line => line.Contains("systemd refused it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_restart_puts_the_frame_back_to_work_where_a_bare_retry_only_clears_the_budget()
    {
        // The two verbs are the same reset with a different ending, and this is the ending: after a
        // restart the frame has a fresh three and acts again. Driven through the loop rather than
        // asserted on the ledger, because "forces a new retry" is a claim about what happens next.
        var resource = Broken();
        using var harness = new ReconcileHarness(Options, resource) { Telemetry = { Connected = true } };

        await harness.ConvergeAsync();
        Assert.True(harness.Loop.HasStopped);
        Assert.Equal(3, resource.Acts);

        var systemControl = new RecordingSystemControl();
        var recovery = new FrameRecovery(new FrameRecoveryServices
        {
            ResetBudgets = harness.Loop.ResetExhaustedBudgets,
            SystemControl = systemControl,
            Log = new RecordingLog(),
        });

        Assert.True(await recovery.RestartAsync("Somebody at the frame", TestContext.Current.CancellationToken));
        Assert.Equal(["reboot"], systemControl.Commands);
        Assert.False(harness.Loop.HasStopped);

        // The frame the reboot brings back: a fresh budget, and the ladder walked again from the
        // top rather than from where it left off.
        using var afterwards = ReconcileHarness.Rebooted(harness, Options, resource);
        var again = await afterwards.PassAsync();

        Assert.Equal(4, resource.Acts);
        Assert.Equal(1, ReconcileJournal.EntryFor(afterwards.Journal.Read(), resource.Name).Attempts);
        Assert.Equal(PassResult.Rebooted, again.Result);
    }
}
