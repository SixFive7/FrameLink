using System.Text.Json;
using FrameLink.Agent.Link;
using FrameLink.Agent.Telemetry;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// §4.1's telemetry and events channels: what goes up, what happens when nothing is listening,
/// and what drains when something is again.
/// </summary>
public sealed class AgentTelemetryTests
{
    [Fact]
    public void The_report_payload_keeps_the_wire_names_it_shipped_with()
    {
        // Frozen once shipped, and a renamed property is a silent break: the peer deserialises a
        // default instead of failing. Asserting the bytes is the only way to notice.
        var json = JsonSerializer.Serialize(
            new ReconcileReport
            {
                DeviceId = "AAAA-AAAA-AAAA-AAAA",
                Sequence = 7,
                GeneratedUtc = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero),
                LoopState = LoopStateNames.Reconciling,
                CurrentResource = "identity.hostname",
                CurrentPhase = "act",
                InSync = 3,
                Drifted = 1,
                Blocked = 2,
                RebootsExpected = 3,
                Resources =
                [
                    new ResourceReport
                    {
                        Name = "identity.hostname",
                        Status = ResourceStatusNames.AwaitingReboot,
                        Delta = "expected a, observed b",
                        Action = "write the seed",
                        Attempts = 1,
                        AttemptBudget = 5,
                    },
                ],
            },
            ProtocolJson.Default.ReconcileReport);

