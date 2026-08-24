using System.Globalization;
using System.Text;

namespace FrameLink.Diagram;

/// <summary>
/// <c>reference/reconcile-dag.md</c>, rendered from the catalog the agent runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every word of the document is here, including the prose.</b> That is what makes the
/// freshness check in the suite exact: the committed file is compared byte for byte against this
/// renderer's output, so there is no hand-written half for somebody to edit and no way for the
/// picture and the sentences around it to disagree.
/// </para>
/// <para>
/// <b>Three views, deliberately, because one is unreadable.</b> Eighty-odd nodes in a single
/// picture is a picture nobody opens twice. The area map is small enough to take in at a glance
/// and says where the gates are; the numbered order is the plainest possible statement of what
/// runs when; the per-area diagrams are small enough to read. The dense whole-graph picture is
/// last, and the document says outright that it is dense.
/// </para>
/// </remarks>
public static class DiagramDocument
{
    /// <summary>Where the rendered document belongs, relative to the repository root.</summary>
    public const string RelativePath = "reference/reconcile-dag.md";

    /// <summary>How many blockers the "what most things wait on" table names.</summary>
    private const int BlockerRows = 10;

    /// <summary>Renders the whole document, LF-terminated, from a freshly built catalog.</summary>
    public static string Render() => Render(new CatalogModel(CatalogGraph.Snapshot()));

    /// <summary>CRLF and a UTF-8 byte-order mark are accidents of the platform, not content.</summary>
    /// <remarks>
    /// <c>.gitattributes</c> normalises this repository to LF on every platform, so a checkout on
    /// Windows can still hand back CRLF depending on how it was made. Comparing raw bytes would
    /// then fail the freshness check for a reason that has nothing to do with the catalog.
    /// </remarks>
    public static string Normalise(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('﻿');
    }

    /// <summary>The first line two renders disagree on, said in one sentence.</summary>
    /// <remarks>
    /// A whole diff would be useless here — a catalog edit moves every position after it, so the
    /// interesting information is the <i>first</i> divergence and nothing after it.
    /// </remarks>
    public static string FirstDifference(string committed, string rendered)
    {
        ArgumentNullException.ThrowIfNull(committed);
        ArgumentNullException.ThrowIfNull(rendered);

        var left = committed.Split('\n');
        var right = rendered.Split('\n');

        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
            {
                return $"line {N(index + 1)} reads \"{Clip(left[index])}\" and should read \"{Clip(right[index])}\"";
            }
        }

