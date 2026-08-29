using FrameLink.Agent.Hosting;
using FrameLink.Agent.Identity;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;

namespace FrameLink.Tests;

/// <summary>
/// Guide 12's four hardening resources, and the four cross-guide ones that finish the catalog.
/// </summary>
/// <remarks>
/// <para>
/// Two of these are asserted against <c>reference/v1-state-inventory.txt</c> rather than against
/// a fixture somebody invented: the <c>/tmp</c> mount line and the EEPROM configuration are both
/// captured verbatim from the frame that defines parity, so what the tests prove is agreement with
/// a real machine rather than with each other.
/// </para>
/// <para>
/// The <c>/tmp</c> one is the sharper of the two, and it is the catalog's named <b>parity trap</b>.
/// Guide 12's own command falls back to <c>size=100M</c>, and the v1 frame shows <c>1029504k</c> —
/// systemd's default of half of RAM — which means that fallback never fired. Both satisfy "is
/// <c>/tmp</c> a tmpfs?", and one of them gives Chromium's entire working profile a tenth of the
/// room it has today.
/// </para>
/// </remarks>
public sealed class AgentReliabilityTests
{
    /// <summary>Verbatim from the v1 inventory's <c>GOVERNOR_ZRAM_TMPFS</c> block.</summary>
    private const string V1TmpMount = "/tmp tmpfs tmpfs rw,nosuid,nodev,size=1029504k,nr_inodes=1048576";

    /// <summary>Guide 12 step 5's <c>/etc/fstab</c> fallback, as a <c>findmnt</c> row.</summary>
    private const string FallbackTmpMount = "/tmp tmpfs tmpfs rw,noatime,size=102400k";

    private static readonly string[] FindmntTmp = ["-n", "-t", "tmpfs", "/tmp"];

