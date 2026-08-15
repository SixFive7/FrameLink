using System.Net;
using System.Security.Cryptography;
using FrameLink.Control;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// §3.5 on the server: reconciliation reports and device events recorded, exposed on the
/// operator API, and rolled off after a month.
/// </summary>
public sealed class ControlTelemetryTests
{
    [Fact]
    public async Task A_report_a_frame_sends_is_stored_and_handed_back_verbatim()
    {
        using var storage = new StorageFixture();
        await storage.SeeDeviceAsync("AAAA-AAAA-AAAA-AAAA");

        await storage.Telemetry.RecordReportAsync(
            Report("AAAA-AAAA-AAAA-AAAA", sequence: 4),
            TestContext.Current.CancellationToken);

        var stored = await storage.Telemetry.GetReportAsync(
            "AAAA-AAAA-AAAA-AAAA",
            TestContext.Current.CancellationToken);

        Assert.NotNull(stored);
        Assert.Equal(4, stored.Sequence);
        Assert.Equal(LoopStateNames.Reconciling, stored.LoopState);
        Assert.Equal("identity.hostname", stored.Resources[0].Name);
        Assert.Equal(ResourceStatusNames.AwaitingReboot, stored.Resources[0].Status);
    }

    [Fact]
    public async Task A_report_older_than_the_one_already_stored_never_replaces_it()
    {
        // §4.1 buffers telemetry on disk when a frame is offline and drains it on reconnect, so
        // an out-of-order arrival is ordinary rather than exceptional and the newest picture has
        // to win.
        using var storage = new StorageFixture();
        await storage.SeeDeviceAsync("AAAA-AAAA-AAAA-AAAA");

        await storage.Telemetry.RecordReportAsync(Report("AAAA-AAAA-AAAA-AAAA", 9), TestContext.Current.CancellationToken);
        await storage.Telemetry.RecordReportAsync(Report("AAAA-AAAA-AAAA-AAAA", 3), TestContext.Current.CancellationToken);

        var stored = await storage.Telemetry.GetReportAsync("AAAA-AAAA-AAAA-AAAA", TestContext.Current.CancellationToken);

        Assert.Equal(9, stored!.Sequence);
    }

    [Fact]
    public async Task Events_come_back_newest_first_and_keep_their_arrival_order_within_a_second()
    {
        using var storage = new StorageFixture();
        await storage.SeeDeviceAsync("AAAA-AAAA-AAAA-AAAA");

        var at = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        foreach (var index in Enumerable.Range(1, 3))
        {
            await storage.Telemetry.RecordEventAsync(
                Event("AAAA-AAAA-AAAA-AAAA", at, $"event {index}"),
                TestContext.Current.CancellationToken);
        }

        var events = await storage.Telemetry.ListEventsAsync(
            "AAAA-AAAA-AAAA-AAAA",
            10,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, events.Count);
        Assert.Equal("event 3", events[0].Summary);
        Assert.Equal("event 1", events[2].Summary);
    }

    [Fact]
    public async Task Events_older_than_the_retention_window_are_rolled_off()
    {
        // §3.5, decision 21: one month of events and reconciliation history, then rolled off.
        using var storage = new StorageFixture();
        await storage.SeeDeviceAsync("AAAA-AAAA-AAAA-AAAA");

        var now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        await storage.Telemetry.RecordEventAsync(
            Event("AAAA-AAAA-AAAA-AAAA", now - TimeSpan.FromDays(40), "ancient"),
            TestContext.Current.CancellationToken);
        await storage.Telemetry.RecordEventAsync(
            Event("AAAA-AAAA-AAAA-AAAA", now - TimeSpan.FromDays(2), "recent"),
            TestContext.Current.CancellationToken);

        var rolled = await storage.Telemetry.ExpireEventsAsync(
            now - TimeSpan.FromDays(31),
            TestContext.Current.CancellationToken);

        var remaining = await storage.Telemetry.ListEventsAsync(
            "AAAA-AAAA-AAAA-AAAA",
            10,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, rolled);
        Assert.Single(remaining);
        Assert.Equal("recent", remaining[0].Summary);
    }

