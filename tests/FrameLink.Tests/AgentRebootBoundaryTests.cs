using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.State;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// §2.4's reboot-verified apply: <b>"Applied" is never claimed from a successful write, only
/// from an observation after the setting had to survive a boot.</b>
/// </summary>
/// <remarks>
/// The whole cross-reboot sequence is exercised here without anything rebooting, which is the
/// point of <see cref="IRebootBoundary"/> being a seam. What is <i>not</i> simulated is a real
/// <c>systemctl reboot</c> on a real frame; that is stated plainly rather than implied.
/// </remarks>
public sealed class AgentRebootBoundaryTests
{
    [Fact]
    public async Task A_change_is_journalled_before_the_reboot_is_requested()
    {
        // The window between "the machine is going down" and "the journal is on disk" is the one
        // window in which an agent could lose track of what it was doing.
        var resource = new ScriptedResource("spy", "want", "have-not");
        using var harness = new ReconcileHarness(resource);
        PendingApply? journalledAtCrossing = null;

        harness.Boundary.OnBoot = (_, _) =>
        {
            journalledAtCrossing = harness.Journal.Read().Pending;
            return Task.CompletedTask;
        };

        await harness.PassAsync();

        Assert.NotNull(journalledAtCrossing);
        Assert.Equal("spy", journalledAtCrossing.Resource);
        Assert.Equal(1, journalledAtCrossing.Attempt);
        Assert.Equal("set spy to want", journalledAtCrossing.Change);
        Assert.Equal("boot-1", journalledAtCrossing.BootId);
    }

