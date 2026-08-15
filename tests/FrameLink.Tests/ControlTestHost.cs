using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using FrameLink.Control;
using FrameLink.Control.Authentication;
using FrameLink.Control.Storage;
using FrameLink.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;

namespace FrameLink.Tests;

/// <summary>
/// A clock the tests move by hand.
/// </summary>
/// <remarks>
/// The Fleet Manager's abuse controls are all time-shaped — a rate limit window, a pending
/// TTL, a pong deadline, a session lifetime — and every one of them is worthless if it is
/// only ever asserted by sleeping. Advancing an injected clock is what turns "the sweep
/// deletes rows older than the TTL" into something a test can prove in a millisecond.
/// </remarks>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    /// <summary>Starts at a fixed, arbitrary instant so nothing depends on the wall clock.</summary>
    public TestClock()
        : this(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero))
    {
    }

    /// <inheritdoc/>
    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Moves the clock forward.</summary>
    public void Advance(TimeSpan delta) => _now += delta;
}

/// <summary>A throwaway directory that takes its database and release files with it.</summary>
public sealed class TempWorkspace : IDisposable
{
    /// <summary>Creates an empty workspace under the system temp directory.</summary>
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "framelink-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ReleaseDirectory);
    }

    /// <summary>Root of the workspace.</summary>
    public string Root { get; }

    /// <summary>Where a database file goes.</summary>
    public string DatabasePath => Path.Combine(Root, "framelink.db");

    /// <summary>Where served agent binaries go.</summary>
    public string ReleaseDirectory => Path.Combine(Root, "release");

    /// <summary>Writes a fake agent binary for a runtime identifier and returns its path.</summary>
    public string WriteAgentBinary(string runtimeIdentifier, string content, string? version = null)
    {
        var directory = Path.Combine(ReleaseDirectory, runtimeIdentifier);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fl-agent");
        File.WriteAllText(path, content);

        if (version is not null)
        {
            File.WriteAllText(path + ".version", version);
        }

        return path;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // SQLite pools connections, so the file stays open until the pool is cleared. Without
        // this the delete fails on Windows and every test leaves a directory behind.
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// The storage layer wired up over a real SQLite file.
/// </summary>
/// <remarks>
/// A real file rather than an in-memory database or a fake: the behaviours under test —
/// cascade deletes, the adopted-only guard on settings, the eviction ordering — are
/// expressed in SQL, and a fake repository would assert the test double instead of the code
/// that ships.
/// </remarks>
public sealed class StorageFixture : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    /// <summary>Creates the schema against a fresh database file.</summary>
    public StorageFixture(TestClock? clock = null)
    {
        Clock = clock ?? new TestClock();
        Database = new SqliteDatabase(_workspace.DatabasePath);
        Devices = new SqliteDeviceStore(Database, Clock);
        Settings = new SqliteSettingsStore(Database, Clock);
    }

    /// <summary>The test clock the stores stamp rows with.</summary>
    public TestClock Clock { get; }

    /// <summary>The open database.</summary>
    public SqliteDatabase Database { get; }

    /// <summary>Device repository under test.</summary>
    public IDeviceStore Devices { get; }

    /// <summary>Settings repository under test.</summary>
    public ISettingsStore Settings { get; }

    /// <summary>Registers a device by proven contact and returns its row.</summary>
    public Task<DeviceRecord> SeeDeviceAsync(string deviceId, string publicKey = "key", int cap = 100) =>
        Devices.RecordContactAsync(
            new DeviceContact
            {
                DeviceId = deviceId,
                PublicKey = publicKey,
                ProtocolVersion = ProtocolConstants.Version,
                AgentVersion = "1.0.0",
            },
            cap,
            TestContext.Current.CancellationToken);

    /// <inheritdoc/>
    public void Dispose()
    {
        Database.Dispose();
        _workspace.Dispose();
    }
}

