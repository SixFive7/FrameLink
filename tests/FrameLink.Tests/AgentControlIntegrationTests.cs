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
        Assert.Equal(VirtualFrame.StatusText, pending.AgentStatus);
        Assert.Equal(ProtocolConstants.Version, pending.ProtocolVersion);
        Assert.True(pending.ProtocolCompatible);
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
        var response = await server.Client.PostAsJsonAsync(
            $"/api/devices/{deviceId}/adopt",
            new AdoptRequest { Name = name },
            ControlJson.Default.AdoptRequest,
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
    /// <summary>The free-text self-report of §4.2, asserted end to end.</summary>
    public const string StatusText = "integration harness, endpoints resolved by test";

    /// <summary>The board serial of §3.3's bench matching, asserted end to end.</summary>
    public const string Serial = "10000000feedface";

    private readonly ControlServer _server;
    private readonly DeviceKey _key;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _running;

    private VirtualFrame(ControlServer server, DeviceKey key, AgentStatusHub hub, ControlLink link)
    {
        _server = server;
        _key = key;
        Hub = hub;
        Link = link;
        _running = link.RunAsync(_stop.Token);
    }

    /// <summary>The status the console stage would be painting.</summary>
    public AgentStatusHub Hub { get; }

    /// <summary>The loop under test.</summary>
    public ControlLink Link { get; }

    /// <summary>The device identity this frame proves on every connect.</summary>
    public string DeviceId => _key.DeviceId;

    /// <summary>The agent's current view of itself.</summary>
    public AgentStatus Status => Hub.Current;

    /// <summary>Starts a frame pointed at <paramref name="server"/>.</summary>
    public static Task<VirtualFrame> StartAsync(ControlServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        var key = DeviceKey.From(DeviceIdentity.CreateKeyPair());
        var hub = new AgentStatusHub(new AgentStatus
        {
            Condition = DeviceStateLadder.Starting,
            DeviceId = key.DeviceId,
            HardwareSerial = Serial,
        });

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
            AgentStatusText = StatusText,
            HandshakeTimeout = TimeSpan.FromSeconds(10),
        };

        return Task.FromResult(new VirtualFrame(server, key, hub, link));
    }

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
            await _running.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // The loop returning through its own cancellation is the ordinary way out.
        }
        catch (TimeoutException)
        {
            // Reported by the assertions, not by an exception thrown while tearing down.
        }

        _stop.Dispose();
        _key.Dispose();
    }
}
