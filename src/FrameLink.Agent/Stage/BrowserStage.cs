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
/// <b>The handover is a reveal, not a switch.</b> §2.7's console stage writes directly to
/// <c>/dev/tty1</c> and never stops; the browser stage takes over because labwc draws over that
/// console, and the browser renders the <i>same</i> narration through
/// <see cref="LocalChannel"/> from the <i>same</i> status hub. Nothing hands anything off, which
/// is what makes the reverse direction free: tearing the graphical session down uncovers a console
/// that has been painting the current state all along, so the frame is narrating again in the same
/// instant the desktop disappears rather than after a stage has been restarted.
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
/// <b>Tearing down means stopping <c>getty@tty1</c>, not killing labwc.</b> Killing the compositor
/// achieves nothing: the login shell it replaced is respawned by the getty, <c>.bash_profile</c>
/// execs labwc again, and the frame flaps. <c>getty@tty1</c> is the thing that <i>causes</i> the
/// graphical session, so stopping it ends the session, frees the tty, and — because the agent
/// writes to <c>/dev/tty1</c> itself and needs no login — leaves the console narration visible.
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

        return new StageMessage
        {
            Condition = status.Condition.State.ToString(),
            ProductRuns = status.ProductRuns,
            Headline = status.Condition.Headline,
            Detail = status.Condition.Detail,
            Detected = status.Narration.Detected,
            WhyItMatters = status.Narration.WhyItMatters,
            Action = status.Narration.Action,
            ActionGloss = status.Narration.ActionGloss,
            Resource = status.Reconcile.Resource,
            Attempt = status.Reconcile.Attempt,
            AttemptBudget = status.Reconcile.AttemptBudget,
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
            // is before any graphical stack exists.
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

        var started = await _services.SystemControl
            .RunAsync(["start", GettyUnitName], cancellationToken)
            .ConfigureAwait(false);

        _services.Log.Info(
            $"Bringing the screen back up for another try after taking it down {Teardowns} time(s)."
                + (started.Succeeded ? string.Empty : $" (starting {GettyUnitName} was refused: {started.Output})"));
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
