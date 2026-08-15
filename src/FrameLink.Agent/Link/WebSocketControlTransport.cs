using System.Net.WebSockets;
using FrameLink.Protocol;

namespace FrameLink.Agent.Link;

/// <summary>
/// §4.1's transport: one persistent agent-initiated WebSocket over TLS.
/// </summary>
/// <remarks>
/// Outbound 443 is the only network requirement at a household, and nothing is ever dialled
/// inward — the property that lets a frame sit behind an ordinary router with no port forwarding
/// and still be fully manageable.
/// </remarks>
public sealed class WebSocketControlTransport : IControlTransport
{
    /// <summary>
    /// Largest message the agent will assemble before treating the peer as broken.
    /// </summary>
    /// <remarks>
    /// A receive loop that grows a buffer for as long as fragments keep arriving is an
    /// unbounded allocation driven by the remote end. On a 2 GB frame that is the same failure
    /// as the v1 leak reached from the other direction, so the ceiling is explicit.
    /// </remarks>
    public const int MaximumMessageBytes = 1024 * 1024;

    private readonly WebSocket _socket;
    private readonly byte[] _buffer = new byte[16 * 1024];
    private int _disposed;

    /// <summary>Wraps an already-connected socket.</summary>
    public WebSocketControlTransport(WebSocket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        _socket = socket;
    }

    /// <inheritdoc/>
    public ValueTask SendAsync(ReadOnlyMemory<byte> utf8, CancellationToken cancellationToken) =>
        _socket.SendAsync(utf8, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();

        while (true)
        {
            var received = await _socket.ReceiveAsync(_buffer, cancellationToken).ConfigureAwait(false);

            if (received.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(_buffer, 0, received.Count);
            if (message.Length > MaximumMessageBytes)
            {
                throw new InvalidOperationException(
                    $"Fleet Manager sent a message larger than {MaximumMessageBytes} bytes.");
            }

            if (received.EndOfMessage)
            {
                return message.ToArray();
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket
                    .CloseOutputAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, timeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
        {
            // A polite close is best-effort. Abandoning it must never prevent the Dispose below,
            // which is the call that actually frees the socket.
        }
        finally
        {
            _socket.Dispose();
        }
    }
}

/// <summary>Opens real WebSockets.</summary>
public sealed class WebSocketControlTransportFactory : IControlTransportFactory
{
    /// <summary>Route the agent socket lives on (§3.2 — exempt from the operator password).</summary>
    public const string AgentPath = "/agent";

    private readonly Func<ClientWebSocket> _create;

    /// <summary>Creates a factory.</summary>
    /// <param name="create">
    /// How to make a socket. Injected so that the release-on-failure obligation in
    /// <see cref="IControlTransportFactory.ConnectAsync"/> can be observed by a test rather than
    /// taken on trust.
    /// </param>
    public WebSocketControlTransportFactory(Func<ClientWebSocket>? create = null) =>
        _create = create ?? (static () => new ClientWebSocket());

    /// <inheritdoc/>
    public async ValueTask<IControlTransport> ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var socket = _create();
        try
        {
            await socket.ConnectAsync(SocketUriFor(endpoint), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The obligation from IControlTransportFactory: a failed connect hands back no
            // handle, so this is the only place the socket can be released. Everything the
            // reconnect loop does downstream depends on this catch existing.
            socket.Dispose();
            throw;
        }

        return new WebSocketControlTransport(socket);
    }

    /// <summary>Maps a control endpoint onto the WebSocket URL of the agent route.</summary>
    public static Uri SocketUriFor(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var scheme = string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            ? "wss"
            : "ws";

        var basePath = endpoint.AbsolutePath.TrimEnd('/');

        return new UriBuilder(endpoint)
        {
            Scheme = scheme,
            Path = basePath + AgentPath,
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;
    }
}

/// <summary>Versionless HTTP routes that sit outside the negotiated protocol (§4.2).</summary>
public static class ControlRoutes
{
    /// <summary>Where <see cref="AgentRelease"/> is published.</summary>
    public const string Release = "/agent/release";

    /// <summary>Builds the release URL for a runtime identifier.</summary>
    /// <remarks>
    /// The runtime identifier is a path segment, not a query parameter. That is the shape the
    /// Fleet Manager serves (<c>/agent/release/{runtimeIdentifier}</c>), and it matches the
    /// binary route the released metadata itself points at
    /// (<c>/agent/binary/{runtimeIdentifier}</c>). The two programs were written concurrently and
    /// disagreed here: the agent asked for <c>/agent/release?rid=…</c>, which no route matches,
    /// so every hourly convergence check answered 404 and §2.8's primary mechanism — the one
    /// every other failure mode is supposed to resolve through — silently did nothing.
    /// </remarks>
    public static Uri ReleaseFor(Uri endpoint, string runtimeIdentifier)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);

        var basePath = endpoint.AbsolutePath.TrimEnd('/');

        return new UriBuilder(endpoint)
        {
            Path = $"{basePath}{Release}/{Uri.EscapeDataString(runtimeIdentifier)}",
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;
    }
}
