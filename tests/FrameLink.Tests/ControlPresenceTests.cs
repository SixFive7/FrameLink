using System.Net.Http.Json;
using System.Security.Cryptography;
using FrameLink.Control;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// Presence and liveness (§3.5), asserted against a running server.
/// </summary>
/// <remarks>
/// "Presence is the socket" is only a true statement if something proves the socket is
/// really there. A pulled plug leaves a half-open TCP connection that accepts writes forever,
/// so without ping/pong the online list would be accurate for polite disconnections and
/// wrong for exactly the failure everybody actually has.
/// </remarks>
public sealed class ControlPresenceTests
{
    private const string Password = "a-long-operator-passphrase-for-the-fleet";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_adopted_device_is_online_while_its_socket_is_open_and_not_after()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await AdoptedDeviceAsync(server, key);

        var agent = await server.ConnectAgentAsync(key);
        Assert.Equal(HandshakeStatus.Ok, agent.Result.Status);
        Assert.True(await server.WaitForDeviceAsync(deviceId, d => d.Online, TimeSpan.FromSeconds(3)));

        await agent.DisposeAsync();

        // Nothing is polled and no heartbeat row is aged out; the entry goes when the socket
        // does. Which is also why "offline since" is just the last contact timestamp.
        Assert.True(await server.WaitForDeviceAsync(deviceId, d => !d.Online, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_pending_device_is_never_online()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();

        await using var agent = await server.ConnectAgentAsync(key);
        await server.SignInAsync(Password);
        var devices = await server.ListDevicesAsync();

        // It holds no socket, so it holds no presence. §3.3: a pending record allocates no
        // resources, on an endpoint that is deliberately open to the internet.
        Assert.Equal(HandshakeStatus.Pending, agent.Result.Status);
        Assert.False(Assert.Single(devices.Devices).Online);
    }

    [Fact]
    public async Task A_device_that_answers_its_pings_stays_connected()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await AdoptedDeviceAsync(server, key);

        await using var agent = await server.ConnectAgentAsync(key);
        Assert.Equal(HandshakeStatus.Ok, agent.Result.Status);

        // Well past the 500 ms deadline. A healthy frame must not be disconnected by the very
        // mechanism that exists to notice unhealthy ones.
        await agent.AnswerPingsAsync(TimeSpan.FromSeconds(2));

        Assert.True(agent.IsOpen);
        Assert.True(await server.WaitForDeviceAsync(deviceId, d => d.Online, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task A_device_that_stops_answering_is_torn_down_and_goes_offline()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await AdoptedDeviceAsync(server, key);

        await using var agent = await server.ConnectAgentAsync(key);
        Assert.Equal(HandshakeStatus.Ok, agent.Result.Status);

        // The agent never sends anything again — the half-open case a pulled plug produces.
        // The socket stays writable, so only a missed answer can reveal it.
        Assert.True(await agent.WaitForCloseAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await server.WaitForDeviceAsync(deviceId, d => !d.Online, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task The_server_actually_sends_pings_rather_than_only_expecting_pongs()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        await AdoptedDeviceAsync(server, key);

        await using var agent = await server.ConnectAgentAsync(key);

        // Drain the settings frame first, then the probe itself.
        WireEnvelope? ping = null;
        for (var attempt = 0; attempt < 8 && ping is null; attempt++)
        {
            var envelope = await agent.ReceiveAsync(TimeSpan.FromMilliseconds(400));
            if (envelope is not null
                && string.Equals(envelope.Kind, ControlWire.KindPing, StringComparison.Ordinal))
            {
                ping = envelope;
            }
        }

        Assert.NotNull(ping);
        Assert.Equal(ProtocolConstants.ChannelControl, ping.Channel);
        Assert.NotNull(ping.PayloadAs(ProtocolJson.Default.AgentPing));
    }

    [Fact]
    public async Task Changing_a_fleet_default_reaches_a_device_that_is_already_connected()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        await AdoptedDeviceAsync(server, key);

        await using var agent = await server.ConnectAgentAsync(key);
        Assert.Equal(HandshakeStatus.Ok, agent.Result.Status);
        await agent.ReceiveAsync(TimeSpan.FromSeconds(2));

        var response = await server.Client.PutAsJsonAsync(
            "/api/settings/backlight.evening",
            new SettingValueRequest { Value = "30" },
            ControlJson.Default.SettingValueRequest,
            Token);
        response.EnsureSuccessStatusCode();

        var pushed = await WaitForSettingsAsync(agent, TimeSpan.FromSeconds(3));

        Assert.NotNull(pushed);
        Assert.Equal("30", pushed.Values["backlight.evening"]);
    }

    [Fact]
    public async Task An_override_pushed_to_a_live_device_beats_the_fleet_default()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await AdoptedDeviceAsync(server, key);

        (await server.Client.PutAsJsonAsync(
            "/api/settings/volume",
            new SettingValueRequest { Value = "60" },
            ControlJson.Default.SettingValueRequest,
            Token)).EnsureSuccessStatusCode();

        await using var agent = await server.ConnectAgentAsync(key);
        var initial = await WaitForSettingsAsync(agent, TimeSpan.FromSeconds(3));
        Assert.Equal("60", initial!.Values["volume"]);

        (await server.Client.PutAsJsonAsync(
            $"/api/devices/{deviceId}/settings/volume",
            new SettingValueRequest { Value = "25" },
            ControlJson.Default.SettingValueRequest,
            Token)).EnsureSuccessStatusCode();

        var overridden = await WaitForSettingsAsync(agent, TimeSpan.FromSeconds(3));

        Assert.Equal("25", overridden!.Values["volume"]);
        Assert.True(overridden.Revision > initial.Revision);
    }

    [Fact]
    public async Task An_override_is_refused_for_a_device_that_is_only_pending()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        await using (var agent = await server.ConnectAgentAsync(key))
        {
            Assert.Equal(HandshakeStatus.Pending, agent.Result.Status);
        }

        await server.SignInAsync(Password);
        var deviceId = DeviceIdentity.FingerprintOf(key.ExportSubjectPublicKeyInfo());

        var response = await server.Client.PutAsJsonAsync(
            $"/api/devices/{deviceId}/settings/volume",
            new SettingValueRequest { Value = "25" },
            ControlJson.Default.SettingValueRequest,
            Token);

        var error = await response.ReadAsync(ControlJson.Default.ApiError);
        var view = await (await server.Client.GetAsync($"/api/devices/{deviceId}/settings", Token))
            .ReadAsync(ControlJson.Default.DeviceSettingsResponse);

        // The operator is stopped at the door rather than allowed to stage configuration
        // against a device they have not adopted. §3.3 admits no partial trust.
        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("not-adopted", error.Error);
        Assert.Empty(view.Overrides);
        Assert.Empty(view.Effective);
    }

    [Fact]
    public async Task Blocking_a_connected_device_hangs_up_on_it()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await AdoptedDeviceAsync(server, key);

        await using var agent = await server.ConnectAgentAsync(key);
        Assert.Equal(HandshakeStatus.Ok, agent.Result.Status);

        (await server.Client.PostAsync($"/api/devices/{deviceId}/block", null, Token))
            .EnsureSuccessStatusCode();

        // Blocking has to take effect now, not at the next reconnect. Its handshake is then
        // answered `blocked`, which is what stops the product on the frame (§2.6).
        Assert.True(await agent.WaitForCloseAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await server.WaitForDeviceAsync(deviceId, d => !d.Online, TimeSpan.FromSeconds(5)));
    }

