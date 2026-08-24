using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FrameLink.Agent;
using FrameLink.Agent.Local;
using FrameLink.Agent.Resources;

namespace FrameLink.Tests;

/// <summary>
/// §2.1's embedded app and §2.7's one local origin — the server that replaces v1's
/// <c>framelink-spa.service</c>, its git checkout and the GPIO daemon's second WebSocket port.
/// </summary>
public sealed class AgentLocalOriginTests : IAsyncLifetime
{
    private static readonly CancellationToken None = TestContext.Current.CancellationToken;

    private readonly TemporaryStore _store = new();
    private readonly LocalChannel _channel = new();
    private readonly ManualClock _clock = new();
    private readonly RecordingLog _log = new();
    private LocalOrigin _origin = null!;

    public ValueTask InitializeAsync()
    {
        _origin = new LocalOrigin(
            _channel,
            _clock,
            _log,
            () => AppConfigCatalog.Issued(_store.Store),
            port: 0);

        Assert.True(_origin.Start());
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _origin.DisposeAsync();
        _store.Dispose();
    }

    [Fact]
    public void The_product_app_is_inside_the_binary()
    {
        // §2.1: "the agent serves the app from its own binary, so the app can never drift from the
        // agent managing it". Not a directory beside it, not a checkout, not a mount.
        var paths = EmbeddedApp.Paths;

        Assert.Contains("index.html", paths);
        Assert.Contains("frame-app.js", paths);
        Assert.Contains("frame-stage.js", paths);
        Assert.Contains("vendor/lit-all.min.js", paths);
        Assert.Contains("vendor/livekit-client.umd.js", paths);

        // The build embeds these on whatever host it runs on, and %(RecursiveDir) carries that
        // host's separator. A backslash here would mean the served URL depends on where the binary
        // was built — the workstation for tests, an arm64 container for the frame (§5.2).
        Assert.All(paths, path => Assert.DoesNotContain('\\', path));

        // The five values it templates are Fleet-Manager-supplied now, served from /config.json.
        // Shipping the example beside them would give the app a second, stale place to look.
        Assert.DoesNotContain("config.example.json", paths);
    }

