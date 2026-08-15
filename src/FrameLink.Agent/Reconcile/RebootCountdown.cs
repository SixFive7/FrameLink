using FrameLink.Agent.Hosting;
using FrameLink.Agent.State;

namespace FrameLink.Agent.Reconcile;

/// <summary>
/// The pause before a verifying reboot — §2.7 item 4.
/// </summary>
/// <remarks>
/// <para>
/// It exists so that a person can read what is about to happen before the screen goes away.
/// §2.7 calls the repair screen "deliberately paced, never silent", and a frame that writes a
/// setting and reboots inside the same second is silent in the only sense that matters: nobody
/// saw it.
/// </para>
/// <para>
/// <b>Skippable, from either side.</b> §2.7 asks for a "Reboot now" button — a tap on the
/// touchscreen, and the same skip available remotely — so the skip is a method rather than an
/// input device. Whatever surface offers the affordance calls <see cref="SkipNow"/>; this class
/// does not know or care which.
/// </para>
/// <para>
/// A zero-length countdown returns immediately without publishing anything, which is what
/// decision 25 means by "development runs use 0": not a fast countdown, no countdown.
/// </para>
/// </remarks>
public sealed class RebootCountdown
{
    /// <summary>How often the bar is repainted while counting.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(200);

    private readonly IAgentClock _clock;
    private int _skipRequested;

    /// <summary>Creates a countdown driven by <paramref name="clock"/>.</summary>
    public RebootCountdown(IAgentClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>How many countdowns have been skipped.</summary>
    public int Skips { get; private set; }

    /// <summary>Whether a countdown is running right now.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Asks the current — or the next — countdown to end at once.</summary>
    /// <remarks>
    /// Latching rather than edge-triggered on purpose: a press that lands in the microsecond
    /// between the write and the countdown starting must not be lost, because from the person's
    /// point of view they pressed the button and nothing happened.
    /// </remarks>
    public void SkipNow() => Interlocked.Exchange(ref _skipRequested, 1);

    /// <summary>Counts down, publishing the remaining time, and returns whether it was skipped.</summary>
    /// <param name="total">How long to wait. Zero returns immediately.</param>
    /// <param name="onRemaining">Called with the countdown state on every tick.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<bool> RunAsync(
        TimeSpan total,
        Action<CountdownState> onRemaining,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onRemaining);

        if (total <= TimeSpan.Zero)
        {
            Interlocked.Exchange(ref _skipRequested, 0);
            return false;
        }

        var endsAt = _clock.UtcNow + total;
        var state = new CountdownState(total, endsAt, Skippable: true);

        IsRunning = true;
        try
        {
            while (true)
            {
                if (Interlocked.Exchange(ref _skipRequested, 0) == 1)
                {
                    Skips++;
                    return true;
                }

                var remaining = endsAt - _clock.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }

                onRemaining(state);

                var step = remaining < TickInterval ? remaining : TickInterval;
                await _clock.DelayAsync(step, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            IsRunning = false;
        }
    }
}
