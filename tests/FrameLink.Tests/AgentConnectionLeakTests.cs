using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Identity;
using FrameLink.Agent.Link;
using FrameLink.Agent.State;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// The reconnect loop's cleanup guarantee — version2.md §4.1's "cleanup per failed attempt ... gets
/// its own test".
/// </summary>
/// <remarks>
/// <para>
/// This file is the whole reason that sentence is in the specification. In v1, a LiveKit retry loop
/// leaked engine and listener state on every failed connect: a measured ~15 MB per minute, which
/// killed a 2 GB frame in under two hours. The failure was invisible in code review because each
/// individual attempt looked correct — only the accumulation across attempts was wrong.
/// </para>
/// <para>
/// So these tests assert accumulation, not correctness of a single attempt. They run the loop
/// hundreds of times and then ask three questions a leaky implementation cannot answer well: is
/// every transport disposed, is every subscription released, and does anything still hold a
/// reference to the objects an attempt created.
/// </para>
/// </remarks>
public sealed class AgentConnectionLeakTests
{
    private const int Attempts = 200;
    private static readonly Uri Endpoint = new("https://framelink.example.org/");

    [Fact]
    public async Task Two_hundred_failed_connects_dispose_every_transport_they_create()
    {
        var factory = new TrackingTransportFactory { Behaviour = ServerBehaviour.RefuseConnection };

        var link = await RunAsync(factory, Attempts);

        Assert.Equal(Attempts, link.CompletedAttempts);
        Assert.Equal(Attempts, factory.Created);
        Assert.Equal(Attempts, factory.Disposed);
        Assert.Equal(0, factory.Live);
    }

    [Theory]
    [InlineData(ServerBehaviour.CloseImmediately)]
    [InlineData(ServerBehaviour.SendGarbage)]
    [InlineData(ServerBehaviour.SendWrongKind)]
    [InlineData(ServerBehaviour.ThrowMidHandshake)]
    [InlineData(ServerBehaviour.CompleteHandshake)]
    public async Task Every_way_a_connection_can_end_still_releases_it(ServerBehaviour behaviour)
    {
        // Failing at the socket is the easy case. These are the ones that leaked in v1: the
        // connection opened, something went wrong later, and the unwind path missed a resource.
        var factory = new TrackingTransportFactory { Behaviour = behaviour };

        var link = await RunAsync(factory, Attempts);

        Assert.Equal(Attempts, link.CompletedAttempts);
        Assert.Equal(Attempts, factory.Created);
        Assert.Equal(Attempts, factory.Disposed);
        Assert.Equal(0, factory.Live);
    }

    [Fact]
    public async Task No_two_attempts_are_ever_alive_at_once()
    {
        // The subtler failure mode: nothing is left undisposed, but a second attempt starts while
        // the first is still unwinding. That turns a bounded cost into an unbounded one under a
        // flapping server, which is precisely when the loop runs hardest.
        var factory = new TrackingTransportFactory { Behaviour = ServerBehaviour.CompleteHandshake };

        var link = await RunAsync(factory, Attempts);

        Assert.Equal(1, factory.MaximumConcurrentLive);
        Assert.Equal(1, link.MaximumConcurrentAttempts);
    }

    [Fact]
    public async Task A_peer_that_streams_unusable_frames_ends_the_session_instead_of_spinning()
    {
        // Found by this suite hanging. The session pump ignored anything it could not decode and
        // read again immediately, so a peer that answers the handshake and then streams junk — a
        // captive portal, a stray proxy — pinned a core at 100% forever, with no backoff and no
        // escalation, for as long as it stayed connected. It is the CPU-shaped twin of the v1
        // memory leak and it is just as fatal on a frame.
        var factory = new ChattyNonsenseFactory();
        var hub = new AgentStatusHub(AgentStatusFactory.Starting());
        using var key = DeviceKey.From(DeviceIdentity.CreateKeyPair());
        using var stop = new CancellationTokenSource();
        var clock = new ManualClock();

        var link = new ControlLink(
            factory,
            hub,
            key,
            clock,
            NullLog.Instance,
            () => [Endpoint],
            new Backoff(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), jitter: 0));

