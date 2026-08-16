using System.Globalization;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Local;
using FrameLink.Agent.Resources;
using FrameLink.Agent.State;
using FrameLink.Agent.Supervise;
using FrameLink.Agent.Telemetry;
using FrameLink.Protocol;

namespace FrameLink.Agent.Stage;

/// <summary>Where §2.7's two-stage rendering currently stands.</summary>
public enum BrowserStagePhase
{
    /// <summary>No graphical stack is up. The console stage is the whole screen.</summary>
    Console,

    /// <summary>The GUI has started and the page's first check-in is being waited for.</summary>
    Awaiting,

    /// <summary>The page checked in. The browser is rendering the agent's screen.</summary>
    Live,

    /// <summary>The fallback rule fired. The session is down and the console is narrating.</summary>
    TornDown,
}

/// <summary>Everything the browser stage needs.</summary>
public sealed record BrowserStageServices
{
    /// <summary>Where the page checks in (§2.7 item 3).</summary>
    public required LocalChannel Channel { get; init; }

    /// <summary>The user manager holding the browser unit.</summary>
    public required IUserSession Session { get; init; }

    /// <summary>The system manager holding <c>getty@tty1</c>.</summary>
    public required ISystemControl SystemControl { get; init; }

    /// <summary>Where the phase and the narration are published.</summary>
    public required AgentStatusHub Hub { get; init; }

    /// <summary>Where a teardown is reported (§1.2 principle 3).</summary>
    public required IReconcileTelemetry Telemetry { get; init; }

    /// <summary>Source of time.</summary>
    public required IAgentClock Clock { get; init; }

    /// <summary>The journal.</summary>
    public required IAgentLog Log { get; init; }

    /// <summary>The interlock, so a deliberate teardown is not read as drift.</summary>
    public SupervisionInterlock? Interlock { get; init; }

    /// <summary>
    /// The panel, so a teardown and its retry happen in an order somebody can see.
    /// </summary>
    /// <remarks>
    /// Optional, and null means the stage behaves exactly as it did when both stages shared one
    /// terminal. <see cref="ScreenHandover"/>'s own loop would reach the same end state a tick or
    /// two later on its own, but "the same end state a couple of seconds later" is a couple of
    /// seconds of login prompt on the panel, so the one moment where the order is visible to a
    /// person is ordered here rather than left to converge.
    /// </remarks>
    public ScreenHandover? Screen { get; init; }

    /// <summary>The fleet's <c>stage.*</c> values.</summary>
    public FleetValues Values { get; init; } = FleetValues.None;

    /// <summary>The device id stamped onto events.</summary>
    public string DeviceId { get; init; } = "unknown";
}

/// <summary>
/// <b>§2.7's browser stage and its fallback rule</b> — the frame stops showing a console and
/// starts showing the product, and never shows a blank desktop instead.
/// </summary>
/// <remarks>
/// <para>
/// <b>The handover is a switch now, and it used to be a reveal.</b> Both stages shared
/// <c>/dev/tty1</c>: the console stage never stopped painting, labwc drew over it, and tearing the
/// session down uncovered a console that had been current all along. What that never covered was
/// the hour before a compositor exists, when the other program on <c>tty1</c> is <c>agetty</c> and
/// it repaints its login prompt over the narration. So the console stage has a terminal of its own
/// (<see cref="TtyTerminal.AgentTerminal"/>) and <see cref="ScreenHandover"/> moves the panel
/// between the two. The browser still renders the <i>same</i> narration through
/// <see cref="LocalChannel"/> from the <i>same</i> status hub, so the two surfaces still cannot
/// disagree; what changed is only which one the panel is showing.
/// </para>
/// <para>
/// <b>The fallback rule is the part that matters.</b> "After starting the GUI the agent requires
/// the page to check in over the local channel within a short deadline. If it does not render, the
/// agent tears the graphical session down and returns to console narration explaining why. A blank
/// or broken desktop is never an acceptable state." The failure it exists for is specific and
/// silent: labwc comes up, the browser unit reports <c>active</c>, and the page is a white
/// rectangle or an "Aw, Snap!" — every unit-level check passes and the frame shows nothing. Only
/// the page can say it rendered.
/// </para>
/// <para>
/// <b>Tearing down still means stopping <c>getty@tty1</c>, not killing labwc — and one of the two
/// reasons for that has gone while a better one has arrived.</b> The reason that survives is
/// causal and untouched by separate terminals: killing the compositor achieves nothing, because the
/// login shell it replaced is respawned by the getty, <c>.bash_profile</c> execs labwc again, and
/// the frame flaps. <c>getty@tty1</c> is the thing that <i>causes</i> the graphical session, so
/// stopping it is the only way to end one. The reason that has gone is visibility: stopping the
/// getty used to be what <i>uncovered</i> the narration, and it no longer uncovers anything,
/// because the narration is on another terminal and <see cref="ScreenHandover.TakeAsync"/> is what
/// puts it in front. The reason that has arrived is that the flap is now <i>invisible</i>, which
/// makes it worse rather than better: a compositor respawning on a background terminal has an
/// inactive logind session, so it never gets DRM master, never presents, and never recovers on its
/// own — it would loop behind the repair screen, burning a 2 GB appliance's CPU with nothing on the
/// panel to suggest why. Stopping the getty is what stops that.
/// </para>
/// <para>
/// <b>What stopping it costs is bounded, and it is not §5.5's recovery path.</b> That path is a
/// person at the frame with a keyboard, and <c>systemd-logind</c>'s <c>NAutoVTs=6</c> still gives
/// them a fresh login on Ctrl+Alt+F2 through F6 for as long as the machine is up. What is
/// suspended is only the autologin that starts the product, and only until the retry starts it
/// again.
/// </para>
/// <para>
/// <b>And it retries, because "console forever" is also a broken frame.</b> After the cool-off the
/// getty is started again and the deadline is measured afresh. The interlock window opened over
/// the session resources covers exactly that span, so the reconciler does not fight the teardown
/// while it is deliberate — and when the window expires without the page ever rendering, §2.10
/// clause 3 applies as written: it becomes ordinary drift, the reconciler owns it, and the repair
/// gets the full §2.7 narration and a reboot.
/// </para>
/// </remarks>
public sealed class BrowserStage
{
    /// <summary>Fleet setting: how long the page has to render before the fallback fires.</summary>
    /// <remarks>
    /// A "short deadline" in §2.7's words, expressed as a setting because what counts as short is
    /// a property of the frame: the browser unit's own <c>ExecStartPre</c> guards already wait for
    /// the Wayland socket and the local origin, so this measures only from the moment Chromium is
    /// launched to the moment its first paint reports in.
    /// </remarks>
    public const string CheckInDeadlineKey = "stage.browserCheckInDeadline";

