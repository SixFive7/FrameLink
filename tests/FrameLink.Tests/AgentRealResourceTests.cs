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
        var resource = new JournalStorageResource(files.Files, FleetValues.None);
        using var harness = new ReconcileHarness(Fast, resource);

        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.Equal(
            "[Journal]\nStorage=persistent\nSystemMaxUse=64M\n",
            files.Read(JournalStorageResource.DropInPath));

        // Storage=persistent with the directory missing silently stays volatile, which is how the
        // August 2026 failures left no evidence for days. The directory is part of the setting.
        Assert.True(files.Files.DirectoryExists(JournalStorageResource.JournalDirectory));
    }

    [Fact]
    public async Task The_journal_cap_comes_from_the_fleet_setting_and_the_logic_stays_compiled_in()
    {
        // §2.2, decision 15: the Fleet Manager supplies values, never logic. A string arrives; the
        // file it lands in and the shape it takes are the agent's.
        using var files = new TemporaryFiles();
        var values = FleetValues.From(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [JournalStorageResource.SettingKey] = "256M",
        });

        using var harness = new ReconcileHarness(Fast, new JournalStorageResource(files.Files, values));
        await harness.ConvergeAsync();

        Assert.Contains("SystemMaxUse=256M", files.Read(JournalStorageResource.DropInPath)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_drop_in_that_is_already_right_is_left_completely_alone()
    {
        using var files = new TemporaryFiles();
        var resource = new JournalStorageResource(files.Files, FleetValues.None);
        files.Seed(JournalStorageResource.DropInPath, resource.DesiredContent());
        files.Files.EnsureDirectory(JournalStorageResource.JournalDirectory);

        using var harness = new ReconcileHarness(Fast, resource);
        var outcome = await harness.PassAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.Empty(harness.Boundary.Crossings);
    }

    [Fact]
    public async Task Windows_line_endings_are_not_mistaken_for_drift()
    {
        // The Chromium-command-line trap in a different disguise: an equality compare that is
        // sensitive to something irrelevant reports permanent false drift, and a frame that
        // reboots forever over a carriage return is worse than one that never applies the setting.
        using var files = new TemporaryFiles();
        var resource = new JournalStorageResource(files.Files, FleetValues.None);
        files.Seed(
            JournalStorageResource.DropInPath,
            resource.DesiredContent().Replace("\n", "\r\n", StringComparison.Ordinal));
        files.Files.EnsureDirectory(JournalStorageResource.JournalDirectory);

        var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);

        Assert.True(observation.InSync);
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
