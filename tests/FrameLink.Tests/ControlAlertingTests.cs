using FrameLink.Control;
using FrameLink.Control.Agent;
using FrameLink.Control.Alerting;
using FrameLink.Control.LiveKit;
using FrameLink.Control.Storage;
using FrameLink.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameLink.Tests;

/// <summary>An alert sink that remembers, and can be told to refuse.</summary>
/// <remarks>
/// Refusal is the behaviour worth having a double for at all. A delivery that fails must put the
/// alert back in the retry set rather than dropping it — §3.5 exists because a signal went missing
/// once, and a notifier that can lose one is the same bug wearing a different hat.
/// </remarks>
public sealed class RecordingAlertSink : IAlertSink
{
    private readonly List<AlertNotification> _delivered = [];
    private readonly List<AlertNotification> _attempted = [];

    /// <summary>When true, every delivery is refused.</summary>
    public bool Refuse { get; set; }

    /// <summary>Everything successfully delivered, in order.</summary>
    public IReadOnlyList<AlertNotification> Delivered => _delivered;

    /// <summary>Everything the watch tried to deliver, refusals included.</summary>
    public IReadOnlyList<AlertNotification> Attempted => _attempted;

    /// <summary>Every subject delivered, for readable assertions.</summary>
    public IEnumerable<string> Subjects => _delivered.Select(notification => notification.Alert.Subject);

    /// <inheritdoc/>
    public Task<bool> DeliverAsync(AlertNotification notification, CancellationToken cancellationToken)
    {
        _attempted.Add(notification);

        if (Refuse)
        {
            return Task.FromResult(false);
        }

        _delivered.Add(notification);
        return Task.FromResult(true);
    }
}

/// <summary>
/// §3.5's alerting, assembled over a real database and a hand-driven clock.
/// </summary>
/// <remarks>
/// A real <see cref="SqliteAlertStore"/> rather than a fake, for the reason <c>StorageFixture</c>
/// already gives: the de-duplication behaviour under test is expressed in an upsert that keeps
/// <c>opened_utc</c> and <c>notified_utc</c>, so a fake would assert the double rather than the
/// SQL that ships.
/// </remarks>
public sealed class AlertFixture : IDisposable
{
    private readonly StorageFixture _storage;

    /// <summary>Builds the watch with everything real except the sink and the clock.</summary>
    /// <param name="options">Thresholds. The shipped defaults when omitted.</param>
    /// <param name="livekitMode">Which call-server shape to present to rule 3.</param>
    public AlertFixture(AlertOptions? options = null, LiveKitMode livekitMode = LiveKitMode.Disabled)
    {
        _storage = new StorageFixture();
        Options = options ?? new AlertOptions();
        Alerts = new SqliteAlertStore(_storage.Database);
        Registry = new AgentConnectionRegistry();

        var callOptions = new LiveKitOptions
        {
            Directory = Path.Combine(Path.GetTempPath(), "framelink-tests-livekit"),
            Mode = livekitMode,
            PublicUrl = "ws://livekit.invalid:7880",
        };

        var deployment = new LiveKitDeployment(callOptions, new SqliteLiveKitStore(_storage.Database, Clock));

        var provisioning = new CallProvisioning(
            deployment,
            callOptions,
            _storage.Settings,
            _storage.Devices,
            Clock,
            NullLogger<CallProvisioning>.Instance);

        LiveKit = new LiveKitService(
            callOptions,
            deployment,
            provisioning,
            UnreachableLiveKitDownload.Instance,
            Clock,
            NullLogger<LiveKitService>.Instance);

        Watch = new FleetWatch(
            Options,
            Alerts,
            Sink,
            _storage.Devices,
            _storage.Settings,
            _storage.Telemetry,
            Registry,
            LiveKit,
            Clock,
            NullLogger<FleetWatch>.Instance);
    }

    /// <summary>The clock every rule reads.</summary>
    public TestClock Clock => _storage.Clock;

    /// <summary>The thresholds under test.</summary>
    public AlertOptions Options { get; }

    /// <summary>The open-alert table.</summary>
    public IAlertStore Alerts { get; }

    /// <summary>Where notifications land.</summary>
    public RecordingAlertSink Sink { get; } = new();

    /// <summary>Socket presence (§3.5). Empty, so every device reads as offline.</summary>
    public AgentConnectionRegistry Registry { get; }

    /// <summary>The call server rule 3 asks about.</summary>
    public LiveKitService LiveKit { get; }

    /// <summary>The service under test.</summary>
    public FleetWatch Watch { get; }

