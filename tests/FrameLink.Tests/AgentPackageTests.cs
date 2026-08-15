using System.Globalization;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// The catalog's package block — <c>pkg.*</c>, positions 6–22.
/// </summary>
/// <remarks>
/// <para>
/// Every test here drives the shipping <see cref="PackageResource"/>, <see cref="AptPackages"/>
/// and <see cref="ReconcileLoop"/> against <see cref="FakeDebian"/>, which is a model of the
/// package system rather than a table of canned answers: <c>apt-get install</c> genuinely changes
/// what <c>dpkg-query</c> subsequently reports, an unreachable archive genuinely produces apt's
/// own error text, and a dependency genuinely drags another package in. A double that answered
/// from a field the test sets could not produce the faults these checks exist to catch.
/// </para>
/// <para>
/// No Raspberry Pi has run any of it. What is asserted is the state of a modelled package system
/// after a reconciliation and the words the frame would put on its screen, not that a method was
/// called.
/// </para>
/// </remarks>
public sealed class AgentPackageTests
{
    /// <summary>The fifteen ids, in the order the catalog's own dependency table gives them.</summary>
    private static readonly string[] CatalogOrder =
    [
        "pkg.labwc",
        "pkg.chromium",
        "pkg.wireplumber",
        "pkg.pipewire-alsa",
        "pkg.wlr-randr",
        "pkg.xdg-desktop-portal",
        "pkg.xdg-desktop-portal-gtk",
        "pkg.gstreamer1.0-tools",
        "pkg.gstreamer1.0-plugins-base",
        "pkg.gstreamer1.0-libcamera",
        "pkg.gstreamer1.0-pipewire",
        "pkg.libspa-0.2-libcamera.absent",
        "pkg.dfu-util",
        "pkg.grim",
        "pkg.unattended-upgrades",
    ];

    private static ReconcileOptions Fast => new() { Countdown = TimeSpan.Zero, AttemptBudget = 3 };

    // ---------------------------------------------------------------------------------------
    // The block itself
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void The_block_is_exactly_the_packages_the_catalog_lists_in_the_order_it_lists_them()
    {
        // The count is the acceptance criterion for this migration: positions 6–22 are seventeen
        // entries, of which `pkg.git` is superseded by the pinned-fetch pattern (open question 3)
        // and `tool.xvf-host.installed` is not an apt package at all.
        var built = PackageCatalog.Build(new AptPackages(new FakeDebian()));

        Assert.Equal(15, built.Count);
        Assert.Equal(CatalogOrder, built.Select(resource => resource.Name));
    }

    [Fact]
    public void No_package_resource_declares_a_dependency_because_the_catalog_gives_them_all_a_dash()
    {
        // Including on adoption. §3.3 withholds *configuration* from a pending device, and a
        // package set the catalog fixes is not configuration — while §2.7 needs the kiosk stack
        // present before the browser stage can render anything at all.
        var built = PackageCatalog.Build(new AptPackages(new FakeDebian()));

        Assert.All(built, resource => Assert.Empty(resource.DependsOn));
    }

    [Fact]
    public async Task Nothing_in_the_block_pins_a_version_upward()
    {
        // The reviewed version is a floor, never a ceiling: whatever the archive has moved on to,
        // however far past what anybody reviewed, the frame is in sync. Asserted as behaviour
        // rather than as the absence of a digit in a string, because several of these package
        // names contain digits themselves.
        var debian = FakeDebian.StockImage();

        foreach (var spec in PackageCatalog.Specs.Where(item => !item.MustBeAbsent))
        {
            debian.Installed[spec.Package] = "99999:0-whatever-the-archive-serves";
        }

        using var harness = new ReconcileHarness(Fast, [.. PackageCatalog.Build(new AptPackages(debian))]);
        var outcome = await harness.PassAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.Empty(harness.Boundary.Crossings);
    }

    [Fact]
    public void The_shipped_catalog_carries_the_block_after_the_agent_roots_and_before_the_hostname()
    {
        using var files = new TemporaryFiles();
        var order = DeviceCatalog.BuildGraph(AgentResourceGraphTests.Context(files))
            .Ordered
            .Select(resource => resource.Name)
            .ToList();

        Assert.All(CatalogOrder, name => Assert.Contains(name, order));
        Assert.True(order.IndexOf(AdoptionResource.ResourceName) < order.IndexOf("pkg.labwc"));
        Assert.True(order.IndexOf("pkg.unattended-upgrades") < order.IndexOf(HostnameResource.ResourceName));

        // Declaration order puts the one negative assertion after everything in the block that
        // could plausibly drag it back in.
        Assert.True(order.IndexOf("pkg.gstreamer1.0-pipewire") < order.IndexOf("pkg.libspa-0.2-libcamera.absent"));
    }