    /// <summary>Fleet setting: how long the console narrates before the GUI is tried again.</summary>
    public const string RetryDelayKey = "stage.browserRetryDelay";

    /// <summary>The <c>getty</c> whose autologin session <i>is</i> the graphical session.</summary>
    public const string GettyUnitName = "getty@tty1.service";

    /// <summary>
    /// The resources a deliberate teardown makes transiently wrong.
    /// </summary>
    /// <remarks>
    /// All three read the running session rather than a file: the browser process, the compositor
    /// process, and the transform on a live Wayland output. The files that produce them stay under
    /// ordinary drift detection throughout, which is the same line the supervision windows draw.
    /// </remarks>
    public static IReadOnlyList<string> SessionResources { get; } =
    [
        ChromiumKioskRunningResource.ResourceName,
        BashProfileLabwcResource.ResourceName,
        DisplayTransformResource.ResourceName,
    ];

    private readonly BrowserStageServices _services;
    private readonly IDisposable _subscription;

    private DateTimeOffset? _armedUtc;
    private DateTimeOffset? _retryAtUtc;
    private SupervisionWindow? _window;

    /// <summary>Creates the stage and starts mirroring the status hub onto the local channel.</summary>
    public BrowserStage(BrowserStageServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _services = services;

        // The mirror is the browser stage. Every publish that repaints the console also reaches
        // the page, so the two surfaces cannot disagree about what the frame is doing.
        _subscription = services.Hub.Subscribe(status =>
            _ = services.Channel.PublishAsync(Compose(status, services.Clock.UtcNow), CancellationToken.None));
    }

    /// <summary>Where the stage currently stands.</summary>
    public BrowserStagePhase Phase { get; private set; } = BrowserStagePhase.Console;

    /// <summary>How many times the fallback rule has torn the session down.</summary>
    public int Teardowns { get; private set; }

    /// <summary>Why the session was last torn down, in plain language.</summary>
    public string? TeardownReason { get; private set; }

    /// <summary>The deadline this frame gives a page to render.</summary>
    public TimeSpan CheckInDeadline =>
        SupervisionSettings.ParseDuration(_services.Values.Find(CheckInDeadlineKey)) ?? TimeSpan.FromSeconds(60);

    /// <summary>How long the console narrates before the GUI is tried again.</summary>
    public TimeSpan RetryDelay =>
        SupervisionSettings.ParseDuration(_services.Values.Find(RetryDelayKey)) ?? TimeSpan.FromMinutes(2);

