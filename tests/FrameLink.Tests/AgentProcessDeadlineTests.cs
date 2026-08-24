using System.Diagnostics;
using FrameLink.Agent.Hosting;

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

            Assert.True(result.TimedOut);
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

            Assert.True(result.TimedOut);

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

    /// <summary>A runner with no notion of time, which is every double in the suite.</summary>
    private sealed class DeadlineBlindRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
    }

    /// <summary>A runner that records what each call site asked for.</summary>
    private sealed class DeadlineRecordingRunner : IProcessRunner
    {
        public List<TimeSpan> Deadlines { get; } = [];

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan deadline,
            CancellationToken cancellationToken)
        {
            Deadlines.Add(deadline);
            return RunAsync(executable, arguments, cancellationToken);
        }
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
    private static string GrandchildName => OperatingSystem.IsWindows() ? "PING" : "sleep";

    /// <summary>A command that answers at once.</summary>
    private static (string Executable, string[] Arguments) Echo(string what) =>
        OperatingSystem.IsWindows()
            ? (Shell, ["/c", "echo " + what])
            : (Shell, ["-c", "echo " + what]);

    /// <summary>A command that hangs, with no children of its own.</summary>
    private static (string Executable, string[] Arguments) Hang() =>
        OperatingSystem.IsWindows()
            ? (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE"),
               ["-n", "300", "127.0.0.1"])
            : ("/bin/sleep", ["300"]);

    /// <summary>A command that says something and then hangs.</summary>
    private static (string Executable, string[] Arguments) SaysThenHangs(string what) =>
        OperatingSystem.IsWindows()
            ? (Shell, ["/c", $"echo {what}&ping -n 300 127.0.0.1"])
            : (Shell, ["-c", $"echo {what}; sleep 300"]);

    /// <summary>A command that hangs while a child of its own also runs.</summary>
    private static (string Executable, string[] Arguments) LiveGrandchild() =>
        OperatingSystem.IsWindows()
            ? (Shell, ["/c", "ping -n 300 127.0.0.1"])
            : (Shell, ["-c", "sleep 300 & wait"]);

    /// <summary>A command that exits at once, leaving a child holding the pipe.</summary>
    private static (string Executable, string[] Arguments) OrphanedGrandchild() =>
        OperatingSystem.IsWindows()
            ? (Shell, ["/c", "start /b ping -n 300 127.0.0.1"])
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
