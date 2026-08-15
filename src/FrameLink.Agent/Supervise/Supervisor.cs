using System.Globalization;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Local;
using FrameLink.Agent.Resources;
using FrameLink.Agent.State;
using FrameLink.Agent.Telemetry;
using FrameLink.Protocol;

namespace FrameLink.Agent.Supervise;

/// <summary>Everything supervision needs. Grouped so the constructor stays readable.</summary>
public sealed record SupervisionServices
{
    /// <summary>The page's liveness signal and the call-end event (§2.10).</summary>
    public required LocalChannel Channel { get; init; }

    /// <summary>How the browser and the camera node are restarted.</summary>
    public required IUserSession Session { get; init; }

    /// <summary>The two memory numbers.</summary>
    public required IMemoryProbe Memory { get; init; }

    /// <summary>The interlock shared with the reconciler.</summary>
    public required SupervisionInterlock Interlock { get; init; }

    /// <summary>Where the annotation is published (§2.10).</summary>
    public required AgentStatusHub Hub { get; init; }

    /// <summary>Where every action is reported (§1.2 principle 3, §4.1's <c>events</c>).</summary>
    public required IReconcileTelemetry Telemetry { get; init; }

    /// <summary>Source of time and of waiting.</summary>
    public required IAgentClock Clock { get; init; }

    /// <summary>The journal, in the second sense.</summary>
    public required IAgentLog Log { get; init; }

    /// <summary>The fleet's <c>supervision.*</c> values.</summary>
    public SupervisionSettings Settings { get; init; } = SupervisionSettings.Defaults;

    /// <summary>
    /// Where the daily restart's last run is remembered, or null to keep it in memory only.
    /// </summary>
    /// <remarks>
    /// §2.10's daily restart catches "a missed run up once after an outage", which is v1's
    /// <c>Persistent=true</c> — and <c>Persistent=true</c> works by writing a stamp to disk.
    /// Without one, a frame that was switched off across 03:00 never learns it missed anything,
    /// and every process start after 03:00 looks identical to one that has already run. It matters
    /// more in v2 than it did in v1 for a reason particular to this design: §2.4 reboots the frame
    /// once per resource, so "the process started again" is an ordinary event and not evidence of
    /// anything.
    /// </remarks>
    public IStateStore? Store { get; init; }

    /// <summary>The zone the 03:00 restart is measured in.</summary>
    /// <remarks>
    /// §2.10 says "03:00 <b>local</b>", and local means the frame's configured time zone — a
    /// setting the catalog reconciles as <c>system.timezone</c> and which §3.4 makes fleet-managed
    /// because "the 3 AM restart window and the slideshow both depend on local time". Taking UTC
    /// here would restart a Dutch frame at 04:00 or 05:00 depending on the season.
    /// </remarks>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    /// <summary>The device id stamped onto events.</summary>
    public string DeviceId { get; init; } = "unknown";
}

/// <summary>
/// <b>§2.10's second agent responsibility</b> — keeping a correctly-configured system running.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not resources, and the reason is a collision rather than a taxonomy.</b> §2.6 holds that any
/// drift stops the product, including an active call, and that is correct for drift. A supervised
/// restart is not a departure from the declared state — it <i>is</i> the declared state being kept
/// alive, and what comes back comes back into the same configuration it left. Modelling it as
/// drift would force the two rules into collision and one would have to yield: either drift stops
/// being absolute, or a routine browser blink blanks the frame, kills the call and shows a repair
/// screen every morning at 03:00.
/// </para>
/// <para>
/// <b>The distinguishing question is "is the desired state wrong, or is a correctly-configured
/// thing misbehaving?"</b> Everything here answers the second. A browser renderer that has leaked
/// 900 MB, a page that stopped answering, a camera node hung in shutdown while its unit still
/// reports <c>active</c> — every declared setting is exactly right and the running system is sick
/// anyway. No amount of level-triggered convergence finds those, because there is nothing to
/// compare against.
/// </para>
/// <para>
/// <b>Two consequences follow and are enforced here.</b> Supervision <b>never reboots</b> — its
/// entire vocabulary is restarting a supervised process, and a reboot blanks the frame for a
/// minute, which is exactly the product-stopping behaviour it must not have. And it <b>defers only
/// what can wait</b>: the daily restart stands down during a call and runs at the next
/// opportunity, while the memory watchdog defers for nothing, because the alternative to acting
/// during a call is an OOM kill or a hardware-watchdog reset, which ends that call anyway and
/// takes the frame with it.
/// </para>
/// <para>
/// It runs at full strength in <c>NoContact</c> — that is the case where no help is coming — and
/// stands down in every state where the product is not running, because the agent owns the screen
/// then and restarting a browser that is deliberately not showing the product repairs nothing.
/// </para>
/// </remarks>
public sealed class Supervisor
{
    /// <summary>Behaviour id: Chromium tree RSS or system available memory crossed a limit.</summary>
    public const string MemoryWatchdog = "memory-watchdog";