    [Fact]
    public void The_call_address_and_the_call_token_are_one_credential_from_one_document()
    {
        var app = AgentButtonTests.Asset("frame-app.js");
        var livekit = AgentButtonTests.Asset("livekit.js");

        // §3.7 made the URL and the secret internal details of the Fleet Manager, which mints the
        // token and supplies call.livekitUrl together. A compiled-in address is a second source
        // for one half of that pair, and the failure it produces is the expensive kind: a frame
        // holding a token this server really did sign, dialling a server that has never heard of
        // it. The rejection reads as a call fault and the cause is a configuration one.
        Assert.DoesNotContain("10.20.30.250", app, StringComparison.Ordinal);
        Assert.DoesNotContain("ws://", app, StringComparison.Ordinal);
        Assert.DoesNotContain("wss://", app, StringComparison.Ordinal);

        // And nothing dials half a pair. The check lives beside the connect call rather than in
        // the view, so the rule survives a second caller.
        Assert.Contains("export function callable(", livekit, StringComparison.Ordinal);
        Assert.Contains("config.livekitUrl && config.token", livekit, StringComparison.Ordinal);
        Assert.Contains("callable(this.config)", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_on_the_frame_asks_a_person_for_a_token_or_remembers_one()
    {
        // v1's setup screen collected a hand-pasted token and kept it in localStorage. §3.7 moved
        // minting into the Fleet Manager — the API secret is generated there, written 0600 and
        // shown on no surface — so there is nowhere left for anybody to obtain a token by hand,
        // and a screen asking for one asks for something nobody can supply. That is the defect
        // guide 8 had, in the browser.
        Assert.DoesNotContain("frame-setup.js", EmbeddedApp.Paths);

        var app = AgentButtonTests.Asset("frame-app.js");
        Assert.DoesNotContain("frame-setup", app, StringComparison.Ordinal);

        // The stored credential goes with it, and for a reason that outlives the screen: the token
        // is app.config.livekit-token's value, recorded by the resource that owns it, and a copy in
        // the browser is a second writer for it — one that survives the document being withdrawn.
        // A page that cannot fetch /config.json now has no call to place, which is the honest
        // answer for a frame whose settings have not been issued (§3.3).
        Assert.DoesNotContain("localStorage", app, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Slash_answers_200_which_is_what_the_resource_and_the_kiosk_guard_both_check()
    {
        var status = await LoopbackProbe.StatusAsync(_origin.Port, "/", None);
        Assert.Equal(200, status);

        var resource = new LocalOriginResource(_origin);
        Assert.True((await resource.ObserveAsync(None)).InSync);
    }

    [Fact]
    public async Task Modules_are_served_with_a_content_type_a_browser_will_execute()
    {
        using var client = new HttpClient();
        using var response = await client.GetAsync(
            new Uri($"http://127.0.0.1:{_origin.Port}/frame-stage.js"),
            None);

        Assert.True(response.IsSuccessStatusCode);

        // A module served as application/octet-stream is refused outright, and the page is blank
        // with one console line — the "broken desktop" §2.7's fallback rule exists to catch,
        // arriving from the one place the agent itself controls.
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_frame_that_has_been_issued_nothing_says_so_rather_than_serving_an_empty_document()
    {
        using var client = new HttpClient();
        using var pending = await client.GetAsync(new Uri($"http://127.0.0.1:{_origin.Port}/config.json"), None);

        // §3.3: a pending device receives nothing. Handing the page an empty document would have
        // it try to join a call with no identity rather than wait to be adopted.
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, pending.StatusCode);

        _store.Store.WriteText("app.identity", "framelink-douwe");
        _store.Store.WriteText("app.room", "family");
        _store.Store.WriteText("app.livekit-url", "ws://10.20.30.250:7880");
        _store.Store.WriteText("app.immich-kiosk-url", "http://127.0.0.1:3000/?duration=30");
        _store.Store.WriteText("app.livekit-token", "a.b.c");

        using var issued = await client.GetAsync(new Uri($"http://127.0.0.1:{_origin.Port}/config.json"), None);
        var document = JsonSerializer.Deserialize(
            await issued.Content.ReadAsStringAsync(None),
            AgentJson.Default.AppConfigDocument);

        Assert.NotNull(document);
        Assert.Equal("framelink-douwe", document.Identity);
        Assert.Equal("family", document.Room);
        Assert.Equal("a.b.c", document.Token);

        // The address travels in the same document as the token, which is what lets the page
        // refuse to dial on half a pair. The app carries no address of its own to fall back to.
        Assert.Equal("ws://10.20.30.250:7880", document.LivekitUrl);
    }

    [Fact]
    public async Task Nothing_outside_the_embedded_app_is_reachable()
    {
        using var client = new HttpClient();

        using var missing = await client.GetAsync(new Uri($"http://127.0.0.1:{_origin.Port}/nope.js"), None);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);

        using var traversal = await client.GetAsync(
            new Uri($"http://127.0.0.1:{_origin.Port}/../../etc/shadow"),
            None);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, traversal.StatusCode);
    }

    [Fact]
    public async Task The_page_checks_in_over_the_channel_and_the_agent_narrates_back()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_origin.Port}{LocalOrigin.ChannelPath}"), None);

        var hello = JsonSerializer.SerializeToUtf8Bytes(
            new PageMessage
            {
                Kind = PageMessage.KindHello,
                Identity = "framelink-douwe",
                Room = "family",
                HasToken = true,
            },
            AgentJson.Default.PageMessage);

        await socket.SendAsync(hello, WebSocketMessageType.Text, endOfMessage: true, None);

        await WaitFor(() => _channel.LastCheckInUtc is not null);

        Assert.Equal(_clock.UtcNow, _channel.LastCheckInUtc);
        Assert.Equal("framelink-douwe", _channel.LastReport?.Identity);
        Assert.True(_channel.LastReport?.HasToken);

        // The agent's page renders in that same browser (§2.7 stage 2), so the narration has to
        // travel the other way down the same channel.
        await _channel.PublishAsync(
            new StageMessage
            {
                // The shape a repairing frame actually sends, and the reason decision 82 took
                // `Reconciling` off the ladder: the rung is what the Fleet Manager said, so an
                // adopted frame stays `InSync` there, and `ProductRuns` false is the drift the
                // frame observed of itself. This fixture used to seed `Reconciling`, which is a
                // condition no frame has ever been able to put on this channel.
                Condition = "InSync",
                ProductRuns = false,
                Detected = "The speaker volume setting is not what it should be.",
            },
            None);

        var buffer = new byte[4096];
        var received = await socket.ReceiveAsync(buffer, None);
        var stage = JsonSerializer.Deserialize(
            Encoding.UTF8.GetString(buffer, 0, received.Count),
            AgentJson.Default.StageMessage);

        Assert.False(stage?.ProductRuns);
        Assert.Equal("The speaker volume setting is not what it should be.", stage?.Detected);
    }

