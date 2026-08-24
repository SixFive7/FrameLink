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
/// <b>Not jittered, and the removal was a decision rather than a simplification.</b> Every delay
/// here is now a pure function of the failure count, so two runs of the same build on the same
/// hardware wait exactly the same amount of time — which is what makes a difference in elapsed
/// time between two captured runs mean that something actually happened differently. The
/// operator's instruction was "no timers and jitter or anything", and the reason it matters
/// beyond tidiness is
/// <c>reference/reconcile-determinism.md</c> §3.1: the reconcile retry's delay is <i>persisted</i>
/// as <see cref="Reconcile.ResourceLedgerEntry.NextAttemptUtc"/> and the loop wakes at the
/// earliest pending one, so with two resources in backoff at the same time a jittered delay would
/// have decided which resource ran first. That was unreachable only because the attempt budget is
/// three and one escalation stops the pass — a guarantee held by a different number in a different
/// file. Removing the jitter removes the hazard outright rather than leaving it standing behind a
/// number nobody would think to check before changing.
/// </para>
/// <para>
/// <b>What it costs is stated rather than hidden.</b> A household power cut restarts every frame
/// at the same instant, and without jitter a fleet reconnects in lockstep: six frames behind one
/// router hand one self-hosted container six simultaneous handshakes, status reports and telemetry
/// flushes at 1 s, 2 s, 4 s … 30 s. That is survivable at this fleet's size and is the price of a
/// schedule a person can predict. If it ever stops being survivable, the shape to reach for is a
/// fraction derived from the device id — one code path, still a pure function, and spread across a
/// fleet because no two frames share an id.
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

    /// <summary>Creates a schedule.</summary>
    /// <param name="initial">Wait after the first failure.</param>
    /// <param name="cap">Hard ceiling.</param>
    public Backoff(TimeSpan? initial = null, TimeSpan? cap = null)
    {
        _initial = initial ?? DefaultInitial;
        _cap = cap ?? DefaultCap;
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

        return TimeSpan.FromTicks(ticks);
    }
}
