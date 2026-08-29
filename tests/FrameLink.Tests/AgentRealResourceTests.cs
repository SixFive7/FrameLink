using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;

namespace FrameLink.Tests;

/// <summary>
/// The resources that touch a real system, exercised against a real filesystem.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HostSystemFiles"/> is the shipping implementation, rooted at a throwaway directory
/// rather than at <c>/</c>. So these tests write the real bytes to real files at the real Linux
/// paths, read them back the same way, and hash them with the same code a frame runs. What is
/// <b>not</b> real is the Linux side of the commands — <c>hostnamectl</c> and <c>systemctl</c>
/// are scripted — and no Raspberry Pi has run any of it.
/// </para>
/// <para>
/// §7.2 asks for tests that assert outcomes, and the outcome asserted here is the state of a
/// filesystem after a reconciliation, not the fact that a method was called.
/// </para>
/// </remarks>
public sealed class AgentRealResourceTests
{
    private static ReconcileOptions Fast => new() { Countdown = TimeSpan.Zero, AttemptBudget = 3 };

    [Fact]
    public async Task The_journal_resource_writes_a_real_drop_in_and_creates_the_directory()
    {
        using var files = new TemporaryFiles();
        var journald = new FakeJournald(files.Files);
        var resource = new JournalStorageResource(files.Files, journald, journald, FleetValues.None);
        using var harness = new ReconcileHarness(Fast, resource);

        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.Equal(
            "[Journal]\nStorage=persistent\nSystemMaxUse=64M\n",
            files.Read(JournalStorageResource.DropInPath));

        // Storage=persistent with the directory missing silently stays volatile, which is how the
        // August 2026 failures left no evidence for days. The directory is part of the setting.
        Assert.True(files.Files.DirectoryExists(JournalStorageResource.JournalDirectory));

        // And it converged by reading systemd's own resolver rather than the file it had itself
        // written, which is the whole of what changed about this resource.
        Assert.Contains(JournaldConfig.Command, journald.Commands);
    }

    [Fact]
    public async Task The_journal_cap_comes_from_the_fleet_setting_and_the_logic_stays_compiled_in()
    {
        // §2.2, decision 15: the Fleet Manager supplies values, never logic. A string arrives; the
        // file it lands in and the shape it takes are the agent's.
        using var files = new TemporaryFiles();
        var journald = new FakeJournald(files.Files);
        var values = FleetValues.From(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [JournalStorageResource.SettingKey] = "256M",
        });

        using var harness = new ReconcileHarness(
            Fast,
            new JournalStorageResource(files.Files, journald, journald, values));
        await harness.ConvergeAsync();