    [Fact]
    public async Task A_call_ending_reaches_the_camera_recycle_and_a_tap_reaches_the_countdown()
    {
        var callEnded = 0;
        var rebootAsked = 0;
        _channel.CallEnded += () => Interlocked.Increment(ref callEnded);
        _channel.RebootRequested += () => Interlocked.Increment(ref rebootAsked);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_origin.Port}{LocalOrigin.ChannelPath}"), None);

        foreach (var kind in new[] { PageMessage.KindCallEnded, PageMessage.KindRebootNow })
        {
            await socket.SendAsync(
                JsonSerializer.SerializeToUtf8Bytes(new PageMessage { Kind = kind }, AgentJson.Default.PageMessage),
                WebSocketMessageType.Text,
                endOfMessage: true,
                None);
        }

        await WaitFor(() => Volatile.Read(ref callEnded) == 1 && Volatile.Read(ref rebootAsked) == 1);

        Assert.Equal(1, Volatile.Read(ref callEnded));
        Assert.Equal(1, Volatile.Read(ref rebootAsked));
    }

    [Fact]
    public async Task A_disconnected_page_is_dropped_rather_than_retried()
    {
        // The v1 LiveKit post-mortem as a design constraint: a retry loop that leaks is worse than
        // an outage. A send that throws unregisters the peer instead of accumulating handlers.
        using var attachment = _channel.Attach((_, _) => throw new IOException("the browser went away"));

        Assert.Equal(1, _channel.Peers);

        await _channel.PublishAsync(new StageMessage { Condition = "InSync", ProductRuns = true }, None);

        Assert.Equal(0, _channel.Peers);
    }

    [Fact]
    public void The_websocket_accept_key_is_the_one_RFC_6455_specifies()
    {
        // The worked example from RFC 6455 §1.3. It is asserted because the handshake is written
        // by hand: get this wrong and every browser refuses the channel, which would take the
        // liveness signal and the repair screen with it.
        Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", LocalOrigin.AcceptKey("dGhlIHNhbXBsZSBub25jZQ=="));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10, None);
        }

        Assert.True(condition(), "the condition never became true");
    }

    [Fact]
    public async Task The_accept_loop_is_a_supervised_loop_and_ending_is_the_thing_it_reports()
    {
        // <b>The fifteenth concurrent thing, which was in no list at all.</b> Start() puts the
        // accept loop on a fire-and-forget task against the origin's own token, and until now the
        // only thing that ever awaited it was DisposeAsync — so an accept that failed took the
        // frame's local HTTP server away permanently, the page had nothing to check in to, §2.7's
        // fallback rule tore the browser session down, and the repair screen it fell back to was
        // served by the same dead listener. Nothing anywhere said a word.
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(None);
        var watching = _origin.RunAsync(stopping.Token);

        // It does not return while the origin is serving.
        Assert.False(watching.IsCompleted);

        // And it returns quietly when the agent is stopping, which must never read as a failure.
        await stopping.CancelAsync();
        await watching;

        Assert.DoesNotContain(_log.Lines, line => line.Contains("stopped accepting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_origin_that_was_never_started_says_so_rather_than_watching_nothing()
    {
        // The other way the local server can be absent, and the one a resource repairs: the port is
        // held by something else, Start() returned false, and there is no accept loop at all. The
        // watch reports it as the failure it is instead of returning as though it had watched
        // something.
        var log = new RecordingLog();
        await using var origin = new LocalOrigin(
            _channel,
            _clock,
            log,
            () => AppConfigCatalog.Issued(_store.Store),
            port: 0);

        await origin.RunAsync(None);

        Assert.Contains(log.Lines, line => line.Contains("was never started", StringComparison.Ordinal));
    }
}