    [Fact]
    public async Task Forgetting_a_device_takes_its_whole_history_with_it()
    {
        // §3.3's decommissioning is destructive, and history that outlived the device it was
        // about would be a record nobody could interpret.
        using var storage = new StorageFixture();
        await storage.SeeDeviceAsync("AAAA-AAAA-AAAA-AAAA");
        await storage.Telemetry.RecordReportAsync(Report("AAAA-AAAA-AAAA-AAAA", 1), TestContext.Current.CancellationToken);
        await storage.Telemetry.RecordEventAsync(
            Event("AAAA-AAAA-AAAA-AAAA", DateTimeOffset.UnixEpoch, "gone soon"),
            TestContext.Current.CancellationToken);

        await storage.Devices.ForgetAsync("AAAA-AAAA-AAAA-AAAA", TestContext.Current.CancellationToken);

        Assert.Null(await storage.Telemetry.GetReportAsync("AAAA-AAAA-AAAA-AAAA", TestContext.Current.CancellationToken));
        Assert.Empty(await storage.Telemetry.ListEventsAsync("AAAA-AAAA-AAAA-AAAA", 10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_adopted_frame_reports_over_the_socket_and_the_operator_api_shows_it()
    {
        // End to end through the real pipeline: real handshake, real socket, real store, real
        // route. §7.2's outcome, not a method call.
        await using var server = await ControlServer.StartAsync("a very long operator password");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        await using (var pending = await server.ConnectAgentAsync(key))
        {
            Assert.Equal(HandshakeStatus.Pending, pending.Result.Status);
        }

        await server.SignInAsync("a very long operator password");
        var deviceId = DeviceIdentity.FingerprintOf(key.ExportSubjectPublicKeyInfo());
        await server.AdoptAsync(deviceId);

        await using var agent = await server.ConnectAgentAsync(key);
        Assert.Equal(HandshakeStatus.Ok, agent.Result.Status);

        await agent.SendReportAsync(Report(deviceId, 11));
        await agent.SendEventAsync(Event(deviceId, DateTimeOffset.UtcNow, "the panel line is absent"));

        var reconcile = await server.WaitForReconcileAsync(deviceId, report => report?.Sequence == 11);

        Assert.NotNull(reconcile.Report);
        Assert.True(reconcile.Online);
        Assert.Equal(11, reconcile.Report.Sequence);
        Assert.Equal(3, reconcile.Report.RebootsExpected);

        var events = await server.WaitForEventsAsync(deviceId, stored => stored.Count > 0);
        Assert.Contains(events.Events, item => item.Summary.Contains("panel line", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_device_id_comes_from_the_proven_socket_and_never_from_the_payload()
    {
        // The connection exists only after a handshake that proved a keypair (§3.3), so the id
        // the server binds to is authenticated and the one inside the message is merely claimed.
        // Believing the claim would let any adopted frame write history onto any other.
        await using var server = await ControlServer.StartAsync("a very long operator password");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        await using (var pending = await server.ConnectAgentAsync(key))
        {
            Assert.Equal(HandshakeStatus.Pending, pending.Result.Status);
        }

        await server.SignInAsync("a very long operator password");
        var deviceId = DeviceIdentity.FingerprintOf(key.ExportSubjectPublicKeyInfo());
        await server.AdoptAsync(deviceId);

        await using var agent = await server.ConnectAgentAsync(key);
        await agent.SendReportAsync(Report("SOME-BODY-ELSE-0001", 5));

        var mine = await server.WaitForReconcileAsync(deviceId, report => report is not null);

        Assert.NotNull(mine.Report);
        Assert.Equal(deviceId, mine.Report.DeviceId);

        var forged = await server.Client.GetAsync(
            "/api/devices/SOME-BODY-ELSE-0001/reconcile",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, forged.StatusCode);
    }

    [Fact]
    public async Task A_known_device_with_no_report_yet_answers_with_a_null_report_rather_than_a_404()
    {
        // "Adopted a second ago and has not reported" is a real state the live screen has to
        // render, and it is not an error.
        await using var server = await ControlServer.StartAsync("a very long operator password");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        await using (var pending = await server.ConnectAgentAsync(key))
        {
            Assert.Equal(HandshakeStatus.Pending, pending.Result.Status);
        }

        await server.SignInAsync("a very long operator password");
        var deviceId = DeviceIdentity.FingerprintOf(key.ExportSubjectPublicKeyInfo());

        var response = await server.GetReconcileAsync(deviceId);

        Assert.Null(response.Report);
        Assert.Equal(deviceId, response.DeviceId);
    }

    [Fact]
    public async Task An_unreadable_payload_is_dropped_without_taking_the_connection_with_it()
    {
        // §4.2 freezes the envelope precisely so that a newer or damaged peer stays legible.
        // Hanging up over one bad report would take a working frame offline for the duration of
        // a bug.
        await using var server = await ControlServer.StartAsync("a very long operator password");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        await using (var pending = await server.ConnectAgentAsync(key))
        {
            Assert.Equal(HandshakeStatus.Pending, pending.Result.Status);
        }

        await server.SignInAsync("a very long operator password");
        var deviceId = DeviceIdentity.FingerprintOf(key.ExportSubjectPublicKeyInfo());
        await server.AdoptAsync(deviceId);

        await using var agent = await server.ConnectAgentAsync(key);
        await agent.SendGarbageOnAsync(ControlWire.KindReconcileReport, ProtocolConstants.ChannelTelemetry);
        await agent.SendReportAsync(Report(deviceId, 2));

        var reconcile = await server.WaitForReconcileAsync(deviceId, report => report?.Sequence == 2);

        Assert.NotNull(reconcile.Report);
        Assert.True(agent.IsOpen);
    }

    private static ReconcileReport Report(string deviceId, long sequence) => new()
    {
        DeviceId = deviceId,
        Sequence = sequence,
        GeneratedUtc = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero),
        LoopState = LoopStateNames.Reconciling,
        CurrentResource = "identity.hostname",
        CurrentPhase = "reboot",
        InSync = 5,
        Drifted = 2,
        Blocked = 1,
        RebootsExpected = 3,
        Resources =
        [
            new ResourceReport
            {
                Name = "identity.hostname",
                Status = ResourceStatusNames.AwaitingReboot,
                Delta = "expected framelink-douwe, observed raspberrypi",
                Attempts = 1,
                AttemptBudget = 5,
            },
        ],
    };

    private static DeviceEvent Event(string deviceId, DateTimeOffset at, string summary) => new()
    {
        DeviceId = deviceId,
        Kind = DeviceEventKinds.Drift,
        OccurredUtc = at,
        Resource = "boot.config.dtoverlay-waveshare-panel",
        Summary = summary,
        Delta = "expected the panel line, observed nothing",
        Attempts = 1,
    };
}