        Assert.Contains("SystemMaxUse=256M", files.Read(JournalStorageResource.DropInPath)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_drop_in_that_is_already_right_is_left_completely_alone()
    {
        using var files = new TemporaryFiles();
        var journald = new FakeJournald(files.Files);
        var resource = new JournalStorageResource(files.Files, journald, journald, FleetValues.None);
        files.Seed(JournalStorageResource.DropInPath, resource.DesiredContent());
        files.Files.EnsureDirectory(JournalStorageResource.JournalDirectory);

        using var harness = new ReconcileHarness(Fast, resource);
        var outcome = await harness.PassAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.Empty(harness.Boundary.Crossings);
    }

    [Fact]
    public async Task A_later_drop_in_that_overrides_the_setting_is_drift_and_the_delta_names_the_file()
    {
        // The fault this resource used to have, and the reason it stopped reading its own file.
        // /etc/systemd/journald.conf.d is a merge directory: systemd applies every drop-in in name
        // order and the last assignment wins. persistent.conf was byte-perfect, /var/log/journal
        // existed, and the resource reported a fully green journal on a frame writing nothing to
        // the card.
        using var files = new TemporaryFiles();
        var journald = new FakeJournald(files.Files);
        var resource = new JournalStorageResource(files.Files, journald, journald, FleetValues.None);

        files.Seed(JournalStorageResource.DropInPath, resource.DesiredContent());
        files.Files.EnsureDirectory(JournalStorageResource.JournalDirectory);
        Assert.True((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);

        const string Overriding = "/etc/systemd/journald.conf.d/zz-local.conf";
        files.Seed(Overriding, "[Journal]\nStorage=volatile\n");

        var overridden = await resource.ObserveAsync(TestContext.Current.CancellationToken);

        Assert.False(overridden.InSync);
        Assert.Contains("Storage=volatile", overridden.Observed, StringComparison.Ordinal);

        // The file, and not only the value. An operator told just that Storage is wrong has to go
        // hunting; the one told which file sets it has the file to open — and rewriting our own
        // drop-in cannot win against it, which is why the resource has to say so.
        Assert.Contains(Overriding, overridden.Observed, StringComparison.Ordinal);
        Assert.Contains(JournalStorageResource.DropInPath, overridden.Observed, StringComparison.Ordinal);

        // The Act still writes the half it owns, and the observation stays red — so the ladder
        // carries the override to a person instead of retrying against it in silence.
        await resource.ActAsync(TestContext.Current.CancellationToken);
        Assert.False((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);
    }

    [Fact]
    public async Task A_drop_in_that_sorts_earlier_loses_and_is_not_reported_as_drift()
    {
        // The mirror image, so the test above cannot pass by treating any second file as a fault.
        // 00-early.conf is applied before persistent.conf, so persistent.conf wins and the frame is
        // correct — a resource that flagged this would report drift it could never act on.
        using var files = new TemporaryFiles();
        var journald = new FakeJournald(files.Files);
        var resource = new JournalStorageResource(files.Files, journald, journald, FleetValues.None);

        files.Seed("/etc/systemd/journald.conf.d/00-early.conf", "[Journal]\nStorage=volatile\nSystemMaxUse=1G\n");
        files.Seed(JournalStorageResource.DropInPath, resource.DesiredContent());
        files.Files.EnsureDirectory(JournalStorageResource.JournalDirectory);

        Assert.True((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);
    }

    [Fact]
    public async Task A_cleared_cap_in_a_later_drop_in_is_caught_though_it_names_no_number()
    {
        // SystemMaxUse= with nothing after it is a real assignment in systemd's parser and means
        // "back to the built-in default". It neutralises the cap without ever writing a wrong
        // value, so a reader that skipped empty right-hand sides would call this frame correct.
        using var files = new TemporaryFiles();
        var journald = new FakeJournald(files.Files);
        var resource = new JournalStorageResource(files.Files, journald, journald, FleetValues.None);

        files.Seed(JournalStorageResource.DropInPath, resource.DesiredContent());
        files.Seed("/etc/systemd/journald.conf.d/zz-uncapped.conf", "[Journal]\nSystemMaxUse=\n");
        files.Files.EnsureDirectory(JournalStorageResource.JournalDirectory);

        var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);

        Assert.False(observation.InSync);
        Assert.Contains("SystemMaxUse is cleared", observation.Observed, StringComparison.Ordinal);
        Assert.Contains("zz-uncapped.conf", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_masked_journald_is_drift_however_right_the_configuration_is()
    {
        // The second way this went green while nothing was written: the configuration can be
        // perfect and the daemon pointed at /dev/null. Losing the journal loses the evidence for
        // every other failure on this frame, so a silent stop here hides all of them.
        using var files = new TemporaryFiles();
        var journald = new FakeJournald(files.Files) { Enablement = "masked" };
        var resource = new JournalStorageResource(files.Files, journald, journald, FleetValues.None);

        files.Seed(JournalStorageResource.DropInPath, resource.DesiredContent());
        files.Files.EnsureDirectory(JournalStorageResource.JournalDirectory);

        var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);

        Assert.False(observation.InSync);
        Assert.Contains("systemd-journald.service is masked", observation.Observed, StringComparison.Ordinal);
        Assert.Contains("systemctl unmask systemd-journald.service", observation.Observed, StringComparison.Ordinal);

        // Masking is somebody else's deliberate change and `systemctl enable` refuses against it,
        // so the Act repairs the half it owns, names the half it does not, and reverses nothing.
        var action = await resource.ActAsync(TestContext.Current.CancellationToken);
        Assert.Contains("left masked", action.Change, StringComparison.Ordinal);
        Assert.Contains("only a person", action.Gloss, StringComparison.Ordinal);
        Assert.DoesNotContain(journald.Commands, command => command.StartsWith("unmask", StringComparison.Ordinal));

        // masked-runtime is the same refusal written under /run. Reading only the first spelling
        // would let exactly the temporary masks through.
        journald.Enablement = "masked-runtime";
        Assert.False((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);
    }

    [Fact]
    public async Task A_static_journald_is_the_healthy_answer_and_is_never_read_as_switched_off()
    {
        // systemd-journald.service has no [Install] section, so `is-enabled` answers `static` on
        // every healthy frame ever built. A check that demanded `enabled` here would be permanent
        // false drift on all of them, which is why only the masked spellings are a fault.
        using var files = new TemporaryFiles();
        var journald = new FakeJournald(files.Files) { Enablement = "static" };
        var resource = new JournalStorageResource(files.Files, journald, journald, FleetValues.None);

        files.Seed(JournalStorageResource.DropInPath, resource.DesiredContent());
        files.Files.EnsureDirectory(JournalStorageResource.JournalDirectory);

        Assert.True((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);

        // And a frame whose daemon is fine is never told that somebody switched its logging off:
        // an Act that said so on every frame would make the sentence that matters unreadable.
        var action = await resource.ActAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("reversing a mask", action.Change, StringComparison.Ordinal);
        Assert.DoesNotContain("only a person", action.Gloss, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Journald_left_at_its_own_defaults_is_drift_even_where_the_directory_exists()
    {
        // The near miss the resource must not accept. journald's default is Storage=auto, which
        // means "persistent if /var/log/journal exists" — true right up until somebody removes the
        // directory, at which point the journal goes back into memory with no setting having
        // changed. A frame in that state looks correct from the outside and is one `rm` from
        // keeping no record at all, so an unset value is drift and not a shrug.
        using var files = new TemporaryFiles();
        var journald = new FakeJournald(files.Files);
        var resource = new JournalStorageResource(files.Files, journald, journald, FleetValues.None);

        files.Files.EnsureDirectory(JournalStorageResource.JournalDirectory);

        var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);

        Assert.False(observation.InSync);
        Assert.Contains("Storage is unset", observation.Observed, StringComparison.Ordinal);
        Assert.Contains("SystemMaxUse is unset", observation.Observed, StringComparison.Ordinal);

        await resource.ActAsync(TestContext.Current.CancellationToken);
        Assert.True((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);
    }

    [Fact]
    public async Task A_journald_configuration_that_cannot_be_read_is_drift_and_names_the_command()
    {
        // Not "the settings are wrong" — nothing was read. Drift is still the right direction: a
        // resource that cannot see the configuration escalates to a person rather than reporting a
        // frame it could not inspect as correct.
        //
        // The seeded drop-in is what makes this test worth having. cat-config prints the fragments
        // it could open before it exits non-zero, so the partial output here says exactly what a
        // correct frame says — and a reader that took it would report an incomplete merge as the
        // merge, which is the whole fault this resource exists to end.
        using var files = new TemporaryFiles();
        var journald = new FakeJournald(files.Files) { Readable = false };
        var resource = new JournalStorageResource(files.Files, journald, journald, FleetValues.None);

        files.Seed(JournalStorageResource.DropInPath, resource.DesiredContent());
        files.Files.EnsureDirectory(JournalStorageResource.JournalDirectory);

        var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);

        Assert.False(observation.InSync);
        Assert.Contains(JournaldConfig.Command, observation.Observed, StringComparison.Ordinal);

        // And the same frame with the same files reads as correct the moment the command works,
        // so the drift above is the failed read and nothing else about this machine.
        journald.Readable = true;
        Assert.True((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);
    }

    [Fact]
    public async Task A_perfect_configuration_with_no_journal_directory_is_still_volatile_and_still_drift()
    {
        // Storage=persistent with /var/log/journal missing silently stays volatile: the setting is
        // right, it is not in force, and nothing says so. The directory is part of the setting,
        // which is why it is folded into this Observe rather than left to a second resource.
        using var files = new TemporaryFiles();
        var journald = new FakeJournald(files.Files);
        var resource = new JournalStorageResource(files.Files, journald, journald, FleetValues.None);

        files.Seed(JournalStorageResource.DropInPath, resource.DesiredContent());

        var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);

        Assert.False(observation.InSync);
        Assert.Contains("in memory only", observation.Observed, StringComparison.Ordinal);

        await resource.ActAsync(TestContext.Current.CancellationToken);
        Assert.True(files.Files.DirectoryExists(JournalStorageResource.JournalDirectory));
        Assert.True((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);
    }

    [Fact]
    public void The_effective_journald_setting_is_the_last_assignment_inside_the_journal_section()
    {
        const string Merged =
            "# /etc/systemd/journald.conf\n"
            + "#  This file is part of systemd.\n"
            + "# See journald.conf(5) for details.\n"
            + "[Journal]\n"
            + "#Storage=auto\n"
            + "#SystemMaxUse=\n"
            + "\n"
            + "# /etc/systemd/journald.conf.d/persistent.conf\n"
            + "# written by fl-agent\n"
            + "[Journal]\n"
            + "Storage=persistent\n"
            + "SystemMaxUse=64M\n"
            + "\n"
            + "# /etc/systemd/journald.conf.d/zz-local.conf\n"
            + "[Journal]\n"
            + "Storage = volatile\n"
            + "[Other]\n"
            + "SystemMaxUse=1G\n"
            + "\n"
            + "# /etc/systemd/journald.conf.d/zz-typo.conf\n"
            + "[Journal]\n"
            + "Storage\n";

        // `Storage = volatile` with spaces is a valid assignment — systemd strips both sides of the
        // `=` — so the key is matched after trimming and the value carries neither space.
        var storage = JournaldConfig.Effective(Merged, JournalStorageResource.StorageKey);
        Assert.Equal("volatile", storage!.Value.Value);
        Assert.Equal("/etc/systemd/journald.conf.d/zz-local.conf", storage.Value.Source);

        // The commented-out vendor defaults are comments and never values, and a setting under a
        // section journald does not read is not in force either — so the cap still in force is the
        // one persistent.conf wrote, attributed to persistent.conf and not to the ordinary comment
        // sitting between that file's header and its first assignment.
        var cap = JournaldConfig.Effective(Merged, JournalStorageResource.MaxUseKey);
        Assert.Equal("64M", cap!.Value.Value);
        Assert.Equal(JournalStorageResource.DropInPath, cap.Value.Source);

        // A capture that reached the agent with CRLF must parse identically. The Chromium-command
        // -line trap in a different disguise: a compare sensitive to something irrelevant reports
        // permanent false drift, and a frame that reboots forever over a carriage return is worse
        // than one that never applies the setting.
        Assert.Equal(
            storage,
            JournaldConfig.Effective(Merged.Replace("\n", "\r\n", StringComparison.Ordinal), JournalStorageResource.StorageKey));

        // A setting no file mentions is null rather than an invented default.
        Assert.Null(JournaldConfig.Effective(Merged, "Compress"));
        Assert.Null(JournaldConfig.Effective(string.Empty, JournalStorageResource.StorageKey));

        // The last block is a bare `Storage` with no `=`, which is what a typo in a hand-written
        // drop-in looks like. journald ignores it and so does this, rather than throwing halfway
        // through a merge and turning one person's typo into an unreadable configuration.
        Assert.Equal("volatile", JournaldConfig.Effective(Merged, JournalStorageResource.StorageKey)!.Value.Value);
    }

    [Fact]
    public async Task The_cpu_governor_chain_converges_unit_then_enablement_then_value()
    {
        using var files = new TemporaryFiles();
        var systemd = new ScriptedSystemControl();
        var governorRoot = CpuGovernorResource.PolicyRoot;

        files.Files.EnsureDirectory(governorRoot + "/policy0");
        files.Files.EnsureDirectory(governorRoot + "/policy1");
        files.Seed(governorRoot + "/policy0/scaling_governor", "ondemand");
        files.Seed(governorRoot + "/policy1/scaling_governor", "ondemand");

        using var harness = new ReconcileHarness(
            Fast,
            new CpuGovernorUnitResource(files.Files, systemd),
            new CpuGovernorUnitEnabledResource(systemd),
            new CpuGovernorResource(files.Files, FleetValues.None));

        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.Equal(CpuGovernorUnitResource.DesiredContent, files.Read(CpuGovernorUnitResource.UnitPath));
        Assert.Contains("enable cpu-performance.service", systemd.Commands);
        Assert.Equal("performance", files.Read(governorRoot + "/policy0/scaling_governor"));
        Assert.Equal("performance", files.Read(governorRoot + "/policy1/scaling_governor"));

        // Three resources, three reboots. §2.4 has no exceptions and this is what that costs.
        Assert.Equal(3, harness.Boundary.Crossings.Count);
    }

    [Fact]
    public async Task The_governor_is_blocked_while_its_unit_is_not_enabled()
    {
        using var files = new TemporaryFiles();
        var systemd = new ScriptedSystemControl { EnableSucceeds = false };

        files.Files.EnsureDirectory(CpuGovernorResource.PolicyRoot + "/policy0");
        files.Seed(CpuGovernorResource.PolicyRoot + "/policy0/scaling_governor", "ondemand");

        using var harness = new ReconcileHarness(
            Fast,
            new CpuGovernorUnitResource(files.Files, systemd),
            new CpuGovernorUnitEnabledResource(systemd),
            new CpuGovernorResource(files.Files, FleetValues.None));

        var outcome = await harness.ConvergeAsync();
        var governor = ReconcileHarness.StatusOf(outcome, CpuGovernorResource.ResourceName);

        Assert.Equal(ResourceStatusKind.Blocked, governor.Kind);
        Assert.Equal(CpuGovernorUnitEnabledResource.ResourceName, governor.BlockedBy);
        Assert.Equal("ondemand", files.Read(CpuGovernorResource.PolicyRoot + "/policy0/scaling_governor"));
    }

    [Fact]
    public async Task The_governor_archetype_a_unit_that_is_enabled_and_does_not_work_escalates()
    {
        // §2.4 cites this exact case as the reason every resource reboots: on v1 the kernel
        // parameter landed in /proc/cmdline and the governor still came up ondemand. Here the unit
        // is enabled, the Act writes the value, and the boot puts it back — so the resource passes
        // its Act every time and fails its Verify every time, walks the ladder, and reaches a
        // person. That is what should have happened in v1 and did not.
        using var files = new TemporaryFiles();
        var systemd = new ScriptedSystemControl { AlreadyEnabled = true };
        var policy = CpuGovernorResource.PolicyRoot + "/policy0/scaling_governor";

        files.Files.EnsureDirectory(CpuGovernorResource.PolicyRoot + "/policy0");
        files.Seed(policy, "ondemand");
        files.Seed(CpuGovernorUnitResource.UnitPath, CpuGovernorUnitResource.DesiredContent);

        using var harness = new ReconcileHarness(
            Fast,
            new CpuGovernorUnitResource(files.Files, systemd),
            new CpuGovernorUnitEnabledResource(systemd),
            new CpuGovernorResource(files.Files, FleetValues.None));

        harness.Telemetry.Connected = true;
        harness.Boundary.OnBoot = (_, _) =>
        {
            // The broken oneshot unit: it runs and leaves the governor where it was.
            files.Seed(policy, "ondemand");
            return Task.CompletedTask;
        };

        var outcome = await harness.ConvergeAsync();
        var governor = ReconcileHarness.StatusOf(outcome, CpuGovernorResource.ResourceName);

        Assert.Equal(ResourceStatusKind.Escalated, governor.Kind);
        Assert.Equal(Fast.AttemptBudget, governor.Attempts);
        Assert.Contains("policy0=ondemand", governor.Delta!, StringComparison.Ordinal);
        Assert.NotEmpty(harness.Telemetry.OfKind(FrameLink.Protocol.DeviceEventKinds.Escalation));
    }

    [Fact]
    public async Task A_machine_with_no_cpufreq_is_in_sync_rather_than_permanently_broken()
    {
        // A virtual agent (§5.3) has no cpufreq. Reporting drift would put it into a permanent
        // repair loop over hardware it does not have.
        using var files = new TemporaryFiles();
        var observation = await new CpuGovernorResource(files.Files, FleetValues.None)
            .ObserveAsync(TestContext.Current.CancellationToken);

        Assert.True(observation.InSync);
        Assert.Contains("no cpufreq", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_adoption_resource_will_not_write_an_adoption_it_does_not_have()
    {
        // §3.3: a pending device receives nothing, and that has to include the record that says
        // it received something.
        using var files = new TemporaryFiles();
        using var harness = new ReconcileHarness(Fast, new AdoptionResource(files.Store, () => ServerAnswer.Rejected));

        var outcome = await harness.ConvergeAsync();
        var status = ReconcileHarness.StatusOf(outcome, AdoptionResource.ResourceName);

        Assert.NotEqual(ResourceStatusKind.InSync, status.Kind);
        Assert.Equal("waiting for adoption", files.Store.ReadText(AdoptionResource.FileName));
    }

    [Fact]
    public async Task Everything_that_needs_an_issued_value_is_blocked_until_the_frame_is_adopted()
    {
        using var files = new TemporaryFiles();
        var processes = new RecordingProcessRunner();

        using var harness = new ReconcileHarness(
            Fast,
            new AdoptionResource(files.Store, () => ServerAnswer.Rejected),
            new HostnameResource(files.Files, processes, HostnameValues("framelink-douwe")));

        var outcome = await harness.ConvergeAsync();
        var hostname = ReconcileHarness.StatusOf(outcome, HostnameResource.ResourceName);

        Assert.Equal(ResourceStatusKind.Blocked, hostname.Kind);
        Assert.Equal(AdoptionResource.ResourceName, hostname.BlockedBy);
        Assert.DoesNotContain(processes.Commands, command => command.Contains("set-hostname", StringComparison.Ordinal));
    }

    private static FleetValues HostnameValues(string hostname) =>
        FleetValues.From(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [HostnameResource.SettingKey] = hostname,
        });
}

/// <summary>A systemd that answers the way a frame's would, and remembers what it was asked.</summary>
internal sealed class ScriptedSystemControl : ISystemControl
{
    public List<string> Commands { get; } = [];

    /// <summary>Whether the unit is already enabled before anything happens.</summary>
    public bool AlreadyEnabled { get; set; }

    /// <summary>Whether <c>enable</c> actually works.</summary>
    public bool EnableSucceeds { get; set; } = true;

    public Task<SystemControlResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var line = string.Join(' ', arguments);
        Commands.Add(line);

        if (line.StartsWith("is-enabled", StringComparison.Ordinal))
        {
            // Real systemctl exits non-zero for `disabled` and prints the answer on stdout, which
            // is why the resource reads the text rather than the exit code.
            return Task.FromResult(AlreadyEnabled
                ? new SystemControlResult(true, "enabled")
                : new SystemControlResult(false, "disabled"));
        }

        if (line.StartsWith("enable", StringComparison.Ordinal))
        {
            if (EnableSucceeds)
            {
                AlreadyEnabled = true;
                return Task.FromResult(new SystemControlResult(true, string.Empty));
            }

            return Task.FromResult(new SystemControlResult(false, "Failed to enable unit: Read-only file system"));
        }

        return Task.FromResult(new SystemControlResult(true, string.Empty));
    }
}

/// <summary>
/// journald as systemd resolves it, rendered from the files a frame actually has.
/// </summary>
/// <remarks>
/// <para>
/// The double models the <b>merge</b> rather than scripting an answer, and that is what makes the
/// tests around it worth having: <c>cat-config</c> output is composed from the drop-in directory in
/// name order, so seeding a second file with a later name genuinely overrides the first, and the
/// Act writing <c>persistent.conf</c> genuinely changes what the next Observe reads. A canned
/// string would agree with the resource by construction and could never converge through a
/// harness.
/// </para>
/// <para>
/// It answers both seams because a frame has one journald: <see cref="IProcessRunner"/> for
/// <c>systemd-analyze cat-config</c> and <see cref="ISystemControl"/> for
/// <c>systemctl is-enabled</c>.
/// </para>
/// </remarks>
internal sealed class FakeJournald : IProcessRunner, ISystemControl
{
    /// <summary>The main configuration file as a stock Trixie image ships it: all defaults, all commented.</summary>
    public const string VendorFile =
        "#  This file is part of systemd.\n"
        + "#\n"
        + "# Entries in this file show the compile time defaults. Local configuration should be\n"
        + "# created by creating drop-ins in the journald.conf.d/ subdirectory.\n"
        + "#\n"
        + "# See journald.conf(5) for details.\n"
        + "\n"
        + "[Journal]\n"
        + "#Storage=auto\n"
        + "#SystemMaxUse=\n";

    /// <summary>Where the main file lives.</summary>
    public const string ConfigPath = "/etc/systemd/journald.conf";

    /// <summary>The directory drop-ins are merged from.</summary>
    public const string DropInDirectory = "/etc/systemd/journald.conf.d";

    private readonly ISystemFiles _files;

    public FakeJournald(ISystemFiles files)
    {
        _files = files;
        _files.WriteText(ConfigPath, VendorFile);
    }

    /// <summary>Every command this has been asked to run, in order.</summary>
    public List<string> Commands { get; } = [];

    /// <summary>What <c>systemctl is-enabled systemd-journald.service</c> answers.</summary>
    /// <remarks><c>static</c> is the truth on a healthy frame: the unit has no [Install] section.</remarks>
    public string Enablement { get; set; } = "static";

    /// <summary>Whether the whole configuration could be read.</summary>
    /// <remarks>
    /// False models the shape that matters: <c>cat-config</c> prints the fragments it could open,
    /// says on standard error which one it could not, and exits non-zero. Trusting that partial
    /// output would report a setting from an incomplete merge as though it were the merge, which
    /// is the fault the whole resource exists to end — so the exit code has to be read.
    /// </remarks>
    public bool Readable { get; set; } = true;

    public Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        Commands.Add(executable + " " + string.Join(' ', arguments));

        return Task.FromResult(Readable
            ? new ProcessResult(0, Render(), string.Empty)
            : new ProcessResult(
                1,
                Render(),
                "Failed to open /etc/systemd/journald.conf.d/zz-local.conf: Permission denied"));
    }

    public Task<SystemControlResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        Commands.Add(string.Join(' ', arguments));

        // systemctl exits non-zero for a masked unit and prints the word on standard output either
        // way, which is why the resource reads the text and not the exit code.
        return Task.FromResult(new SystemControlResult(!SystemdUnits.IsMasked(Enablement), Enablement));
    }

    /// <summary>The main file, then every drop-in in name order, each under its own path header.</summary>
    private string Render()
    {
        var text = new System.Text.StringBuilder();
        Append(text, ConfigPath);

        foreach (var path in _files.ListFiles(DropInDirectory))
        {
            Append(text, path);
        }

        return text.ToString();
    }

    private void Append(System.Text.StringBuilder text, string path)
    {
        if (_files.ReadText(path) is not { } content)
        {
            return;
        }

        text.Append("# ").Append(path).Append('\n').Append(content).Append('\n');
    }
}
