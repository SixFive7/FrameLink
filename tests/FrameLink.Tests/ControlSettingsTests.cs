using FrameLink.Control.Storage;

namespace FrameLink.Tests;

/// <summary>
/// The settings mechanism of §3.4, and the half of "a pending device receives nothing" that
/// lives in storage.
/// </summary>
public sealed class ControlSettingsTests
{
    private const string Device = "AAAA-AAAA-AAAA-AAAA";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_fleet_default_reaches_every_adopted_device()
    {
        using var fixture = new StorageFixture();
        await AdoptAsync(fixture, Device);
        await fixture.Settings.SetFleetDefaultAsync("slideshow.interval", "30", Token);

        var resolved = await fixture.Settings.ResolveAsync(Device, Token);

        Assert.Equal("30", resolved.Values["slideshow.interval"]);
    }

    [Fact]
    public async Task A_per_device_override_always_wins()
    {
        using var fixture = new StorageFixture();
        await AdoptAsync(fixture, Device);
        await fixture.Settings.SetFleetDefaultAsync("volume", "60", Token);
        await fixture.Settings.SetDeviceOverrideAsync(Device, "volume", "25", Token);

        var resolved = await fixture.Settings.ResolveAsync(Device, Token);

        // §3.4 states the rule without qualification: the override always wins. Not "wins if
        // newer", not "wins unless the fleet value changed since".
        Assert.Equal("25", resolved.Values["volume"]);
    }

    [Fact]
    public async Task An_override_still_wins_after_the_fleet_default_is_changed_afterwards()
    {
        using var fixture = new StorageFixture();
        await AdoptAsync(fixture, Device);
        await fixture.Settings.SetDeviceOverrideAsync(Device, "volume", "25", Token);
        await fixture.Settings.SetFleetDefaultAsync("volume", "90", Token);

        var resolved = await fixture.Settings.ResolveAsync(Device, Token);

        Assert.Equal("25", resolved.Values["volume"]);
    }

    [Fact]
    public async Task Removing_an_override_restores_the_fleet_default()
    {
        using var fixture = new StorageFixture();
        await AdoptAsync(fixture, Device);
        await fixture.Settings.SetFleetDefaultAsync("volume", "60", Token);
        await fixture.Settings.SetDeviceOverrideAsync(Device, "volume", "25", Token);

        Assert.True(await fixture.Settings.RemoveDeviceOverrideAsync(Device, "volume", Token));

        var resolved = await fixture.Settings.ResolveAsync(Device, Token);
        Assert.Equal("60", resolved.Values["volume"]);
    }

    [Fact]
    public async Task An_override_with_no_fleet_default_is_still_delivered()
    {
        using var fixture = new StorageFixture();
        await AdoptAsync(fixture, Device);
        await fixture.Settings.SetDeviceOverrideAsync(Device, "call.room", "kitchen", Token);

        var resolved = await fixture.Settings.ResolveAsync(Device, Token);

        // The mechanism is generic (§3.4): a key does not have to exist fleet-wide before one
        // device can have it, because the list of settings is expected to grow.
        Assert.Equal("kitchen", resolved.Values["call.room"]);
    }

    [Fact]
    public async Task A_pending_device_resolves_to_nothing_at_all()
    {
        using var fixture = new StorageFixture();
        await fixture.SeeDeviceAsync(Device);
        await fixture.Settings.SetFleetDefaultAsync("volume", "60", Token);
        await fixture.Settings.SetFleetDefaultAsync("slideshow.interval", "30", Token);

        var resolved = await fixture.Settings.ResolveAsync(Device, Token);

        // The whole point of §3.3. Fleet defaults exist and would apply the moment it is
        // adopted, but until then the device gets no configuration whatsoever.
        Assert.Empty(resolved.Values);
    }

    [Fact]
    public async Task A_blocked_device_resolves_to_nothing_at_all()
    {
        using var fixture = new StorageFixture();
        await AdoptAsync(fixture, Device);
        await fixture.Settings.SetFleetDefaultAsync("volume", "60", Token);
        await fixture.Devices.BlockAsync(Device, Token);

        var resolved = await fixture.Settings.ResolveAsync(Device, Token);

        Assert.Empty(resolved.Values);
    }

    [Fact]
    public async Task A_device_nobody_has_ever_met_resolves_to_nothing_at_all()
    {
        using var fixture = new StorageFixture();
        await fixture.Settings.SetFleetDefaultAsync("volume", "60", Token);

        var resolved = await fixture.Settings.ResolveAsync("ZZZZ-ZZZZ-ZZZZ-ZZZZ", Token);

        Assert.Empty(resolved.Values);
    }

    [Fact]
    public async Task An_override_cannot_be_written_for_a_device_that_is_not_adopted()
    {
        using var fixture = new StorageFixture();
        await fixture.SeeDeviceAsync(Device);

        var written = await fixture.Settings.SetDeviceOverrideAsync(Device, "volume", "25", Token);

        // Refused rather than stored-and-ignored: §3.3 says a pending record allocates no
        // resources, and a settings row is a resource.
        Assert.False(written);
        Assert.Empty(await fixture.Settings.GetDeviceOverridesAsync(Device, Token));
    }

    [Fact]
    public async Task An_override_cannot_be_written_for_a_device_that_does_not_exist()
    {
        using var fixture = new StorageFixture();

        Assert.False(await fixture.Settings.SetDeviceOverrideAsync("ZZZZ-ZZZZ-ZZZZ-ZZZZ", "v", "1", Token));
    }

    [Fact]
    public async Task Every_settings_write_moves_the_revision_forward()
    {
        using var fixture = new StorageFixture();
        await AdoptAsync(fixture, Device);

        var start = await fixture.Settings.GetRevisionAsync(Token);
        await fixture.Settings.SetFleetDefaultAsync("volume", "60", Token);
        var afterFleet = await fixture.Settings.GetRevisionAsync(Token);
        await fixture.Settings.SetDeviceOverrideAsync(Device, "volume", "25", Token);
        var afterOverride = await fixture.Settings.GetRevisionAsync(Token);

        Assert.True(afterFleet > start);
        Assert.True(afterOverride > afterFleet);
    }

    [Fact]
    public async Task A_refused_override_does_not_move_the_revision()
    {
        using var fixture = new StorageFixture();
        await fixture.SeeDeviceAsync(Device);

        var start = await fixture.Settings.GetRevisionAsync(Token);
        await fixture.Settings.SetDeviceOverrideAsync(Device, "volume", "25", Token);

        // Nothing changed, so nothing should look like it changed — otherwise every online
        // device would re-fetch settings because an attacker poked a pending row.
        Assert.Equal(start, await fixture.Settings.GetRevisionAsync(Token));
    }

    private static async Task AdoptAsync(StorageFixture fixture, string deviceId)
    {
        await fixture.SeeDeviceAsync(deviceId);
        await fixture.Devices.AdoptAsync(deviceId, "Test frame", Token);
    }
}
