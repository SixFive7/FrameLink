using System.Net.WebSockets;
using System.Security.Cryptography;
using FrameLink.Control;
using FrameLink.Control.Agent;
using FrameLink.Control.Storage;
using FrameLink.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameLink.Tests;

/// <summary>
/// The four abuse controls §3.3 makes mandatory on the open registration path.
/// </summary>
/// <remarks>
/// <para>
/// The acceptance criterion is stated in the specification as an outcome, so it is asserted
/// as one: an attacker must be able to create noise rows and nothing else. Each test names
/// the specific thing that must stay bounded.
/// </para>
/// <para>
/// The handshake budget has two halves and they are tested as two different questions.
/// <b>Unidentified traffic</b> — anything that has not proved a key the operator has acted on —
/// is bounded per source address, and that is the half an attacker meets. <b>A proven device</b>
/// is bounded per keypair, and the tests for it are all multi-device on one address, because
/// decision 87's fault was invisible with a single frame and only appears when six of them share
/// a NAT.
/// </para>
/// </remarks>
public sealed class ControlAbuseControlTests
{
    private const string Password = "a-long-operator-passphrase-for-the-fleet";

    /// <summary>The address every frame in a household — or behind a container's NAT — arrives as.</summary>
    private const string Gateway = "172.18.0.1";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void An_unidentified_address_may_spend_its_budget_and_no_more()
    {
        var clock = new TestClock();
        var options = new ControlOptions
        {
            RateLimitAttempts = 3,
            RateLimitWindow = TimeSpan.FromMinutes(1),
        };
        var limiter = new RegistrationRateLimiter(options, clock);

        var verdicts = Enumerable.Range(0, 5).Select(_ => limiter.TryAdmitUnidentified("10.0.0.1")).ToList();

        Assert.Equal([true, true, true, false, false], verdicts);
    }

    [Fact]
    public void The_budget_refills_when_the_window_rolls_over()
    {
        var clock = new TestClock();
        var options = new ControlOptions { RateLimitAttempts = 2, RateLimitWindow = TimeSpan.FromMinutes(1) };
        var limiter = new RegistrationRateLimiter(options, clock);

        limiter.TryAdmitUnidentified("10.0.0.1");
        limiter.TryAdmitUnidentified("10.0.0.1");
        Assert.False(limiter.TryAdmitUnidentified("10.0.0.1"));

        clock.Advance(TimeSpan.FromMinutes(1));

        // A frame with a genuinely flaky link must not be locked out permanently; §4.1's
        // reconnect discipline is retry-forever, and the limiter has to leave room for it.
        Assert.True(limiter.TryAdmitUnidentified("10.0.0.1"));
    }

    [Fact]
    public void One_noisy_address_cannot_lock_out_another()
    {
        var clock = new TestClock();
        var options = new ControlOptions { RateLimitAttempts = 1, RateLimitWindow = TimeSpan.FromMinutes(1) };
        var limiter = new RegistrationRateLimiter(options, clock);

        limiter.TryAdmitUnidentified("10.0.0.1");
        Assert.False(limiter.TryAdmitUnidentified("10.0.0.1"));
        Assert.True(limiter.TryAdmitUnidentified("10.0.0.2"));
    }

    [Fact]
    public void Requests_with_no_attributable_address_share_one_budget()
    {
        var clock = new TestClock();
        var options = new ControlOptions { RateLimitAttempts = 1, RateLimitWindow = TimeSpan.FromMinutes(1) };
        var limiter = new RegistrationRateLimiter(options, clock);

        Assert.True(limiter.TryAdmitUnidentified(null));

        // Counting them together keeps the budget enforced. Treating "unknown" as exempt
        // would be a free bypass for any transport that hides its source.
        Assert.False(limiter.TryAdmitUnidentified(null));
        Assert.False(limiter.TryAdmitUnidentified(string.Empty));
    }

    [Fact]
    public void The_limiter_refuses_to_grow_past_its_own_ceiling()
    {
        var clock = new TestClock();
        var options = new ControlOptions
        {
            RateLimitAttempts = 100,
            RateLimitWindow = TimeSpan.FromMinutes(1),
            MaxTrackedAddresses = 8,
        };
        var limiter = new RegistrationRateLimiter(options, clock);

        for (var i = 0; i < 8; i++)
        {
            Assert.True(limiter.TryAdmitUnidentified($"10.0.0.{i}"));
        }

        // An attacker with a fresh source address per request must not be able to make the
        // limiter itself the memory-exhaustion vector it exists to prevent.
        Assert.False(limiter.TryAdmitUnidentified("10.0.1.1"));

        // Addresses already being tracked keep working, so the ceiling degrades gracefully
        // rather than blacking out the whole fleet.
        Assert.True(limiter.TryAdmitUnidentified("10.0.0.0"));
    }