        clock.OnDelay = _ =>
        {
            if (link.CompletedAttempts >= 5)
            {
                stop.Cancel();
            }
        };

        await link.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(5, link.CompletedAttempts);

        // Each session ends after a bounded number of reads rather than looping on the same frame.
        Assert.True(factory.Reads < 5 * 10, $"the pump read {factory.Reads} frames across 5 sessions");
    }

    [Fact]
    public async Task Subscriptions_taken_per_attempt_are_all_released()
    {
        // A listener registered on a long-lived object and never removed is the exact shape of the
        // v1 leak. AgentStatusHub publishes its subscriber count so the claim is checkable.
        var factory = new TrackingTransportFactory { Behaviour = ServerBehaviour.CompleteHandshake };
        var hub = new AgentStatusHub(AgentStatusFactory.Starting());

        await RunAsync(factory, Attempts, hub);

        Assert.Equal(0, hub.SubscriberCount);
    }

    [Fact]
    public async Task Failed_connects_leave_nothing_referencing_the_state_they_created()
    {
        // The strongest available statement of "no accumulating state": after the loop, nothing in
        // the agent is still reachable from any attempt's objects. Counters can be satisfied by an
        // implementation that disposes an object and then keeps holding it; the garbage collector
        // cannot be.
        var tracked = await RunAndForgetAsync(Attempts);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.Equal(Attempts, tracked.Count);
        Assert.DoesNotContain(tracked, reference => reference.IsAlive);
    }

    [Fact]
    public async Task A_failed_connect_releases_the_socket_the_factory_itself_allocated()
    {
        // The one part of the cleanup contract the loop cannot enforce: a factory that throws
        // hands back no handle, so only the factory can release what it allocated. Asserted
        // against the real WebSocket factory rather than a double.
        var sockets = new List<ClientWebSocket>();
        var factory = new WebSocketControlTransportFactory(() =>
        {
            var socket = new ClientWebSocket();
            sockets.Add(socket);
            return socket;
        });

        // Port 1 on loopback: nothing listens there, and the refusal is immediate and local.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await factory.ConnectAsync(new Uri("http://127.0.0.1:1/"), CancellationToken.None));

        var socket = Assert.Single(sockets);
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    [Fact]
    public async Task The_loop_retries_forever_and_never_waits_longer_than_the_cap()
    {
        // §4.1 is "retry forever", not "retry until a budget runs out": an unreachable Fleet
        // Manager is silence, and §2.6 says silence is never an answer to stop on.
        var factory = new TrackingTransportFactory { Behaviour = ServerBehaviour.RefuseConnection };
        var backoff = new Backoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), jitter: 0);
        var clock = new ManualClock();

        var link = await RunAsync(factory, 500, hub: null, clock, backoff);

        Assert.Equal(500, link.CompletedAttempts);
        Assert.All(clock.Delays, delay => Assert.True(delay <= backoff.Cap, $"{delay} exceeded the cap"));
        Assert.Equal(backoff.Cap, clock.Delays[^1]);
    }

    private static Task<ControlLink> RunAsync(
        TrackingTransportFactory factory,
        int attempts,
        AgentStatusHub? hub = null,
        ManualClock? clock = null,
        Backoff? backoff = null)
    {
        hub ??= new AgentStatusHub(AgentStatusFactory.Starting());
        clock ??= new ManualClock();

        return RunCoreAsync(factory, attempts, hub, clock, backoff);
    }

    private static async Task<ControlLink> RunCoreAsync(
        TrackingTransportFactory factory,
        int attempts,
        AgentStatusHub hub,
        ManualClock clock,
        Backoff? backoff)
    {
        using var key = DeviceKey.From(DeviceIdentity.CreateKeyPair());
        using var stop = new CancellationTokenSource();

        var link = new ControlLink(
            factory,
            hub,
            key,
            clock,
            NullLog.Instance,
            () => [Endpoint],
            backoff ?? new Backoff(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(4), jitter: 0));

        clock.OnDelay = _ =>
        {
            if (link.CompletedAttempts >= attempts)
            {
                stop.Cancel();
            }
        };

        await link.RunAsync(stop.Token);
        return link;
    }

    /// <summary>
    /// Runs the loop in a frame that has definitely returned before the collection happens, so no
    /// stack slot can be what is keeping an attempt's objects alive.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<List<WeakReference>> RunAndForgetAsync(int attempts)
    {
        var factory = new TrackingTransportFactory { Behaviour = ServerBehaviour.CompleteHandshake };
        await RunAsync(factory, attempts);
        return factory.Tracked;
    }
}

