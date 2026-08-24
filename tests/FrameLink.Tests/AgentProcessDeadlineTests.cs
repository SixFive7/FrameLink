using System.Diagnostics;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Resources;
using FrameLink.Agent.Stage;
using FrameLink.Agent.Supervise;

namespace FrameLink.Tests;

/// <summary>
/// <b>No external command may run for ever, and stopping one has to reach what it started.</b>
/// </summary>
/// <remarks>
/// <para>
/// The defect these pin. <c>IProcessRunner.RunAsync</c> drained both pipes and awaited
/// <c>WaitForExitAsync</c> with only the agent's shutdown token, so a hung <c>apt</c>,
/// <c>amixer</c>, <c>systemctl</c>, <c>pgrep</c>, <c>ps</c> or <c>xvf_host</c> waited for ever with
/// nothing on the screen changing to say so. The file already contained the sentence that condemns
/// it — <i>"a hung pass is worse than a failed one, because nothing on the screen ever changes to
/// say so"</i> — and guarded only the pipe-buffer half of the hazard, never the slow-child half.
/// It reached seven of the fifteen loops: a hung <c>systemctl</c> froze §2.7's browser stage, whose
/// entire purpose is to stop the panel going blank, and a hung <c>ps</c> froze all five of §2.10's
/// supervised behaviours at once because they share one tick.
/// </para>
/// <para>
/// <b>The half that a naive timeout does not fix, and the reason these tests spawn real
/// processes.</b> The commands the agent runs are wrappers — <c>apt</c> is
/// <c>env … apt-get …</c>, every user-scope command is <c>runuser … -- env … systemctl --user …</c>
/// — so the process the agent holds a handle to is not the process that hangs. Worse, a wrapper
/// that starts a child and then <i>exits</i> leaves that child holding the write end of the pipe:
/// the drain never sees end-of-file, the wrapper's own exit is not the end of anything, and a
/// timeout built on cancelling the read fires on paper while the call hangs in practice. That shape
/// has already cost this project a night. It cannot be asserted against a fake, because the whole
/// of it is behaviour of real pipes and real process trees, so these tests start real ones and are
/// the only tests in the suite that do.
/// </para>
/// </remarks>
public sealed class AgentProcessDeadlineTests
{
    /// <summary>Long enough to be unambiguous, short enough to run in a test suite.</summary>
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long a test waits for something it expects promptly before calling it a failure.
    /// </summary>
    /// <remarks>
    /// Generous on purpose. These assertions are about the difference between "seconds" and "never",
    /// so a wide margin costs nothing and removes the flake a tight one would add on a loaded
    /// workstation. A regression here does not miss by a second; it misses by five minutes.
    /// </remarks>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_command_that_answers_inside_its_deadline_is_untouched()
    {
        var (executable, arguments) = Echo("hello");

        var result = await HostProcessRunner.Instance
            .RunAsync(executable, arguments, ProcessDeadline.Local, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Combined);
        Assert.False(result.TimedOut);
        Assert.Null(result.Deadline);
        Assert.Contains("hello", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_command_that_runs_past_its_deadline_is_stopped_and_fails()
    {
        var survivors = Snapshot();
        var elapsed = Stopwatch.StartNew();

        try
        {
            var (executable, arguments) = Hang();

            var result = await HostProcessRunner.Instance
                .RunAsync(executable, arguments, Short, TestContext.Current.CancellationToken);

            elapsed.Stop();

            Assert.True(result.TimedOut, $"exit {result.ExitCode} after {elapsed.Elapsed}: {result.Combined}");
            Assert.False(result.Succeeded);
            Assert.Equal(Short, result.Deadline);

            // The bound that matters is not the exact figure — it is that this returned at all. The
            // command it ran would have taken five minutes.
            Assert.True(
                elapsed.Elapsed < Short + Patience,
                $"the call took {elapsed.Elapsed} against a deadline of {Short}");
        }
        finally
        {
            KillStrays(survivors);
        }
    }

    [Fact]
    public async Task The_failure_names_the_command_the_deadline_and_what_it_had_said()
    {
        var survivors = Snapshot();

        try
        {
            var (executable, arguments) = SaysThenHangs("starting");

            var result = await HostProcessRunner.Instance
                .RunAsync(executable, arguments, Short, TestContext.Current.CancellationToken);

            Assert.True(result.TimedOut, $"exit {result.ExitCode}: {result.Combined}");

            // <b>Whatever arrived before the kill survives it.</b> A report that said only "it did
            // not answer" would throw away the one piece of evidence about how far it got — which
            // for a firmware write or an apt run is the difference between "it never started" and
            // "it stopped half way".
            Assert.Contains("starting", result.StandardOutput, StringComparison.Ordinal);

            // All three facts land in the stream every reporting path in the agent already reads,
            // which is why a timeout needed no reporting site to learn about it.
            Assert.Contains(Path.GetFileName(executable), result.Combined, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(ProcessDeadline.Describe(Short), result.Combined, StringComparison.Ordinal);
            Assert.Contains("did not answer within", result.Combined, StringComparison.Ordinal);
        }
        finally
        {
            KillStrays(survivors);
        }
    }

    [Fact]
    public async Task The_kill_reaches_a_grandchild()
    {
        var before = Snapshot();

        try
        {
            var (executable, arguments) = LiveGrandchild();

            var run = HostProcessRunner.Instance
                .RunAsync(executable, arguments, Short, TestContext.Current.CancellationToken);

            // Proving the kill reaches a grandchild needs a grandchild to exist first, so the test
            // waits for one and says so if the shape it depends on never appeared. Without this it
            // would pass just as happily against a wrapper that spawned nothing.
            var grandchildren = await AppearedAsync(before);
            Assert.NotEmpty(grandchildren);

            var result = await run;
            Assert.True(result.TimedOut);

            foreach (var stray in grandchildren)
            {
                Assert.True(
                    await GoneAsync(stray),
                    $"process {stray} outlived the kill of its parent");
            }

            // Said as well as done: the runner does not assume the tree died, it waits to see the
            // pipes close, which only happens once nothing is holding them.
            Assert.Contains(
                "every process it had started were stopped",
                result.Combined,
                StringComparison.Ordinal);
        }
        finally
        {
            KillStrays(before);
        }
    }

    [Fact]
    public async Task A_grandchild_left_holding_the_pipe_cannot_hang_the_call()
    {
        var before = Snapshot();
        var elapsed = Stopwatch.StartNew();

        try
        {
            // The shape that cost a night: the child starts a process and exits immediately, so the
            // handle the agent holds goes to Exited within milliseconds while the grandchild keeps
            // the write end of stdout open. End-of-file never arrives. Nothing about the child is
            // wrong, and nothing about waiting for it is enough.
            var (executable, arguments) = OrphanedGrandchild();

            var result = await HostProcessRunner.Instance
                .RunAsync(executable, arguments, Short, TestContext.Current.CancellationToken);

            elapsed.Stop();

            Assert.True(result.TimedOut);
            Assert.True(
                elapsed.Elapsed < Short + Patience,
                $"the call took {elapsed.Elapsed} with a grandchild holding the pipe");
        }
        finally
        {
            KillStrays(before);
        }
    }

    [Fact]
    public async Task Being_cancelled_is_still_cancellation_and_not_a_timeout()
    {
        var before = Snapshot();

        try
        {
            var (executable, arguments) = Hang();
            using var caller = new CancellationTokenSource();

            var run = HostProcessRunner.Instance
                .RunAsync(executable, arguments, ProcessDeadline.PackageChange, caller.Token);

            await caller.CancelAsync();

            // <b>The deadline is not a shutdown, and the two must not be confused in either
            // direction.</b> A cancelled agent is standing down and owes its caller the exception it
            // always did; reporting a timeout instead would put "apt did not answer" on the screen
            // of a frame that was simply switched off — and, worse, would kill an apt mid-dpkg on
            // every restart. So cancellation deliberately does not kill the tree either.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        finally
        {
            KillStrays(before);
        }
    }

    [Fact]
    public async Task There_is_no_way_to_ask_for_an_unbounded_wait()
    {
        var (executable, arguments) = Echo("hello");

        // Timeout.InfiniteTimeSpan is the spelling somebody reaches for to restore the old
        // behaviour, and it is -1 milliseconds, so the same guard catches it as catches zero.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => HostProcessRunner.Instance
            .RunAsync(executable, arguments, Timeout.InfiniteTimeSpan, TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => HostProcessRunner.Instance
            .RunAsync(executable, arguments, TimeSpan.Zero, TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => HostProcessRunner.Instance
            .RunAsync(executable, arguments, TimeSpan.FromSeconds(-5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_stray_that_survived_the_kill_is_reported_rather_than_assumed_away()
    {
        var reached = ProcessResult.DeadlineExceeded(
            "pgrep",
            ["-x", "labwc"],
            TimeSpan.FromSeconds(30),
            standardOutput: string.Empty,
            standardError: string.Empty,
            treeKilled: true);

        var missed = ProcessResult.DeadlineExceeded(
            "pgrep",
            ["-x", "labwc"],
            TimeSpan.FromSeconds(30),
            standardOutput: string.Empty,
            standardError: string.Empty,
            treeKilled: false);

        Assert.Contains("pgrep -x labwc", reached.StandardError, StringComparison.Ordinal);
        Assert.Contains("30 seconds", reached.StandardError, StringComparison.Ordinal);
        Assert.Contains("every process it had started were stopped", reached.StandardError, StringComparison.Ordinal);

        // On Linux the kernel reparents an orphan to init, so a grandchild whose parent has already
        // exited cannot be found by following recorded parents. The agent is answered on time
        // either way; what it must not do is claim a clean kill it did not get, because a stray
        // holding a USB device is the next person's mystery.
        Assert.Contains("may have survived it", missed.StandardError, StringComparison.Ordinal);
        Assert.True(missed.TimedOut);
        Assert.False(missed.Succeeded);
    }

    [Fact]
    public void What_the_command_had_already_said_is_kept_beside_the_explanation()
    {
        var result = ProcessResult.DeadlineExceeded(
            "dfu-util",
            ["-D", "image.bin"],
            ProcessDeadline.Firmware,
            standardOutput: "Downloading to address = 0x00000000",
            standardError: "  dfu-util: warning",
            treeKilled: true);

        Assert.Equal("Downloading to address = 0x00000000", result.StandardOutput);
        Assert.StartsWith("dfu-util: warning", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("5 minutes", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Downloading to address", result.Combined, StringComparison.Ordinal);
    }

    [Fact]
    public void A_very_long_command_is_cut_rather_than_allowed_onto_the_panel_whole()
    {
        // The worst case is real: every user-scope command reaches its target through runuser and
        // four environment assignments, so the line is long before the interesting words start.
        var result = ProcessResult.DeadlineExceeded(
            "runuser",
            ["-u", "framelink", "--", "env", new string('x', 400), "systemctl", "--user", "is-active", "x.service"],
            ProcessDeadline.Service,
            standardOutput: string.Empty,
            standardError: string.Empty,
            treeKilled: true);

        Assert.Contains("…", result.StandardError, StringComparison.Ordinal);
        Assert.True(
            result.StandardError.Length < 400,
            $"the explanation was {result.StandardError.Length} characters long");
    }

    [Fact]
    public void A_timeout_leaves_a_loop_and_an_ordinary_failure_does_not()
    {
        var answered = new ProcessResult(1, string.Empty, "chromium-kiosk.service is inactive");
        Assert.Equal(answered, ProcessTimeoutException.ThrowIfTimedOut(answered));

        var timedOut = ProcessResult.DeadlineExceeded(
            "systemctl",
            ["--user", "is-active", "chromium-kiosk.service"],
            ProcessDeadline.Service,
            standardOutput: string.Empty,
            standardError: string.Empty,
            treeKilled: true);

        var thrown = Assert.Throws<ProcessTimeoutException>(() => ProcessTimeoutException.ThrowIfTimedOut(timedOut));

        // This message is the observed half of the delta the frame's own screen renders when the
        // loop's supervisor records agent.loop.<name>, so it has to read as a sentence about a
        // command and not as a type name.
        Assert.Contains("systemctl --user is-active chromium-kiosk.service", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("2 minutes", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', thrown.Message);
        Assert.Equal(ProcessDeadline.Service, thrown.Deadline);
    }

    [Fact]
    public void Every_deadline_is_generous_enough_that_nothing_healthy_reaches_it()
    {
        // Not an assertion about the exact numbers — those are judgements and the remarks argue
        // them. This is the ordering between them, which is the part a later edit could quietly
        // break: a command that talks to systemd cannot be bounded more tightly than one that reads
        // /proc, and apt cannot be bounded more tightly than a firmware write.
        Assert.True(ProcessDeadline.Local < ProcessDeadline.Resolver);
        Assert.True(ProcessDeadline.Resolver < ProcessDeadline.Array);
        Assert.True(ProcessDeadline.Array < ProcessDeadline.Service);
        Assert.True(ProcessDeadline.Service < ProcessDeadline.Firmware);
        Assert.True(ProcessDeadline.Firmware < ProcessDeadline.Storage);
        Assert.True(ProcessDeadline.Storage < ProcessDeadline.PackageChange);

        // Every one of them is finite and none is a placeholder.
        foreach (var deadline in new[]
        {
            ProcessDeadline.Local,
            ProcessDeadline.Resolver,
            ProcessDeadline.Array,
            ProcessDeadline.Service,
            ProcessDeadline.Firmware,
            ProcessDeadline.Storage,
            ProcessDeadline.PackageChange,
            ProcessDeadline.KillGrace,
        })
        {
            Assert.True(deadline > TimeSpan.Zero);
            Assert.True(deadline <= TimeSpan.FromHours(1));
        }

        // systemd's own DefaultTimeoutStartSec on Debian is 90 seconds. A deadline at or below it
        // would fire on jobs systemd was itself seconds from failing honestly, which is the false
        // timeout §2.7's browser stage can least afford.
        Assert.True(ProcessDeadline.Service > TimeSpan.FromSeconds(90));
    }

    [Fact]
    public void The_gate_in_front_of_the_microphone_array_can_be_bounded_now_and_is()
    {
        // <b>The in-code justification for waiting forever here was that the tool could wait
        // forever anyway</b> — "a hung tool wedges the caller today, with or without this gate" —
        // so bounding the gate bought a second way to report a working array as absent and nothing
        // else. That was true, and the premise is gone.
        Assert.NotEqual(Timeout.InfiniteTimeSpan, XvfHost.ConversationWait);
        Assert.True(XvfHost.ConversationWait > TimeSpan.Zero);

        // Strictly longer than one holder's whole deadline, which is the property that keeps a
        // legitimate queue from ever failing: three things in the process hold this gate, so at most
        // two can be ahead of any waiter, and each may spend its whole deadline.
        Assert.True(XvfHost.ConversationWait > ProcessDeadline.Array * 2);
        Assert.Equal(ProcessDeadline.Array * 3, XvfHost.ConversationWait);
    }

    [Fact]
    public async Task A_caller_that_cannot_get_the_array_gate_is_told_so_rather_than_left_waiting()
    {
        using var held = new SemaphoreSlim(0, 1);
        var tool = new XvfHost(new MemorySystemFiles(), new BlockingRunner(held), new FakeUserSession());

        // One conversation in flight, holding the process-wide gate for as long as this test likes.
        var first = tool.RunAsync("/opt/framelink", [XvfHost.VersionCommand], TestContext.Current.CancellationToken);

        try
        {
            var second = await tool.RunAsync(
                "/opt/framelink",
                [XvfHost.GpoReadCommand],
                TimeSpan.FromMilliseconds(50),
                TestContext.Current.CancellationToken);

            // It comes back as an ordinary timeout, so the pass reads it as the drift it is and the
            // loops beside the pass convert it exactly as they convert every other one.
            Assert.True(second.TimedOut);
            Assert.False(second.Succeeded);
            Assert.Contains(XvfHost.Binary, second.Combined, StringComparison.Ordinal);
            Assert.Contains("held the tool for longer than", second.Combined, StringComparison.Ordinal);
        }
        finally
        {
            held.Release();
            await first;
        }
    }

    [Fact]
    public void Durations_are_written_in_the_register_the_rest_of_the_agent_uses()
    {
        Assert.Equal("30 seconds", ProcessDeadline.Describe(TimeSpan.FromSeconds(30)));
        Assert.Equal("1 minute", ProcessDeadline.Describe(TimeSpan.FromMinutes(1)));
        Assert.Equal("2 minutes", ProcessDeadline.Describe(TimeSpan.FromMinutes(2)));
        Assert.Equal("60 minutes", ProcessDeadline.Describe(TimeSpan.FromMinutes(60)));
        Assert.Equal("90 seconds", ProcessDeadline.Describe(TimeSpan.FromSeconds(90)));
        Assert.Equal("1.5 seconds", ProcessDeadline.Describe(TimeSpan.FromSeconds(1.5)));
    }

    [Fact]
    public async Task A_double_that_cannot_time_anything_still_answers_a_call_with_a_deadline()
    {
        // The seam has to stay cheap to fake. Five doubles in this suite answer from a dictionary in
        // microseconds, and making the deadline abstract would have made every one of them
        // implement a clock for nothing.
        IProcessRunner deaf = new DeadlineBlindRunner();

        var result = await deaf.RunAsync("amixer", ["sget", "Master"], ProcessDeadline.Local, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task A_double_that_wants_to_can_assert_which_deadline_the_call_site_chose()
    {
        var runner = new DeadlineRecordingRunner();

        await runner.RunAsync("apt-get", ["full-upgrade"], ProcessDeadline.PackageChange, TestContext.Current.CancellationToken);
        await runner.RunAsync("amixer", ["sget", "Master"], ProcessDeadline.Local, TestContext.Current.CancellationToken);

        Assert.Equal([ProcessDeadline.PackageChange, ProcessDeadline.Local], runner.Deadlines);
    }

    [Fact]
    public async Task Every_systemctl_call_is_bounded_by_systemds_own_job_timeout()
    {
        var runner = new DeadlineRecordingRunner();
        var systemd = new SystemdControl(runner);

        await systemd.RunAsync(["restart", "chromium-kiosk.service"], TestContext.Current.CancellationToken);

        // One deadline for the whole adapter rather than one per caller: everything through here is
        // systemctl, so the cost profile is the adapter's and not the call site's.
        Assert.Equal([ProcessDeadline.Service], runner.Deadlines);
    }

    [Fact]
    public async Task A_user_scope_command_is_bounded_and_so_is_the_lookup_in_front_of_it()
    {
        var runner = new DeadlineRecordingRunner();
        runner.Answers["id -u framelink"] = new ProcessResult(0, "1000", string.Empty);
        var session = new LoginUserSession(runner, () => "framelink");

        await session.RunAsync(
            "systemctl",
            ["--user", "is-active", "chromium-kiosk.service"],
            TestContext.Current.CancellationToken);

        // Two commands, two different questions, two different bounds. `id` reads a local file and
        // is measured in milliseconds; the runuser wrapper reaches a session bus and a systemd job.
        // The old code gave both of them for ever.
        Assert.Equal(
            [(ProcessDeadline.Local, "id"), (ProcessDeadline.Service, "runuser")],
            runner.Calls.ConvertAll(call => (call.Deadline, call.Executable)));
    }

    [Fact]
    public async Task Handing_a_file_to_the_login_user_is_bounded()
    {
        var runner = new DeadlineRecordingRunner();
        var session = new LoginUserSession(runner, () => "framelink");

        await session.GiveToUserAsync("/home/framelink/.config/labwc/autostart", TestContext.Current.CancellationToken);

        Assert.NotEmpty(runner.Calls);
        Assert.All(runner.Calls, call => Assert.Equal(ProcessDeadline.Local, call.Deadline));
        Assert.All(runner.Calls, call => Assert.Equal("chown", call.Executable));
    }

    [Fact]
    public async Task Talking_to_the_microphone_array_is_bounded_by_the_bus_and_not_by_the_transaction()
    {
        var runner = new DeadlineRecordingRunner();
        var tool = new XvfHost(new MemorySystemFiles(), runner, new FakeUserSession());

        await tool.RunAsync("/opt/framelink", [XvfHost.VersionCommand], TestContext.Current.CancellationToken);

        // The transaction is a sub-second control transfer. What the deadline allows for is an array
        // that has just been reset and is re-enumerating on the bus.
        Assert.Equal([ProcessDeadline.Array], runner.Deadlines);
    }

    [Fact]
    public async Task Reading_what_is_installed_is_bounded_tightly_and_changing_it_is_not()
    {
        var runner = new DeadlineRecordingRunner();
        var apt = new AptPackages(runner);

        await apt.ListInstalledAsync(TestContext.Current.CancellationToken);
        Assert.Equal([ProcessDeadline.Local], runner.Deadlines);

        runner.Calls.Clear();
        await apt.InstallAsync("chromium", TestContext.Current.CancellationToken);

        // <b>The asymmetry the operator asked for, in one test.</b> Querying the package database is
        // a local read and gets thirty seconds; changing it downloads over the household's
        // connection and gets an hour, because a false timeout here would kill an apt mid-dpkg and
        // leave a state the agent cannot repair from.
        Assert.Contains(ProcessDeadline.PackageChange, runner.Deadlines);
        Assert.DoesNotContain(ProcessDeadline.Local, runner.Deadlines);
    }

    /// <summary>A runner with no notion of time, which is every double in the suite.</summary>
    private sealed class DeadlineBlindRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
    }

    [Fact]
    public async Task A_command_that_never_answered_leaves_the_screen_handover_loop()
    {
        using var caller = new CancellationTokenSource();
        var clock = new ManualClock();

        // <b>The loop is given a way out, so a regression fails rather than spins.</b> This clock
        // does not really wait, so a filter that swallowed the timeout would leave RunAsync turning
        // over as fast as the machine allows and the test would hang instead of failing. Stopping it
        // after a few ticks means a swallowed timeout comes back as "RunAsync returned" -- which is
        // the assertion below, failing in milliseconds and saying what it means.
        clock.OnDelay = _ =>
        {
            if (clock.Delays.Count >= 3)
            {
                caller.Cancel();
            }
        };

        var handover = new ScreenHandover(
            new RecordingVirtualTerminals(TtyTerminal.ProductTerminal),
            new TimingOutRunner(),
            clock,
            new RecordingLog());

        // <b>Both halves of the mechanism in one assertion.</b> The call site has to convert the
        // flag into something that leaves, and the loop's broad catch has to let it past — either
        // one alone and this loop goes on forking a pgrep every two seconds against a system that
        // has stopped answering, for the life of the frame, with nothing counting it.
        var thrown = await Assert.ThrowsAsync<ProcessTimeoutException>(() => handover.RunAsync(caller.Token));

        Assert.Contains("pgrep", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("did not answer within", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_memory_watchdogs_only_measurement_is_bounded()
    {
        var probe = new ProcMemoryProbe(new MemorySystemFiles(), new TimingOutRunner());

        // §2.10's five behaviours share one tick, so this is the call whose hang froze all of them
        // at once — and it is the frame's last defence against an OOM kill.
        var thrown = await Assert.ThrowsAsync<ProcessTimeoutException>(async () =>
            await probe.SampleAsync(TestContext.Current.CancellationToken));

        Assert.Contains("ps -eo rss=,comm=", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_timeout_ends_the_reporting_loop_and_fails_the_resource_from_the_same_class()
    {
        var apt = new AptPackages(new TimingOutRunner());

        // <b>The same class, two opposite answers, and both are right.</b> Listing installed
        // packages is only ever called by a supervised loop with no ledger, so a dpkg database that
        // has stopped answering has to leave the loop rather than be reported as an empty inventory
        // once an hour for ever.
        await Assert.ThrowsAsync<ProcessTimeoutException>(async () =>
            await apt.ListInstalledAsync(TestContext.Current.CancellationToken));

        // Installing is a resource's Act. It fails the resource, spends one of the three attempts
        // and lets the ladder render it — which is the operator's decision, and a throw here would
        // instead take the whole reconcile pass down over one package.
        var outcome = await apt.InstallAsync("chromium", TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Contains("did not answer within", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_narrower_result_systemctl_hands_back_can_still_say_it_never_answered()
    {
        var systemd = new SystemdControl(new TimingOutRunner());

        var result = await systemd.RunAsync(["stop", "getty@tty1.service"], TestContext.Current.CancellationToken);

        // §2.7's browser stage stops and starts the getty through this type, not through
        // ProcessResult. A result that could not carry the flag would leave those two calls unable
        // to tell "systemd said no" from "systemd never answered".
        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);

        var thrown = Assert.Throws<ProcessTimeoutException>(() => ProcessTimeoutException.ThrowIfTimedOut(result));
        Assert.Contains("systemctl stop getty@tty1.service", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', thrown.Message);

        // And an answer that arrived is passed straight through, so nothing that works changes.
        var answered = new SystemControlResult(true, "done");
        Assert.Equal(answered, ProcessTimeoutException.ThrowIfTimedOut(answered));
    }

    /// <summary>
    /// A runner on which every command is stopped for running past its deadline.
    /// </summary>
    /// <remarks>
    /// It builds a real <see cref="ProcessResult.DeadlineExceeded"/>, so what the tests read is the
    /// same sentence a frame would put on its own screen rather than a stand-in for it.
    /// </remarks>
    private sealed class TimingOutRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) =>
            RunAsync(executable, arguments, ProcessDeadline.Local, cancellationToken);

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan deadline,
            CancellationToken cancellationToken) =>
            Task.FromResult(ProcessResult.DeadlineExceeded(
                executable,
                arguments,
                deadline,
                standardOutput: string.Empty,
                standardError: string.Empty,
                treeKilled: true));
    }

    /// <summary>A runner that does not answer until it is let go.</summary>
    private sealed class BlockingRunner(SemaphoreSlim release) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) =>
            RunAsync(executable, arguments, ProcessDeadline.Array, cancellationToken);

        public async Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan deadline,
            CancellationToken cancellationToken)
        {
            await release.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new ProcessResult(0, string.Empty, string.Empty);
        }
    }

    /// <summary>What one call site asked for.</summary>
    private readonly record struct RecordedCall(string Executable, string Line, TimeSpan Deadline);

    /// <summary>
    /// A runner that records what each call site asked for.
    /// </summary>
    /// <remarks>
    /// <b>It overrides the deadline-bearing overload</b>, which is the arrangement that makes "did
    /// this call site choose the right bound?" an assertion rather than a code review. A double that
    /// only implemented the older overload would see the deadline discarded by the interface's
    /// default and could never tell a right choice from no choice at all.
    /// </remarks>
    private sealed class DeadlineRecordingRunner : IProcessRunner
    {
        /// <summary>
        /// What a call site that has not chosen a deadline records as.
        /// </summary>
        /// <remarks>
        /// A value no real call site uses, so a test asserting a deadline fails loudly rather than
        /// quietly matching whatever a transitional overload happens to pass.
        /// </remarks>
        private static readonly TimeSpan Unchosen = TimeSpan.FromTicks(1);

        public List<RecordedCall> Calls { get; } = [];

        public Dictionary<string, ProcessResult> Answers { get; } = new(StringComparer.Ordinal);

        public List<TimeSpan> Deadlines => Calls.ConvertAll(call => call.Deadline);

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) =>
            RunAsync(executable, arguments, Unchosen, cancellationToken);

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan deadline,
            CancellationToken cancellationToken)
        {
            var line = ProcessResultLine(executable, arguments);
            Calls.Add(new RecordedCall(executable, line, deadline));

            return Task.FromResult(Answers.TryGetValue(line, out var scripted)
                ? scripted
                : new ProcessResult(0, string.Empty, string.Empty));
        }

        private static string ProcessResultLine(string executable, IReadOnlyList<string> arguments) =>
            arguments.Count == 0 ? executable : executable + " " + string.Join(' ', arguments);
    }

    // ---- the process shapes, per platform -------------------------------------------------
    //
    // The suite's gate runs on the workstation, which is Windows; the agent runs on a frame, which
    // is Linux. Both branches are written because the shapes are the same idea in two dialects, and
    // only the workstation half has been executed — the frame half is unverified and says so here
    // rather than in a commit message somebody has to go and find.

    private static string Shell => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")
        : "/bin/sh";

    /// <summary>The name the grandchild appears under in the process table.</summary>
    private static string GrandchildName => OperatingSystem.IsWindows() ? "WAITFOR" : "sleep";

    /// <summary>
    /// A command that hangs and cannot fail on its way there.
    /// </summary>
    /// <remarks>
    /// <b>Not <c>ping</c>, and the reason is measured rather than stylistic.</b> <c>ping -n 300</c>
    /// is the usual Windows sleeper, but it needs the IP helper driver and a socket, and under a
    /// loaded parallel test run it occasionally fails to get one and exits in under a second. That
    /// reads to these tests as "the deadline never fired", which is the opposite of what happened,
    /// and it flaked two of them. <c>waitfor</c> waits on a named signal that is never sent: no
    /// socket, no console, no driver, nothing to fail at.
    /// </remarks>
    private static string Sleeper(string signal) =>
        OperatingSystem.IsWindows() ? $"waitfor /t 300 {signal}" : "sleep 300";

    /// <summary>A command that answers at once.</summary>
    private static (string Executable, string[] Arguments) Echo(string what) =>
        OperatingSystem.IsWindows()
            ? (Shell, ["/c", "echo " + what])
            : (Shell, ["-c", "echo " + what]);

    /// <summary>A command that hangs, with no children of its own.</summary>
    private static (string Executable, string[] Arguments) Hang() =>
        OperatingSystem.IsWindows()
            ? (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "waitfor.exe"),
               ["/t", "300", "FrameLinkDeadlineHang"])
            : ("/bin/sleep", ["300"]);

    /// <summary>A command that says something and then hangs.</summary>
    private static (string Executable, string[] Arguments) SaysThenHangs(string what) =>
        OperatingSystem.IsWindows()
            ? (Shell, ["/c", $"echo {what}&" + Sleeper("FrameLinkDeadlineSaid")])
            : (Shell, ["-c", $"echo {what}; sleep 300"]);

    /// <summary>A command that hangs while a child of its own also runs.</summary>
    private static (string Executable, string[] Arguments) LiveGrandchild() =>
        OperatingSystem.IsWindows()
            ? (Shell, ["/c", Sleeper("FrameLinkDeadlineLive")])
            : (Shell, ["-c", "sleep 300 & wait"]);

    /// <summary>A command that exits at once, leaving a child holding the pipe.</summary>
    private static (string Executable, string[] Arguments) OrphanedGrandchild() =>
        OperatingSystem.IsWindows()
            ? (Shell, ["/c", "start /b " + Sleeper("FrameLinkDeadlineOrphan")])
            : (Shell, ["-c", "sleep 300 &"]);

    // ---- looking at the process table ----------------------------------------------------

    private static HashSet<int> Snapshot()
    {
        var pids = new HashSet<int>();

        foreach (var process in Process.GetProcessesByName(GrandchildName))
        {
            try
            {
                pids.Add(process.Id);
            }
            catch (InvalidOperationException)
            {
                // It exited between the enumeration and the read, which is the same as not being
                // in the snapshot at all.
            }
            finally
            {
                process.Dispose();
            }
        }

        return pids;
    }

    /// <summary>Waits for processes that were not in <paramref name="before"/> to appear.</summary>
    private static async Task<List<int>> AppearedAsync(HashSet<int> before)
    {
        var until = Stopwatch.StartNew();

        while (until.Elapsed < Patience)
        {
            var now = Snapshot();
            now.ExceptWith(before);

            if (now.Count > 0)
            {
                return [.. now];
            }

            await Task.Delay(25, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        return [];
    }

    /// <summary>Waits for <paramref name="pid"/> to stop existing.</summary>
    private static async Task<bool> GoneAsync(int pid)
    {
        var until = Stopwatch.StartNew();

        while (until.Elapsed < Patience)
        {
            if (!Alive(pid))
            {
                return true;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        return !Alive(pid);
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Kills anything this test started that is still running.
    /// </summary>
    /// <remarks>
    /// A suite that leaks a five-minute <c>ping</c> per run is a suite that slowly fills a
    /// workstation's process table, and the cancellation test leaks one <i>by design</i> — the
    /// runner deliberately does not kill a tree it was cancelled out of, because doing so would kill
    /// an <c>apt</c> mid-transaction on every agent shutdown.
    /// </remarks>
    private static void KillStrays(HashSet<int> before)
    {
        var strays = Snapshot();
        strays.ExceptWith(before);

        foreach (var pid in strays)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                // Already gone, or never ours to kill.
            }
        }
    }
}
