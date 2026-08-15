using FrameLink.Control;
using FrameLink.Control.Agent;
using FrameLink.Control.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameLink.Tests;

/// <summary>
/// The four abuse controls §3.3 makes mandatory on the open registration path.
/// </summary>
/// <remarks>
/// The acceptance criterion is stated in the specification as an outcome, so it is asserted
/// as one: an attacker must be able to create noise rows and nothing else. Each test names
/// the specific thing that must stay bounded.
/// </remarks>
public sealed class ControlAbuseControlTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void An_address_may_spend_its_budget_and_no_more()
    {
        var clock = new TestClock();
        var options = new ControlOptions
        {
            RateLimitAttempts = 3,
            RateLimitWindow = TimeSpan.FromMinutes(1),
        };
        var limiter = new RegistrationRateLimiter(options, clock);

        var verdicts = Enumerable.Range(0, 5).Select(_ => limiter.TryAcquire("10.0.0.1")).ToList();

        Assert.Equal([true, true, true, false, false], verdicts);
    }

    [Fact]
    public void The_budget_refills_when_the_window_rolls_over()
    {
        var clock = new TestClock();
        var options = new ControlOptions { RateLimitAttempts = 2, RateLimitWindow = TimeSpan.FromMinutes(1) };
        var limiter = new RegistrationRateLimiter(options, clock);

        limiter.TryAcquire("10.0.0.1");
        limiter.TryAcquire("10.0.0.1");
        Assert.False(limiter.TryAcquire("10.0.0.1"));

        clock.Advance(TimeSpan.FromMinutes(1));

        // A frame with a genuinely flaky link must not be locked out permanently; §4.1's
        // reconnect discipline is retry-forever, and the limiter has to leave room for it.
        Assert.True(limiter.TryAcquire("10.0.0.1"));
    }

    [Fact]
    public void One_noisy_address_cannot_lock_out_another()
    {
        var clock = new TestClock();
        var options = new ControlOptions { RateLimitAttempts = 1, RateLimitWindow = TimeSpan.FromMinutes(1) };
        var limiter = new RegistrationRateLimiter(options, clock);

        limiter.TryAcquire("10.0.0.1");
        Assert.False(limiter.TryAcquire("10.0.0.1"));
        Assert.True(limiter.TryAcquire("10.0.0.2"));
    }

    [Fact]
    public void Requests_with_no_attributable_address_share_one_budget()
    {
        var clock = new TestClock();
        var options = new ControlOptions { RateLimitAttempts = 1, RateLimitWindow = TimeSpan.FromMinutes(1) };
        var limiter = new RegistrationRateLimiter(options, clock);

        Assert.True(limiter.TryAcquire(null));

        // Counting them together keeps the budget enforced. Treating "unknown" as exempt
        // would be a free bypass for any transport that hides its source.
        Assert.False(limiter.TryAcquire(null));
        Assert.False(limiter.TryAcquire(string.Empty));
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
            Assert.True(limiter.TryAcquire($"10.0.0.{i}"));
        }

        // An attacker with a fresh source address per request must not be able to make the
        // limiter itself the memory-exhaustion vector it exists to prevent.
        Assert.False(limiter.TryAcquire("10.0.1.1"));

        // Addresses already being tracked keep working, so the ceiling degrades gracefully
        // rather than blacking out the whole fleet.
        Assert.True(limiter.TryAcquire("10.0.0.0"));
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
}
