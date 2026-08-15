namespace FrameLink.Agent.Hosting;

/// <summary>
/// The agent's only source of time and of waiting.
/// </summary>
/// <remarks>
/// Every wait in the agent is a wait the design cares about — reconnect backoff, the hourly
/// update tick, the countdown before a verifying reboot. Routing them all through one seam
/// is what lets the tests assert the <i>schedule</i> (how long, how many times, capped where)
/// rather than sleeping through it.
/// </remarks>
public interface IAgentClock
{
    /// <summary>Current UTC instant.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>Waits for <paramref name="delay"/>, or until cancelled.</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>Real time, on top of <see cref="TimeProvider"/>.</summary>
public sealed class SystemAgentClock : IAgentClock
{
    private readonly TimeProvider _time;

    /// <summary>Creates a clock over <paramref name="timeProvider"/>, defaulting to the system one.</summary>
    public SystemAgentClock(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc/>
    public DateTimeOffset UtcNow => _time.GetUtcNow();

    /// <inheritdoc/>
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, _time, cancellationToken);
}
