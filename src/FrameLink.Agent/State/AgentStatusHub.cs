namespace FrameLink.Agent.State;

/// <summary>
/// The single long-lived holder of <see cref="AgentStatus"/>, and the thing every short-lived
/// part of the agent subscribes to.
/// </summary>
/// <remarks>
/// <para>
/// This type is where the v1 LiveKit post-mortem is encoded as a design constraint rather than
/// a comment. That retry loop leaked <i>listener</i> state: every failed connect attached
/// handlers to objects that outlived it, and the accumulation — measured at roughly 15 MB per
/// minute — killed a 2 GB frame in under two hours. The hub is deliberately the only long-lived
/// object a connection attempt is allowed to attach to, it hands back an
/// <see cref="IDisposable"/> rather than exposing an event, and it publishes
/// <see cref="SubscriberCount"/> so that "nothing accumulated" is a fact a test can assert
/// instead of a claim a reviewer has to believe.
/// </para>
/// <para>
/// Subscribers are held in an immutable array swapped under a lock, so publishing never holds
/// the lock while calling out and a subscriber that unsubscribes mid-publish cannot corrupt the
/// iteration.
/// </para>
/// </remarks>
public sealed class AgentStatusHub
{
    private readonly Lock _gate = new();
    private Action<AgentStatus>[] _subscribers = [];
    private AgentStatus _current;

    /// <summary>Creates a hub holding <paramref name="initial"/>.</summary>
    public AgentStatusHub(AgentStatus initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    /// <summary>The current snapshot.</summary>
    public AgentStatus Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>How many live subscriptions exist.</summary>
    /// <remarks>
    /// Exposed for the reconnect-loop leak test (§4.1, "cleanup per failed attempt ... gets its
    /// own test"). A retry loop that leaks listeners shows up here as a number that only ever
    /// grows.
    /// </remarks>
    public int SubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _subscribers.Length;
            }
        }
    }

    /// <summary>Subscribes to snapshot changes until the returned handle is disposed.</summary>
    public IDisposable Subscribe(Action<AgentStatus> onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);

        lock (_gate)
        {
            _subscribers = [.. _subscribers, onChanged];
        }

        return new Subscription(this, onChanged);
    }

    /// <summary>Replaces the snapshot and notifies every subscriber.</summary>
    public AgentStatus Publish(Func<AgentStatus, AgentStatus> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        AgentStatus published;
        Action<AgentStatus>[] listeners;

        lock (_gate)
        {
            published = update(_current);
            ArgumentNullException.ThrowIfNull(published);
            _current = published;
            listeners = _subscribers;
        }

        foreach (var listener in listeners)
        {
            listener(published);
        }

        return published;
    }

    private void Unsubscribe(Action<AgentStatus> onChanged)
    {
        lock (_gate)
        {
            var index = Array.IndexOf(_subscribers, onChanged);
            if (index < 0)
            {
                return;
            }

            var remaining = new Action<AgentStatus>[_subscribers.Length - 1];
            Array.Copy(_subscribers, remaining, index);
            Array.Copy(_subscribers, index + 1, remaining, index, _subscribers.Length - index - 1);
            _subscribers = remaining;
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly AgentStatusHub _hub;
        private Action<AgentStatus>? _onChanged;

        public Subscription(AgentStatusHub hub, Action<AgentStatus> onChanged)
        {
            _hub = hub;
            _onChanged = onChanged;
        }

        public void Dispose()
        {
            var handler = Interlocked.Exchange(ref _onChanged, null);
            if (handler is not null)
            {
                _hub.Unsubscribe(handler);
            }
        }
    }
}
