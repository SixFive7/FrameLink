using FrameLink.Control.Storage;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// The device table's behaviours, asserted against a real SQLite file.
/// </summary>
/// <remarks>
/// The invariants here are the ones an internet-exposed registration path depends on: a
/// reconnect cannot change adoption state, the pending table is bounded, and un-adoption
/// gives back everything adoption granted.
/// </remarks>
public sealed class ControlStorageTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_device_that_has_never_been_seen_appears_as_pending()
    {
        using var fixture = new StorageFixture();

        var record = await fixture.SeeDeviceAsync("AAAA-AAAA-AAAA-AAAA");

        // Pointing a frame at the URL is enough to make it appear (§3.3), and appearing means
        // pending — never adopted, never trusted.
        Assert.Equal(DeviceState.Pending, record.State);
        Assert.Null(record.DisplayName);
    }

    [Fact]
    public async Task Reconnecting_refreshes_the_report_but_never_the_adoption_state()
    {
        using var fixture = new StorageFixture();
        await fixture.SeeDeviceAsync("AAAA-AAAA-AAAA-AAAA");
        await fixture.Devices.BlockAsync("AAAA-AAAA-AAAA-AAAA", Token);

        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        var reconnected = await fixture.Devices.RecordContactAsync(
            new DeviceContact
            {
                DeviceId = "AAAA-AAAA-AAAA-AAAA",
                PublicKey = "key",
                ProtocolVersion = ProtocolConstants.Version,
                AgentVersion = "9.9.9",
                AgentStatus = "self-update failed twice",
            },
            pendingCap: 100,
            Token);

        // A blocked frame reconnecting must not launder itself back into the list, but its
        // self-report still has to reach the operator — that is what makes a broken agent
        // legible rather than a mystery row.
        Assert.Equal(DeviceState.Blocked, reconnected.State);
        Assert.Equal("9.9.9", reconnected.AgentVersion);
        Assert.Equal("self-update failed twice", reconnected.AgentStatus);
    }

    [Fact]
    public async Task Blocked_devices_are_hidden_from_the_list_but_reachable_behind_the_toggle()
    {
        using var fixture = new StorageFixture();
        await fixture.SeeDeviceAsync("AAAA-AAAA-AAAA-AAAA");
        await fixture.SeeDeviceAsync("BBBB-BBBB-BBBB-BBBB");
        await fixture.Devices.BlockAsync("BBBB-BBBB-BBBB-BBBB", Token);

        var visible = await fixture.Devices.ListAsync(includeBlocked: false, Token);
        var everything = await fixture.Devices.ListAsync(includeBlocked: true, Token);

        // §3.3: filtered by default, still there behind the toggle, so an accidental block is
        // reversible rather than a device that has silently vanished.
        Assert.Equal(["AAAA-AAAA-AAAA-AAAA"], visible.Select(d => d.DeviceId));
        Assert.Equal(2, everything.Count);
    }

    [Fact]
    public async Task Un_adopting_gives_back_the_name_and_the_overrides()
    {
        using var fixture = new StorageFixture();
        await fixture.SeeDeviceAsync("AAAA-AAAA-AAAA-AAAA");
        await fixture.Devices.AdoptAsync("AAAA-AAAA-AAAA-AAAA", "Kitchen", Token);
        await fixture.Settings.SetDeviceOverrideAsync("AAAA-AAAA-AAAA-AAAA", "volume", "40", Token);

        var pending = await fixture.Devices.ReturnToPendingAsync("AAAA-AAAA-AAAA-AAAA", Token);
        var overrides = await fixture.Settings.GetDeviceOverridesAsync("AAAA-AAAA-AAAA-AAAA", Token);

        // Un-adoption is the reverse of adoption, so it has to revoke what adoption granted.
        // A pending row holding settings would be a pending record that owns a resource.
        Assert.Equal(DeviceState.Pending, pending!.State);
        Assert.Null(pending.DisplayName);
        Assert.Empty(overrides);
    }

    [Fact]
    public async Task Forgetting_a_device_takes_its_settings_with_it()
    {
        using var fixture = new StorageFixture();
        await fixture.SeeDeviceAsync("AAAA-AAAA-AAAA-AAAA");
        await fixture.Devices.AdoptAsync("AAAA-AAAA-AAAA-AAAA", "Kitchen", Token);
        await fixture.Settings.SetDeviceOverrideAsync("AAAA-AAAA-AAAA-AAAA", "volume", "40", Token);

        Assert.True(await fixture.Devices.ForgetAsync("AAAA-AAAA-AAAA-AAAA", Token));

        Assert.Null(await fixture.Devices.FindAsync("AAAA-AAAA-AAAA-AAAA", Token));
        Assert.Empty(await fixture.Settings.GetDeviceOverridesAsync("AAAA-AAAA-AAAA-AAAA", Token));
    }

    [Fact]
    public async Task Adoption_state_changes_are_refused_for_a_device_nobody_has_met()
    {
        using var fixture = new StorageFixture();

        var adoption = await fixture.Devices.AdoptAsync("ZZZZ-ZZZZ-ZZZZ-ZZZZ", "Ghost", Token);
        Assert.Equal(DeviceAdoptionResult.Unknown, adoption.Result);
        Assert.Null(adoption.Record);
        Assert.Null(await fixture.Devices.BlockAsync("ZZZZ-ZZZZ-ZZZZ-ZZZZ", Token));
        Assert.False(await fixture.Devices.ForgetAsync("ZZZZ-ZZZZ-ZZZZ-ZZZZ", Token));
    }

    [Fact]
    public async Task Timestamps_survive_a_round_trip_through_the_database()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 8, 15, 9, 30, 15, 123, TimeSpan.Zero));
        using var fixture = new StorageFixture(clock);

        var record = await fixture.SeeDeviceAsync("AAAA-AAAA-AAAA-AAAA");

        // The auto-expiry sweep is a range query over these strings, so losing precision or
        // offset here would silently break the abuse control that depends on it.
        Assert.Equal(clock.GetUtcNow(), record.FirstSeenUtc);
        Assert.Equal(TimeSpan.Zero, record.LastSeenUtc.Offset);
    }
}