    /// <summary>The narration frame a page is sent, from a status snapshot.</summary>
    /// <param name="status">What the agent knows about itself right now.</param>
    /// <param name="now">
    /// The instant the countdown's remaining time is measured against. Passed rather than read,
    /// because this composes a frame for a page and the same snapshot has to render identically on
    /// the console beside it.
    /// </param>
    public static StageMessage Compose(AgentStatus status, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(status);

        // §2.7 items 5, 7, 8 and 9, composed once in ReconcileVoice so that the page and the
        // console say the same thing about the same frame — including the part that matters most,
        // which is whether the frame has stopped. A stopped frame sends StoppedLine and no
        // ProgressLine, and the page has nothing left to animate.
        var hasStopped = ReconcileVoice.HasStopped(status);

        return new StageMessage
        {
            Condition = status.Condition.State.ToString(),
            ProductRuns = status.ProductRuns,
            Headline = ReconcileVoice.Headline(status),
            Detail = ReconcileVoice.Detail(status),
            Detected = ReconcileVoice.Detected(status),
            WhyItMatters = ReconcileVoice.WhyItMatters(status),
            Action = status.Narration.Action,
            ActionGloss = status.Narration.ActionGloss,
            Resource = status.Reconcile.Resource,
            Attempt = status.Reconcile.Attempt,
            AttemptBudget = status.Reconcile.AttemptBudget,
            ProgressLine = ReconcileVoice.ProgressLine(status),
            StoppedLine = ReconcileVoice.StoppedLine(status),
            EscalationLine = hasStopped ? status.Reconcile.EscalationLine : null,
            ContactLine = hasStopped ? ReconcileVoice.ContactLine(status.Contact) : null,
            CanRetry = hasStopped,
            CountdownSeconds = status.Reconcile.Countdown is { } countdown
                ? (int)countdown.Remaining(now).TotalSeconds
                : null,
            DeviceId = status.DeviceId,
            SupervisionOverlay = status.Supervision?.Overlay,
        };
    }

