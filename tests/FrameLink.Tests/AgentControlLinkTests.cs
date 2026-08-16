using FrameLink.Agent.Hosting;
using FrameLink.Agent.Identity;
using FrameLink.Agent.Link;
using FrameLink.Agent.State;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// The control link's behaviour — the frozen handshake of version2.md §4.2 and the reconnect
/// discipline of §4.1.
/// </summary>
public sealed class AgentControlLinkTests
{
    private static readonly Uri Public = new("https://framelink.example.org/");
    private static readonly Uri Lan = new("http://192.168.1.9:8080/");

    [Fact]
    public async Task The_handshake_runs_on_every_connect_not_only_the_first()
    {
        // §4.2, verbatim: "Every socket opens with a version handshake — on every connect, not
        // just the first." It is how a frame finds out that the answer changed underneath it: an
        // operator pressed Adopt, or Block, or reverted the container tag.
        var server = new RecordingServer(AgentServerScript.Pending());

        await RunAsync(server, attempts: 4);

        Assert.Equal(4, server.Connections.Count);
        Assert.All(server.Connections, connection => Assert.NotNull(connection.Hello));
    }

    [Fact]
    public async Task The_hello_carries_the_identity_the_fleet_manager_needs_to_show_a_row()
    {
        var server = new RecordingServer(AgentServerScript.Pending());

        var (_, identity) = await RunAsync(server, attempts: 1, serial: "10000000abcd1234");

        var hello = server.Connections[0].Hello!;
        Assert.Equal(ProtocolConstants.Version, hello.ProtocolVersion);
        Assert.Equal(identity, hello.DeviceId);
        Assert.Equal("10000000abcd1234", hello.HardwareSerial);
        Assert.NotEmpty(hello.PublicKey);
        Assert.NotEmpty(hello.Nonce);
    }

