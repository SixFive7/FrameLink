namespace FrameLink.Agent.Reconcile;

/// <summary>
/// Thrown when the catalog does not describe a runnable order.
/// </summary>
/// <remarks>
/// §2.2's DAG is "explicit lightweight", and both words matter: a cycle or a dangling
/// <c>dependsOn</c> is a mistake in the compiled catalog, not a condition a frame can find
/// itself in. Failing at construction turns it into a build-time-shaped error that the test
/// suite catches, rather than a frame that mysteriously never reconciles two resources.
/// </remarks>
public sealed class ResourceGraphException : InvalidOperationException
{
    /// <summary>Creates the exception with the explanation an implementer needs.</summary>
    public ResourceGraphException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    public ResourceGraphException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with no explanation.</summary>
    public ResourceGraphException()
    {
    }
}

/// <summary>
/// §2.2's explicit lightweight DAG: resources in a topological order, validated once.
/// </summary>
/// <remarks>
/// <para>
/// Ordering happens here and nowhere else, so the loop never has to ask whether a dependency
/// has been reached yet — by the time it gets to a resource, every id in its
/// <see cref="IResource.DependsOn"/> has already been through this pass.
/// </para>
/// <para>
/// The sort is deterministic: ties are broken by the order the resources were registered in,
/// which is the order the catalog declares them. §2.2 asks for sequential and single-threaded
/// execution because "determinism beats throughput on a 2 GB appliance", and a sort that could
/// return two different valid orders on two boots would undo half of that.
/// </para>
/// </remarks>
public sealed class ResourceGraph
{
    private readonly List<IResource> _ordered;
    private readonly Dictionary<string, IResource> _byName;

    /// <summary>Validates and orders <paramref name="resources"/>.</summary>
    /// <exception cref="ResourceGraphException">
    /// A duplicate id, a dependency on something that is not in the catalog, or a cycle.
    /// </exception>
    public ResourceGraph(IEnumerable<IResource> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);

        var declared = resources.ToList();
        _byName = new Dictionary<string, IResource>(declared.Count, StringComparer.Ordinal);

        foreach (var resource in declared)
        {
            if (!_byName.TryAdd(resource.Name, resource))
            {
                throw new ResourceGraphException(
                    $"Two resources are both called '{resource.Name}'. Resource ids are how the "
                    + "DAG, the journal and the Fleet Manager all refer to the same thing, so they "
                    + "have to be unique.");
            }
        }

        foreach (var resource in declared)
        {
            foreach (var dependency in resource.DependsOn)
            {
                if (!_byName.ContainsKey(dependency))
                {
                    throw new ResourceGraphException(
                        $"'{resource.Name}' depends on '{dependency}', which is not in the catalog. "
                        + "A dependency that does not exist would silently never be satisfied.");
                }
            }
        }

        _ordered = Sort(declared, _byName);
    }

    /// <summary>The resources, dependencies first.</summary>
    public IReadOnlyList<IResource> Ordered => _ordered;

    /// <summary>How many resources the catalog holds.</summary>
    public int Count => _ordered.Count;

    /// <summary>Finds a resource by id, or null.</summary>
    public IResource? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _byName.GetValueOrDefault(name);
    }

    /// <summary>
    /// Kahn's algorithm, with the ready set kept in declaration order.
    /// </summary>
    /// <remarks>
    /// A depth-first sort would also work and would be shorter, but it reports a cycle as "I
    /// found a back edge" rather than as the set of resources that are stuck — and the set is
    /// what an implementer needs, because a three-resource cycle names itself.
    /// </remarks>
    private static List<IResource> Sort(List<IResource> declared, Dictionary<string, IResource> byName)
    {
        var remaining = new Dictionary<string, int>(declared.Count, StringComparer.Ordinal);
        var dependents = new Dictionary<string, List<string>>(declared.Count, StringComparer.Ordinal);
        var declaredAt = new Dictionary<string, int>(declared.Count, StringComparer.Ordinal);

        for (var index = 0; index < declared.Count; index++)
        {
            declaredAt[declared[index].Name] = index;
        }

        foreach (var resource in declared)
        {
            remaining[resource.Name] = resource.DependsOn.Count;
            foreach (var dependency in resource.DependsOn)
            {
                if (!dependents.TryGetValue(dependency, out var list))
                {
                    list = [];
                    dependents[dependency] = list;
                }

                list.Add(resource.Name);
            }
        }

        var ordered = new List<IResource>(declared.Count);
        var ready = new List<string>(declared.Count);

        foreach (var resource in declared)
        {
            if (remaining[resource.Name] == 0)
            {
                ready.Add(resource.Name);
            }
        }

        while (ready.Count > 0)
        {
            // Lowest declaration index, not lowest arrival. Appending newly-ready resources would
            // also produce a valid order, and a much worse one: a resource declared second but
            // unblocked fourth would land at the end, so the catalog's own sequencing — display
            // first, its rotation immediately after — would survive only by accident.
            var pick = 0;
            for (var index = 1; index < ready.Count; index++)
            {
                if (declaredAt[ready[index]] < declaredAt[ready[pick]])
                {
                    pick = index;
                }
            }

            var name = ready[pick];
            ready.RemoveAt(pick);
            ordered.Add(byName[name]);

            if (!dependents.TryGetValue(name, out var waiting))
            {
                continue;
            }

            foreach (var dependent in waiting)
            {
                if (--remaining[dependent] == 0)
                {
                    ready.Add(dependent);
                }
            }
        }

        if (ordered.Count != declared.Count)
        {
            var stuck = declared
                .Where(resource => remaining[resource.Name] > 0)
                .Select(resource => resource.Name)
                .Order(StringComparer.Ordinal);

            throw new ResourceGraphException(
                "The catalog has a dependency cycle. These resources can never start because "
                + $"each is waiting on another in the group: {string.Join(", ", stuck)}.");
        }

        return ordered;
    }
}
