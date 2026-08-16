using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.State;
using FrameLink.Agent.Telemetry;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// A whole reconciliation loop, wired to run in a millisecond on a workstation.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is a mock of the loop. It is the shipping <see cref="ReconcileLoop"/> with the
/// three seams that make a frame a frame pointed somewhere testable: the reboot boundary
/// (<see cref="InProcessRebootBoundary"/>, which crosses without restarting and runs
/// <see cref="InProcessRebootBoundary.OnBoot"/> in the gap), the clock, and the state
/// directory. Everything else — the journal format, the ledger arithmetic, the escalation
/// ladder, the telemetry shapes — is the code that ships.
/// </para>
/// <para>
/// <see cref="Boot"/> being separate from the boundary is what lets a test model the case §2.4
/// exists for: an agent that restarted without the machine rebooting.
/// </para>
/// </remarks>
internal sealed class ReconcileHarness : IDisposable
{
    private readonly TemporaryStore _store = new();

    public ReconcileHarness(params IResource[] resources)
        : this(new ReconcileOptions { Countdown = TimeSpan.Zero }, resources)
    {
    }

    public ReconcileHarness(ReconcileOptions options, params IResource[] resources)
        : this(options, boot: null, resources)
    {
    }

    /// <summary>
    /// Builds a loop over a boot identity the caller already holds.
    /// </summary>
    /// <remarks>
    /// Needed by anything that shares the boot identity with something outside the loop — a
    /// <c>BootPartitionGuard</c>, above all, whose boot counting is only meaningful if it and the
    /// loop agree on what a boot is.
    /// </remarks>
    public ReconcileHarness(ReconcileOptions options, MutableBootIdentity? boot, params IResource[] resources)
    {
        Boot = boot ?? new MutableBootIdentity();
        Boundary = new InProcessRebootBoundary(Boot);
        Journal = new ReconcileJournal(_store.Store, Log);
        Graph = new ResourceGraph(resources);
        Hub = new AgentStatusHub(AgentStatusFactory.Starting());
        Countdown = new RebootCountdown(Clock);

        Loop = new ReconcileLoop(new ReconcileServices
        {
            Graph = Graph,
            Journal = Journal,
            Boot = Boot,
            Reboots = Boundary,
            Countdown = Countdown,
            Telemetry = Telemetry,
            Hub = Hub,
            Clock = Clock,
            Log = Log,
            Options = options,
        })
        {
            DeviceId = "TEST-DEVI-CEID-0001",
        };
    }

    public ManualClock Clock { get; } = new();

    public RecordingLog Log { get; } = new();

    public RecordingTelemetry Telemetry { get; } = new();

    public MutableBootIdentity Boot { get; }

    public InProcessRebootBoundary Boundary { get; }

    public ReconcileJournal Journal { get; }

    public ResourceGraph Graph { get; }

    public AgentStatusHub Hub { get; }

    public RebootCountdown Countdown { get; }

    public ReconcileLoop Loop { get; }

    public IStateStore Store => _store.Store;

    /// <summary>The state directory, so a second loop can be built over the same journal.</summary>
    public string Root => _store.Root;

    /// <summary>Runs one pass.</summary>
    public Task<PassOutcome> PassAsync() => Loop.RunPassAsync(TestContext.Current.CancellationToken);

    /// <summary>Runs passes until nothing changes, or <paramref name="limit"/> is reached.</summary>
    /// <remarks>
    /// Bounded, because a loop that will not converge is the failure this suite is most
    /// interested in and a test that hangs waiting for it reports nothing at all.
    /// </remarks>
    public async Task<PassOutcome> ConvergeAsync(int limit = 30)
    {
        PassOutcome outcome = null!;

        for (var pass = 0; pass < limit; pass++)
        {
            outcome = await PassAsync();

            if (outcome.Result is PassResult.Converged or PassResult.Restarting)
            {
                return outcome;
            }

            if (outcome.Result is PassResult.Pending or PassResult.Escalated && outcome.NextAttemptUtc is { } next)
            {
                // The driver's own wait, applied by hand so a backoff of half an hour costs a
                // test nothing while still being the schedule the loop actually chose.
                Clock.UtcNow = next;
            }
            else if (outcome.Result is PassResult.Pending or PassResult.Escalated)
            {
                return outcome;
            }
        }

        return outcome;
    }

