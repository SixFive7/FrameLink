using System.Security.Cryptography;
using FrameLink.Control;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// §2.5 rung 3's <b>retry</b>, from the operator's press to the frame's socket.
/// </summary>
/// <remarks>
/// <para>
/// The rung offers exactly two actions — retry, or open a remote shell — and until this existed
/// only the second one did. The ladder stopped a resource, notified the operator, and left them
/// with a delta and nothing to press; the attempt budget it had to clear lives in the agent's
/// durable journal, which survives the reboot §2.4 takes and the update §2.8 brings, so nothing
/// the operator could reach from the frame's own side would clear it either.
/// </para>
/// <para>
/// What is asserted here is the half the server owns: that the press reaches a live socket as the
/// message the agent's dispatch is waiting for, that a frame nobody can reach is refused loudly
/// rather than told "done", and that an unadopted device is stopped at the door like every other
/// operator action against one.
/// </para>
/// </remarks>
public sealed class ControlRetryTests
{
    private const string Password = "a-long-operator-passphrase-for-the-fleet";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_retry_reaches_the_frame_as_the_message_its_dispatch_is_waiting_for()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await AdoptedDeviceAsync(server, key);

        await using var agent = await server.ConnectAgentAsync(key);
        Assert.Equal(HandshakeStatus.Ok, agent.Result.Status);

        var response = await server.Client.PostAsync(
            $"/api/devices/{deviceId}/retry/boot.autologin.getty-tty1",
            content: null,
            Token);

        response.EnsureSuccessStatusCode();
        var body = await response.ReadAsync(ControlJson.Default.RetryResponse);

        Assert.Equal("sent", body.Outcome);
        Assert.Equal("boot.autologin.getty-tty1", body.Resource);

        var request = await WaitForRetryAsync(agent, TimeSpan.FromSeconds(5));

