using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;
using FrameLink.Diagram;

namespace FrameLink.Tests;

/// <summary>
/// The two properties the reconcile DAG's documentation rests on.
/// </summary>
/// <remarks>
/// <para>
/// <b>One: the catalog file is the execution order, verbatim.</b> §2.2 asks for sequential,
/// single-threaded execution because "determinism beats throughput on a 2 GB appliance", and
/// <c>ResourceGraph</c> delivers it — Kahn's algorithm, a lowest-declaration-index tie-break, a
/// <c>List</c> computed once at construction. Nothing asserted that the result of all that is the
/// declaration order it started from, which is the property a person actually relies on when they
/// read <c>DeviceCatalog.Build</c> top to bottom and take it for the running order.
/// </para>
/// <para>
/// <b>Two: the committed diagram is that graph.</b> <c>reference/reconcile-dag.md</c> is generated
/// by <c>tools/FrameLink.Diagram</c> from the same catalog. A committed generated artifact rots
/// exactly the way <c>wwwroot</c> does, and for the same reason nothing about a stale one looks
/// wrong — so this re-renders it on every run and fails if the file on disk differs.
/// </para>
/// </remarks>
public sealed class ReconcileDagTests
{
    [Fact]
    public void The_walk_order_is_the_catalog_declaration_order_verbatim()
    {
        using var files = new TemporaryFiles();

        // One build, sorted. Two builds would compare two catalogs and could agree by accident on
        // a day when the sort was doing something.
        var declared = DeviceCatalog.Build(AgentResourceGraphTests.Context(files));
        var graph = new ResourceGraph(declared);

        var wanted = declared.Select(resource => resource.Name).ToList();
        var walked = graph.Ordered.Select(resource => resource.Name).ToList();

        // Named before the sequence assertion, because "expected 82 strings, got 82 strings" is a
        // useless failure and the first divergence is the whole diagnosis.
        var divergence = -1;
        for (var index = 0; index < Math.Min(wanted.Count, walked.Count); index++)
        {
            if (!string.Equals(wanted[index], walked[index], StringComparison.Ordinal))
            {
                divergence = index;
                break;
            }
        }

        if (divergence >= 0)
        {
            Assert.Fail(
                "The topological sort no longer returns the catalog's declaration order.\n\n"
                + $"  position {divergence + 1}\n"
                + $"  declared  {wanted[divergence]}\n"
                + $"  walked    {walked[divergence]}\n\n"
                + "Something now declares a dependency on a resource that comes after it, so "
                + "DeviceCatalog.Build can no longer be read as the running order. Either move the "
                + "declaration to where the sort puts it, or accept the reorder and regenerate "
                + "reference/reconcile-dag.md — the document states which of the two is true.");
        }

        Assert.Equal(wanted, walked);
    }

    [Fact]
    public void The_generator_reads_the_same_catalog_the_agent_builds()
    {
        // The freshness check below is only worth having if the tool's catalog is the frame's.
        // The tool builds its context from the production seams and the suite builds one from
        // doubles, so this is the assertion that those two agree about every id and every edge.
        using var files = new TemporaryFiles();
        var shipped = DeviceCatalog.BuildGraph(AgentResourceGraphTests.Context(files));
        var rendered = CatalogGraph.Snapshot().Graph;

        Assert.Equal(
            shipped.Ordered.Select(resource => resource.Name),
            rendered.Ordered.Select(resource => resource.Name));

        foreach (var resource in shipped.Ordered)
        {
            Assert.Equal(resource.DependsOn, rendered.Find(resource.Name)!.DependsOn);
        }
    }

    [Fact]
    public void The_committed_diagram_is_what_the_generator_produces_today()
    {
        var path = Path.Combine(
            GuiFreshnessTests.RepositoryRoot(),
            DiagramDocument.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(
            File.Exists(path),
            $"{DiagramDocument.RelativePath} is missing. It is generated — run "
            + "`dotnet run --project tools/FrameLink.Diagram -- write` and commit the result.");

        var committed = DiagramDocument.Normalise(File.ReadAllText(path));
        var rendered = DiagramDocument.Render();

        Assert.True(
            string.Equals(committed, rendered, StringComparison.Ordinal),
            $"{DiagramDocument.RelativePath} no longer describes the catalog.\n\n"
            + "  " + DiagramDocument.FirstDifference(committed, rendered) + "\n\n"
            + "Run `dotnet run --project tools/FrameLink.Diagram -- write` and commit what changes.");
    }
}