/// <summary>
/// A peer that completes the handshake and then never stops sending things the agent cannot use.
/// </summary>
internal sealed class ChattyNonsenseFactory : IControlTransportFactory
{
    private int _reads;

    public int Reads => Volatile.Read(ref _reads);

    public ValueTask<IControlTransport> ConnectAsync(Uri endpoint, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IControlTransport>(new ChattyNonsenseTransport(this));

    public void NoteRead() => Interlocked.Increment(ref _reads);
}

/// <summary>One such connection.</summary>
internal sealed class ChattyNonsenseTransport : IControlTransport
{
    private readonly ChattyNonsenseFactory _owner;
    private readonly Queue<byte[]> _handshake = new();

    public ChattyNonsenseTransport(ChattyNonsenseFactory owner) => _owner = owner;

    public ValueTask SendAsync(ReadOnlyMemory<byte> utf8, CancellationToken cancellationToken)
    {
        var envelope = WireMessage.Decode(utf8.Span);

        if (string.Equals(envelope?.Kind, WireMessage.KindHello, StringComparison.Ordinal))
        {
            _handshake.Enqueue(WireMessage.Encode(
                WireMessage.KindChallenge,
                new HandshakeChallenge { Nonce = DeviceIdentity.NewNonce() },
                ProtocolJson.Default.HandshakeChallenge));
        }
        else if (string.Equals(envelope?.Kind, WireMessage.KindProof, StringComparison.Ordinal))
        {
            _handshake.Enqueue(WireMessage.Encode(
                WireMessage.KindResult,
                AgentServerScript.Ok(),
                ProtocolJson.Default.HandshakeResult));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_handshake.Count > 0)
        {
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(_handshake.Dequeue());
        }

        _owner.NoteRead();
        return ValueTask.FromResult<ReadOnlyMemory<byte>?>("<html>captive portal</html>"u8.ToArray());
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>The reconnect schedule itself.</summary>
public sealed class AgentBackoffTests
{
    [Fact]
    public void No_failures_means_no_wait()
    {
        var backoff = new Backoff(jitter: 0);

        Assert.Equal(TimeSpan.Zero, backoff.Delay(0));
    }

    [Fact]
    public void The_delay_doubles_until_it_reaches_the_cap_and_then_stops()
    {
        var backoff = new Backoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8), jitter: 0);

        Assert.Equal(TimeSpan.FromSeconds(1), backoff.Delay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.Delay(2));
        Assert.Equal(TimeSpan.FromSeconds(4), backoff.Delay(3));
        Assert.Equal(TimeSpan.FromSeconds(8), backoff.Delay(4));
        Assert.Equal(TimeSpan.FromSeconds(8), backoff.Delay(5));
    }

    [Theory]
    [InlineData(40)]
    [InlineData(1000)]
    [InlineData(int.MaxValue)]
    public void A_frame_whose_server_is_gone_for_good_still_gets_a_sane_delay(int failures)
    {
        // Doubling into a TimeSpan overflows to a negative value around the fortieth failure, which
        // on a frame is roughly a month of a genuinely absent Fleet Manager — rare enough to ship
        // and catastrophic enough to matter.
        var backoff = new Backoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), jitter: 0);

        var delay = backoff.Delay(failures);

        Assert.Equal(TimeSpan.FromSeconds(30), delay);
    }

    [Fact]
    public void Jitter_only_ever_shortens_the_wait_and_stays_inside_its_fraction()
    {
        var full = new Backoff(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), jitter: 0.2, fraction: () => 1.0);
        var none = new Backoff(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), jitter: 0.2, fraction: () => 0.0);

        Assert.Equal(TimeSpan.FromSeconds(8), full.Delay(1));
        Assert.Equal(TimeSpan.FromSeconds(10), none.Delay(1));
    }
}