        Assert.NotNull(request);
        Assert.Equal(deviceId, request.DeviceId);
        Assert.Equal("boot.autologin.getty-tty1", request.Resource);
    }

    [Fact]
    public async Task A_frame_wide_retry_carries_no_resource_at_all()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await AdoptedDeviceAsync(server, key);

        await using var agent = await server.ConnectAgentAsync(key);

        (await server.Client.PostAsync($"/api/devices/{deviceId}/retry", content: null, Token))
            .EnsureSuccessStatusCode();

        var request = await WaitForRetryAsync(agent, TimeSpan.FromSeconds(5));

        // Absent, not empty. Rung 4 halts the *device* and several resources can have given up at
        // once, so "everything that gave up" is a real instruction — and it must not be reachable
        // by accident from a resource name that happened to be blank.
        Assert.NotNull(request);
        Assert.Null(request.Resource);
    }

    [Fact]
    public async Task A_remote_restart_is_the_same_message_with_the_operators_own_ending_on_it()
    {
        // "The last one (the reboot) can also be triggered from the fleet manager given the agent
        // is connected." It is the retry message with one field set rather than a kind of its own,
        // so an older frame that does not know the field still resets its budgets and tries again.
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await AdoptedDeviceAsync(server, key);

        await using var agent = await server.ConnectAgentAsync(key);
        Assert.Equal(HandshakeStatus.Ok, agent.Result.Status);

        var response = await server.Client.PostAsync($"/api/devices/{deviceId}/restart", content: null, Token);

        response.EnsureSuccessStatusCode();
        var body = await response.ReadAsync(ControlJson.Default.RetryResponse);

        Assert.Equal("sent", body.Outcome);
        Assert.Null(body.Resource);
        Assert.Contains("restart and try again", body.Detail, StringComparison.Ordinal);

        var request = await WaitForRetryAsync(agent, TimeSpan.FromSeconds(5));

        Assert.NotNull(request);
        Assert.Equal(deviceId, request.DeviceId);
        Assert.True(request.Reboot);
        Assert.Null(request.Resource);
    }

    [Fact]
    public async Task An_ordinary_retry_never_carries_the_restart()
    {
        // The two verbs share a message and must not share a meaning. A retry that arrived with the
        // reboot flag set would take a household's photographs away for a minute every time an
        // operator cleared a budget.
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await AdoptedDeviceAsync(server, key);

        await using var agent = await server.ConnectAgentAsync(key);

        (await server.Client.PostAsync($"/api/devices/{deviceId}/retry", content: null, Token))
            .EnsureSuccessStatusCode();

        var request = await WaitForRetryAsync(agent, TimeSpan.FromSeconds(5));

        Assert.NotNull(request);
        Assert.False(request.Reboot);
    }

    [Fact]
    public async Task A_restart_at_a_frame_that_is_not_connected_is_refused_and_says_where_the_other_button_is()
    {
        // "Given the agent is connected" is the whole of the condition, and it is enforced by the
        // socket rather than by a flag: a frame with no connection is answered 409, and nothing
        // queues the restart for later. The operator is told the frame's own screen still has the
        // button, because that is the one surface a person can reach when the server cannot.
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await AdoptedDeviceAsync(server, key);

        var response = await server.Client.PostAsync($"/api/devices/{deviceId}/restart", content: null, Token);
        var body = await response.ReadAsync(ControlJson.Default.RetryResponse);

        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("offline", body.Outcome);
        Assert.Contains("not connected", body.Detail, StringComparison.Ordinal);
        Assert.Contains("the button on the frame itself", body.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_retry_at_a_frame_nobody_can_reach_is_refused_rather_than_reported_done()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await AdoptedDeviceAsync(server, key);

        // Adopted, and holding no socket. §3.5 makes presence the socket, so this is exactly the
        // state the operator's own screen is already showing them as offline.
        var response = await server.Client.PostAsync($"/api/devices/{deviceId}/retry", content: null, Token);
        var body = await response.ReadAsync(ControlJson.Default.RetryResponse);

        // A 409 rather than a 200 with a sad field. Unlike a settings push there is no reconnect
        // path that replays this — the budget is held on the frame and resolved nowhere else — so
        // an operator whose press went nowhere has to be told to press it again.
        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("offline", body.Outcome);
        Assert.Contains("press it again", body.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_device_that_was_never_adopted_cannot_be_told_to_try_again()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();

        await using (var first = await server.ConnectAgentAsync(key))
        {
            Assert.Equal(HandshakeStatus.Pending, first.Result.Status);
        }

        await server.SignInAsync(Password);
        var deviceId = DeviceIdentity.FingerprintOf(key.ExportSubjectPublicKeyInfo());

        var response = await server.Client.PostAsync($"/api/devices/{deviceId}/retry", content: null, Token);
        var error = await response.ReadAsync(ControlJson.Default.ApiError);

        // §3.3 admits no partial trust: a pending frame receives nothing, and that includes being
        // steered. It has no reconcile loop the operator is entitled to reach into.
        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("not-adopted", error.Error);
    }

    [Fact]
    public async Task A_retry_at_a_device_that_does_not_exist_is_a_404()
    {
        await using var server = await ControlServer.StartAsync(Password);
        await server.SignInAsync(Password);

        var response = await server.Client.PostAsync("/api/devices/ZZZZ-ZZZZ-ZZZZ-ZZZZ/retry", content: null, Token);
        var error = await response.ReadAsync(ControlJson.Default.ApiError);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-such-device", error.Error);
    }

    [Fact]
    public async Task Retry_is_behind_the_operator_session_like_every_other_action()
    {
        await using var server = await ControlServer.StartAsync(Password);

        // No sign-in. The gate is structural rather than per-route, and a route added without a
        // test is exactly how something ends up outside it.
        var response = await server.Client.PostAsync("/api/devices/ZZZZ-ZZZZ-ZZZZ-ZZZZ/retry", content: null, Token);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Reads until a retry arrives, answering pings so the socket is not dropped.</summary>
    private static async Task<RetryRequest?> WaitForRetryAsync(TestAgent agent, TimeSpan timeout)
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

            if (string.Equals(envelope.Kind, ControlWire.KindRetry, StringComparison.Ordinal))
            {
                return envelope.PayloadAs(ProtocolJson.Default.RetryRequest);
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
        (await server.Client.PostAsync($"/api/devices/{deviceId}/adopt?name=Mule", content: null, Token))
            .EnsureSuccessStatusCode();

        return deviceId;
    }
}
