using System.Text.Json;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Link;
using FrameLink.Protocol;

namespace FrameLink.Agent.Telemetry;

/// <summary>
/// §4.1's offline behaviour: telemetry buffers on disk, bounded, and drains on reconnect.
/// </summary>
/// <remarks>
/// <para>
/// The two channels are buffered differently on purpose, because they mean different things.
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Events are history</b> — drift, escalation, boot — and §3.5 keeps a month of them, so
/// losing one loses a fact. They are appended to a bounded ring; when it is full the
/// <i>oldest</i> is dropped, because on a frame that has been offline for a week the newest
/// events are the ones that explain the current state.
/// </description></item>
/// <item><description>
/// <b>Reports are the current picture</b>, so only the latest is kept. Buffering a queue of
/// them would fill the card with pictures nobody will ever look at, and the one that matters is
/// the one that says where the frame stands now.
/// </description></item>
/// </list>
/// <para>
/// Bounded is not a nicety here. The frame that began the August 2026 incident chain had a
/// volatile journal and no telemetry at all; the fix must not become the next thing that fills
/// the SD card while nobody is watching.
/// </para>
/// </remarks>
public sealed class TelemetryOutbox : IReconcileTelemetry
{
    /// <summary>Buffered events, one JSON object per line.</summary>
    public const string EventsFileName = "telemetry-events.jsonl";

    /// <summary>The latest undelivered report.</summary>
    public const string ReportFileName = "telemetry-report.json";

    private readonly AgentUplink _uplink;
    private readonly IStateStore _store;
    private readonly IAgentLog _log;
    private readonly Lock _gate = new();

    /// <summary>Creates an outbox over <paramref name="uplink"/>, spilling to <paramref name="store"/>.</summary>
    public TelemetryOutbox(AgentUplink uplink, IStateStore store, IAgentLog log)
    {
        ArgumentNullException.ThrowIfNull(uplink);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(log);

        _uplink = uplink;
        _store = store;
        _log = log;
    }

    /// <summary>How many buffered events are kept before the oldest are dropped.</summary>
    public int Capacity { get; init; } = 500;

    /// <summary>How many events have been dropped for want of room.</summary>
    public int Dropped { get; private set; }

    /// <summary>How many events are waiting on disk.</summary>
    public int Buffered
    {
        get
        {
            lock (_gate)
            {
                return ReadBuffer().Count;
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask ReportAsync(ReconcileReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        var payload = JsonSerializer.Serialize(report, ProtocolJson.Default.ReconcileReport);
        var sent = await SendAsync(
            ControlWire.KindReconcileReport,
            payload,
            ProtocolConstants.ChannelTelemetry,
            cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (sent)
            {
                _store.Delete(ReportFileName);
            }
            else
            {
                _store.WriteText(ReportFileName, payload);
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask<bool> EventAsync(DeviceEvent deviceEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deviceEvent);

        var payload = JsonSerializer.Serialize(deviceEvent, ProtocolJson.Default.DeviceEvent);

        // A buffer with anything in it is drained before a new event goes up, so the Fleet
        // Manager never sees an event that happened after one it has not received yet.
        var backlog = Buffered > 0;
        var sent = !backlog && await SendAsync(
            ControlWire.KindDeviceEvent,
            payload,
            ProtocolConstants.ChannelEvents,
            cancellationToken).ConfigureAwait(false);

        if (!sent)
        {
            Append(payload);
        }

        return sent;
    }

    /// <summary>
    /// Pushes everything buffered, oldest first, stopping at the first failure.
    /// </summary>
    /// <returns>How many events were delivered.</returns>
    /// <remarks>
    /// Stopping at the first failure rather than skipping past it is what keeps the order
    /// intact. A drain that carried on would deliver an escalation before the drift that caused
    /// it, which is worse than delivering neither.
    /// </remarks>
    public async Task<int> DrainAsync(CancellationToken cancellationToken)
    {
        List<string> pending;
        lock (_gate)
        {
            pending = ReadBuffer();
        }

        var delivered = 0;
        foreach (var payload in pending)
        {
            if (!await SendAsync(
                    ControlWire.KindDeviceEvent,
                    payload,
                    ProtocolConstants.ChannelEvents,
                    cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            delivered++;
        }

        if (delivered > 0)
        {
            lock (_gate)
            {
                // Re-read rather than reusing the snapshot: an event may have arrived while the
                // drain was in flight, and rewriting the snapshot would silently discard it.
                var remaining = ReadBuffer();
                remaining.RemoveRange(0, Math.Min(delivered, remaining.Count));
                WriteBuffer(remaining);
            }

            _log.Info($"Drained {delivered} buffered device events to the Fleet Manager.");
        }

        await DrainReportAsync(cancellationToken).ConfigureAwait(false);
        return delivered;
    }

    private async Task DrainReportAsync(CancellationToken cancellationToken)
    {
        string? stored;
        lock (_gate)
        {
            stored = _store.ReadText(ReportFileName);
        }

        if (string.IsNullOrWhiteSpace(stored))
        {
            return;
        }

        if (await SendAsync(
                ControlWire.KindReconcileReport,
                stored,
                ProtocolConstants.ChannelTelemetry,
                cancellationToken).ConfigureAwait(false))
        {
            lock (_gate)
            {
                _store.Delete(ReportFileName);
            }
        }
    }

    private async ValueTask<bool> SendAsync(
        string kind,
        string payload,
        string channel,
        CancellationToken cancellationToken)
    {
        if (!_uplink.IsConnected)
        {
            return false;
        }

        // The payload is already JSON, so it is spliced into the frozen envelope as a raw
        // element rather than being deserialised and re-serialised. That keeps a buffered event
        // byte-identical to the one that would have gone out live.
        byte[] bytes;
        try
        {
            using var document = JsonDocument.Parse(payload);
            bytes = WireMessage.EncodeRaw(kind, document.RootElement, channel);
        }
        catch (JsonException exception)
        {
            // A buffer line that will not parse can never be delivered, so treating it as sent
            // is what removes it. Silently discarding it would be the wrong shape of quiet.
            _log.Warn($"Discarding an unreadable buffered telemetry payload: {exception.Message}");
            return true;
        }

        return await _uplink.SendAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private void Append(string payload)
    {
        lock (_gate)
        {
            var buffer = ReadBuffer();
            buffer.Add(payload);

            if (buffer.Count > Capacity)
            {
                var excess = buffer.Count - Capacity;
                buffer.RemoveRange(0, excess);
                Dropped += excess;
            }

            WriteBuffer(buffer);
        }
    }

    private List<string> ReadBuffer()
    {
        var text = _store.ReadText(EventsFileName);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    private void WriteBuffer(List<string> buffer)
    {
        if (buffer.Count == 0)
        {
            _store.Delete(EventsFileName);
            return;
        }

        _store.WriteText(EventsFileName, string.Join('\n', buffer) + "\n");
    }
}