    /// <summary>Behaviour id: the scheduled restart that bounds session age.</summary>
    public const string DailyRestart = "daily-restart";

    /// <summary>Behaviour id: the page's local channel went silent.</summary>
    public const string KioskLiveness = "kiosk-liveness";

    /// <summary>Behaviour id: the prophylactic camera recycle after every call.</summary>
    public const string CameraRecycle = "camera-recycle";

    /// <summary>
    /// <c>events</c>-channel kind for one supervision action (§4.1 lists it beside drift and
    /// escalation).
    /// </summary>
    /// <remarks>
    /// Declared here rather than in <c>DeviceEventKinds</c> because that type lives in
    /// <c>FrameLink.Protocol</c>, which is <b>frozen</b> (§4.2). <see cref="DeviceEvent.Kind"/> is
    /// a free string precisely so a new kind of event does not need the envelope to move, and this
    /// is the first use of that latitude. Nothing about the wire shape changes.
    /// </remarks>
    public const string SupervisionEventKind = "supervision";

    /// <summary>
    /// <c>events</c>-channel kind for the rate-based fault of §2.10.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="SupervisionEventKind"/> because it routes differently: a
    /// supervision action lands in the per-device history, while a fault "notifies the operator
    /// through the same §3.5 path as an escalation". Same channel, different urgency.
    /// </remarks>
    public const string SupervisionFaultEventKind = "supervision-fault";

    /// <summary>The resource a browser restart transiently disturbs.</summary>
    /// <remarks>
    /// Exactly one, and it is the right one: <c>unit.chromium-kiosk.running-matches-content</c> is
    /// the resource that reads the <i>running</i> process, so it is the only one that can observe
    /// "no browser process is running" during a restart. The unit file and its enablement are
    /// untouched by a restart and must keep being checked throughout — a window that excused them
    /// too would be a hole in drift detection rather than an interlock.
    /// </remarks>
    public static IReadOnlyList<string> BrowserResources { get; } =
        [ChromiumKioskRunningResource.ResourceName];

    /// <summary>The resources a camera recycle transiently disturbs.</summary>
    /// <remarks>
    /// These were named here before the camera block existed, as strings; now that it does they are
    /// the catalog's own constants, so a renamed resource cannot leave the interlock quietly
    /// pointing at nothing. Both are needed and neither is enough: the node assertion is the one
    /// that observes a camera which is briefly gone during a recycle, and the unit content is what
    /// the reconciler holds while it rewrites the unit this restarts.
    /// </remarks>
    public static IReadOnlyList<string> CameraResources { get; } =
        [CameraNodeResource.ResourceName, CameraUnitResource.ResourceName];

    /// <summary>The camera node's user unit.</summary>
    public const string CameraUnitName = CameraUnitResource.UnitName;

    /// <summary>Where the daily restart's last local date is stamped (§2.1's persisted state).</summary>
    public const string DailyRestartStampFile = "supervision-daily-restart";

    private readonly SupervisionServices _services;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _actions = new(StringComparer.Ordinal);
    private readonly List<(SupervisionWindow Window, IReadOnlyList<string> Resources)> _watching = [];

