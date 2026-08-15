using System.Net.Http.Json;
using System.Security.Cryptography;
using FrameLink.Control;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// The frozen handshake over a real socket, driven end to end through the real pipeline.
/// </summary>
/// <remarks>
/// <para>
/// §2.6 states the rule these tests exist to protect: <b>rejection is an answer; silence is
/// not.</b> So every case asserts what the frame was <i>told</i>, not merely that the
/// connection failed — a dropped socket would pass a test that only checked for absence.
/// </para>
/// <para>
/// The agent side speaks nothing but the frozen contract in <c>FrameLink.Protocol</c>, so
/// these are also a check that the contract a separate workstream is building against is the
/// one this server actually implements.
/// </para>
/// </remarks>
public sealed class ControlHandshakeTests
{
    private const string Password = "a-long-operator-passphrase-for-the-fleet";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pointing_a_frame_at_the_url_is_enough_to_make_it_appear()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();

        await using var agent = await server.ConnectAgentAsync(key, hardwareSerial: "10000000abcd1234");
        await server.SignInAsync(Password);
        var devices = await server.ListDevicesAsync();

        // §3.3, UniFi-style: no pre-registration, no shared secret, no claim code.
        Assert.Equal(HandshakeStatus.Pending, agent.Result.Status);
        var row = Assert.Single(devices.Devices);
        Assert.Equal(agent.DeviceId, row.DeviceId);
        Assert.Equal("pending", row.State);

