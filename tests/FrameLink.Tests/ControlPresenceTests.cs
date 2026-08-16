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

    /// <summary>
    /// A deadline short enough to watch fire, for the two tests that are about the deadline.
    /// </summary>
    /// <remarks>
    /// Every other test in this file — and in the Fleet Manager suite — takes the host default,
    /// which is long. A test that is not about liveness must not have its socket torn down
    /// underneath an assertion about something else, and naming the value here is what keeps
    /// "the server hangs up on a silent frame" an asserted behaviour rather than an ambient
    /// hazard the rest of the suite has to work around.
    /// </remarks>
    private static ControlOptions Liveness(ControlOptions options) => options with
    {
        PingInterval = TimeSpan.FromMilliseconds(80),
        PongDeadline = TimeSpan.FromMilliseconds(500),
    };

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
        await using var server = await ControlServer.StartAsync(Password, Liveness);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await AdoptedDeviceAsync(server, key);

        await using var agent = await server.ConnectAgentAsync(key);
        Assert.Equal(HandshakeStatus.Ok, agent.Result.Status);

        using var answering = new CancellationTokenSource();
        var pump = agent.AnswerPingsUntilAsync(answering.Token);

        // Four deadlines and twenty-five pings. A healthy frame must not be disconnected by the
        // very mechanism that exists to notice unhealthy ones — and survival is counted in
        // answered probes rather than in elapsed milliseconds, because those two are the same
        // number only on an unloaded machine. Sleeping for two seconds instead would hand a busy
        // one fewer cycles and read the shortfall as the connection having failed; waiting for
        // the cycles makes it take longer and assert the same thing, over more silence.
        var answered = await agent.WaitForAnsweredPingsAsync(25, TimeSpan.FromSeconds(30));

        // Both readings are taken while the frame is still answering, which is the whole point:
        // stopping the pump first would hand the assertion a 500 ms budget to complete an HTTP
        // round trip in, and a machine slow enough to miss it would report a correctly torn-down
        // socket as a failure of the behaviour under test.
        var stillOpen = agent.IsOpen;
        var online = await server.WaitForDeviceAsync(deviceId, d => d.Online, TimeSpan.FromSeconds(5));

        await answering.CancelAsync();
        await pump;

        Assert.True(answered, $"the frame answered only {agent.AnsweredPings} of twenty-five pings");
        Assert.True(stillOpen);
        Assert.True(online);
    }

    [Fact]
    public async Task A_quiet_moment_on_the_socket_is_not_a_disconnection()
    {
        // The reproduction the fix above was written from, kept so it cannot come back. Waiting a
        // while for a frame that does not arrive has to be an observation about traffic and
        // nothing else. Implemented as a cancelled socket read it is instead an abort — cancelling
        // a ClientWebSocket receive tears the connection down rather than abandoning the read — so
        // every timed receive in this suite quietly held the power to kill the connection the test
        // around it was about to assert against. It only fired when a frame was late, which is why
        // it presented as a flake rather than as a failure.
        await using var server = await ControlServer.StartAsync(
            Password,
            options => options with { PingInterval = TimeSpan.FromMinutes(10) });

        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await AdoptedDeviceAsync(server, key);

        await using var agent = await server.ConnectAgentAsync(key);
        Assert.Equal(HandshakeStatus.Ok, agent.Result.Status);

        // Drain the connect-time frames, so the wait that follows genuinely finds an empty socket
        // rather than a queued frame. Two of them now: the settings push, and the operator contact
        // of §2.7 item 8 (decision 71).
        Assert.NotNull(await agent.ReceiveAsync(TimeSpan.FromSeconds(3)));
        Assert.NotNull(await agent.ReceiveAsync(TimeSpan.FromSeconds(3)));

        Assert.Null(await agent.ReceiveAsync(TimeSpan.FromMilliseconds(50)));
        Assert.True(agent.IsOpen);
        Assert.True(await server.WaitForDeviceAsync(deviceId, d => d.Online, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task A_device_that_stops_answering_is_torn_down_and_goes_offline()
    {
        await using var server = await ControlServer.StartAsync(Password, Liveness);
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
