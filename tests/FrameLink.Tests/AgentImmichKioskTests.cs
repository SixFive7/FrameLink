using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Kiosk;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;
using FrameLink.Agent.Supervise;

namespace FrameLink.Tests;

/// <summary>
/// Guide 9's block — <b>the one that takes Docker off the frame</b> (§2.1, decision 41).
/// </summary>
/// <remarks>
/// <para>
/// Immich Kiosk stays upstream: it is a mature product with a team behind it and v2 does not
/// reimplement it. What changes is the delivery — a pinned release, checksum-verified, supervised as
/// a child of the agent instead of a container under a Docker Engine whose corrupted network store
/// began the August 2026 incident chain. Removing Docker deletes that failure class rather than
/// repairing it, which is why <c>docker-selfheal</c> has nothing left to act on.
/// </para>
/// <para>
/// The install path is asserted against real archives built here rather than against a mock, for
/// the same reason the rest of the suite runs against <c>HostSystemFiles</c>: real gzip, real tar,
/// real SHA-256, real atomic rename. Only the network is a seam, and only because a test must not
/// reach it.
/// </para>
/// </remarks>
public sealed class AgentImmichKioskTests
{
    [Fact]
    public void The_pin_records_the_release_that_was_actually_reviewed()
    {
        var pin = KioskReleasePin.Current;

        // §7.1: an upstream artifact's version and checksum are reviewable facts, not memory. Every
        // one of these was measured on 2026-08-15 UTC against the bytes the URL served — the release
        // API answered v0.42.0, the published checksums file names the archive digest, and the
        // archive holds a single 18,546,850-byte `immich-kiosk` whose own Go build record reads
        // `-X main.version=0.42.0` with `GOOS=linux GOARCH=arm64 CGO_ENABLED=0`.
        Assert.Equal("0.42.0", pin.Version);
        Assert.Equal("v0.42.0", pin.Tag);
        Assert.Equal("immich-kiosk_Linux_arm64.tar.gz", pin.AssetFileName);
        Assert.Equal("93476535e86dd6914b1b8e644fdc147b4770903434f0db15d0ee469e0857e423", pin.ArchiveSha256);
        Assert.Equal(7_712_323, pin.ArchiveSizeBytes);
        Assert.Equal("162043f2ec65e72dae41c3b7885df4607951e1a69543a30b46d5a3dbb90ec81c", pin.BinarySha256);
        Assert.Equal(18_546_850, pin.BinarySizeBytes);
        Assert.Equal("immich-kiosk", pin.BinaryMemberName);

        // Both URLs name the tag, so a version bumped in one field and not the others is a test
        // failure rather than a frame fetching last month's release from this month's pin.
        Assert.Contains(pin.Tag, pin.ArchiveUrl.ToString(), StringComparison.Ordinal);
        Assert.Contains(pin.Version, pin.ChecksumsUrl.ToString(), StringComparison.Ordinal);
        Assert.Equal("https", pin.ArchiveUrl.Scheme, StringComparer.Ordinal);
    }

