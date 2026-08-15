using FrameLink.Agent;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Systemd;

namespace FrameLink.Tests;

/// <summary>
/// The systemd unit, which ships inside the binary rather than beside it (version2.md §2.1).
/// </summary>
public sealed class AgentSystemdUnitTests
{
    [Fact]
    public void The_unit_travels_inside_the_binary()
    {
        // §2.1: "One single Native AOT binary. No supplemental program files, ever." A unit file
        // copied alongside the binary would be one more thing to keep in step and to get wrong.
        var unit = UnitInstaller.ReadUnit();

        Assert.Contains("[Unit]", unit, StringComparison.Ordinal);
        Assert.Contains("[Service]", unit, StringComparison.Ordinal);
        Assert.Contains("[Install]", unit, StringComparison.Ordinal);
    }

    [Fact]
    public void The_unit_starts_the_agent_and_always_brings_it_back()
    {
        var unit = UnitInstaller.ReadUnit();

        Assert.Contains("ExecStart=/usr/local/bin/fl-agent run", unit, StringComparison.Ordinal);
        Assert.Contains("Restart=always", unit, StringComparison.Ordinal);
        Assert.Contains("WantedBy=multi-user.target", unit, StringComparison.Ordinal);
    }

    [Fact]
    public void The_state_directory_is_created_root_only_before_the_agent_starts()
    {
        // §2.9. Letting systemd create it removes the window in which the keypair could be written
        // under a wider mode on first boot.
        var unit = UnitInstaller.ReadUnit();

        Assert.Contains("StateDirectory=fl-agent", unit, StringComparison.Ordinal);
        Assert.Contains("StateDirectoryMode=0700", unit, StringComparison.Ordinal);
    }

    [Fact]
    public void The_unit_never_weakens_KillMode_to_work_around_an_updater()
    {
        // This is the §6.2 finding turned into a guard. Velopack was dropped for the agent because
        // it applies updates from a child process, which the default KillMode=control-group kills
        // the instant the daemon exits. The workarounds — KillMode=process, systemd-run --scope —
        // are exactly what this assertion exists to keep out: the custom updater does the whole
        // swap in-process, so the default is correct and must stay.
        var directives = Directives();

        Assert.DoesNotContain("KillMode=", directives, StringComparison.Ordinal);
        Assert.DoesNotContain("systemd-run", directives, StringComparison.Ordinal);

        // The reasoning is in the file as a comment, so the next person to reach for KillMode
        // finds out why before changing it.
        Assert.Contains("KillMode", UnitInstaller.ReadUnit(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_network_is_wanted_but_never_required()
    {
        // §4.1: the agent reconciles, verifies, retries and escalates with no server present. A
        // household with a dead router must still get a narrated screen, not a service that never
        // starts.
        var directives = Directives();

        Assert.Contains("Wants=network-online.target", directives, StringComparison.Ordinal);
        Assert.DoesNotContain("Requires=", directives, StringComparison.Ordinal);
    }

    /// <summary>The unit with its commentary stripped, so a rule reads directives not prose.</summary>
    private static string Directives() =>
        string.Join(
            '\n',
            UnitInstaller.ReadUnit()
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && line[0] != '#'));

    [Fact]
    public async Task Installing_writes_the_unit_and_enables_it()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fl-agent-tests", Guid.NewGuid().ToString("N"));
        var unitPath = Path.Combine(directory, "fl-agent.service");
        var systemControl = new RecordingSystemControl();

        try
        {
            var installed = await UnitInstaller.InstallAsync(
                systemControl, NullLog.Instance, unitPath, TestContext.Current.CancellationToken);

            Assert.True(installed);
            Assert.Equal(UnitInstaller.ReadUnit(), await File.ReadAllTextAsync(unitPath, TestContext.Current.CancellationToken));
            Assert.Equal(["daemon-reload", "enable --now fl-agent.service"], systemControl.Commands);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Installing_twice_leaves_the_same_file_and_reports_success_both_times()
    {
        // §0.1: every command that mutates state is safe to run a second time with no additional
        // effect.
        var directory = Path.Combine(Path.GetTempPath(), "fl-agent-tests", Guid.NewGuid().ToString("N"));
        var unitPath = Path.Combine(directory, "fl-agent.service");
        var systemControl = new RecordingSystemControl();

        try
        {
            await UnitInstaller.InstallAsync(systemControl, NullLog.Instance, unitPath, TestContext.Current.CancellationToken);
            var afterFirst = await File.ReadAllTextAsync(unitPath, TestContext.Current.CancellationToken);

            var again = await UnitInstaller.InstallAsync(systemControl, NullLog.Instance, unitPath, TestContext.Current.CancellationToken);

            Assert.True(again);
            Assert.Equal(afterFirst, await File.ReadAllTextAsync(unitPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_failing_daemon_reload_is_reported_rather_than_claimed_as_success()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fl-agent-tests", Guid.NewGuid().ToString("N"));
        var unitPath = Path.Combine(directory, "fl-agent.service");
        var systemControl = new RecordingSystemControl { Succeed = false };

        try
        {
            var installed = await UnitInstaller.InstallAsync(
                systemControl, NullLog.Instance, unitPath, TestContext.Current.CancellationToken);

            Assert.False(installed);
            Assert.Equal(["daemon-reload"], systemControl.Commands);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void The_exit_code_for_an_update_restart_is_distinguishable_from_an_ordinary_stop()
    {
        // So that a restart caused by an update is legible in `systemctl status` rather than
        // looking like an unexplained exit.
        Assert.NotEqual(ExitCodes.Success, ExitCodes.RestartToApplyUpdate);
        Assert.NotEqual(ExitCodes.Unrecoverable, ExitCodes.RestartToApplyUpdate);
    }
}
