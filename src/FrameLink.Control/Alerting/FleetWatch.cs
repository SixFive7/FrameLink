using System.Globalization;
using FrameLink.Control.Agent;
using FrameLink.Control.LiveKit;
using FrameLink.Control.Storage;
using FrameLink.Protocol;

namespace FrameLink.Control.Alerting;

/// <summary>
/// §3.5's alerting: the four conditions worth waking somebody for, evaluated on a timer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all, stated once and plainly.</b> On 2026-07-23 a LiveKit token minted
/// with a 30-day default expired. Nothing watched it. The frame retried the connection it could no
/// longer make, leaked memory doing so, and died — and the first person to find out was a family
/// member who pressed the call button. Every design choice in this file is aimed at that shape of
/// failure and no other: <i>something that was in contact went quiet</i>, and <i>something is
/// expiring</i>.
/// </para>
/// <para>
/// <b>Level-triggered, exactly like §2.2's reconciliation loop.</b> A pass computes the complete
/// set of conditions that are true right now, compares it against what the database says is
/// already open, and delivers the difference. No rule remembers anything, no rule has to be told
/// when to stop firing, and the whole thing is correct after a restart because the comparison set
/// is on disk. Edge-triggered alerting — "notify when the state changes" — is what produces alerts
/// nobody can clear and conditions nobody was told about, and it is the same argument §2.2 already
/// settled for resources.
/// </para>
/// <para>
/// <b>What is deliberately not here.</b> No metrics, no scrape endpoint, no time series, no second
/// container. §3.5's requirement is one sentence — "offline beyond a threshold is alertable" — and
/// the milestone asks for alerting, not observability. Everything a dashboard would show already
/// streams to the console over <c>/api/events</c>; what did not exist was anything that reaches a
/// person who is not looking at the console, and that is the entire gap this closes.
/// </para>
/// </remarks>
public sealed class FleetWatch(
    AlertOptions options,
    IAlertStore store,
    IAlertSink sink,
    IDeviceStore devices,
    ISettingsStore settings,
    IFleetTelemetryStore telemetry,
    AgentConnectionRegistry registry,
    LiveKitService livekit,
    TimeProvider clock,
    ILogger<FleetWatch> logger) : BackgroundService
{
    /// <summary>Key of the one condition that is about this server rather than about a frame.</summary>
    public const string CallServerKey = AlertKinds.CallServerDown;

    /// <summary>
    /// When this Fleet Manager came up, which is what the call-server grace is measured from.
    /// </summary>
    /// <remarks>
    /// Stamped at construction rather than in <see cref="ExecuteAsync"/>, so the grace exists for
    /// anything that drives a sweep — including the suite. Reading the clock in the hosted-service
    /// entry point instead would have left a directly-driven <see cref="SweepAsync"/> with no grace
    /// at all, which is a behaviour difference between the tested path and the shipped one.
    /// </remarks>
    private readonly DateTimeOffset _startedUtc = clock.GetUtcNow();

    /// <summary>The options this watch was built from. Rendered on the alerts route.</summary>
    public AlertOptions Options { get; } = options;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Options.Interval, clock);

        while (!stoppingToken.IsCancellationRequested)
        {
            await SweepAsync(stoppingToken).ConfigureAwait(false);

            if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Runs one evaluation pass and delivers whatever moved. Public so a test can drive it.
    /// </summary>
    /// <remarks>
    /// Never throws. A failing alert sweep must not be able to take the Fleet Manager down: the
    /// alternative to a late notification is no photos, no adoption and no calls, which is a
    /// strictly worse outage than the one being reported.
    /// </remarks>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = clock.GetUtcNow();
            var current = await EvaluateAsync(now, cancellationToken).ConfigureAwait(false);
            var open = await store.ListOpenAsync(cancellationToken).ConfigureAwait(false);

            foreach (var stale in open.Where(entry => !current.ContainsKey(entry.Alert.Key)))
            {
                // Only tell somebody a condition ended if they were told it started. A row that
                // never delivered — an unreachable webhook, a receiver that was down — is removed
                // silently, because "resolved: a thing you never heard about" is noise.
                var announce = stale.NotifiedUtc is not null;
                var delivered = !announce || await sink
                    .DeliverAsync(
                        new AlertNotification
                        {
                            Transition = AlertTransition.Cleared,
                            Alert = stale.Alert,
                            OpenedUtc = stale.OpenedUtc,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!delivered)
                {
                    // Leave it open. The condition is already false, so the next pass tries the
                    // same delivery again; nothing is lost and nothing is duplicated.
                    continue;
                }

                await store.CloseAsync(stale.Alert.Key, cancellationToken).ConfigureAwait(false);
                logger.AlertCleared(stale.Alert.Kind, stale.Alert.Key, stale.Alert.Subject);
            }

            foreach (var alert in current.Values)
            {
                var stored = await store.OpenAsync(alert, now, cancellationToken).ConfigureAwait(false);
                if (stored.NotifiedUtc is not null)
                {
                    continue;
                }

                logger.AlertOpened(alert.Kind, alert.Key, alert.Subject, alert.Detail);

                var delivered = await sink
                    .DeliverAsync(
                        new AlertNotification
                        {
                            Transition = AlertTransition.Opened,
                            Alert = alert,
                            OpenedUtc = stored.OpenedUtc,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                if (delivered)
                {
                    await store.MarkNotifiedAsync(alert.Key, now, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.AlertSweepFailed(exception);
        }
    }

    /// <summary>
    /// Every condition that is true at <paramref name="now"/>, keyed by identity.
    /// </summary>
    /// <remarks>
    /// Public and pure with respect to storage — it reads, it never writes — so the suite can
    /// assert the rules themselves without a sink, a timer or a delivery in the way.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, FleetAlert>> EvaluateAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, FleetAlert>(StringComparer.Ordinal);

        if (CallServerDown(now) is { } down)
        {
            found[down.Key] = down;
        }

        var records = await devices.ListAsync(includeBlocked: false, cancellationToken).ConfigureAwait(false);

        foreach (var device in records.Where(record => record.State is DeviceState.Adopted))
        {
            if (Offline(device, now) is { } offline)
            {
                found[offline.Key] = offline;
            }

            if (await TokenExpiringAsync(device, now, cancellationToken).ConfigureAwait(false) is { } expiring)
            {
                found[expiring.Key] = expiring;
            }

            if (await StoppedAsync(device, cancellationToken).ConfigureAwait(false) is { } stopped)
            {
                found[stopped.Key] = stopped;
            }
        }

        return found;
    }

    /// <summary>
    /// Rule 1 — an adopted frame that was in contact has gone quiet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two conditions, and both are needed. The socket must be down <i>now</i>, because §3.5 makes
    /// presence the socket and nothing else is authoritative about it; and <c>LastSeenUtc</c> must
    /// be older than the threshold, because that value is in the database and therefore survives a
    /// redeploy. Using only the socket would alert on every frame every time this container
    /// restarts; using only the timestamp would keep alerting for half an hour after a frame came
    /// back.
    /// </para>
    /// <para>
    /// Adopted devices only. A pending frame that never came back is somebody's noise row and the
    /// reaper deletes it (§3.3); a blocked one was refused on purpose.
    /// </para>
    /// </remarks>
    private FleetAlert? Offline(DeviceRecord device, DateTimeOffset now)
    {
        if (registry.IsOnline(device.DeviceId))
        {
            return null;
        }

        var quiet = now - device.LastSeenUtc;
        if (quiet < Options.OfflineAfter)
        {
            return null;
        }

        return new FleetAlert
        {
            Key = AlertKinds.DeviceOffline + ":" + device.DeviceId,
            Kind = AlertKinds.DeviceOffline,
            Severity = AlertSeverity.Warning,
            DeviceId = device.DeviceId,
            DeviceName = device.DisplayName,
            Subject = $"{Name(device)} has gone quiet",
            Detail = "The frame has not been in contact with this Fleet Manager since "
                + $"{Stamp(device.LastSeenUtc)}, which is {Duration(quiet)} ago. It may be switched "
                + "off, off the network, or stuck. Its photos keep playing if it was green when "
                + "contact dropped.",
        };
    }

    /// <summary>
    /// Rule 2 — a frame is holding a call token that runs out soon.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the 2026-07-23 alarm, and it is deliberately a check on the renewal machinery
    /// rather than a second copy of it.</b> §3.7 mints for a year and re-mints inside the last
    /// third, on adoption, on every reconnect and after every settings write — so a frame that is
    /// reachable at all cannot arrive here. A token inside its last thirty days means the renewal
    /// path is not running for this frame, and the operator has thirty days to find out why rather
    /// than a call that fails in front of a family.
    /// </para>
    /// <para>
    /// A frame that has <i>no</i> token is not alerted on and that is not an oversight: with
    /// calling switched off, or before the first review has run, an absent token is the ordinary
    /// state and firing on it would mean every fresh adoption raises an alert that clears itself
    /// minutes later.
    /// </para>
    /// </remarks>
    private async Task<FleetAlert?> TokenExpiringAsync(
        DeviceRecord device,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var resolved = await settings.ResolveAsync(device.DeviceId, cancellationToken).ConfigureAwait(false);
        if (!resolved.Values.TryGetValue(CallProvisioning.TokenKey, out var token) || token.Length == 0)
        {
            return null;
        }

        var expiry = LiveKitToken.ExpiryOf(token);
        if (expiry is not { } expires || expires - now > Options.TokenExpiryWithin)
        {
            return null;
        }

        var expired = expires <= now;

        return new FleetAlert
        {
            Key = AlertKinds.CallTokenExpiring + ":" + device.DeviceId,
            Kind = AlertKinds.CallTokenExpiring,
            Severity = expired ? AlertSeverity.Critical : AlertSeverity.Warning,
            DeviceId = device.DeviceId,
            DeviceName = device.DisplayName,
            Subject = expired
                ? $"{Name(device)} cannot place a call — its credential has expired"
                : $"{Name(device)} has a call credential running out",
            Detail = $"The call token this frame holds {(expired ? "expired" : "expires")} on "
                + $"{Stamp(expires)}. This Fleet Manager renews tokens automatically whenever a "
                + "frame is in contact, so one getting this close means the renewal is not reaching "
                + "it. Check that the frame is online and that calling is switched on, then use the "
                + "console's call-token button to issue a new one.",
        };
    }

    /// <summary>
    /// Rule 3 — the bundled call server is not answering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one condition with no frame attached, and the one with a start-up grace. A first run
    /// fetches roughly 17 MB and writes a 50 MB executable before anything can start, so alerting
    /// immediately would page an operator about every first deploy.
    /// </para>
    /// <para>
    /// Critical rather than warning, because unlike everything else here it means a capability is
    /// unavailable <i>right now</i> for the whole fleet: every frame's slideshow keeps running and
    /// not one of them can place a call. It is also invisible from every other surface — a frame
    /// has no way to discover that the server it would dial is down until somebody presses the
    /// button.
    /// </para>
    /// </remarks>
    private FleetAlert? CallServerDown(DateTimeOffset now)
    {
        var state = livekit.State;

        // Nothing to be down. An operator who switched calling off, or who pointed this Fleet
        // Manager at their own LiveKit, has not asked this server to supervise anything.
        if (state.Mode is not LiveKitMode.Bundled)
        {
            return null;
        }

        if (state.Ready)
        {
            return null;
        }

        if (now - _startedUtc < Options.CallServerGrace)
        {
            return null;
        }

        var why = state.Problems.Count > 0
            ? string.Join(" ", state.Problems)
            : $"The last thing it did was: {state.Step}.";

        return new FleetAlert
        {
            Key = CallServerKey,
            Kind = AlertKinds.CallServerDown,
            Severity = AlertSeverity.Critical,
            Subject = "No frame in this fleet can place a call",
            Detail = "The bundled LiveKit call server is not running or is not configured, so every "
                + "frame's call button will fail. Photos and everything else are unaffected. " + why,
        };
    }

    /// <summary>
    /// Rule 4 — a frame has stopped reconciling and is waiting for a person.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Re-keyed onto the terminal escalation by decision 66</b>, which removes <c>Halted</c>
    /// from the design. It used to fire on <c>LoopStateNames.Halted</c>, and leaving it there would
    /// have silently deleted the alert rather than changed it — a rule that can never fire is
    /// exactly the shape of the 2026-07-23 post-mortem this whole component exists for.
    /// </para>
    /// <para>
    /// The condition it watches is unchanged in substance. Decision 68 makes an escalation stop
    /// the whole frame — nothing further is observed, acted on or rebooted for until a human
    /// retries — so this is still by construction a state nothing gets out of on its own, and still
    /// the one loop state that must reach a person rather than wait to be noticed on a console. The
    /// others are transient and the console already renders them live (§3.5).
    /// </para>
    /// </remarks>
    private async Task<FleetAlert?> StoppedAsync(DeviceRecord device, CancellationToken cancellationToken)
    {
        var report = await telemetry.GetReportAsync(device.DeviceId, cancellationToken).ConfigureAwait(false);

        if (report is null || !string.Equals(report.LoopState, LoopStateNames.Escalated, StringComparison.Ordinal))
        {
            return null;
        }

        var resource = report.Resources
            .FirstOrDefault(candidate => candidate.Status is ResourceStatusNames.Escalated
                or ResourceStatusNames.Degraded);

        return new FleetAlert
        {
            Key = AlertKinds.DeviceStopped + ":" + device.DeviceId,
            Kind = AlertKinds.DeviceStopped,
            Severity = AlertSeverity.Critical,
            DeviceId = device.DeviceId,
            DeviceName = device.DisplayName,
            Subject = $"{Name(device)} has stopped and needs a person",
            Detail = $"The frame gave up on {resource?.Name ?? "one of its settings"} after repeated "
                + "attempts and has stopped changing anything, including the settings it had "
                + $"already applied. {resource?.Delta ?? "The console has the detail."} It will not "
                + "recover on its own — open its page in the console and retry the resource or open "
                + $"a shell. Last report {Stamp(report.GeneratedUtc)}.",
        };
    }

    /// <summary>How a frame is named in a notification: the operator's name, else the short id.</summary>
    private static string Name(DeviceRecord device) =>
        string.IsNullOrWhiteSpace(device.DisplayName)
            ? "Frame " + (device.DeviceId.Length > 12 ? device.DeviceId[..12] : device.DeviceId)
            : "\"" + device.DisplayName + "\"";

    /// <summary>A duration a person reads, not one a machine parses.</summary>
    private static string Duration(TimeSpan span) => span switch
    {
        { TotalDays: >= 2 } => ((int)span.TotalDays).ToString(CultureInfo.InvariantCulture) + " days",
        { TotalHours: >= 2 } => ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + " hours",
        _ => ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " minutes",
    };

    /// <summary>A timestamp a person reads. Always UTC and always said so, because the operator's
    /// server, the frame and the reader are not reliably in one time zone.</summary>
    private static string Stamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
}
