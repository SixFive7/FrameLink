using System.Net.Http.Json;
using FrameLink.Agent;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Identity;
using FrameLink.Agent.Link;
using FrameLink.Agent.State;
using FrameLink.Agent.Update;
using FrameLink.Control;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// The real agent link against the real Fleet Manager pipeline, in one process.
/// </summary>
/// <remarks>
/// <para>
/// §5.1 puts the walking skeleton before the reconciler because <b>every genuinely unknown risk
/// in M1 is an integration risk</b> — the frozen handshake, adoption, socket liveness, the update
/// path. This file is where those are retired, and it is the only place in the suite where both
/// programs are exercised together.
/// </para>
/// <para>
/// It exists because of a specific failure. The two programs were built concurrently and could
/// not see each other. The Fleet Manager grew an application-level ping on the control channel
/// with a sixty-second missed-pong deadline; the agent's session pump skipped every message that
/// was not a handshake verdict, so it never answered one. Every real connection would have died
/// seventy-five seconds after the handshake and reconnected forever — and both suites were green,
/// because each side tested its own half against its own idea of the other. The same gap had also
/// left the agent polling <c>/agent/release?rid=…</c> for an update feed the server publishes at
/// <c>/agent/release/{rid}</c>.
/// </para>
/// <para>
/// The rule these tests encode: a mirrored contract is only real if something drives both copies
/// of it across a socket. Everything here therefore uses the shipping types — a real
/// <see cref="ControlLink"/> over a real <see cref="WebSocketControlTransportFactory"/> against a
/// real Kestrel-hosted <see cref="ControlServer"/>. No hand-written stand-in for either side.
/// </para>
/// </remarks>
public sealed class AgentControlIntegrationTests
{
    private const string Password = "a-long-operator-passphrase-for-the-fleet";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Liveness intervals small enough that a whole ping/pong conversation fits in a test, and
    /// still far enough apart that scheduling noise cannot fake either outcome.
    /// </summary>
    /// <remarks>
    /// The per-address budget is lifted for the same reason the intervals are shrunk. A frame
    /// reconnects on a schedule measured in seconds; this one reconnects in tens of milliseconds
    /// so a test can watch several attempts, which spends §3.3's real budget in under a second and
    /// would have the limiter — correctly — refusing the very connections under test. The budget
    /// itself is asserted at its real values in <c>ControlAbuseControlTests</c>.
    /// </remarks>
    private static ControlOptions Liveness(ControlOptions options) => options with
    {
        PingInterval = TimeSpan.FromMilliseconds(100),
        PongDeadline = TimeSpan.FromMilliseconds(400),
        RateLimitAttempts = 100_000,
    };

    [Fact]
    public async Task An_adopted_frame_answers_the_pings_and_holds_one_connection_open()
    {
        // THE test of this file. With PongDeadline at 400 ms, an agent that does not answer is
        // disconnected inside half a second; the assertion is that a single connection survives
        // five deadlines and twenty pings without the loop ever starting a new attempt.
        await using var server = await ControlServer.StartAsync(Password, Liveness);
        await server.SignInAsync(Password);
        await using var frame = await VirtualFrame.StartAsync(server);

        await frame.AwaitPendingAsync();
        await AdoptAsync(server, frame.DeviceId, "Hall frame");

        Assert.True(
            await server.WaitForDeviceAsync(frame.DeviceId, d => d.Online, TimeSpan.FromSeconds(10)),
            "the frame never came back online after being adopted");

        var attemptsWhenOnline = frame.Link.CompletedAttempts;
        await Task.Delay(TimeSpan.FromSeconds(2), Token);

        var devices = await server.ListDevicesAsync();
        var device = Assert.Single(devices.Devices, d => d.DeviceId == frame.DeviceId);

        // Still online, and — the part a reconnect would hide — still the *same* connection. A
        // silent agent would show as online again here, having been dropped and rebuilt several
        // times over, so the attempt counter is what distinguishes a live socket from a fast loop.
        Assert.True(device.Online, "the connection did not survive the missed-pong deadline");
        Assert.Equal(attemptsWhenOnline, frame.Link.CompletedAttempts);
        Assert.True(frame.Status.Connected);
        Assert.Equal(FrameLink.Agent.State.DeviceState.InSync, frame.Status.Condition.State);
    }