        return left.Length == right.Length
            ? "the two texts differ but no line does, which should be impossible"
            : $"the committed file has {N(left.Length)} lines and a fresh render has {N(right.Length)}";
    }

    private static string Clip(string line) => line.Length <= 120 ? line : line[..120] + "…";

    /// <summary>Renders the whole document, LF-terminated.</summary>
    public static string Render(CatalogModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var text = new StringBuilder(64 * 1024);

        Banner(text, model);
        HowToRead(text, model);
        AreaMap(text, model);
        ExecutionOrder(text, model);
        AreaDetail(text, model);
        WholeGraph(text, model);
        Shape(text, model);

        return text.ToString();
    }

    private static void Banner(StringBuilder text, CatalogModel model)
    {
        text.Append("# The reconcile DAG\n\n");
        text.Append("<!--\n");
        text.Append("  GENERATED FILE — do not edit by hand. Every node, edge, position and count below is\n");
        text.Append("  read out of the catalog the agent actually runs.\n\n");
        text.Append("  Regenerate:   dotnet run --project tools/FrameLink.Diagram -- write\n");
        text.Append("  Check only:   dotnet run --project tools/FrameLink.Diagram -- check\n\n");
        text.Append("  The suite re-renders this file on every run and fails if the committed copy differs,\n");
        text.Append("  so a stale diagram is a red test rather than a picture people quietly stop trusting.\n");
        text.Append("-->\n\n");

        text.Append("§2.2's \"explicit lightweight DAG\", drawn from itself. `tools/FrameLink.Diagram` builds\n");
        text.Append("the real catalog — the same `DeviceCatalog.Build` an agent builds at start-up — sorts it\n");
        text.Append("through the same `ResourceGraph`, and renders this file from the result. Nothing here is\n");
        text.Append("typed by hand, so nothing here can be out of date without a test going red.\n\n");

        text.Append("The catalog holds **");
        text.Append(Count(model.Count, "resource"));
        text.Append("** in ");
        text.Append(Count(model.Areas.Count, "area"));
        text.Append(", joined by ");
        text.Append(Count(model.Edges.Count(), "dependency edge"));
        text.Append(".\n\n");

        text.Append("Two hand-written companions sit beside this one and are not generated:\n");
        text.Append("[every source of non-determinism, classified](reconcile-determinism.md), which says what can\n");
        text.Append("and cannot make two runs differ; and [reconcile ordering and every bound in the\n");
        text.Append("agent](reconcile-ordering-and-timeouts.md), which inventories every number the agent waits on.\n\n");

        text.Append("---\n\n");
    }

    private static void HowToRead(StringBuilder text, CatalogModel model)
    {
        text.Append("## 1. How to read this\n\n");

        text.Append("**An arrow means \"has to be `InSync` first\".** `A --> B` reads *A before B*: while A is\n");
        text.Append("anything other than `InSync` this pass, B is recorded `Blocked(A)` and is neither observed\n");
        text.Append("nor acted on, so it spends no attempt and takes no reboot. That is the only thing an edge\n");
        text.Append("does, and it is the whole reason the graph exists — `Blocked(dependency)` is a derived\n");
        text.Append("fact rather than a claim somebody maintained by hand.\n\n");

        text.Append("**The walk is one sequential `foreach` over the order in §3.** No parallelism, no\n");
        text.Append("re-sorting, no arrival-order tie-break: the sort runs once at construction, ties break on\n");
        text.Append("declaration index, and the result is a `List`. Same build, same hardware, same route\n");
        text.Append("through the graph, every pass and every boot.\n\n");

        if (model.SortChangesNothing)
        {
            text.Append("**The catalog file *is* the execution order, verbatim.** The topological sort returned\n");
            text.Append("the declaration order unchanged — on this catalog it is an identity function with a\n");
            text.Append("validator attached. `AgentResourceGraphTests` asserts exactly that, so the day an edge\n");
            text.Append("reorders something this sentence changes and a test goes red on the same commit.\n\n");
        }
        else
        {
            text.Append("**The sort moved something.** The declaration order in `DeviceCatalog.Build` is no\n");
            text.Append("longer a valid topological order on its own, so the catalog file no longer reads as the\n");
            text.Append("execution order. The positions that differ:\n\n");

            text.Append("| Declared at | Walked at | Resource |\n|---|---|---|\n");
            foreach (var (declared, walked, name) in model.Moved)
            {
                text.Append("| ");
                text.Append(N(declared));
                text.Append(" | ");
                text.Append(N(walked));
                text.Append(" | `");
                text.Append(name);
                text.Append("` |\n");
            }

            text.Append('\n');
        }

        text.Append("**Areas are the resource id's first dot-separated segment** — `pkg`, `audio`, `kiosk`,\n");
        text.Append("`unit`. A mechanical rule rather than an editorial one, because an editorial one is a\n");
        text.Append("second thing to maintain and would be the part of a generated document that still drifts.\n");
        text.Append("It has one real cost, stated rather than hidden: the session and kiosk stack is one\n");
        text.Append("subject spread across `session`, `labwc`, `unit`, `portal` and `camera`, because that is\n");
        text.Append("how its ids are spelled.\n\n");

        text.Append("**Three views, because one picture of ");
        text.Append(N(model.Count));
        text.Append(" nodes is a picture nobody opens twice.** §2 is the\n");
        text.Append("area map — small enough to take in at a glance, and where the gates are. §3 is the\n");
        text.Append("numbered running order, which is the plainest statement of what happens when. §4 is one\n");
        text.Append("small diagram per area that waits on something. §5 is the whole graph in one picture, and\n");
        text.Append("it is dense; it is last because it is the least useful of the three, not the most.\n\n");

        text.Append("---\n\n");
    }

    private static void AreaMap(StringBuilder text, CatalogModel model)
    {
        text.Append("## 2. The area map\n\n");

        text.Append("Areas, and which areas they wait on. An edge label is how many resource-level\n");
        text.Append("dependencies it stands for; self-edges are left out because an area waiting on itself is\n");
        text.Append("not a fact about the shape. An area with no arrow at either end holds only resources that\n");
        text.Append("wait on nothing and that nothing waits on.\n\n");

        var pairs = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<(string From, string To)>();

        foreach (var (from, to) in model.Edges)
        {
            if (string.Equals(from.Area, to.Area, StringComparison.Ordinal))
            {
                continue;
            }

            var key = from.Area + ">" + to.Area;
            if (!pairs.TryGetValue(key, out var seen))
            {
                order.Add((from.Area, to.Area));
                seen = 0;
            }

            pairs[key] = seen + 1;
        }

        text.Append("```mermaid\nflowchart TD\n");

        foreach (var area in model.Areas)
        {
            var resources = model.InArea(area);
            text.Append("  ");
            text.Append(AreaId(area));
            text.Append("[\"");
            text.Append(area);
            text.Append("<br/>");
            text.Append(N(resources.Count));
            text.Append(resources.Count == 1 ? " resource" : " resources");
            text.Append("\"]\n");
        }

        foreach (var (from, to) in order)
        {
            var weight = pairs[from + ">" + to];
            text.Append("  ");
            text.Append(AreaId(from));
            text.Append(weight == 1 ? " --> " : " -->|" + N(weight) + "| ");
            text.Append(AreaId(to));
            text.Append('\n');
        }

        text.Append("```\n\n");
        text.Append("---\n\n");
    }

    private static void ExecutionOrder(StringBuilder text, CatalogModel model)
    {
        text.Append("## 3. The execution order\n\n");

        text.Append("Every resource, in the order one pass visits it. A pass changes at most one of them and\n");
        text.Append("then reboots and verifies, so this is the order a bare frame converges in rather than a\n");
        text.Append("list of things that happen at once. Dependencies are shown by position, sorted ascending,\n");
        text.Append("which makes the property worth checking by eye visible: **every number after \"waits for\"\n");
        text.Append("is smaller than the one it sits beside.**\n\n");

        foreach (var node in model.Nodes)
        {
            text.Append(N(node.Position));
            text.Append(". `");
            text.Append(node.Name);
            text.Append('`');

            if (node.DependsOn.Count > 0)
            {
                text.Append(" — waits for ");

                var first = true;
                foreach (var dependency in node.DependsOn
                    .Select(model.Node)
                    .OrderBy(dependency => dependency.Position))
                {
                    if (!first)
                    {
                        text.Append(", ");
                    }

                    first = false;
                    text.Append('#');
                    text.Append(N(dependency.Position));
                    text.Append(" `");
                    text.Append(dependency.Name);
                    text.Append('`');
                }
            }

            text.Append('\n');
        }

        text.Append("\n---\n\n");
    }

    private static void AreaDetail(StringBuilder text, CatalogModel model)
    {
        text.Append("## 4. Area detail\n\n");

        text.Append("One diagram per area that waits on something, showing that area's own resources and\n");
        text.Append("whatever they wait on. Nodes carry their position from §3. A **rounded** node belongs to\n");
        text.Append("another area and is drawn here only because something in this one names it.\n\n");

        var silent = new List<string>();

        foreach (var area in model.Areas)
        {
            var resources = model.InArea(area);
            if (!resources.Any(node => node.DependsOn.Count > 0))
            {
                silent.Add(area);
                continue;
            }

            var external = new List<CatalogNode>();

            foreach (var node in resources)
            {
                foreach (var dependency in node.DependsOn.Select(model.Node))
                {
                    if (string.Equals(dependency.Area, area, StringComparison.Ordinal)
                        || external.Contains(dependency))
                    {
                        continue;
                    }

                    external.Add(dependency);
                }
            }

            external.Sort((left, right) => left.Position.CompareTo(right.Position));

            text.Append("### `");
            text.Append(area);
            text.Append("` — ");
            text.Append(Count(resources.Count, "resource"));
            text.Append(resources.Count == 1
                ? ", walked at #" + N(resources[0].Position)
                : ", walked between #" + N(resources[0].Position) + " and #" + N(resources[^1].Position));
            text.Append("\n\n");

            text.Append("```mermaid\nflowchart LR\n");

            foreach (var node in resources)
            {
                Node(text, node);
            }

            foreach (var node in external)
            {
                Node(text, node, borrowed: true);
            }

            foreach (var node in resources)
            {
                foreach (var dependency in node.DependsOn.Select(model.Node))
                {
                    text.Append("  ");
                    text.Append(NodeId(dependency));
                    text.Append(" --> ");
                    text.Append(NodeId(node));
                    text.Append('\n');
                }
            }

            text.Append("```\n\n");
        }

        text.Append("**No diagram for ");
        text.Append(Count(silent.Count, "area"));
        text.Append(":** ");
        text.Append(string.Join(", ", silent.Select(area => "`" + area + "`")));
        text.Append(". Nothing in them declares a\n");
        text.Append("dependency, so there is no picture to draw — every resource in them is reached at its\n");
        text.Append("position in §3 with nothing gating it.\n\n");

        text.Append("---\n\n");
    }

    private static void WholeGraph(StringBuilder text, CatalogModel model)
    {
        var connected = model.Nodes
            .Where(node => node.DependsOn.Count > 0 || node.Dependents.Count > 0)
            .ToList();

        var isolated = model.Nodes
            .Where(node => node.DependsOn.Count == 0 && node.Dependents.Count == 0)
            .ToList();

        text.Append("## 5. The whole graph in one picture\n\n");

        text.Append("**This one is dense, and that is the honest description of it.** ");
        text.Append(N(connected.Count));
        text.Append(" of the catalog's\n");
        text.Append(N(model.Count));
        text.Append(" resources touch an edge; the other ");
        text.Append(N(isolated.Count));
        text.Append(" are listed underneath rather than drawn, because a\n");
        text.Append("node with no arrows is a row in §3 and not a shape. Boxes group by area. Use §2 and §4\n");
        text.Append("first — this is here for the times somebody needs the whole thing at once.\n\n");

        text.Append("```mermaid\nflowchart TD\n");

        foreach (var area in model.Areas)
        {
            var members = model.InArea(area)
                .Where(node => node.DependsOn.Count > 0 || node.Dependents.Count > 0)
                .ToList();

            if (members.Count == 0)
            {
                continue;
            }

            text.Append("  subgraph ");
            text.Append(AreaId(area));
            text.Append("[\"");
            text.Append(area);
            text.Append("\"]\n");

            foreach (var node in members)
            {
                text.Append("  ");
                Node(text, node);
            }

            text.Append("  end\n");
        }

        foreach (var (from, to) in model.Edges)
        {
            text.Append("  ");
            text.Append(NodeId(from));
            text.Append(" --> ");
            text.Append(NodeId(to));
            text.Append('\n');
        }

        text.Append("```\n\n");

        text.Append("**Not drawn — ");
        text.Append(Count(isolated.Count, "resource"));
        text.Append(" with no edge in either direction.** They wait on nothing and\n");
        text.Append("nothing waits on them, so their position in §3 is the whole of what there is to say:\n\n");

        foreach (var node in isolated)
        {
            text.Append("- #");
            text.Append(N(node.Position));
            text.Append(" `");
            text.Append(node.Name);
            text.Append("`\n");
        }

        text.Append("\n---\n\n");
    }

    private static void Shape(StringBuilder text, CatalogModel model)
    {
        var edges = model.Edges.Count();
        var declaring = model.Nodes.Count(node => node.DependsOn.Count > 0);
        var dependedOn = model.Nodes.Count(node => node.Dependents.Count > 0);
        var isolated = model.Nodes.Count(node => node.DependsOn.Count == 0 && node.Dependents.Count == 0);
        var chain = model.LongestChain();

        text.Append("## 6. What the shape says\n\n");

        text.Append("| | |\n|---|---|\n");
        Row(text, "Resources in the catalog", model.Count);
        Row(text, "Dependency edges", edges);
        Row(text, "Resources that declare at least one dependency", declaring);
        Row(text, "Resources something else waits on", dependedOn);
        Row(text, "Resources with no edge in either direction", isolated);
        Row(text, "Areas", model.Areas.Count);
        Row(text, "Longest chain, in resources", chain.Count);
        text.Append('\n');

        text.Append("**The graph is wide, not deep.** The longest chain in it is ");
        text.Append(Count(chain.Count, "resource"));
        text.Append(" long:\n\n");

        text.Append(string.Join(
            " → ",
            chain.Select(node => "#" + N(node.Position) + " `" + node.Name + "`")));
        text.Append("\n\n");

        text.Append("So no resource in this catalog is more than ");
        text.Append(N(chain.Count - 1));
        text.Append(" hops from something that gates nothing.\n");
        text.Append("Depth is not what the DAG is for here; refusing to attempt doomed work is.\n\n");

        text.Append("**What most things wait on.** *Waiting on it directly* counts the resources that name it\n");
        text.Append("in `dependsOn`; *blocked behind it* counts everything that can never be attempted while it\n");
        text.Append("is not `InSync`, which is the number that matters when one has escalated and the frame has\n");
        text.Append("stopped acting.\n\n");

        text.Append("| Position | Resource | Waiting on it directly | Blocked behind it |\n|---|---|---|---|\n");

        foreach (var node in model.Nodes
            .Where(node => node.Dependents.Count > 0)
            .OrderByDescending(model.BlockedBehind)
            .ThenBy(node => node.Position)
            .Take(BlockerRows))
        {
            text.Append("| ");
            text.Append(N(node.Position));
            text.Append(" | `");
            text.Append(node.Name);
            text.Append("` | ");
            text.Append(N(node.Dependents.Count));
            text.Append(" | ");
            text.Append(N(model.BlockedBehind(node)));
            text.Append(" |\n");
        }

        text.Append('\n');
    }

    private static void Row(StringBuilder text, string label, int value)
    {
        text.Append("| ");
        text.Append(label);
        text.Append(" | **");
        text.Append(N(value));
        text.Append("** |\n");
    }

    /// <summary>One mermaid node; a borrowed one is drawn as a stadium rather than a box.</summary>
    /// <remarks>
    /// Shape rather than <c>classDef</c>, deliberately: node shapes are core flowchart syntax and
    /// render the same everywhere, where a style declaration is the part of a mermaid block a
    /// renderer is most likely to drop — and a legend that has quietly stopped being true is worse
    /// than no legend.
    /// </remarks>
    private static void Node(StringBuilder text, CatalogNode node, bool borrowed = false)
    {
        text.Append("  ");
        text.Append(NodeId(node));
        text.Append(borrowed ? "([\"" : "[\"");
        text.Append(N(node.Position));
        text.Append(" · ");
        text.Append(node.Name);
        text.Append(borrowed ? "\"])\n" : "\"]\n");
    }

    private static string NodeId(CatalogNode node) => "r" + N(node.Position);

    private static string AreaId(string area) => "a_" + area;

    private static string Count(int value, string noun) =>
        N(value) + " " + noun + (value == 1 ? string.Empty : "s");

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
