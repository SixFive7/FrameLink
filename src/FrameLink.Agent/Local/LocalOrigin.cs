using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Local;

/// <summary>
/// <c>app.http.local-origin</c>'s server — <b>one origin for the product, the repair screen and
/// the local channel</b>.
/// </summary>
/// <remarks>
/// <para>
/// v1 served the app with <c>busybox httpd</c> from a <c>framelink-spa.service</c> user unit
/// pointed at a git checkout, and talked to the GPIO daemon over a second WebSocket server on
/// <c>127.0.0.1:8889</c>. The catalog retires all three: the unit, the checkout and the port. What
/// replaces them is this, on the same <c>127.0.0.1:8888</c> the kiosk unit's readiness guard and
/// its <c>ExecStart</c> URL already name, so nothing downstream had to move.
/// </para>
/// <para>
/// <b>Written by hand rather than taken from ASP.NET Core, and that is a §2.1 constraint rather
/// than a preference.</b> The shipped agent is a 1.35 MB Native AOT ELF linking only
/// <c>libc</c>/<c>libm</c> (§6.1); a web framework is the single largest thing that could be
/// added to it, in exchange for routing this file does in a hundred lines. <c>HttpListener</c> is
/// no help either — its <c>AcceptWebSocketAsync</c> is Windows-only, and the local channel is the
/// whole reason the server exists.
/// </para>
/// <para>
/// <b>Loopback only, always.</b> The listener binds <see cref="IPAddress.Loopback"/> and nothing
/// else, exactly as v1's <c>busybox httpd -p 127.0.0.1:8888</c> did. The app, the repair screen
/// and the channel are all reachable by this frame's own browser and by nothing on the network —
/// which matters more in v2 than it did in v1, because the document this server hands out now
/// carries the LiveKit token.
/// </para>
/// </remarks>
public sealed class LocalOrigin : IAsyncDisposable
{
    /// <summary>The port the kiosk unit's URL and readiness guard both name.</summary>
    public const int DefaultPort = 8888;

    /// <summary>Where the page opens the local channel.</summary>
    public const string ChannelPath = "/local";

    /// <summary>Where the app fetches its five configured values.</summary>
    public const string ConfigPath = "/config.json";

    /// <summary>The magic string RFC 6455 mixes into the accept key.</summary>
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private const int MaximumHeadBytes = 8 * 1024;

    private readonly LocalChannel _channel;
    private readonly IAgentClock _clock;
    private readonly IAgentLog _log;
    private readonly Func<AppConfigDocument?> _configuration;
    private readonly Func<StageMessage>? _greeting;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();

    private TcpListener? _listener;
    private Task? _accepting;
    private int _disposed;

    /// <summary>Creates the server.</summary>
    /// <param name="channel">Where a connected page is registered.</param>
    /// <param name="clock">Source of check-in timestamps.</param>
    /// <param name="log">Where refusals are recorded.</param>
    /// <param name="configuration">
    /// The five <c>app.config.*</c> values as the agent currently has them, read per request so a
    /// value pushed by the Fleet Manager reaches the next page load rather than the next reboot.
    /// Null means the frame has nothing to serve yet, which is answered <c>503</c>.
    /// </param>
    /// <param name="greeting">
    /// The narration a page is sent the instant it connects, so the repair screen does not wait
    /// for the next status change to have something to render.
    /// </param>
    /// <param name="port">The listening port; <see cref="DefaultPort"/> on a frame.</param>
    public LocalOrigin(
        LocalChannel channel,
        IAgentClock clock,
        IAgentLog log,
        Func<AppConfigDocument?>? configuration = null,
        Func<StageMessage>? greeting = null,
        int port = DefaultPort)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(log);

