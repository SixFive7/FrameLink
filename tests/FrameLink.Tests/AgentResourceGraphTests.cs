using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;

namespace FrameLink.Tests;

/// <summary>
/// §2.2's explicit lightweight DAG, and §5.1's requirement that dependents be marked
/// <c>Blocked(dependency)</c> rather than left to fail confusingly on their own.
/// </summary>
public sealed class AgentResourceGraphTests
{
    [Fact]
    public void Resources_are_ordered_with_their_dependencies_first()
    {
        var graph = new ResourceGraph(
        [
            new ScriptedResource("governor", "x", "x", "enabled"),
            new ScriptedResource("enabled", "x", "x", "unit"),
            new ScriptedResource("unit", "x", "x"),
        ]);

        Assert.Equal(["unit", "enabled", "governor"], graph.Ordered.Select(resource => resource.Name));
    }

    [Fact]
    public void Independent_resources_keep_the_order_the_catalog_declared_them_in()
    {
        // §2.2 asks for sequential and single-threaded execution because "determinism beats
        // throughput on a 2 GB appliance". A sort that could return two different valid orders on
        // two boots would give half of that back, and the order is what puts the display first.
        var graph = new ResourceGraph(
        [
            new ScriptedResource("display", "x", "x"),
            new ScriptedResource("journal", "x", "x"),
            new ScriptedResource("adoption", "x", "x"),
        ]);

        Assert.Equal(["display", "journal", "adoption"], graph.Ordered.Select(resource => resource.Name));
    }