    [Fact]
    public void Nothing_in_this_repository_carries_the_release_it_fetches()
    {
        // Immich Kiosk is AGPL-3.0, and §2.1 fetches rather than redistributes precisely so the
        // source-offer obligation stays with the publisher — off this project and off every
        // self-hoster. A vendored binary would move it here, quietly, in a commit nobody read as a
        // licensing change. The pin names a URL; the bytes are never in the tree.
        //
        // Tracked files only, and the reason is not tidiness: a whole-tree enumeration walks .git,
        // node_modules and every bin/obj while a build may be writing into them, which throws when
        // a directory vanishes mid-walk. It also asked the wrong question — what must not happen is
        // that these bytes are *committed*, and an artifact somebody downloaded into their own
        // working copy is not that.
        var tracked = TrackedFiles();
        Assert.NotEmpty(tracked);

        Assert.DoesNotContain(
            tracked,
            path => Path.GetFileName(path) is "immich-kiosk"
                || (Path.GetFileName(path).StartsWith("immich-kiosk_", StringComparison.Ordinal)
                    && path.EndsWith(".tar.gz", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_well_formed_release_is_verified_unpacked_and_made_executable()
    {
        using var fixture = new KioskFixture();
        var release = KioskFixture.Archive(fixture.Payload);
        fixture.Serve(release.Bytes);

        var result = await fixture.Installer(release.Pin).InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(KioskInstallResult.Installed, result);
        Assert.Equal(fixture.Payload, await File.ReadAllBytesAsync(fixture.BinaryPath, TestContext.Current.CancellationToken));

        // The executable bit is applied to the staging file before the rename, so the target is
        // never briefly present and unstartable.
        Assert.Contains(
            fixture.Permissions.Applied,
            applied => applied.Path.EndsWith(KioskInstaller.BinaryStagingSuffix, StringComparison.Ordinal)
                && (applied.Mode & UnixFileMode.UserExecute) != 0);
    }

    [Fact]
    public async Task An_archive_that_does_not_match_the_published_checksum_is_refused_and_nothing_is_written()
    {
        using var fixture = new KioskFixture();
        var release = KioskFixture.Archive(fixture.Payload);

        // One byte different is a different release. What matters is not that the digest changed
        // but that the archive is refused *before* it is decompressed: a gzip stream from an
        // unverified source is a decompressor being driven by whoever answered the URL.
        fixture.Serve(release.Bytes);
        var pin = release.Pin with { ArchiveSha256 = new string('a', 64) };

        var result = await fixture.Installer(pin).InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(KioskInstallResult.ArchiveChecksumMismatch, result);
        Assert.False(File.Exists(fixture.BinaryPath));
        Assert.False(File.Exists(fixture.BinaryPath + KioskInstaller.ArchiveStagingSuffix));
        Assert.False(File.Exists(fixture.BinaryPath + KioskInstaller.BinaryStagingSuffix));
    }

    [Fact]
    public async Task An_archive_of_the_wrong_length_is_refused_before_it_is_hashed_against_anything()
    {
        using var fixture = new KioskFixture();
        var release = KioskFixture.Archive(fixture.Payload);
        fixture.Serve(release.Bytes);

        var result = await fixture
            .Installer(release.Pin with { ArchiveSizeBytes = release.Bytes.Length + 1 })
            .InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(KioskInstallResult.ArchiveSizeMismatch, result);
        Assert.False(File.Exists(fixture.BinaryPath));
    }

    [Fact]
    public async Task An_unreachable_upstream_is_an_outage_and_not_a_verification_failure()
    {
        // Different diagnoses with different fixes: one is somebody's router, the other is a
        // release that is not the release the pin names.
        using var fixture = new KioskFixture();
        var release = KioskFixture.Archive(fixture.Payload);

        var result = await fixture.Installer(release.Pin).InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(KioskInstallResult.Unreachable, result);
    }

    [Fact]
    public async Task An_archive_without_the_pinned_member_is_refused()
    {
        using var fixture = new KioskFixture();
        var release = KioskFixture.Archive(fixture.Payload, memberName: "something-else");
        fixture.Serve(release.Bytes);

        var result = await fixture
            .Installer(release.Pin with { BinaryMemberName = "immich-kiosk" })
            .InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(KioskInstallResult.ArchiveMalformed, result);
        Assert.False(File.Exists(fixture.BinaryPath));
    }

    [Fact]
    public async Task An_executable_whose_own_digest_is_wrong_never_reaches_the_target_path()
    {
        // The second digest earns its place here. The archive can be exactly the published bytes
        // and the file inside it still not be the file the pin describes — a re-cut release under
        // the same tag is precisely that — and this is the check that would catch it, on the file
        // the frame actually runs.
        using var fixture = new KioskFixture();
        var release = KioskFixture.Archive(fixture.Payload);
        fixture.Serve(release.Bytes);

        var result = await fixture
            .Installer(release.Pin with { BinarySha256 = new string('b', 64) })
            .InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(KioskInstallResult.BinaryChecksumMismatch, result);
        Assert.False(File.Exists(fixture.BinaryPath));
    }

    [Fact]
    public async Task An_install_that_is_already_in_place_fetches_nothing()
    {
        using var fixture = new KioskFixture();
        var release = KioskFixture.Archive(fixture.Payload);
        fixture.Serve(release.Bytes);
        var installer = fixture.Installer(release.Pin);

        Assert.Equal(KioskInstallResult.Installed, await installer.InstallAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Download.Opens);

        Assert.Equal(
            KioskInstallResult.AlreadyInstalled,
            await installer.InstallAsync(TestContext.Current.CancellationToken));

        // §2.4 reboots the frame once per resource, so this Observe runs on every boot of a green
        // frame. Re-fetching 7.4 MB each time would be a fleet re-downloading a release it already
        // has, for ever.
        Assert.Equal(1, fixture.Download.Opens);
    }

    [Fact]
    public async Task The_binary_resource_reads_the_file_rather_than_a_note_saying_it_installed()
    {
        using var fixture = new KioskFixture();
        var release = KioskFixture.Archive(fixture.Payload);
        fixture.Serve(release.Bytes);
        var installer = fixture.Installer(release.Pin);
        var resource = new KioskBinaryResource(installer);

        var before = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(before.InSync);
        Assert.Contains("nothing at", before.Observed, StringComparison.Ordinal);

        await resource.ActAsync(TestContext.Current.CancellationToken);
        var after = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.True(after.InSync);

        // "Applied" is never claimed from a successful write (§2.4). Take the file away and the
        // very next Observe says so, because the observation is a hash of the real file.
        File.Delete(fixture.BinaryPath);
        Assert.False((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);
    }

    [Fact]
    public void The_child_runs_on_the_environment_variables_upstream_actually_reads()
    {
        // Not guessed: all five appear verbatim in the v0.42.0 executable's string table, and the
        // first four are the ones guide 9's Compose file set. KIOSK_PORT replaces the `ports:`
        // line, which is the one setting that did not survive as an environment variable because
        // Docker was performing it rather than Kiosk.
        var settings = new KioskProcessSettings
        {
            WorkingDirectory = "/var/lib/fl-agent/kiosk",
            ImmichUrl = "https://immich.example.invalid",
            ImmichApiKey = "a-key",
            OfflineModeEnabled = true,
            OfflineAssetCount = 200,
            Port = 3000,
        };

        Assert.Equal(
            ["KIOSK_IMMICH_URL", "KIOSK_IMMICH_API_KEY", "KIOSK_OFFLINE_MODE_ENABLED", "KIOSK_OFFLINE_MODE_NUMBER_OF_ASSETS", "KIOSK_PORT"],
            settings.Environment.Select(pair => pair.Key));

        Assert.Equal("true", settings.Environment.Single(pair => pair.Key == "KIOSK_OFFLINE_MODE_ENABLED").Value);
        Assert.Equal("200", settings.Environment.Single(pair => pair.Key == "KIOSK_OFFLINE_MODE_NUMBER_OF_ASSETS").Value);
        Assert.Equal("3000", settings.Environment.Single(pair => pair.Key == "KIOSK_PORT").Value);
    }

    [Fact]
    public void The_api_key_is_never_written_down_anywhere_a_person_or_a_server_can_read_it()
    {
        var settings = new KioskProcessSettings
        {
            WorkingDirectory = "/var/lib/fl-agent/kiosk",
            ImmichUrl = "https://immich.example.invalid",
            ImmichApiKey = "the-actual-secret-key",
        };

        // Describe() is what reaches the journal on every launch.
        Assert.DoesNotContain("the-actual-secret-key", settings.Describe(), StringComparison.Ordinal);
        Assert.Contains("KIOSK_IMMICH_API_KEY=<set>", settings.Describe(), StringComparison.Ordinal);

        // And a fingerprint is what reaches a delta: enough to tell two keys apart, never enough to
        // use one.
        var fingerprint = SecretFingerprint.Of("the-actual-secret-key");
        Assert.DoesNotContain("the-actual-secret-key", fingerprint, StringComparison.Ordinal);
        Assert.StartsWith("a key, sha256:", fingerprint, StringComparison.Ordinal);
        Assert.Equal(fingerprint, SecretFingerprint.Of("the-actual-secret-key"));
        Assert.NotEqual(fingerprint, SecretFingerprint.Of("a different key"));
        Assert.Equal("no key", SecretFingerprint.Of(string.Empty));
    }

    [Fact]
    public void A_frame_that_has_not_been_adopted_has_nothing_to_start()
    {
        // §3.3 gives a pending device nothing — no configuration, no token — and an API key is a
        // token. Starting Kiosk anyway would produce a process answering 401 for ever and a
        // relaunch loop restarting it, which is noise standing where "adopt me" should be.
        var pending = new KioskProcessSettings { WorkingDirectory = "/var/lib/fl-agent/kiosk" };
        Assert.False(pending.IsComplete);

        Assert.False((pending with { ImmichUrl = "https://immich.example.invalid" }).IsComplete);
        Assert.False((pending with { ImmichApiKey = "a-key" }).IsComplete);
        Assert.True((pending with { ImmichUrl = "https://immich.example.invalid", ImmichApiKey = "a-key" }).IsComplete);
    }

    [Fact]
    public void The_child_is_configured_from_what_the_reconciler_recorded_not_from_the_last_push()
    {
        using var store = new TemporaryStore();

        store.Store.WriteText("kiosk.immich-url", "https://immich.example.invalid");
        store.Store.WriteSecretAtomic("kiosk.immich-api-key", Encoding.UTF8.GetBytes("a-key"));
        store.Store.WriteText("kiosk.offline-mode", "true");
        store.Store.WriteText("kiosk.offline-asset-count", "150");

        var settings = KioskCatalog.SettingsFrom(store.Store, "/var/lib/fl-agent/kiosk");

        Assert.Equal("https://immich.example.invalid", settings.ImmichUrl);
        Assert.Equal("a-key", settings.ImmichApiKey);
        Assert.True(settings.OfflineModeEnabled);
        Assert.Equal(150, settings.OfflineAssetCount);
        Assert.Equal(KioskProcess.DefaultPort, settings.Port);
    }

    [Fact]
    public void A_recorded_asset_count_that_is_nonsense_falls_back_rather_than_disabling_the_cache()
    {
        using var store = new TemporaryStore();
        store.Store.WriteText("kiosk.offline-asset-count", "not-a-number");

        // Guide 9's measured value. A typo must not silently become zero, because zero is a cache
        // that never fills and a frame that goes blank the day Immich is unreachable — which §2.6
        // says must never happen in someone else's house.
        Assert.Equal(200, KioskCatalog.SettingsFrom(store.Store, "/tmp/kiosk").OfflineAssetCount);
        Assert.True(KioskCatalog.SettingsFrom(store.Store, "/tmp/kiosk").OfflineModeEnabled);
    }

    [Fact]
    public void The_running_childs_own_environment_is_the_cross_check_the_catalog_asks_for()
    {
        var block = KioskChildEnvironment.Read(
            new MemorySystemFiles { ["/proc/4242/environ"] = "KIOSK_IMMICH_URL=https://a.invalid\0KIOSK_PORT=3000\0" },
            4242);

        Assert.NotNull(block);
        Assert.Equal("https://a.invalid", block["KIOSK_IMMICH_URL"]);
        Assert.Equal("3000", block["KIOSK_PORT"]);

        // No child is not a disagreement. A slideshow that has not started yet says nothing about
        // whether the recorded value is right, and failing on its silence would make all four
        // kiosk.config.* resources unfixable on exactly the frame that needs them.
        Assert.Null(KioskChildEnvironment.Read(new MemorySystemFiles(), null));
        Assert.Null(KioskChildEnvironment.Read(new MemorySystemFiles(), 4242));
    }

    [Fact]
    public void The_listen_reading_ties_a_socket_to_the_process_that_owns_it()
    {
        // The whole point of `ss -tlnp` over a bare connect: a port answering is not evidence that
        // *this* child is what is answering it.
        const string Output = """
            State  Recv-Q Send-Q Local Address:Port Peer Address:Port Process
            LISTEN 0      4096         127.0.0.1:8888      0.0.0.0:*     users:(("fl-agent",pid=900,fd=12))
            LISTEN 0      4096                 *:3000            *:*     users:(("immich-kiosk",pid=4242,fd=7))
            """;

        Assert.Equal(["*:3000"], ListenSockets.OwnedBy(Output, 4242, 3000));
        Assert.Empty(ListenSockets.OwnedBy(Output, 900, 3000));
        Assert.Equal(["127.0.0.1:8888"], ListenSockets.OwnedBy(Output, 900, 8888));
    }

    [Fact]
    public void Upstream_binds_every_interface_and_that_is_recorded_rather_than_papered_over()
    {
        // ⚠ A catalog-versus-upstream contradiction, pinned here so it cannot quietly stop being
        // true or quietly be forgotten. The catalog's `kiosk.listen-address` says "listening on
        // 127.0.0.1:3000 and nowhere else", and that property was Docker's port publishing rather
        // than Kiosk's: upstream's main.go at v0.42.0 starts its server with
        // `Address: fmt.Sprintf(":%v", baseConfig.Kiosk.Port)` and its config struct has a `port`
        // field and no host or bind field at all. So the resource asserts what is inside its reach
        // — the port, and that loopback reaches it — and writes the real bind set into the
        // observation every pass, in sync or not, because §1.2 principle 3 forbids a silence here.
        const string Wildcard = """
            LISTEN 0 4096 *:3000 *:* users:(("immich-kiosk",pid=4242,fd=7))
            """;

        Assert.Equal(["*:3000"], ListenSockets.OwnedBy(Wildcard, 4242, 3000));

        var documentation = File.ReadAllText(Path.Combine(
            GuiFreshnessTests.RepositoryRoot(),
            "src",
            "FrameLink.Agent",
            "Resources",
            "KioskResources.cs"));

        Assert.Contains("not achievable against Immich Kiosk", documentation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_relaunch_is_an_excused_transient_and_not_drift()
    {
        // §2.10's collision, reproduced exactly on a second supervised thing: kiosk.process.
        // supervised observes "alive and answering", a pass landing between an exit and the
        // relaunch sees it down, and §2.6 would then stop the product and reboot a frame whose
        // only fault was a process blinking.
        using var store = new TemporaryStore();
        var clock = new ManualClock();
        var interlock = new SupervisionInterlock();

        var kiosk = new KioskProcess(new KioskProcessServices
        {
            Store = store.Store,
            Clock = clock,
            Log = new RecordingLog(),
            Interlock = interlock,
            RecoveryDeadline = () => TimeSpan.FromMinutes(2),
            Settings = () => new KioskProcessSettings
            {
                WorkingDirectory = store.Root,
                ImmichUrl = "https://immich.example.invalid",
                ImmichApiKey = "a-key",
            },
        });

        // No binary, so the launch cannot take — which is the state a relaunch loop spends its
        // whole life in when something is wrong, and the state the window has to cover.
        Assert.False(kiosk.Start());
        kiosk.SuperviseOnce();
        kiosk.SuperviseOnce();

        Assert.Equal(
            [KioskSupervisedResource.ResourceName, KioskListenAddressResource.ResourceName],
            KioskProcess.DisturbedResources);

        // The unit-file analogues are absent from that list because there are none: this child has
        // no unit, which is the structural difference from the browser.
        Assert.DoesNotContain(
            KioskProcess.DisturbedResources,
            name => name.StartsWith("unit.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_relaunch_window_excuses_only_the_two_resources_it_disturbs()
    {
        var interlock = new SupervisionInterlock();
        var now = new DateTimeOffset(2026, 8, 16, 3, 0, 0, TimeSpan.Zero);

        interlock.Open(
            KioskProcess.SupervisionBehaviour,
            KioskProcess.DisturbedResources,
            now,
            TimeSpan.FromMinutes(2));

        Assert.True(interlock.Excuses(KioskSupervisedResource.ResourceName, now));
        Assert.True(interlock.Excuses(KioskListenAddressResource.ResourceName, now));

        // The binary and the four settings are untouched by a relaunch and must keep being checked
        // throughout — a window that excused them too would be a hole in drift detection rather
        // than an interlock.
        Assert.False(interlock.Excuses(KioskBinaryResource.ResourceName, now));
        Assert.False(interlock.Excuses("kiosk.config.immich-api-key", now));

        // And the deadline is the boundary, not a safety valve: a child that has not come back
        // stops being a transient and becomes ordinary drift (§2.10 clause 3).
        Assert.False(interlock.Excuses(KioskSupervisedResource.ResourceName, now + TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public async Task The_offline_cache_resource_makes_a_directory_and_proves_it_can_be_written()
    {
        using var files = new TemporaryFiles();
        using var store = new TemporaryStore();

        var kiosk = new KioskProcess(new KioskProcessServices
        {
            Store = store.Store,
            Clock = new ManualClock(),
            Log = new RecordingLog(),
            Settings = () => new KioskProcessSettings { WorkingDirectory = store.Root },
        });

        var resource = new KioskOfflineCacheResource(files.Files, kiosk);

        var before = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(before.InSync);
        Assert.Contains("not there", before.Observed, StringComparison.Ordinal);

        await resource.ActAsync(TestContext.Current.CancellationToken);

        var after = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.True(after.InSync);
        Assert.Contains("took a test write", after.Observed, StringComparison.Ordinal);

        // The probe leaves nothing behind. A cache directory that slowly fills with probe files
        // would be this resource paying for its own assertion out of the SD card.
        Assert.False(files.Files.FileExists(Path.Combine(kiosk.OfflineCachePath, KioskOfflineCacheResource.ProbeName)));

        // And 65532 is nowhere near any of it: that uid is the container's non-root user, a Docker
        // artifact, and transcribing it onto a frame with no container leaves a directory the child
        // cannot write and a cache that silently never fills.
        Assert.DoesNotContain("65532", after.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void The_block_is_the_catalogs_eight_with_the_catalogs_dependencies()
    {
        using var files = new TemporaryFiles();
        var graph = DeviceCatalog.BuildGraph(AgentResourceGraphTests.Context(files));

        string[] block =
        [
            KioskBinaryResource.ResourceName,
            KioskOfflineCacheResource.ResourceName,
            "kiosk.config.immich-url",
            "kiosk.config.immich-api-key",
            "kiosk.config.offline-mode-enabled",
            "kiosk.config.offline-asset-count",
            KioskListenAddressResource.ResourceName,
            KioskSupervisedResource.ResourceName,
        ];

        Assert.All(block, name => Assert.NotNull(graph.Find(name)));
        Assert.Equal(8, block.Length);

        // The dependsOn rule, and the catalog's own statement of why: "the test is not 'is there a
        // fleet setting' but 'would this resource have to guess'." The address of somebody's photo
        // server and the key that reads it are values this project cannot hold, and §3.3 means the
        // key literally — a pending device receives no token, and an API key is one.
        Assert.Contains(AdoptionResource.ResourceName, graph.Find("kiosk.config.immich-url")!.DependsOn);
        Assert.Contains(AdoptionResource.ResourceName, graph.Find("kiosk.config.immich-api-key")!.DependsOn);

        // And offline mode and its count are not gated, because their catalog defaults are right on
        // an unadopted frame; a later override is ordinary drift.
        Assert.DoesNotContain(
            AdoptionResource.ResourceName,
            graph.Find("kiosk.config.offline-mode-enabled")!.DependsOn);
        Assert.DoesNotContain(
            AdoptionResource.ResourceName,
            graph.Find("kiosk.config.offline-asset-count")!.DependsOn);

        // Nor is the fetch. The binary is a value the catalog fixes, so a pending frame installs it
        // — which is what lets the whole block converge the moment adoption issues the two values
        // it cannot invent.
        Assert.Empty(graph.Find(KioskBinaryResource.ResourceName)!.DependsOn);
    }

    [Fact]
    public void The_block_converges_in_the_order_the_catalog_schedules_it()
    {
        using var files = new TemporaryFiles();
        var order = DeviceCatalog.BuildGraph(AgentResourceGraphTests.Context(files))
            .Ordered
            .Select(resource => resource.Name)
            .ToList();

        // Nothing before the binary, because there is nothing to configure until it is there.
        Assert.True(order.IndexOf(KioskBinaryResource.ResourceName) < order.IndexOf(KioskOfflineCacheResource.ResourceName));
        Assert.True(order.IndexOf(KioskBinaryResource.ResourceName) < order.IndexOf("kiosk.config.immich-url"));

        // The count follows the switch it modifies.
        Assert.True(order.IndexOf("kiosk.config.offline-mode-enabled") < order.IndexOf("kiosk.config.offline-asset-count"));

        // And the process comes last of the block, after the address it serves on and the two
        // values without which it can only answer 401.
        Assert.True(order.IndexOf(KioskListenAddressResource.ResourceName) < order.IndexOf(KioskSupervisedResource.ResourceName));
        Assert.True(order.IndexOf("kiosk.config.immich-api-key") < order.IndexOf(KioskSupervisedResource.ResourceName));

        // The slideshow URL names the address, so it cannot be applied before the address exists.
        Assert.True(order.IndexOf(KioskListenAddressResource.ResourceName) < order.IndexOf("app.config.immich-kiosk-url"));
    }

    /// <summary>Every path git is tracking, relative to the repository root.</summary>
    private static IReadOnlyList<string> TrackedFiles()
    {
        using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = GuiFreshnessTests.RepositoryRoot(),
            RedirectStandardOutput = true,
            UseShellExecute = false,
            ArgumentList = { "ls-files" },
        });

        Assert.NotNull(git);

        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();

        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim())];
    }

    [Fact]
    public void The_slideshow_url_and_the_listen_address_name_the_same_port()
    {
        // Two places, one number, and no setting between them — a settable port would be a value
        // that has to agree with itself across a reboot.
        Assert.Contains(
            $"127.0.0.1:{KioskProcess.DefaultPort}",
            AppConfigCatalog.SlideshowBase,
            StringComparison.Ordinal);

        // The serve half of the offline pair. kiosk.config.offline-mode-enabled makes Kiosk
        // download and cache; this makes it serve from that cache. Either alone leaves the frame
        // blank when Immich is unreachable.
        Assert.Contains("use_offline_mode=true", AppConfigCatalog.SlideshowBase, StringComparison.Ordinal);
    }
}

/// <summary>A real filesystem, a real archive and a download that never leaves the machine.</summary>
internal sealed class KioskFixture : IDisposable
{
    public KioskFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "fl-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        // A payload with a shape rather than a length: enough bytes to cross the installer's 64 kB
        // copy buffer, so the streaming path is the path under test.
        Payload = new byte[200_000];
        for (var index = 0; index < Payload.Length; index++)
        {
            Payload[index] = (byte)(index % 251);
        }
    }

    public string Root { get; }

    public byte[] Payload { get; }

    public string BinaryPath => Path.Combine(Root, "immich-kiosk");

    public RecordingPermissions Permissions { get; } = new();

    public StubKioskDownload Download { get; } = new();

    public void Serve(byte[] archive) => Download.Payload = archive;

    public KioskInstaller Installer(KioskReleasePin pin) =>
        new(BinaryPath, Download, Permissions, new RecordingLog(), pin);

    /// <summary>Builds a real gzip'd tar carrying the three members upstream ships.</summary>
    public static (byte[] Bytes, KioskReleasePin Pin) Archive(byte[] payload, string memberName = "immich-kiosk")
    {
        ArgumentNullException.ThrowIfNull(payload);

        using var buffer = new MemoryStream();

        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            // LICENSE and README.md travel in the real archive and must be stepped over rather than
            // tripped on, so they are here too.
            writer.WriteEntry(Entry("LICENSE", "GNU AFFERO GENERAL PUBLIC LICENSE"u8.ToArray()));
            writer.WriteEntry(Entry("README.md", "# Immich Kiosk"u8.ToArray()));
            writer.WriteEntry(Entry(memberName, payload));
        }

        var bytes = buffer.ToArray();

        return (bytes, new KioskReleasePin
        {
            Version = "0.0.0-test",
            AssetFileName = "immich-kiosk_Linux_arm64.tar.gz",
            ArchiveUrl = new Uri("https://example.invalid/immich-kiosk_Linux_arm64.tar.gz"),
            ChecksumsUrl = new Uri("https://example.invalid/checksums.txt"),
            ArchiveSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            ArchiveSizeBytes = bytes.Length,
            BinaryMemberName = memberName,
            BinarySha256 = Convert.ToHexStringLower(SHA256.HashData(payload)),
            BinarySizeBytes = payload.Length,
            ReviewedUtc = new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero),
        });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private static PaxTarEntry Entry(string name, byte[] content)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(content),
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
        };

        return entry;
    }
}

/// <summary>
/// A filesystem held in a dictionary, for the paths a real one cannot produce.
/// </summary>
/// <remarks>
/// The rest of the suite runs its file resources against <see cref="HostSystemFiles"/> on purpose.
/// This exists for <c>/proc/&lt;pid&gt;/environ</c> alone, which is a kernel-synthesised file whose
/// contents are a NUL-separated block that no test can make a real filesystem produce.
/// </remarks>
internal sealed class MemorySystemFiles : ISystemFiles
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    public string this[string path]
    {
        set => _files[path] = value;
    }

    public bool FileExists(string path) => _files.ContainsKey(path);

    public bool DirectoryExists(string path) => false;

    public string? ReadText(string path) => _files.GetValueOrDefault(path);

    public void WriteText(string path, string content) => _files[path] = content;

    public void DeleteFile(string path) => _files.Remove(path);

    public void EnsureDirectory(string path)
    {
        // Nothing to create in a dictionary.
    }

    public IReadOnlyList<string> ListDirectories(string path) => [];

    public IReadOnlyList<string> ListFiles(string path) => [];

    public UnixFileMode? ModeOf(string path) => null;

    public void SetMode(string path, UnixFileMode mode)
    {
        // No modes in a dictionary.
    }
}

/// <summary>Answers with bytes held in memory, and counts how often it is asked.</summary>
internal sealed class StubKioskDownload : IKioskDownload
{
    public byte[]? Payload { get; set; }

    public int Opens { get; private set; }

    public Task<Stream?> OpenAsync(Uri url, CancellationToken cancellationToken)
    {
        Opens++;
        return Task.FromResult<Stream?>(Payload is null ? null : new MemoryStream(Payload));
    }
}