    [Fact]
    public async Task What_the_frame_claims_is_what_the_fleet_manager_records()
    {
        // The other half of the same class of bug: the hello crosses the wire as camelCase JSON
        // built from a source-generated context on each side, and a disagreement about a field
        // name or its casing is invisible to both unit suites. Reading the values back out of the
        // server's own device list is what makes the agreement observable.
        await using var server = await ControlServer.StartAsync(Password, Liveness);
        await server.SignInAsync(Password);
        await using var frame = await VirtualFrame.StartAsync(server);

        var pending = await frame.AwaitPendingAsync();

        Assert.Equal(frame.DeviceId, pending.DeviceId);
        Assert.Equal(VirtualFrame.Serial, pending.HardwareSerial);
        Assert.Equal(AgentBuild.Version, pending.AgentVersion);
        Assert.Equal(ProtocolConstants.Version, pending.ProtocolVersion);
        Assert.True(pending.ProtocolCompatible);

        // The self-report carries no vocabulary head, because this frame has not published a loop
        // state yet — it says what it knows about itself and claims nothing about its own
        // convergence. The server reads that as `unknown`, which is deliberately not a problem.
        Assert.Equal(VirtualFrame.StatusText, pending.AgentStatus);
        Assert.Equal(AgentHealth.Unknown, pending.Health);
    }

    [Fact]
    public async Task A_converged_frame_tells_the_fleet_list_it_is_in_sync_not_progressing()
    {
        // The defect this pins. The self-report was one `Progressing(...)` string composed when
        // the agent process started and never recomputed, so a frame verified at 81 of 81 kept
        // telling its operator it was part-way through applying something — for the whole of its
        // uptime, and in the direction that hides trouble. §2.6: what a frame says about itself is
        // what it observed, in both directions.
        await using var server = await ControlServer.StartAsync(Password, Liveness);
        await server.SignInAsync(Password);
        await using var frame = await VirtualFrame.StartAsync(server, LoopStateNames.Converged);

        await frame.AwaitPendingAsync();
        await AdoptAsync(server, frame.DeviceId, "Hall frame");

        Assert.True(
            await server.WaitForDeviceAsync(
                frame.DeviceId,
                d => d.Online && d.Health == AgentHealth.InSync,
                TimeSpan.FromSeconds(10)),
            "the fleet list never read this frame as in sync");

        var device = Assert.Single(
            (await server.ListDevicesAsync()).Devices,
            d => d.DeviceId == frame.DeviceId);

        Assert.Equal($"{AgentResourceStatus.InSync}({VirtualFrame.StatusText})", device.AgentStatus);
    }

    [Fact]
    public async Task A_frame_that_converges_mid_session_says_so_without_reconnecting()
    {
        // The half a per-hello fix cannot reach, and the half that matters on a real frame. §4.2
        // puts a handshake on every connect — and a healthy frame never connects again, because
        // the session it opened after its last provisioning reboot simply stays up. Whatever the
        // loop happened to be doing during those seconds is what the operator would read for the
        // rest of the frame's life.
        await using var server = await ControlServer.StartAsync(Password, Liveness);
        await server.SignInAsync(Password);
        await using var frame = await VirtualFrame.StartAsync(server, LoopStateNames.Reconciling);

        await frame.AwaitPendingAsync();
        await AdoptAsync(server, frame.DeviceId, "Hall frame");

        Assert.True(
            await server.WaitForDeviceAsync(
                frame.DeviceId,
                d => d.Online && d.Health == AgentHealth.Working,
                TimeSpan.FromSeconds(10)),
            "the frame never came back online reporting the pass it was running");

        var attempts = frame.Link.CompletedAttempts;
        frame.Reconcile(LoopStateNames.Converged);

        Assert.True(
            await server.WaitForDeviceAsync(
                frame.DeviceId,
                d => d.Health == AgentHealth.InSync,
                TimeSpan.FromSeconds(10)),
            "the fleet list never learned this frame had converged");

        // On the connection it already had. A new attempt here would mean the value only ever
        // travelled in a hello, which is the case this test exists to rule out.
        Assert.Equal(attempts, frame.Link.CompletedAttempts);
        Assert.Equal(1, frame.StatusPushes);

        // And it keeps working in the other direction: a frame that gives up says so on the same
        // socket, which is the whole point of the fleet list — deciding whether to look at a frame.
        frame.Reconcile(LoopStateNames.Escalated);

        Assert.True(
            await server.WaitForDeviceAsync(
                frame.DeviceId,
                d => d.Health == AgentHealth.Degraded,
                TimeSpan.FromSeconds(10)),
            "the fleet list never learned this frame had given up");

        Assert.Equal(attempts, frame.Link.CompletedAttempts);
    }