    // ---------------------------------------------------------------------------------------
    // Observe
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stock_frame_reports_every_one_of_them_missing()
    {
        var debian = FakeDebian.StockImage();
        var apt = new AptPackages(debian);

        foreach (var resource in PackageCatalog.Build(apt).Cast<PackageResource>().Where(item => !item.MustBeAbsent))
        {
            var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);

            Assert.False(observation.InSync);
            Assert.Equal(ObservationOutcome.Drifted, observation.Outcome);
            Assert.Contains("not installed", observation.Observed, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_package_removed_but_not_purged_is_not_installed()
    {
        // The trap the catalog's choice of dpkg-query format exists for. `dpkg -l` prints a line
        // for a package in `rc`, so anything deciding presence by finding the name in a list calls
        // this installed. The status field says `config-files`, and it is not.
        var debian = FakeDebian.StockImage();
        debian.RemoveWithoutPurging("labwc");

        var observation = await Resource(debian, "labwc").ObserveAsync(TestContext.Current.CancellationToken);

        Assert.False(observation.InSync);
        Assert.Contains("dpkg 'rc'", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_half_configured_package_is_not_installed_either()
    {
        var debian = FakeDebian.StockImage();
        debian.Interrupted.Add("chromium");

        var observation = await Resource(debian, "chromium").ObserveAsync(TestContext.Current.CancellationToken);

        Assert.False(observation.InSync);
        Assert.Contains("half-configured", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_package_ahead_of_the_reviewed_version_is_in_sync_and_still_reported()
    {
        // This is what a Debian security update looks like from the frame's side, and it is the
        // case the whole one-sided comparison exists for: a literal pin would call it drift and
        // §2.6 would stop the product until the update had been undone. The version still reaches
        // the observed value, because a delta that names it is worth more than one that does not.
        var debian = FakeDebian.StockImage();
        debian.Installed["labwc"] = "0.9.9-1+rpt9";

        var observation = await Resource(debian, "labwc").ObserveAsync(TestContext.Current.CancellationToken);

        Assert.True(observation.InSync);
        Assert.Contains("0.9.9-1+rpt9", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_package_below_the_reviewed_version_is_drift_and_the_delta_names_both()
    {
        // The other direction, and the reason the floor is recorded at all. Nothing a frame does
        // on its own moves a package backward, so finding one there means something is wrong —
        // and §2.5 needs a person to be able to read what.
        var debian = FakeDebian.StockImage();
        debian.Installed["labwc"] = "0.9.1-1+rpt1";

        var observation = await Resource(debian, "labwc").ObserveAsync(TestContext.Current.CancellationToken);

        Assert.False(observation.InSync);
        Assert.Equal(ObservationOutcome.Drifted, observation.Outcome);
        Assert.Contains("0.9.2-1+rpt4", observation.Delta, StringComparison.Ordinal);
        Assert.Contains("0.9.1-1+rpt1", observation.Delta, StringComparison.Ordinal);
        Assert.Contains("older than the reviewed version", observation.Delta, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repairing_a_package_that_moved_backward_installs_rather_than_downgrades()
    {
        // The floor is a record, not a pin, and the Act proves it: apt-get install brings the
        // package up to whatever the archive now offers. Nothing anywhere names the reviewed
        // version on a command line.
        var debian = FakeDebian.StockImage();
        debian.InstallAll(PackageCatalog.Specs.Where(spec => !spec.MustBeAbsent).Select(spec => spec.Package));
        debian.Installed["labwc"] = "0.9.1-1+rpt1";

        using var harness = new ReconcileHarness(Fast, [.. PackageCatalog.Build(new AptPackages(debian))]);
        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.Equal("0.9.2-1+rpt4", debian.Installed["labwc"]);
        Assert.Contains(debian.Commands, command => command.Contains("apt-get install -y labwc", StringComparison.Ordinal));
        Assert.All(
            debian.Commands,
            command => Assert.DoesNotContain("0.9.2-1+rpt4", command, StringComparison.Ordinal));
    }

    [Fact]
    public void Every_reviewed_version_matches_the_frozen_v1_reference()
    {
        // §7.1: version claims are never asserted from memory. The catalog records a floor per
        // package and this reads the file those floors were transcribed from, so the two can
        // never quietly part company. Two of the fifteen have no floor and both are deliberate:
        // libspa-0.2-libcamera asserts absence, and unattended-upgrades is not on the v1 frame.
        var reference = V1Reference.Packages();
        var checkedCount = 0;

        foreach (var spec in PackageCatalog.Specs)
        {
            if (spec.MustBeAbsent)
            {
                Assert.Null(spec.ReviewedVersion);
                Assert.False(reference.ContainsKey(spec.Package));
                continue;
            }

            if (spec.ReviewedVersion is null)
            {
                Assert.Equal("unattended-upgrades", spec.Package);
                Assert.False(reference.ContainsKey(spec.Package));
                continue;
            }

            Assert.Equal(reference[spec.Package], spec.ReviewedVersion);
            checkedCount++;
        }

        Assert.Equal(13, checkedCount);
    }

    [Fact]
    public async Task Observe_asks_dpkg_and_never_the_network()
    {
        // The catalog's observability rule: readable on a freshly booted frame, with no preceding
        // action in the same session. A converged frame that has just come up must not need apt,
        // and must not be reporting a memory of having installed something.
        var debian = FakeDebian.StockImage();
        debian.InstallAll(PackageCatalog.Specs.Where(spec => !spec.MustBeAbsent).Select(spec => spec.Package));

        using var harness = new ReconcileHarness(Fast, [.. PackageCatalog.Build(new AptPackages(debian))]);
        var outcome = await harness.PassAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.All(debian.Commands, command => Assert.StartsWith("dpkg-query", command, StringComparison.Ordinal));
        Assert.Empty(harness.Boundary.Crossings);
    }

    [Fact]
    public async Task A_dpkg_that_cannot_answer_is_drift_rather_than_silence()
    {
        // Explicitly not Unevaluable. A local read that failed has learned something real about
        // this machine, and that outcome is reserved for an off-device authority that did not
        // answer — it must never become the place a real failure goes to be quiet.
        var debian = FakeDebian.StockImage();
        debian.DpkgBroken = true;

        var observation = await Resource(debian, "grim").ObserveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ObservationOutcome.Drifted, observation.Outcome);
        Assert.Contains("could not be read from dpkg", observation.Observed, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // Applying
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_bare_frame_installs_all_fifteen_and_reboots_once_for_each()
    {
        var debian = FakeDebian.StockImage();
        debian.Installed["libspa-0.2-libcamera"] = "1.4.2-1+rpt3";

        using var harness = new ReconcileHarness(Fast, [.. PackageCatalog.Build(new AptPackages(debian))]);
        var outcome = await harness.ConvergeAsync(limit: 40);

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.All(outcome.Statuses, status => Assert.Equal(ResourceStatusKind.InSync, status.Kind));

        foreach (var spec in PackageCatalog.Specs.Where(item => !item.MustBeAbsent))
        {
            Assert.True(debian.Installed.ContainsKey(spec.Package), spec.Package);
        }

        Assert.False(debian.Installed.ContainsKey("libspa-0.2-libcamera"));

        // §2.4 has no exceptions and decision 26 is explicit that per-resource cleverness about
        // which of these "really" needs a reboot is the reasoning that produced v1's governor bug.
        Assert.Equal(15, harness.Boundary.Crossings.Count);
    }

    [Fact]
    public async Task A_package_that_is_already_there_is_left_completely_alone()
    {
        var debian = FakeDebian.StockImage();
        debian.Installed["grim"] = "1.4.0+ds-2+b2";

        using var harness = new ReconcileHarness(Fast, Resource(debian, "grim"));
        var outcome = await harness.PassAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.Empty(harness.Boundary.Crossings);
        Assert.DoesNotContain(debian.Commands, command => command.Contains("apt-get", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_apt_call_runs_with_a_noninteractive_frontend()
    {
        // The agent is a systemd service with no controlling terminal. A maintainer script that
        // reaches for debconf there can leave a child waiting for an answer that never comes, and
        // a reconciliation pass that never returns is worse than one that fails: nothing on the
        // screen ever changes to say so.
        var debian = FakeDebian.StockImage();

        using var harness = new ReconcileHarness(Fast, Resource(debian, "dfu-util"));
        await harness.ConvergeAsync();

        var aptCalls = debian.Commands.Where(command => command.Contains("apt-get", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(aptCalls);
        Assert.All(aptCalls, command =>
            Assert.StartsWith("env DEBIAN_FRONTEND=noninteractive apt-get", command, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Installing_refreshes_the_package_list_first()
    {
        // There is no resource for the apt index — every package entry's dependsOn is "—" — so
        // the refresh belongs to the Act. "Fresh" is a property of a moment rather than of the
        // frame, so it is not independently verifiable and cannot be a resource under §2.2.
        var debian = FakeDebian.StockImage();

        using var harness = new ReconcileHarness(Fast, Resource(debian, "wlr-randr"));
        await harness.ConvergeAsync();

        var apt = debian.Commands.Where(command => command.Contains("apt-get", StringComparison.Ordinal)).ToList();

        Assert.Equal("env DEBIAN_FRONTEND=noninteractive apt-get update", apt[0]);
        Assert.Equal("env DEBIAN_FRONTEND=noninteractive apt-get install -y wlr-randr", apt[1]);
    }

    [Fact]
    public async Task The_transitive_set_is_apts_problem_and_not_the_catalogs()
    {
        // Guide 5's five pull in roughly 215 dependencies. None is enumerated and none should be:
        // a resource asserting the closure would report drift every time Debian re-cut it.
        var debian = FakeDebian.StockImage();
        debian.Depends["chromium"] = ["rpi-chromium-mods"];

        using var harness = new ReconcileHarness(Fast, Resource(debian, "chromium"));
        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.True(debian.Installed.ContainsKey("rpi-chromium-mods"));
    }

    // ---------------------------------------------------------------------------------------
    // The negative assertion
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_absent_resource_purges_so_that_no_rc_line_survives()
    {
        // purge rather than remove, and the reason is parity: a removed-but-not-purged package
        // keeps an `rc` line in dpkg's list and the v1 reference has no line for it at all.
        var debian = FakeDebian.StockImage();
        debian.Installed["libspa-0.2-libcamera"] = "1.4.2-1+rpt3";

        using var harness = new ReconcileHarness(Fast, Resource(debian, "libspa-0.2-libcamera"));
        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.False(debian.Installed.ContainsKey("libspa-0.2-libcamera"));
        Assert.DoesNotContain("libspa-0.2-libcamera", debian.Removed);
        Assert.Contains(
            "env DEBIAN_FRONTEND=noninteractive apt-get purge -y libspa-0.2-libcamera",
            debian.Commands);
    }

    [Fact]
    public async Task The_absent_resource_never_installs_anything()
    {
        var debian = FakeDebian.StockImage();
        debian.Installed["libspa-0.2-libcamera"] = "1.4.2-1+rpt3";

        using var harness = new ReconcileHarness(Fast, Resource(debian, "libspa-0.2-libcamera"));
        await harness.ConvergeAsync();

        Assert.DoesNotContain(debian.Commands, command => command.Contains("apt-get install", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_package_dragged_back_in_by_a_later_install_is_removed_again()
    {
        // Level-triggered convergence, in the one place in this block where it earns its keep: the
        // catalog calls the WirePlumber fragment "the belt to this braces, for the case where a
        // future dependency drags the plugin back in", and this is the braces doing their half.
        var debian = FakeDebian.StockImage();
        debian.Installed["libspa-0.2-libcamera"] = "1.4.2-1+rpt3";
        debian.Depends["unattended-upgrades"] = ["libspa-0.2-libcamera"];

        using var harness = new ReconcileHarness(Fast, [.. PackageCatalog.Build(new AptPackages(debian))]);
        var outcome = await harness.ConvergeAsync(limit: 40);

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.False(debian.Installed.ContainsKey("libspa-0.2-libcamera"));

        // Once when the block reached it, once after the later install put it back — sixteen
        // crossings rather than fifteen, which is what a repaired drift costs.
        Assert.Equal(16, harness.Boundary.Crossings.Count);
    }

    [Fact]
    public async Task A_plugin_left_in_rc_counts_as_absent_and_costs_no_reboot()
    {
        // Its files are gone, which is the whole of what this resource asserts. Purging the
        // leftover configuration would spend a reboot on something nothing can observe, and the
        // raw state still reaches telemetry rather than being quietly equated with a clean frame.
        var debian = FakeDebian.StockImage();
        debian.Installed["libspa-0.2-libcamera"] = "1.4.2-1+rpt3";
        debian.RemoveWithoutPurging("libspa-0.2-libcamera");

        using var harness = new ReconcileHarness(Fast, Resource(debian, "libspa-0.2-libcamera"));
        var outcome = await harness.PassAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.Empty(harness.Boundary.Crossings);
    }

    // ---------------------------------------------------------------------------------------
    // An unreachable archive is not the same thing as a package that does not exist
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unreachable_archive_is_drift_and_not_an_observation_that_could_not_be_made()
    {
        // The decision this block turns on. Observe is dpkg-query against a local database, so it
        // always has an answer: the frame genuinely does not have labwc, whatever the reason.
        // Calling that Unevaluable would (a) misuse an outcome documented as reserved for an
        // off-device authority, (b) render on the frame and in the Fleet Manager as "waiting for
        // the Fleet Manager", which is untrue, and (c) leave a frame with a genuinely broken
        // sources.list waiting silently forever with nobody told.
        var debian = FakeDebian.StockImage();
        debian.ArchiveReachable = false;

        var resource = Resource(debian, "labwc");
        var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ObservationOutcome.Drifted, observation.Outcome);

        using var harness = new ReconcileHarness(Fast, resource);
        var outcome = await harness.ConvergeAsync();
        var status = ReconcileHarness.StatusOf(outcome, "pkg.labwc");

        Assert.NotEqual(ResourceStatusKind.Blocked, status.Kind);
        Assert.Null(status.BlockedBy);
    }

    [Fact]
    public async Task An_unreachable_archive_names_the_archive_rather_than_the_package()
    {
        var debian = FakeDebian.StockImage();
        debian.ArchiveReachable = false;

        var action = await Resource(debian, "labwc").ActAsync(TestContext.Current.CancellationToken);

        Assert.Contains("the package archive could not be reached", action.Change, StringComparison.Ordinal);
        Assert.Contains("Temporary failure resolving", action.Change, StringComparison.Ordinal);
        Assert.Contains("could not reach the place software is downloaded from", action.Gloss, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_index_behind_an_unreachable_archive_is_not_reported_as_a_missing_package()
    {
        // The sharp edge. A frame whose lists are empty answers `E: Unable to locate package
        // labwc`, a sentence that says the package does not exist — and the cause is that the
        // archive was never reached. Refreshing first is what makes the two distinguishable.
        var debian = FakeDebian.StockImage();
        debian.ArchiveReachable = false;
        debian.HasPackageIndex = false;

        var action = await Resource(debian, "labwc").ActAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Unable to locate package labwc", action.Change, StringComparison.Ordinal);
        Assert.Contains("the package archive could not be reached", action.Change, StringComparison.Ordinal);
        Assert.DoesNotContain("does not offer this package", action.Change, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_package_that_genuinely_is_not_in_the_archive_fails_loudly()
    {
        // The other direction, and getting it wrong here is the worse half: a name the catalog got
        // wrong must reach a person rather than retry politely forever.
        var debian = FakeDebian.StockImage();
        debian.Archive.Remove("grim");

        var action = await Resource(debian, "grim").ActAsync(TestContext.Current.CancellationToken);

        Assert.Contains("the archive answered and does not offer this package", action.Change, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be reached", action.Change, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_archive_that_stays_unreachable_reaches_a_person()
    {
        var debian = FakeDebian.StockImage();
        debian.ArchiveReachable = false;

        using var harness = new ReconcileHarness(Fast, Resource(debian, "labwc"));
        harness.Telemetry.Connected = true;

        var outcome = await harness.ConvergeAsync();
        var status = ReconcileHarness.StatusOf(outcome, "pkg.labwc");

        Assert.Equal(ResourceStatusKind.Escalated, status.Kind);
        Assert.Equal(Fast.AttemptBudget, status.Attempts);
        Assert.NotEmpty(harness.Telemetry.OfKind(DeviceEventKinds.Escalation));
        Assert.Contains("labwc not installed", status.Delta!, StringComparison.Ordinal);

        // A frame that cannot reach Debian is a frame somebody has to look at, and the row they
        // look at has to say so. The delta on its own reads as "labwc is broken".
        Assert.Contains("the package archive could not be reached", status.Action!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_notification_names_the_archive_and_not_only_the_missing_package()
    {
        // The device row above is not what reaches a person. §2.5 rung 3 is a Home Assistant or
        // SMTP notification offering retry or a remote shell, and it is built from the escalation
        // event — which said "labwc is missing" for an unreachable archive and for a package name
        // the catalog got wrong alike, while those two want opposite answers.
        var debian = FakeDebian.StockImage();
        debian.ArchiveReachable = false;

        using var harness = new ReconcileHarness(Fast, Resource(debian, "labwc"));
        harness.Telemetry.Connected = true;

        await harness.ConvergeAsync();
        var escalation = harness.Telemetry.OfKind(DeviceEventKinds.Escalation).First();

        // Symptom and cause, in that order, in the one sentence the notification carries.
        Assert.Contains("is missing", escalation.Summary, StringComparison.Ordinal);
        Assert.Contains("the package archive could not be reached", escalation.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("does not offer this package", escalation.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_transient_outage_is_repaired_by_the_retry_rather_than_by_a_person()
    {
        // §2.5's exponential backoff is the mechanism a network blip is supposed to survive, and
        // it costs nothing extra: the archive comes back before the budget is gone, the resource
        // converges, and nobody is told anything.
        var debian = FakeDebian.StockImage();
        debian.ArchiveReachable = false;

        using var harness = new ReconcileHarness(Fast, Resource(debian, "labwc"));
        harness.Telemetry.Connected = true;

        // One attempt against a dead archive, then the outage ends while the resource is waiting
        // out its backoff. Nobody is notified, because nothing was ever given up on.
        await harness.PassAsync();
        Assert.False(debian.Installed.ContainsKey("labwc"));

        debian.ArchiveReachable = true;
        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.True(debian.Installed.ContainsKey("labwc"));
        Assert.Empty(harness.Telemetry.OfKind(DeviceEventKinds.Escalation));
    }

    [Fact]
    public async Task A_held_package_lock_is_named_as_a_lock_and_clears_by_itself()
    {
        // unattended-upgrades running while the agent wants to install is legitimate and
        // self-clearing. It has to read as "something else was busy", not as a broken package.
        var debian = FakeDebian.StockImage();
        debian.LockedBy = "unattended-upgr";

        var action = await Resource(debian, "pipewire-alsa").ActAsync(TestContext.Current.CancellationToken);
        Assert.Contains("another program is holding the package system's lock", action.Change, StringComparison.Ordinal);

        using var harness = new ReconcileHarness(Fast, Resource(debian, "pipewire-alsa"));

        await harness.PassAsync();
        Assert.False(debian.Installed.ContainsKey("pipewire-alsa"));

        debian.LockedBy = null;
        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.True(debian.Installed.ContainsKey("pipewire-alsa"));
    }

    [Fact]
    public async Task A_refresh_that_fails_does_not_fail_the_resource_when_the_install_works_anyway()
    {
        // apt can install from a cached index. Only the install's own outcome is judged, because
        // failing on the refresh would turn a frame that is perfectly able to converge into one
        // that escalates over a mirror that was briefly down.
        var debian = FakeDebian.StockImage();
        debian.ArchiveReachable = false;
        debian.Cached.Add("grim");

        using var harness = new ReconcileHarness(Fast, Resource(debian, "grim"));
        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.True(debian.Installed.ContainsKey("grim"));
    }

    // ---------------------------------------------------------------------------------------
    // The two pure functions everything above rests on
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("installed 0.9.2-1+rpt4", PackageState.Installed, "0.9.2-1+rpt4")]
    [InlineData("installed", PackageState.Installed, "")]
    [InlineData("config-files 1.4.2-1+rpt3", PackageState.ConfigFilesOnly, "1.4.2-1+rpt3")]
    [InlineData("not-installed ", PackageState.NotInstalled, "")]
    [InlineData("half-configured 146.0.7680.164", PackageState.Partial, "146.0.7680.164")]
    [InlineData("unpacked 1.0", PackageState.Partial, "1.0")]
    [InlineData("triggers-pending 1.0", PackageState.Partial, "1.0")]
    [InlineData("", PackageState.Unreadable, "")]
    [InlineData("something-dpkg-grew-later 1.0", PackageState.Unreadable, "1.0")]
    public void Dpkg_status_lines_are_read_the_way_dpkg_means_them(
        string line,
        PackageState state,
        string version)
    {
        var status = AptPackages.Parse(line);

        Assert.Equal(state, status.State);
        Assert.Equal(version, status.Version);
    }

    [Theory]
    [InlineData("E: Could not get lock /var/lib/dpkg/lock-frontend. It is held by process 812", AptFailure.Locked)]
    [InlineData("E: Unable to acquire the dpkg frontend lock (/var/lib/dpkg/lock-frontend), is another process using it?", AptFailure.Locked)]
    [InlineData("W: Failed to fetch http://deb.debian.org/debian/dists/trixie/InRelease  Temporary failure resolving 'deb.debian.org'", AptFailure.ArchiveUnreachable)]
    [InlineData("E: Unable to fetch some archives, maybe run apt-get update or try with --fix-missing?", AptFailure.ArchiveUnreachable)]
    [InlineData("W: Some index files failed to download. They have been ignored, or old ones used instead.", AptFailure.ArchiveUnreachable)]
    [InlineData("Err:1 http://deb.debian.org/debian trixie InRelease\n  Could not connect to deb.debian.org:80", AptFailure.ArchiveUnreachable)]
    [InlineData("E: Unable to locate package labwc", AptFailure.NotInArchive)]
    [InlineData("E: Package 'grim' has no installation candidate", AptFailure.NotInArchive)]
    [InlineData("E: Sub-process /usr/bin/dpkg returned an error code (1)", AptFailure.Other)]
    [InlineData("Setting up labwc (0.9.2-1+rpt4) ...", AptFailure.None)]
    public void Apts_own_error_text_is_classified_by_cause(string output, AptFailure expected)
    {
        var classified = AptPackages.Classify(output);

        // Classify reads the text and says nothing about exit codes, so a clean run classifies as
        // None; the caller is what turns an unrecognised non-zero exit into Other.
        Assert.Equal(expected is AptFailure.Other ? AptFailure.None : expected, classified);
    }

    [Fact]
    public void An_apt_log_is_reduced_to_the_lines_that_say_something()
    {
        const string Log =
            "Reading package lists...\n"
            + "Building dependency tree...\n"
            + "E: Unable to locate package labwc\n"
            + "E: Unable to locate package chromium\n";

        Assert.Equal(
            "E: Unable to locate package labwc · E: Unable to locate package chromium",
            AptPackages.Summarise(Log));
    }

    [Fact]
    public void A_refresh_that_only_warns_still_has_its_warning_carried()
    {
        // apt-get update exits zero after falling back to the index it already had, so W: is the
        // only evidence there is that the archive was unreachable.
        const string Log =
            "Hit:1 http://archive.raspberrypi.com/debian trixie InRelease\n"
            + "W: Failed to fetch http://deb.debian.org/debian/dists/trixie/InRelease  Temporary failure resolving 'deb.debian.org'\n"
            + "Reading package lists... Done\n";

        Assert.StartsWith("W: Failed to fetch", AptPackages.Summarise(Log), StringComparison.Ordinal);
    }

    [Fact]
    public void A_very_long_apt_log_is_truncated_rather_than_pasted_into_the_journal()
    {
        var log = "E: " + new string('x', 4000);

        Assert.True(AptPackages.Summarise(log).Length < 260);
        Assert.EndsWith("…", AptPackages.Summarise(log), StringComparison.Ordinal);
    }

    private static PackageResource Resource(FakeDebian debian, string package)
    {
        var apt = new AptPackages(debian);
        var spec = PackageCatalog.Specs.Single(item =>
            string.Equals(item.Package, package, StringComparison.Ordinal));

        return new PackageResource(apt, spec);
    }
}

/// <summary>
/// A Debian package system: a dpkg database, an apt index, an archive, and a network in front of
/// it.
/// </summary>
/// <remarks>
/// <para>
/// The couplings are the point. <c>apt-get install</c> writes into the same dictionary
/// <c>dpkg-query</c> reads, so a resource that converges here has genuinely changed the state its
/// own Observe consults rather than a flag the test set. Cutting the network makes
/// <c>apt-get update</c> warn and leaves the index it had, which is why a frame with an empty
/// index reports <c>Unable to locate package</c> — the exact sentence that makes an unreachable
/// archive look like a package that does not exist.
/// </para>
/// <para>
/// Every string apt produces here is apt's own wording, so the classifier is being tested against
/// the text it will actually meet.
/// </para>
/// </remarks>
internal sealed class FakeDebian : IProcessRunner
{
    /// <summary>Packages dpkg records as <c>installed</c>, by version.</summary>
    public Dictionary<string, string> Installed { get; } = new(StringComparer.Ordinal);

    /// <summary>Packages dpkg records as <c>config-files</c> — removed but not purged.</summary>
    public HashSet<string> Removed { get; } = new(StringComparer.Ordinal);

    /// <summary>Packages dpkg records as <c>half-configured</c>.</summary>
    public HashSet<string> Interrupted { get; } = new(StringComparer.Ordinal);

    /// <summary>What the archive offers, by version.</summary>
    public Dictionary<string, string> Archive { get; } = new(StringComparer.Ordinal);

    /// <summary>Packages whose .deb is already in the local cache, so no fetch is needed.</summary>
    public HashSet<string> Cached { get; } = new(StringComparer.Ordinal);

    /// <summary>What installing one package drags in with it.</summary>
    public Dictionary<string, string[]> Depends { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether the archive answers at all.</summary>
    public bool ArchiveReachable { get; set; } = true;

    /// <summary>Whether <c>/var/lib/apt/lists</c> holds anything usable.</summary>
    public bool HasPackageIndex { get; set; } = true;

    /// <summary>Which process holds the dpkg frontend lock, if any.</summary>
    public string? LockedBy { get; set; }

    /// <summary>Whether <c>dpkg-query</c> itself is broken.</summary>
    public bool DpkgBroken { get; set; }

    /// <summary>Every command line this system was asked to run.</summary>
    public List<string> Commands { get; } = [];

    /// <summary>
    /// A stock Raspberry Pi OS Lite image: nothing from the catalog installed, everything in it
    /// available, at the versions the v1 reference inventory recorded.
    /// </summary>
    public static FakeDebian StockImage()
    {
        var debian = new FakeDebian();

        // Versions read off reference/v1-state-inventory.txt, so the fixture is the frame that
        // defines parity rather than an invention. Nothing compares them; they are here so that a
        // version appearing in an observed value is a real one.
        var archive = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["labwc"] = "0.9.2-1+rpt4",
            ["chromium"] = "1:146.0.7680.164-1~deb13u1+rpt1",
            ["wireplumber"] = "0.5.8-2",
            ["pipewire-alsa"] = "1.4.2-1+rpt3",
            ["wlr-randr"] = "0.4.1-1",
            ["xdg-desktop-portal"] = "1.20.3+ds-1",
            ["xdg-desktop-portal-gtk"] = "1.15.3-1",
            ["gstreamer1.0-tools"] = "1.26.2-2",
            ["gstreamer1.0-plugins-base"] = "1.26.2-1+rpt3+deb13u1",
            ["gstreamer1.0-libcamera"] = "0.7.0+rpt20260205-1",
            ["gstreamer1.0-pipewire"] = "1.4.2-1+rpt3",
            ["dfu-util"] = "0.11-3",
            ["grim"] = "1.4.0+ds-2+b2",

            // Absent from the v1 reference — guide 12 step 6 was never applied to the frame that
            // defines parity (open question 9). The archive still offers it.
            ["unattended-upgrades"] = "2.13",

            // The one the catalog wants gone. Present in the archive, deliberately not installed.
            ["libspa-0.2-libcamera"] = "1.4.2-1+rpt3",
            ["rpi-chromium-mods"] = "20260211",
        };

        foreach (var entry in archive)
        {
            debian.Archive[entry.Key] = entry.Value;
        }

        return debian;
    }

    /// <summary>Puts several packages on the system, as an already-provisioned frame would have.</summary>
    public void InstallAll(IEnumerable<string> packages)
    {
        foreach (var package in packages)
        {
            Installed[package] = Archive.GetValueOrDefault(package, "1.0");
        }
    }

    /// <summary><c>apt-get remove</c> rather than <c>purge</c>: the <c>rc</c> state.</summary>
    public void RemoveWithoutPurging(string package)
    {
        Installed.Remove(package);
        Removed.Add(package);
    }

    public Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var line = executable + " " + string.Join(' ', arguments);
        Commands.Add(line);

        return Task.FromResult(executable switch
        {
            // Two shapes of the same tool, told apart the way dpkg-query itself tells them apart:
            // the whole-database form carries no package argument at all.
            AptPackages.DpkgQuery when arguments is [_, AptPackages.ListFormat] => List(),
            AptPackages.DpkgQuery => Query(arguments[^1]),
            AptPackages.Env => Apt([.. arguments.Skip(2)]),
            _ => new ProcessResult(127, string.Empty, $"{executable}: command not found"),
        });
    }

    /// <summary>
    /// The whole database, the way <c>dpkg-query -W</c> prints it with no package argument.
    /// </summary>
    /// <remarks>
    /// Deliberately prints the <c>rc</c> and half-configured entries too, with their versions,
    /// because those lines are the reason the reader filters on the status field rather than
    /// counting fields. A model that only printed installed packages could not catch that.
    /// </remarks>
    private ProcessResult List()
    {
        if (DpkgBroken)
        {
            return new ProcessResult(
                2,
                string.Empty,
                "dpkg-query: error: failed to open package info file '/var/lib/dpkg/status' for reading: Input/output error");
        }

        var lines = new List<string>();
        foreach (var entry in Installed)
        {
            lines.Add($"installed {entry.Key} {entry.Value}");
        }

        foreach (var package in Removed)
        {
            lines.Add($"config-files {package} {Archive.GetValueOrDefault(package, string.Empty)}".TrimEnd());
        }

        foreach (var package in Interrupted)
        {
            lines.Add($"half-configured {package} {Archive.GetValueOrDefault(package, string.Empty)}".TrimEnd());
        }

        return Ok(string.Join('\n', lines));
    }

    private ProcessResult Query(string package)
    {
        if (DpkgBroken)
        {
            return new ProcessResult(
                2,
                string.Empty,
                "dpkg-query: error: failed to open package info file '/var/lib/dpkg/status' for reading: Input/output error");
        }

        if (Installed.TryGetValue(package, out var version))
        {
            return Ok($"installed {version}");
        }

        if (Interrupted.Contains(package))
        {
            return Ok($"half-configured {Archive.GetValueOrDefault(package, string.Empty)}".TrimEnd());
        }

        if (Removed.Contains(package))
        {
            return Ok($"config-files {Archive.GetValueOrDefault(package, string.Empty)}".TrimEnd());
        }

        // dpkg-query exits 1 for a name this system has never seen, which is every one of these on
        // a stock image. The message is verbatim.
        return new ProcessResult(1, string.Empty, $"dpkg-query: no packages found matching {package}");
    }

    private ProcessResult Apt(IReadOnlyList<string> arguments) => arguments switch
    {
        ["update"] => Update(),
        ["install", "-y", var package] => Install(package),
        ["purge", "-y", var package] => Purge(package),
        _ => new ProcessResult(100, string.Empty, "E: Invalid operation " + string.Join(' ', arguments)),
    };

    private ProcessResult Update()
    {
        if (!ArchiveReachable)
        {
            // apt-get update exits zero and warns: it falls back to whatever index it already had.
            return Ok(
                "Err:1 http://deb.debian.org/debian trixie InRelease\n"
                + "  Temporary failure resolving 'deb.debian.org'\n"
                + "Reading package lists...\n"
                + "W: Failed to fetch http://deb.debian.org/debian/dists/trixie/InRelease  Temporary failure resolving 'deb.debian.org'\n"
                + "W: Some index files failed to download. They have been ignored, or old ones used instead.");
        }

        HasPackageIndex = true;
        return Ok("Hit:1 http://deb.debian.org/debian trixie InRelease\nReading package lists... Done");
    }

    private ProcessResult Install(string package)
    {
        if (LockedBy is { } holder)
        {
            return new ProcessResult(
                100,
                string.Empty,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"E: Could not get lock /var/lib/dpkg/lock-frontend. It is held by process 812 ({holder})\n"
                    + $"E: Unable to acquire the dpkg frontend lock (/var/lib/dpkg/lock-frontend), is another process using it?"));
        }

        if (!HasPackageIndex || !Archive.ContainsKey(package))
        {
            return new ProcessResult(100, string.Empty, $"E: Unable to locate package {package}");
        }

        if (!ArchiveReachable && !Cached.Contains(package))
        {
            return new ProcessResult(
                100,
                string.Empty,
                $"E: Failed to fetch http://deb.debian.org/debian/pool/main/{package}.deb  Temporary failure resolving 'deb.debian.org'\n"
                + "E: Unable to fetch some archives, maybe run apt-get update or try with --fix-missing?");
        }

        var settingUp = new List<string>();
        Apply(package, settingUp);

        return Ok(string.Join('\n', settingUp));
    }

    private void Apply(string package, List<string> settingUp)
    {
        var candidate = Archive.GetValueOrDefault(package, "1.0");

        // `apt-get install` on a package that is already there is an upgrade to the candidate, not
        // a no-op — which is the whole repair path for a package that somehow moved backward. A
        // model that returned early here would make that case look unfixable.
        if (Installed.TryGetValue(package, out var current)
            && DebianVersion.Compare(current, candidate) >= 0)
        {
            return;
        }

        foreach (var dependency in Depends.GetValueOrDefault(package, []))
        {
            Apply(dependency, settingUp);
        }

        Installed[package] = candidate;
        Removed.Remove(package);
        Interrupted.Remove(package);
        settingUp.Add($"Setting up {package} ({candidate}) ...");
    }

    private ProcessResult Purge(string package)
    {
        if (LockedBy is { } holder)
        {
            return new ProcessResult(
                100,
                string.Empty,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"E: Could not get lock /var/lib/dpkg/lock-frontend. It is held by process 812 ({holder})"));
        }

        var present = Installed.Remove(package) | Removed.Remove(package) | Interrupted.Remove(package);

        return Ok(present
            ? $"Removing {package} ...\nPurging configuration files for {package} ..."
            : $"Package '{package}' is not installed, so not removed");
    }

    private static ProcessResult Ok(string output) => new(0, output, string.Empty);
}
