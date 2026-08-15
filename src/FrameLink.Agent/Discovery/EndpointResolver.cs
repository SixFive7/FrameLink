using System.Text.Json;
using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Discovery;

/// <summary>
/// §4.3's single code path: <b>find a candidate endpoint → enroll → persist → never rediscover</b>.
/// </summary>
/// <remarks>
/// <para>
/// The "never rediscover" half is enforced structurally rather than by discipline: if the
/// persisted file has endpoints in it, no source is asked anything. Not a cache with a refresh,
/// not a preference that discovery can override — an early return. A frame that has been told
/// where it belongs stays told.
/// </para>
/// <para>
/// It is worth being explicit about why: the mDNS candidate makes the local network a voice in
/// this decision, and a frame that re-ran discovery on every boot could be moved to a different
/// Fleet Manager by anything on the LAN willing to answer first. Decommissioning is the only
/// way back into the adoption queue, and §3.3 makes it a confirmed, destructive action.
/// </para>
/// </remarks>
public sealed class EndpointResolver
{
    /// <summary>File name of the persisted endpoint list.</summary>
    public const string FileName = "endpoints.json";

    private readonly IStateStore _store;
    private readonly IReadOnlyList<IEndpointSource> _sources;
    private readonly IAgentClock _clock;
    private readonly IAgentLog _log;

    /// <summary>Creates a resolver over the ordered <paramref name="sources"/>.</summary>
    public EndpointResolver(
        IStateStore store,
        IReadOnlyList<IEndpointSource> sources,
        IAgentClock clock,
        IAgentLog log)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(log);

        _store = store;
        _sources = sources;
        _clock = clock;
        _log = log;
    }

    /// <summary>Reads the persisted list without consulting any source.</summary>
    public ControlEndpoints? Persisted()
    {
        var content = _store.ReadText(FileName);
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            var stored = JsonSerializer.Deserialize(content, AgentJson.Default.ControlEndpoints);
            return stored is { Endpoints.Count: > 0 } ? stored : null;
        }
        catch (JsonException exception)
        {
            _log.Warn($"{FileName} could not be read ({exception.Message}); rediscovering.");
            return null;
        }
    }

    /// <summary>Returns the endpoint list, discovering and persisting it on first boot.</summary>
    public async Task<ControlEndpoints?> ResolveAsync(CancellationToken cancellationToken)
    {
        var persisted = Persisted();
        if (persisted is not null)
        {
            return persisted;
        }

        foreach (var source in _sources)
        {
            var candidates = await source.DiscoverAsync(cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                continue;
            }

            var resolved = new ControlEndpoints
            {
                Endpoints = candidates,
                DiscoveredBy = source.Name,
                DiscoveredAt = _clock.UtcNow,
            };

            Persist(resolved);
            _log.Info($"Fleet Manager found via {source.Name}: {string.Join(", ", candidates.Select(e => e.ToString()))}");
            return resolved;
        }

        return null;
    }

    /// <summary>Writes the list, making the choice permanent.</summary>
    public void Persist(ControlEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        _store.WriteText(FileName, JsonSerializer.Serialize(endpoints, AgentJson.Default.ControlEndpoints));
    }
}
