using System.Collections.Concurrent;

namespace FrameLink.Control.Agent;

/// <summary>
/// Which devices are online, and how to reach them.
/// </summary>
/// <remarks>
/// <para>
/// §3.5: <b>presence is the socket</b>. There is no heartbeat table to age out and nothing to
/// poll — a device is online exactly while it holds an entry here, and it stops being online
/// the instant the entry goes. That is only trustworthy because the connection actively
/// proves itself with ping/pong; a half-open TCP connection would otherwise sit in this
/// dictionary forever, reporting a frame as online long after its plug was pulled.
/// </para>
/// <para>
/// In memory by design. On a restart every frame reconnects and the truth rebuilds itself,
/// which is more accurate than anything that could have been persisted.
/// </para>
/// </remarks>
/// <param name="events">
/// Told about every arrival and departure, so an open console sees a frame appear the moment
/// it is plugged in rather than on its next poll. Optional, because presence is a property of
/// this dictionary and nothing about it depends on anyone listening.
/// </param>
public sealed class AgentConnectionRegistry(FleetEvents? events = null)
{
    private readonly ConcurrentDictionary<string, AgentConnection> _connections = new(StringComparer.Ordinal);

    /// <summary>How many devices are online right now.</summary>
    public int Count => _connections.Count;

    /// <summary>
    /// Registers a connection, displacing any earlier one for the same device.
    /// </summary>
    /// <returns>The displaced connection, which the caller must close.</returns>
    /// <remarks>
    /// A device reconnecting while the server still believes the previous socket is alive is
    /// the normal shape of recovery from a network drop, and one device must never hold two
    /// sockets — the second would receive settings pushes the first never saw.
    /// </remarks>
    public AgentConnection? Register(AgentConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        AgentConnection? displaced = null;
        _connections.AddOrUpdate(
            connection.DeviceId,
            connection,
            (_, existing) =>
            {
                displaced = existing;
                return connection;
            });

        events?.Publish(connection.DeviceId);
        return displaced;
    }

    /// <summary>Removes a connection, but only if it is still the current one for that device.</summary>
    public void Remove(AgentConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Only the removal that actually took anything out is a presence change. A displaced
        // connection tidying itself up after a reconnect has not taken the device offline, and
        // announcing that it had would flicker the console on every ordinary network drop.
        if (_connections.TryRemove(new KeyValuePair<string, AgentConnection>(connection.DeviceId, connection)))
        {
            events?.Publish(connection.DeviceId);
        }
    }

    /// <summary>True while the device holds a live socket.</summary>
    public bool IsOnline(string deviceId) => _connections.ContainsKey(deviceId);

    /// <summary>The live connection for a device, or null.</summary>
    public AgentConnection? Find(string deviceId) =>
        _connections.TryGetValue(deviceId, out var connection) ? connection : null;

    /// <summary>Every live connection, for fleet-wide pushes.</summary>
    public IReadOnlyCollection<AgentConnection> All() => _connections.Values.ToArray();
}
