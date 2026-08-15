using System.Globalization;

namespace FrameLink.Agent.State;

/// <summary>
/// The countdown before a verifying reboot — §2.7 item 4.
/// </summary>
/// <param name="Total">How long the countdown runs.</param>
/// <param name="EndsAt">When it expires, so the bar genuinely counts down.</param>
/// <param name="Skippable">Whether the "Reboot now" affordance is offered.</param>
public readonly record struct CountdownState(TimeSpan Total, DateTimeOffset EndsAt, bool Skippable)
{
    /// <summary>How long is left at <paramref name="now"/>, clamped to the countdown.</summary>
    public TimeSpan Remaining(DateTimeOffset now)
    {
        var remaining = EndsAt - now;
        return remaining < TimeSpan.Zero ? TimeSpan.Zero
            : remaining > Total ? Total
            : remaining;
    }

    /// <summary>How much of the countdown has elapsed, as a fraction of its total.</summary>
    public double Elapsed(DateTimeOffset now) =>
        Total <= TimeSpan.Zero ? 1 : 1 - (Remaining(now).Ticks / (double)Total.Ticks);
}

/// <summary>
/// Everything §2.7 items 3–7 need about the reconciliation loop, in one place.
/// </summary>
/// <remarks>
/// <para>
/// Kept separate from <see cref="AgentStatus.Attempt"/> and its backoff fields, which belong to
/// the <i>connection</i> loop, because the two are genuinely different retries with genuinely
/// different schedules and a frame can be in both at once. Folding them together would make a
/// reconnect attempt render as a repair attempt.
/// </para>
/// <para>
/// A record rather than loose fields on the status so that "the loop is doing nothing" is one
/// null rather than six defaults that have to agree.
/// </para>
/// </remarks>
public sealed record ReconcileNarration
{
    /// <summary>Nothing is being reconciled.</summary>
    public static ReconcileNarration None { get; } = new();

    /// <summary>One of <c>FrameLink.Protocol.LoopStateNames</c>.</summary>
    public string? LoopState { get; init; }

    /// <summary>The resource being worked on.</summary>
    public string? Resource { get; init; }

    /// <summary>Which step of §2.3's contract it is in.</summary>
    public string? Phase { get; init; }

    /// <summary>Which attempt this is (§2.7 item 5).</summary>
    public int Attempt { get; init; }

    /// <summary>The budget the attempt counts against, so the screen can say "of 5".</summary>
    public int AttemptBudget { get; init; }

    /// <summary>How long the current backoff runs (§2.7 item 6).</summary>
    public TimeSpan BackoffTotal { get; init; }

    /// <summary>When the backoff expires, so remaining time can be rendered as it shrinks.</summary>
    public DateTimeOffset? BackoffEndsAt { get; init; }

    /// <summary>The pre-reboot countdown, when one is running (§2.7 item 4).</summary>
    public CountdownState? Countdown { get; init; }

    /// <summary>How many times the budget has been exhausted on this resource.</summary>
    public int Escalations { get; init; }

    /// <summary>Whether the Fleet Manager has actually received the escalation (§2.5).</summary>
    public bool AdminNotified { get; init; }

    /// <summary>Whether this device has stopped reconciling (§2.5 rung 4).</summary>
    public bool Halted { get; init; }

    /// <summary>Whether anything here is worth putting on the screen.</summary>
    public bool IsActive =>
        Countdown is not null
        || Halted
        || Escalations > 0
        || Attempt > 0
        || Resource is { Length: > 0 };

    /// <summary>One line naming the escalation state, or null (§2.7 item 7).</summary>
    public string? EscalationLine =>
        Halted
            ? "This frame has stopped trying. An administrator has been told more than once and "
                + "repeated restarts would do more harm than good."
        : Escalations > 0 && AdminNotified
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Gave up after {Attempt} attempts. Your Fleet Manager has been told and is waiting for you.")
        : Escalations > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Gave up after {Attempt} attempts. The Fleet Manager cannot be reached, so nobody has been told yet.")
        : null;
}