    [Fact]
    public void A_proven_device_hands_the_shared_address_charge_straight_back()
    {
        var clock = new TestClock();
        var options = new ControlOptions { RateLimitAttempts = 1, RateLimitWindow = TimeSpan.FromMinutes(1) };
        var limiter = new RegistrationRateLimiter(options, clock);

        // The pre-upgrade charge is provisional: at this point the server knows only where the
        // packets came from, which behind a NAT is every frame the operator owns.
        Assert.True(limiter.TryAdmitUnidentified(Gateway));
        Assert.True(limiter.TryAdmitDevice("DEVICE-A", Gateway));

        // Released, so the one-attempt address budget is whole again even though a handshake
        // just went through it. This is the single assertion the whole fix rests on.
        Assert.True(limiter.TryAdmitUnidentified(Gateway));
    }

    [Fact]
    public void Six_frames_behind_one_address_do_not_share_one_budget()
    {
        // Decision 87, as an outcome. The address budget is deliberately set below the size of
        // the fleet: under the per-address keying this was the bug, six frames on one household
        // NAT would exhaust it between them and the rest would be refused before they could say
        // who they were.
        var clock = new TestClock();
        var options = new ControlOptions
        {
            RateLimitAttempts = 2,
            DeviceRateLimitAttempts = 3,
            RateLimitWindow = TimeSpan.FromMinutes(1),
        };
        var limiter = new RegistrationRateLimiter(options, clock);

        var fleet = Enumerable.Range(1, 6).Select(n => $"FRAME-{n}").ToList();

        for (var round = 0; round < 3; round++)
        {
            foreach (var frame in fleet)
            {
                Assert.True(limiter.TryAdmitUnidentified(Gateway), $"{frame} was refused before the upgrade.");
                Assert.True(limiter.TryAdmitDevice(frame, Gateway), $"{frame} was refused after proving itself.");
            }
        }
    }

    [Fact]
    public void A_device_may_spend_its_own_budget_and_no_more()
    {
        var clock = new TestClock();
        var options = new ControlOptions
        {
            RateLimitAttempts = 1_000,
            DeviceRateLimitAttempts = 3,
            RateLimitWindow = TimeSpan.FromMinutes(1),
        };
        var limiter = new RegistrationRateLimiter(options, clock);

        var verdicts = Enumerable.Range(0, 5)
            .Select(_ => Handshake(limiter, "DEVICE-A"))
            .ToList();

        // Replacing a shared budget with no budget would have been the other half of the bug.
        Assert.Equal([true, true, true, false, false], verdicts);
    }

    [Fact]
    public void A_frame_in_a_reconnect_loop_cannot_spend_its_neighbours_allowance()
    {
        var clock = new TestClock();
        var options = new ControlOptions
        {
            RateLimitAttempts = 2,
            DeviceRateLimitAttempts = 2,
            RateLimitWindow = TimeSpan.FromMinutes(1),
        };
        var limiter = new RegistrationRateLimiter(options, clock);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            Handshake(limiter, "NOISY");
        }

