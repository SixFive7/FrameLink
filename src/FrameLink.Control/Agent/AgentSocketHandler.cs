using System.Net.WebSockets;
using FrameLink.Control.Storage;
using FrameLink.Protocol;

namespace FrameLink.Control.Agent;

/// <summary>
/// The <c>/agent</c> endpoint's conversation: the frozen handshake, then the logical channels.
/// </summary>
/// <remarks>
/// §4.2 requires the handshake on <b>every</b> connect, not only the first, and requires every
/// outcome to be an explicit answer rather than a dropped socket. Both are properties of this
/// method's shape: there is exactly one path in, it always ends by sending a
/// <c>HandshakeResult</c>, and only the <c>ok</c> branch continues past it.
/// </remarks>
public sealed class AgentSocketHandler(
    DeviceHandshake handshake,
    ISettingsStore settings,
    SettingsPublisher publisher,
    LiveKit.CallProvisioning calls,
    AgentConnectionRegistry registry,
    TelemetryIngest telemetry,
    ControlOptions options,
    TimeProvider clock,
    ILoggerFactory loggerFactory,
    ILogger<AgentSocketHandler> logger)
{
    /// <summary>Runs one device conversation from open to close.</summary>
    public async Task HandleAsync(
        WebSocket socket,
        string? remoteAddress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);

        var decision = await NegotiateAsync(socket, remoteAddress, cancellationToken).ConfigureAwait(false);
        if (decision is null || !decision.KeepOpen || decision.Device is null)
        {
            // §2.7 item 8 is deliberately NOT sent here, and the reason is worth keeping. A frame
            // that is not adopted cannot be told who to contact, because this endpoint is open to
            // the internet (§3.3) and anything that connects is answered `pending` — so a contact
            // frame on this path would hand the operator's name and telephone number to every
            // anonymous caller that found the URL. What it would have bought is close to nothing:
            // §3.2 records that "the operator is usually the first person to connect a frame", so
            // the person standing in front of an unadopted frame is almost always the operator
            // themselves, and they do not need to be told their own number (decision 71).
            //
            // Answered and finished. Everything except `ok` closes here, so no pending,
            // blocked, unconfigured or version-mismatched device ever holds a socket, a
            // registry slot or a ping timer (§3.3: a pending record allocates no resources).
            await CloseQuietlyAsync(socket).ConfigureAwait(false);
            return;
        }

        var deviceId = decision.Device.DeviceId;
        var connection = new AgentConnection(
            deviceId,
            socket,
            options,
            clock,
            loggerFactory.CreateLogger<AgentConnection>())
        {
            OnInbound = telemetry.HandleAsync,
        };

        var displaced = registry.Register(connection);
        if (displaced is not null)
        {
            logger.DisplacedPreviousSocket(deviceId);
            displaced.RequestClose();
        }

        try
        {
            logger.DeviceOnline(deviceId, remoteAddress);

            // Before the settings are resolved, so that a frame whose call token has aged into
            // its last third collects the replacement in the same frame that carries everything
            // else — no second push, no window, and nothing for the frame to ask for. This is
            // where §3.7's "rotate at will" stops being a button an operator has to remember and
            // becomes a property of reconnecting, which is the difference between the July-23
            // expiry being survivable and it being invisible.
            //
            // A no-op for a frame whose token is fine, which is nearly every connect: a base64
            // decode, four string comparisons and no write.
            await calls.ReviewAsync(deviceId, force: false, cancellationToken).ConfigureAwait(false);

            // The first thing an adopted device receives, and the whole of the difference
            // between adopted and pending on this route.
            var resolved = await settings.ResolveAsync(deviceId, cancellationToken).ConfigureAwait(false);
            await publisher.PushAsync(connection, resolved, cancellationToken).ConfigureAwait(false);

            // §2.7 item 8, on the adopted path. Sent on every connect rather than only when it
            // changes, because the frame is the thing that remembers and a frame that was reflashed
            // remembers nothing — and because the agent already writes only on a change, so a
            // reconnect storm costs one small frame each and no disk.
            var contact = await publisher.ResolveContactAsync(cancellationToken).ConfigureAwait(false);
            await publisher.PushContactAsync(connection, contact, cancellationToken).ConfigureAwait(false);

            await connection.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            registry.Remove(connection);
            await connection.DisposeAsync().ConfigureAwait(false);
            logger.DeviceOffline(deviceId);
        }
    }

    private async Task<HandshakeDecision?> NegotiateAsync(
        WebSocket socket,
        string? remoteAddress,
        CancellationToken cancellationToken)
    {
        // The handshake gets its own deadline. A peer that opens a socket and then says
        // nothing is the cheapest possible attack on an open endpoint, and without a timeout
        // it costs the server a connection indefinitely.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.HandshakeTimeout);

        try
        {
            var opening = await WireSocket
                .ReceiveAsync(socket, options.MaxFrameBytes, deadline.Token)
                .ConfigureAwait(false);

            if (opening is null || !string.Equals(opening.Kind, WireMessage.KindHello, StringComparison.Ordinal))
            {
                logger.NoHello(remoteAddress);
                return null;
            }

            var hello = opening.PayloadAs(ProtocolJson.Default.HandshakeHello);
            if (hello is null)
            {
                logger.UnreadableHello(remoteAddress);
                return null;
            }

            var serverNonce = DeviceIdentity.NewNonce();
            await WireSocket.SendAsync(
                    socket,
                    WireMessage.KindChallenge,
                    new HandshakeChallenge { Nonce = serverNonce },
                    ProtocolJson.Default.HandshakeChallenge,
                    channel: null,
                    deadline.Token)
                .ConfigureAwait(false);

            var answer = await WireSocket
                .ReceiveAsync(socket, options.MaxFrameBytes, deadline.Token)
                .ConfigureAwait(false);

            if (answer is null || !string.Equals(answer.Kind, WireMessage.KindProof, StringComparison.Ordinal))
            {
                logger.NoProof(remoteAddress);
                return null;
            }

            var proof = answer.PayloadAs(ProtocolJson.Default.HandshakeProof);
            if (proof is null)
            {
                return null;
            }

            var decision = await handshake
                .DecideAsync(hello, serverNonce, proof, remoteAddress, deadline.Token)
                .ConfigureAwait(false);

            await WireSocket.SendAsync(
                    socket,
                    WireMessage.KindResult,
                    decision.Result,
                    ProtocolJson.Default.HandshakeResult,
                    channel: null,
                    deadline.Token)
                .ConfigureAwait(false);

            return decision;
        }
        catch (OperationCanceledException)
        {
            logger.HandshakeTimedOut(remoteAddress);
            return null;
        }
        catch (WebSocketException exception)
        {
            logger.HandshakeTransportFailed(exception, remoteAddress);
            return null;
        }
    }

    private static async Task CloseQuietlyAsync(WebSocket socket)
    {
        try
        {
            if (socket.State is WebSocketState.Open)
            {
                await socket
                    .CloseAsync(WebSocketCloseStatus.NormalClosure, "handshake complete", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
            // The peer left first.
        }
    }
}
