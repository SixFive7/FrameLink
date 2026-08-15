using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// The resource contract — version2.md §2.3: <b>Observe → Compare → Act (only on drift) → Verify →
/// Status</b>.
/// </summary>
/// <remarks>
/// Driven through <see cref="ReconcileLoop"/> rather than through a separate single-resource
/// runner, because M2 deletes the second path. §2.2 requires provisioning and repair to be the
/// same code; a convenience wrapper beside the engine would have been a second one.
/// </remarks>
public sealed class AgentReconcileTests
{
    [Fact]
    public async Task An_already_converged_resource_is_observed_and_left_alone()
    {
        // §2.2's level-triggered rule: the agent never "runs an installer", it converges. Running
        // this on a healthy frame has to be a no-op, or every pass would be a change.
        var resource = new ScriptedResource("spy", desired: "Hallway", observed: "Hallway");
        using var harness = new ReconcileHarness(resource);

        var outcome = await harness.PassAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.Equal(ResourceStatusKind.InSync, ReconcileHarness.StatusOf(outcome, "spy").Kind);
        Assert.Equal(0, resource.Acts);
        Assert.Equal(1, resource.Observations);
        Assert.Empty(harness.Boundary.Crossings);
    }

    [Fact]
    public async Task A_drifted_resource_is_acted_on_once_and_then_verified_across_a_reboot()
    {
        var resource = new ScriptedResource("spy", desired: "Hallway", observed: string.Empty);
        using var harness = new ReconcileHarness(resource);

        var outcome = await harness.PassAsync();

        Assert.Equal(PassResult.Rebooted, outcome.Result);
        Assert.Equal(ResourceStatusKind.InSync, ReconcileHarness.StatusOf(outcome, "spy").Kind);
        Assert.Equal(1, resource.Acts);

        // Two observations: the one that found the drift, and the one that proved it was fixed.
        // §2.3 requires Verify to be the same implementation as Observe — a check written against
        // "did the write succeed" is exactly how v1's governor bug reported success while the
        // setting was quietly wrong.
        Assert.Equal(2, resource.Observations);

        // And §2.4: the verify happened on the far side of a reboot, not on the near side.
        Assert.Single(harness.Boundary.Crossings);
        Assert.Equal(1, harness.Boot.Boots);
    }

    [Fact]
    public async Task A_resource_that_does_not_stick_is_reported_with_its_exact_delta()
    {
        // §2.5: the failure is marked with the exact expected-versus-observed delta and the attempt
        // count, because that is what the Fleet Manager shows the operator instead of "failed".
        var resource = new ScriptedResource("spy", desired: "Hallway", observed: string.Empty)
        {
            ActHasNoEffect = true,
        };

        using var harness = new ReconcileHarness(resource);
        var outcome = await harness.PassAsync();
        var status = ReconcileHarness.StatusOf(outcome, "spy");

        Assert.Equal(1, status.Attempts);
        Assert.Equal(5, status.AttemptBudget);
        Assert.Contains("Hallway", status.Delta!, StringComparison.Ordinal);
        Assert.NotNull(status.Action);
    }

    [Fact]
    public async Task The_change_that_was_made_is_carried_back_for_the_screen_with_its_gloss()
    {
        // §2.7 item 3: the repair screen shows "the exact command or change, plus a plain-language
        // gloss". Both can only come from the resource that made the change.
        var resource = new ScriptedResource("spy", desired: "Hallway", observed: string.Empty);
        using var harness = new ReconcileHarness(resource);

        var outcome = await harness.PassAsync();
        var status = ReconcileHarness.StatusOf(outcome, "spy");

        Assert.Equal("set spy to Hallway", status.Action);
        Assert.Equal("Setting spy to Hallway in words a person can read.", status.Gloss);
    }

    [Fact]
    public async Task The_device_name_resource_writes_a_value_an_operator_can_read_back()
    {
        using var files = new TemporaryStore();
        using var loop = new ReconcileHarness(
            new AdoptionResource(files.Store, () => true),
            new DeviceNameResource(files.Store, () => "Hallway"));

        var outcome = await loop.ConvergeAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.Equal("Hallway", files.Store.ReadText(DeviceNameResource.FileName));
        Assert.Equal(AdoptionResource.AdoptedMarker, files.Store.ReadText(AdoptionResource.FileName));
    }

