using FrameLink.Agent;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;
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

    [Fact]
    public void The_two_committed_copies_of_the_unit_are_one_text()
    {
        // The unit is committed twice: tools/harness/assets/fl-agent.service is what `fl.py deploy`
        // puts on a bare frame, and src/FrameLink.Agent/Systemd/fl-agent.service is embedded in the
        // binary and written by `fl-agent install`. Neither can read the other — the harness is
        // Python reading the checkout, the agent is a Native AOT binary carrying a resource — so
        // nothing but this assertion stops them drifting.
        //
        // They had already drifted, which is the point. The harness copy carried a start limit the
        // embedded copy lacked, set TTYPath (and so gave the console stage a $TERM) where the
        // embedded copy did not, and the embedded copy's Documentation= named a repository that no
        // longer exists. A frame was running a different service depending on which tool installed
        // it, and each copy's tests passed.
        //
        // Bytes, not text: a checkout that lands CRLF on one and LF on the other is a real defect,
        // not a cosmetic one. `fl.py deploy` compares the remote file against a CRLF-normalised
        // template, while the agent writes its resource verbatim, so the two would fight over the
        // file forever — each rewriting it, reloading systemd and restarting the agent.
        var harness = File.ReadAllBytes(
            Path.Combine(RepositoryRoot(), "tools", "harness", "assets", "fl-agent.service"));
        var embedded = EmbeddedUnitBytes();

        Assert.True(
            harness.AsSpan().SequenceEqual(embedded),
            "The two committed copies of fl-agent.service differ.\n\n"
            + $"  tools/harness/assets/fl-agent.service         {harness.Length} bytes\n"
            + $"  src/FrameLink.Agent/Systemd/fl-agent.service  {embedded.Length} bytes\n"
            + FirstDifference(harness, embedded)
            + "\n\nThey are one text with two homes. Edit one, edit both, in the same commit.");
    }

    [Fact]
    public void Every_directive_sits_in_a_section_that_accepts_it()
    {
        // The guard for the defect the mule found on 2026-08-15: StartLimitIntervalSec written under
        // [Service], where systemd's parser does not register it. systemd logged
        //
        //   Unknown key 'StartLimitIntervalSec' in section [Service], ignoring.
        //
        // and then started the service, so `systemctl status` was green and the restart rate
        // limiting was silently gone. Nothing in the build, the deploy or this suite noticed.
        //
        // `systemd-analyze verify` is the real check and build/verify-unit.sh runs it, but it needs
        // Docker, the network and a Debian image. This runs everywhere, in 11 seconds with the rest
        // of the suite, off nothing but the checkout — which is what makes it the one that will
        // actually be red when somebody adds the next directive to the wrong section.
        var offenders = new List<string>();
        var seen = 0;

        foreach (var (line, section, key, _) in ParsedDirectives())
        {
            seen++;

            if (!AcceptingSection.TryGetValue(key, out var expected))
            {
                offenders.Add(
                    $"  line {line}: '{key}' is not in this test's table. Look it up in "
                    + "systemd.unit(5), systemd.service(5) or systemd.exec(5), then add it to "
                    + "AcceptingSection with the section it belongs to.");
            }
            else if (!string.Equals(expected, section, StringComparison.Ordinal))
            {
                offenders.Add(
                    $"  line {line}: '{key}' is a [{expected}] key but sits in [{section}]. "
                    + "systemd will ignore it and start the unit anyway.");
            }
        }

        // Without this the test passes when the parser below finds nothing at all — the same shape
        // of failure as a gate whose command never ran. Bumped only when a directive is added.
        Assert.True(
            seen >= 19,
            $"Only {seen} directives were parsed out of the unit; the parser in this test is broken, "
            + "so its verdict means nothing.");

        Assert.True(
            offenders.Count == 0,
            "fl-agent.service has directives in sections systemd does not accept them in:\n\n"
            + string.Join('\n', offenders));
    }

    [Fact]
    public void The_unit_names_the_terminal_the_console_stage_actually_paints()
    {
        // TTYPath is what makes exec_context_has_tty() true and therefore what makes systemd derive
        // and export $TERM for the service. Pointing it at a terminal the stage does not write to
        // would still produce a $TERM, so nothing at runtime would complain — it would just be a
        // unit describing a frame that no longer exists. One value, one source.
        var directives = ParsedDirectives().Single(directive => directive.Key == "TTYPath");

        Assert.Equal(TtyTerminal.DefaultPath, directives.Value);
        Assert.NotEqual("/dev/tty" + TtyTerminal.ProductTerminal, directives.Value);
    }

    [Fact]
    public void The_unit_still_refuses_to_take_the_console_getty_away()
    {
        // Conflicts=getty@tty1.service would give the console stage the panel to itself and would
        // remove the physical login §5.5 leans on for a frame that will not come up and cannot be
        // reached over the network. It has been rejected twice for that reason, and once more now
        // that the stage has a terminal of its own and does not need it. The comment is the record
        // of why, so the directive is what must stay absent while the reasoning stays present.
        Assert.DoesNotContain("Conflicts=", Directives(), StringComparison.Ordinal);
        Assert.Contains("Conflicts=getty@tty1.service", UnitInstaller.ReadUnit(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_restart_loop_has_a_brake_and_it_is_switched_on()
    {
        // version2.md §2.4: "an unbounded retry cycle is more damaging than a stalled provision."
        // With Restart=always and no working start limit, a crash-looping agent restarts forever on
        // a 2 GB appliance. Two ways to lose the brake, so two assertions: the keys can go missing,
        // and they can be present but disabled.
        var directives = ParsedDirectives().ToList();

        var interval = directives.SingleOrDefault(d => d.Key == "StartLimitIntervalSec");
        var burst = directives.SingleOrDefault(d => d.Key == "StartLimitBurst");

        Assert.True(
            interval.Key is not null && burst.Key is not null,
            "StartLimitIntervalSec= and StartLimitBurst= must both be set. Restart=always with no "
            + "start limit is the unbounded retry cycle §2.4 exists to forbid.");

        Assert.Equal("Unit", interval.Section);
        Assert.Equal("Unit", burst.Section);

        // StartLimitIntervalSec=0 disables rate limiting outright — it does not mean "use the
        // default". Moving the old [Service] line to [Unit] unchanged would have swapped an
        // accidentally absent brake for a deliberately absent one, which is strictly worse: the
        // ignored key at least left DefaultStartLimitIntervalSec in force.
        Assert.NotEqual("0", interval.Value);
        Assert.NotEqual("0", burst.Value);
    }

    /// <summary>The unit with its commentary stripped, so a rule reads directives not prose.</summary>
    private static string Directives() =>
        string.Join(
            '\n',
            UnitInstaller.ReadUnit()
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && line[0] != '#'));

    /// <summary>
    /// The section systemd's parser accepts each directive this unit uses, and only those.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Transcribed from systemd 257's <c>src/core/load-fragment-gperf.gperf.in</c>, which is the
    /// table the parser is generated from, cross-checked against <c>systemd-analyze verify</c> via
    /// <c>build/verify-unit.sh</c>. A key absent from this table fails the test rather than passing
    /// it: adding a directive to the unit is meant to cost one documentation lookup.
    /// </para>
    /// <para>
    /// The start-limit keys are the reason the table exists. systemd moved them from [Service] to
    /// [Unit] in v229 and kept <c>StartLimitInterval</c>, <c>StartLimitBurst</c> and
    /// <c>StartLimitAction</c> working under [Service] as legacy aliases — but not the modern
    /// <c>StartLimitIntervalSec</c> spelling, which exists under [Unit] alone. So three of the four
    /// names appear to work in the wrong section and the fourth does not, and the one that does not
    /// fails by being ignored. This table records where each key <i>belongs</i>, not everywhere
    /// systemd will tolerate it, so the deprecated [Service] spellings are deliberately not listed.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> AcceptingSection = new(StringComparer.Ordinal)
    {
        // [Unit] — systemd.unit(5).
        ["Description"] = "Unit",
        ["Documentation"] = "Unit",
        ["Wants"] = "Unit",
        ["After"] = "Unit",
        ["StartLimitIntervalSec"] = "Unit",
        ["StartLimitBurst"] = "Unit",

        // [Service] — systemd.service(5).
        ["Type"] = "Service",
        ["ExecStart"] = "Service",
        ["Restart"] = "Service",
        ["RestartSec"] = "Service",
        ["TimeoutStopSec"] = "Service",

        // [Service] too, but documented in systemd.exec(5): these come from the parser's
        // EXEC_CONTEXT_CONFIG_ITEMS block, which every unit type that forks a process shares.
        ["User"] = "Service",
        ["StateDirectory"] = "Service",
        ["StateDirectoryMode"] = "Service",
        ["TTYPath"] = "Service",
        ["StandardOutput"] = "Service",
        ["StandardError"] = "Service",
        ["SyslogIdentifier"] = "Service",

        // [Install] — systemd.unit(5).
        ["WantedBy"] = "Install",
    };

    /// <summary>Every <c>Key=Value</c> in the unit, with the section it was written under.</summary>
    private static List<(int Line, string Section, string Key, string Value)> ParsedDirectives()
    {
        var directives = new List<(int, string, string, string)>();
        var section = string.Empty;
        var number = 0;

        foreach (var raw in UnitInstaller.ReadUnit().Split('\n'))
        {
            number++;
            var line = raw.Trim();

            // systemd treats both '#' and ';' as comment introducers.
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1];
                continue;
            }

            var split = line.IndexOf('=');
            if (split > 0)
            {
                directives.Add((number, section, line[..split].Trim(), line[(split + 1)..].Trim()));
            }
        }

        return directives;
    }

    /// <summary>The embedded unit as bytes, so line endings and a stray BOM are both visible.</summary>
    private static byte[] EmbeddedUnitBytes()
    {
        using var stream = typeof(UnitInstaller).Assembly
            .GetManifestResourceStream(UnitInstaller.ResourceName)
            ?? throw new InvalidOperationException(
                $"The embedded resource '{UnitInstaller.ResourceName}' is missing from this build.");

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Where two copies of the unit part company, named in a way a human can act on.</summary>
    private static string FirstDifference(byte[] harness, byte[] embedded)
    {
        var shared = Math.Min(harness.Length, embedded.Length);
        for (var i = 0; i < shared; i++)
        {
            if (harness[i] != embedded[i])
            {
                var line = harness.AsSpan(0, i).Count((byte)'\n') + 1;
                return $"\n  first difference at byte {i} (line {line}): "
                    + $"0x{harness[i]:x2} in the harness copy, 0x{embedded[i]:x2} in the embedded one.";
            }
        }

        return $"\n  identical for the first {shared} bytes, then one copy continues.";
    }

    /// <summary>Walks up from the test binary to the directory holding the solution.</summary>
    private static string RepositoryRoot()
    {
        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && probe is not null; depth++, probe = probe.Parent)
        {
            if (File.Exists(Path.Combine(probe.FullName, "FrameLink.slnx")))
            {
                return probe.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"No FrameLink.slnx above {AppContext.BaseDirectory}; this test reads the repository, not the build output.");
    }

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
    public async Task The_unit_is_staged_beside_itself_and_renamed_into_place()
    {
        // A plain overwrite truncates the target and then writes it, and the target here is the
        // file that starts the agent. A power cut inside that window leaves half a unit, which is
        // a frame that boots without the one process that could have repaired it. So the bytes go
        // to a sibling and reach the real name by rename(2), and the stale sibling a previous
        // interrupted install would have left is simply consumed by the next one.
        var directory = Path.Combine(Path.GetTempPath(), "fl-agent-tests", Guid.NewGuid().ToString("N"));
        var unitPath = Path.Combine(directory, "fl-agent.service");
        var staging = unitPath + UnitInstaller.StagingSuffix;
        var systemControl = new RecordingSystemControl();

        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                staging, "[Unit]\nDescription=half a un", TestContext.Current.CancellationToken);

            Assert.True(await UnitInstaller.InstallAsync(
                systemControl, NullLog.Instance, unitPath, TestContext.Current.CancellationToken));

            // Gone because it was renamed, not because anything deleted it.
            Assert.False(File.Exists(staging));
            Assert.Equal(directory, Path.GetDirectoryName(staging));

            // Byte for byte the embedded resource: routing the write through a staging file had
            // to change where the bytes go and nothing about what they are, and a byte-order mark
            // or a substituted character is exactly what a new encoder would introduce.
            Assert.Equal(
                EmbeddedUnitBytes(),
                await File.ReadAllBytesAsync(unitPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_write_that_cannot_even_start_leaves_the_unit_systemd_already_has()
    {
        // The failure the old File.WriteAllTextAsync could not survive: it truncated first, so a
        // write that failed at all destroyed the working unit that was there. Staging means a
        // failure before the rename cannot reach the target. The failure is produced by putting a
        // directory where the staging file wants to be, which is the cheapest way to make one
        // FileStream throw.
        var directory = Path.Combine(Path.GetTempPath(), "fl-agent-tests", Guid.NewGuid().ToString("N"));
        var unitPath = Path.Combine(directory, "fl-agent.service");
        var systemControl = new RecordingSystemControl();

        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                unitPath,
                "[Unit]\nDescription=the unit this frame is running\n",
                TestContext.Current.CancellationToken);

            Directory.CreateDirectory(unitPath + UnitInstaller.StagingSuffix);

            var failure = await Record.ExceptionAsync(() => UnitInstaller.InstallAsync(
                systemControl, NullLog.Instance, unitPath, TestContext.Current.CancellationToken));

            Assert.True(
                failure is IOException or UnauthorizedAccessException,
                $"Expected the blocked staging path to fail the write; got {failure?.GetType().Name ?? "no exception"}.");

            Assert.Equal(
                "[Unit]\nDescription=the unit this frame is running\n",
                await File.ReadAllTextAsync(unitPath, TestContext.Current.CancellationToken));

            // And systemd was never told to reload a unit that was never written.
            Assert.Empty(systemControl.Commands);
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

/// <summary>
/// The agent's own unit as three reconciled resources.
/// </summary>
/// <remarks>
/// <para>
/// Every systemd unit this product installs was reconciled except <c>fl-agent.service</c>, the one
/// that starts the reconciler. It was written once by <c>fl-agent install</c> — or by
/// <c>fl.py deploy</c> — and never looked at again, so a unit written by an old installer survived
/// for the life of the SD card while updates replaced only the binary.
/// </para>
/// <para>
/// <b>Every one of these tests is about a repair with a deadline.</b> The resources are reconciled
/// by the agent this unit starts, so they can only ever help while that agent is running: after a
/// reboot into a broken unit there is no agent, no pass, no screen and nothing left to report that
/// anything is wrong. That is why the Acts below write, enable and reload, and why none of them
/// restarts — a restart would end the process performing the repair.
/// </para>
/// </remarks>
public sealed class AgentUnitResourceTests
{
    private static ReconcileOptions Fast => new() { Countdown = TimeSpan.Zero, AttemptBudget = 3 };

    [Fact]
    public async Task The_agents_own_unit_is_reconciled_against_the_text_this_build_ships()
    {
        using var files = new TemporaryFiles();
        var systemd = new FakeAgentUnit();
        var resource = new AgentUnitResource(files.Files, systemd);

        var absent = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(absent.InSync);
        Assert.Contains("absent", absent.Observed, StringComparison.Ordinal);

        await resource.ActAsync(TestContext.Current.CancellationToken);

        // The embedded text, byte for byte. The verb that first installs the unit and the resource
        // that repairs it share one writer precisely so a frame provisioned either way runs the
        // same service — the two committed copies of this file have drifted from each other once
        // already, and a third path to the same defect was not worth having.
        Assert.Equal(UnitInstaller.ReadUnit(), files.Read(AgentUnitResource.UnitPath));
        Assert.True((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);

        // A unit systemd has not re-read is a unit the other two resources would be reading a stale
        // answer about.
        Assert.Contains("daemon-reload", systemd.Commands);

        // And never a restart. `systemctl restart fl-agent.service` would kill the process running
        // this pass, which is the only process that can still repair this frame; §2.4's reboot is
        // what puts the new unit into service.
        Assert.DoesNotContain(systemd.Commands, command => command.StartsWith("restart", StringComparison.Ordinal));
        Assert.DoesNotContain(systemd.Commands, command => command.StartsWith("stop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_unit_that_drifted_is_rewritten_through_a_staging_file_that_is_renamed_away()
    {
        // A plain overwrite truncates the target and then writes it, and the target is the file
        // that starts the agent: a power cut inside that window leaves half a unit, which is a
        // frame that boots without the one process that could have repaired it. The resource
        // therefore writes through UnitInstaller rather than through ISystemFiles.WriteText.
        using var files = new TemporaryFiles();
        var systemd = new FakeAgentUnit();
        var resource = new AgentUnitResource(files.Files, systemd);

        files.Seed(AgentUnitResource.UnitPath, "[Unit]\nDescription=a unit some older installer wrote\n");
        Assert.False((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);

        // A stale sibling, as an install or a repair interrupted by a power cut would have left it.
        // The next write consumes it, which is what proves the bytes travelled through the rename
        // rather than through a plain overwrite of the unit itself.
        var staging = files.Files.Resolve(AgentUnitResource.UnitPath) + UnitInstaller.StagingSuffix;
        await File.WriteAllTextAsync(
            staging, "[Unit]\nDescription=half a un", TestContext.Current.CancellationToken);

        var action = await resource.ActAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UnitInstaller.ReadUnit(), files.Read(AgentUnitResource.UnitPath));
        Assert.False(File.Exists(staging));
        Assert.Contains(AgentUnitResource.UnitPath, action.Change, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_broken_unit_is_repaired_while_the_agent_is_still_the_one_that_can_do_it()
    {
        using var files = new TemporaryFiles();
        var systemd = new FakeAgentUnit { Enablement = "disabled" };

        using var harness = new ReconcileHarness(
            Fast,
            new AgentUnitResource(files.Files, systemd),
            new AgentUnitEnabledResource(systemd),
            new AgentUnitRunningResource(systemd));

        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.All(outcome.Statuses, status => Assert.Equal(ResourceStatusKind.InSync, status.Kind));
        Assert.Equal(UnitInstaller.ReadUnit(), files.Read(AgentUnitResource.UnitPath));
        Assert.Equal(SystemdUnits.EnabledState, systemd.Enablement);

        // §2.4: every resource reboots, and the verify that matters is the one on the far side of
        // it. Which is also the honest limit of the whole trio — a frame that has already rebooted
        // into a broken unit has no agent to run any of this.
        Assert.NotEmpty(harness.Boundary.Crossings);
    }

    [Fact]
    public async Task A_unit_that_is_not_enabled_is_drift_and_enabled_runtime_is_never_accepted()
    {
        // The half the review did not propose and the operator did, because it is the one whose
        // failure is silent: a disabled fl-agent.service behaves perfectly — green passes,
        // telemetry, the right screen — until the next boot, after which nothing comes back and
        // what did not come back is the reporter.
        var systemd = new FakeAgentUnit();
        var resource = new AgentUnitEnabledResource(systemd);

        Assert.True((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);

        systemd.Enablement = "disabled";
        var disabled = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(disabled.InSync);
        Assert.Equal("disabled", disabled.Observed);

        // The state that most resembles success: an enablement written under /run, which is a
        // tmpfs. It reads as enabled to anything asking for a boolean and is gone at the next boot,
        // so accepting it would be the exact fault wearing the costume of the fix.
        systemd.Enablement = SystemdUnits.RuntimeEnabledState;
        var runtime = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(runtime.InSync);
        Assert.Contains("tmpfs", runtime.Observed, StringComparison.Ordinal);

        // `static` would be right for a unit with no [Install] section; this one has one, so the
        // answer is wrong and is reported rather than tolerated.
        systemd.Enablement = "static";
        Assert.False((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);

        systemd.Enablement = "disabled";
        var action = await resource.ActAsync(TestContext.Current.CancellationToken);

        // A bare `enable`, never `enable --now`: the unit is already running, and this agent is it.
        Assert.Contains("enable fl-agent.service", systemd.Commands);
        Assert.DoesNotContain("enable --now fl-agent.service", systemd.Commands);
        Assert.Contains("every time the frame comes on", action.Gloss, StringComparison.Ordinal);
        Assert.True((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);
    }

    [Fact]
    public async Task A_masked_agent_unit_names_its_unmask_and_is_never_enabled_around()
    {
        // Masking points the unit at /dev/null and `systemctl enable` refuses against it, so an Act
        // that tried anyway would spend three attempts and three reboots reaching an escalation
        // whose delta said only that the unit is not enabled.
        var systemd = new FakeAgentUnit { Enablement = "masked" };
        var resource = new AgentUnitEnabledResource(systemd);

        var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(observation.InSync);
        Assert.Contains("systemctl unmask fl-agent.service", observation.Observed, StringComparison.Ordinal);

        var action = await resource.ActAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(systemd.Commands, command => command.StartsWith("enable", StringComparison.Ordinal));
        Assert.Contains("left masked", action.Change, StringComparison.Ordinal);
        Assert.Contains("only a person", action.Gloss, StringComparison.Ordinal);

        // masked-runtime is the same refusal written under /run, and reading only the first
        // spelling would send the agent into the enable-fails-every-time loop for half of them.
        systemd.Enablement = "masked-runtime";
        var runtime = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(runtime.InSync);
        Assert.Contains("systemctl unmask fl-agent.service", runtime.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_service_systemd_runs_is_the_one_the_file_describes()
    {
        var systemd = new FakeAgentUnit();
        var resource = new AgentUnitRunningResource(systemd, () => 4242);
        systemd.MainPid = 4242;

        var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);

        Assert.True(observation.InSync);
        Assert.Contains(AgentUnitResource.UnitPath, observation.Observed, StringComparison.Ordinal);
        Assert.Contains("4242", observation.Observed, StringComparison.Ordinal);

        // One `systemctl show`, not four. Asking separately would put gaps between readings of a
        // state that moves, which is the race the browser resource had to close after it restarted
        // a browser seconds from drawing.
        Assert.Single(systemd.Commands);
        Assert.StartsWith("show fl-agent.service", systemd.Commands[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_unit_edited_since_systemd_read_it_is_drift_and_a_daemon_reload_is_the_whole_repair()
    {
        // systemd parses a unit once and keeps it; editing the file afterwards changes nothing
        // until a daemon-reload. NeedDaemonReload is systemd computing that divergence from its own
        // load time, rather than this code guessing at it from a hash.
        var systemd = new FakeAgentUnit { NeedDaemonReload = "yes" };
        var resource = new AgentUnitRunningResource(systemd, () => systemd.MainPid);

        var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(observation.InSync);
        Assert.Contains("NeedDaemonReload=yes", observation.Observed, StringComparison.Ordinal);
        Assert.Contains("previous version of the file", observation.Observed, StringComparison.Ordinal);

        var action = await resource.ActAsync(TestContext.Current.CancellationToken);

        Assert.Contains("daemon-reload", systemd.Commands);
        Assert.DoesNotContain(systemd.Commands, command => command.StartsWith("restart", StringComparison.Ordinal));
        Assert.Contains("re-read", action.Gloss, StringComparison.Ordinal);
        Assert.True((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);
    }

    [Fact]
    public async Task A_run_shadow_outranking_the_installed_unit_is_drift_and_the_delta_names_it()
    {
        // The journald override in the unit search path: /run/systemd/system outranks
        // /etc/systemd/system, so unit.fl-agent.content can be byte-perfect for ever while systemd
        // runs something else entirely — and because /run is a tmpfs the shadow disappears at the
        // next boot, which makes it impossible to reason about afterwards.
        var systemd = new FakeAgentUnit { FragmentPath = "/run/systemd/system/fl-agent.service" };
        var resource = new AgentUnitRunningResource(systemd, () => systemd.MainPid);

        var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);

        Assert.False(observation.InSync);
        Assert.Contains("/run/systemd/system/fl-agent.service", observation.Observed, StringComparison.Ordinal);
        Assert.Contains(AgentUnitResource.UnitPath, observation.Observed, StringComparison.Ordinal);

        // A unit systemd loaded from nowhere at all is a different reading from one it loaded from
        // the wrong place, and both are drift.
        systemd.FragmentPath = string.Empty;
        var missing = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(missing.InSync);
        Assert.Contains("no unit file loaded", missing.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_systemd_did_not_start_is_drift_that_names_both_processes()
    {
        // The strongest identification available, and available only to this resource: the agent is
        // the unit's main process, so it compares systemd's answer against its own process id and
        // never against a path. What it catches is the frame whose unit is broken and where
        // somebody has started the agent by hand to keep it going — the state in which repairing
        // the unit is most urgent and least likely to be noticed.
        var systemd = new FakeAgentUnit { MainPid = 1234 };
        var resource = new AgentUnitRunningResource(systemd, () => 5678);

        var mismatched = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(mismatched.InSync);
        Assert.Contains("1234", mismatched.Observed, StringComparison.Ordinal);
        Assert.Contains("5678", mismatched.Observed, StringComparison.Ordinal);

        // Zero is a real reading and a different fact: systemd is running no process for the unit
        // at all, which an agent can only ever observe about itself if something else started it.
        systemd.MainPid = 0;
        systemd.ActiveState = "inactive";
        systemd.SubState = "dead";
        var stopped = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(stopped.InSync);
        Assert.Contains("no process", stopped.Observed, StringComparison.Ordinal);
        Assert.Contains("inactive", stopped.Observed, StringComparison.Ordinal);
        Assert.Contains("dead", stopped.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pid_systemd_did_not_report_is_told_apart_from_a_pid_of_zero()
    {
        // Absent and zero are two different facts. Zero says systemd is running nothing for this
        // unit; absent says systemd did not answer that question at all, and inventing a zero from
        // it would report a stopped service on a frame where the service is fine.
        var systemd = new FakeAgentUnit();
        systemd.Withheld.Add(AgentUnitRunningResource.MainPidProperty);
        var resource = new AgentUnitRunningResource(systemd, () => 4242);

        var withheld = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(withheld.InSync);
        Assert.Contains("did not say which process", withheld.Observed, StringComparison.Ordinal);
        Assert.DoesNotContain("no process", withheld.Observed, StringComparison.Ordinal);

        // A number that cannot be a process id is the same non-answer wearing digits, and must not
        // reach the delta as though systemd had named a process.
        systemd.Withheld.Clear();
        systemd.MainPid = -1;
        var nonsense = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.False(nonsense.InSync);
        Assert.Contains("did not say which process", nonsense.Observed, StringComparison.Ordinal);
        Assert.DoesNotContain("-1", nonsense.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_systemd_that_answers_nothing_is_told_apart_from_a_broken_unit()
    {
        // Not "the unit is wrong" — nothing was asked successfully. The two have to stay apart, or
        // a systemd that would not answer reads as a broken unit and the Act reloads a manager that
        // was never observed.
        var systemd = new FakeAgentUnit { Answers = false };
        var resource = new AgentUnitRunningResource(systemd, () => 4242);

        var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);

        Assert.False(observation.InSync);
        Assert.Contains("said nothing", observation.Observed, StringComparison.Ordinal);
        Assert.Contains("Failed to connect to bus", observation.Observed, StringComparison.Ordinal);
        Assert.DoesNotContain("NeedDaemonReload", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void The_three_resources_carry_the_browser_chains_edges_and_are_reached_before_adoption()
    {
        using var files = new TemporaryFiles();
        var graph = DeviceCatalog.BuildGraph(AgentResourceGraphTests.Context(files));
        var order = graph.Ordered.Select(resource => resource.Name).ToList();

        // Mirroring unit.chromium-kiosk.*: the content depends on nothing, the enablement on the
        // content, and the running-versus-content on both.
        Assert.Empty(graph.Find(AgentUnitResource.ResourceName)!.DependsOn);
        Assert.Equal(
            [AgentUnitResource.ResourceName],
            graph.Find(AgentUnitEnabledResource.ResourceName)!.DependsOn);
        Assert.Equal(
            [AgentUnitResource.ResourceName, AgentUnitEnabledResource.ResourceName],
            graph.Find(AgentUnitRunningResource.ResourceName)!.DependsOn);

        // An escalation stops the pass (decision 68), so a resource declared late is one a frame
        // with an earlier fault never gets acted on — and this is the repair with a deadline.
        // Behind the journal so the repair leaves a record on the card, ahead of everything else.
        Assert.True(order.IndexOf(JournalStorageResource.ResourceName)
            < order.IndexOf(AgentUnitResource.ResourceName));
        Assert.True(order.IndexOf(AgentUnitRunningResource.ResourceName)
            < order.IndexOf(AdoptionResource.ResourceName));

        // Not gates. Writing the file, enabling the unit and reloading systemd are all real Acts
        // that converge on the first attempt, which is exactly the distinction IResource.IsGate
        // draws.
        Assert.False(graph.Find(AgentUnitResource.ResourceName)!.IsGate);
        Assert.False(graph.Find(AgentUnitEnabledResource.ResourceName)!.IsGate);
        Assert.False(graph.Find(AgentUnitRunningResource.ResourceName)!.IsGate);
    }

    [Fact]
    public void The_two_registers_of_the_repair_screen_say_the_repair_has_a_deadline()
    {
        // §2.7's plain half. A person reading these has to understand that the frame is working
        // now and will not be after the next restart, without being told to panic — and without
        // being told what a systemd unit is.
        using var files = new TemporaryFiles();

        // Read through the interface, because the four members below are what the repair screen
        // and the Fleet Manager row are built from and nothing else about these types matters.
        IResource[] resources =
        [
            new AgentUnitResource(files.Files, new FakeAgentUnit()),
            new AgentUnitEnabledResource(new FakeAgentUnit()),
            new AgentUnitRunningResource(new FakeAgentUnit()),
        ];

        Assert.Contains("still running", resources[0].WhyItMatters, StringComparison.Ordinal);
        Assert.Contains(
            "would not come back after the next restart",
            resources[1].WhyItMatters,
            StringComparison.Ordinal);
        Assert.Contains("nothing would be left to say so", resources[1].WhyItMatters, StringComparison.Ordinal);
        Assert.Contains("only what is running now", resources[2].WhyItMatters, StringComparison.Ordinal);

        foreach (var resource in resources)
        {
            Assert.DoesNotContain("systemd", resource.Detected, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("unit", resource.Detected, StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>
/// systemd, for the three resources that ask it about <c>fl-agent.service</c>.
/// </summary>
/// <remarks>
/// It holds state rather than scripting answers, so an Act genuinely changes what the next Observe
/// reads: <c>enable</c> moves the enablement and <c>daemon-reload</c> clears
/// <c>NeedDaemonReload</c>, which is what lets the resources converge through a harness instead of
/// agreeing with a canned second answer.
/// </remarks>
internal sealed class FakeAgentUnit : ISystemControl
{
    /// <summary>Every argument vector this has been asked to run, in order.</summary>
    public List<string> Commands { get; } = [];

    /// <summary>What <c>systemctl is-enabled</c> answers.</summary>
    public string Enablement { get; set; } = SystemdUnits.EnabledState;

    /// <summary>Which file systemd says it loaded.</summary>
    public string FragmentPath { get; set; } = AgentUnitResource.UnitPath;

    /// <summary>Whether the file on disk has moved since systemd read it.</summary>
    public string NeedDaemonReload { get; set; } = AgentUnitRunningResource.NoReloadNeeded;

    /// <summary>The pid systemd says it runs the unit as.</summary>
    public int MainPid { get; set; } = Environment.ProcessId;

    /// <summary>The coarse lifecycle state.</summary>
    public string ActiveState { get; set; } = "active";

    /// <summary>The fine-grained lifecycle state.</summary>
    public string SubState { get; set; } = "running";

    /// <summary>Properties <c>systemctl show</c> is asked for and does not answer.</summary>
    /// <remarks>
    /// systemd prints a line per property it knows and simply omits one it does not, so a reader
    /// has to tell an absent property from a present one — which is the difference between
    /// not knowing which process runs the unit and knowing that no process does.
    /// </remarks>
    public HashSet<string> Withheld { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether systemd can be reached at all.</summary>
    public bool Answers { get; set; } = true;

    public Task<SystemControlResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        Commands.Add(string.Join(' ', arguments));

        if (!Answers)
        {
            return Answer(false, "Failed to connect to bus: No such file or directory");
        }

        switch (arguments[0])
        {
            case "is-enabled":
                return Answer(
                    string.Equals(Enablement, SystemdUnits.EnabledState, StringComparison.Ordinal),
                    Enablement);

            case "enable":
                if (SystemdUnits.IsMasked(Enablement))
                {
                    // The refusal that makes a mask worth telling apart at all.
                    return Answer(false, "Failed to enable unit: Unit file fl-agent.service is masked.");
                }

                Enablement = SystemdUnits.EnabledState;
                return Answer(true, string.Empty);

            case "daemon-reload":
                NeedDaemonReload = AgentUnitRunningResource.NoReloadNeeded;
                return Answer(true, string.Empty);

            case "show":
                return Answer(true, Show(arguments));

            default:
                return Answer(true, string.Empty);
        }
    }

    /// <summary>
    /// One <c>Property=value</c> line per property, in the order asked — which is what real
    /// <c>systemctl show -p</c> prints, and what makes the resource parse a shape it will meet.
    /// </summary>
    private string Show(IReadOnlyList<string> arguments)
    {
        var lines = new List<string>();

        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (!string.Equals(arguments[index], "-p", StringComparison.Ordinal))
            {
                continue;
            }

            var name = arguments[index + 1];
            if (Withheld.Contains(name))
            {
                continue;
            }

            lines.Add(name + "=" + name switch
            {
                AgentUnitRunningResource.MainPidProperty =>
                    MainPid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                AgentUnitRunningResource.ActiveStateProperty => ActiveState,
                AgentUnitRunningResource.SubStateProperty => SubState,
                AgentUnitRunningResource.NeedDaemonReloadProperty => NeedDaemonReload,
                AgentUnitRunningResource.FragmentPathProperty => FragmentPath,
                _ => string.Empty,
            });
        }

        return string.Join('\n', lines);
    }

    private static Task<SystemControlResult> Answer(bool succeeded, string output) =>
        Task.FromResult(new SystemControlResult(succeeded, output));
}
