namespace FrameLink.Agent.Discovery;

/// <summary>
/// One candidate source in §4.3's ordered search.
/// </summary>
/// <remarks>
/// Sources are consulted in order and the first one that produces anything wins. They are all
/// the same shape so that the ordering lives in one list in one place, rather than in a chain
/// of fallbacks that has to be re-read to work out which wins.
/// </remarks>
public interface IEndpointSource
{
    /// <summary>Identifier recorded in <see cref="ControlEndpoints.DiscoveredBy"/>.</summary>
    string Name { get; }

    /// <summary>Returns candidate endpoints in preference order, or an empty list.</summary>
    Task<IReadOnlyList<Uri>> DiscoverAsync(CancellationToken cancellationToken);
}