        // Written with single quotes and translated, because the expected value is one long line
        // of JSON and a raw string literal cannot start a fragment with the quote character.
        Assert.Equal(
            Json(
                "{'deviceId':'AAAA-AAAA-AAAA-AAAA','sequence':7,'generatedUtc':'2026-08-15T12:00:00+00:00',"
                + "'loopState':'reconciling','currentResource':'identity.hostname','currentPhase':'act',"
                + "'inSync':3,'drifted':1,'blocked':2,'rebootsExpected':3,'resources':[{'name':'identity.hostname',"
                + "'status':'awaiting-reboot','delta':'expected a, observed b',"
                + "'action':'write the seed','attempts':1,'attemptBudget':5,'escalations':0}]}"),
            json);
    }

    [Fact]
    public void The_event_payload_keeps_the_wire_names_it_shipped_with()
    {
        var json = JsonSerializer.Serialize(
            new DeviceEvent
            {
                DeviceId = "AAAA-AAAA-AAAA-AAAA",
                Kind = DeviceEventKinds.Escalation,
                OccurredUtc = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero),
                Resource = "cpu.governor.performance",
                Summary = "It has been tried 5 times and is still wrong.",
                Delta = "expected performance, observed ondemand",
                Attempts = 5,
            },
            ProtocolJson.Default.DeviceEvent);

        Assert.Equal(
            Json(
                "{'deviceId':'AAAA-AAAA-AAAA-AAAA','kind':'escalation','occurredUtc':'2026-08-15T12:00:00+00:00',"
                + "'resource':'cpu.governor.performance','summary':'It has been tried 5 times and is still wrong.',"
                + "'delta':'expected performance, observed ondemand','attempts':5}"),
            json);
    }

    [Fact]
    public async Task An_event_sent_with_no_link_is_buffered_and_reports_that_it_was_not_delivered()
    {
        // The boolean is load-bearing: it is what keeps Degraded and Escalated(admin-notified)
        // two different things.
        using var store = new TemporaryStore();
        using var uplink = new AgentUplink();
        var outbox = new TelemetryOutbox(uplink, store.Store, NullAgentLog);

        var delivered = await outbox.EventAsync(Event(1), TestContext.Current.CancellationToken);

        Assert.False(delivered);
        Assert.Equal(1, outbox.Buffered);
    }

    [Fact]
    public async Task Buffered_events_drain_in_order_on_reconnect()
    {
        using var store = new TemporaryStore();
        using var uplink = new AgentUplink();
        var outbox = new TelemetryOutbox(uplink, store.Store, NullAgentLog);

        for (var index = 1; index <= 3; index++)
        {
            await outbox.EventAsync(Event(index), TestContext.Current.CancellationToken);
        }

        var transport = new CapturingTransport();
        using var attachment = uplink.Attach(transport);
        var drained = await outbox.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, drained);
        Assert.Equal(0, outbox.Buffered);
        Assert.Equal([1, 2, 3], transport.Attempts());
    }

    [Fact]
    public async Task A_drain_that_fails_part_way_keeps_the_rest_in_order()
    {
        // Stopping at the first failure rather than skipping past it is what keeps the order
        // intact. A drain that carried on would deliver an escalation before the drift that
        // caused it, which is worse than delivering neither.
        using var store = new TemporaryStore();
        using var uplink = new AgentUplink();
        var outbox = new TelemetryOutbox(uplink, store.Store, NullAgentLog);

        for (var index = 1; index <= 4; index++)
        {
            await outbox.EventAsync(Event(index), TestContext.Current.CancellationToken);
        }

        var transport = new CapturingTransport { FailAfter = 2 };
        using var attachment = uplink.Attach(transport);
        var drained = await outbox.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, drained);
        Assert.Equal(2, outbox.Buffered);
        Assert.Equal([1, 2], transport.Attempts());
    }

    [Fact]
    public async Task The_buffer_is_bounded_and_drops_the_oldest_first()
    {
        // §4.1 says bounded, and the frame that began the August 2026 incident chain had a
        // volatile journal and no telemetry at all. The fix must not become the next thing that
        // fills the SD card while nobody is watching.
        using var store = new TemporaryStore();
        using var uplink = new AgentUplink();
        var outbox = new TelemetryOutbox(uplink, store.Store, NullAgentLog) { Capacity = 3 };

        for (var index = 1; index <= 6; index++)
        {
            await outbox.EventAsync(Event(index), TestContext.Current.CancellationToken);
        }

        var transport = new CapturingTransport();
        using var attachment = uplink.Attach(transport);
        await outbox.DrainAsync(TestContext.Current.CancellationToken);

        // On a frame that has been offline for a week the newest events explain the current
        // state, so the oldest are what goes.
        Assert.Equal(3, outbox.Dropped);
        Assert.Equal([4, 5, 6], transport.Attempts());
    }

    [Fact]
    public async Task Only_the_latest_report_is_kept_while_the_frame_is_offline()
    {
        // A report is the current picture; buffering a queue of them would fill the card with
        // pictures nobody will ever look at.
        using var store = new TemporaryStore();
        using var uplink = new AgentUplink();
        var outbox = new TelemetryOutbox(uplink, store.Store, NullAgentLog);

        await outbox.ReportAsync(Report(1), TestContext.Current.CancellationToken);
        await outbox.ReportAsync(Report(2), TestContext.Current.CancellationToken);
        await outbox.ReportAsync(Report(3), TestContext.Current.CancellationToken);

        var transport = new CapturingTransport();
        using var attachment = uplink.Attach(transport);
        await outbox.DrainAsync(TestContext.Current.CancellationToken);

        var sent = transport.Envelopes()
            .Where(envelope => string.Equals(envelope.Kind, ControlWire.KindReconcileReport, StringComparison.Ordinal))
            .Select(envelope => envelope.PayloadAs(ProtocolJson.Default.ReconcileReport)!.Sequence)
            .ToList();

        Assert.Equal([3], sent);
    }

    [Fact]
    public async Task A_report_that_goes_up_live_leaves_nothing_behind_to_drain()
    {
        using var store = new TemporaryStore();
        using var uplink = new AgentUplink();
        var outbox = new TelemetryOutbox(uplink, store.Store, NullAgentLog);

        var transport = new CapturingTransport();
        using var attachment = uplink.Attach(transport);
        await outbox.ReportAsync(Report(1), TestContext.Current.CancellationToken);

        Assert.False(store.Store.Exists(TelemetryOutbox.ReportFileName));
        Assert.Single(transport.Envelopes());
    }

    [Fact]
    public async Task Telemetry_travels_on_the_channels_section_4_1_names()
    {
        using var store = new TemporaryStore();
        using var uplink = new AgentUplink();
        var outbox = new TelemetryOutbox(uplink, store.Store, NullAgentLog);

        var transport = new CapturingTransport();
        using var attachment = uplink.Attach(transport);
        await outbox.ReportAsync(Report(1), TestContext.Current.CancellationToken);
        await outbox.EventAsync(Event(1), TestContext.Current.CancellationToken);

        var envelopes = transport.Envelopes();

        Assert.Equal(ProtocolConstants.ChannelTelemetry, envelopes[0].Channel);
        Assert.Equal(ControlWire.KindReconcileReport, envelopes[0].Kind);
        Assert.Equal(ProtocolConstants.ChannelEvents, envelopes[1].Channel);
        Assert.Equal(ControlWire.KindDeviceEvent, envelopes[1].Kind);
    }

    [Fact]
    public async Task The_uplink_reports_failure_rather_than_throwing_when_the_session_has_gone()
    {
        using var uplink = new AgentUplink();

        Assert.False(uplink.IsConnected);
        Assert.False(await uplink.SendAsync(new byte[] { 1 }, TestContext.Current.CancellationToken));

        var transport = new CapturingTransport { FailAfter = 0 };
        using var attachment = uplink.Attach(transport);

        Assert.True(uplink.IsConnected);
        Assert.False(await uplink.SendAsync(new byte[] { 1 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Detaching_one_attempt_does_not_unhook_the_session_that_replaced_it()
    {
        // The reconnect loop's shape: an attempt can be unwinding while its successor is already
        // live, and a late Dispose must not take the new session's route to the server with it.
        using var uplink = new AgentUplink();
        var first = new CapturingTransport();
        var second = new CapturingTransport();

        var firstAttachment = uplink.Attach(first);
        using var secondAttachment = uplink.Attach(second);
        firstAttachment.Dispose();

        Assert.True(uplink.IsConnected);
        Assert.True(await uplink.SendAsync(new byte[] { 1 }, TestContext.Current.CancellationToken));
        Assert.Single(second.Sent);
        Assert.Empty(first.Sent);
    }

    private static FrameLink.Agent.Hosting.IAgentLog NullAgentLog =>
        FrameLink.Agent.Hosting.NullLog.Instance;

    /// <summary>Turns a readable single-quoted template into the JSON it stands for.</summary>
    private static string Json(string template) =>
        template.Replace("'", "\"", StringComparison.Ordinal);

    private static DeviceEvent Event(int attempts) => new()
    {
        DeviceId = "AAAA-AAAA-AAAA-AAAA",
        Kind = DeviceEventKinds.Drift,
        OccurredUtc = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero),
        Summary = "drifted",
        Attempts = attempts,
    };

    private static ReconcileReport Report(long sequence) => new()
    {
        DeviceId = "AAAA-AAAA-AAAA-AAAA",
        Sequence = sequence,
        GeneratedUtc = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero),
        LoopState = LoopStateNames.Reconciling,
        InSync = 0,
        Drifted = 1,
        Blocked = 0,
        RebootsExpected = 1,
        Resources = [],
    };

    /// <summary>A transport that keeps every frame and can be told to start failing.</summary>
    private sealed class CapturingTransport : IControlTransport
    {
        public List<byte[]> Sent { get; } = [];

        /// <summary>How many sends succeed before the session "goes away". Null means all.</summary>
        public int? FailAfter { get; init; }

        public List<WireEnvelope> Envelopes() =>
            [.. Sent.Select(bytes => WireMessage.Decode(bytes)!)];

        public List<int> Attempts() =>
            [.. Envelopes()
                .Where(envelope => string.Equals(envelope.Kind, ControlWire.KindDeviceEvent, StringComparison.Ordinal))
                .Select(envelope => envelope.PayloadAs(ProtocolJson.Default.DeviceEvent)!.Attempts)];

        public ValueTask SendAsync(ReadOnlyMemory<byte> utf8, CancellationToken cancellationToken)
        {
            if (FailAfter is { } limit && Sent.Count >= limit)
            {
                throw new IOException("the session went away");
            }

            Sent.Add(utf8.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