    private static async Task<SettingsPush?> WaitForSettingsAsync(TestAgent agent, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var envelope = await agent.ReceiveAsync(TimeSpan.FromMilliseconds(200));
            if (envelope is null)
            {
                continue;
            }

            if (string.Equals(envelope.Kind, ControlWire.KindPing, StringComparison.Ordinal))
            {
                var ping = envelope.PayloadAs(ProtocolJson.Default.AgentPing);
                await agent.PongAsync(ping?.Sequence ?? 0);
                continue;
            }

            if (string.Equals(envelope.Kind, ControlWire.KindSettings, StringComparison.Ordinal))
            {
                return envelope.PayloadAs(ProtocolJson.Default.SettingsPush);
            }
        }

        return null;
    }

    private static async Task<string> AdoptedDeviceAsync(ControlServer server, ECDsa key)
    {
        var deviceId = DeviceIdentity.FingerprintOf(key.ExportSubjectPublicKeyInfo());

        await using (var first = await server.ConnectAgentAsync(key))
        {
            Assert.Equal(HandshakeStatus.Pending, first.Result.Status);
        }

        await server.SignInAsync(Password);
        var response = await server.Client.PostAsync(
            $"/api/devices/{deviceId}/adopt?name=Kitchen%20frame",
            content: null,
            Token);
        response.EnsureSuccessStatusCode();

        return deviceId;
    }
}
