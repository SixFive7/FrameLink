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

            // The first thing an adopted device receives, and the whole of the difference
            // between adopted and pending on this route.
            var resolved = await settings.ResolveAsync(deviceId, cancellationToken).ConfigureAwait(false);
            await publisher.PushAsync(connection, resolved, cancellationToken).ConfigureAwait(false);

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