        _channel = channel;
        _clock = clock;
        _log = log;
        _configuration = configuration ?? (() => null);
        _greeting = greeting;
        RequestedPort = port;
    }

    /// <summary>The port asked for at construction.</summary>
    public int RequestedPort { get; }

    /// <summary>The port actually bound, or 0 if the server is not listening.</summary>
    public int Port { get; private set; }

    /// <summary>Whether the server is accepting connections.</summary>
    public bool IsListening
    {
        get
        {
            lock (_gate)
            {
                return _listener is not null;
            }
        }
    }

    /// <summary>Why the last <see cref="Start"/> failed, if it did.</summary>
    public string? LastFailure { get; private set; }

    /// <summary>How many requests have been answered.</summary>
    public int Requests { get; private set; }

    /// <summary>Binds the port and starts accepting. Safe to call when already listening.</summary>
    /// <returns>Whether the server is listening when this returns.</returns>
    public bool Start()
    {
        lock (_gate)
        {
            if (_listener is not null)
            {
                return true;
            }

            var listener = new TcpListener(IPAddress.Loopback, RequestedPort);

            try
            {
                listener.Start();
            }
            catch (SocketException exception)
            {
                // The one failure that matters is "address already in use", and it has a real
                // cause on a frame: a leftover v1 framelink-spa.service still holding 8888. The
                // resource reports it, escalates, and an operator sees the port and the owner
                // rather than a blank screen.
                listener.Dispose();
                LastFailure = exception.Message;
                _log.Fail($"The local origin could not bind 127.0.0.1:{RequestedPort} — {exception.Message}");
                return false;
            }

            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            LastFailure = null;
        }

        _accepting = Task.Run(() => AcceptAsync(_stopping.Token), CancellationToken.None);
        _log.Info($"The local origin is serving the product app and the repair screen on http://127.0.0.1:{Port}/");
        return true;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);

        TcpListener? listener;
        lock (_gate)
        {
            listener = _listener;
            _listener = null;
        }

        listener?.Dispose();

        if (_accepting is { } accepting)
        {
            try
            {
                await accepting.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Asked to stop.
            }
        }

        _stopping.Dispose();
    }

    /// <summary>The RFC 6455 accept key for a client's <c>Sec-WebSocket-Key</c>.</summary>
    /// <remarks>
    /// <b>SHA-1 is fixed by RFC 6455 §4.2.2 and is not a security decision this code gets to
    /// make.</b> The value it produces protects nothing: it exists so a server proves it
    /// understood the <c>Upgrade</c> request rather than being a cache or a proxy replaying an
    /// old response, and every WebSocket client on earth — including the Chromium on this frame —
    /// computes the same digest and refuses the connection if the answer differs. Any other
    /// algorithm makes the handshake fail. §7.2 forbids weakening an analyser to make code pass;
    /// this is the documented single-site suppression for a rule that cannot apply, with the
    /// reason recorded, and the alternative — abandoning WebSockets for the local channel — would
    /// change the protocol the product app already speaks in order to satisfy a rule about a hash
    /// that guards nothing.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "RFC 6455 §4.2.2 mandates SHA-1 for the Sec-WebSocket-Accept handshake value. It is a protocol constant, not a security primitive: it authenticates nothing and keeps nothing secret.")]
    public static string AcceptKey(string clientKey)
    {
        ArgumentNullException.ThrowIfNull(clientKey);

        return Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(clientKey + WebSocketGuid)));
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpListener? listener;
            lock (_gate)
            {
                listener = _listener;
            }

            if (listener is null)
            {
                return;
            }

            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                return;
            }

            // Fire and forget, deliberately: one slow page must not stop the next one being
            // served, and there is no work after a connection ends that anything waits on.
            _ = Task.Run(() => ServeAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream, cancellationToken).ConfigureAwait(false);

                if (head is null)
                {
                    return;
                }

                Requests++;
                await RouteAsync(stream, head, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The agent is stopping.
            }
            catch (Exception exception) when (exception is IOException
                or SocketException
                or ObjectDisposedException
                or InvalidOperationException
                or WebSocketException)
            {
                // A browser that went away mid-request. §1.2.3 forbids silent repair, not silent
                // disconnects: nothing was repaired here and nothing is wrong with the frame.
                _log.Write(AgentLogLevel.Info, $"A local request ended early: {exception.Message}");
            }
        }
    }

    private async Task RouteAsync(NetworkStream stream, RequestHead head, CancellationToken cancellationToken)
    {
        if (!string.Equals(head.Method, "GET", StringComparison.Ordinal))
        {
            await WriteAsync(stream, 405, "text/plain; charset=utf-8", "Only GET is served here.\n", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(head.Path, ChannelPath, StringComparison.Ordinal))
        {
            await UpgradeAsync(stream, head, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(head.Path, ConfigPath, StringComparison.Ordinal))
        {
            var configuration = _configuration();

            if (configuration is null)
            {
                // §3.3: a pending device receives nothing. The page asking for values this frame
                // has not been issued is answered honestly rather than with an empty document it
                // would then try to connect with.
                await WriteAsync(
                    stream,
                    503,
                    "text/plain; charset=utf-8",
                    "This frame has not been issued its settings yet.\n",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var json = JsonSerializer.Serialize(configuration, AgentJson.Default.AppConfigDocument);
            await WriteAsync(stream, 200, "application/json; charset=utf-8", json, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (EmbeddedApp.Find(head.Path) is { } asset)
        {
            await WriteAsync(
                stream,
                200,
                EmbeddedApp.ContentTypeOf(head.Path.Length <= 1 ? EmbeddedApp.IndexPath : head.Path),
                asset,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteAsync(stream, 404, "text/plain; charset=utf-8", "Not found.\n", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task UpgradeAsync(NetworkStream stream, RequestHead head, CancellationToken cancellationToken)
    {
        if (head.Header("sec-websocket-key") is not { Length: > 0 } key)
        {
            await WriteAsync(stream, 400, "text/plain; charset=utf-8", "Not a WebSocket request.\n", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var response = "HTTP/1.1 101 Switching Protocols\r\n"
            + "Upgrade: websocket\r\n"
            + "Connection: Upgrade\r\n"
            + $"Sec-WebSocket-Accept: {AcceptKey(key)}\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        using var socket = WebSocket.CreateFromStream(
            stream,
            isServer: true,
            subProtocol: null,
            keepAliveInterval: TimeSpan.FromSeconds(20));

        await PumpAsync(socket, cancellationToken).ConfigureAwait(false);
    }

    private async Task PumpAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var sending = new SemaphoreSlim(1, 1);

        async Task SendAsync(StageMessage message, CancellationToken token)
        {
            await sending.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(message, AgentJson.Default.StageMessage);
                await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, token)
                    .ConfigureAwait(false);
            }
            finally
            {
                sending.Release();
            }
        }

        using var attachment = _channel.Attach(SendAsync);

        if (_greeting is not null)
        {
            await SendAsync(_greeting(), cancellationToken).ConfigureAwait(false);
        }

        var buffer = new byte[4 * 1024];

        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var received = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (received.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (received.MessageType != WebSocketMessageType.Text || !received.EndOfMessage)
            {
                continue;
            }

            PageMessage? message;
            try
            {
                message = JsonSerializer.Deserialize(
                    buffer.AsSpan(0, received.Count),
                    AgentJson.Default.PageMessage);
            }
            catch (JsonException)
            {
                continue;
            }

            if (message is { Kind.Length: > 0 })
            {
                _channel.Receive(message, _clock.UtcNow);
            }
        }
    }

    private static async Task<RequestHead?> ReadHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaximumHeadBytes];
        var filled = 0;

        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            filled += read;

            var text = Encoding.ASCII.GetString(buffer, 0, filled);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0)
            {
                return RequestHead.Parse(text[..end]);
            }
        }

        return null;
    }

    private static Task WriteAsync(
        NetworkStream stream,
        int status,
        string contentType,
        string body,
        CancellationToken cancellationToken) =>
        WriteAsync(stream, status, contentType, Encoding.UTF8.GetBytes(body), cancellationToken);

    private static async Task WriteAsync(
        NetworkStream stream,
        int status,
        string contentType,
        byte[] body,
        CancellationToken cancellationToken)
    {
        var head = string.Create(
            CultureInfo.InvariantCulture,
            $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        404 => "Not Found",
        405 => "Method Not Allowed",
        503 => "Service Unavailable",
        _ => "Unknown",
    };

    /// <summary>A parsed request line plus its headers.</summary>
    private sealed record RequestHead(string Method, string Path, IReadOnlyDictionary<string, string> Headers)
    {
        public static RequestHead? Parse(string head)
        {
            var lines = head.Split("\r\n");
            var request = lines[0].Split(' ');

            if (request.Length < 2)
            {
                return null;
            }

            var target = request[1];
            var query = target.IndexOf('?', StringComparison.Ordinal);
            if (query >= 0)
            {
                target = target[..query];
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 1; index < lines.Length; index++)
            {
                var colon = lines[index].IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                {
                    headers[lines[index][..colon].Trim()] = lines[index][(colon + 1)..].Trim();
                }
            }

            return new RequestHead(request[0], target, headers);
        }

        public string? Header(string name) => Headers.GetValueOrDefault(name);
    }
}
