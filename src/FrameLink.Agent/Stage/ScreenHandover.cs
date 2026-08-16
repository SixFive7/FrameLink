using System.Globalization;
using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Stage;

/// <summary>Which of §2.7's two stages the panel is showing.</summary>
public enum ScreenOwner
{
    /// <summary>The agent's own virtual terminal, carrying the console stage.</summary>
    Agent,

    /// <summary>The autologin session's terminal, carrying the compositor and the browser stage.</summary>
    Product,
}

/// <summary>
/// <b>Moves the panel between §2.7's two stages</b> — the agent's console terminal and the
/// product's.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this replaces.</b> Both stages used to share <c>/dev/tty1</c>, and §2.7 called that a
/// reveal rather than a switch: the console never stopped painting and labwc simply drew over it.
/// That worked from the moment a compositor existed and not one second before it. Until then the
/// other program on <c>tty1</c> was <c>agetty</c>, and it won — a repair screen visible for under a
/// second before a login prompt replaced it, measured on the frame with both processes holding the
/// device. The fault window is exactly the provisioning hour, which is the one hour in a frame's
/// life when the narration is all it has to say for itself.
/// </para>
/// <para>
/// <b>The rule reproduces the old reveal exactly, and adds the hour it never covered.</b> The
/// console keeps the panel while no compositor is running, and hands it over the moment one is —
/// which is precisely the instant labwc used to draw over the console on the shared terminal. So on
/// a converged frame the visible behaviour is unchanged, down to the second, and §2.6's "the screen
/// belongs to the agent whenever anything is not green" is satisfied where it always was: on the
/// browser surface, rendering <i>the same narration from the same status hub</i>. What is new is
/// only what happens when there is no compositor — the provisioning hour, and the seconds after a
/// compositor dies — where the console now has the panel to itself instead of sharing it with
/// <c>agetty</c>.
/// </para>
/// <para>
/// <b>Yielding to a live compositor is not politeness, it is the thing that keeps the frame able to
/// converge.</b> A Wayland compositor whose terminal is in the background has an inactive logind
/// session, holds no DRM master, and fails every output commit — so it cannot present a page and it
/// cannot apply <c>display.dsi2-transform</c>. A handover that kept the panel while labwc ran would
/// make that resource fail on every boot, and §2.6 turns a resource that fails on every boot into a
/// reboot loop. The compositor is therefore never left backgrounded except in the one case where it
/// is about to be killed anyway: <see cref="BrowserStagePhase.TornDown"/>.
/// </para>
/// <para>
/// <b>Level-triggered, like everything else here (§2.2).</b> The loop compares what should be in
/// front against what was last <i>confirmed</i> in front, so a switch the kernel dropped, a switch
/// that lost a race, and a switch that never happened all self-correct on the next tick. An
/// edge-triggered handover would have exactly one attempt per transition and no way to notice it
/// failed.
/// </para>
/// </remarks>
public sealed class ScreenHandover : IDisposable
{
    /// <summary>The compositor whose presence hands the panel back.</summary>
    public const string CompositorProcess = "labwc";

    private readonly IVirtualTerminals _terminals;
    private readonly IProcessRunner _processes;
    private readonly IAgentClock _clock;
    private readonly IAgentLog _log;
    private readonly Func<BrowserStagePhase> _phase;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DateTimeOffset _nextAttemptUtc = DateTimeOffset.MinValue;
    private DateTimeOffset? _wantedAgentSince;
    private int? _standingAsideOn;
    private bool _reportedUnconfirmed;