/// <summary>
/// The whole Fleet Manager, running on an ephemeral port.
/// </summary>
/// <remarks>
/// §7.2 asks for tests that assert outcomes. The outcome that matters most in M1 — what a
/// device is actually told and actually receives — only exists once a real socket has been
/// through the real pipeline, so these tests drive Kestrel rather than calling a method.
/// </remarks>
public sealed class ControlServer : IAsyncDisposable
{
    private readonly TempWorkspace _workspace;
    private readonly WebApplication _app;

    private ControlServer(TempWorkspace workspace, WebApplication app, Uri baseAddress)
    {
        _workspace = workspace;
        _app = app;
        BaseAddress = baseAddress;
        Client = new HttpClient { BaseAddress = baseAddress };
    }

    /// <summary>The HTTP origin the server bound to.</summary>
    public Uri BaseAddress { get; }

    /// <summary>An HTTP client already pointed at it.</summary>
    public HttpClient Client { get; }

    /// <summary>Where the workspace lives, for writing agent binaries mid-test.</summary>
    public TempWorkspace Workspace => _workspace;

    /// <summary>Starts a server. A null password leaves it unconfigured (§3.2).</summary>
    /// <remarks>
    /// The system clock, deliberately. Ping/pong is driven by a <c>PeriodicTimer</c> built on
    /// the injected <c>TimeProvider</c>, so a frozen clock would mean the liveness mechanism
    /// never runs and the tests that matter most here would pass without exercising anything.
    /// The intervals are shrunk to milliseconds instead, which keeps the real code path.
    /// Time-shaped behaviours with no timer — expiry, rate limit windows, sessions — use
    /// <see cref="TestClock"/> against their own components.
    /// </remarks>
    public static async Task<ControlServer> StartAsync(
        string? operatorPassword,
        Func<ControlOptions, ControlOptions>? configure = null)
    {
        var workspace = new TempWorkspace();

        var options = new ControlOptions
        {
            DataDirectory = workspace.Root,
            ReleaseDirectory = workspace.ReleaseDirectory,

            // Short enough that a liveness test finishes, long enough that an ordinary test
            // never trips it by accident.
            PingInterval = TimeSpan.FromMilliseconds(80),
            PongDeadline = TimeSpan.FromMilliseconds(500),
            HandshakeTimeout = TimeSpan.FromSeconds(10),
            ReaperInterval = TimeSpan.FromHours(1),
        };

        if (configure is not null)
        {
            options = configure(options);
        }

        var app = ControlApp.Build(
            [
                "--urls",
                "http://127.0.0.1:0",

                // The slim builder reads command-line configuration, so this is the whole of
                // the test logging setup. Kestrel's per-request Information logging would
                // otherwise bury a failure message under a few hundred lines of transcript.
                "--Logging:LogLevel:Default=Warning",
                "--Logging:LogLevel:Microsoft=Warning",
            ],
            options,
            OperatorCredential.FromValue(operatorPassword),
            TimeProvider.System);

        await app.StartAsync(TestContext.Current.CancellationToken);

        var address = new Uri(app.Urls.First());
        return new ControlServer(workspace, app, address);
    }