    private DateTimeOffset? _lastMemoryCheck;
    private DateTimeOffset? _lastLivenessRestart;
    private DateOnly? _lastDailyRestart;
    private int _callEndsPending;
    private bool _callActive;

    /// <summary>Creates the supervisor.</summary>
    public Supervisor(SupervisionServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _services = services;
        services.Channel.CallEnded += NoteCallEnded;
    }

    /// <summary>How many supervision actions have been taken since the process started.</summary>
    public int Actions { get; private set; }

    /// <summary>Why the last tick did nothing, for a test and for the journal.</summary>
    public string? LastStandDown { get; private set; }

    /// <summary>Whether a call is in progress, which the daily restart defers for.</summary>
    public bool CallActive
    {
        get
        {
            lock (_gate)
            {
                return _callActive;
            }
        }
        set
        {
            lock (_gate)
            {
                _callActive = value;
            }
        }
    }

    /// <summary>The event trigger of §2.10's camera recycle.</summary>
    /// <remarks>
    /// Queued rather than acted on inline, so the action goes through the same interlock, the same
    /// reporting path and the same fault-rate counter as every health-triggered one. §2.10 is
    /// explicit that the recycle "is the same responsibility, not a second one" — two trigger
    /// kinds, one mechanism.
    /// </remarks>
    public void NoteCallEnded() => Interlocked.Increment(ref _callEndsPending);

    /// <summary>Runs one evaluation of every behaviour.</summary>
    /// <returns>How many actions this tick took.</returns>
    public async Task<int> TickAsync(CancellationToken cancellationToken)
    {
        var now = _services.Clock.UtcNow;
        LastStandDown = null;

        await ExpireWindowsAsync(now, cancellationToken).ConfigureAwait(false);
        CloseRecoveredWindows(now);

        if (!_services.Hub.Current.ProductRuns)
        {
            // "It stands down in every state where the product is not running, because the agent
            // owns the screen then and restarting a browser that is deliberately not showing the
            // product repairs nothing." Note that NoContact-while-green has ProductRuns true, so
            // this is not an "is the server reachable" test — supervision runs at full strength
            // through an outage, which is the offline half of §1.2 principle 2.
            LastStandDown = "the product is not running, so there is nothing to keep alive";
            _callEndsPending = 0;
            return 0;
        }

        var taken = 0;

        taken += await MemoryTickAsync(now, cancellationToken).ConfigureAwait(false);
        taken += await DailyTickAsync(now, cancellationToken).ConfigureAwait(false);
        taken += await LivenessTickAsync(now, cancellationToken).ConfigureAwait(false);
        taken += await CameraTickAsync(now, cancellationToken).ConfigureAwait(false);

        return taken;
    }