    /// <summary>Runs one evaluation of §2.7's stage 2 and its fallback rule.</summary>
    public async Task<BrowserStagePhase> TickAsync(CancellationToken cancellationToken)
    {
        var now = _services.Clock.UtcNow;

        if (Phase is BrowserStagePhase.TornDown)
        {
            if (_retryAtUtc is { } retryAt && now < retryAt)
            {
                return Phase;
            }

            await RestoreAsync(cancellationToken).ConfigureAwait(false);
            return Phase;
        }

        var running = await BrowserIsRunningAsync(cancellationToken).ConfigureAwait(false);

        if (!running)
        {
            // No GUI to require anything of. The console stage is the whole screen, exactly as it
            // is before any graphical stack exists — and with separate terminals that is now a
            // statement about which one is in front, which the handover reads off this phase.
            _armedUtc = null;
            Phase = BrowserStagePhase.Console;
            return Phase;
        }

        if (Phase is BrowserStagePhase.Console)
        {
            // "After starting the GUI" — this is that moment. Forgetting any earlier check-in is
            // what makes the deadline measure *this* browser rather than the one before it.
            _services.Channel.Forget();
            _armedUtc = now;
            Phase = BrowserStagePhase.Awaiting;
            _services.Log.Info(string.Create(
                CultureInfo.InvariantCulture,
                $"The browser is up. Waiting {(int)CheckInDeadline.TotalSeconds} s for the page to say it rendered."));
            return Phase;
        }

        if (_services.Channel.LastCheckInUtc is { } checkIn && _armedUtc is { } armed && checkIn >= armed)
        {
            if (Phase is not BrowserStagePhase.Live)
            {
                Phase = BrowserStagePhase.Live;
                CloseWindow();
                _services.Log.Info(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The page checked in after {(int)(checkIn - armed).TotalSeconds} s. The browser is now the frame's screen."));
            }

            return Phase;
        }

        if (_services.Screen is { Held: ScreenOwner.Agent })
        {
            // A page cannot fail to render on a panel it is not being shown on. A running browser
            // implies a running compositor, so the handover has normally already given the
            // product's terminal back by the time this line is reached; what it exists for is the
            // case where it cannot — somebody logged in on another terminal, which §5.5 says the
            // frame must let them do. The deadline is therefore measured from when the panel
            // actually became the product's, not from when Chromium started. Only a positive "the
            // agent's console is what is in front" counts: an unknown panel leaves the rule armed.
            _armedUtc = now;
            return Phase;
        }

        if (_armedUtc is { } startedAt && now - startedAt >= CheckInDeadline)
        {
            await TearDownAsync(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the page did not render within {(int)CheckInDeadline.TotalSeconds} s of the browser starting"),
                now,
                cancellationToken).ConfigureAwait(false);
        }

        return Phase;
    }

    /// <summary>Stops mirroring the hub.</summary>
    public void Detach() => _subscription.Dispose();

    private async Task<bool> BrowserIsRunningAsync(CancellationToken cancellationToken)
    {
        var result = await _services.Session
            .RunAsync("systemctl", ["--user", "is-active", ChromiumKioskUnitResource.UnitName], cancellationToken)
            .ConfigureAwait(false);

        return string.Equals(result.StandardOutput.Trim(), "active", StringComparison.Ordinal);
    }

    private async Task TearDownAsync(string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Teardowns++;
        TeardownReason = reason;
        Phase = BrowserStagePhase.TornDown;
        _armedUtc = null;
        _retryAtUtc = now + RetryDelay;

        // The window covers the teardown *and* the retry that follows it, so the reconciler does
        // not repair a session the agent is deliberately holding down. If the retry also fails,
        // the window expires under §2.10 clause 3 and this becomes ordinary drift — which is the
        // designed escape from "console forever".
        _window = _services.Interlock?.Open(
            "browser-stage-fallback",
            SessionResources,
            now,
            RetryDelay + CheckInDeadline);

        // Before anything is stopped, and that ordering is the whole reason this call is here
        // rather than left to the handover's next tick. Stopping the getty kills the compositor,
        // the compositor drops DRM master, and the panel falls back to whatever text is on the
        // product's terminal — a login prompt, for as long as it takes the loop to notice. Taking
        // the screen first means the compositor dies on a terminal nobody is looking at, and the
        // panel goes straight from the empty desktop to the explanation. The phase is set above
        // first so the loop already agrees with this; the two paths serialise inside the handover,
        // so whichever gets there first, the other finds the terminal already in front.
        await TakeScreenAsync(cancellationToken).ConfigureAwait(false);

        await _services.Session
            .RunAsync("systemctl", ["--user", "stop", ChromiumKioskUnitResource.UnitName], cancellationToken)
            .ConfigureAwait(false);

        var stopped = await _services.SystemControl
            .RunAsync(["stop", GettyUnitName], cancellationToken)
            .ConfigureAwait(false);

        _services.Channel.Forget();

        _services.Log.Fail(
            $"The graphical session has been taken down: {reason}. The frame is narrating on its console instead."
                + (stopped.Succeeded ? string.Empty : $" (stopping {GettyUnitName} was refused: {stopped.Output})"));

        // §2.7 item 1 and 2, on the console the teardown has just uncovered.
        _services.Hub.Publish(status => status with
        {
            Narration = new Narration
            {
                Detected = "The screen came up but nothing was drawn on it.",
                WhyItMatters = "A frame showing an empty desktop is worse than one explaining itself.",
                Action = $"systemctl --user stop {ChromiumKioskUnitResource.UnitName}; systemctl stop {GettyUnitName}",
                ActionGloss = $"Closing the empty screen and telling you what happened here instead — {reason}.",
            },
        });

        await _services.Telemetry.EventAsync(
            new DeviceEvent
            {
                DeviceId = _services.DeviceId,
                Kind = DeviceEventKinds.Display,
                OccurredUtc = now,
                Resource = ChromiumKioskUnitResource.ResourceName,
                Summary = "The graphical session was taken down because the page never rendered.",
                Delta = reason,
                Attempts = Teardowns,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RestoreAsync(CancellationToken cancellationToken)
    {
        _retryAtUtc = null;
        Phase = BrowserStagePhase.Console;

        // Nothing hands the panel back here, and that omission is the design. The handover gives
        // the product's terminal back when a compositor is actually on it, which is seconds after
        // this getty starts — so the console keeps narrating "bringing the screen back up" for
        // exactly as long as there is nothing else to show, and the switch happens when there is.
        // Handing it back at this line instead would put the panel on a terminal showing a login
        // prompt, and would hand it to a compositor that has not started yet.
        var started = await _services.SystemControl
            .RunAsync(["start", GettyUnitName], cancellationToken)
            .ConfigureAwait(false);

        _services.Log.Info(
            $"Bringing the screen back up for another try after taking it down {Teardowns} time(s)."
                + (started.Succeeded ? string.Empty : $" (starting {GettyUnitName} was refused: {started.Output})"));
    }

    private async Task TakeScreenAsync(CancellationToken cancellationToken)
    {
        if (_services.Screen is not { } screen)
        {
            return;
        }

        if (!await screen.TakeAsync(cancellationToken).ConfigureAwait(false))
        {
            // Said, not swallowed. A teardown whose narration never reached the panel is the exact
            // shape of failure §2.7's rule against blank screens exists to forbid, and the frame
            // has no way to notice it from the inside — the console is being painted either way.
            _services.Log.Warn(
                "The graphical session is being taken down but the panel could not be moved to the "
                + "agent's console, so the explanation may not be on screen. The journal and the "
                + "Fleet Manager still have it.");
        }
    }

    private void CloseWindow()
    {
        if (_window is { } window)
        {
            _services.Interlock?.Close(window);
            _window = null;
        }
    }
}
