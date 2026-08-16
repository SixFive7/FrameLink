using FrameLink.Agent.Hosting;
using FrameLink.Agent.Stage;

namespace FrameLink.Tests;

/// <summary>
/// §2.7's two stages on two virtual terminals, and the boundary between them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect these exist for was measured on the frame.</b> The system booted, the agent's
/// repair screen appeared for under a second, and a login prompt replaced it — two programs holding
/// <c>/dev/tty1</c>, <c>agetty</c> and <c>fl-agent</c>, and the getty repainting last. The fault
/// window is exactly the provisioning hour, which is the one hour in a frame's life when the
/// narration is all it has to say for itself.
/// </para>
/// <para>
/// <b>None of this can be verified on the machine that runs the suite</b>, which has no consoles,
/// and it cannot be verified on the machine that has consoles, which does not run the suite. So the
/// seam is the same shape as <c>TtyTerminal.Over</c>: <see cref="IVirtualTerminals"/> is the whole
/// of the kernel's involvement, and every case below — including the two failure modes, a kernel
/// that refuses the request and a switch that is accepted and never happens — is exercised through
/// it.
/// </para>
/// </remarks>
public sealed class AgentScreenHandoverTests
{
    private static readonly CancellationToken None = TestContext.Current.CancellationToken;

    [Fact]
    public void The_agents_terminal_is_clear_of_everything_that_claims_one()
    {
        // One is the product's, and getty serves §5.5's physical login on it. Two through six are
        // claimed on demand by systemd-logind's NAutoVTs=6, so putting the stage on one of those
        // would defer the same two-programs-one-screen defect rather than fix it, and ReserveVT=6
        // keeps a getty on six permanently. Seven is where an X server lands by convention.
        Assert.Equal(1, TtyTerminal.ProductTerminal);
        Assert.True(TtyTerminal.AgentTerminal > 7, "tty1-6 are logind's and tty7 is X11's by convention.");

        // And it has to stay inside the twelve function keys, because Ctrl+Alt+F8 is how an
        // operator standing at the frame reads the narration when the agent has deliberately not
        // taken the screen. There is no Ctrl+Alt+F13.
        Assert.True(TtyTerminal.AgentTerminal <= 12, "The terminal has to be reachable from a keyboard.");

        // The device the stage paints and the terminal the handover reveals are one decision.
        Assert.Equal(TtyTerminal.AgentTerminal, TtyTerminal.NumberOf(TtyTerminal.DefaultPath));
    }

    [Theory]
    [InlineData("/dev/tty1", 1)]
    [InlineData("/dev/tty8", 8)]
    [InlineData("/dev/tty63", 63)]
    [InlineData("/dev/ttyAMA10", null)]
    [InlineData("/dev/tty0", null)]
    [InlineData("/dev/null", null)]
    [InlineData("", null)]
    public void A_console_device_path_says_which_terminal_it_is(string path, int? expected) =>
        Assert.Equal(expected, TtyTerminal.NumberOf(path));

    [Theory]
    [InlineData("tty1\n", 1)]
    [InlineData("tty8", 8)]
    [InlineData("ttyAMA10 tty1", 1)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("ttyS0", null)]
    public void The_kernels_reading_of_which_console_is_in_front_is_parsed(string active, int? expected) =>
        Assert.Equal(expected, LinuxVirtualTerminals.NumberIn(active));

    [Theory]
    [InlineData(false, BrowserStagePhase.Console, ScreenOwner.Agent)]
    [InlineData(false, BrowserStagePhase.Awaiting, ScreenOwner.Agent)]
    [InlineData(false, BrowserStagePhase.Live, ScreenOwner.Agent)]
    [InlineData(false, BrowserStagePhase.TornDown, ScreenOwner.Agent)]
    [InlineData(true, BrowserStagePhase.Console, ScreenOwner.Product)]
    [InlineData(true, BrowserStagePhase.Awaiting, ScreenOwner.Product)]
    [InlineData(true, BrowserStagePhase.Live, ScreenOwner.Product)]
    [InlineData(true, BrowserStagePhase.TornDown, ScreenOwner.Agent)]
    public void A_live_compositor_gets_its_terminal_unless_the_fallback_rule_has_condemned_it(
        bool compositor,
        BrowserStagePhase phase,
        ScreenOwner expected) =>
        Assert.Equal(expected, ScreenHandover.Decide(compositor, phase));

    [Fact]
    public async Task A_frame_with_no_compositor_yet_is_shown_the_agents_console()
    {
        // The provisioning hour. Nothing else is on the panel, the narration is the whole screen,
        // and before this it was being repainted over by a login prompt.
        using var frame = new Panel();

        Assert.Equal(ScreenOwner.Agent, await frame.Handover.ReconcileAsync(None));
        Assert.Equal(TtyTerminal.AgentTerminal, frame.Terminals.Active);
        Assert.Equal([TtyTerminal.AgentTerminal], frame.Terminals.Activated);
    }

