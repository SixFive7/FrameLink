using FrameLink.Agent.Reconcile;

namespace FrameLink.Diagram;

/// <summary>
/// One resource, as the diagram needs it: a position, an id, an area and its two edge lists.
/// </summary>
/// <param name="Position">1-based position in <see cref="ResourceGraph.Ordered"/>.</param>
/// <param name="Name">The resource id.</param>
/// <param name="Area">The id's first dot-separated segment.</param>
/// <param name="DependsOn">Ids this resource waits for, in the order it declares them.</param>
/// <param name="Dependents">Ids that wait for this one, in walk order.</param>
public sealed record CatalogNode(
    int Position,
    string Name,
    string Area,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> Dependents);

/// <summary>
/// The catalog reduced to what a picture and a numbered list need, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Areas are the id's first dot-separated segment.</b> That is a mechanical rule rather than an
/// editorial one, chosen because an editorial one is a second thing to maintain and would be the
/// part of a generated document that still drifts. The cost is stated in the document it renders:
/// the session and kiosk stack is one subject spread across <c>session</c>, <c>labwc</c>,
/// <c>unit</c>, <c>portal</c> and <c>camera</c>, because that is how its ids are spelled.
/// </para>
/// <para>
/// Every collection here is built by walking <see cref="ResourceGraph.Ordered"/> in order, so
/// every list this exposes is in walk order and the rendered document is byte-stable for a given
/// catalog. That is what makes the freshness check in the suite meaningful.
/// </para>
/// </remarks>
public sealed class CatalogModel
{
    private readonly Dictionary<string, CatalogNode> _byName;
    private readonly Dictionary<string, int> _depth;

    /// <summary>Reduces <paramref name="snapshot"/> to the model the renderer walks.</summary>
    public CatalogModel(CatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var graph = snapshot.Graph;
        Declared = snapshot.Declared;

        var dependents = new Dictionary<string, List<string>>(graph.Count, StringComparer.Ordinal);
        foreach (var resource in graph.Ordered)
        {
            dependents[resource.Name] = [];
        }

        foreach (var resource in graph.Ordered)
        {
            foreach (var dependency in resource.DependsOn)
            {
                dependents[dependency].Add(resource.Name);
            }
        }

        var nodes = new List<CatalogNode>(graph.Count);
        var areas = new List<string>();
        var position = 0;

        foreach (var resource in graph.Ordered)
        {
            position++;
            var area = AreaOf(resource.Name);
            if (!areas.Contains(area, StringComparer.Ordinal))
            {
                areas.Add(area);
            }

            nodes.Add(new CatalogNode(
                position,
                resource.Name,
                area,
                [.. resource.DependsOn],
                dependents[resource.Name]));
        }

        Nodes = nodes;
        Areas = areas;
        _byName = nodes.ToDictionary(node => node.Name, StringComparer.Ordinal);
        _depth = Depths(nodes, _byName);
    }

    /// <summary>Every resource, in walk order.</summary>
    public IReadOnlyList<CatalogNode> Nodes { get; }

    /// <summary>Every resource id, in the order <c>DeviceCatalog.Build</c> declares them.</summary>
    public IReadOnlyList<string> Declared { get; }

    /// <summary>
    /// Whether the topological sort returned the declaration order unchanged.
    /// </summary>
    /// <remarks>
    /// The property the operator's instinct is about: when this is true the catalog file <i>is</i>
    /// the execution order, verbatim, and the sort is an identity function with a validator
    /// attached. It is rendered as a fact rather than asserted here, so a catalog edit that makes
    /// it false changes the document rather than being quietly wrong in it.
    /// </remarks>
    public bool SortChangesNothing =>
        Declared.SequenceEqual(Nodes.Select(node => node.Name), StringComparer.Ordinal);

