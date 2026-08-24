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
    public async Task A_throttled_frame_keeps_its_photos_and_the_answer_it_was_last_given()
    {
        // §3.3's per-device budget, from the frame's side. Being asked to knock less often is not
        // being told anything about adoption, so a green frame stays green — and stays green
        // through the outage after it, which is the failure this would otherwise cause: a throttle
        // recorded as the last authoritative answer makes §2.6's "was it fully green" compute
        // false, and the photos go off because a server asked for a pause.
        var green = new RecordingServer(AgentServerScript.Ok());
        var (hub, _) = await RunAsync(green, attempts: 1);
        Assert.True(hub.Current.ProductRuns);

        var throttling = new RecordingServer(AgentServerScript.RateLimited());
        var afterThrottle = await ContinueAsync(throttling, hub, attempts: 3);

        Assert.Equal(HandshakeStatus.Ok, afterThrottle.Current.LastAuthoritative!.Cause);
        Assert.Equal(DeviceState.NoContact, afterThrottle.Current.Condition.State);
        Assert.True(afterThrottle.Current.ProductRuns);
    }

    [Fact]
    public async Task A_throttle_is_repeated_to_the_reader_in_the_servers_own_words()
    {
        // Legible rather than silent: the frame did get an answer, and the sentence the server
        // chose is the one on the screen. What it is not is a rung of the ladder — §1.2.3 wants
        // every abnormal state named, and "the server is busy" is not a state this frame is in.
        var throttling = new RecordingServer(AgentServerScript.RateLimited());

        var (hub, _) = await RunAsync(throttling, attempts: 2);

        Assert.Contains(
            "reconnected too often",
            hub.Current.Condition.ServerMessage ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Equal(DeviceStateLadder.SilenceCause, hub.Current.Condition.Cause);
        Assert.Null(hub.Current.LastAuthoritative);
    }

    [Fact]
    public async Task A_throttle_pushed_down_a_live_session_is_ignored_rather_than_believed()
    {
        // The other door a result can come through. Nothing sends one here today, but a build that
        // believed it would let a message about knocking replace a message about adoption, and the
        // frame would go dark mid-session with the socket still open.
        var server = new RecordingServer(AgentServerScript.Ok("Hallway"), AgentServerScript.RateLimited());

        var (hub, _) = await RunAsync(server, attempts: 1);

        Assert.Equal(HandshakeStatus.Ok, hub.Current.LastAuthoritative!.Cause);
        Assert.True(hub.Current.ProductRuns);
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

    private static Backoff Fast() => new(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2));

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

    [Fact]
    public async Task A_shutdown_pushed_down_a_live_socket_reaches_the_power_switch_and_not_the_retry()
    {
        // Decision 92, and the whole reason it is a kind of its own. A shutdown that arrived on the
        // retry's path would clear budgets and reconcile a frame whose operator had asked for it to
        // be off, so the dispatch is asserted from both sides: the shutdown hook is called, and the
        // retry hook is not.
        var server = new RecordingServer(AgentServerScript.Ok());
        var shutdowns = new List<ShutdownRequest>();
        var retries = new List<RetryRequest>();

        server.PushAfterHandshake.Enqueue(WireMessage.Encode(
            ControlWire.KindShutdown,
            new ShutdownRequest
            {
                DeviceId = "AAAA-AAAA-AAAA-AAAA",
                RequestedUtc = DateTimeOffset.UnixEpoch,
            },
            ProtocolJson.Default.ShutdownRequest,
            ProtocolConstants.ChannelControl));

        await RunAsync(server, attempts: 1, onRetry: retries.Add, onShutdown: shutdowns.Add);

        var shutdown = Assert.Single(shutdowns);
        Assert.Equal("AAAA-AAAA-AAAA-AAAA", shutdown.DeviceId);
        Assert.Equal(DateTimeOffset.UnixEpoch, shutdown.RequestedUtc);
        Assert.Empty(retries);
    }

    [Fact]
    public async Task A_shutdown_nothing_is_listening_for_is_skipped_rather_than_guessed_at()
    {
        // The degradation this kind was chosen for, asserted rather than assumed: a build with no
        // shutdown hook wired does nothing at all and keeps its socket. An agent that instead fell
        // through to the retry would be the failure the kind exists to prevent, and one that dropped
        // the connection would be a reconnect storm across a fleet on the day the server learns a
        // new verb.
        var server = new RecordingServer(AgentServerScript.Ok());
        var retries = new List<RetryRequest>();

        server.PushAfterHandshake.Enqueue(WireMessage.Encode(
            ControlWire.KindShutdown,
            new ShutdownRequest { DeviceId = "AAAA-AAAA-AAAA-AAAA", RequestedUtc = DateTimeOffset.UnixEpoch },
            ProtocolJson.Default.ShutdownRequest,
            ProtocolConstants.ChannelControl));

        var (hub, _) = await RunAsync(server, attempts: 1, onRetry: retries.Add);

        Assert.Empty(retries);
        Assert.Single(server.Connections);
        Assert.NotNull(hub);
    }

    [Fact]
    public async Task A_shutdown_the_payload_of_which_cannot_be_read_does_not_take_the_socket_with_it()
    {
        var server = new RecordingServer(AgentServerScript.Ok());
        var shutdowns = new List<ShutdownRequest>();

        server.PushAfterHandshake.Enqueue(WireMessage.EncodeRaw(
            ControlWire.KindShutdown,
            System.Text.Json.JsonDocument.Parse("\"not an object\"").RootElement.Clone(),
            ProtocolConstants.ChannelControl));

        var (hub, _) = await RunAsync(server, attempts: 1, onShutdown: shutdowns.Add);

        Assert.Empty(shutdowns);
        Assert.Single(server.Connections);
        Assert.NotNull(hub);
    }

    [Fact]
    public void The_hello_carries_what_the_loop_is_now_and_not_what_it_was_at_startup()
    {
        // The defect, at the layer that composes the sentence. `AgentStatusText` used to be a
        // string set once when the process started; a frame that had converged, or given up, went
        // on claiming `Progressing` on every connect for as long as the process lived.
        var hub = new AgentStatusHub(AgentStatusFactory.Starting());
        using var uplink = new AgentUplink();
        using var reporter = new AgentStatusReporter(hub, uplink, NullLog.Instance, "AAAA-BBBB-CCCC-DDDD", "linux-arm64");

        Assert.Equal("linux-arm64", reporter.Hello());

        Reconcile(hub, LoopStateNames.Reconciling);
        Assert.Equal("Progressing(linux-arm64)", reporter.Hello());

        Reconcile(hub, LoopStateNames.Converged);
        Assert.Equal("InSync(linux-arm64)", reporter.Hello());

        Reconcile(hub, LoopStateNames.Escalated);
        Assert.Equal("Escalated(linux-arm64)", reporter.Hello());
    }

    [Fact]
    public async Task A_self_report_that_has_not_changed_is_not_sent_again()
    {
        // A pass on a converged frame publishes to the hub every few minutes and says the same
        // thing every time (§2.2 — a pass is a sweep of observations). Re-sending on each of those
        // would put a message on the wire per frame per pass to say nothing at all.
        var hub = new AgentStatusHub(AgentStatusFactory.Starting());
        using var uplink = new AgentUplink();
        using var reporter = new AgentStatusReporter(hub, uplink, NullLog.Instance, "AAAA-BBBB-CCCC-DDDD", "linux-arm64");
        await using var transport = new RecordingUplink();
        using var attached = uplink.Attach(transport);

        using var stop = new CancellationTokenSource();
        var running = reporter.RunAsync(stop.Token);

        Reconcile(hub, LoopStateNames.Converged);
        Assert.True(await transport.WaitForAsync(1), "the change was never pushed");

        // Two more passes that observe the same thing, and one that is a different loop state but
        // the same §2.3 term — backing off is progressing, and the operator's row does not move.
        Reconcile(hub, LoopStateNames.Converged);
        Reconcile(hub, LoopStateNames.Reconciling);
        Reconcile(hub, LoopStateNames.BackingOff);

        Assert.True(await transport.WaitForAsync(2), "the move away from converged was never pushed");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await stop.CancelAsync();
        await running;

        Assert.Equal(2, transport.Sent.Count);
        Assert.Equal(2, reporter.Sent);
        Assert.Equal("InSync(linux-arm64)", StatusOf(transport.Sent[0]));
        Assert.Equal("Progressing(linux-arm64)", StatusOf(transport.Sent[1]));
    }

    [Fact]
    public async Task A_frame_with_no_session_buffers_nothing_and_lets_the_next_hello_say_it()
    {
        // Deliberately unlike every other thing the agent sends (§4.1). A self-report is the
        // current picture, and §4.2 puts a handshake on every connect — so a buffered one could
        // only ever arrive stale, behind the hello that already said the same thing or better.
        var hub = new AgentStatusHub(AgentStatusFactory.Starting());
        using var uplink = new AgentUplink();
        using var reporter = new AgentStatusReporter(hub, uplink, NullLog.Instance, "AAAA-BBBB-CCCC-DDDD", "linux-arm64");

        using var stop = new CancellationTokenSource();
        var running = reporter.RunAsync(stop.Token);

        Reconcile(hub, LoopStateNames.Escalated);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await stop.CancelAsync();
        await running;

        Assert.Equal(0, reporter.Sent);
        Assert.Equal("Escalated(linux-arm64)", reporter.Hello());
    }

    private static void Reconcile(AgentStatusHub hub, string loopState) =>
        hub.Publish(status => status with
        {
            Reconcile = status.Reconcile with { LoopState = loopState },
        });

    private static string? StatusOf(ReadOnlyMemory<byte> frame) =>
        WireMessage.Decode(frame.Span)?.PayloadAs(ProtocolJson.Default.AgentStatusUpdate)?.Status;

    private static async Task<(AgentStatusHub Hub, string DeviceId)> RunAsync(
        RecordingServer server,
        int attempts,
        string? serial = null,
        Func<HandshakeResult, CancellationToken, Task>? onVerdict = null,
        Action<RetryRequest>? onRetry = null,
        Action<ShutdownRequest>? onShutdown = null)
    {
        var hub = new AgentStatusHub(AgentStatusFactory.Starting());
        using var key = DeviceKey.From(DeviceIdentity.CreateKeyPair());
        using var stop = new CancellationTokenSource();
        var clock = new ManualClock();

        var link = new ControlLink(server, hub, key, clock, NullLog.Instance, () => [Public], Fast(), onVerdict)
        {
            HardwareSerial = serial,
            OnRetry = onRetry,
            OnShutdown = onShutdown,
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

/// <summary>
/// A live session as far as <see cref="AgentUplink"/> is concerned: it records and never answers.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="RecordedConnection"/>. That one plays a scripted handshake for the
/// reconnect loop; this one exists for the other thing an attempt publishes its transport for —
/// the long-lived senders that write to a session they do not own — and the only question those
/// tests ask is what bytes went up.
/// </remarks>
internal sealed class RecordingUplink : IControlTransport
{
    private readonly List<ReadOnlyMemory<byte>> _sent = [];
    private readonly Lock _gate = new();

    /// <summary>Every frame this transport was handed, in order.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> Sent
    {
        get
        {
            lock (_gate)
            {
                return [.. _sent];
            }
        }
    }

    /// <summary>Waits until at least <paramref name="count"/> frames have arrived, or gives up.</summary>
    public async Task<bool> WaitForAsync(int count)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (Sent.Count >= count)
            {
                return true;
            }

            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        return Sent.Count >= count;
    }

    /// <inheritdoc/>
    public ValueTask SendAsync(ReadOnlyMemory<byte> utf8, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _sent.Add(utf8);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
