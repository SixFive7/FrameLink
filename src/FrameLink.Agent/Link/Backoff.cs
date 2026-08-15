using System.Security.Cryptography;

namespace FrameLink.Agent.Link;

/// <summary>
/// §4.1's reconnect schedule: capped exponential backoff, retry forever.
/// </summary>
/// <remarks>
/// <para>
/// <b>Capped</b> because the agent must never give up on a Fleet Manager. §2.6 draws the line:
/// silence is not an answer, so an unreachable server is a condition to keep trying through, not
/// a terminal state. An uncapped schedule would eventually put the retry interval into hours,
/// and a frame that takes four hours to notice its server came back is indistinguishable from a
/// broken one.
/// </para>
/// <para>
/// Jittered because a household power cut restarts every frame at the same instant, and a fleet
/// that reconnects in lockstep turns the operator's own recovery into a thundering herd.
/// </para>
/// </remarks>
public sealed class Backoff
{
    /// <summary>Wait after the first failure.</summary>
    public static readonly TimeSpan DefaultInitial = TimeSpan.FromSeconds(1);

    /// <summary>The ceiling the schedule never exceeds.</summary>
    public static readonly TimeSpan DefaultCap = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _initial;
    private readonly TimeSpan _cap;
    private readonly double _jitter;
    private readonly Func<double>? _fraction;

    /// <summary>Creates a schedule.</summary>
    /// <param name="initial">Wait after the first failure.</param>
    /// <param name="cap">Hard ceiling, before jitter.</param>
    /// <param name="jitter">Fraction of the delay that may be shaved off, 0 to 1.</param>
    /// <param name="fraction">
    /// Source of the jitter fraction in [0,1). Injected only so tests can pin the extremes;
    /// production uses the cryptographic generator, which sidesteps the seeded-<c>Random</c>
    /// lockstep problem entirely.
    /// </param>
    public Backoff(
        TimeSpan? initial = null,
        TimeSpan? cap = null,
        double jitter = 0.2,
        Func<double>? fraction = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(jitter);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(jitter, 1.0);

        _initial = initial ?? DefaultInitial;
        _cap = cap ?? DefaultCap;
        _jitter = jitter;
        _fraction = fraction;
    }

    /// <summary>The ceiling this schedule was built with.</summary>
    public TimeSpan Cap => _cap;

    /// <summary>How long to wait before attempt <paramref name="consecutiveFailures"/> + 1.</summary>
    public TimeSpan Delay(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return TimeSpan.Zero;
        }

        // Doubling in ticks with an early exit, rather than Math.Pow: at 40 consecutive
        // failures the naive expression overflows to a negative TimeSpan, which is a
        // once-a-month bug on a frame whose server is genuinely gone.
        var ticks = _initial.Ticks;
        var capTicks = _cap.Ticks;
        for (var step = 1; step < consecutiveFailures && ticks < capTicks; step++)
        {
            ticks *= 2;
        }

        if (ticks > capTicks || ticks < 0)
        {
            ticks = capTicks;
        }

        var shave = _jitter <= 0 ? 0 : (long)(ticks * _jitter * NextFraction());
        return TimeSpan.FromTicks(ticks - shave);
    }

    private double NextFraction() =>
        _fraction?.Invoke() ?? RandomNumberGenerator.GetInt32(0, 1000) / 1000.0;
}
