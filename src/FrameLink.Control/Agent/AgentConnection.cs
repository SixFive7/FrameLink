using System.Net.WebSockets;
using System.Text.Json.Serialization.Metadata;
using FrameLink.Protocol;

namespace FrameLink.Control.Agent;

/// <summary>
/// One adopted device's live socket: the logical channels of §4.1 and the liveness
/// discipline of §3.5.
/// </summary>
/// <remarks>
/// Only ever created after a handshake answered <c>ok</c>. Every other outcome is answered
/// and closed, so no pending, blocked, unconfigured or version-mismatched device ever reaches
/// this class — which is where "a pending device receives nothing" stops being a rule
/// somebody has to remember and becomes a fact about the object graph.
/// </remarks>
public sealed class AgentConnection(
    string deviceId,
    WebSocket socket,
    ControlOptions options,
    TimeProvider clock,
    ILogger logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private long _lastInboundTicks = clock.GetUtcNow().UtcTicks;
    private long _pingSequence;

    /// <summary>The proven identity this socket is bound to.</summary>
    public string DeviceId { get; } = deviceId;

    /// <summary>When the socket was adopted into the registry.</summary>
    public DateTimeOffset ConnectedUtc { get; } = clock.GetUtcNow();

    /// <summary>Sends a payload on a logical channel.</summary>
    /// <remarks>
    /// Serialised through a lock because a WebSocket permits exactly one send at a time, and
    /// this connection has at least two writers: the ping timer and whatever the operator
    /// does in the GUI.
    /// </remarks>
    public async Task SendAsync<TPayload>(
        string kind,
        TPayload payload,
        JsonTypeInfo<TPayload> payloadTypeInfo,
        string? channel,
        CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (socket.State is WebSocketState.Open)
            {
                await WireSocket
                    .SendAsync(socket, kind, payload, payloadTypeInfo, channel, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Pumps the socket until it closes, fails, or misses its pong deadline.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);

        var receiving = ReceiveLoopAsync(linked.Token);
        var probing = LivenessLoopAsync(linked.Token);

        await Task.WhenAny(receiving, probing).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);

        // Both loops are awaited even though only one of them ended the connection, so that
        // neither is left running against a socket that is about to be disposed.
        await SwallowAsync(receiving).ConfigureAwait(false);
        await SwallowAsync(probing).ConfigureAwait(false);
    }

    /// <summary>Asks the connection to shut down without waiting for it.</summary>
    public void RequestClose() => _closing.Cancel();

    /// <summary>Closes the socket politely if it is still open, then releases everything.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (socket.State is WebSocketState.Open)
            {
                await socket
                    .CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
            // The peer is already gone. Nothing to say and nobody to say it to.
        }
        catch (OperationCanceledException)
        {
            // Same.
        }

        _closing.Dispose();
        _sendLock.Dispose();
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var envelope = await WireSocket
                .ReceiveAsync(socket, options.MaxFrameBytes, cancellationToken)
                .ConfigureAwait(false);

            if (envelope is null)
            {
                return;
            }

            // Any inbound traffic proves the socket is alive, not just a pong. A device that
            // is busy streaming telemetry has already answered the question a ping asks.
            Interlocked.Exchange(ref _lastInboundTicks, clock.GetUtcNow().UtcTicks);
            Dispatch(envelope);
        }
    }

    private void Dispatch(WireEnvelope envelope)
    {
        if (string.Equals(envelope.Kind, ControlWire.KindPong, StringComparison.Ordinal))
        {
            // The timestamp is already refreshed by the caller; a pong carries no other news.
            return;
        }

        // Telemetry and events are accepted and logged for M1 — retention and the live
        // reconciliation screen are §3.5 work for a later milestone. Unknown kinds take the
        // same path rather than closing the socket: the envelope is frozen so that a newer
        // peer stays legible, and hanging up on an unrecognised Kind would throw that away.
        logger.InboundMessage(DeviceId, envelope.Kind, envelope.Channel);
    }

    private async Task LivenessLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.PingInterval, clock);

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var silence = clock.GetUtcNow() - new DateTimeOffset(Interlocked.Read(ref _lastInboundTicks), TimeSpan.Zero);
            if (silence >= options.PongDeadline)
            {
                // This is the half-open TCP case §3.5 is about. The socket still accepts
                // writes and will do so forever, so nothing except a missed answer can tell
                // us the frame is gone. Abort rather than close: there is no peer to complete
                // a closing handshake with.
                logger.PongDeadlineMissed(DeviceId, silence);
                socket.Abort();
                return;
            }

            var ping = new AgentPing
            {
                Sequence = Interlocked.Increment(ref _pingSequence),
                SentUtc = clock.GetUtcNow(),
            };

            await SendAsync(
                    ControlWire.KindPing,
                    ping,
                    ProtocolJson.Default.AgentPing,
                    ProtocolConstants.ChannelControl,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: the sibling loop ended the connection and cancelled this one.
        }
        catch (WebSocketException)
        {
            // Expected: the peer vanished mid-read or mid-write.
        }
    }
}