    [Fact]
    public async Task The_v1_tmp_mount_is_in_sync_and_guide_12s_own_fallback_is_not()
    {
        var processes = new RecordingProcessRunner();
        var control = new RecordingSystemControl();
        using var files = new TemporaryFiles();
        var resource = new TmpfsMountResource(files.Files, processes, control);

        processes.Answers["findmnt " + string.Join(' ', FindmntTmp)] = new ProcessResult(0, V1TmpMount, string.Empty);
        var parity = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.True(parity.InSync);
        Assert.Contains("1005 MB", parity.Observed, StringComparison.Ordinal);

        // The trap. Same predicate — /tmp is a tmpfs — and a tenth of the room.
        processes.Answers["findmnt " + string.Join(' ', FindmntTmp)] = new ProcessResult(0, FallbackTmpMount, string.Empty);
        var trapped = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(trapped.InSync);
        Assert.Contains("100 MB", trapped.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tmp_that_is_not_a_tmpfs_at_all_is_repaired_with_a_drop_in_and_never_with_fstab()
    {
        var processes = new RecordingProcessRunner();
        var control = new RecordingSystemControl();
        using var files = new TemporaryFiles();
        var resource = new TmpfsMountResource(files.Files, processes, control);

        // `findmnt -t tmpfs` exits non-zero and prints nothing when the filter matches nothing.
        processes.Answers["findmnt " + string.Join(' ', FindmntTmp)] = new ProcessResult(1, string.Empty, string.Empty);

        var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(observation.InSync);

        var action = await resource.ActAsync(TestContext.Current.CancellationToken);

        // The catalog asks for a systemd drop-in "over an /etc/fstab line that competes with the
        // fstab generator", and two owners for one mount point is exactly what that avoids.
        Assert.Equal(TmpfsMountResource.DesiredContent(), files.Read(TmpfsMountResource.DropInPath));
        Assert.Null(files.Read("/etc/fstab"));
        Assert.Contains("size=50%", action.Change, StringComparison.Ordinal);
        Assert.Contains("daemon-reload", control.Commands);
        Assert.Contains("enable tmp.mount", control.Commands);
    }

    [Fact]
    public void A_tmpfs_with_no_stated_size_is_the_kernel_default_and_not_a_fault()
    {
        // Absence of `size=` means the kernel applies half of RAM, which is the good case. A
        // reader that treated "no number" as zero would report drift on a correct frame.
        Assert.Null(TmpfsMountResource.SizeKbOf("/tmp tmpfs tmpfs rw,nosuid,nodev"));
        Assert.Equal(1_029_504, TmpfsMountResource.SizeKbOf(V1TmpMount));
        Assert.Equal(512 * 1024, TmpfsMountResource.SizeKbOf("/tmp tmpfs tmpfs rw,size=512m"));
        Assert.Null(TmpfsMountResource.SizeKbOf("/tmp tmpfs tmpfs rw,size=50%"));
    }

    [Fact]
    public async Task Swap_on_a_file_is_drift_and_swap_on_zram_is_not()
    {
        var processes = new RecordingProcessRunner();
        var control = new RecordingSystemControl();
        var resource = new NoFileSwapResource(processes, control);

        control.Answer("is-enabled dphys-swapfile", "not-found", succeeded: false);
        processes.Answers["swapon --show"] = new ProcessResult(
            0,
            "NAME       TYPE      SIZE USED PRIO\n/dev/zram0 partition 512M   0B  100",
            string.Empty);

        Assert.True((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);

        processes.Answers["swapon --show"] = new ProcessResult(
            0,
            "NAME       TYPE      SIZE USED PRIO\n/dev/zram0 partition 512M   0B  100\n/var/swap  file      512M   4M   -2",
            string.Empty);

        var drifted = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(drifted.InSync);
        Assert.Contains("/var/swap", drifted.Observed, StringComparison.Ordinal);

        await resource.ActAsync(TestContext.Current.CancellationToken);
        Assert.Contains("swapoff /var/swap", processes.Commands);
        Assert.Contains("disable --now dphys-swapfile", control.Commands);
    }

    [Fact]
    public async Task An_enabled_dphys_swapfile_is_drift_even_when_nothing_is_swapping_yet()
    {
        var processes = new RecordingProcessRunner();
        var control = new RecordingSystemControl();
        var resource = new NoFileSwapResource(processes, control);

        // It is not swapping *now*; it is armed to at the next boot, which is the state §2.4's
        // reboot discipline exists to make visible.
        control.Answer("is-enabled dphys-swapfile", "enabled");
        processes.Answers["swapon --show"] = new ProcessResult(0, string.Empty, string.Empty);

        var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(observation.InSync);
        Assert.Contains("dphys-swapfile is enabled", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void The_negative_swap_assertion_waits_for_the_positive_one()
    {
        // Asserting that nothing swaps onto the card is only meaningful once something else is
        // providing swap. The catalog gives this edge and the graph enforces the order.
        Assert.Equal(
            [SwapZramResource.ResourceName],
            new NoFileSwapResource(new RecordingProcessRunner(), new RecordingSystemControl()).DependsOn);
    }

    [Fact]
    public async Task The_apt_periodic_switches_are_read_from_the_merged_configuration()
    {
        var processes = new RecordingProcessRunner();
        using var files = new TemporaryFiles();
        var resource = new AptAutoUpgradesResource(files.Files, processes, FleetValues.None);

        processes.Answers["apt-config dump"] = new ProcessResult(
            0,
            "APT::Periodic::Update-Package-Lists \"1\";\nAPT::Periodic::Unattended-Upgrade \"1\";",
            string.Empty);

        Assert.True((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);

        // One switch off is the half-applied state, and it is drift rather than a pass: the lists
        // refresh and nothing installs.
        processes.Answers["apt-config dump"] = new ProcessResult(
            0,
            "APT::Periodic::Update-Package-Lists \"1\";\nAPT::Periodic::Unattended-Upgrade \"0\";",
            string.Empty);

        Assert.False((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);

        await resource.ActAsync(TestContext.Current.CancellationToken);
        var written = files.Read(AptAutoUpgradesResource.ConfigPath);
        Assert.Contains("APT::Periodic::Update-Package-Lists \"1\";", written, StringComparison.Ordinal);
        Assert.Contains("APT::Periodic::Unattended-Upgrade \"1\";", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Switching_security_updates_off_leaves_the_package_installed_with_both_switches_at_zero()
    {
        var processes = new RecordingProcessRunner();
        using var files = new TemporaryFiles();
        var values = FleetValues.From(new Dictionary<string, string> { [AptAutoUpgradesResource.SettingKey] = "false" });
        var resource = new AptAutoUpgradesResource(files.Files, processes, values);

        // The catalog states this shape outright. Making the *package* the toggle would mean a
        // purge-and-reinstall — an apt transaction and, under §2.4, a reboot — to change two
        // characters, and it would collapse two different faults into one resource.
        Assert.False(resource.Enabled);
        Assert.Equal([PackageResource.Prefix + "unattended-upgrades"], resource.DependsOn);

        processes.Answers["apt-config dump"] = new ProcessResult(
            0,
            "APT::Periodic::Update-Package-Lists \"1\";\nAPT::Periodic::Unattended-Upgrade \"1\";",
            string.Empty);

        Assert.False((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);

        await resource.ActAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"0\";", files.Read(AptAutoUpgradesResource.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_origins_policy_is_a_name_the_catalog_knows_and_never_a_string_the_server_supplies()
    {
        var processes = new RecordingProcessRunner();
        using var files = new TemporaryFiles();

        // §2.2: the Fleet Manager supplies values, never logic. An origins pattern is a rule apt
        // executes as root against the archive, so the setting selects from a compiled-in set and
        // anything unrecognised falls back to the narrow end rather than to nothing.
        var nonsense = FleetValues.From(new Dictionary<string, string>
        {
            [UnattendedUpgradesPolicyResource.SettingKey] = "origin=Anything,label=Whatever",
        });

        var resource = new UnattendedUpgradesPolicyResource(files.Files, processes, nonsense);
        Assert.Equal(UnattendedUpgradesPolicyResource.SecurityOnly, resource.Policy);

        // The real shape of `apt-config dump` for a list: the parent node is printed with an empty
        // value and the members follow it, each spelled with a bare trailing `::`. A parser that
        // counted the parent would see one member too many on every correct frame.
        var dump = new List<string> { $"{UnattendedUpgradesPolicyResource.OriginsKey} \"\";" };
        dump.AddRange(resource.DesiredPatterns.Select(pattern =>
            $"{UnattendedUpgradesPolicyResource.OriginsKey}:: \"{pattern}\";"));

        processes.Answers["apt-config dump"] = new ProcessResult(0, string.Join('\n', dump), string.Empty);

        Assert.True((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);
        Assert.Equal(2, AptConfig.List(string.Join('\n', dump), UnattendedUpgradesPolicyResource.OriginsKey).Count);
    }

    [Fact]
    public async Task A_wider_origins_list_than_the_policy_allows_is_drift_and_the_file_clears_before_it_declares()
    {
        var processes = new RecordingProcessRunner();
        using var files = new TemporaryFiles();
        var resource = new UnattendedUpgradesPolicyResource(files.Files, processes, FleetValues.None);

        processes.Answers["apt-config dump"] = new ProcessResult(
            0,
            string.Join(
                '\n',
                resource.DesiredPatterns
                    .Append("origin=Debian,codename=${distro_codename}-updates")
                    .Select(pattern => $"{UnattendedUpgradesPolicyResource.OriginsKey}:: \"{pattern}\";")),
            string.Empty);

        Assert.False((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);

        await resource.ActAsync(TestContext.Current.CancellationToken);
        var written = files.Read(UnattendedUpgradesPolicyResource.ConfigPath);

        // Without the clear, apt appends and the result is the *union* with Debian's own
        // 50unattended-upgrades — the opposite of restricting anything. The file name sorts after
        // 50, so the clear happens second.
        Assert.StartsWith("#clear " + UnattendedUpgradesPolicyResource.OriginsKey, written, StringComparison.Ordinal);
        Assert.Contains("label=Debian-Security", written, StringComparison.Ordinal);
        Assert.DoesNotContain("-updates", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Both_apt_timers_have_to_be_enabled_and_active_before_the_frame_is_taking_security_updates()
    {
        // The gap `reference/outside-the-dag-review.md` item 39 names: the two resources above
        // reconcile apt's configuration, and a frame whose timers had been switched off satisfied
        // both of them while never installing another fix. All four of the states below are frames
        // that report a perfect apt configuration.
        var systemd = new FakeTimerUnits();
        var resource = new AptDailyTimersResource(systemd);

        Assert.True((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);

        // One of the two is enough to stop the frame receiving updates, and it is the case a check
        // that looked at only the first timer — or let the last answer overwrite the verdict —
        // would call healthy.
        systemd.Set(AptDailyTimersResource.UpgradeTimer, "disabled", "inactive");
        var half = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(half.InSync);
        Assert.Contains("apt-daily-upgrade.timer is disabled and inactive", half.Observed, StringComparison.Ordinal);
        Assert.Contains("apt-daily.timer is enabled and active", half.Observed, StringComparison.Ordinal);

        // The mirror image, so neither position in the pair is the only one being read.
        systemd.Set(AptDailyTimersResource.UpgradeTimer, "enabled", "active");
        systemd.Set(AptDailyTimersResource.RefreshTimer, "disabled", "inactive");
        Assert.False((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);

        // Enabled and not running: it will fire again after the next restart and not before, which
        // on a frame that reboots when the reconciler tells it to can be a very long time.
        systemd.Set(AptDailyTimersResource.RefreshTimer, "enabled", "inactive");
        var idle = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(idle.InSync);
        Assert.Contains("apt-daily.timer is enabled and inactive", idle.Observed, StringComparison.Ordinal);

        // And the state that most resembles success: enabled under /run, which is a tmpfs. It is
        // armed right now, it reads as enabled to anything asking for a boolean, and it is gone at
        // the next boot — so accepting it would put back exactly the silent stop being fixed here.
        systemd.Set(AptDailyTimersResource.RefreshTimer, "enabled-runtime", "active");
        var runtime = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(runtime.InSync);
        Assert.Contains("apt-daily.timer is enabled-runtime and active", runtime.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Arming_the_timers_enables_and_starts_them_in_one_call_and_the_verify_agrees()
    {
        var systemd = new FakeTimerUnits();
        systemd.Set(AptDailyTimersResource.RefreshTimer, "disabled", "inactive");
        systemd.Set(AptDailyTimersResource.UpgradeTimer, "disabled", "inactive");

        var resource = new AptDailyTimersResource(systemd);
        Assert.False((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);

        var action = await resource.ActAsync(TestContext.Current.CancellationToken);

        // `--now` and not a bare `enable`: enable alone writes the timers.target want and leaves
        // the frame not running the timer until something restarts it.
        Assert.Contains("enable --now apt-daily.timer", systemd.Commands);
        Assert.Contains("enable --now apt-daily-upgrade.timer", systemd.Commands);
        Assert.Contains("systemctl enable --now apt-daily.timer", action.Change, StringComparison.Ordinal);
        Assert.Contains("security fixes", action.Gloss, StringComparison.Ordinal);

        // Observe is the Verify (§2.3), and it is reading a model that the enable genuinely
        // changed rather than a canned second answer.
        Assert.True((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);
    }

    [Fact]
    public async Task A_masked_timer_is_named_in_the_delta_with_the_unmask_and_is_never_enabled_around()
    {
        // Masking points the unit at /dev/null and `systemctl enable` refuses against it, so an Act
        // that tried anyway would spend three attempts and three reboots to reach an escalation
        // whose delta said only that the timer is not enabled. The mask is somebody's deliberate
        // change; this resource reports it and does not reverse it.
        var systemd = new FakeTimerUnits();
        systemd.Set(AptDailyTimersResource.RefreshTimer, "masked", "inactive");

        var resource = new AptDailyTimersResource(systemd);
        var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);

        Assert.False(observation.InSync);
        Assert.Contains("apt-daily.timer is masked and inactive", observation.Observed, StringComparison.Ordinal);
        Assert.Contains("systemctl unmask apt-daily.timer", observation.Observed, StringComparison.Ordinal);
        Assert.Contains("deliberate override", observation.Observed, StringComparison.Ordinal);

        var action = await resource.ActAsync(TestContext.Current.CancellationToken);

        // The healthy half of the pair is still repaired. Refusing the whole Act because one unit
        // is masked would leave a second, ordinary fault sitting behind the first one.
        Assert.DoesNotContain("enable --now apt-daily.timer", systemd.Commands);
        Assert.Contains("enable --now apt-daily-upgrade.timer", systemd.Commands);
        Assert.Contains("left masked", action.Change, StringComparison.Ordinal);
        Assert.Contains("only a person", action.Gloss, StringComparison.Ordinal);

        // masked-runtime is the same refusal written under /run, and reading only the first
        // spelling would send the agent into the enable-fails-every-time loop for half of them.
        Assert.True(AptTimerState.IsMasked("masked-runtime"));
        Assert.False(AptTimerState.IsMasked("disabled"));
    }

    [Fact]
    public async Task A_timer_that_is_not_on_the_frame_at_all_reports_systemds_own_sentence_about_it()
    {
        // `is-enabled` exits non-zero for disabled, for masked and for a unit that does not exist
        // alike, so the exit code cannot tell them apart. Anything unrecognised has to travel
        // verbatim rather than being collapsed into a word this code invented.
        var systemd = new FakeTimerUnits();
        systemd.Remove(AptDailyTimersResource.UpgradeTimer);

        var observation = await new AptDailyTimersResource(systemd)
            .ObserveAsync(TestContext.Current.CancellationToken);

        Assert.False(observation.InSync);
        Assert.Contains("No such file or directory", observation.Observed, StringComparison.Ordinal);

        // And a systemctl that answers nothing at all — the shape a service manager that has
        // stopped responding produces — says so rather than being read as an empty state name.
        var silent = await new AptDailyTimersResource(new RecordingSystemControl())
            .ObserveAsync(TestContext.Current.CancellationToken);

        Assert.False(silent.InSync);
        Assert.Contains("no answer from systemctl", silent.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void The_timer_resource_declares_no_edge_and_sits_with_the_other_apt_resources()
    {
        // Read through the interface, because both members below are interface defaults the
        // resource deliberately does not override — which is the assertion.
        IResource resource = new AptDailyTimersResource(new RecordingSystemControl());

        // The catalog's dash, and not an omission. The timers ship with apt, are on every frame,
        // and `systemctl enable --now` succeeds against them whatever apt's configuration says, so
        // there is no prerequisite whose absence could make this fail confusingly — which is the
        // whole job of DependsOn. An edge on apt.auto-upgrades-enabled would also be actively
        // harmful: a dependent of a resource that is not in sync is Blocked, so a frame whose
        // 20auto-upgrades could not be read would hide a masked timer behind a blocked row, one
        // silent stop concealing another.
        Assert.Empty(resource.DependsOn);

        // Not a gate: observing is a status query and `systemctl enable --now` plainly works, which
        // is exactly the distinction IResource.IsGate draws.
        Assert.False(resource.IsGate);

        using var files = new TemporaryFiles();
        var order = DeviceCatalog.BuildGraph(AgentResourceGraphTests.Context(files))
            .Ordered
            .Select(item => item.Name)
            .ToList();

        Assert.True(order.IndexOf(AptAutoUpgradesResource.ResourceName)
            < order.IndexOf(AptDailyTimersResource.ResourceName));
        Assert.True(order.IndexOf(UnattendedUpgradesPolicyResource.ResourceName)
            < order.IndexOf(AptDailyTimersResource.ResourceName));
        Assert.True(order.IndexOf(AptDailyTimersResource.ResourceName)
            < order.IndexOf(HostnameResource.ResourceName));
    }

    [Fact]
    public async Task Switched_off_timers_converge_across_the_reboot_the_ladder_takes()
    {
        var systemd = new FakeTimerUnits();
        systemd.Set(AptDailyTimersResource.RefreshTimer, "disabled", "inactive");
        systemd.Set(AptDailyTimersResource.UpgradeTimer, "disabled", "inactive");

        using var harness = new ReconcileHarness(
            new ReconcileOptions { Countdown = TimeSpan.Zero, AttemptBudget = 3 },
            new AptDailyTimersResource(systemd));

        var outcome = await harness.ConvergeAsync();

        // §2.4: every resource reboots, and the verify that matters is the one on the far side of
        // it. This converges rather than escalating, and it crosses the boundary to do it.
        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.NotEmpty(harness.Boundary.Crossings);
        Assert.Equal(
            ResourceStatusKind.InSync,
            ReconcileHarness.StatusOf(outcome, AptDailyTimersResource.ResourceName).Kind);
    }

    [Fact]
    public async Task A_masked_timer_reaches_a_person_with_the_command_that_fixes_it()
    {
        var systemd = new FakeTimerUnits();
        systemd.Set(AptDailyTimersResource.RefreshTimer, "masked", "inactive");
        systemd.Set(AptDailyTimersResource.UpgradeTimer, "masked", "inactive");

        using var harness = new ReconcileHarness(
            new ReconcileOptions { Countdown = TimeSpan.Zero, AttemptBudget = 3 },
            new AptDailyTimersResource(systemd));

        var outcome = await harness.ConvergeAsync();
        var status = ReconcileHarness.StatusOf(outcome, AptDailyTimersResource.ResourceName);

        // It still walks the ladder to a person, because a frame that is not receiving security
        // updates is a fault however deliberately it was caused. What §2.5 requires is that the
        // delta reaching that person names the cause and the command rather than the symptom.
        Assert.True(status.Kind.HasGivenUp());
        Assert.Contains("systemctl unmask", status.Delta!, StringComparison.Ordinal);

        // §2.7's two registers, both on the row: the plain half says what stopped happening rather
        // than what a timer is, and the technical half is the exact expected-versus-observed.
        Assert.Equal(
            "This frame has stopped looking for its own security fixes, so it is not getting them any more.",
            status.Detected);
        Assert.Contains("apt-daily.timer is enabled and active", status.Delta!, StringComparison.Ordinal);
        Assert.Contains("apt-daily.timer is masked and inactive", status.Delta!, StringComparison.Ordinal);

        // Both masked names both, in one unmask a person can paste, rather than repeating a
        // singular sentence twice or naming only the first one it found.
        Assert.Contains(
            "apt-daily.timer and apt-daily-upgrade.timer are masked",
            status.Delta!,
            StringComparison.Ordinal);
        Assert.Contains(
            "systemctl unmask apt-daily.timer apt-daily-upgrade.timer",
            status.Delta!,
            StringComparison.Ordinal);
        Assert.StartsWith("Both of this frame's daily checks", status.Gloss!, StringComparison.Ordinal);
        Assert.DoesNotContain(systemd.Commands, command => command.StartsWith("enable ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_lost_keypair_is_refused_rather_than_regenerated()
    {
        using var files = new TemporaryFiles();
        var resource = new AgentKeypairResource(files.Store, files.Files, () => "AAAA-BBBB-CCCC-DDDD");

        var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(observation.InSync);

        var action = await resource.ActAsync(TestContext.Current.CancellationToken);

        // A fresh keypair would be a new identity wearing the old frame's name: the record drops
        // out of the Fleet Manager and the frame reappears in the adoption queue as somebody else.
        // §3.3 makes that a confirmed destructive decommission a person takes.
        Assert.Contains("refused", action.Change, StringComparison.Ordinal);
        Assert.False(files.Store.Exists(DeviceKeyStore.KeyFileName));
    }

    [Fact]
    public async Task A_fingerprint_that_moved_is_reported_and_not_quietly_accepted()
    {
        using var files = new TemporaryFiles();
        var identity = "AAAA-BBBB-CCCC-DDDD";
        var resource = new AgentKeypairResource(files.Store, files.Files, () => identity);

        files.Store.WriteSecretAtomic(DeviceKeyStore.KeyFileName, "not a real key"u8);

        // First pass records the fingerprint, which is what makes the comparison possible at all.
        Assert.False((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);
        await resource.ActAsync(TestContext.Current.CancellationToken);
        Assert.True((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);

        // §2.9's root-only file, read back through the same seam the Act wrote it through — an Act
        // whose effect its own Verify cannot see would be no verification at all.
        Assert.Equal(
            AgentKeypairResource.SecretMode,
            files.Files.ModeOf(files.Store.PathOf(DeviceKeyStore.KeyFileName)));

        identity = "EEEE-FFFF-GGGG-HHHH";
        var moved = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(moved.InSync);
        Assert.Contains("AAAA-BBBB-CCCC-DDDD", moved.Observed, StringComparison.Ordinal);
        Assert.Contains("EEEE-FFFF-GGGG-HHHH", moved.Observed, StringComparison.Ordinal);

        // Recording the new one would erase the only evidence the identity changed, so the Act
        // deliberately leaves the record alone and lets the ladder escalate to a person.
        await resource.ActAsync(TestContext.Current.CancellationToken);
        Assert.Equal("AAAA-BBBB-CCCC-DDDD", files.Store.ReadText(AgentKeypairResource.FingerprintFileName)?.Trim());
    }

    [Fact]
    public async Task An_unset_time_zone_leaves_the_frame_alone_rather_than_guessing_utc()
    {
        var processes = new RecordingProcessRunner();
        using var files = new TemporaryFiles();
        var resource = new TimeZoneResource(files.Files, processes, FleetValues.None);

        processes.Answers["timedatectl show -p Timezone --value"] =
            new ProcessResult(0, "Europe/Amsterdam", string.Empty);

        // A time zone belongs to the room the frame stands in. There is no catalog default, so an
        // unconfigured fleet must not move every frame onto one.
        var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.True(observation.InSync);
        Assert.Contains("Europe/Amsterdam", observation.Observed, StringComparison.Ordinal);
        Assert.DoesNotContain("timedatectl set-timezone", processes.Commands);
        Assert.Equal([AdoptionResource.ResourceName], resource.DependsOn);
    }

    [Fact]
    public async Task A_time_zone_is_converged_and_a_seed_that_disagrees_is_reported_but_never_acted_on()
    {
        var processes = new RecordingProcessRunner();
        using var files = new TemporaryFiles();
        var values = FleetValues.From(new Dictionary<string, string>
        {
            [TimeZoneResource.SettingKey] = "Europe/Amsterdam",
        });

        var resource = new TimeZoneResource(files.Files, processes, values);
        processes.Answers["timedatectl show -p Timezone --value"] = new ProcessResult(0, "Etc/UTC", string.Empty);

        // The catalog's instruction is to read the seed before designing an Act around it, and its
        // hostname sibling is the measured case where the assumed cloud-init trap did not exist.
        // So the seed is named in the delta and nothing writes to it.
        files.Seed(CloudInitSeed.UserDataPath, "#cloud-config\ntimezone: Europe/Berlin\n#hostname: raspberrypi\n");

        var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(observation.InSync);
        Assert.Contains("Europe/Berlin", observation.Observed, StringComparison.Ordinal);

        await resource.ActAsync(TestContext.Current.CancellationToken);
        Assert.Contains("timedatectl set-timezone Europe/Amsterdam", processes.Commands);
        Assert.Equal(
            "#cloud-config\ntimezone: Europe/Berlin\n#hostname: raspberrypi\n",
            files.Read(CloudInitSeed.UserDataPath));
    }

    [Fact]
    public async Task A_nonsense_time_zone_is_refused_before_it_reaches_timedatectl()
    {
        var processes = new RecordingProcessRunner();
        using var files = new TemporaryFiles();
        var values = FleetValues.From(new Dictionary<string, string>
        {
            [TimeZoneResource.SettingKey] = "../../etc/passwd",
        });

        var resource = new TimeZoneResource(files.Files, processes, values);
        var action = await resource.ActAsync(TestContext.Current.CancellationToken);

        Assert.Contains("refused", action.Change, StringComparison.Ordinal);
        Assert.DoesNotContain(processes.Commands, command => command.StartsWith("timedatectl set", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_keyboard_half_is_read_and_written_where_the_boot_time_services_look()
    {
        var processes = new RecordingProcessRunner();
        using var files = new TemporaryFiles();
        var values = FleetValues.From(new Dictionary<string, string> { [LocaleResource.KeyboardKey] = "gb" });
        var resource = new LocaleResource(files.Files, processes, values);

        processes.Answers["localectl status"] = new ProcessResult(0, "   System Locale: LANG=en_GB.UTF-8", string.Empty);
        files.Seed(LocaleResource.KeyboardPath, "XKBMODEL=\"pc105\"\nXKBLAYOUT=\"us\"\nXKBVARIANT=\"\"\n");

        Assert.Equal("us", resource.LiveKeyboard());
        Assert.False((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);

        await resource.ActAsync(TestContext.Current.CancellationToken);

        // set-x11-keymap and not set-keymap: console-setup.service and keyboard-setup.service are
        // enabled in the v1 reference and re-apply from /etc/default/keyboard at every boot, so a
        // console keymap alone would be put back. That competing owner is evidenced by the
        // inventory rather than inferred by analogy.
        Assert.Contains("localectl set-x11-keymap gb", processes.Commands);
        Assert.DoesNotContain("localectl set-locale LANG=", processes.Commands);
    }

    [Fact]
    public async Task An_unconfigured_fleet_never_switches_a_frame_to_a_default_locale()
    {
        var processes = new RecordingProcessRunner();
        using var files = new TemporaryFiles();
        var resource = new LocaleResource(files.Files, processes, FleetValues.None);

        processes.Answers["localectl status"] = new ProcessResult(0, "   System Locale: LANG=nl_NL.UTF-8", string.Empty);
        files.Seed(LocaleResource.KeyboardPath, "XKBLAYOUT=\"us\"\n");

        var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.True(observation.InSync);
        Assert.Contains("nl_NL.UTF-8", observation.Observed, StringComparison.Ordinal);
        Assert.DoesNotContain(processes.Commands, command => command.StartsWith("localectl set", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_two_writers_of_the_single_kernel_command_line_do_not_delete_each_others_work()
    {
        using var files = new TemporaryFiles();
        var log = new RecordingLog();
        var guard = new BootPartitionGuard(files.Files, files.Store, new MutableBootIdentity(), new ManualClock(), log);
        var processes = new RecordingProcessRunner();

        // Position 2 has already run and appended the rotation. Position 79 now has to merge into
        // the file that writer left behind, without re-serialising from anything older.
        files.Seed(
            BootConfigText.CmdlinePath,
            "console=serial0,115200 console=tty1 root=PARTUUID=f870549c-02 rootfstype=ext4 fsck.repair=yes rootwait fbcon=rotate:1\n");

        var values = FleetValues.From(new Dictionary<string, string>
        {
            [WifiRegulatoryDomainResource.SettingKey] = "NL",
        });

        var resource = new WifiRegulatoryDomainResource(files.Files, guard, processes, values, log);
        await resource.ActAsync(TestContext.Current.CancellationToken);

        var line = BootConfigText.ReadCmdline(files.Read(BootConfigText.CmdlinePath));
        Assert.Contains("fbcon=rotate:1", line, StringComparison.Ordinal);
        Assert.Contains("cfg80211.ieee80211_regdom=NL", line, StringComparison.Ordinal);
        Assert.Contains("root=PARTUUID=f870549c-02", line, StringComparison.Ordinal);
        Assert.Single(files.Read(BootConfigText.CmdlinePath)!.TrimEnd('\n').Split('\n'));

        // The FAT32 backup a card reader can reach, which is the only recovery a bricked boot
        // partition has (§5.5).
        Assert.NotNull(files.Read(BootPartitionGuard.BackupFor(BootConfigText.CmdlinePath)));
    }

    [Fact]
    public async Task A_country_change_rewrites_the_parameter_in_place_rather_than_adding_a_second()
    {
        using var files = new TemporaryFiles();
        var log = new RecordingLog();
        var guard = new BootPartitionGuard(files.Files, files.Store, new MutableBootIdentity(), new ManualClock(), log);

        files.Seed(
            BootConfigText.CmdlinePath,
            "console=tty1 root=PARTUUID=f870549c-02 rootwait cfg80211.ieee80211_regdom=NL fbcon=rotate:1\n");

        var values = FleetValues.From(new Dictionary<string, string>
        {
            [WifiRegulatoryDomainResource.SettingKey] = "de",
        });

        var resource = new WifiRegulatoryDomainResource(
            files.Files,
            guard,
            new RecordingProcessRunner(),
            values,
            log);

        await resource.ActAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            "console=tty1 root=PARTUUID=f870549c-02 rootwait cfg80211.ieee80211_regdom=DE fbcon=rotate:1",
            BootConfigText.ReadCmdline(files.Read(BootConfigText.CmdlinePath)));
    }

    [Fact]
    public async Task An_unset_country_leaves_a_flashed_regulatory_domain_exactly_where_it_is()
    {
        using var files = new TemporaryFiles();
        var log = new RecordingLog();
        var guard = new BootPartitionGuard(files.Files, files.Store, new MutableBootIdentity(), new ManualClock(), log);

        const string Line = "console=tty1 root=PARTUUID=f870549c-02 rootwait cfg80211.ieee80211_regdom=NL\n";
        files.Seed(BootConfigText.CmdlinePath, Line);

        var resource = new WifiRegulatoryDomainResource(
            files.Files,
            guard,
            new RecordingProcessRunner(),
            FleetValues.None,
            log);

        // There is no catalog default and there must not be: a regulatory domain is a property of
        // the country the frame stands in, and `00` is the most restrictive value rather than a
        // correct one. A frame flashed with NL keeps NL.
        var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.True(observation.InSync);
        Assert.Contains("cfg80211.ieee80211_regdom=NL", observation.Observed, StringComparison.Ordinal);
        Assert.Equal(Line, files.Read(BootConfigText.CmdlinePath));
    }

    [Fact]
    public void A_command_line_edit_that_would_cost_the_machine_its_root_parameter_is_refused()
    {
        const string Original = "console=tty1 root=PARTUUID=f870549c-02 rootwait";

        var good = BootConfigText.SetToken(Original, "cfg80211.ieee80211_regdom=", "cfg80211.ieee80211_regdom=NL");
        Assert.True(BootConfigText
            .ValidateCmdlineToken(Original, good, "cfg80211.ieee80211_regdom=", "cfg80211.ieee80211_regdom=NL")
            .Valid);

        // The failure §5.5 is actually about, and it has to be what the refusal says rather than
        // being reported as untidiness.
        var fatal = BootConfigText.ValidateCmdlineToken(
            Original,
            "console=tty1 rootwait cfg80211.ieee80211_regdom=NL",
            "cfg80211.ieee80211_regdom=",
            "cfg80211.ieee80211_regdom=NL");

        Assert.False(fatal.Valid);
        Assert.Contains("root=", fatal.Problem, StringComparison.Ordinal);

        // The firmware reads the first line only, so a second line is a silently ignored edit.
        Assert.False(BootConfigText
            .ValidateCmdlineToken(Original, Original + "\ncfg80211.ieee80211_regdom=NL\n", "cfg80211.ieee80211_regdom=", "cfg80211.ieee80211_regdom=NL")
            .Valid);
    }

    /// <summary>Verbatim from the v1 inventory's <c>EEPROM_CONFIG</c> block.</summary>
    private const string V1Eeprom = "[all]\nBOOT_UART=1\nPOWER_OFF_ON_HALT=1\nBOOT_ORDER=0xf461\n";

    [Fact]
    public async Task The_v1_bootloader_configuration_is_already_in_sync_so_nothing_is_ever_flashed()
    {
        var processes = new RecordingProcessRunner();
        using var files = new TemporaryFiles();
        var log = new RecordingLog();
        var guard = new BootPartitionGuard(files.Files, files.Store, new MutableBootIdentity(), new ManualClock(), log);
        var resource = new EepromConfigResource(processes, files.Files, files.Store, guard, log);

        processes.Answers["rpi-eeprom-config "] = new ProcessResult(0, V1Eeprom, string.Empty);

        // Measured on the stock mule 2026-08-15 and matching the v1 reference: these are
        // stock-image values, so on every frame this build targets the resource agrees and the Act
        // never runs. That is what makes a brick-capable resource affordable at all.
        Assert.True((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);
        Assert.DoesNotContain(processes.Commands, command => command.Contains("--apply", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_bootloader_configuration_that_cannot_be_read_is_never_written_over()
    {
        var processes = new RecordingProcessRunner();
        using var files = new TemporaryFiles();
        var log = new RecordingLog();
        var guard = new BootPartitionGuard(files.Files, files.Store, new MutableBootIdentity(), new ManualClock(), log);
        var resource = new EepromConfigResource(processes, files.Files, files.Store, guard, log);

        processes.Answers["rpi-eeprom-config "] = new ProcessResult(1, string.Empty, "command not found");

        var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(observation.InSync);

        // A blind write would replace whatever is in the EEPROM with the catalog's three keys and
        // nothing else, which on a Pi 5 is how a frame stops booting. There is no rollback for it.
        var action = await resource.ActAsync(TestContext.Current.CancellationToken);
        Assert.Contains("refused", action.Change, StringComparison.Ordinal);
        Assert.DoesNotContain(processes.Commands, command => command.Contains("--apply", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_drifted_boot_order_is_merged_into_the_configuration_that_is_there_and_backed_up_first()
    {
        var processes = new RecordingProcessRunner();
        using var files = new TemporaryFiles();
        var log = new RecordingLog();
        var guard = new BootPartitionGuard(files.Files, files.Store, new MutableBootIdentity(), new ManualClock(), log);
        var resource = new EepromConfigResource(processes, files.Files, files.Store, guard, log);

        const string Drifted = "[all]\nBOOT_UART=1\nPOWER_OFF_ON_HALT=1\nBOOT_ORDER=0xf41\nWAKE_ON_GPIO=1\n";
        processes.Answers["rpi-eeprom-config "] = new ProcessResult(0, Drifted, string.Empty);

        var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(observation.InSync);
        Assert.Contains("BOOT_ORDER=0xf41", observation.Observed, StringComparison.Ordinal);

        await resource.ActAsync(TestContext.Current.CancellationToken);

        // The FAT32 copy is the whole recovery path: an EEPROM write cannot be undone from
        // software, so what a person does with a card reader is apply this file back.
        Assert.Equal(Drifted, files.Read(EepromConfigResource.BackupPath));

        var candidate = files.Store.ReadText(EepromConfigResource.CandidateFileName);
        Assert.Contains("BOOT_ORDER=0xf461", candidate, StringComparison.Ordinal);
        Assert.Contains("WAKE_ON_GPIO=1", candidate, StringComparison.Ordinal);
        Assert.Contains("[all]", candidate, StringComparison.Ordinal);
        Assert.Contains(
            processes.Commands,
            command => command.StartsWith("rpi-eeprom-config --apply", StringComparison.Ordinal));
    }

    [Fact]
    public void A_bootloader_merge_that_would_touch_a_line_the_agent_does_not_own_is_refused()
    {
        // Structural, not semantic. Nothing here knows which bootloader settings exist — the
        // firmware does, and guessing would give false confidence — so what is proved is that no
        // line the catalog does not own has moved.
        Assert.True(EepromConfigResource.Validate(V1Eeprom, EepromConfigResource.Merge(V1Eeprom)).Valid);

        var meddled = EepromConfigResource.Merge(V1Eeprom).Replace("[all]", "[none]", StringComparison.Ordinal);
        var verdict = EepromConfigResource.Validate(V1Eeprom, meddled);
        Assert.False(verdict.Valid);
        Assert.Contains("[all]", verdict.Problem, StringComparison.Ordinal);

        Assert.False(EepromConfigResource.Validate(V1Eeprom, "BOOT_UART=1\n").Valid);

        // A key already present is rewritten in place, so it stays under the section header that
        // decides what it applies to.
        var merged = EepromConfigResource.Merge("[all]\nBOOT_ORDER=0xf41\nWAKE_ON_GPIO=1\n");
        Assert.Equal("0xf461", EepromConfigResource.ValueOf(merged, "BOOT_ORDER"));
        Assert.StartsWith("[all]\nBOOT_ORDER=0xf461\nWAKE_ON_GPIO=1\n", merged, StringComparison.Ordinal);
    }

    [Fact]
    public void The_three_bootloader_values_are_the_ones_the_v1_reference_carries()
    {
        // §7.1: read rather than remembered. reference/v1-state-inventory.txt is the frozen v1
        // reference Precondition zero exists to produce, and this asserts against the file.
        var inventory = File.ReadAllText(
            Path.Combine(GuiFreshnessTests.RepositoryRoot(), "reference", "v1-state-inventory.txt"));

        var start = inventory.IndexOf("== EEPROM_CONFIG", StringComparison.Ordinal);
        Assert.True(start > 0);

        var block = inventory[start..];
        var end = block.IndexOf("== PACKAGES", StringComparison.Ordinal);
        block = end > 0 ? block[..end] : block;

        foreach (var pair in EepromConfigResource.DesiredKeys)
        {
            Assert.Contains($"{pair.Key}={pair.Value}", block, StringComparison.Ordinal);
        }
    }
}

/// <summary>
/// systemd's timer half, modelled rather than scripted.
/// </summary>
/// <remarks>
/// <para>
/// A table of canned answers cannot produce the fault <see cref="AptDailyTimersResource"/> exists
/// to catch, because the whole question is whether an <c>enable</c> that was issued actually
/// changed what the next <c>is-enabled</c> reports. So this holds unit state: <c>enable --now</c>
/// really moves a unit to enabled and active, a masked unit really refuses it and stays masked —
/// which is systemd's own behaviour and the reason the Act reads the enablement before acting on
/// it — and a unit that is not on the frame answers in systemd's own words rather than in a word
/// this suite invented.
/// </para>
/// <para>
/// It answers <c>is-enabled</c>, <c>is-active</c> and <c>enable</c> and nothing else. Anything
/// further would be modelling systemd rather than the two questions this resource asks of it.
/// </para>
/// </remarks>
internal sealed class FakeTimerUnits : ISystemControl
{
    private readonly Dictionary<string, (string Enablement, string Activity)> _units = new(StringComparer.Ordinal);

    /// <summary>Both apt timers as a stock Debian image ships them.</summary>
    public FakeTimerUnits()
    {
        foreach (var timer in AptDailyTimersResource.Timers)
        {
            _units[timer] = (AptTimerState.EnabledState, AptTimerState.ActiveState);
        }
    }

    /// <summary>Every argument vector this has been asked to run, in order.</summary>
    public List<string> Commands { get; } = [];

    /// <summary>Puts a unit into a given state.</summary>
    public void Set(string unit, string enablement, string activity) => _units[unit] = (enablement, activity);

    /// <summary>Takes a unit off the frame entirely.</summary>
    public void Remove(string unit) => _units.Remove(unit);

    public Task<SystemControlResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        Commands.Add(string.Join(' ', arguments));

        var unit = arguments[^1];
        var known = _units.TryGetValue(unit, out var state);

        switch (arguments[0])
        {
            case "is-enabled":
                // systemctl says this on standard error and exits 1; SystemControlResult.Output is
                // output and error together, so the sentence is what the resource reads.
                return Answer(
                    known && string.Equals(state.Enablement, AptTimerState.EnabledState, StringComparison.Ordinal),
                    known ? state.Enablement : $"Failed to get unit file state for {unit}: No such file or directory");

            case "is-active":
                // `is-active` on a unit that does not exist prints `inactive` and exits 3, which is
                // why enablement is the half that can tell a missing unit from a disabled one.
                return Answer(
                    known && string.Equals(state.Activity, AptTimerState.ActiveState, StringComparison.Ordinal),
                    known ? state.Activity : "inactive");

            case "enable":
                if (!known)
                {
                    return Answer(false, $"Failed to enable unit: Unit file {unit} does not exist.");
                }

                if (AptTimerState.IsMasked(state.Enablement))
                {
                    // The refusal that makes the mask worth telling apart at all: enable does not
                    // undo a mask, and the unit is still masked afterwards.
                    return Answer(false, $"Failed to enable unit: Unit file {unit} is masked.");
                }

                _units[unit] = (
                    AptTimerState.EnabledState,
                    arguments.Contains("--now") ? AptTimerState.ActiveState : state.Activity);
                return Answer(true, string.Empty);

            default:
                return Answer(true, string.Empty);
        }
    }

    private static Task<SystemControlResult> Answer(bool succeeded, string output) =>
        Task.FromResult(new SystemControlResult(succeeded, output));
}