    /// <summary>Resources the sort moved, as (declared position, walked position, id).</summary>
    public IEnumerable<(int Declared, int Walked, string Name)> Moved
    {
        get
        {
            for (var index = 0; index < Declared.Count && index < Nodes.Count; index++)
            {
                if (!string.Equals(Declared[index], Nodes[index].Name, StringComparison.Ordinal))
                {
                    yield return (index + 1, Nodes[index].Position, Nodes[index].Name);
                }
            }
        }
    }

    /// <summary>Every area, in the order its first resource is walked.</summary>
    public IReadOnlyList<string> Areas { get; }

    /// <summary>How many resources the catalog holds.</summary>
    public int Count => Nodes.Count;

    /// <summary>Every dependency edge, as (dependency, dependent) pairs in walk order.</summary>
    public IEnumerable<(CatalogNode From, CatalogNode To)> Edges
    {
        get
        {
            foreach (var node in Nodes)
            {
                foreach (var dependency in node.DependsOn)
                {
                    yield return (_byName[dependency], node);
                }
            }
        }
    }

    /// <summary>Finds a resource by id.</summary>
    public CatalogNode Node(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _byName[name];
    }

    /// <summary>Every resource in <paramref name="area"/>, in walk order.</summary>
    public IReadOnlyList<CatalogNode> InArea(string area)
    {
        ArgumentNullException.ThrowIfNull(area);
        return [.. Nodes.Where(node => string.Equals(node.Area, area, StringComparison.Ordinal))];
    }

    /// <summary>
    /// The longest chain of dependencies ending at <paramref name="node"/>, counted in resources.
    /// </summary>
    /// <remarks>
    /// A resource that declares nothing has depth 1. The catalog's deepest value is the honest
    /// answer to "how many things have to happen in sequence before the last one can", and it is
    /// the number that says whether this DAG is deep or merely wide.
    /// </remarks>
    public int Depth(CatalogNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return _depth[node.Name];
    }

    /// <summary>One of the catalog's longest chains, dependency first.</summary>
    public IReadOnlyList<CatalogNode> LongestChain()
    {
        var deepest = Nodes.OrderByDescending(Depth).ThenBy(node => node.Position).First();
        var chain = new List<CatalogNode> { deepest };

        var walk = deepest;
        while (walk.DependsOn.Count > 0)
        {
            walk = walk.DependsOn
                .Select(_byName.GetValueOrDefault)
                .Where(candidate => candidate is not null)
                .Select(candidate => candidate!)
                .OrderByDescending(Depth)
                .ThenBy(candidate => candidate.Position)
                .First();

            chain.Add(walk);
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>
    /// Everything that can never be attempted while <paramref name="node"/> is not in sync.
    /// </summary>
    /// <remarks>
    /// The transitive closure of the dependents, which is the number an operator actually wants
    /// when a resource has escalated: <c>Blocked(dependency)</c> propagates, so a root that six
    /// things name directly can be holding thirty.
    /// </remarks>
    public int BlockedBehind(CatalogNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        pending.Enqueue(node.Name);

        while (pending.TryDequeue(out var name))
        {
            foreach (var dependent in _byName[name].Dependents)
            {
                if (seen.Add(dependent))
                {
                    pending.Enqueue(dependent);
                }
            }
        }

        return seen.Count;
    }

    /// <summary>The id's first dot-separated segment.</summary>
    public static string AreaOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var dot = name.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? name : name[..dot];
    }

    private static Dictionary<string, int> Depths(
        List<CatalogNode> nodes,
        Dictionary<string, CatalogNode> byName)
    {
        // Walk order is already topological, so one forward pass is enough: every dependency has
        // been given a depth by the time anything that names it is reached.
        var depth = new Dictionary<string, int>(nodes.Count, StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            var deepest = 0;
            foreach (var dependency in node.DependsOn)
            {
                deepest = Math.Max(deepest, depth[byName[dependency].Name]);
            }

            depth[node.Name] = deepest + 1;
        }

        return depth;
    }
}