    [Fact]
    public async Task Adoption_reaches_the_frame_and_the_operators_name_comes_with_it()
    {
        // §5.1's M1 in one method: connect, appear pending, get adopted in the GUI, and find out.
        // The frame learns on its next backoff reconnect, because the server answers a pending
        // handshake and then closes the socket (§3.3 — a pending record allocates no resources on
        // a route that is open to the internet).
        await using var server = await ControlServer.StartAsync(Password, Liveness);
        await server.SignInAsync(Password);
        await using var frame = await VirtualFrame.StartAsync(server);

        await frame.AwaitPendingAsync();
        Assert.Equal(FrameLink.Agent.State.DeviceState.NotAdopted, frame.Status.Condition.State);
        Assert.False(frame.Status.ProductRuns);

        await AdoptAsync(server, frame.DeviceId, "Hall frame");

        Assert.True(
            await frame.WaitForConditionAsync(status =>
                status.Condition.State is FrameLink.Agent.State.DeviceState.InSync),
            "the frame never learned it had been adopted");

        Assert.True(frame.Status.ProductRuns);
        Assert.True(frame.Status.Condition.IsAuthoritative);
    }

    [Fact]
    public async Task A_frame_that_is_answered_and_hung_up_on_backs_off_instead_of_hammering()
    {
        // The server closes every non-ok handshake, so the reconnect loop must treat that as a
        // failed attempt for scheduling purposes even though the handshake itself succeeded.
        // Counting it as a good session reset the schedule to its first-failure delay and put an
        // unadopted frame into a one-second reconnect loop against the one endpoint §3.3's abuse
        // controls exist to protect.
        await using var server = await ControlServer.StartAsync(Password, Liveness);
        await server.SignInAsync(Password);
        await using var frame = await VirtualFrame.StartAsync(server);

        await frame.AwaitPendingAsync();

        Assert.True(
            await frame.WaitForConditionAsync(_ => frame.Link.CompletedAttempts >= 4),
            $"the loop only completed {frame.Link.CompletedAttempts} attempts");

        // Climbing, not pinned at 1. The exact number depends on how many reconnects the wait
        // covered, so the assertion is on the shape of the schedule rather than a value.
        Assert.True(
            frame.Status.Attempt >= 3,
            $"the backoff was still reporting attempt {frame.Status.Attempt} after "
            + $"{frame.Link.CompletedAttempts} answered-and-closed handshakes");
        Assert.True(frame.Status.BackoffTotal > TimeSpan.Zero);
    }