        // The noisy frame is long past its own budget and its five neighbours have lost nothing.
        Assert.False(Handshake(limiter, "NOISY"));
        Assert.True(Handshake(limiter, "HEALTHY"));
    }

    [Fact]
    public void An_over_budget_device_still_releases_the_window_its_neighbours_need()
    {
        // The deadlock this ordering exists to prevent: refuse the device *and* keep its address
        // charge, and one frame in a hard loop drains the shared window instead — at which point
        // its neighbours are refused before the upgrade, so they never reach the proof that would
        // have released them. Same lockout, one layer down.
        var clock = new TestClock();
        var options = new ControlOptions
        {
            RateLimitAttempts = 4,
            DeviceRateLimitAttempts = 1,
            RateLimitWindow = TimeSpan.FromMinutes(1),
        };
        var limiter = new RegistrationRateLimiter(options, clock);

        for (var attempt = 0; attempt < 50; attempt++)
        {
            Handshake(limiter, "NOISY");
        }

        Assert.True(limiter.TryAdmitUnidentified(Gateway));
    }

    [Fact]
    public void The_device_budget_refills_when_the_window_rolls_over()
    {
        var clock = new TestClock();
        var options = new ControlOptions
        {
            RateLimitAttempts = 1_000,
            DeviceRateLimitAttempts = 1,
            RateLimitWindow = TimeSpan.FromMinutes(1),
        };
        var limiter = new RegistrationRateLimiter(options, clock);

        Assert.True(Handshake(limiter, "DEVICE-A"));
        Assert.False(Handshake(limiter, "DEVICE-A"));

        clock.Advance(TimeSpan.FromMinutes(1));

        // Retry-forever again: a throttle is a pause, never a permanent refusal.
        Assert.True(Handshake(limiter, "DEVICE-A"));
    }

    [Fact]
    public void The_device_window_refuses_to_grow_past_its_own_ceiling()
    {
        var clock = new TestClock();
        var options = new ControlOptions
        {
            RateLimitAttempts = 1_000,
            DeviceRateLimitAttempts = 100,
            RateLimitWindow = TimeSpan.FromMinutes(1),
            MaxTrackedDevices = 4,
        };
        var limiter = new RegistrationRateLimiter(options, clock);

        for (var i = 0; i < 4; i++)
        {
            Assert.True(Handshake(limiter, $"DEVICE-{i}"));
        }

        // Nothing a stranger can send reaches this dictionary — only an identity the operator
        // has adopted or blocked — so hitting the ceiling means a fleet of that size, not an
        // attack. It is still capped, for the same reason the address one is.
        Assert.False(Handshake(limiter, "DEVICE-4"));
        Assert.True(Handshake(limiter, "DEVICE-0"));
    }

    [Fact]
    public async Task One_frame_reconnecting_in_a_loop_cannot_lock_out_the_rest_of_the_fleet()
    {
        // The same claim as the unit test above, through the real pipeline: real sockets, the
        // real handshake, the real limiter. Every connection in this suite arrives from
        // 127.0.0.1, which is exactly the shape decision 87 measured — one address for the whole
        // fleet — so this test would have failed before the keying changed and passes now.
        await using var server = await ControlServer.StartAsync(Password, options => options with
        {
            RateLimitAttempts = 4,
            DeviceRateLimitAttempts = 3,
            RateLimitWindow = TimeSpan.FromMinutes(10),
        });

        using var noisy = DeviceIdentity.CreateKeyPair();
        using var healthy = DeviceIdentity.CreateKeyPair();

        await server.EnrolAsync(noisy, Password);
        await server.EnrolAsync(healthy);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            await using var loop = await TryConnectAsync(server, noisy);
        }

        await using var neighbour = await TryConnectAsync(server, healthy);

        Assert.NotNull(neighbour);
        Assert.Equal(HandshakeStatus.Ok, neighbour.Result.Status);
    }

    [Fact]
    public async Task A_frame_that_reconnects_too_often_is_told_so_rather_than_dropped()
    {
        await using var server = await ControlServer.StartAsync(Password, options => options with
        {
            RateLimitAttempts = 1_000,
            DeviceRateLimitAttempts = 2,
            RateLimitWindow = TimeSpan.FromMinutes(10),
        });

        using var key = DeviceIdentity.CreateKeyPair();
        await server.EnrolAsync(key, Password);

        await using (var first = await server.ConnectAgentAsync(key))
        {
            Assert.Equal(HandshakeStatus.Ok, first.Result.Status);
        }

        await using (var second = await server.ConnectAgentAsync(key))
        {
            Assert.Equal(HandshakeStatus.Ok, second.Result.Status);
        }

        await using var throttled = await server.ConnectAgentAsync(key);

        // §2.6: rejection is an answer and silence is not, so a throttle is answered too — and
        // the answer says the adoption has not changed, because it has not.
        Assert.Equal(HandshakeStatus.RateLimited, throttled.Result.Status);
        Assert.False(string.IsNullOrWhiteSpace(throttled.Result.Message));
        Assert.Null(await throttled.ReceiveAsync(TimeSpan.FromMilliseconds(700)));
        Assert.True(await throttled.WaitForCloseAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task A_device_the_operator_has_never_adopted_stays_on_the_shared_budget()
    {
        // The unknown half, and the one an attacker actually sits on. A freshly minted keypair
        // proves itself perfectly well and is still a stranger, so it is never released onto a
        // budget of its own — otherwise minting keypairs would be a way to mint budgets.
        await using var server = await ControlServer.StartAsync(Password, options => options with
        {
            RateLimitAttempts = 3,
            DeviceRateLimitAttempts = 1_000,
            RateLimitWindow = TimeSpan.FromMinutes(10),
        });

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var stranger = DeviceIdentity.CreateKeyPair();
            await using var connection = await TryConnectAsync(server, stranger);
            Assert.NotNull(connection);
            Assert.Equal(HandshakeStatus.Pending, connection.Result.Status);
        }

        using var next = DeviceIdentity.CreateKeyPair();
        await using var refused = await TryConnectAsync(server, next);

        // Refused before the upgrade, which is the whole point of keeping this half in front of
        // the WebSocket: it costs one HTTP response and reaches no crypto and no database.
        Assert.Null(refused);
    }

    [Fact]
    public async Task Naming_a_device_is_not_enough_to_spend_its_budget()
    {
        // The budget is charged to the fingerprint the *proof* established, never to the one the
        // hello claimed. A hello is unauthenticated, so charging a claimed id would turn this
        // route into a way to throttle somebody else's frame by naming it.
        await using var server = await ControlServer.StartAsync(Password, options => options with
        {
            RateLimitAttempts = 1_000,
            DeviceRateLimitAttempts = 1,
            RateLimitWindow = TimeSpan.FromMinutes(10),
        });

        using var key = DeviceIdentity.CreateKeyPair();
        await server.EnrolAsync(key, Password);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await using var forged = await server.ConnectAgentAsync(key, signCorrectly: false);
            Assert.Equal(HandshakeStatus.BadSignature, forged.Result.Status);
        }

        await using var genuine = await server.ConnectAgentAsync(key);
        Assert.Equal(HandshakeStatus.Ok, genuine.Result.Status);
    }

    [Fact]
    public async Task The_pending_table_never_grows_past_its_cap()
    {
        using var fixture = new StorageFixture();

        for (var i = 0; i < 30; i++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.SeeDeviceAsync($"NOISE-{i:0000}", cap: 5);
        }

        Assert.Equal(5, await fixture.Devices.CountPendingAsync(Token));
    }

    [Fact]
    public async Task The_cap_evicts_the_oldest_noise_rather_than_refusing_the_newcomer()
    {
        using var fixture = new StorageFixture();

        await fixture.SeeDeviceAsync("OLDEST", cap: 2);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.SeeDeviceAsync("MIDDLE", cap: 2);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.SeeDeviceAsync("NEWEST", cap: 2);

        // §2.6 forbids answering a genuine frame with silence, so a full queue gives way
        // rather than turning newcomers away. The row an attacker created is the one that goes.
        Assert.Null(await fixture.Devices.FindAsync("OLDEST", Token));
        Assert.NotNull(await fixture.Devices.FindAsync("MIDDLE", Token));
        Assert.NotNull(await fixture.Devices.FindAsync("NEWEST", Token));
    }

    [Fact]
    public async Task The_cap_never_evicts_an_adopted_or_blocked_device()
    {
        using var fixture = new StorageFixture();
        await fixture.SeeDeviceAsync("ADOPTED", cap: 2);
        await fixture.Devices.AdoptAsync("ADOPTED", "Kitchen", Token);
        await fixture.SeeDeviceAsync("BLOCKED", cap: 2);
        await fixture.Devices.BlockAsync("BLOCKED", Token);

        for (var i = 0; i < 10; i++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.SeeDeviceAsync($"NOISE-{i:00}", cap: 2);
        }

        // The cap counts and evicts only un-adopted rows. Otherwise flooding the endpoint
        // would be a way to delete somebody's configured fleet.
        Assert.NotNull(await fixture.Devices.FindAsync("ADOPTED", Token));
        Assert.NotNull(await fixture.Devices.FindAsync("BLOCKED", Token));
    }

    [Fact]
    public async Task Un_adopted_rows_expire_and_adopted_ones_do_not()
    {
        using var fixture = new StorageFixture();
        await fixture.SeeDeviceAsync("STALE");
        await fixture.SeeDeviceAsync("KEPT");
        await fixture.Devices.AdoptAsync("KEPT", "Kitchen", Token);

        fixture.Clock.Advance(TimeSpan.FromDays(10));
        var expired = await fixture.Devices.ExpirePendingAsync(
            fixture.Clock.GetUtcNow() - TimeSpan.FromDays(7),
            Token);

        Assert.Equal(1, expired);
        Assert.Null(await fixture.Devices.FindAsync("STALE", Token));
        Assert.NotNull(await fixture.Devices.FindAsync("KEPT", Token));
    }

    [Fact]
    public async Task A_pending_device_that_keeps_reconnecting_is_never_expired()
    {
        using var fixture = new StorageFixture();
        await fixture.SeeDeviceAsync("ON-THE-BENCH");

        for (var day = 0; day < 30; day++)
        {
            fixture.Clock.Advance(TimeSpan.FromDays(1));
            await fixture.SeeDeviceAsync("ON-THE-BENCH");
            await fixture.Devices.ExpirePendingAsync(fixture.Clock.GetUtcNow() - TimeSpan.FromDays(7), Token);
        }

        // A frame waiting on a bench for a month is a real situation. Expiry is measured from
        // last contact precisely so that anything actually running is never a candidate.
        Assert.NotNull(await fixture.Devices.FindAsync("ON-THE-BENCH", Token));
    }

    [Fact]
    public async Task The_reaper_sweep_expires_rows_and_survives_being_run_repeatedly()
    {
        using var fixture = new StorageFixture();
        var options = new ControlOptions { PendingDeviceTtl = TimeSpan.FromDays(7) };
        var limiter = new RegistrationRateLimiter(options, fixture.Clock);
        var reaper = new PendingDeviceReaper(
            fixture.Devices,
            fixture.Telemetry,
            fixture.Packages,
            limiter,
            options,
            fixture.Clock,
            NullLogger<PendingDeviceReaper>.Instance);

        await fixture.SeeDeviceAsync("STALE");
        fixture.Clock.Advance(TimeSpan.FromDays(8));

        await reaper.SweepAsync(Token);
        await reaper.SweepAsync(Token);

        Assert.Null(await fixture.Devices.FindAsync("STALE", Token));
        Assert.Equal(0, await fixture.Devices.CountPendingAsync(Token));
    }

    [Fact]
    public void A_known_device_gives_back_its_own_attempt_and_not_the_whole_window()
    {
        // The release is a decrement with a floor, never a reset, and the difference is a
        // security property rather than an implementation detail. An attacker and a legitimate
        // frame can share an address — the household the frame lives in — and a release that
        // cleared the window would let one adopted frame wipe the anonymous flood counter its
        // neighbour on the same NAT had just filled.
        var clock = new TestClock();
        var options = new ControlOptions { RateLimitAttempts = 2, RateLimitWindow = TimeSpan.FromMinutes(1) };
        var limiter = new RegistrationRateLimiter(options, clock);

        Assert.True(limiter.TryAdmitUnidentified(Gateway));
        Assert.True(limiter.TryAdmitUnidentified(Gateway));

        limiter.TryAdmitDevice("DEVICE-A", Gateway);
        limiter.TryAdmitDevice("DEVICE-B", Gateway);
        limiter.TryAdmitDevice("DEVICE-C", Gateway);

        // Two charges were made and at most two can come back, however many devices ask.
        Assert.True(limiter.TryAdmitUnidentified(Gateway));
        Assert.True(limiter.TryAdmitUnidentified(Gateway));
        Assert.False(limiter.TryAdmitUnidentified(Gateway));
    }

    /// <summary>One whole admission: the provisional address charge, then the proven device.</summary>
    private static bool Handshake(RegistrationRateLimiter limiter, string deviceId) =>
        limiter.TryAdmitUnidentified(Gateway) && limiter.TryAdmitDevice(deviceId, Gateway);

    /// <summary>Connects, or returns null when the server refused before the upgrade.</summary>
    /// <remarks>
    /// A pre-upgrade refusal is an HTTP 429, so the WebSocket never opens and the client throws
    /// rather than answering. That is the outcome under test in half of these, so it is a value
    /// here rather than an exception.
    /// </remarks>
    private static async Task<TestAgent?> TryConnectAsync(ControlServer server, ECDsa key)
    {
        try
        {
            return await server.ConnectAgentAsync(key);
        }
        catch (WebSocketException)
        {
            return null;
        }
    }
}