    /// <summary>Device rows.</summary>
    public IDeviceStore Devices => _storage.Devices;

    /// <summary>Settings, where a call token lives.</summary>
    public ISettingsStore Settings => _storage.Settings;

    /// <summary>Reconciliation reports, where a halt is visible.</summary>
    public IFleetTelemetryStore Telemetry => _storage.Telemetry;

    /// <summary>Registers and adopts a device, and returns its id.</summary>
    public async Task<string> AdoptAsync(string deviceId, string? name = null)
    {
        await _storage.SeeDeviceAsync(deviceId);
        await Devices.AdoptAsync(deviceId, name, TestContext.Current.CancellationToken);
        return deviceId;
    }

    /// <summary>Runs one evaluation-and-delivery pass.</summary>
    public Task SweepAsync() => Watch.SweepAsync(TestContext.Current.CancellationToken);

    /// <inheritdoc/>
    public void Dispose() => _storage.Dispose();
}

/// <summary>
/// The alerting behaviours §3.5 asks for and the 2026-07-23 post-mortem demands.
/// </summary>
public sealed class ControlAlertingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The headline behaviour: a frame that was in contact and stops being in contact produces
    /// exactly one notification, and coming back produces exactly one more.
    /// </summary>
    [Fact]
    public async Task AFrameThatGoesQuietIsAlertedOnAndComingBackClearsIt()
    {
        using var fixture = new AlertFixture();
        await fixture.AdoptAsync("device-quiet", "Douwe");

        // Inside the threshold: nothing at all. A frame rebooting between resources (§2.4) must
        // never page anybody.
        fixture.Clock.Advance(TimeSpan.FromMinutes(20));
        await fixture.SweepAsync();
        Assert.Empty(fixture.Sink.Delivered);

        fixture.Clock.Advance(TimeSpan.FromMinutes(20));
        await fixture.SweepAsync();

        var opened = Assert.Single(fixture.Sink.Delivered);
        Assert.Equal(AlertTransition.Opened, opened.Transition);
        Assert.Equal(AlertKinds.DeviceOffline, opened.Alert.Kind);
        Assert.Contains("Douwe", opened.Alert.Subject, StringComparison.Ordinal);

        // The frame comes back. RecordContactAsync is what a proven handshake does.
        await fixture.Devices.RecordContactAsync(
            new DeviceContact
            {
                DeviceId = "device-quiet",
                PublicKey = "key",
                ProtocolVersion = ProtocolConstants.Version,
            },
            100,
            Token);

        await fixture.SweepAsync();

        Assert.Equal(2, fixture.Sink.Delivered.Count);
        Assert.Equal(AlertTransition.Cleared, fixture.Sink.Delivered[1].Transition);
        Assert.Empty(await fixture.Alerts.ListOpenAsync(Token));
    }

    /// <summary>
    /// A condition that stays true is delivered once, not once per pass.
    /// </summary>
    /// <remarks>
    /// The property that decides whether the channel is one an operator keeps switched on. A frame
    /// away for repair is offline for a fortnight; at a five-minute interval, an alerter without
    /// this would send four thousand notifications about it.
    /// </remarks>
    [Fact]
    public async Task AConditionThatStaysTrueIsDeliveredOnce()
    {
        using var fixture = new AlertFixture();
        await fixture.AdoptAsync("device-repeat");

        fixture.Clock.Advance(TimeSpan.FromHours(2));

        for (var pass = 0; pass < 5; pass++)
        {
            await fixture.SweepAsync();
            fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        }

        Assert.Single(fixture.Sink.Delivered);
    }

    /// <summary>
    /// The wording is refreshed on every pass while the delivery is not repeated.
    /// </summary>
    /// <remarks>
    /// "Out of contact for 3 hours" has to become "for 2 days" on the console, or the open-alert
    /// list is a snapshot of when the condition started rather than of what is true now.
    /// </remarks>
    [Fact]
    public async Task TheStoredWordingFollowsTheConditionWithoutReDelivering()
    {
        using var fixture = new AlertFixture();
        await fixture.AdoptAsync("device-wording");

        fixture.Clock.Advance(TimeSpan.FromHours(3));
        await fixture.SweepAsync();

        var first = Assert.Single(await fixture.Alerts.ListOpenAsync(Token));
        Assert.Contains("3 hours", first.Alert.Detail, StringComparison.Ordinal);

        fixture.Clock.Advance(TimeSpan.FromDays(4));
        await fixture.SweepAsync();

        var later = Assert.Single(await fixture.Alerts.ListOpenAsync(Token));
        Assert.Contains("4 days", later.Alert.Detail, StringComparison.Ordinal);
        Assert.Equal(first.OpenedUtc, later.OpenedUtc);
        Assert.Single(fixture.Sink.Delivered);
    }

    /// <summary>
    /// A refused delivery is retried on the next pass rather than thrown away.
    /// </summary>
    /// <remarks>
    /// The single most important behaviour in this file. A Home Assistant that is restarting when
    /// the sweep runs must cost a five-minute delay and not the alert.
    /// </remarks>
    [Fact]
    public async Task ARefusedDeliveryIsRetriedRatherThanLost()
    {
        using var fixture = new AlertFixture();
        await fixture.AdoptAsync("device-retry");

        fixture.Clock.Advance(TimeSpan.FromHours(1));

        fixture.Sink.Refuse = true;
        await fixture.SweepAsync();

        Assert.Empty(fixture.Sink.Delivered);
        Assert.Single(fixture.Sink.Attempted);
        Assert.Null(Assert.Single(await fixture.Alerts.ListOpenAsync(Token)).NotifiedUtc);

        fixture.Sink.Refuse = false;
        await fixture.SweepAsync();

        Assert.Single(fixture.Sink.Delivered);
        Assert.NotNull(Assert.Single(await fixture.Alerts.ListOpenAsync(Token)).NotifiedUtc);
    }

    /// <summary>
    /// A condition nobody was ever told about is closed silently.
    /// </summary>
    /// <remarks>
    /// "Resolved: something you never heard of" is noise, and the receiver has no way to reconcile
    /// it against anything. So the clear is announced only when the open was.
    /// </remarks>
    [Fact]
    public async Task AnUndeliveredConditionClearsWithoutAnnouncingIt()
    {
        using var fixture = new AlertFixture();
        await fixture.AdoptAsync("device-silent");

        fixture.Clock.Advance(TimeSpan.FromHours(1));

        fixture.Sink.Refuse = true;
        await fixture.SweepAsync();
        fixture.Sink.Refuse = false;

        await fixture.Devices.RecordContactAsync(
            new DeviceContact
            {
                DeviceId = "device-silent",
                PublicKey = "key",
                ProtocolVersion = ProtocolConstants.Version,
            },
            100,
            Token);

        await fixture.SweepAsync();

        Assert.Empty(fixture.Sink.Delivered);
        Assert.Empty(await fixture.Alerts.ListOpenAsync(Token));
    }

    /// <summary>
    /// Only adopted frames are watched. A pending row is somebody's noise and a blocked one was
    /// refused on purpose (§3.3).
    /// </summary>
    [Fact]
    public async Task PendingAndBlockedFramesAreNotAlertedOn()
    {
        using var fixture = new AlertFixture();

        await fixture.AdoptAsync("adopted-one");
        await fixture.Devices.RecordContactAsync(
            new DeviceContact { DeviceId = "pending-one", PublicKey = "k2", ProtocolVersion = ProtocolConstants.Version },
            100,
            Token);
        await fixture.Devices.RecordContactAsync(
            new DeviceContact { DeviceId = "blocked-one", PublicKey = "k3", ProtocolVersion = ProtocolConstants.Version },
            100,
            Token);
        await fixture.Devices.BlockAsync("blocked-one", Token);

        fixture.Clock.Advance(TimeSpan.FromDays(1));
        await fixture.SweepAsync();

        var alert = Assert.Single(fixture.Sink.Delivered);
        Assert.Equal("adopted-one", alert.Alert.DeviceId);
    }

    /// <summary>
    /// A redeploy does not page the operator about the whole fleet.
    /// </summary>
    /// <remarks>
    /// Every socket is down for a moment when this container restarts, so a rule that read only
    /// socket presence would alert on every adopted frame on every deploy. The threshold is
    /// measured against <c>LastSeenUtc</c>, which is in the database and therefore survives —
    /// so a fleet that was in contact a minute ago stays quiet with no sockets open at all.
    /// </remarks>
    [Fact]
    public async Task ARestartWithEveryFrameRecentlySeenAlertsOnNothing()
    {
        using var fixture = new AlertFixture();
        await fixture.AdoptAsync("device-a");
        await fixture.AdoptAsync("device-b");
        await fixture.AdoptAsync("device-c");

        // No connection is registered, which is exactly the state a fresh container is in.
        Assert.Equal(0, fixture.Registry.Count);

        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        await fixture.SweepAsync();

        Assert.Empty(fixture.Sink.Delivered);
        Assert.Empty(await fixture.Alerts.ListOpenAsync(Token));
    }

    /// <summary>
    /// Rule 2, and the reason this whole subsystem exists: a call token inside its last thirty
    /// days means renewal is not reaching that frame.
    /// </summary>
    [Fact]
    public async Task ACallTokenRunningOutIsAlertedOn()
    {
        using var fixture = new AlertFixture();
        var deviceId = await fixture.AdoptAsync("device-token", "Kitchen");

        var credential = new LiveKitCredential("APItest", new string('s', 40), fixture.Clock.GetUtcNow());
        var token = LiveKitToken.Mint(
            credential,
            deviceId,
            "family",
            "Kitchen",
            fixture.Clock.GetUtcNow(),
            TimeSpan.FromDays(20));

        await fixture.Settings.SetDeviceOverrideAsync(deviceId, CallProvisioning.TokenKey, token, Token);

        var found = await fixture.Watch.EvaluateAsync(fixture.Clock.GetUtcNow(), Token);
        var alert = found[AlertKinds.CallTokenExpiring + ":" + deviceId];

        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Contains("renewal is not reaching it", alert.Detail, StringComparison.Ordinal);

        // Past the expiry it becomes critical, because at that point the frame genuinely cannot
        // call — which is the 2026-07-23 state itself.
        fixture.Clock.Advance(TimeSpan.FromDays(21));
        var expired = await fixture.Watch.EvaluateAsync(fixture.Clock.GetUtcNow(), Token);
        Assert.Equal(
            AlertSeverity.Critical,
            expired[AlertKinds.CallTokenExpiring + ":" + deviceId].Severity);
    }

    /// <summary>
    /// A healthy token — one the §3.7 renewal has just written — raises nothing, and neither does
    /// a frame that has not been issued one yet.
    /// </summary>
    [Fact]
    public async Task AHealthyOrAbsentTokenRaisesNothing()
    {
        using var fixture = new AlertFixture();
        var healthy = await fixture.AdoptAsync("device-healthy");
        var untokened = await fixture.AdoptAsync("device-untokened");

        var credential = new LiveKitCredential("APItest", new string('s', 40), fixture.Clock.GetUtcNow());
        await fixture.Settings.SetDeviceOverrideAsync(
            healthy,
            CallProvisioning.TokenKey,
            LiveKitToken.Mint(credential, healthy, "family", null, fixture.Clock.GetUtcNow(), TimeSpan.FromDays(365)),
            Token);

        var found = await fixture.Watch.EvaluateAsync(fixture.Clock.GetUtcNow(), Token);

        Assert.DoesNotContain(AlertKinds.CallTokenExpiring + ":" + healthy, found.Keys);
        Assert.DoesNotContain(AlertKinds.CallTokenExpiring + ":" + untokened, found.Keys);
    }

    /// <summary>
    /// Rule 3 waits out the start-up grace, then fires — and never fires at all on a deployment
    /// that was never asked to supervise a call server.
    /// </summary>
    [Fact]
    public async Task TheCallServerIsAlertedOnOnlyAfterTheStartupGrace()
    {
        using var bundled = new AlertFixture(livekitMode: LiveKitMode.Bundled);

        var early = await bundled.Watch.EvaluateAsync(bundled.Clock.GetUtcNow(), Token);
        Assert.DoesNotContain(FleetWatch.CallServerKey, early.Keys);

        bundled.Clock.Advance(TimeSpan.FromMinutes(20));

        var late = await bundled.Watch.EvaluateAsync(bundled.Clock.GetUtcNow(), Token);
        Assert.Equal(AlertSeverity.Critical, late[FleetWatch.CallServerKey].Severity);

        using var off = new AlertFixture(livekitMode: LiveKitMode.Disabled);
        off.Clock.Advance(TimeSpan.FromDays(1));
        Assert.DoesNotContain(
            FleetWatch.CallServerKey,
            (await off.Watch.EvaluateAsync(off.Clock.GetUtcNow(), Token)).Keys);
    }

    /// <summary>
    /// Rule 4: a halted frame reaches a person, because decision 49 makes that the one state
    /// nothing recovers from on its own.
    /// </summary>
    [Fact]
    public async Task AHaltedFrameIsAlertedOn()
    {
        using var fixture = new AlertFixture();
        var deviceId = await fixture.AdoptAsync("device-halted", "Hallway");

        await fixture.Telemetry.RecordReportAsync(
            new ReconcileReport
            {
                DeviceId = deviceId,
                Sequence = 1,
                GeneratedUtc = fixture.Clock.GetUtcNow(),
                LoopState = LoopStateNames.Halted,
                InSync = 40,
                Drifted = 1,
                Blocked = 3,
                RebootsExpected = 4,
                Resources =
                [
                    new ResourceReport
                    {
                        Name = "display.dsi2-overlay",
                        Status = ResourceStatusNames.Halted,
                        Delta = "expected dtoverlay=vc4-kms-dsi-waveshare-panel, observed nothing",
                        Attempts = 3,
                    },
                ],
            },
            Token);

        var found = await fixture.Watch.EvaluateAsync(fixture.Clock.GetUtcNow(), Token);
        var alert = found[AlertKinds.DeviceHalted + ":" + deviceId];

        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Contains("display.dsi2-overlay", alert.Detail, StringComparison.Ordinal);
        Assert.Contains("Hallway", alert.Subject, StringComparison.Ordinal);
    }

    /// <summary>
    /// A converged frame produces no halt alert, so rule 4 is not simply "any report".
    /// </summary>
    [Fact]
    public async Task AConvergedFrameRaisesNoHaltAlert()
    {
        using var fixture = new AlertFixture();
        var deviceId = await fixture.AdoptAsync("device-green");

        await fixture.Telemetry.RecordReportAsync(
            new ReconcileReport
            {
                DeviceId = deviceId,
                Sequence = 1,
                GeneratedUtc = fixture.Clock.GetUtcNow(),
                LoopState = LoopStateNames.Converged,
                InSync = 44,
                Drifted = 0,
                Blocked = 0,
                RebootsExpected = 0,
                Resources = [],
            },
            Token);

        Assert.DoesNotContain(
            AlertKinds.DeviceHalted + ":" + deviceId,
            (await fixture.Watch.EvaluateAsync(fixture.Clock.GetUtcNow(), Token)).Keys);
    }

    /// <summary>The webhook body is flat, named the way a template reads it, and carries no secret.</summary>
    [Fact]
    public void TheWebhookBodyIsFlatAndCarriesNoCredential()
    {
        var body = AlertWebhookBody.From(new AlertNotification
        {
            Transition = AlertTransition.Opened,
            OpenedUtc = new DateTimeOffset(2026, 8, 16, 9, 0, 0, TimeSpan.Zero),
            Alert = new FleetAlert
            {
                Key = "device-offline:abc",
                Kind = AlertKinds.DeviceOffline,
                Severity = AlertSeverity.Warning,
                Subject = "\"Douwe\" has gone quiet",
                Detail = "detail",
                DeviceId = "abc",
                DeviceName = "Douwe",
            },
        });

        Assert.Equal("framelink-fleet-manager", body.Source);
        Assert.Equal("opened", body.Event);
        Assert.Equal("warning", body.Severity);

        var json = System.Text.Json.JsonSerializer.Serialize(body, ControlJson.Default.AlertWebhookBody);
        Assert.Contains("\"subject\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A malformed webhook URL leaves the server working and says so, rather than refusing to
    /// start (§3.2's shape, applied to alerting).
    /// </summary>
    [Fact]
    public void AMalformedWebhookIsAProblemAndNotACrash()
    {
        var options = new AlertOptions { WebhookUrl = null };

        Assert.False(options.HasWebhook);
        Assert.NotEmpty(options.Problems());
        Assert.Contains(
            options.Problems(),
            problem => problem.Contains(AlertOptions.WebhookVariable, StringComparison.Ordinal));
    }

    /// <summary>The alerts route renders the open set, and is behind the operator password.</summary>
    [Fact]
    public async Task TheAlertsRouteIsGatedAndRendersTheOpenSet()
    {
        const string password = "a-very-long-operator-password";
        await using var server = await ControlServer.StartAsync(password);

        var anonymous = await server.Client.GetAsync("/api/alerts", Token);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, anonymous.StatusCode);

        await server.SignInAsync(password);

        var response = await server.Client.GetAsync("/api/alerts", Token);
        response.EnsureSuccessStatusCode();

        var body = await response.ReadAsync(ControlJson.Default.AlertsResponse);

        Assert.Empty(body.Alerts);
        Assert.False(body.DeliveryConfigured);
        Assert.Null(body.WebhookUrl);
        Assert.Equal(30, body.OfflineAfterMinutes);
        Assert.Equal(30, body.TokenExpiryWithinDays);
        Assert.NotEmpty(body.Problems);
    }
}