    [Fact]
    public async Task The_compositor_getting_its_terminal_back_is_what_the_old_reveal_did()
    {
        using var frame = new Panel();
        await frame.Handover.ReconcileAsync(None);

        // labwc coming up is exactly the moment it used to draw over the shared console, so it is
        // exactly the moment the panel goes back. Nothing about the ladder is consulted: a frame
        // that is still reconciling gets its narration on the browser surface, which renders the
        // same status from the same hub.
        frame.Processes.CompositorRunning = true;
        frame.Settle();

        Assert.Equal(ScreenOwner.Product, await frame.Handover.ReconcileAsync(None));
        Assert.Equal(TtyTerminal.ProductTerminal, frame.Terminals.Active);
    }

    [Fact]
    public async Task A_backgrounded_compositor_is_never_left_running_because_it_could_never_converge()
    {
        // The consequence that decides the rule. A compositor whose terminal is in the background
        // has an inactive logind session, holds no DRM master and fails every output commit — so it
        // cannot present a page and cannot apply display.dsi2-transform. Keeping the panel while
        // labwc ran would make that resource fail on every boot, and §2.6 turns a resource that
        // fails on every boot into a reboot loop.
        using var frame = new Panel();
        frame.Processes.CompositorRunning = true;

        foreach (var phase in new[] { BrowserStagePhase.Console, BrowserStagePhase.Awaiting, BrowserStagePhase.Live })
        {
            frame.Phase = phase;
            frame.Settle();

            Assert.Equal(ScreenOwner.Product, await frame.Handover.ReconcileAsync(None));
        }
    }

    [Fact]
    public async Task An_agent_restart_on_a_working_frame_does_not_grab_the_panel()
    {
        // Every update and every crash restarts this process while the product is on the screen.
        // The first thing the host does is reconcile rather than take, so a service restart is
        // never a visible event.
        using var frame = new Panel();
        frame.Processes.CompositorRunning = true;

        Assert.Equal(ScreenOwner.Product, await frame.Handover.ReconcileAsync(None));
        Assert.Empty(frame.Terminals.Activated);
    }

    [Fact]
    public async Task A_compositor_that_blinks_does_not_flip_the_panel_and_one_that_dies_does()
    {
        using var frame = new Panel();
        frame.Processes.CompositorRunning = true;
        await frame.Handover.ReconcileAsync(None);
        Assert.Equal(ScreenOwner.Product, frame.Handover.Held);

        // The compositor has no restart policy of its own — the thing that restarts it is
        // getty@tty1 respawning the login that execs it — so it reappears a second or two later.
        // Dropping the panel to a text console and back for each of those is a fault of its own.
        frame.Processes.CompositorRunning = false;
        frame.Settle();
        await frame.Handover.ReconcileAsync(None);
        Assert.Equal(ScreenOwner.Product, frame.Handover.Held);

        frame.Processes.CompositorRunning = true;
        frame.Settle();
        await frame.Handover.ReconcileAsync(None);
        Assert.Equal(ScreenOwner.Product, frame.Handover.Held);
        Assert.Empty(frame.Terminals.Activated);

        // One that stays gone is a frame showing nothing, and the console is what it has left.
        frame.Processes.CompositorRunning = false;
        frame.Settle();
        await frame.Handover.ReconcileAsync(None);
        frame.Clock.UtcNow += frame.Handover.CoverAfter;
        await frame.Handover.ReconcileAsync(None);

        Assert.Equal(ScreenOwner.Agent, frame.Handover.Held);
        Assert.Equal(TtyTerminal.AgentTerminal, frame.Terminals.Active);
    }

    [Fact]
    public async Task The_panel_is_left_alone_for_a_settle_period_after_every_attempt()
    {
        // A switch away from a compositor makes it drop DRM master and a switch back makes it take
        // it again and repaint, so a status that oscillates would strobe the panel and hammer the
        // acquire path — which is where a black frame nobody can explain comes from.
        using var frame = new Panel();
        await frame.Handover.ReconcileAsync(None);
        Assert.Single(frame.Terminals.Activated);

        frame.Processes.CompositorRunning = true;
        await frame.Handover.ReconcileAsync(None);
        Assert.Single(frame.Terminals.Activated);

        frame.Settle();
        await frame.Handover.ReconcileAsync(None);
        Assert.Equal([TtyTerminal.AgentTerminal, TtyTerminal.ProductTerminal], frame.Terminals.Activated);
    }

    [Fact]
    public async Task A_switch_the_kernel_took_but_never_performed_is_not_reported_as_done()
    {
        // VT_ACTIVATE only asks. The switch completes when the process holding the outgoing
        // terminal releases it, which on a converged frame is a compositor dropping DRM master, and
        // one that has wedged never does. Reporting the request as the outcome would be the
        // write-only optimism §2.4 refuses, and what it would hide is a black panel.
        using var frame = new Panel();
        frame.Terminals.Completes = false;

        Assert.False(await frame.Handover.TakeAsync(None));
        Assert.Null(frame.Handover.Held);
        Assert.Contains(frame.Terminals.Activated, terminal => terminal == TtyTerminal.AgentTerminal);
        Assert.Contains(frame.Log.Lines, line => line.Contains("never said it happened", StringComparison.Ordinal));

        // Said once, not once per attempt: this runs on a loop and the journal has to stay
        // readable. The attempt itself repeats, because the handover is level-triggered.
        var said = frame.Log.Lines.Count(line => line.Contains("never said it happened", StringComparison.Ordinal));
        await frame.Handover.TakeAsync(None);
        Assert.Equal(said, frame.Log.Lines.Count(line => line.Contains("never said it happened", StringComparison.Ordinal)));
        Assert.True(frame.Terminals.Activated.Count >= 2, "A failed switch is retried, not given up on.");
    }