    /// <summary>Creates a handover over <paramref name="terminals"/>.</summary>
    /// <param name="terminals">The seam onto the kernel's consoles.</param>
    /// <param name="processes">Where <c>pgrep</c> is run to find the compositor.</param>
    /// <param name="clock">Source of time, and of the confirmation deadline.</param>
    /// <param name="log">The journal.</param>
    /// <param name="phase">Where §2.7's second stage currently stands.</param>
    /// <param name="agentTerminal">The console stage's terminal.</param>
    /// <param name="productTerminal">The autologin session's terminal.</param>
    public ScreenHandover(
        IVirtualTerminals terminals,
        IProcessRunner processes,
        IAgentClock clock,
        IAgentLog? log = null,
        Func<BrowserStagePhase>? phase = null,
        int agentTerminal = TtyTerminal.AgentTerminal,
        int productTerminal = TtyTerminal.ProductTerminal)
    {
        ArgumentNullException.ThrowIfNull(terminals);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(agentTerminal);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(productTerminal);

        _terminals = terminals;
        _processes = processes;
        _clock = clock;
        _log = log ?? NullLog.Instance;
        _phase = phase ?? (static () => BrowserStagePhase.Console);

        AgentTerminal = agentTerminal;
        ProductTerminal = productTerminal;
    }

    /// <summary>The console stage's terminal.</summary>
    public int AgentTerminal { get; }

    /// <summary>The autologin session's terminal.</summary>
    public int ProductTerminal { get; }