    [Fact]
    public void A_dependency_cycle_is_refused_at_construction_and_names_everyone_in_it()
    {
        // Detected at construction, not at run time: a cycle is a mistake in the compiled catalog
        // and can never be a condition a frame finds itself in.
        var thrown = Assert.Throws<ResourceGraphException>(() => new ResourceGraph(
        [
            new ScriptedResource("a", "x", "x", "c"),
            new ScriptedResource("b", "x", "x", "a"),
            new ScriptedResource("c", "x", "x", "b"),
        ]));

        Assert.Contains("cycle", thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a, b, c", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dependency_that_is_not_in_the_catalog_is_refused_at_construction()
    {
        var thrown = Assert.Throws<ResourceGraphException>(() => new ResourceGraph(
        [
            new ScriptedResource("a", "x", "x", "nowhere"),
        ]));

        Assert.Contains("nowhere", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_resources_with_the_same_id_are_refused_at_construction()
    {
        var thrown = Assert.Throws<ResourceGraphException>(() => new ResourceGraph(
        [
            new ScriptedResource("same", "x", "x"),
            new ScriptedResource("same", "x", "x"),
        ]));

        Assert.Contains("same", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dependent_of_a_drifted_resource_is_blocked_and_never_attempted()
    {
        var root = new ScriptedResource("root", "want", "have-not") { ActHasNoEffect = true };
        var dependent = new ScriptedResource("dependent", "want", "have-not", "root");
        using var harness = new ReconcileHarness(root, dependent);

        var outcome = await harness.PassAsync();
        var status = ReconcileHarness.StatusOf(outcome, "dependent");

        Assert.Equal(ResourceStatusKind.Blocked, status.Kind);
        Assert.Equal("root", status.BlockedBy);
        Assert.Equal(0, dependent.Acts);

        // Not observed either. §2.2's point is that a dependent must not "fail confusingly on its
        // own", and observing it would put a meaningless value in the delta the operator reads.
        Assert.Equal(0, dependent.Observations);
    }

    [Fact]
    public async Task A_blocked_resource_becomes_reachable_once_its_dependency_converges()
    {
        var root = new ScriptedResource("root", "want", "have-not");
        var dependent = new ScriptedResource("dependent", "want", "have-not", "root");
        using var harness = new ReconcileHarness(root, dependent);

        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.Equal(1, root.Acts);
        Assert.Equal(1, dependent.Acts);
        Assert.All(outcome.Statuses, status => Assert.Equal(ResourceStatusKind.InSync, status.Kind));

        // One reboot each, no exceptions (§2.4).
        Assert.Equal(2, harness.Boundary.Crossings.Count);
    }

    [Fact]
    public void The_shipped_catalog_orders_the_display_first_and_needs_nothing_to_do_it()
    {
        using var files = new TemporaryFiles();
        var graph = DeviceCatalog.BuildGraph(Context(files));

        Assert.Equal(DisplayPanelOverlayResource.ResourceName, graph.Ordered[0].Name);
        Assert.Equal(ConsoleRotationResource.ResourceName, graph.Ordered[1].Name);

        // The whole point of moving it: it is gated by nothing. A pending frame has to be able to
        // show its own fingerprint (§3.3), so an adoption dependency here would defeat the change.
        Assert.Empty(graph.Ordered[0].DependsOn);
        Assert.Equal([DisplayPanelOverlayResource.ResourceName], graph.Ordered[1].DependsOn);
    }

    [Fact]
    public void The_shipped_catalog_is_a_valid_dag_with_the_dependencies_the_catalog_document_states()
    {
        using var files = new TemporaryFiles();
        var graph = DeviceCatalog.BuildGraph(Context(files));

        // Nine from M2 plus the catalog's fifteen-resource package block (M3's first migration).
        Assert.Equal(24, graph.Count);
        Assert.Equal([AdoptionResource.ResourceName], graph.Find(HostnameResource.ResourceName)!.DependsOn);
        Assert.Equal(
            [CpuGovernorUnitResource.ResourceName],
            graph.Find(CpuGovernorUnitEnabledResource.ResourceName)!.DependsOn);
        Assert.Equal(
            [CpuGovernorUnitEnabledResource.ResourceName],
            graph.Find(CpuGovernorResource.ResourceName)!.DependsOn);

        // Ordering is a property of the graph, so the governor cannot precede its unit whatever
        // order the catalog happens to declare them in.
        var order = graph.Ordered.Select(resource => resource.Name).ToList();
        Assert.True(order.IndexOf(CpuGovernorUnitResource.ResourceName)
            < order.IndexOf(CpuGovernorUnitEnabledResource.ResourceName));
        Assert.True(order.IndexOf(CpuGovernorUnitEnabledResource.ResourceName)
            < order.IndexOf(CpuGovernorResource.ResourceName));
    }

    internal static DeviceCatalogContext Context(TemporaryFiles files) => new()
    {
        Files = files.Files,
        Store = files.Store,
        Processes = new RecordingProcessRunner(),
        SystemControl = new RecordingSystemControl(),
        Display = StaticDisplayProbe.Visible,
        Boot = new MutableBootIdentity(),
        Clock = new ManualClock(),
        Log = new RecordingLog(),
    };
}

/// <summary>
/// A real filesystem, rooted somewhere throwaway.
/// </summary>
/// <remarks>
/// The point of <see cref="HostSystemFiles"/> taking a root is that the resources are exercised
/// against the implementation that ships — real paths, real UTF-8 bytes, real directory
/// creation, real SHA-256 — rather than against an in-memory stand-in that agrees with them by
/// construction.
/// </remarks>
internal sealed class TemporaryFiles : IDisposable
{
    private readonly TemporaryStore _store = new();

    public TemporaryFiles()
    {
        Root = Path.Combine(Path.GetTempPath(), "fl-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Files = new HostSystemFiles(Root);
    }

    public string Root { get; }

    public HostSystemFiles Files { get; }

    public IStateStore Store => _store.Store;

    /// <summary>Writes a file through the real implementation, as a frame would already have it.</summary>
    public void Seed(string path, string content) => Files.WriteText(path, content);

    /// <summary>Reads a file back through the real implementation.</summary>
    public string? Read(string path) => Files.ReadText(path);

    public void Dispose()
    {
        _store.Dispose();

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
}

/// <summary>Records every command and answers with a script.</summary>
internal sealed class RecordingProcessRunner : IProcessRunner
{
    public List<string> Commands { get; } = [];

    public Dictionary<string, ProcessResult> Answers { get; } = new(StringComparer.Ordinal);

    public ProcessResult Default { get; set; } = new(0, string.Empty, string.Empty);

    public Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var line = executable + " " + string.Join(' ', arguments);
        Commands.Add(line);

        return Task.FromResult(Answers.TryGetValue(line, out var scripted) ? scripted : Default);
    }
}