    [Fact]
    public async Task Every_connection_signs_a_fresh_challenge()
    {
        // The proof binds to both nonces, so a captured one is worthless on the next connection.
        var server = new RecordingServer(AgentServerScript.Pending());

        await RunAsync(server, attempts: 3);

        var nonces = server.Connections.Select(c => c.Hello!.Nonce).ToList();
        var proofs = server.Connections.Select(c => c.Proof!.Signature).ToList();

        Assert.Equal(3, nonces.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3, proofs.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task The_proof_verifies_against_the_device_id_the_agent_claimed()
    {
        var server = new RecordingServer(AgentServerScript.Pending());

        var (_, identity) = await RunAsync(server, attempts: 1);

        var connection = server.Connections[0];
        Assert.True(DeviceIdentity.VerifyProof(
            connection.Hello!.PublicKey,
            identity,
            connection.Hello.Nonce,
            connection.ServerNonce!,
            connection.Proof!.Signature));
    }

    [Fact]
    public async Task A_pending_verdict_puts_the_frame_on_the_adoption_rung()
    {
        var server = new RecordingServer(AgentServerScript.Pending());

        var (hub, _) = await RunAsync(server, attempts: 1);

        Assert.Equal(HandshakeStatus.Pending, hub.Current.LastAuthoritative!.Cause);
        Assert.False(hub.Current.ProductRuns);
    }

    [Fact]
    public async Task Adoption_pushed_over_the_live_connection_flips_the_frame_without_reconnecting()
    {
        // §3.5 makes connection presence *be* online status, so a pending frame holds the socket
        // open — which is also what lets the operator's Adopt land on the screen in the same
        // second rather than at the next backoff tick.
        var server = new RecordingServer(AgentServerScript.Pending(), AgentServerScript.Ok("Hallway"));

        var (hub, _) = await RunAsync(server, attempts: 1);

        Assert.Equal(HandshakeStatus.Ok, hub.Current.LastAuthoritative!.Cause);
        Assert.True(hub.Current.ProductRuns);
        Assert.Single(server.Connections);
    }

    [Fact]
    public async Task A_verdict_hands_the_served_version_to_whoever_asked_to_be_told()
    {
        var seen = new List<HandshakeResult>();
        var server = new RecordingServer(AgentServerScript.Ok(servedVersion: "0.4.0"));

        await RunAsync(server, attempts: 1, onVerdict: (verdict, _) =>
        {
            seen.Add(verdict);
            return Task.CompletedTask;
        });

        Assert.Equal("0.4.0", Assert.Single(seen).ServedAgentVersion);
    }

    [Fact]
    public async Task A_frame_that_was_green_keeps_showing_photos_when_the_server_goes_quiet()
    {
        // §2.6: rejection is an answer, silence is not. This is that rule reached through the real
        // loop rather than through the ladder in isolation.
        var server = new RecordingServer(AgentServerScript.Ok());

        var (hub, _) = await RunAsync(server, attempts: 1);
        Assert.True(hub.Current.ProductRuns);

        server.Refuse = true;
        var afterOutage = await ContinueAsync(server, hub, attempts: 3);

        Assert.Equal(DeviceState.NoContact, afterOutage.Current.Condition.State);
        Assert.True(afterOutage.Current.ProductRuns);
    }

    [Fact]
    public async Task A_frame_that_was_never_adopted_shows_nothing_when_the_server_goes_quiet()
    {
        var server = new RecordingServer(AgentServerScript.Pending());

        var (hub, _) = await RunAsync(server, attempts: 1);
        server.Refuse = true;
        var afterOutage = await ContinueAsync(server, hub, attempts: 3);

        Assert.Equal(DeviceState.NoContact, afterOutage.Current.Condition.State);
        Assert.False(afterOutage.Current.ProductRuns);
    }

    [Fact]
    public async Task The_endpoints_are_tried_in_order_with_the_public_url_leading()
    {
        // §4.3: the list is ordered by preference, so a failure rotates to the LAN address and the
        // public URL leads again on the attempt after.
        var server = new RecordingServer(AgentServerScript.Pending()) { Refuse = true };
        var hub = new AgentStatusHub(AgentStatusFactory.Starting());
        using var key = DeviceKey.From(DeviceIdentity.CreateKeyPair());
        var clock = new ManualClock();
        using var stop = new CancellationTokenSource();

        var link = new ControlLink(server, hub, key, clock, NullLog.Instance, () => [Public, Lan], Fast());
        clock.OnDelay = _ =>
        {
            if (link.CompletedAttempts >= 4)
            {
                stop.Cancel();
            }
        };

        await link.RunAsync(stop.Token);

        Assert.Equal([Public, Lan, Public, Lan], server.Attempted);
    }

    [Fact]
    public async Task A_frame_with_no_address_yet_waits_and_says_so_rather_than_spinning()
    {
        var server = new RecordingServer(AgentServerScript.Pending());
        var hub = new AgentStatusHub(AgentStatusFactory.Starting());
        using var key = DeviceKey.From(DeviceIdentity.CreateKeyPair());
        var clock = new ManualClock();
        using var stop = new CancellationTokenSource();

        var link = new ControlLink(server, hub, key, clock, NullLog.Instance, () => [], Fast());
        clock.OnDelay = _ =>
        {
            if (clock.Delays.Count >= 3)
            {
                stop.Cancel();
            }
        };

        await link.RunAsync(stop.Token);

        Assert.Empty(server.Attempted);
        Assert.Contains("No Fleet Manager address", hub.Current.Condition.ServerMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_captive_portal_is_reported_as_the_wrong_endpoint_rather_than_a_crash()
    {
        var factory = new TrackingTransportFactory { Behaviour = ServerBehaviour.SendGarbage };
        var hub = new AgentStatusHub(AgentStatusFactory.Starting());
        using var key = DeviceKey.From(DeviceIdentity.CreateKeyPair());
        var clock = new ManualClock();
        using var stop = new CancellationTokenSource();

        var link = new ControlLink(factory, hub, key, clock, NullLog.Instance, () => [Public], Fast());
        clock.OnDelay = _ =>
        {
            if (link.CompletedAttempts >= 2)
            {
                stop.Cancel();
            }
        };

        await link.RunAsync(stop.Token);

        Assert.Equal(DeviceState.NoContact, hub.Current.Condition.State);
        Assert.False(hub.Current.Connected);
    }

    private static Backoff Fast() => new(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), jitter: 0);

    [Fact]
    public async Task A_retry_pushed_down_a_live_socket_reaches_the_reconciler()
    {
        // §2.5 rung 3's whole delivery path in one assertion. It matters that this is a *message*
        // rather than anything the frame can do to itself: the attempt ledger is durable across
        // the reboot §2.4 takes and the update §2.8 brings, so nothing an operator can reach from
        // the frame's own side clears it.
        var server = new RecordingServer(AgentServerScript.Ok());
        var received = new List<RetryRequest>();

        server.PushAfterHandshake.Enqueue(WireMessage.Encode(
            ControlWire.KindRetry,
            new RetryRequest
            {
                DeviceId = "AAAA-AAAA-AAAA-AAAA",
                Resource = "boot.autologin.getty-tty1",
                RequestedUtc = DateTimeOffset.UnixEpoch,
            },
            ProtocolJson.Default.RetryRequest,
            ProtocolConstants.ChannelControl));

        await RunAsync(server, attempts: 1, onRetry: received.Add);

        var retry = Assert.Single(received);
        Assert.Equal("boot.autologin.getty-tty1", retry.Resource);
    }

    [Fact]
    public async Task A_retry_the_payload_of_which_cannot_be_read_does_not_take_the_socket_with_it()
    {
        // The same forward-compatibility the package inventory bought going the other way: a newer
        // server that reshapes this payload must cost an ignored message, not a reconnect storm on
        // every frame in the fleet.
        var server = new RecordingServer(AgentServerScript.Ok());
        var received = new List<RetryRequest>();

        server.PushAfterHandshake.Enqueue(WireMessage.EncodeRaw(
            ControlWire.KindRetry,
            System.Text.Json.JsonDocument.Parse("\"not an object\"").RootElement.Clone(),
            ProtocolConstants.ChannelControl));

        var (hub, _) = await RunAsync(server, attempts: 1, onRetry: received.Add);

        Assert.Empty(received);
        Assert.Single(server.Connections);
        Assert.NotNull(hub);
    }

    private static async Task<(AgentStatusHub Hub, string DeviceId)> RunAsync(
        RecordingServer server,
        int attempts,
        string? serial = null,
        Func<HandshakeResult, CancellationToken, Task>? onVerdict = null,
        Action<RetryRequest>? onRetry = null)
    {
        var hub = new AgentStatusHub(AgentStatusFactory.Starting());
        using var key = DeviceKey.From(DeviceIdentity.CreateKeyPair());
        using var stop = new CancellationTokenSource();
        var clock = new ManualClock();

        var link = new ControlLink(server, hub, key, clock, NullLog.Instance, () => [Public], Fast(), onVerdict)
        {
            HardwareSerial = serial,
            OnRetry = onRetry,
        };

        clock.OnDelay = _ =>
        {
            if (link.CompletedAttempts >= attempts)
            {
                stop.Cancel();
            }
        };

        await link.RunAsync(stop.Token);
        return (hub, key.DeviceId);
    }

    private static async Task<AgentStatusHub> ContinueAsync(RecordingServer server, AgentStatusHub hub, int attempts)
    {
        using var key = DeviceKey.From(DeviceIdentity.CreateKeyPair());
        using var stop = new CancellationTokenSource();
        var clock = new ManualClock();

        var link = new ControlLink(server, hub, key, clock, NullLog.Instance, () => [Public], Fast());
        clock.OnDelay = _ =>
        {
            if (link.CompletedAttempts >= attempts)
            {
                stop.Cancel();
            }
        };

        await link.RunAsync(stop.Token);
        return hub;
    }
}

/// <summary>
/// A Fleet Manager that answers the frozen handshake and remembers what it was told.
/// </summary>
/// <remarks>
/// Keeps strong references to every connection on purpose — the opposite of
/// <see cref="TrackingTransportFactory"/>, which keeps only weak ones. Here the point is to
/// inspect what the agent said; there the point is to prove nothing is holding on.
/// </remarks>
internal sealed class RecordingServer : IControlTransportFactory
{
    private readonly Queue<HandshakeResult> _verdicts;
    private readonly HandshakeResult _last;

    public RecordingServer(params HandshakeResult[] verdicts)
    {
        _verdicts = new Queue<HandshakeResult>(verdicts);
        _last = verdicts[^1];
    }

    public bool Refuse { get; set; }

    /// <summary>Frames to push down the live socket once the handshake is done.</summary>
    /// <remarks>
    /// The operator acting on a frame that is already connected — pressing retry, changing a
    /// setting. Scripted here rather than through <see cref="_verdicts"/> because those are
    /// handshake results and these are ordinary control traffic; conflating them would make a
    /// test of the dispatch a test of the handshake.
    /// </remarks>
    public Queue<byte[]> PushAfterHandshake { get; } = new();

    public List<RecordedConnection> Connections { get; } = [];

    public List<Uri> Attempted { get; } = [];

    public ValueTask<IControlTransport> ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        Attempted.Add(endpoint);

        if (Refuse)
        {
            throw new IOException("Connection refused.");
        }

        var connection = new RecordedConnection(this);
        Connections.Add(connection);
        return ValueTask.FromResult<IControlTransport>(connection);
    }

    public HandshakeResult NextVerdict() => _verdicts.Count > 0 ? _verdicts.Dequeue() : _last;

    public bool HasMoreVerdicts => _verdicts.Count > 0;
}

/// <summary>One scripted server-side connection.</summary>
internal sealed class RecordedConnection : IControlTransport
{
    private readonly RecordingServer _server;
    private readonly Queue<byte[]> _outbound = new();
    private bool _greeted;

    public RecordedConnection(RecordingServer server) => _server = server;

    public HandshakeHello? Hello { get; private set; }

    public HandshakeProof? Proof { get; private set; }

    public string? ServerNonce { get; private set; }

    public bool IsDisposed { get; private set; }

    public ValueTask SendAsync(ReadOnlyMemory<byte> utf8, CancellationToken cancellationToken)
    {
        var envelope = WireMessage.Decode(utf8.Span)
            ?? throw new InvalidOperationException("The agent sent something that is not FrameLink traffic.");

        if (string.Equals(envelope.Kind, WireMessage.KindHello, StringComparison.Ordinal))
        {
            Hello = envelope.PayloadAs(ProtocolJson.Default.HandshakeHello);
            ServerNonce = DeviceIdentity.NewNonce();
            _outbound.Enqueue(WireMessage.Encode(
                WireMessage.KindChallenge,
                new HandshakeChallenge { Nonce = ServerNonce },
                ProtocolJson.Default.HandshakeChallenge));
        }
        else if (string.Equals(envelope.Kind, WireMessage.KindProof, StringComparison.Ordinal))
        {
            Proof = envelope.PayloadAs(ProtocolJson.Default.HandshakeProof);
            _outbound.Enqueue(WireMessage.Encode(
                WireMessage.KindResult,
                _server.NextVerdict(),
                ProtocolJson.Default.HandshakeResult));
            _greeted = true;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_outbound.Count > 0)
        {
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(_outbound.Dequeue());
        }

        if (_greeted && _server.PushAfterHandshake.Count > 0)
        {
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(_server.PushAfterHandshake.Dequeue());
        }

        // Once the handshake is done, push any remaining scripted verdicts down the live socket —
        // this is the operator pressing Adopt while the frame is connected.
        if (_greeted && _server.HasMoreVerdicts)
        {
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(WireMessage.Encode(
                WireMessage.KindResult,
                _server.NextVerdict(),
                ProtocolJson.Default.HandshakeResult));
        }

        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}
