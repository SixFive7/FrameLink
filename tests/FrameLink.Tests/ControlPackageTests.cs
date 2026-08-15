using System.Net;
using System.Security.Cryptography;
using FrameLink.Control;
using FrameLink.Control.Storage;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// Fleet-wide package visibility on the server: what is stored, what drift means, and what the
/// operator API answers.
/// </summary>
public sealed class ControlPackageTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // -------------------------------------------------------------------------------------------
    // The baseline
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void The_embedded_baseline_is_the_frozen_v1_reference_verbatim()
    {
        // §7.1: the pin lives in source where changing it is a diff somebody reviews, and nothing
        // is asserted from memory. The container has no repository in it, so the build embeds a
        // copy — and this is what stops the copy quietly becoming the authority.
        var reference = V1Reference.Packages();

        Assert.Equal(929, reference.Count);
        Assert.Equal(reference.Count, PackageBaseline.Versions.Count);

        foreach (var (package, version) in reference)
        {
            Assert.Equal(version, PackageBaseline.VersionOf(package));
        }
    }

    // -------------------------------------------------------------------------------------------
    // Storage
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_inventory_is_stored_and_handed_back_whole()
    {
        using var storage = new StorageFixture();
        await storage.SeeDeviceAsync("AAAA-AAAA-AAAA-AAAA");

        await storage.Packages.RecordInventoryAsync(Inventory("AAAA-AAAA-AAAA-AAAA", 1, Set()), Token);
        var stored = await storage.Packages.GetAsync("AAAA-AAAA-AAAA-AAAA", Token);

        Assert.NotNull(stored);
        Assert.Equal(3, stored.Packages.Count);
        Assert.Equal("0.9.2-1+rpt4", stored.Packages["labwc"]);
        Assert.Equal(PackageInventory.HashOf(Set()), stored.ContentHash);
    }

    [Fact]
    public async Task Two_frames_with_the_same_packages_share_one_stored_set()
    {
        // The whole storage argument. Ten converged frames are ten small rows and one blob; the
        // shape that stored a row per package per device would be ~9300 rows saying one thing.
        using var storage = new StorageFixture();
        await storage.SeeDeviceAsync("AAAA-AAAA-AAAA-AAAA");
        await storage.SeeDeviceAsync("BBBB-BBBB-BBBB-BBBB");

        await storage.Packages.RecordInventoryAsync(Inventory("AAAA-AAAA-AAAA-AAAA", 1, Set()), Token);
        await storage.Packages.RecordInventoryAsync(Inventory("BBBB-BBBB-BBBB-BBBB", 1, Set()), Token);

        Assert.Equal(1, await CountSetsAsync(storage));

        var sets = await storage.Packages.ListAsync(Token);
        Assert.Equal(2, sets.Count);
        Assert.Equal(sets[0].ContentHash, sets[1].ContentHash);
    }

    [Fact]
    public async Task An_inventory_older_than_the_one_already_stored_never_replaces_it()
    {
        // §4.1 buffers on disk while a frame is offline and drains on reconnect, so an
        // out-of-order arrival is ordinary rather than exceptional.
        using var storage = new StorageFixture();
        await storage.SeeDeviceAsync("AAAA-AAAA-AAAA-AAAA");

        var newer = Set();
        newer["grim"] = "1.4.0+ds-2+b2";

        await storage.Packages.RecordInventoryAsync(Inventory("AAAA-AAAA-AAAA-AAAA", 9, newer), Token);
        await storage.Packages.RecordInventoryAsync(Inventory("AAAA-AAAA-AAAA-AAAA", 3, Set()), Token);

        var stored = await storage.Packages.GetAsync("AAAA-AAAA-AAAA-AAAA", Token);
        Assert.Equal(9, stored!.Sequence);
        Assert.Contains("grim", stored.Packages.Keys);
    }

    [Fact]
    public async Task The_same_inventory_arriving_twice_is_one_history_entry()
    {
        using var storage = new StorageFixture();
        await storage.SeeDeviceAsync("AAAA-AAAA-AAAA-AAAA");

        await storage.Packages.RecordInventoryAsync(Inventory("AAAA-AAAA-AAAA-AAAA", 4, Set()), Token);
        await storage.Packages.RecordInventoryAsync(Inventory("AAAA-AAAA-AAAA-AAAA", 4, Set()), Token);

        var history = await storage.Packages.ListHistoryAsync("AAAA-AAAA-AAAA-AAAA", 10, Token);
        Assert.Single(history);
    }

    [Fact]
    public async Task Forgetting_a_device_takes_its_inventory_and_then_its_blob()
    {
        using var storage = new StorageFixture();
        await storage.SeeDeviceAsync("AAAA-AAAA-AAAA-AAAA");
        await storage.Packages.RecordInventoryAsync(Inventory("AAAA-AAAA-AAAA-AAAA", 1, Set()), Token);

        await storage.Devices.ForgetAsync("AAAA-AAAA-AAAA-AAAA", Token);

        Assert.Null(await storage.Packages.GetAsync("AAAA-AAAA-AAAA-AAAA", Token));
        Assert.Equal(1, await CountSetsAsync(storage));

        Assert.Equal(1, await storage.Packages.CollectUnreferencedSetsAsync(Token));
        Assert.Equal(0, await CountSetsAsync(storage));
    }

    [Fact]
    public async Task History_rolls_off_after_a_month_but_never_the_newest_entry()
    {
        // §3.5's month. The newest entry is exempt whatever its age: on a frame stable for six
        // months it is the only record of when its packages last moved, and rolling it off would
        // leave a device with a current set and a history reading "nothing ever happened".
        using var storage = new StorageFixture();
        await storage.SeeDeviceAsync("AAAA-AAAA-AAAA-AAAA");

        var now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        var older = Set();
        var newer = Set();
        newer["grim"] = "1.4.0+ds-2+b2";

        await storage.Packages.RecordInventoryAsync(
            Inventory("AAAA-AAAA-AAAA-AAAA", 1, older, now - TimeSpan.FromDays(90)),
            Token);
        await storage.Packages.RecordInventoryAsync(
            Inventory("AAAA-AAAA-AAAA-AAAA", 2, newer, now - TimeSpan.FromDays(60)),
            Token);

        var rolled = await storage.Packages.ExpireHistoryAsync(now - TimeSpan.FromDays(31), Token);
        var history = await storage.Packages.ListHistoryAsync("AAAA-AAAA-AAAA-AAAA", 10, Token);

        Assert.Equal(1, rolled);
        Assert.Single(history);
        Assert.Contains("grim", history[0].Packages.Keys);

        // The blob the rolled-off entry was the last reference to goes with it.
        Assert.Equal(1, await storage.Packages.CollectUnreferencedSetsAsync(Token));
        Assert.Equal(1, await CountSetsAsync(storage));
    }

    // -------------------------------------------------------------------------------------------
    // Drift against the reviewed baseline
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void A_frame_that_matches_the_baseline_has_no_drift_at_all()
    {
        var packages = V1Reference.Packages();
        var summary = PackageDrift.Summarise(SetFor("AAAA-AAAA-AAAA-AAAA", packages), null, true);

        Assert.Equal(929, summary.Installed);
        Assert.Equal(0, summary.Ahead);
        Assert.Equal(0, summary.Behind);
        Assert.Equal(0, summary.Missing);
        Assert.Equal(0, summary.Extra);
        Assert.Empty(PackageDrift.AgainstBaseline(packages));
    }

    [Fact]
    public void A_security_update_reads_as_ahead_and_never_as_a_fault()
    {
        // The operator's decision in one assertion: forward movement is expected, reported, and
        // never acted on. Debian's stable security uploads keep the upstream version and bump the
        // revision, so `+deb13u2` after `+deb13u1` is what one actually looks like.
        var packages = V1Reference.Packages();
        packages["chromium"] = "1:147.0.7700.100-1~deb13u1+rpt1";
        packages["openssl"] = Bump(packages["openssl"]);

        var summary = PackageDrift.Summarise(SetFor("AAAA-AAAA-AAAA-AAAA", packages), null, true);
        var drift = PackageDrift.AgainstBaseline(packages);

        Assert.Equal(2, summary.Ahead);
        Assert.Equal(0, summary.Behind);
        Assert.All(drift, row => Assert.Equal(PackageDrift.StatusAhead, row.Status));
        Assert.Contains(drift, row => row.Package == "chromium" && row.Installed == "1:147.0.7700.100-1~deb13u1+rpt1");
    }

    [Fact]
    public void A_package_that_moved_backward_or_vanished_is_what_reads_as_wrong()
    {
        var packages = V1Reference.Packages();
        packages["chromium"] = "1:145.0.7600.100-1~deb13u1+rpt1";
        packages.Remove("labwc");
        packages["something-local"] = "0.1";

        var summary = PackageDrift.Summarise(SetFor("AAAA-AAAA-AAAA-AAAA", packages), null, true);
        var drift = PackageDrift.AgainstBaseline(packages);

        Assert.Equal(1, summary.Behind);
        Assert.Equal(1, summary.Missing);
        Assert.Equal(1, summary.Extra);
        Assert.Equal(0, summary.Ahead);

        // Worst first, because the ordering is the triage.
        Assert.Equal(PackageDrift.StatusBehind, drift[0].Status);
        Assert.Equal("chromium", drift[0].Package);
        Assert.Equal(PackageDrift.StatusMissing, drift[1].Status);
        Assert.Equal("labwc", drift[1].Package);
        Assert.Equal(PackageDrift.StatusExtra, drift[2].Status);
    }

    [Fact]
    public void The_drift_list_is_capped_and_says_so_by_returning_fewer_than_it_counted()
    {
        // The pathological case: a frame built from a much newer base image is legitimately ahead
        // on hundreds of packages, and the honest answer is a bounded list beside a real total.
        var packages = V1Reference.Packages();
        foreach (var name in packages.Keys.ToList())
        {
            packages[name] = Bump(packages[name]);
        }

        var summary = PackageDrift.Summarise(SetFor("AAAA-AAAA-AAAA-AAAA", packages), null, true);
        var drift = PackageDrift.AgainstBaseline(packages);

        Assert.Equal(929, summary.Ahead);
        Assert.Equal(PackageDrift.MaxRows, drift.Count);
    }

    // -------------------------------------------------------------------------------------------
    // Drift across the fleet
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void A_fleet_that_agrees_produces_an_empty_disagreement_list()
    {
        var packages = V1Reference.Packages();
        var (rows, total, agreed) = PackageDrift.AcrossFleet(
        [
            SetFor("AAAA-AAAA-AAAA-AAAA", packages),
            SetFor("BBBB-BBBB-BBBB-BBBB", packages),
        ]);

        Assert.Empty(rows);
        Assert.Equal(0, total);
        Assert.Equal(929, agreed);
    }

    [Fact]
    public void Two_frames_that_differ_are_reported_package_by_package_with_the_frames_named()
    {
        var first = V1Reference.Packages();
        var second = V1Reference.Packages();
        second["chromium"] = "1:147.0.7700.100-1~deb13u1+rpt1";
        second.Remove("labwc");

        var (rows, total, agreed) = PackageDrift.AcrossFleet(
        [
            SetFor("AAAA-AAAA-AAAA-AAAA", first),
            SetFor("BBBB-BBBB-BBBB-BBBB", second),
        ]);

        Assert.Equal(2, total);
        Assert.Equal(927, agreed);

        var chromium = rows.Single(row => row.Package == "chromium");
        Assert.Equal(2, chromium.Versions.Count);
        Assert.Equal("1:147.0.7700.100-1~deb13u1+rpt1", chromium.Versions[0].Version);
        Assert.Equal(["BBBB-BBBB-BBBB-BBBB"], chromium.Versions[0].DeviceIds);
        Assert.Equal(["AAAA-AAAA-AAAA-AAAA"], chromium.Versions[1].DeviceIds);

        // A package one frame lacks entirely is a disagreement with a group that has no version.
        var labwc = rows.Single(row => row.Package == "labwc");
        Assert.Null(labwc.Versions[^1].Version);
        Assert.Equal(["BBBB-BBBB-BBBB-BBBB"], labwc.Versions[^1].DeviceIds);
    }

    [Fact]
    public void One_frame_cannot_disagree_with_itself()
    {
        var (rows, total, agreed) = PackageDrift.AcrossFleet(
            [SetFor("AAAA-AAAA-AAAA-AAAA", V1Reference.Packages())]);

        Assert.Empty(rows);
        Assert.Equal(0, total);
        Assert.Equal(929, agreed);
    }

    // -------------------------------------------------------------------------------------------
    // What changed on this frame
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void A_change_set_names_the_four_things_that_can_happen_to_a_package()
    {
        var before = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["stays"] = "1.0",
            ["moves-up"] = "1.0",
            ["moves-down"] = "2.0",
            ["goes"] = "1.0",
        };

        var after = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["stays"] = "1.0",
            ["moves-up"] = "1.1",
            ["moves-down"] = "1.9",
            ["arrives"] = "1.0",
        };

        var change = PackageDrift.Between(before, after, DateTimeOffset.UnixEpoch);

        Assert.Equal(4, change.Total);
        Assert.Equal(PackageDrift.ChangeDowngraded, change.Changes[0].Change);
        Assert.Equal("moves-down", change.Changes[0].Package);
        Assert.Equal("2.0", change.Changes[0].From);
        Assert.Equal("1.9", change.Changes[0].To);
        Assert.Equal(PackageDrift.ChangeRemoved, change.Changes[1].Change);
        Assert.Equal(PackageDrift.ChangeInstalled, change.Changes[2].Change);
        Assert.Equal(PackageDrift.ChangeUpgraded, change.Changes[3].Change);
    }

    [Fact]
    public async Task A_frames_timeline_is_one_entry_per_reported_change()
    {
        using var storage = new StorageFixture();
        await storage.SeeDeviceAsync("AAAA-AAAA-AAAA-AAAA");

        var at = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        var first = Set();
        var second = Set();
        second["grim"] = "1.4.0+ds-2+b2";
        var third = Set();
        third["grim"] = "1.4.0+ds-2+b2";
        third["labwc"] = "0.9.3-1+rpt1";

        await storage.Packages.RecordInventoryAsync(Inventory("AAAA-AAAA-AAAA-AAAA", 1, first, at), Token);
        await storage.Packages.RecordInventoryAsync(
            Inventory("AAAA-AAAA-AAAA-AAAA", 2, second, at.AddHours(6)), Token);
        await storage.Packages.RecordInventoryAsync(
            Inventory("AAAA-AAAA-AAAA-AAAA", 3, third, at.AddHours(12)), Token);

        var history = await storage.Packages.ListHistoryAsync("AAAA-AAAA-AAAA-AAAA", 10, Token);
        var timeline = PackageDrift.Timeline(history);

        // Three reports, two transitions — the oldest set in the window is a state, not a change.
        Assert.Equal(2, timeline.Count);
        Assert.Equal(at.AddHours(12), timeline[0].ObservedUtc);
        Assert.Equal("labwc", timeline[0].Changes[0].Package);
        Assert.Equal(PackageDrift.ChangeUpgraded, timeline[0].Changes[0].Change);
        Assert.Equal("grim", timeline[1].Changes[0].Package);
        Assert.Equal(PackageDrift.ChangeInstalled, timeline[1].Changes[0].Change);
    }

    // -------------------------------------------------------------------------------------------
    // End to end, over a real socket and the real routes
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_adopted_frame_reports_its_packages_and_the_operator_api_shows_the_drift()
    {
        await using var server = await ControlServer.StartAsync("a very long operator password");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        await using (var pending = await server.ConnectAgentAsync(key))
        {
            Assert.Equal(HandshakeStatus.Pending, pending.Result.Status);
        }

        await server.SignInAsync("a very long operator password");
        var deviceId = DeviceIdentity.FingerprintOf(key.ExportSubjectPublicKeyInfo());
        await server.AdoptAsync(deviceId);

        var packages = V1Reference.Packages();
        packages["chromium"] = "1:145.0.7600.100-1~deb13u1+rpt1";

        await using var agent = await server.ConnectAgentAsync(key);
        await agent.SendPackagesAsync(new PackageInventory
        {
            DeviceId = deviceId,
            Sequence = 1,
            GeneratedUtc = DateTimeOffset.UtcNow,
            ContentHash = PackageInventory.HashOf(packages),
            ObservedCount = packages.Count,
            Packages = packages,
        });

        var view = await server.WaitForPackagesAsync(deviceId, response => response.Summary is not null);

        Assert.NotNull(view.Summary);
        Assert.True(view.Online);
        Assert.Equal(929, view.Summary.Installed);
        Assert.Equal(1, view.Summary.Behind);
        Assert.Equal(929, view.BaselineCount);
        Assert.Equal("chromium", Assert.Single(view.Drift).Package);
        Assert.Equal(PackageDrift.StatusBehind, view.Drift[0].Status);
    }

    [Fact]
    public async Task A_known_frame_that_has_never_reported_answers_with_a_null_summary_not_a_404()
    {
        await using var server = await ControlServer.StartAsync("a very long operator password");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        await using (var pending = await server.ConnectAgentAsync(key))
        {
            Assert.Equal(HandshakeStatus.Pending, pending.Result.Status);
        }

        await server.SignInAsync("a very long operator password");
        var deviceId = DeviceIdentity.FingerprintOf(key.ExportSubjectPublicKeyInfo());

        var view = await server.GetPackagesAsync(deviceId);

        Assert.Null(view.Summary);
        Assert.Empty(view.Drift);
        Assert.Empty(view.Recent);

        var unknown = await server.Client.GetAsync("/api/devices/NOPE-NOPE-NOPE-NOPE/packages", Token);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task The_fleet_route_answers_with_the_disagreement_and_not_with_the_sets()
    {
        await using var server = await ControlServer.StartAsync("a very long operator password");
        using var first = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var second = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var firstId = await server.EnrolAsync(first, "a very long operator password");
        var secondId = await server.EnrolAsync(second);

        var theirs = V1Reference.Packages();
        var mine = V1Reference.Packages();
        mine["chromium"] = "1:147.0.7700.100-1~deb13u1+rpt1";

        await using (var agent = await server.ConnectAgentAsync(first))
        {
            await agent.SendPackagesAsync(InventoryFor(firstId, theirs));
            await using var other = await server.ConnectAgentAsync(second);
            await other.SendPackagesAsync(InventoryFor(secondId, mine));

            var fleet = await server.WaitForFleetPackagesAsync(response => response.Devices.Count == 2);

            Assert.Equal(2, fleet.DistinctSets);
            Assert.Equal(928, fleet.Agreed);
            Assert.Equal(1, fleet.DisagreementTotal);

            var row = Assert.Single(fleet.Disagreements);
            Assert.Equal("chromium", row.Package);
            Assert.Equal(2, row.Versions.Count);
            Assert.Equal("1:147.0.7700.100-1~deb13u1+rpt1", row.Versions[0].Version);
            Assert.Equal([secondId], row.Versions[0].DeviceIds);
        }
    }

    [Fact]
    public async Task The_device_id_comes_from_the_proven_socket_and_never_from_the_payload()
    {
        await using var server = await ControlServer.StartAsync("a very long operator password");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var deviceId = await server.EnrolAsync(key, "a very long operator password");

        await using var agent = await server.ConnectAgentAsync(key);
        await agent.SendPackagesAsync(InventoryFor("SOME-BODY-ELSE-0001", Set()));

        var mine = await server.WaitForPackagesAsync(deviceId, response => response.Summary is not null);
        Assert.Equal(deviceId, mine.Summary!.DeviceId);

        var forged = await server.Client.GetAsync("/api/devices/SOME-BODY-ELSE-0001/packages", Token);
        Assert.Equal(HttpStatusCode.NotFound, forged.StatusCode);
    }

    [Fact]
    public async Task An_unreadable_inventory_is_dropped_without_taking_the_connection_with_it()
    {
        await using var server = await ControlServer.StartAsync("a very long operator password");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var deviceId = await server.EnrolAsync(key, "a very long operator password");

        await using var agent = await server.ConnectAgentAsync(key);
        await agent.SendGarbageOnAsync(ControlWire.KindPackageInventory, ProtocolConstants.ChannelTelemetry);
        await agent.SendPackagesAsync(InventoryFor(deviceId, Set()));

        var view = await server.WaitForPackagesAsync(deviceId, response => response.Summary is not null);

        Assert.NotNull(view.Summary);
        Assert.True(agent.IsOpen);
    }

    // -------------------------------------------------------------------------------------------

    private static string Bump(string version) => version + "+deb13u9";

    private static Dictionary<string, string> Set() => new(StringComparer.Ordinal)
    {
        ["labwc"] = "0.9.2-1+rpt4",
        ["chromium"] = "1:146.0.7680.164-1~deb13u1+rpt1",
        ["wlr-randr"] = "0.4.1-1",
    };

    private static DevicePackageSet SetFor(string deviceId, Dictionary<string, string> packages) => new(
        deviceId,
        1,
        new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero),
        PackageInventory.HashOf(packages),
        packages.Count,
        packages);

    private static PackageInventory Inventory(
        string deviceId,
        long sequence,
        Dictionary<string, string> packages,
        DateTimeOffset? at = null) => new()
    {
        DeviceId = deviceId,
        Sequence = sequence,
        GeneratedUtc = at ?? new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero),
        ContentHash = PackageInventory.HashOf(packages),
        ObservedCount = packages.Count,
        Packages = packages,
    };

    private static PackageInventory InventoryFor(string deviceId, Dictionary<string, string> packages) =>
        Inventory(deviceId, 1, packages, DateTimeOffset.UtcNow);

    private static async Task<int> CountSetsAsync(StorageFixture storage)
    {
        await using var connection = storage.Database.OpenRead();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM package_sets;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture);
    }
}