    [Fact]
    public async Task The_frames_update_check_finds_the_release_the_server_actually_publishes()
    {
        // §2.8's hourly out-of-band convergence is the primary mechanism — the handshake only
        // brings it forward — so this route working is what makes every other failure mode
        // self-repairing. Driven with the agent's own HttpReleaseSource against the server's own
        // route, because the two disagreed about the URL and each side's tests agreed with itself.
        await using var server = await ControlServer.StartAsync(Password);
        server.Workspace.WriteAgentBinary("linux-arm64", "the agent binary", version: "0.3.1+a1b2c3d");

        using var http = new HttpClient();
        var source = new HttpReleaseSource(http, NullLog.Instance);

        var release = await source.GetReleaseAsync(server.BaseAddress, "linux-arm64", Token);

        Assert.NotNull(release);
        Assert.Equal("0.3.1+a1b2c3d", release.Version);
        Assert.Equal("linux-arm64", release.RuntimeIdentifier);

        // The URL the metadata advertises has to be one this same server answers, or the check
        // succeeds and the download that follows it does not.
        await using var payload = await source.DownloadAsync(server.BaseAddress, release, Token);
        Assert.NotNull(payload);

        using var reader = new StreamReader(payload);
        Assert.Equal("the agent binary", await reader.ReadToEndAsync(Token));
    }

    [Fact]
    public async Task An_unconfigured_fleet_manager_is_reported_as_such_rather_than_as_silence()
    {
        // §2.6: rejection is an answer and silence is not, and §3.2 makes "the operator has not
        // set the password yet" a designed state the frame renders verbatim. The frame is usually
        // the diagnostic that tells the operator their server is unconfigured.
        await using var server = await ControlServer.StartAsync(operatorPassword: null, Liveness);
        await using var frame = await VirtualFrame.StartAsync(server);

        Assert.True(
            await frame.WaitForConditionAsync(status =>
                status.Condition.State is FrameLink.Agent.State.DeviceState.ControlNotConfigured),
            $"the frame reported {frame.Status.Condition.State} instead of ControlNotConfigured");

        Assert.True(frame.Status.Condition.IsAuthoritative);
        Assert.Contains(
            "FRAMELINK_OPERATOR_PASSWORD",
            frame.Status.Condition.ServerMessage ?? string.Empty,
            StringComparison.Ordinal);
    }

    private static async Task AdoptAsync(ControlServer server, string deviceId, string name)
    {
        var response = await server.Client.PostAsync(
            $"/api/devices/{deviceId}/adopt?name={Uri.EscapeDataString(name)}",
            content: null,
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
    }
}

/// <summary>
/// One frame, running the agent's real connection loop against a real Fleet Manager.
/// </summary>
/// <remarks>
/// Everything the agent would use on hardware except the hardware: a real ECDSA identity, the
/// real <see cref="WebSocketControlTransportFactory"/>, the real handshake, the real session
/// pump and the real backoff. Only the schedule is shortened, and only so a reconnect that
/// takes seconds on a frame takes milliseconds here.
/// </remarks>
internal sealed class VirtualFrame : IAsyncDisposable
{
    /// <summary>
    /// The detail half of §4.2's free-text self-report, asserted end to end.
    /// </summary>
    /// <remarks>
    /// Only the detail, because the head in front of it is the reconciliation loop's own state and
    /// this harness has no loop — <see cref="Reconcile"/> is what stands in for one. A frame that
    /// has not published a loop state reports the detail alone and nothing else, which is what the
    /// seconds before a first pass finishes actually look like.
    /// </remarks>
    public const string StatusText = "integration harness, endpoints resolved by test";

    /// <summary>The board serial of §3.3's bench matching, asserted end to end.</summary>
    public const string Serial = "10000000feedface";

    private readonly ControlServer _server;
    private readonly DeviceKey _key;
    private readonly CancellationTokenSource _stop = new();
    private readonly AgentUplink _uplink;
    private readonly AgentStatusReporter _reporter;
    private readonly Task _running;
    private readonly Task _reporting;

    private VirtualFrame(
        ControlServer server,
        DeviceKey key,
        AgentStatusHub hub,
        ControlLink link,
        AgentUplink uplink,
        AgentStatusReporter reporter)
    {
        _server = server;
        _key = key;
        _uplink = uplink;
        _reporter = reporter;
        Hub = hub;
        Link = link;
        _running = link.RunAsync(_stop.Token);
        _reporting = reporter.RunAsync(_stop.Token);
    }

    /// <summary>The status the console stage would be painting.</summary>
    public AgentStatusHub Hub { get; }