    /// <summary>Reads the device list, signing in first if the caller has not.</summary>
    public async Task<DeviceListResponse> ListDevicesAsync(bool includeBlocked = false)
    {
        var response = await Client.GetAsync(
            $"/api/devices?includeBlocked={(includeBlocked ? "true" : "false")}",
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.ReadAsync(ControlJson.Default.DeviceListResponse);
    }

    /// <summary>Waits for a device row to satisfy a condition, or gives up.</summary>
    /// <remarks>
    /// Presence is the socket (§3.5), and a socket closes asynchronously. Polling a real
    /// server for a state change is honest; asserting immediately would be a flake generator.
    /// </remarks>
    public async Task<bool> WaitForDeviceAsync(
        string deviceId,
        Func<DeviceView, bool> condition,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var devices = await ListDevicesAsync(includeBlocked: true);
            var device = devices.Devices.FirstOrDefault(d => d.DeviceId == deviceId);
            if (device is not null && condition(device))
            {
                return true;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        return false;
    }

    /// <summary>Signs in and attaches the session token to <see cref="Client"/>.</summary>
    public async Task SignInAsync(string password)
    {
        var response = await Client.PostAsJsonAsync(
            "/api/session",
            new LoginRequest { Password = password },
            ControlJson.Default.LoginRequest,
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();

        var login = await response.Content.ReadFromJsonAsync(
            ControlJson.Default.LoginResponse,
            TestContext.Current.CancellationToken);

        Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login!.Token);
    }

    /// <summary>Opens a device connection and runs the frozen handshake.</summary>
    public Task<TestAgent> ConnectAgentAsync(
        ECDsa key,
        int protocolVersion = ProtocolConstants.Version,
        bool signCorrectly = true,
        string? hardwareSerial = null,
        string? agentStatus = null) =>
        TestAgent.ConnectAsync(BaseAddress, key, protocolVersion, signCorrectly, hardwareSerial, agentStatus);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _app.StopAsync(shutdown.Token);
        await _app.DisposeAsync();
        _workspace.Dispose();
    }
}

/// <summary>
/// A device, as far as the wire is concerned.
/// </summary>
/// <remarks>
/// Speaks only the frozen contract in <c>FrameLink.Protocol</c>, so it stays a faithful stand-in
/// for the real agent being built against the same types by a separate workstream.
/// </remarks>
public sealed class TestAgent : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;

    private TestAgent(ClientWebSocket socket, string deviceId, HandshakeResult result)
    {
        _socket = socket;
        DeviceId = deviceId;
        Result = result;
    }

    /// <summary>The fingerprint this agent proved.</summary>
    public string DeviceId { get; }

    /// <summary>The server's verdict.</summary>
    public HandshakeResult Result { get; }

    /// <summary>Whether the server left the socket open after answering.</summary>
    public bool IsOpen => _socket.State is WebSocketState.Open;

    /// <summary>Connects, sends a hello, answers the challenge and reads the verdict.</summary>
    public static async Task<TestAgent> ConnectAsync(
        Uri baseAddress,
        ECDsa key,
        int protocolVersion = ProtocolConstants.Version,
        bool signCorrectly = true,
        string? hardwareSerial = null,
        string? agentStatus = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(key);

        var spki = key.ExportSubjectPublicKeyInfo();
        var deviceId = DeviceIdentity.FingerprintOf(spki);
        var clientNonce = DeviceIdentity.NewNonce();

        var socket = new ClientWebSocket();
        var target = new Uri($"ws://{baseAddress.Authority}/agent");
        await socket.ConnectAsync(target, TestContext.Current.CancellationToken);

        await SendAsync(
            socket,
            WireMessage.KindHello,
            new HandshakeHello
            {
                ProtocolVersion = protocolVersion,
                AgentVersion = "0.0.1+test",
                DeviceId = deviceId,
                PublicKey = Convert.ToBase64String(spki),
                Nonce = clientNonce,
                HardwareSerial = hardwareSerial,
                AgentStatus = agentStatus,
            },
            ProtocolJson.Default.HandshakeHello);

        var challengeEnvelope = await ReceiveAsync(socket)
            ?? throw new InvalidOperationException("The server did not send a challenge.");
        var challenge = challengeEnvelope.PayloadAs(ProtocolJson.Default.HandshakeChallenge)
            ?? throw new InvalidOperationException("The challenge could not be read.");

        // The wrong-signature case signs a nonce the server never issued, which is exactly
        // what a replayed or forged proof looks like.
        var signedServerNonce = signCorrectly ? challenge.Nonce : DeviceIdentity.NewNonce();
        var signature = key.SignData(
            DeviceIdentity.ChallengeBytes(clientNonce, signedServerNonce, deviceId),
            HashAlgorithmName.SHA256);

        await SendAsync(
            socket,
            WireMessage.KindProof,
            new HandshakeProof { Signature = Convert.ToBase64String(signature) },
            ProtocolJson.Default.HandshakeProof);

        var resultEnvelope = await ReceiveAsync(socket)
            ?? throw new InvalidOperationException("The server did not answer the handshake.");
        var result = resultEnvelope.PayloadAs(ProtocolJson.Default.HandshakeResult)
            ?? throw new InvalidOperationException("The handshake result could not be read.");

        return new TestAgent(socket, deviceId, result);
    }