    /// <summary>The status of one resource in an outcome.</summary>
    public static ResourceStatus StatusOf(PassOutcome outcome, string name) =>
        outcome.Statuses.Single(status => string.Equals(status.Name, name, StringComparison.Ordinal));

    public void Dispose() => _store.Dispose();
}

/// <summary>A telemetry sink that records, and can be told whether the link is up.</summary>
/// <remarks>
/// The <see cref="Connected"/> flag is the whole point. §2.5's <c>Escalated(admin-notified)</c>
/// is only true if the notification reached the Fleet Manager, so a sink that always claimed
/// delivery would make <c>Degraded</c> unreachable and would let an offline frame assert that
/// somebody had been told.
/// </remarks>
internal sealed class RecordingTelemetry : IReconcileTelemetry
{
    public List<ReconcileReport> Reports { get; } = [];

    public List<DeviceEvent> Events { get; } = [];

    public bool Connected { get; set; }

    public ReconcileReport? Latest => Reports.Count == 0 ? null : Reports[^1];

    public IEnumerable<DeviceEvent> OfKind(string kind) =>
        Events.Where(item => string.Equals(item.Kind, kind, StringComparison.Ordinal));

    public ValueTask ReportAsync(ReconcileReport report, CancellationToken cancellationToken)
    {
        Reports.Add(report);
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> EventAsync(DeviceEvent deviceEvent, CancellationToken cancellationToken)
    {
        Events.Add(deviceEvent);
        return ValueTask.FromResult(Connected);
    }
}

/// <summary>
/// A resource whose behaviour a test scripts outright.
/// </summary>
/// <remarks>
/// Used for the loop's own mechanics — ordering, budgets, the ladder — where the point is the
/// engine rather than any particular setting. The real resources are exercised separately
/// against a real filesystem in <c>AgentRealResourceTests</c>, because §7.2 asks for tests that
/// assert outcomes and a suite made only of scripted doubles asserts its own script.
/// </remarks>
internal sealed class ScriptedResource : IResource
{
    private readonly string _desired;
    private string _observed;

    public ScriptedResource(string name, string desired, string observed, params string[] dependsOn)
    {
        Name = name;
        _desired = desired;
        _observed = observed;
        DependsOn = dependsOn;
    }

    public string Name { get; }

    public IReadOnlyList<string> DependsOn { get; }

    public string Detected => $"The {Name} value is wrong.";

    public string WhyItMatters => "Because this is a test.";

    /// <summary>When true, Act changes nothing — the write-succeeded-and-is-wrong case.</summary>
    public bool ActHasNoEffect { get; set; }

    /// <summary>When true, the value is put back at the next boot — cloud-init's shape.</summary>
    public bool RevertedAtBoot { get; set; }

    /// <summary>
    /// When true, Observe cannot see anything at all — the Fleet Manager's shape (§2.6).
    /// </summary>
    /// <remarks>
    /// Distinct from a wrong observed value on purpose. This is the third outcome, and the loop
    /// has to treat it as neither success nor failure: no attempt spent, no reboot, no escalation.
    /// </remarks>
    public bool Unevaluable { get; set; }

    public int Observations { get; private set; }

    public int Acts { get; private set; }

    public ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        Observations++;

        if (Unevaluable)
        {
            return ValueTask.FromResult(ResourceObservation.Unevaluable(
                _desired,
                "the Fleet Manager has not answered"));
        }

        return ValueTask.FromResult(new ResourceObservation(
            string.Equals(_desired, _observed, StringComparison.Ordinal),
            _desired,
            _observed));
    }

    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        Acts++;
        if (!ActHasNoEffect)
        {
            _observed = _desired;
        }

        return ValueTask.FromResult(new ResourceAction(
            $"set {Name} to {_desired}",
            $"Setting {Name} to {_desired} in words a person can read."));
    }

    /// <summary>What the machine's other owners do while it boots.</summary>
    public void Boot()
    {
        if (RevertedAtBoot)
        {
            _observed = "reverted-by-someone-else";
        }
    }

    /// <summary>Puts this resource out of sync from outside the loop — ordinary drift.</summary>
    public void Drift() => _observed = "drifted-again";
}
