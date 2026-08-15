using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Tests;

/// <summary>
/// The resource contract — version2.md §2.3: <b>Observe → Compare → Act (only on drift) → Verify →
/// Status</b>.
/// </summary>
/// <remarks>
/// M1 proves the contract with one resource, not the engine. The DAG, the retry schedule, the
/// reboot-verified apply and the escalation ladder are M2 (§5.1), and a separate workstream is
/// cataloguing the real resources right now.
/// </remarks>
public sealed class AgentReconcileTests
{
    [Fact]
    public async Task An_already_converged_resource_is_observed_and_left_alone()
    {
        // §2.2's level-triggered rule: the agent never "runs an installer", it converges. Running
        // this on a healthy frame has to be a no-op, or every pass would be a change.
        var resource = new SpyResource(desired: "Hallway", observed: "Hallway");

        var status = await new Reconciler(NullLog.Instance)
            .ReconcileAsync(resource, TestContext.Current.CancellationToken);

        Assert.Equal(ResourceStatusKind.InSync, status.Kind);
        Assert.Equal(0, resource.Acts);
        Assert.Equal(1, resource.Observations);
    }

    [Fact]
    public async Task A_drifted_resource_is_acted_on_once_and_then_verified()
    {
        var resource = new SpyResource(desired: "Hallway", observed: string.Empty);

        var status = await new Reconciler(NullLog.Instance)
            .ReconcileAsync(resource, TestContext.Current.CancellationToken);

        Assert.Equal(ResourceStatusKind.InSync, status.Kind);
        Assert.Equal(1, resource.Acts);
        Assert.Equal(1, status.Attempts);

        // Two observations: the one that found the drift, and the one that proved it was fixed.
        // §2.3 requires Verify to be the same implementation as Observe — a check written against
        // "did the write succeed" is exactly how v1's governor bug reported success while the
        // setting was quietly wrong.
        Assert.Equal(2, resource.Observations);
    }

    [Fact]
    public async Task A_resource_that_does_not_stick_is_reported_with_its_exact_delta()
    {
        // §2.5: the failure is marked with the exact expected-versus-observed delta and the attempt
        // count, because that is what the Fleet Manager shows the operator instead of "failed".
        var resource = new SpyResource(desired: "Hallway", observed: string.Empty) { ActHasNoEffect = true };

        var status = await new Reconciler(NullLog.Instance)
            .ReconcileAsync(resource, TestContext.Current.CancellationToken);

        Assert.Equal(ResourceStatusKind.Degraded, status.Kind);
        Assert.Equal(1, status.Attempts);
        Assert.Contains("Hallway", status.Delta!, StringComparison.Ordinal);
        Assert.NotNull(status.Action);
    }

    [Fact]
    public async Task The_change_that_was_made_is_carried_back_for_the_screen()
    {
        // §2.7 item 3: the repair screen shows "the exact command or change, plus a plain-language
        // gloss". The exact change can only come from the resource that made it.
        var resource = new SpyResource(desired: "Hallway", observed: string.Empty);

        var status = await new Reconciler(NullLog.Instance)
            .ReconcileAsync(resource, TestContext.Current.CancellationToken);

        Assert.Equal("set the name to Hallway", status.Action);
    }

    [Fact]
    public async Task The_device_name_resource_writes_a_value_an_operator_can_read_back()
    {
        using var temporary = new TemporaryStore();
        var resource = new DeviceNameResource(temporary.Store, () => "Hallway");

        var status = await new Reconciler(NullLog.Instance)
            .ReconcileAsync(resource, TestContext.Current.CancellationToken);

        Assert.Equal(ResourceStatusKind.InSync, status.Kind);
        Assert.Equal("Hallway", temporary.Store.ReadText(DeviceNameResource.FileName));
    }

    [Fact]
    public async Task The_device_name_resource_corrects_drift_on_the_next_pass()
    {
        using var temporary = new TemporaryStore();
        var desired = "Hallway";
        var resource = new DeviceNameResource(temporary.Store, () => desired);
        var reconciler = new Reconciler(NullLog.Instance);
        await reconciler.ReconcileAsync(resource, TestContext.Current.CancellationToken);

        // The operator renames the frame in the Fleet Manager; the value the agent holds changes
        // and the file has to follow it.
        desired = "Kitchen";
        var status = await reconciler.ReconcileAsync(resource, TestContext.Current.CancellationToken);

        Assert.Equal(ResourceStatusKind.InSync, status.Kind);
        Assert.Equal(1, status.Attempts);
        Assert.Equal("Kitchen", temporary.Store.ReadText(DeviceNameResource.FileName));
    }

    [Fact]
    public async Task Reconciling_an_unchanged_device_name_writes_nothing()
    {
        using var temporary = new TemporaryStore();
        var resource = new DeviceNameResource(temporary.Store, () => "Hallway");
        var reconciler = new Reconciler(NullLog.Instance);
        await reconciler.ReconcileAsync(resource, TestContext.Current.CancellationToken);
        var writtenAt = File.GetLastWriteTimeUtc(temporary.Store.PathOf(DeviceNameResource.FileName));

        var status = await reconciler.ReconcileAsync(resource, TestContext.Current.CancellationToken);

        Assert.Equal(ResourceStatusKind.InSync, status.Kind);
        Assert.Equal(0, status.Attempts);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(temporary.Store.PathOf(DeviceNameResource.FileName)));
    }

    [Fact]
    public async Task An_unnamed_device_converges_on_an_empty_name_rather_than_churning()
    {
        // A frame adopted without a display name would otherwise be permanently "drifted", which
        // under §2.6 means permanently not green — for a field the operator simply left blank.
        using var temporary = new TemporaryStore();
        var resource = new DeviceNameResource(temporary.Store, () => null);
        var reconciler = new Reconciler(NullLog.Instance);

        var first = await reconciler.ReconcileAsync(resource, TestContext.Current.CancellationToken);
        var second = await reconciler.ReconcileAsync(resource, TestContext.Current.CancellationToken);

        Assert.Equal(ResourceStatusKind.InSync, first.Kind);
        Assert.Equal(ResourceStatusKind.InSync, second.Kind);
        Assert.Equal(0, second.Attempts);
    }

    private sealed class SpyResource : IResource
    {
        private readonly string _desired;
        private string _observed;

        public SpyResource(string desired, string observed)
        {
            _desired = desired;
            _observed = observed;
        }

        public bool ActHasNoEffect { get; init; }

        public int Observations { get; private set; }

        public int Acts { get; private set; }

        public string Name => "spy";

        public string Detected => "The spy value is wrong.";

        public string WhyItMatters => "Because this is a test.";

        public ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
        {
            Observations++;
            return ValueTask.FromResult(new ResourceObservation(
                string.Equals(_desired, _observed, StringComparison.Ordinal),
                _desired,
                _observed));
        }

        public ValueTask<string> ActAsync(CancellationToken cancellationToken)
        {
            Acts++;
            if (!ActHasNoEffect)
            {
                _observed = _desired;
            }

            return ValueTask.FromResult($"set the name to {_desired}");
        }
    }
}