    /// <summary>How often the loop re-asks whose the panel should be.</summary>
    /// <remarks>
    /// One <c>pgrep -x labwc</c> per tick, which is the same signal
    /// <c>session.bash-profile-exec-labwc</c> already treats as authoritative for "is the
    /// compositor up" and is therefore the one already proven on this hardware. Two seconds is
    /// chosen against that cost rather than against responsiveness: nothing here needs to be fast,
    /// because the one moment that must be instant — the fallback rule taking the screen before it
    /// stops the getty — is an explicit <see cref="TakeAsync"/> that does not wait for a tick.
    /// </remarks>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a requested switch has to actually happen before it is called failed.</summary>
    public TimeSpan SwitchDeadline { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long the panel is left alone after any attempt.</summary>
    /// <remarks>
    /// The anti-flap. Every switch away from a compositor makes it drop DRM master and every
    /// switch back makes it take it again and repaint, so a status that oscillates would strobe the
    /// panel and hammer the acquire path — which is where a black frame nobody can explain comes
    /// from. One attempt per settle period, whatever happens upstream.
    /// </remarks>
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long the compositor has to be gone before the console covers it.</summary>
    /// <remarks>
    /// Asymmetric on purpose: instant to give the screen back, slow to take it. The compositor has
    /// no restart policy — the thing that restarts it is <c>getty@tty1</c> respawning the login
    /// that execs it — so a compositor that has just died reappears a second or two later, and
    /// without this the panel would drop to a text console and back for every one of them. It does
    /// not delay the first take at boot, because nothing has been confirmed in front yet, and it
    /// does not delay a teardown, because that takes the screen explicitly.
    /// </remarks>
    public TimeSpan CoverAfter { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Whose the panel was last <i>confirmed</i> to be, or null if never confirmed.</summary>
    public ScreenOwner? Held { get; private set; }

    /// <summary>How many confirmed switches this process has made.</summary>
    public int Handovers { get; private set; }

    /// <summary>
    /// Whether switching is possible at all on this machine.
    /// </summary>
    /// <remarks>
    /// False once the kernel has refused the request outright, which on a frame means there are no
    /// virtual consoles to switch between. Demoted for the life of the process, and reported once
    /// rather than twice a second, for the same reason <see cref="ConsoleStage.CanWrite"/> is: the
    /// answer cannot change without a reboot, and a per-tick version of the line is a journal
    /// nobody can read.
    /// </remarks>
    public bool Switchable { get; private set; } = true;

    /// <summary>Whose the panel should be.</summary>
    /// <param name="compositorRunning">Whether a compositor holds the product's terminal.</param>
    /// <param name="phase">Where §2.7's second stage stands.</param>
    /// <remarks>
    /// The whole policy, in one expression that reads a boolean and an enum and touches nothing.
    /// A live compositor gets its terminal, because it is unusable without it and because that is
    /// exactly when it used to draw over the shared console anyway. The one exception is a session
    /// the fallback rule has already condemned: there the compositor is seconds from being stopped,
    /// so backgrounding it costs nothing and taking the panel first is what keeps the login prompt
    /// off the screen in between.
    /// </remarks>
    public static ScreenOwner Decide(bool compositorRunning, BrowserStagePhase phase) =>
        compositorRunning && phase is not BrowserStagePhase.TornDown
            ? ScreenOwner.Product
            : ScreenOwner.Agent;

    /// <summary>Brings the agent's console to the front.</summary>
    public Task<bool> TakeAsync(CancellationToken cancellationToken) =>
        SwitchAsync(ScreenOwner.Agent, cancellationToken);

    /// <summary>Brings the product's terminal to the front.</summary>
    public Task<bool> GiveBackAsync(CancellationToken cancellationToken) =>
        SwitchAsync(ScreenOwner.Product, cancellationToken);

    /// <summary>Brings <paramref name="owner"/>'s terminal to the front and waits for it to be there.</summary>
    public async Task<bool> SwitchAsync(ScreenOwner owner, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await SwitchCoreAsync(owner, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Asks who the panel should belong to and makes it so, once.</summary>
    /// <returns>Whose the panel is now confirmed to be, or null if that is not known.</returns>
    public async Task<ScreenOwner?> ReconcileAsync(CancellationToken cancellationToken)
    {
        if (!Switchable)
        {
            return Held;
        }

        var wanted = Decide(await CompositorIsRunningAsync(cancellationToken).ConfigureAwait(false), _phase());
        var now = _clock.UtcNow;

        _wantedAgentSince = wanted is ScreenOwner.Agent ? _wantedAgentSince ?? now : null;

        if (wanted == Held || now < _nextAttemptUtc)
        {
            return Held;
        }

        // The grace only ever delays covering a product that was genuinely in front. A frame that
        // has confirmed nothing yet — every boot, before the first switch — takes the screen at
        // once, which is the whole point of the move.
        if (wanted is ScreenOwner.Agent
            && Held is ScreenOwner.Product
            && _wantedAgentSince is { } since
            && now - since < CoverAfter)
        {
            return Held;
        }

        await SwitchAsync(wanted, cancellationToken).ConfigureAwait(false);
        _nextAttemptUtc = _clock.UtcNow + Settle;
        return Held;
    }

    /// <summary>Keeps the panel matching §2.7's phase until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!Switchable)
            {
                // Nothing left to reconcile. Returning frees the loop rather than waking every
                // couple of seconds to re-ask a question whose answer cannot change — and, since
                // every tick is a pgrep, to fork a process for it. The console stage does exactly
                // this once its terminal stops taking bytes.
                return;
            }

            try
            {
                await ReconcileAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                // §1.2.2 again: a frame provisions and self-heals with the server unreachable, and
                // a panel showing the wrong terminal is a strictly smaller problem than that. This
                // loop is awaited beside the reconciler, so a throw here would take the agent down.
                _log.Warn($"A screen handover tick failed and was skipped: {exception.Message}");
            }

            try
            {
                await _clock.DelayAsync(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Whether a compositor holds the product's terminal.</summary>
    /// <remarks>
    /// <c>pgrep -x labwc</c>, exactly as <c>session.bash-profile-exec-labwc</c> observes it. A
    /// process check rather than a unit check, because there is no unit: the compositor is what the
    /// autologin shell <c>exec</c>s, so systemd knows it only as <c>getty@tty1</c>. Any failure to
    /// run <c>pgrep</c> at all answers "no compositor", which errs towards the console keeping the
    /// panel — the surface that is definitely being painted.
    /// </remarks>
    private async Task<bool> CompositorIsRunningAsync(CancellationToken cancellationToken)
    {
        var result = await _processes
            .RunAsync("pgrep", ["-x", CompositorProcess], cancellationToken)
            .ConfigureAwait(false);

        return result.Succeeded;
    }

    private async Task<bool> SwitchCoreAsync(ScreenOwner owner, CancellationToken cancellationToken)
    {
        if (!Switchable)
        {
            return false;
        }

        var target = owner is ScreenOwner.Agent ? AgentTerminal : ProductTerminal;
        var foreground = _terminals.Foreground();

        if (foreground == target)
        {
            Held = owner;
            _standingAsideOn = null;
            return true;
        }

        // §5.5's recovery path, protected. Somebody who pressed Ctrl+Alt+F2 to log in by hand is
        // holding a terminal that is neither of ours, and pulling the panel out from under them is
        // precisely what keeping getty on tty1 was supposed to make impossible. Stand aside and
        // say so; the next tick after they switch back resumes on its own.
        if (foreground is { } current && current != AgentTerminal && current != ProductTerminal)
        {
            // Neither terminal is in front, so nothing is held. Saying so rather than keeping the
            // last answer matters downstream: the browser stage suppresses its check-in deadline
            // only on a *positive* "the agent's console is what the panel is showing", so an
            // unknown panel leaves §2.7's fallback rule armed. Ambiguity must never be the thing
            // that quietly switches the rule off.
            Held = null;

            if (_standingAsideOn != current)
            {
                _standingAsideOn = current;
                _log.Info(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Somebody is using tty{current}, so the screen is being left where they put it. "
                    + $"The agent's narration is on tty{AgentTerminal} (Ctrl+Alt+F{AgentTerminal}) until they switch back."));
            }

            return false;
        }

        _standingAsideOn = null;

        if (!_terminals.Activate(target))
        {
            Switchable = false;
            _log.Warn(
                "This machine will not switch virtual terminals, so the console stage stays on "
                + "whichever one is in front. Nothing further will be attempted and this is said "
                + "once rather than on every tick; the agent keeps reconciling.");
            return false;
        }

        // Confirmed against the kernel's own answer, never against the ioctl's return code. The
        // request is accepted immediately; the switch completes only once the process holding the
        // outgoing terminal has released it, and on a converged frame that process is a Wayland
        // compositor dropping DRM master. That handshake is exactly what makes this safe in the
        // other direction too: the switch back to the product is what *causes* the compositor to
        // reacquire, so there is no window in which a switch can race an acquire already under way.
        var deadline = _clock.UtcNow + SwitchDeadline;

        while (true)
        {
            if (_terminals.Foreground() == target)
            {
                Held = owner;
                Handovers++;
                _reportedUnconfirmed = false;

                if (owner is ScreenOwner.Product)
                {
                    // The grace timer starts again from scratch the next time the console wants the
                    // panel, so a compositor that comes back does not carry the last outage's clock.
                    _wantedAgentSince = null;
                }

                _log.Info(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The screen is now the {(owner is ScreenOwner.Agent ? "agent's" : "product's")} "
                    + $"— tty{target} is in front."));

                return true;
            }

            if (_clock.UtcNow >= deadline)
            {
                break;
            }

            try
            {
                await _clock.DelayAsync(ConfirmInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        if (!_reportedUnconfirmed)
        {
            _reportedUnconfirmed = true;
            _log.Warn(string.Create(
                CultureInfo.InvariantCulture,
                $"The kernel took the request to show tty{target} but "
                + $"{LinuxVirtualTerminals.ForegroundPath} never said it happened within "
                + $"{(int)SwitchDeadline.TotalSeconds} s. Something is holding the screen. The "
                + $"attempt repeats, and this is said once rather than every time."));
        }

        return false;
    }

    private static TimeSpan ConfirmInterval => TimeSpan.FromMilliseconds(100);

    /// <inheritdoc/>
    /// <remarks>
    /// Releases the gate that serialises the two callers, and nothing else. The terminals belong to
    /// whoever created them, and the panel is deliberately left showing whatever it was showing —
    /// the agent stopping is not a reason to move a screen somebody may be reading.
    /// </remarks>
    public void Dispose() => _gate.Dispose();
}
