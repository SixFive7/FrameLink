using System.Collections.Concurrent;
using System.Threading.Channels;

namespace FrameLink.Control.Agent;

/// <summary>
/// A nudge to every open operator console that something about the fleet moved.
/// </summary>
/// <remarks>
/// <para>
/// §3.5 makes presence <i>be</i> the socket, which means the truth about a device changes at
/// the instant a frame connects or drops — and the operator API had no way to say so, leaving
/// the console to poll every four seconds. §3.3 optimises for one moment above all others: a
/// frame is plugged in on the bench and its row must appear. Four seconds is a long time to
/// stare at a screen wondering whether you got the URL wrong.
/// </para>
/// <para>
/// What crosses the wire is deliberately <b>only a device id</b>, never a device. This is a
/// signal, not a replication channel: the console re-reads <c>/api/devices</c>, which stays the
/// one place a fleet row is rendered from. That keeps the push impossible to get subtly wrong —
/// there is no second serialisation of a device, so there is no second thing to keep in step —
/// and it means a dropped or coalesced event costs a few hundred milliseconds rather than
/// correctness. The poll stays as the safety net underneath.
/// </para>
/// <para>
/// Subscriber queues are bounded and drop their oldest entry when full. A console that has
/// stopped reading must not be able to grow this server's memory, and since every event says
/// the same thing — "read the list again" — the oldest is exactly the one worth losing.
/// </para>
/// </remarks>
public sealed class FleetEvents
{
    /// <summary>Events a console may fall this far behind before the oldest are dropped.</summary>
    private const int QueueDepth = 64;

    private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();

    /// <summary>How many consoles are listening. Exists so a test can prove unsubscribe works.</summary>
    public int SubscriberCount => _subscribers.Count;

    /// <summary>Opens a stream of device ids. Dispose it to stop listening.</summary>
    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueDepth)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        return new Subscription(this, id, channel.Reader);
    }

    /// <summary>Tells every listening console that this device changed.</summary>
    /// <remarks>
    /// Never throws and never blocks: a publish happens on a device's socket thread and inside
    /// operator requests, and neither may be held up — let alone failed — by a browser.
    /// </remarks>
    public void Publish(string deviceId)
    {
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(deviceId);
        }
    }

    private void Unsubscribe(Guid id) => _subscribers.TryRemove(id, out _);

    /// <summary>One console's listening handle.</summary>
    public sealed class Subscription(FleetEvents owner, Guid id, ChannelReader<string> reader) : IDisposable
    {
        /// <summary>The device ids, in the order they were published.</summary>
        public ChannelReader<string> Reader { get; } = reader;

        /// <inheritdoc/>
        public void Dispose() => owner.Unsubscribe(id);
    }
}