    [Fact]
    public async Task A_machine_with_no_consoles_stands_down_once_and_keeps_reconciling()
    {
        // A container, a workstation, a virtual agent (§5.3). The same demotion the console stage
        // makes when its terminal stops taking bytes, for the same reason: the answer cannot change
        // without a reboot, so a per-tick version of the line is a journal nobody can read.
        using var frame = new Panel();
        frame.Terminals.Accepts = false;

        Assert.False(await frame.Handover.TakeAsync(None));
        Assert.False(frame.Handover.Switchable);
        Assert.Null(frame.Handover.Held);

        var said = frame.Log.Lines.Count(line => line.Contains("will not switch virtual terminals", StringComparison.Ordinal));
        Assert.Equal(1, said);

        await frame.Handover.TakeAsync(None);
        await frame.Handover.ReconcileAsync(None);
        Assert.Single(frame.Terminals.Activated);
        Assert.Equal(said, frame.Log.Lines.Count(line => line.Contains("will not switch virtual terminals", StringComparison.Ordinal)));

        // And the loop frees itself rather than waking every couple of seconds forever.
        await frame.Handover.RunAsync(None);
    }

    [Fact]
    public async Task Somebody_logged_in_on_another_terminal_keeps_it()
    {
        // §5.5's recovery path is a person at the frame with a keyboard, and keeping getty on tty1
        // is what makes it exist. Pulling the panel out from under somebody who pressed
        // Ctrl+Alt+F2 would give that away again through the back door.
        using var frame = new Panel(foreground: 3);

        Assert.False(await frame.Handover.TakeAsync(None));
        Assert.Empty(frame.Terminals.Activated);
        Assert.Equal(3, frame.Terminals.Active);
        Assert.Contains(frame.Log.Lines, line => line.Contains("Somebody is using tty3", StringComparison.Ordinal));

        // Announced once while it lasts, and resumed on its own the moment they switch back.
        await frame.Handover.TakeAsync(None);
        Assert.Equal(1, frame.Log.Lines.Count(line => line.Contains("Somebody is using tty3", StringComparison.Ordinal)));

        frame.Terminals.Active = TtyTerminal.ProductTerminal;
        Assert.True(await frame.Handover.TakeAsync(None));
        Assert.Equal(TtyTerminal.AgentTerminal, frame.Terminals.Active);
    }

    [Fact]
    public async Task Finding_the_terminal_already_in_front_counts_as_holding_it()
    {
        // The kernel is asked nothing when the answer is already right, which is what keeps a
        // level-triggered loop from issuing an ioctl every couple of seconds forever.
        using var frame = new Panel(foreground: TtyTerminal.AgentTerminal);

        Assert.True(await frame.Handover.TakeAsync(None));
        Assert.Equal(ScreenOwner.Agent, frame.Handover.Held);
        Assert.Empty(frame.Terminals.Activated);
        Assert.Equal(0, frame.Handover.Handovers);
    }

    [Fact]
    public async Task The_environment_override_moves_the_switch_with_the_device()
    {
        // FL_TTY points the console stage at another device; a handover still aimed at terminal
        // eight would paint one console and reveal another, which is a blank screen with a
        // perfectly healthy log beside it.
        using var frame = new Panel(agentTerminal: 11);

        await frame.Handover.TakeAsync(None);

        Assert.Equal([11], frame.Terminals.Activated);
    }

    /// <summary>A handover over terminals nothing is actually drawing on.</summary>
    private sealed class Panel : IDisposable
    {
        public Panel(int foreground = TtyTerminal.ProductTerminal, int agentTerminal = TtyTerminal.AgentTerminal)
        {
            Terminals = new RecordingVirtualTerminals(foreground);
            Handover = new ScreenHandover(
                Terminals,
                Processes,
                Clock,
                Log,
                () => Phase,
                agentTerminal,
                TtyTerminal.ProductTerminal);
        }

        public RecordingVirtualTerminals Terminals { get; }

        public ScriptedProcessRunner Processes { get; } = new();

        public ManualClock Clock { get; } = new();

        public RecordingLog Log { get; } = new();

        public BrowserStagePhase Phase { get; set; } = BrowserStagePhase.Console;

        public ScreenHandover Handover { get; }

        /// <summary>Moves past the anti-flap window so the next decision is acted on.</summary>
        public void Settle() => Clock.UtcNow += Handover.Settle + TimeSpan.FromSeconds(1);

        public void Dispose() => Handover.Dispose();
    }
}