    /// <summary>Ticks forever, on the shortest interval any behaviour needs.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                // A supervisor that dies takes the memory watchdog with it, which is how v1 died
                // every ninety minutes. §1.2.3 forbids repairing invisibly, not surviving loudly:
                // the reason is recorded and the next tick happens on schedule.
                _services.Log.Warn($"A supervision tick failed and was skipped: {exception.Message}");
            }

            try
            {
                await _services.Clock
                    .DelayAsync(_services.Settings.KioskCheckInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<int> MemoryTickAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = _services.Settings;

        var previousCheck = _lastMemoryCheck;

        if (previousCheck is { } last && now - last < settings.MemoryCheckInterval)
        {
            return 0;
        }

        _lastMemoryCheck = now;

        var sample = await _services.Memory.SampleAsync(cancellationToken).ConfigureAwait(false);
        var overCeiling = sample.BrowserTreeRssKb > settings.BrowserTreeRssCeilingKb;

        // -1 means /proc/meminfo could not be read. Treating that as "under the floor" would
        // restart the browser on every tick of anything that is not a Linux frame.
        var underFloor = sample.MemAvailableKb >= 0 && sample.MemAvailableKb < settings.MemAvailableFloorKb;

        if (!overCeiling && !underFloor)
        {
            return 0;
        }

        var measurement = overCeiling
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"the browser is using {sample.BrowserTreeRssKb} kB across {sample.BrowserProcesses} processes, over the {settings.BrowserTreeRssCeilingKb} kB ceiling")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"only {sample.MemAvailableKb} kB of memory is available, under the {settings.MemAvailableFloorKb} kB floor");

        // The memory watchdog defers for nothing, not even an active call: the alternative to
        // acting is an OOM kill or a hardware-watchdog reset, which ends that call anyway and
        // takes the frame with it.
        if (await RestartBrowserAsync(MemoryWatchdog, measurement, now, cancellationToken).ConfigureAwait(false))
        {
            return 1;
        }

        // The interlock refused, so this sample reached no conclusion. Putting the interval back
        // is what makes the reconciler's lock a *pause* rather than a five-minute hole: a frame
        // over its ceiling would otherwise wait out the whole sampling interval after the apply it
        // stood aside for had finished, with the memory still climbing.
        _lastMemoryCheck = previousCheck;
        return 0;
    }

    private async Task<int> DailyTickAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_services.Settings.DailyRestartTime is not { } scheduled)
        {
            return 0;
        }

        var local = TimeZoneInfo.ConvertTime(now, _services.TimeZone);
        var today = DateOnly.FromDateTime(local.DateTime);
        var last = LastDailyRestart();

        if (last == today)
        {
            return 0;
        }

        if (last is null)
        {
            // Never recorded one. A frame that has never had a daily restart scheduled while it
            // was running has not *missed* one, so today is marked as taken and the first real
            // restart happens at the next crossing. Without this, every fresh agent that reaches
            // green after 03:00 local blinks its browser once for no reason — including on each
            // of the reboots §2.4 takes during provisioning.
            RecordDailyRestart(today);
            return 0;
        }

        // Owed either because today's time has come, or because a whole 03:00 went by while this
        // frame was off — the second case is exactly what Persistent=true exists for, and it fires
        // as soon as the frame is back rather than waiting another day.
        var missedAWholeDay = last < today.AddDays(-1);

        if (!missedAWholeDay && TimeOnly.FromDateTime(local.DateTime) < scheduled)
        {
            return 0;
        }

        if (CallActive)
        {
            // "The daily restart stands down while a call is active and runs at the next
            // opportunity, exactly as v1's Persistent=true catches a missed run up." Not marking
            // the day done is the whole mechanism: the next tick after the call ends finds the
            // schedule still owed and takes it.
            LastStandDown = "a call is in progress, so the daily restart waits for the next opportunity";
            return 0;
        }

        RecordDailyRestart(today);

        return await RestartBrowserAsync(
            DailyRestart,
            $"the scheduled {scheduled:HH\\:mm} restart is due",
            now,
            cancellationToken).ConfigureAwait(false) ? 1 : 0;
    }

    private async Task<int> LivenessTickAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = _services.Settings;

        if (_services.Channel.LastCheckInUtc is not { } lastCheckIn)
        {
            // The page has never checked in during this process. That is §2.7's fallback rule's
            // business — a browser that never rendered at all — and BrowserStage owns it, because
            // the answer there is to tear the session down rather than to restart it forever.
            return 0;
        }

        var silence = now - lastCheckIn;
        if (silence < settings.KioskSilenceTimeout)
        {
            return 0;
        }

        if (_lastLivenessRestart is { } previous && now - previous < settings.KioskRestartCooldown)
        {
            LastStandDown = "the browser was restarted recently, so the liveness check is in its cooldown";
            return 0;
        }

        var measurement = string.Create(
            CultureInfo.InvariantCulture,
            $"the page has said nothing for {(int)silence.TotalSeconds} s, past the {(int)settings.KioskSilenceTimeout.TotalSeconds} s limit");

        if (!await RestartBrowserAsync(KioskLiveness, measurement, now, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        _lastLivenessRestart = now;
        return 1;
    }

    private async Task<int> CameraTickAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pending = Interlocked.Exchange(ref _callEndsPending, 0);

        if (pending == 0)
        {
            return 0;
        }

        if (!_services.Settings.CameraRestartOnCallEnd)
        {
            // "PipeWire ≥ 1.6.0 guards that path; when the OS carries it, this behaviour is
            // switched off by setting, then deleted." The setting is the switch.
            return 0;
        }

        if (_services.Interlock.FirstHeld(CameraResources) is { } held)
        {
            LastStandDown = $"the reconciler is working on '{held}', so the camera was left alone";
            return 0;
        }

        var window = _services.Interlock.Open(
            CameraRecycle,
            CameraResources,
            now,
            _services.Settings.RecoveryDeadline);

        Watch(window, CameraResources);

        var result = await _services.Session
            .RunAsync("systemctl", ["--user", "restart", CameraUnitName], cancellationToken)
            .ConfigureAwait(false);

        await RecordAsync(
            CameraRecycle,
            "a call ended",
            $"systemctl --user restart {CameraUnitName}" + (result.Succeeded ? string.Empty : $" (refused: {result.Combined})"),
            now,
            cancellationToken).ConfigureAwait(false);

        return 1;
    }

    /// <summary>Restarts the browser, unless §2.10 clause 1 says the reconciler owns it.</summary>
    private async Task<bool> RestartBrowserAsync(
        string behaviour,
        string measurement,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_services.Interlock.FirstHeld(BrowserResources) is { } held)
        {
            // Clause 1, verbatim: "Restarting a browser the reconciler is deliberately holding
            // down, or racing an apply, produces exactly the interference that makes 'which change
            // broke it' unanswerable."
            LastStandDown = $"the reconciler is working on '{held}', so the browser was left alone";
            _services.Log.Info($"Supervision stood down: {LastStandDown}");
            return false;
        }

        var window = _services.Interlock.Open(behaviour, BrowserResources, now, _services.Settings.RecoveryDeadline);
        Watch(window, BrowserResources);

        // The page that was there is gone. Forgetting its last check-in is what stops the liveness
        // rule measuring the new browser's silence against the old browser's heartbeat, which
        // would fire again one interval later and again after that.
        _services.Channel.Forget();

        var result = await _services.Session
            .RunAsync("systemctl", ["--user", "restart", ChromiumKioskUnitResource.UnitName], cancellationToken)
            .ConfigureAwait(false);

        await RecordAsync(
            behaviour,
            measurement,
            $"systemctl --user restart {ChromiumKioskUnitResource.UnitName}"
                + (result.Succeeded ? string.Empty : $" (refused: {result.Combined})"),
            now,
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Reports one action and updates the rate-based fault state (§2.10).
    /// </summary>
    /// <remarks>
    /// <b>Rate, not budget, and deliberately not §2.5's ladder.</b> That ladder ends in
    /// <c>Halted</c> — stop touching it — which is the right terminal state for a resource that
    /// cannot be applied and the wrong one for a frame that needs restarting to stay alive: giving
    /// up there means a dark frame. Each action here is individually legitimate; the abnormality
    /// is the frequency. So the fault <b>never inhibits supervision</b> — the restarts continue,
    /// because a frame restarting every ten minutes still beats a dark one — and escalation here
    /// is diagnostic where §2.5's is inhibitory.
    /// </remarks>
    private async Task RecordAsync(
        string behaviour,
        string measurement,
        string action,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Actions++;

        int rate;
        var window = _services.Settings.FaultRateWindow;

        lock (_gate)
        {
            if (!_actions.TryGetValue(behaviour, out var history))
            {
                history = [];
                _actions[behaviour] = history;
            }

            history.RemoveAll(at => now - at > window);
            history.Add(now);
            rate = history.Count;
        }

        var fault = rate > _services.Settings.FaultRateThreshold;

        _services.Log.Info($"Supervision ({behaviour}): {measurement}. {action}");

        // §2.10: "A supervised restart while InSync leaves the device InSync." The annotation is
        // published beside the condition and never instead of it, which is what makes it an
        // annotation rather than a rung.
        _services.Hub.Publish(status => status with
        {
            Supervision = new SupervisionAnnotation
            {
                Behaviour = behaviour,
                LastActionUtc = now,
                ActionsInWindow = rate,
                AtFaultLevel = fault,
                Detail = measurement,
            },
        });

        await _services.Telemetry.EventAsync(
            new DeviceEvent
            {
                DeviceId = _services.DeviceId,
                Kind = SupervisionEventKind,
                OccurredUtc = now,
                Resource = behaviour,
                Summary = $"{measurement}; {action}",
                Delta = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{rate} {behaviour} actions in the last {(int)window.TotalMinutes} minutes"),
                Attempts = rate,
            },
            cancellationToken).ConfigureAwait(false);

        if (fault)
        {
            _services.Log.Fail(string.Create(
                CultureInfo.InvariantCulture,
                $"Supervision fault: {behaviour} has acted {rate} times in {(int)window.TotalMinutes} minutes. The restarts continue; somebody has to look at this frame."));

            await _services.Telemetry.EventAsync(
                new DeviceEvent
                {
                    DeviceId = _services.DeviceId,
                    Kind = SupervisionFaultEventKind,
                    OccurredUtc = now,
                    Resource = behaviour,
                    Summary = "This frame keeps needing the same repair. Every repair was correct; the frequency is not.",
                    Delta = string.Create(
                        CultureInfo.InvariantCulture,
                        $"{rate} actions against a threshold of {_services.Settings.FaultRateThreshold}"),
                    Attempts = rate,
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Closes windows whose subject came back healthy (§2.10 clause 3).</summary>
    private void CloseRecoveredWindows(DateTimeOffset now)
    {
        (SupervisionWindow Window, IReadOnlyList<string> Resources)[] watching;
        lock (_gate)
        {
            watching = [.. _watching];
        }

        foreach (var entry in watching)
        {
            var healthy = ReferenceEquals(entry.Resources, CameraResources)
                || _services.Channel.LastCheckInUtc is { } checkIn && checkIn > entry.Window.OpenedUtc;

            if (!healthy)
            {
                continue;
            }

            _services.Interlock.Close(entry.Window);

            lock (_gate)
            {
                _watching.Remove(entry);
            }

            _services.Log.Info(string.Create(
                CultureInfo.InvariantCulture,
                $"Supervision ({entry.Window.Behaviour}): recovered in {(int)(now - entry.Window.OpenedUtc).TotalSeconds} s."));
        }
    }

    /// <summary>Lets expired windows fall through to ordinary drift (§2.10 clause 3).</summary>
    private async Task ExpireWindowsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var window in _services.Interlock.Expire(now))
        {
            lock (_gate)
            {
                _watching.RemoveAll(entry => ReferenceEquals(entry.Window, window));
            }

            _services.Log.Warn(
                $"Supervision ({window.Behaviour}): nothing recovered inside the deadline, so this is now ordinary drift and the reconciler owns it.");

            await _services.Telemetry.EventAsync(
                new DeviceEvent
                {
                    DeviceId = _services.DeviceId,
                    Kind = SupervisionEventKind,
                    OccurredUtc = now,
                    Resource = window.Behaviour,
                    Summary = "A supervised restart did not recover inside its deadline, so this has become ordinary drift.",
                    Delta = $"window over {string.Join(", ", window.Resources)} expired",
                    Attempts = 0,
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private void Watch(SupervisionWindow window, IReadOnlyList<string> resources)
    {
        lock (_gate)
        {
            _watching.Add((window, resources));
        }
    }

    /// <summary>The local date the daily restart last ran, from memory or from the stamp.</summary>
    private DateOnly? LastDailyRestart()
    {
        if (_lastDailyRestart is { } remembered)
        {
            return remembered;
        }

        if (_services.Store?.ReadText(DailyRestartStampFile)?.Trim() is { Length: > 0 } stamp
            && DateOnly.TryParse(stamp, CultureInfo.InvariantCulture, out var parsed))
        {
            _lastDailyRestart = parsed;
            return parsed;
        }

        return null;
    }

    private void RecordDailyRestart(DateOnly day)
    {
        _lastDailyRestart = day;
        _services.Store?.WriteText(DailyRestartStampFile, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }
}
