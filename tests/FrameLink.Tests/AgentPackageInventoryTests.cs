using FrameLink.Agent.Hosting;
using FrameLink.Agent.Link;
using FrameLink.Agent.Resources;
using FrameLink.Agent.Telemetry;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// The whole-system package inventory the agent reports (§4.1's <c>telemetry</c> channel).
/// </summary>
/// <remarks>
/// Everything here drives the shipping <see cref="PackageInventoryReporter"/>,
/// <see cref="AptPackages"/> and <see cref="TelemetryOutbox"/> against <see cref="FakeDebian"/>,
/// which models a package system rather than answering from a field: installing a package
/// genuinely changes what the next <c>dpkg-query -W</c> prints, which is what makes "reported only
/// on change" a behaviour a test can catch rather than a comment.
/// </remarks>
public sealed class AgentPackageInventoryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // -------------------------------------------------------------------------------------------
    // Reading dpkg
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_whole_database_is_read_with_one_process_and_not_one_per_package()
    {
        // ~930 packages is ~930 process launches under the single-package query, several times a
        // day, forever. The list form is the only shape in which this is affordable at all.
        var debian = FakeDebian.StockImage();
        debian.InstallAll(PackageCatalog.Specs.Where(spec => !spec.MustBeAbsent).Select(spec => spec.Package));

        var installed = await new AptPackages(debian).ListInstalledAsync(Token);

        Assert.Equal(14, installed.Count);
        Assert.Single(debian.Commands);
        Assert.Contains("dpkg-query -W", debian.Commands[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_installed_packages_are_reported_never_the_ones_dpkg_merely_remembers()
    {
        // dpkg prints a version for a package in `rc` — removed, configuration kept — and for one
        // whose install was interrupted. Reporting either would put software in the fleet's
        // inventory that is not on the disk.
        var debian = FakeDebian.StockImage();
        debian.InstallAll(["labwc", "chromium"]);
        debian.RemoveWithoutPurging("labwc");
        debian.Interrupted.Add("grim");

        var installed = await new AptPackages(debian).ListInstalledAsync(Token);

        Assert.Equal(["chromium"], installed.Keys);
    }

    [Fact]
    public async Task A_dpkg_that_cannot_be_read_reports_nothing_rather_than_an_empty_system()
    {
        var debian = FakeDebian.StockImage();
        debian.InstallAll(["labwc"]);
        debian.DpkgBroken = true;

        var installed = await new AptPackages(debian).ListInstalledAsync(Token);

        Assert.Empty(installed);
    }

    // -------------------------------------------------------------------------------------------
    // The canonical rendering and its hash — the contract both programs depend on
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void The_canonical_rendering_is_ordinal_ordered_and_newline_terminated()
    {
        var packages = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["zutty"] = "0.16.2",
            ["adduser"] = "3.152",
        };

        Assert.Equal("adduser 3.152\nzutty 0.16.2\n", PackageInventory.Canonicalise(packages));
    }

    [Fact]
    public void The_hash_ignores_the_order_the_packages_arrived_in()
    {
        // The hash is a storage key shared across the whole fleet, so two frames with the same
        // packages must produce the same key whatever order their dpkg printed them in.
        var forwards = new Dictionary<string, string>(StringComparer.Ordinal) { ["a"] = "1", ["b"] = "2" };
        var backwards = new Dictionary<string, string>(StringComparer.Ordinal) { ["b"] = "2", ["a"] = "1" };

        Assert.Equal(PackageInventory.HashOf(forwards), PackageInventory.HashOf(backwards));
        Assert.NotEqual(
            PackageInventory.HashOf(forwards),
            PackageInventory.HashOf(new Dictionary<string, string>(StringComparer.Ordinal) { ["a"] = "1", ["b"] = "3" }));
    }

    [Fact]
    public void The_canonical_rendering_round_trips_through_its_own_reader()
    {
        var packages = V1Reference.Packages();
        var parsed = PackageInventory.ParseCanonical(PackageInventory.Canonicalise(packages));

        Assert.Equal(929, parsed.Count);
        Assert.Equal(packages["chromium"], parsed["chromium"]);
        Assert.Equal(PackageInventory.HashOf(packages), PackageInventory.HashOf(parsed));
    }

    // -------------------------------------------------------------------------------------------
    // Cadence
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_first_tick_reports_and_an_unchanged_second_tick_says_nothing()
    {
        // The whole reason the report is not on the telemetry heartbeat. ~30 kB per tick for a set
        // that moves a handful of times a month is the cost this avoids.
        using var store = new TemporaryStore();
        var (reporter, telemetry, _) = Reporter(store);

        Assert.True(await reporter.TickAsync(Token));
        Assert.False(await reporter.TickAsync(Token));
        Assert.Single(telemetry.Inventories);
    }

    [Fact]
    public async Task Installing_something_makes_the_next_tick_report_again()
    {
        using var store = new TemporaryStore();
        var (reporter, telemetry, debian) = Reporter(store);

        await reporter.TickAsync(Token);

        // A transitive dependency, which is the ordinary case: nothing in the catalog names it and
        // the inventory reports it anyway.
        debian.Installed["libspa-0.2-modules"] = "1.4.2-1+rpt3";

        Assert.True(await reporter.TickAsync(Token));
        Assert.Equal(2, telemetry.Inventories.Count);
        Assert.Equal(2, telemetry.Inventories[1].Sequence);
        Assert.Contains("libspa-0.2-modules", telemetry.Inventories[1].Packages.Keys);
    }

    [Fact]
    public async Task A_reboot_does_not_re_send_a_set_that_has_not_moved()
    {
        // §2.4 reboots after every applied resource, so the agent restarts constantly during a
        // provision. A hash held only in memory would make every one of those a full report.
        using var store = new TemporaryStore();
        var (first, telemetry, debian) = Reporter(store);
        await first.TickAsync(Token);

        var second = new PackageInventoryReporter(
            new AptPackages(debian),
            telemetry,
            store.Store,
            new ManualClock(),
            new RecordingLog())
        {
            DeviceId = "AAAA-AAAA-AAAA-AAAA",
        };

        Assert.False(await second.TickAsync(Token));
        Assert.Single(telemetry.Inventories);
    }

    [Fact]
    public async Task A_dpkg_that_answers_nothing_is_never_reported_as_a_frame_with_no_packages()
    {
        // An empty inventory would replace a real one on the server and make every other frame
        // look like it had diverged from this one.
        using var store = new TemporaryStore();
        var (reporter, telemetry, debian) = Reporter(store);
        debian.DpkgBroken = true;

        Assert.False(await reporter.TickAsync(Token));
        Assert.Empty(telemetry.Inventories);
    }

    [Fact]
    public async Task The_report_carries_the_hash_the_server_will_recompute()
    {
        using var store = new TemporaryStore();
        var (reporter, telemetry, debian) = Reporter(store);
        await reporter.TickAsync(Token);

        var inventory = Assert.Single(telemetry.Inventories);
        var installed = await new AptPackages(debian).ListInstalledAsync(Token);

        Assert.Equal(PackageInventory.HashOf(installed), inventory.ContentHash);
        Assert.Equal(inventory.Packages.Count, inventory.ObservedCount);
        Assert.Equal("AAAA-AAAA-AAAA-AAAA", inventory.DeviceId);
    }

    [Fact]
    public void The_interval_is_a_fleet_setting_with_a_default_that_catches_the_nightly_upgrade()
    {
        using var store = new TemporaryStore();
        var debian = FakeDebian.StockImage();
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        var reporter = new PackageInventoryReporter(
            new AptPackages(debian),
            new RecordingPackageTelemetry(),
            store.Store,
            new ManualClock(),
            new RecordingLog(),
            FleetValues.From(settings))
        {
            DeviceId = "AAAA-AAAA-AAAA-AAAA",
        };

        Assert.Equal(TimeSpan.FromHours(6), reporter.Interval);

        settings[PackageInventoryReporter.IntervalSettingKey] = "00:30:00";
        Assert.Equal(TimeSpan.FromMinutes(30), reporter.Interval);

        // A value nobody can act on falls back rather than disabling the mechanism silently.
        settings[PackageInventoryReporter.IntervalSettingKey] = "not a duration";
        Assert.Equal(TimeSpan.FromHours(6), reporter.Interval);
    }

    // -------------------------------------------------------------------------------------------
    // Surviving an outage (§4.1)
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_inventory_sent_with_no_link_is_buffered_and_drains_on_reconnect()
    {
        using var store = new TemporaryStore();
        using var uplink = new AgentUplink();
        var outbox = new TelemetryOutbox(uplink, store.Store, new RecordingLog());

        await outbox.InventoryAsync(Inventory(1), Token);
        Assert.NotNull(store.Store.ReadText(TelemetryOutbox.PackagesFileName));

        var transport = new InventoryTransport();
        using var attachment = uplink.Attach(transport);
        await outbox.DrainAsync(Token);

        Assert.Null(store.Store.ReadText(TelemetryOutbox.PackagesFileName));
        var envelope = WireMessage.Decode(Assert.Single(transport.Sent));
        Assert.Equal(ControlWire.KindPackageInventory, envelope!.Kind);
        Assert.Equal(ProtocolConstants.ChannelTelemetry, envelope.Channel);
    }

    [Fact]
    public async Task Only_the_newest_buffered_inventory_survives_an_outage()
    {
        // A picture, not history. A frame that spent a week offline while apt ran twice should
        // deliver the set it ended with, not a megabyte of superseded package lists.
        using var store = new TemporaryStore();
        using var uplink = new AgentUplink();
        var outbox = new TelemetryOutbox(uplink, store.Store, new RecordingLog());

        await outbox.InventoryAsync(Inventory(1), Token);
        await outbox.InventoryAsync(Inventory(2), Token);

        var transport = new InventoryTransport();
        using var attachment = uplink.Attach(transport);
        await outbox.DrainAsync(Token);

        var envelope = WireMessage.Decode(Assert.Single(transport.Sent));
        Assert.Equal(2, envelope!.PayloadAs(ProtocolJson.Default.PackageInventory)!.Sequence);
    }

    [Fact]
    public async Task A_buffered_inventory_goes_out_as_the_bytes_a_live_one_would_have()
    {
        // Same rule the event buffer follows, and the reason EncodeRaw exists: what the Fleet
        // Manager receives must not depend on whether the frame happened to be online when the
        // observation was made. Asserted as the two byte sequences, because an assertion on the
        // deserialised value would pass under exactly the re-serialisation this forbids.
        using var offline = new TemporaryStore();
        using var offlineUplink = new AgentUplink();
        var buffering = new TelemetryOutbox(offlineUplink, offline.Store, new RecordingLog());

        await buffering.InventoryAsync(Inventory(7), Token);
        Assert.NotNull(offline.Store.ReadText(TelemetryOutbox.PackagesFileName));

        var drained = new InventoryTransport();
        using (offlineUplink.Attach(drained))
        {
            await buffering.DrainAsync(Token);
        }

        using var online = new TemporaryStore();
        using var onlineUplink = new AgentUplink();
        var live = new InventoryTransport();
        using var attachment = onlineUplink.Attach(live);

        await new TelemetryOutbox(onlineUplink, online.Store, new RecordingLog())
            .InventoryAsync(Inventory(7), Token);

        Assert.Equal(Assert.Single(live.Sent), Assert.Single(drained.Sent));
        Assert.Null(offline.Store.ReadText(TelemetryOutbox.PackagesFileName));
    }

    private static PackageInventory Inventory(long sequence)
    {
        var packages = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["labwc"] = "0.9.2-1+rpt4",
            ["chromium"] = "1:146.0.7680.164-1~deb13u1+rpt1",
        };

        return new PackageInventory
        {
            DeviceId = "AAAA-AAAA-AAAA-AAAA",
            Sequence = sequence,
            GeneratedUtc = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero),
            ContentHash = PackageInventory.HashOf(packages),
            ObservedCount = packages.Count,
            Packages = packages,
        };
    }

    private static (PackageInventoryReporter Reporter, RecordingPackageTelemetry Telemetry, FakeDebian Debian)
        Reporter(TemporaryStore store)
    {
        var debian = FakeDebian.StockImage();
        debian.InstallAll(PackageCatalog.Specs.Where(spec => !spec.MustBeAbsent).Select(spec => spec.Package));

        var telemetry = new RecordingPackageTelemetry();

        return (
            new PackageInventoryReporter(
                new AptPackages(debian),
                telemetry,
                store.Store,
                new ManualClock(),
                new RecordingLog())
            {
                DeviceId = "AAAA-AAAA-AAAA-AAAA",
            },
            telemetry,
            debian);
    }
}