    /// <summary>The loop under test.</summary>
    public ControlLink Link { get; }

    /// <summary>The device identity this frame proves on every connect.</summary>
    public string DeviceId => _key.DeviceId;

    /// <summary>The agent's current view of itself.</summary>
    public AgentStatus Status => Hub.Current;

    /// <summary>How many self-report changes this frame has pushed over a live session.</summary>
    public int StatusPushes => _reporter.Sent;

    /// <summary>Starts a frame pointed at <paramref name="server"/>.</summary>
    /// <param name="server">The Fleet Manager to connect to.</param>
    /// <param name="loopState">
    /// What the reconciliation loop is doing, as it would have published it with its last census —
    /// one of <see cref="LoopStateNames"/>, or null for a frame whose first pass has not finished.
    /// </param>
    public static Task<VirtualFrame> StartAsync(ControlServer server, string? loopState = null)
    {
        ArgumentNullException.ThrowIfNull(server);

        var key = DeviceKey.From(DeviceIdentity.CreateKeyPair());
        var hub = new AgentStatusHub(new AgentStatus
        {
            Condition = DeviceStateLadder.Starting,
            DeviceId = key.DeviceId,
            HardwareSerial = Serial,
            Reconcile = new ReconcileNarration { LoopState = loopState },
        });

        var uplink = new AgentUplink();
        var reporter = new AgentStatusReporter(hub, uplink, NullLog.Instance, key.DeviceId, StatusText);

        var link = new ControlLink(
            new WebSocketControlTransportFactory(),
            hub,
            key,
            new SystemAgentClock(),
            NullLog.Instance,
            () => [server.BaseAddress],

            // Jitter off so a test never waits for a value it cannot predict; the cap is what
            // bounds the whole test, and the schedule's shape is asserted rather than its numbers.
            new Backoff(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(160), jitter: 0))
        {
            HardwareSerial = Serial,
            Uplink = uplink,
            AgentStatusText = reporter.Hello,
            HandshakeTimeout = TimeSpan.FromSeconds(10),
        };

        return Task.FromResult(new VirtualFrame(server, key, hub, link, uplink, reporter));
    }

    /// <summary>Stands in for a reconciliation pass publishing where the loop now stands.</summary>
    /// <remarks>
    /// The real loop publishes exactly this field with every census (<c>ReconcileLoop</c>), so
    /// moving it by hand is the same input the reporter sees on a frame — and the field is the one
    /// authority, which is why the harness sets nothing else.
    /// </remarks>
    public void Reconcile(string loopState) =>
        Hub.Publish(status => status with
        {
            Reconcile = status.Reconcile with { LoopState = loopState },
        });

    /// <summary>Waits for the Fleet Manager to have a row for this frame, and returns it.</summary>
    /// <remarks>Reads through the operator API, so the caller must have signed in first.</remarks>
    public async Task<DeviceView> AwaitPendingAsync()
    {
        Assert.True(
            await _server.WaitForDeviceAsync(DeviceId, static _ => true, TimeSpan.FromSeconds(15)),
            "the frame never registered with the Fleet Manager");

        var devices = await _server.ListDevicesAsync(includeBlocked: true);
        return devices.Devices.First(d => d.DeviceId == DeviceId);
    }

    /// <summary>Polls the agent's own status until it satisfies a condition, or gives up.</summary>
    /// <remarks>
    /// Polling rather than subscribing, because the thing under test is what a person standing in
    /// front of the frame would see: the state the hub actually settles on, not an event it fired
    /// on the way there.
    /// </remarks>
    public async Task<bool> WaitForConditionAsync(Func<AgentStatus, bool> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition(Hub.Current))
            {
                return true;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        return condition(Hub.Current);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();

        try
        {
            await Task.WhenAll(_running, _reporting).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // The loop returning through its own cancellation is the ordinary way out.
        }
        catch (TimeoutException)
        {
            // Reported by the assertions, not by an exception thrown while tearing down.
        }

        _reporter.Dispose();
        _uplink.Dispose();
        _stop.Dispose();
        _key.Dispose();
    }
}
