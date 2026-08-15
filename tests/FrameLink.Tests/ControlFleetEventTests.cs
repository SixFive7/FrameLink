using System.Net;
using System.Security.Cryptography;
using FrameLink.Control;
using FrameLink.Control.Agent;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// The operator-side push channel of §3.5, and the discipline that keeps it cheap.
/// </summary>
/// <remarks>
/// The console polled every four seconds because presence <i>is</i> the socket and the server
/// had no way to say so. What these tests protect is not the latency — that is easy — but the
/// two properties that make an always-open stream safe to ship: a console that stops reading
/// cannot grow the server, and a console that goes away leaves nothing behind.
/// </remarks>
public sealed class ControlFleetEventTests
{
    private const string Password = "a-long-operator-passphrase-for-the-fleet";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void A_console_that_hangs_up_leaves_nothing_behind()
    {
        // The v1 LiveKit post-mortem in one sentence: a subscription that outlives its
        // subscriber is a leak, and a leak on a route a browser opens is a leak per tab.
        var events = new FleetEvents();

        var first = events.Subscribe();
        var second = events.Subscribe();
        Assert.Equal(2, events.SubscriberCount);

        first.Dispose();
        Assert.Equal(1, events.SubscriberCount);

        second.Dispose();
        Assert.Equal(0, events.SubscriberCount);
    }

    [Fact]
    public void A_console_that_stops_reading_cannot_grow_the_server()
    {
        var events = new FleetEvents();
        using var subscription = events.Subscribe();

        for (var i = 0; i < 10_000; i++)
        {
            events.Publish($"DEVICE-{i}");
        }

        var queued = 0;
        while (subscription.Reader.TryRead(out _))
        {
            queued++;
        }

        // Bounded, and the oldest are the ones dropped: every event says the same thing, "read
        // the list again", so the newest is the only one with any information in it.
        Assert.InRange(queued, 1, 64);
    }

    [Fact]
    public void Publishing_with_nobody_listening_is_not_an_error()
    {
        // A device connects on a server nobody has open in a browser roughly always.
        var events = new FleetEvents();
        events.Publish("AAAA-AAAA-AAAA-AAAA");
        Assert.Equal(0, events.SubscriberCount);
    }

    [Fact]
    public async Task The_stream_is_behind_the_operator_password_like_everything_else_under_api()
    {
        await using var server = await ControlServer.StartAsync(Password);

        var response = await server.Client.GetAsync(
            "/api/events",
            HttpCompletionOption.ResponseHeadersRead,
            Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_frame_appearing_on_the_bench_reaches_an_open_console_at_once()
    {
        // §3.3's moment: somebody plugs a frame in and watches the screen. This is the whole
        // reason the route exists, so it is asserted end to end — a real socket from a real
        // device, through the real registry, out of the real SSE endpoint.
        await using var server = await ControlServer.StartAsync(Password);
        await server.SignInAsync(Password);

        using var stream = await server.Client.GetStreamAsync("/api/events", Token);
        using var reader = new StreamReader(stream);

        Assert.Equal("event: ready", await ReadLineAsync(reader));

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var agent = await server.ConnectAgentAsync(key);
        var deviceId = DeviceIdentity.FingerprintOf(key.ExportSubjectPublicKeyInfo());

        var announced = await ReadUntilDeviceAsync(reader, TimeSpan.FromSeconds(10));
        Assert.Equal(deviceId, announced);
    }

    [Fact]
    public async Task An_operator_action_reaches_every_other_open_console()
    {
        // Two people, two tabs, one fleet. Adoption is a fleet-wide fact and not a property of
        // the browser that happened to press the button.
        await using var server = await ControlServer.StartAsync(Password);
        await server.SignInAsync(Password);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using (await server.ConnectAgentAsync(key))
        {
        }

        var deviceId = DeviceIdentity.FingerprintOf(key.ExportSubjectPublicKeyInfo());

        using var stream = await server.Client.GetStreamAsync("/api/events", Token);
        using var reader = new StreamReader(stream);
        Assert.Equal("event: ready", await ReadLineAsync(reader));

        var adopted = await server.Client.PostAsync($"/api/devices/{deviceId}/adopt", null, Token);
        adopted.EnsureSuccessStatusCode();

        Assert.Equal(deviceId, await ReadUntilDeviceAsync(reader, TimeSpan.FromSeconds(10)));
    }

    /// <summary>Reads one line, or fails rather than hanging the suite.</summary>
    private static async Task<string?> ReadLineAsync(StreamReader reader)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        return await reader.ReadLineAsync(deadline.Token);
    }

    /// <summary>Reads frames until a <c>device</c> event arrives, and returns its id.</summary>
    /// <remarks>
    /// The device's own connection also produces a registry departure when the socket closes,
    /// so the stream carries more than one frame for one plug-in. Scanning for the first
    /// <c>data:</c> after a <c>device</c> event is what makes the assertion about the id rather
    /// than about the exact frame ordering.
    /// </remarks>
    private static async Task<string?> ReadUntilDeviceAsync(StreamReader reader, TimeSpan timeout)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(timeout);

        var expectData = false;
        while (await reader.ReadLineAsync(deadline.Token) is { } line)
        {
            if (expectData && line.StartsWith("data: ", StringComparison.Ordinal))
            {
                return line["data: ".Length..];
            }

            expectData = string.Equals(line, "event: device", StringComparison.Ordinal);
        }

        return null;
    }
}