/// <summary>A transport that keeps whatever the uplink writes to it.</summary>
internal sealed class InventoryTransport : IControlTransport
{
    public List<byte[]> Sent { get; } = [];

    public ValueTask SendAsync(ReadOnlyMemory<byte> utf8, CancellationToken cancellationToken)
    {
        Sent.Add(utf8.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Keeps every inventory handed to it, in order.</summary>
internal sealed class RecordingPackageTelemetry : IPackageTelemetry
{
    public List<PackageInventory> Inventories { get; } = [];

    public ValueTask InventoryAsync(PackageInventory inventory, CancellationToken cancellationToken)
    {
        Inventories.Add(inventory);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The v1 reference inventory, read from the repository rather than remembered.
/// </summary>
/// <remarks>
/// §7.1: version claims are never asserted from memory. Both the agent's fifteen recorded floors
/// and the Fleet Manager's embedded 929-package baseline are transcriptions of this file, and the
/// tests that keep them honest all read it through here so there is one parser and one idea of
/// where the block starts and ends.
/// </remarks>
internal static class V1Reference
{
    /// <summary>Header line that opens the package block.</summary>
    private const string BlockHeader = "== PACKAGES";

    /// <summary>Every package the frozen v1 frame had, with its exact version.</summary>
    public static Dictionary<string, string> Packages()
    {
        var path = Path.Combine(GuiFreshnessTests.RepositoryRoot(), "reference", "v1-state-inventory.txt");
        var packages = new Dictionary<string, string>(StringComparer.Ordinal);
        var inside = false;

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.TrimEnd();

            if (line.StartsWith("== ", StringComparison.Ordinal))
            {
                inside = line.StartsWith(BlockHeader, StringComparison.Ordinal);
                continue;
            }

            if (!inside || line.Length == 0 || line.StartsWith("===", StringComparison.Ordinal))
            {
                continue;
            }

            var space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0)
            {
                packages[line[..space]] = line[(space + 1)..].Trim();
            }
        }

        return packages;
    }
}