    /// <summary>Reads the next envelope, or null if the socket closed within the timeout.</summary>
    public async Task<WireEnvelope?> ReceiveAsync(TimeSpan timeout)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            return await ReceiveAsync(_socket, deadline.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (WebSocketException)
        {
            return null;
        }
    }

    /// <summary>Waits for the socket to stop being open, or gives up.</summary>
    public async Task<bool> WaitForCloseAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_socket.State is not WebSocketState.Open)
            {
                return true;
            }

            // A close only becomes visible once a receive observes it, which is precisely how
            // a real agent finds out it was answered and hung up on.
            if (await ReceiveAsync(TimeSpan.FromMilliseconds(150)) is null
                && _socket.State is not WebSocketState.Open)
            {
                return true;
            }
        }

        return _socket.State is not WebSocketState.Open;
    }

    /// <summary>Sends a pong, the way a live agent answers the server's liveness probe.</summary>
    public Task PongAsync(long sequence) =>
        SendAsync(
            _socket,
            ControlWire.KindPong,
            new AgentPong { Sequence = sequence },
            ControlJson.Default.AgentPong,
            ProtocolConstants.ChannelControl);

    /// <summary>
    /// Behaves like a healthy agent for a while: answers every ping and collects everything
    /// else that arrives.
    /// </summary>
    /// <returns>Envelopes received that were not pings.</returns>
    public async Task<IReadOnlyList<WireEnvelope>> AnswerPingsAsync(TimeSpan duration)
    {
        var others = new List<WireEnvelope>();
        var deadline = DateTimeOffset.UtcNow + duration;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var envelope = await ReceiveAsync(TimeSpan.FromMilliseconds(100));
            if (envelope is null)
            {
                continue;
            }

            if (string.Equals(envelope.Kind, ControlWire.KindPing, StringComparison.Ordinal))
            {
                var ping = envelope.PayloadAs(ControlJson.Default.AgentPing);
                await PongAsync(ping?.Sequence ?? 0);
            }
            else
            {
                others.Add(envelope);
            }
        }

        return others;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State is WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
        }
        catch (OperationCanceledException)
        {
        }

        _socket.Dispose();
    }

    private static async Task SendAsync<TPayload>(
        ClientWebSocket socket,
        string kind,
        TPayload payload,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TPayload> typeInfo,
        string? channel = null)
    {
        var bytes = WireMessage.Encode(kind, payload, typeInfo, channel);
        await socket.SendAsync(
            bytes,
            WebSocketMessageType.Text,
            endOfMessage: true,
            TestContext.Current.CancellationToken);
    }

    private static Task<WireEnvelope?> ReceiveAsync(ClientWebSocket socket) =>
        ReceiveAsync(socket, TestContext.Current.CancellationToken);

    private static async Task<WireEnvelope?> ReceiveAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var assembled = new MemoryStream();

        while (true)
        {
            var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (received.MessageType is WebSocketMessageType.Close)
            {
                return null;
            }

            assembled.Write(buffer, 0, received.Count);
            if (received.EndOfMessage)
            {
                return WireMessage.Decode(assembled.ToArray());
            }
        }
    }
}

/// <summary>Small helpers shared by the Fleet Manager tests.</summary>
public static class ControlTestHelpers
{
    /// <summary>Reads a JSON body with a source-generated contract.</summary>
    public static async Task<TValue> ReadAsync<TValue>(
        this HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TValue> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(response);

        var value = await response.Content.ReadFromJsonAsync(
            typeInfo,
            TestContext.Current.CancellationToken);

        return value ?? throw new JsonException($"The response body was not a {typeof(TValue).Name}.");
    }
}