        // The serial is shown beside the fingerprint so an operator can tell which row is
        // which frame on the bench.
        Assert.Equal("10000000abcd1234", row.HardwareSerial);
    }

    [Fact]
    public async Task A_pending_device_is_given_an_answer_and_nothing_else()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();

        await using var agent = await server.ConnectAgentAsync(key);
        var afterResult = await agent.ReceiveAsync(TimeSpan.FromMilliseconds(700));

        // The whole of §3.3's "a pending device receives nothing". No name, no settings, no
        // token, no commands — and no socket left open to deliver any of them later.
        Assert.Equal(HandshakeStatus.Pending, agent.Result.Status);
        Assert.Null(agent.Result.DeviceName);
        Assert.Null(afterResult);
        Assert.True(await agent.WaitForCloseAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task A_pending_device_is_told_why_it_is_waiting()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();

        await using var agent = await server.ConnectAgentAsync(key);

        // The frame renders this. An authoritative "you are not adopted" is what stops the
        // product on the device (§2.6); silence would leave it running the old state.
        Assert.False(string.IsNullOrWhiteSpace(agent.Result.Message));
    }

    [Fact]
    public async Task An_adopted_device_is_told_its_name_and_handed_its_settings()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();

        await using (var first = await server.ConnectAgentAsync(key))
        {
            Assert.Equal(HandshakeStatus.Pending, first.Result.Status);
        }

        await server.SignInAsync(Password);
        await AdoptAsync(server, DeviceIdOf(key), "Kitchen frame");
        await PutFleetSettingAsync(server, "slideshow.interval", "30");
        await PutDeviceSettingAsync(server, DeviceIdOf(key), "volume", "25");

        await using var adopted = await server.ConnectAgentAsync(key);
        var pushed = await adopted.ReceiveAsync(TimeSpan.FromSeconds(3));
        var settings = pushed?.PayloadAs(ProtocolJson.Default.SettingsPush);

        // The full M1 arc: connects, appears pending, is adopted, and comes back to a
        // configured, named identity.
        Assert.Equal(HandshakeStatus.Ok, adopted.Result.Status);
        Assert.Equal("Kitchen frame", adopted.Result.DeviceName);
        Assert.NotNull(settings);
        Assert.Equal("30", settings.Values["slideshow.interval"]);
        Assert.Equal("25", settings.Values["volume"]);
    }

    [Fact]
    public async Task A_forged_proof_is_refused_and_leaves_no_trace()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();

        await using var agent = await server.ConnectAgentAsync(key, signCorrectly: false);
        await server.SignInAsync(Password);
        var devices = await server.ListDevicesAsync(includeBlocked: true);

        // Refused with a reason, not dropped. And no row: an unproven claim must never be
        // able to create or edit a device, or the open registration path becomes a way to
        // rewrite the reported state of somebody else's frame.
        Assert.Equal(HandshakeStatus.BadSignature, agent.Result.Status);
        Assert.Empty(devices.Devices);
    }

    [Fact]
    public async Task A_bad_proof_is_refused_before_anything_else_is_even_considered()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();

        await using var agent = await server.ConnectAgentAsync(
            key,
            protocolVersion: ProtocolConstants.Version + 99,
            signCorrectly: false);

        // Both wrong at once. Authentication comes first, so the answer names the thing that
        // actually has to be fixed rather than leaking that the identity was recognised.
        Assert.Equal(HandshakeStatus.BadSignature, agent.Result.Status);
        Assert.Null(agent.Result.ServedAgentVersion);
    }

    [Fact]
    public async Task A_version_mismatch_is_answered_with_the_way_out_never_dropped()
    {
        await using var server = await ControlServer.StartAsync(Password);
        server.Workspace.WriteAgentBinary("linux-arm64", "the served build", version: "0.4.0+deadbee");
        using var key = DeviceIdentity.CreateKeyPair();

        await using (var first = await server.ConnectAgentAsync(key))
        {
            Assert.Equal(HandshakeStatus.Pending, first.Result.Status);
        }

        await server.SignInAsync(Password);
        await AdoptAsync(server, DeviceIdOf(key), "Kitchen frame");

        await using var stale = await server.ConnectAgentAsync(
            key,
            protocolVersion: ProtocolConstants.Version + 1);

        // §4.2: strict matching is affordable precisely because the answer carries what the
        // agent needs to fix itself, so the mismatch triggers an update instead of needing a
        // second dialect. Incompatibility stays legible rather than becoming a dead socket.
        Assert.Equal(HandshakeStatus.VersionMismatch, stale.Result.Status);
        Assert.Equal(ProtocolConstants.Version, stale.Result.ProtocolVersion);
        Assert.Equal("0.4.0+deadbee", stale.Result.ServedAgentVersion);
        Assert.Equal("/agent/binary/linux-arm64", stale.Result.UpdateUrl);
        Assert.False(string.IsNullOrWhiteSpace(stale.Result.Message));
    }

    [Fact]
    public async Task A_version_mismatched_device_still_gets_a_row_the_operator_can_read()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();

        await using (var first = await server.ConnectAgentAsync(key))
        {
            Assert.Equal(HandshakeStatus.Pending, first.Result.Status);
        }

        await server.SignInAsync(Password);
        await AdoptAsync(server, DeviceIdOf(key), "Kitchen frame");

        await using (var stale = await server.ConnectAgentAsync(
            key,
            protocolVersion: 99,
            agentStatus: "self-update has failed four times"))
        {
            Assert.Equal(HandshakeStatus.VersionMismatch, stale.Result.Status);
        }

        var devices = await server.ListDevicesAsync();
        var row = Assert.Single(devices.Devices);

        // The hello is frozen so that a hopelessly outdated agent can still say who it is and
        // why it is stuck. This is the payoff: the operator sees the reason, not a mystery.
        Assert.Equal(99, row.ProtocolVersion);
        Assert.False(row.ProtocolCompatible);
        Assert.Equal("self-update has failed four times", row.AgentStatus);
    }

    [Fact]
    public async Task An_unconfigured_server_tells_every_frame_that_it_is_not_set_up()
    {
        await using var server = await ControlServer.StartAsync(operatorPassword: null);
        using var key = DeviceIdentity.CreateKeyPair();

        await using var agent = await server.ConnectAgentAsync(key);

        // §3.2: the operator is usually the first person to connect a frame, so the frame is
        // how they find out the server is not set up. The status is what puts "connected to a
        // Fleet Manager, but it is not set up yet" on the screen.
        Assert.Equal(HandshakeStatus.NotConfigured, agent.Result.Status);
        Assert.Contains("FRAMELINK_OPERATOR_PASSWORD", agent.Result.Message, StringComparison.Ordinal);
        Assert.Null(agent.Result.DeviceName);
        Assert.True(await agent.WaitForCloseAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task An_unconfigured_server_still_remembers_the_frames_that_called()
    {
        await using var server = await ControlServer.StartAsync(operatorPassword: null);
        using var key = DeviceIdentity.CreateKeyPair();

        await using (var agent = await server.ConnectAgentAsync(key))
        {
            Assert.Equal(HandshakeStatus.NotConfigured, agent.Result.Status);
        }

        await using var configured = await ControlServer.StartAsync(Password, options => options with
        {
            DataDirectory = server.Workspace.Root,
            ReleaseDirectory = server.Workspace.ReleaseDirectory,
        });

        await configured.SignInAsync(Password);
        var devices = await configured.ListDevicesAsync();

        // So the moment the operator sets the password, the frame they are holding is already
        // in the adoption queue rather than needing another reconnect to reappear.
        Assert.Equal([DeviceIdOf(key)], devices.Devices.Select(d => d.DeviceId));
    }

    [Fact]
    public async Task A_blocked_device_is_refused_and_gets_no_configuration()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();

        await using (var first = await server.ConnectAgentAsync(key))
        {
            Assert.Equal(HandshakeStatus.Pending, first.Result.Status);
        }

        await server.SignInAsync(Password);
        await PutFleetSettingAsync(server, "volume", "60");
        var blocked = await server.Client.PostAsync(
            $"/api/devices/{DeviceIdOf(key)}/block",
            content: null,
            Token);
        blocked.EnsureSuccessStatusCode();

        await using var refused = await server.ConnectAgentAsync(key);
        var afterResult = await refused.ReceiveAsync(TimeSpan.FromMilliseconds(700));

        Assert.Equal(HandshakeStatus.Blocked, refused.Result.Status);
        Assert.Null(refused.Result.DeviceName);
        Assert.Null(afterResult);
        Assert.True(await refused.WaitForCloseAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Blocking_is_reversible_and_returns_the_device_to_the_queue()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        await using (var first = await server.ConnectAgentAsync(key))
        {
            Assert.Equal(HandshakeStatus.Pending, first.Result.Status);
        }

        await server.SignInAsync(Password);
        (await server.Client.PostAsync($"/api/devices/{DeviceIdOf(key)}/block", null, Token))
            .EnsureSuccessStatusCode();
        Assert.Empty((await server.ListDevicesAsync()).Devices);
        Assert.Single((await server.ListDevicesAsync(includeBlocked: true)).Devices);

        (await server.Client.PostAsync($"/api/devices/{DeviceIdOf(key)}/unblock", null, Token))
            .EnsureSuccessStatusCode();

        await using var again = await server.ConnectAgentAsync(key);

        // Unblocking returns the device to pending rather than adopting it: the operator
        // blocked it once, so trusting it again has to be a separate, deliberate press.
        Assert.Equal(HandshakeStatus.Pending, again.Result.Status);
    }

    [Fact]
    public async Task An_adopted_device_that_asks_for_a_second_socket_loses_the_first()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        await using (var first = await server.ConnectAgentAsync(key))
        {
            Assert.Equal(HandshakeStatus.Pending, first.Result.Status);
        }

        await server.SignInAsync(Password);
        await AdoptAsync(server, DeviceIdOf(key), "Kitchen frame");

        await using var original = await server.ConnectAgentAsync(key);
        Assert.Equal(HandshakeStatus.Ok, original.Result.Status);

        await using var replacement = await server.ConnectAgentAsync(key);
        Assert.Equal(HandshakeStatus.Ok, replacement.Result.Status);

        // One device, one socket. Two would mean settings pushes landing on a connection the
        // frame is no longer reading, which is worse than no push at all.
        Assert.True(await original.WaitForCloseAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task A_hello_that_is_not_a_hello_gets_no_conversation()
    {
        await using var server = await ControlServer.StartAsync(Password);

        using var socket = new System.Net.WebSockets.ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{server.BaseAddress.Authority}/agent"), Token);
        await socket.SendAsync(
            System.Text.Encoding.UTF8.GetBytes("{\"not\":\"framelink traffic\"}"),
            System.Net.WebSockets.WebSocketMessageType.Text,
            endOfMessage: true,
            Token);

        var buffer = new byte[1024];
        var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), Token);

        // A stray proxy, a captive portal or a port scanner is closed rather than argued
        // with. The magic value in the frozen envelope is what makes that distinguishable
        // from a real FrameLink peer.
        Assert.Equal(System.Net.WebSockets.WebSocketMessageType.Close, received.MessageType);
    }

    [Fact]
    public async Task A_browser_pointed_at_the_device_route_is_told_what_it_found()
    {
        await using var server = await ControlServer.StartAsync(Password);

        var response = await server.Client.GetAsync("/agent", Token);
        var error = await response.ReadAsync(ControlJson.Default.ApiError);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("websocket-required", error.Error);
    }

    private static string DeviceIdOf(ECDsa key) =>
        DeviceIdentity.FingerprintOf(key.ExportSubjectPublicKeyInfo());

    private static async Task AdoptAsync(ControlServer server, string deviceId, string name)
    {
        var response = await server.Client.PostAsync(
            $"/api/devices/{deviceId}/adopt?name={Uri.EscapeDataString(name)}",
            content: null,
            Token);

        response.EnsureSuccessStatusCode();
    }

    private static async Task PutFleetSettingAsync(ControlServer server, string key, string value)
    {
        var response = await server.Client.PutAsJsonAsync(
            $"/api/settings/{key}",
            new SettingValueRequest { Value = value },
            ControlJson.Default.SettingValueRequest,
            Token);

        response.EnsureSuccessStatusCode();
    }

    private static async Task PutDeviceSettingAsync(
        ControlServer server,
        string deviceId,
        string key,
        string value)
    {
        var response = await server.Client.PutAsJsonAsync(
            $"/api/devices/{deviceId}/settings/{key}",
            new SettingValueRequest { Value = value },
            ControlJson.Default.SettingValueRequest,
            Token);

        response.EnsureSuccessStatusCode();
    }
}
