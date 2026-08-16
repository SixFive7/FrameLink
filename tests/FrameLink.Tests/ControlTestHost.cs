using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using FrameLink.Control;
using FrameLink.Control.Authentication;
using FrameLink.Control.Storage;
using FrameLink.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;

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
/// <remarks>
/// Deleting the directory needs whoever opened a database inside it to have released the file
/// first, which <see cref="SqliteDatabase.Dispose"/> now does for its own connection string.
/// This class must not reach for <c>SqliteConnection.ClearAllPools()</c> to force the issue:
/// that call is process-global, and calling it from one test's teardown disposes connections
/// belonging to every other test running in parallel.
/// </remarks>
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
        Telemetry = new SqliteFleetTelemetryStore(Database);
        Packages = new SqlitePackageStore(Database, NullLogger<SqlitePackageStore>.Instance);
    }

    /// <summary>The test clock the stores stamp rows with.</summary>
    public TestClock Clock { get; }

    /// <summary>The open database.</summary>
    public SqliteDatabase Database { get; }

    /// <summary>Device repository under test.</summary>
    public IDeviceStore Devices { get; }

    /// <summary>Settings repository under test.</summary>
    public ISettingsStore Settings { get; }

    /// <summary>Reconciliation reports and device events (§3.5).</summary>
    public IFleetTelemetryStore Telemetry { get; }

    /// <summary>Per-device package inventories, content-addressed.</summary>
    public IPackageStore Packages { get; }

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
    /// <param name="operatorPassword">The operator password, or null to leave it unconfigured.</param>
    /// <param name="configure">Adjusts the options the server is built with.</param>
    /// <param name="webRoot">
    /// Directory to serve static files from, standing in for the built GUI. Omitted, the server
    /// runs with no <c>wwwroot</c> at all, which is the shape of an image built before the
    /// Svelte output existed — and the shape every other test in the suite wants.
    /// </param>
    public static async Task<ControlServer> StartAsync(
        string? operatorPassword,
        Func<ControlOptions, ControlOptions>? configure = null,
        string? webRoot = null)
    {
        var workspace = new TempWorkspace();

        var options = new ControlOptions
        {
            DataDirectory = workspace.Root,
            ReleaseDirectory = workspace.ReleaseDirectory,

            // Probes often, hangs up slowly. The interval stays in milliseconds because
            // several tests wait to watch a probe arrive. The deadline does not: almost no
            // test in this suite answers a ping, so a short one made every one of those a
            // socket the server was entitled to abort in the middle of an assertion about
            // something else entirely — "is this device online", "did my report arrive" —
            // giving each of them a hidden few-hundred-millisecond budget and a flake on any
            // machine slow enough to exceed it. The two tests the deadline actually belongs
            // to name their own; see ControlPresenceTests.Liveness.
            PingInterval = TimeSpan.FromMilliseconds(80),
            PongDeadline = TimeSpan.FromSeconds(30),
            HandshakeTimeout = TimeSpan.FromSeconds(10),
            ReaperInterval = TimeSpan.FromHours(1),
        };

        if (configure is not null)
        {
            options = configure(options);
        }

        string[] args =
        [
            "--urls",
            "http://127.0.0.1:0",

            // The slim builder reads command-line configuration, so this is the whole of
            // the test logging setup. Kestrel's per-request Information logging would
            // otherwise bury a failure message under a few hundred lines of transcript.
            "--Logging:LogLevel:Default=Warning",
            "--Logging:LogLevel:Microsoft=Warning",

            // Same mechanism: `webroot` is a host configuration key, so passing it here is
            // how a test gives the server a GUI without one existing beside the test binary.
            .. webRoot is null ? (string[])[] : ["--webroot", webRoot],
        ];

        var app = ControlApp.Build(
            args,
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

    /// <summary>Adopts a device through the operator route.</summary>
    public async Task AdoptAsync(string deviceId, string? name = null)
    {
        var query = name is null ? string.Empty : "?name=" + Uri.EscapeDataString(name);
        var response = await Client.PostAsync(
            $"/api/devices/{deviceId}/adopt{query}",
            content: null,
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>Reads a device's live reconciliation state (§3.5).</summary>
    public async Task<DeviceReconcileResponse> GetReconcileAsync(string deviceId)
    {
        var response = await Client.GetAsync(
            $"/api/devices/{deviceId}/reconcile",
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.ReadAsync(ControlJson.Default.DeviceReconcileResponse);
    }

    /// <summary>
    /// Runs a device through the whole enrollment dance and returns its id.
    /// </summary>
    /// <remarks>
    /// Pending handshake, sign in, adopt. Four lines that appear at the top of nearly every test
    /// that needs an adopted frame, and that say nothing about what the test is for.
    /// </remarks>
    /// <param name="key">The device keypair.</param>
    /// <param name="password">The operator password, or null when this client is already signed in.</param>
    public async Task<string> EnrolAsync(ECDsa key, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(key);

        await using (var pending = await ConnectAgentAsync(key))
        {
            if (pending.Result.Status != HandshakeStatus.Pending)
            {
                throw new InvalidOperationException(
                    $"A first connect should be answered '{HandshakeStatus.Pending}', not '{pending.Result.Status}'.");
            }
        }

        if (password is not null)
        {
            await SignInAsync(password);
        }

        var deviceId = DeviceIdentity.FingerprintOf(key.ExportSubjectPublicKeyInfo());
        await AdoptAsync(deviceId);
        return deviceId;
    }

    /// <summary>Reads a device's package inventory view.</summary>
    public async Task<DevicePackagesResponse> GetPackagesAsync(string deviceId)
    {
        var response = await Client.GetAsync(
            $"/api/devices/{deviceId}/packages",
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.ReadAsync(ControlJson.Default.DevicePackagesResponse);
    }

    /// <summary>Reads the fleet-wide package comparison.</summary>
    public async Task<FleetPackagesResponse> GetFleetPackagesAsync()
    {
        var response = await Client.GetAsync("/api/packages", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.ReadAsync(ControlJson.Default.FleetPackagesResponse);
    }

    /// <summary>Polls the device package route until it satisfies a condition, or gives up.</summary>
    public async Task<DevicePackagesResponse> WaitForPackagesAsync(
        string deviceId,
        Func<DevicePackagesResponse, bool> condition,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        DevicePackagesResponse latest;

        do
        {
            latest = await GetPackagesAsync(deviceId);
            if (condition(latest))
            {
                return latest;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        return latest;
    }

    /// <summary>Polls the fleet package route until it satisfies a condition, or gives up.</summary>
    public async Task<FleetPackagesResponse> WaitForFleetPackagesAsync(
        Func<FleetPackagesResponse, bool> condition,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        FleetPackagesResponse latest;

        do
        {
            latest = await GetFleetPackagesAsync();
            if (condition(latest))
            {
                return latest;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        return latest;
    }

    /// <summary>Reads a device's recent events.</summary>
    public async Task<DeviceEventsResponse> GetEventsAsync(string deviceId, int limit = 50)
    {
        var response = await Client.GetAsync(
            $"/api/devices/{deviceId}/events?limit={limit}",
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.ReadAsync(ControlJson.Default.DeviceEventsResponse);
    }

    /// <summary>Polls the reconcile route until it satisfies a condition, or gives up.</summary>
    /// <remarks>
    /// The agent's message crosses a real socket and is stored on the server's own task, so the
    /// route is eventually consistent with the send by a few milliseconds. Polling is the honest
    /// way to wait for that; asserting immediately would be a flake generator.
    /// </remarks>
    public async Task<DeviceReconcileResponse> WaitForReconcileAsync(
        string deviceId,
        Func<ReconcileReport?, bool> condition,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        DeviceReconcileResponse latest;

        do
        {
            latest = await GetReconcileAsync(deviceId);
            if (condition(latest.Report))
            {
                return latest;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        return latest;
    }

    /// <summary>Polls the events route until it satisfies a condition, or gives up.</summary>
    public async Task<DeviceEventsResponse> WaitForEventsAsync(
        string deviceId,
        Func<IReadOnlyList<DeviceEvent>, bool> condition,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        DeviceEventsResponse latest;

        do
        {
            latest = await GetEventsAsync(deviceId);
            if (condition(latest.Events))
            {
                return latest;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        return latest;
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
    private readonly Channel<WireEnvelope> _inbound =
        Channel.CreateUnbounded<WireEnvelope>(new UnboundedChannelOptions { SingleWriter = true });

    private readonly CancellationTokenSource _reading;
    private readonly Task _pump;
    private int _answeredPings;

    private TestAgent(ClientWebSocket socket, string deviceId, HandshakeResult result)
    {
        _socket = socket;
        DeviceId = deviceId;
        Result = result;

        // The read side starts here and never stops until the socket does. See PumpAsync for why
        // it cannot be one receive per caller.
        _reading = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        _pump = PumpAsync(_reading.Token);
    }

    /// <summary>The fingerprint this agent proved.</summary>
    public string DeviceId { get; }

    /// <summary>The server's verdict.</summary>
    public HandshakeResult Result { get; }

    /// <summary>Whether the server left the socket open after answering.</summary>
    public bool IsOpen => _socket.State is WebSocketState.Open;

    /// <summary>How many pings this agent has answered since it connected.</summary>
    /// <remarks>
    /// The unit a liveness test should measure survival in. Elapsed milliseconds are a proxy for
    /// it that stops being accurate exactly when the machine is loaded, which is exactly when a
    /// liveness assertion is most likely to be wrong for reasons that are not the code's fault.
    /// </remarks>
    public int AnsweredPings => Volatile.Read(ref _answeredPings);

    /// <summary>True once the socket has closed and every frame it delivered has been read.</summary>
    private bool InboundExhausted => _inbound.Reader.Completion.IsCompleted;

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

    /// <summary>
    /// Reads the next envelope, or null if none arrived within the timeout and none ever will.
    /// </summary>
    /// <remarks>
    /// The timeout is taken against the queue the pump fills, never against the socket. Timing
    /// out a read here is therefore an observation about traffic and nothing else — it leaves
    /// the connection exactly as it found it, which is the assumption every caller of this
    /// method was already making.
    /// </remarks>
    public async Task<WireEnvelope?> ReceiveAsync(TimeSpan timeout)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            return await _inbound.Reader.ReadAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ChannelClosedException)
        {
            // The socket closed and everything it delivered has been read.
            return null;
        }
    }

    /// <summary>Waits for the socket to stop being open, or gives up.</summary>
    /// <remarks>
    /// A close only becomes visible once a receive observes it, and the pump is the only thing
    /// receiving — so the close is awaited where it actually happens rather than polled for.
    /// </remarks>
    public async Task<bool> WaitForCloseAsync(TimeSpan timeout)
    {
        try
        {
            await _pump.WaitAsync(timeout, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
        }

        return _socket.State is not WebSocketState.Open;
    }

    /// <summary>Sends a reconciliation report on the <c>telemetry</c> channel (§4.1).</summary>
    public Task SendReportAsync(ReconcileReport report) =>
        SendAsync(
            _socket,
            ControlWire.KindReconcileReport,
            report,
            ProtocolJson.Default.ReconcileReport,
            ProtocolConstants.ChannelTelemetry);

    /// <summary>Sends a package inventory on the <c>telemetry</c> channel (§4.1).</summary>
    public Task SendPackagesAsync(PackageInventory inventory) =>
        SendAsync(
            _socket,
            ControlWire.KindPackageInventory,
            inventory,
            ProtocolJson.Default.PackageInventory,
            ProtocolConstants.ChannelTelemetry);

    /// <summary>Sends one device event on the <c>events</c> channel (§4.1).</summary>
    public Task SendEventAsync(DeviceEvent deviceEvent) =>
        SendAsync(
            _socket,
            ControlWire.KindDeviceEvent,
            deviceEvent,
            ProtocolJson.Default.DeviceEvent,
            ProtocolConstants.ChannelEvents);

    /// <summary>Sends a well-formed envelope whose payload the server cannot read.</summary>
    /// <remarks>
    /// The shape a newer agent, or a damaged one, produces: the frozen envelope parses and the
    /// body does not. §4.2 makes that legible on purpose, so it must not close the socket.
    /// </remarks>
    public async Task SendGarbageOnAsync(string kind, string channel)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            $$"""{"magic":"framelink","kind":"{{kind}}","channel":"{{channel}}","payload":"not an object"}""");

        await _socket.SendAsync(
            bytes,
            WebSocketMessageType.Text,
            endOfMessage: true,
            TestContext.Current.CancellationToken);
    }

    /// <summary>Sends a pong, the way a live agent answers the server's liveness probe.</summary>
    public Task PongAsync(long sequence) =>
        SendAsync(
            _socket,
            ControlWire.KindPong,
            new AgentPong { Sequence = sequence },
            ProtocolJson.Default.AgentPong,
            ProtocolConstants.ChannelControl);

    /// <summary>Behaves like a healthy agent until cancelled: answers every ping, drops the rest.</summary>
    /// <remarks>
    /// The open-ended form of <see cref="AnswerPingsAsync"/>, for a test that has to assert
    /// something about a device <i>while</i> it is still answering. Pumping for a fixed
    /// duration and asserting afterwards leaves the socket silent for however long the
    /// assertion takes, and a silent socket is precisely what the server is built to hang up
    /// on — so the assertion races the mechanism it is trying to prove benign.
    /// A socket that goes away underneath it ends the loop quietly, so that the test's own
    /// assertions report the failure rather than an exception thrown out of a background task.
    /// </remarks>
    public async Task AnswerPingsUntilAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !InboundExhausted)
            {
                var envelope = await ReceiveAsync(TimeSpan.FromMilliseconds(100));
                if (envelope is not null
                    && string.Equals(envelope.Kind, ControlWire.KindPing, StringComparison.Ordinal))
                {
                    var ping = envelope.PayloadAs(ProtocolJson.Default.AgentPing);
                    await PongAsync(ping?.Sequence ?? 0);
                    Interlocked.Increment(ref _answeredPings);
                }
            }
        }
        catch (WebSocketException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Waits until this agent has answered <paramref name="count"/> pings.</summary>
    /// <remarks>
    /// What a liveness test should wait for instead of sleeping. A fixed sleep asserts that the
    /// machine was fast enough to fit the cycles into the window, because a loaded one completes
    /// fewer of them and fails an assertion about the connection on the strength of it. Waiting
    /// for the cycles themselves makes a slow machine slow rather than red, and every extra
    /// millisecond it takes is extra silence the connection demonstrably survived.
    /// </remarks>
    public async Task<bool> WaitForAnsweredPingsAsync(int count, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (AnsweredPings >= count)
            {
                return true;
            }

            // A torn-down socket delivers no further pings, so a genuine failure ends the wait
            // where it happened rather than at the timeout.
            if (InboundExhausted)
            {
                return false;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        return AnsweredPings >= count;
    }

    /// <summary>
    /// Behaves like a healthy agent for a while: answers every ping and collects everything
    /// else that arrives.
    /// </summary>
    /// <returns>Envelopes received that were not pings.</returns>
    public async Task<IReadOnlyList<WireEnvelope>> AnswerPingsAsync(TimeSpan duration)
    {
        var others = new List<WireEnvelope>();
        var deadline = DateTimeOffset.UtcNow + duration;

        try
        {
            while (DateTimeOffset.UtcNow < deadline && !InboundExhausted)
            {
                var envelope = await ReceiveAsync(TimeSpan.FromMilliseconds(100));
                if (envelope is null)
                {
                    continue;
                }

                if (string.Equals(envelope.Kind, ControlWire.KindPing, StringComparison.Ordinal))
                {
                    var ping = envelope.PayloadAs(ProtocolJson.Default.AgentPing);
                    await PongAsync(ping?.Sequence ?? 0);
                    Interlocked.Increment(ref _answeredPings);
                }
                else
                {
                    others.Add(envelope);
                }
            }
        }
        catch (WebSocketException)
        {
        }
        catch (OperationCanceledException)
        {
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
                // Output half only. CloseAsync waits for the peer's answering close, and waiting
                // means receiving — which is the pump's job and cannot be done twice at once on
                // one socket.
                await _socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "done",
                    CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
        }
        catch (OperationCanceledException)
        {
        }

        // The pump ends by itself as soon as the peer tears the connection down, which it does
        // on seeing the close frame already flushed above. Cancelling is only the fallback for a
        // peer that leaves the socket hanging, and it aborts nothing that is still needed.
        try
        {
            await _pump.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            await _reading.CancelAsync();
            await _pump;
        }
        catch (OperationCanceledException)
        {
            await _pump;
        }

        _reading.Dispose();
        _socket.Dispose();
    }

    /// <summary>
    /// Drains the socket into <see cref="_inbound"/> for as long as the connection lives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The read side has to be one uninterrupted loop, and this is the reason: cancelling a
    /// <see cref="ClientWebSocket"/> receive does not abandon the read, it aborts the
    /// connection. A "wait up to N milliseconds for a frame" built directly on the socket
    /// therefore destroys the thing the caller is about to assert against — and only on the
    /// runs where the frame happened to be late, which is what made it a flake rather than a
    /// bug. Draining here and timing out against the channel keeps a timeout meaning "nothing
    /// arrived", which is what every caller already assumed it meant.
    /// </para>
    /// <para>
    /// Frames that do not decode are dropped rather than treated as a close, matching what the
    /// server does with unreadable traffic on its side (§4.2).
    /// </para>
    /// </remarks>
    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];

        try
        {
            while (true)
            {
                using var assembled = new MemoryStream();
                WebSocketReceiveResult received;

                do
                {
                    received = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    if (received.MessageType is WebSocketMessageType.Close)
                    {
                        return;
                    }

                    assembled.Write(buffer, 0, received.Count);
                }
                while (!received.EndOfMessage);

                if (WireMessage.Decode(assembled.ToArray()) is { } envelope)
                {
                    await _inbound.Writer.WriteAsync(envelope, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _inbound.Writer.TryComplete();
        }
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