    [Fact]
    public async Task The_device_name_resource_corrects_drift_on_a_later_pass()
    {
        var desired = "Hallway";
        using var files = new TemporaryStore();
        using var loop = new ReconcileHarness(
            new AdoptionResource(files.Store, () => true),
            new DeviceNameResource(files.Store, () => desired));

        await loop.ConvergeAsync();
        Assert.Equal("Hallway", files.Store.ReadText(DeviceNameResource.FileName));

        // The operator renames the frame in the Fleet Manager; the value the agent holds changes
        // and the file has to follow it.
        desired = "Kitchen";
        await loop.ConvergeAsync();

        Assert.Equal("Kitchen", files.Store.ReadText(DeviceNameResource.FileName));
    }

    [Fact]
    public async Task Reconciling_an_unchanged_device_name_writes_nothing()
    {
        using var files = new TemporaryStore();
        using var loop = new ReconcileHarness(
            new AdoptionResource(files.Store, () => true),
            new DeviceNameResource(files.Store, () => "Hallway"));

        await loop.ConvergeAsync();
        var writtenAt = File.GetLastWriteTimeUtc(files.Store.PathOf(DeviceNameResource.FileName));

        var outcome = await loop.PassAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(files.Store.PathOf(DeviceNameResource.FileName)));
    }

    [Fact]
    public async Task An_unnamed_device_converges_on_an_empty_name_rather_than_churning()
    {
        // A frame adopted without a display name would otherwise be permanently "drifted", which
        // under §2.6 means permanently not green — for a field the operator simply left blank.
        using var files = new TemporaryStore();
        using var loop = new ReconcileHarness(
            new AdoptionResource(files.Store, () => true),
            new DeviceNameResource(files.Store, () => null));

        await loop.ConvergeAsync();
        var second = await loop.PassAsync();

        Assert.Equal(PassResult.Converged, second.Result);
        Assert.All(second.Statuses, status => Assert.Equal(ResourceStatusKind.InSync, status.Kind));
    }

    [Fact]
    public async Task A_pass_acts_on_one_resource_at_a_time_but_still_reports_every_one()
    {
        // §1.2.5, one diagnosis per change: only one resource is acted on. The others are still
        // observed, or the Fleet Manager's picture would have holes in it exactly when a frame is
        // busy converging.
        var first = new ScriptedResource("a", "want", "have-not");
        var second = new ScriptedResource("b", "want", "have-not");
        using var harness = new ReconcileHarness(first, second);

        var outcome = await harness.PassAsync();

        Assert.Equal(1, first.Acts);
        Assert.Equal(0, second.Acts);
        Assert.Equal(2, outcome.Statuses.Count);
        Assert.Contains(outcome.Statuses, status => string.Equals(status.Name, "b", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_pass_publishes_a_report_that_counts_the_reboots_still_to_come()
    {
        // §3.5: "reboots expected before convergence". §2.4 reboots per resource with no
        // exceptions, so the number is simply what is not yet verified — and it is the only
        // honest answer to "how long will this take".
        using var harness = new ReconcileHarness(
            new ScriptedResource("a", "want", "want"),
            new ScriptedResource("b", "want", "have-not"),
            new ScriptedResource("c", "want", "have-not"));

        await harness.PassAsync();
        var report = harness.Telemetry.Latest;

        Assert.NotNull(report);
        Assert.Equal(3, report.Resources.Count);
        Assert.True(report.RebootsExpected >= 1);
        Assert.Contains(report.Resources, item => string.Equals(item.Status, ResourceStatusNames.InSync, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_observe_that_throws_is_reported_as_drift_rather_than_crashing_the_loop()
    {
        // §1.2.3 bans a generic failure bucket. An exception becomes the observed value and
        // travels to the screen and the Fleet Manager, where somebody can read it.
        using var harness = new ReconcileHarness(new ThrowingResource());

        var outcome = await harness.PassAsync();
        var status = ReconcileHarness.StatusOf(outcome, "throws");

        Assert.NotEqual(ResourceStatusKind.InSync, status.Kind);
        Assert.Contains("the sysfs node is not there", status.Delta!, StringComparison.Ordinal);
    }

    private sealed class ThrowingResource : IResource
    {
        public string Name => "throws";

        public string Detected => "Something cannot be read.";

        public string WhyItMatters => "Because this is a test.";

        public ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken) =>
            throw new IOException("the sysfs node is not there");

        public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ResourceAction("nothing", "nothing"));
    }
}