    [Fact]
    public async Task The_resource_is_awaiting_reboot_while_the_boundary_is_being_crossed()
    {
        // The status vocabulary's AwaitingReboot rung, reached where §2.3 says it is: written,
        // not yet proven.
        var resource = new ScriptedResource("spy", "want", "have-not");
        using var harness = new ReconcileHarness(resource);
        var seen = new List<string>();

        harness.Boundary.OnBoot = (_, _) =>
        {
            seen.Add(harness.Hub.Current.Reconcile.LoopState ?? "none");
            seen.AddRange(harness.Telemetry.Reports.Select(report => report.LoopState));
            return Task.CompletedTask;
        };

        await harness.PassAsync();

        Assert.Contains(LoopStateNames.AwaitingReboot, seen, StringComparer.Ordinal);
        Assert.Contains(
            harness.Telemetry.Reports,
            report => report.Resources.Any(item =>
                string.Equals(item.Status, ResourceStatusNames.AwaitingReboot, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_process_restart_that_is_not_a_reboot_proves_nothing_and_asks_again()
    {
        // The most important test in this file. Restart=always means the agent comes back from a
        // crash looking exactly like it came back from a boot; without the boot-identity compare
        // the agent would re-read the value it had just written, find it correct, and report
        // InSync. That is precisely the write-only check the hostname trap defeats.
        var resource = new ScriptedResource("spy", "want", "have-not");
        using var harness = new ReconcileHarness(resource);

        // Journal a pending apply by hand, stamped with the boot that is still current — which is
        // exactly what a crash mid-contract leaves behind.
        harness.Journal.Update(state => state with
        {
            Pending = new PendingApply
            {
                Resource = "spy",
                Attempt = 1,
                Expected = "want",
                Change = "set spy to want",
                BootId = harness.Boot.Current,
                WrittenUtc = harness.Clock.UtcNow,
            },
            LastBootId = harness.Boot.Current,
        });

        var outcome = await harness.PassAsync();

        Assert.Single(harness.Boundary.Crossings);
        Assert.Equal(1, harness.Boundary.Crossings[0].Attempt);
        Assert.Contains("restarted without a reboot", harness.Log.Transcript, StringComparison.Ordinal);
        Assert.Equal(PassResult.Rebooted, outcome.Result);
    }

    [Fact]
    public async Task A_verify_after_a_real_boot_clears_the_journal_and_reports_in_sync()
    {
        var resource = new ScriptedResource("spy", "want", "have-not");
        using var harness = new ReconcileHarness(resource);

        var outcome = await harness.PassAsync();

        Assert.Equal(ResourceStatusKind.InSync, ReconcileHarness.StatusOf(outcome, "spy").Kind);
        Assert.Null(harness.Journal.Read().Pending);
    }

    [Fact]
    public async Task A_change_that_the_boot_undoes_is_caught_rather_than_believed()
    {
        // cloud-init's shape, in miniature: the write succeeds, the verify happens on the far
        // side of a boot, and something else has put the value back.
        var resource = new ScriptedResource("spy", "want", "have-not") { RevertedAtBoot = true };
        using var harness = new ReconcileHarness(resource);
        harness.Boundary.OnBoot = (_, _) =>
        {
            resource.Boot();
            return Task.CompletedTask;
        };

        var outcome = await harness.PassAsync();
        var status = ReconcileHarness.StatusOf(outcome, "spy");

        Assert.NotEqual(ResourceStatusKind.InSync, status.Kind);
        Assert.Contains("reverted-by-someone-else", status.Delta!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pending_apply_survives_the_agent_being_replaced_by_a_new_process()
    {
        // §2.1: persisted state is data, never touched by an update. A journal that did not
        // outlive the process would make the whole reboot-verified apply unimplementable.
        var resource = new ScriptedResource("spy", "want", "have-not");
        using var first = new ReconcileHarness(resource);

        first.Journal.Update(state => state with
        {
            Pending = new PendingApply
            {
                Resource = "spy",
                Attempt = 2,
                Expected = "want",
                Change = "set spy to want",
                BootId = "boot-0",
                WrittenUtc = first.Clock.UtcNow,
            },
        });

        // A second journal over the same directory is what the next process sees.
        var reread = new ReconcileJournal(first.Store, first.Log).Read();

        Assert.NotNull(reread.Pending);
        Assert.Equal("spy", reread.Pending.Resource);
        Assert.Equal(2, reread.Pending.Attempt);
        Assert.Equal("boot-0", reread.Pending.BootId);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_reboot_the_machine_accepts_ends_the_pass_and_claims_nothing()
    {
        // The production path: systemctl reboot returns, the process is about to be killed, and
        // the loop must not go on to verify anything.
        var resource = new ScriptedResource("spy", "want", "have-not");
        using var harness = new ReconcileHarness(resource);

        var outcome = await new ReconcileLoop(Services(harness, new StubBoundary(RebootCrossing.Restarting)))
            .RunPassAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PassResult.Restarting, outcome.Result);
        Assert.Equal(ResourceStatusKind.AwaitingReboot, ReconcileHarness.StatusOf(outcome, "spy").Kind);
        Assert.NotNull(harness.Journal.Read().Pending);
        Assert.Equal(1, resource.Observations);
    }

    [Fact]
    public async Task A_reboot_the_machine_refuses_costs_an_attempt_rather_than_hanging()
    {
        // A frame that can never reboot has to reach a human rather than sit forever claiming to
        // be mid-apply.
        var resource = new ScriptedResource("spy", "want", "have-not");
        using var harness = new ReconcileHarness(resource);

        var loop = new ReconcileLoop(Services(harness, new StubBoundary(RebootCrossing.Refused, "no such method")));
        var outcome = await loop.RunPassAsync(TestContext.Current.CancellationToken);
        var status = ReconcileHarness.StatusOf(outcome, "spy");

        Assert.Equal(1, status.Attempts);
        Assert.Contains("reboot", status.Delta!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(harness.Journal.Read().Pending);
    }

    [Fact]
    public async Task The_boot_that_carried_a_change_is_announced_on_the_events_channel()
    {
        // §4.1 lists boot alongside drift and escalation. Without a boot marker in the stream,
        // "escalated twice in one boot" and "escalated once on each of two boots" read the same.
        var resource = new ScriptedResource("spy", "want", "have-not") { ActHasNoEffect = true };
        using var harness = new ReconcileHarness(resource);

        await harness.PassAsync();
        harness.Clock.UtcNow += TimeSpan.FromMinutes(5);
        await harness.PassAsync();

        var boots = harness.Telemetry.OfKind(DeviceEventKinds.Boot).ToList();

        Assert.NotEmpty(boots);
        Assert.Contains(boots, item => item.Summary.Contains("Booted", StringComparison.Ordinal));
    }

    private static ReconcileServices Services(ReconcileHarness harness, IRebootBoundary boundary) => new()
    {
        Graph = harness.Graph,
        Journal = harness.Journal,
        Boot = harness.Boot,
        Reboots = boundary,
        Countdown = harness.Countdown,
        Telemetry = harness.Telemetry,
        Hub = harness.Hub,
        Clock = harness.Clock,
        Log = harness.Log,
        Options = new ReconcileOptions { Countdown = TimeSpan.Zero },
    };

    private sealed class StubBoundary(RebootCrossing crossing, string? detail = null) : IRebootBoundary
    {
        public Task<RebootOutcome> CrossAsync(RebootRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new RebootOutcome(crossing, detail));
    }
}

/// <summary>The boot-identity seam itself.</summary>
public sealed class AgentBootIdentityTests
{
    [Fact]
    public void The_kernel_boot_id_is_read_from_proc_and_held_for_the_life_of_the_process()
    {
        var files = new MemoryTextFiles();
        files.Files[KernelBootIdentity.Path] = "  d1e4b1c0-0000-4000-8000-000000000001\n";

        var identity = new KernelBootIdentity(files);
        var first = identity.Current;

        files.Files[KernelBootIdentity.Path] = "different";

        // Read once, on purpose: if it could change while this process lives, the process would
        // not be alive to notice.
        Assert.Equal("d1e4b1c0-0000-4000-8000-000000000001", first);
        Assert.Equal(first, identity.Current);
    }

    [Fact]
    public void A_machine_with_no_kernel_boot_id_gets_a_value_that_differs_per_process()
    {
        // The conservative answer rather than the convenient one: on such a host every process
        // start looks like a reboot, so a resource is verified more often than needed, never less.
        var files = new MemoryTextFiles();

        Assert.NotEqual(new KernelBootIdentity(files).Current, new KernelBootIdentity(files).Current);
    }
}
