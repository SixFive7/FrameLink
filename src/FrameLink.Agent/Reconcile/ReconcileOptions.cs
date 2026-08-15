using System.Globalization;
using FrameLink.Agent.Link;

namespace FrameLink.Agent.Reconcile;

/// <summary>
/// The budgets and schedules of §2.4 and §2.5.
/// </summary>
/// <remarks>
/// Values, not logic — they can therefore come from the Fleet Manager (§2.2, decision 15)
/// without any of the objections that apply to a server-driven executor. Every default here is
/// a number rather than a behaviour, and none of them can change what the agent <i>does</i>.
/// </remarks>
public sealed record ReconcileOptions
{
    /// <summary>
    /// How many times one resource may be acted on before the budget is exhausted (§2.5 rung 2).
    /// </summary>
    /// <remarks>
    /// Five, because §2.7 item 5 shows "Attempt 2 of 5" as the example a person reads, and
    /// because at 40–60 s a boot-and-verify cycle five attempts is roughly four minutes — long
    /// enough for a transient to clear, short enough that a genuinely broken setting reaches a
    /// human before an hour of reboots has worn the card.
    /// </remarks>
    public int AttemptBudget { get; init; } = 5;

    /// <summary>
    /// How many exhausted budgets on one resource halt the device (§2.5 rung 4).
    /// </summary>
    /// <remarks>
    /// Two. The first exhaustion notifies; the operator's <b>retry</b> resets the budget; a
    /// second exhaustion means an administrator has been told more than once, which is the exact
    /// condition §2.5 names for <c>Halted</c>.
    /// </remarks>
    public int EscalationLimit { get; init; } = 2;

    /// <summary>Wait before the second attempt on a resource.</summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Ceiling on the per-resource backoff.
    /// </summary>
    /// <remarks>
    /// §2.4's reason for backoff is wear: "an unbounded retry cycle is more damaging than a
    /// stalled provision". Half an hour between attempts is well past the point where the frame
    /// is doing any harm, and the budget runs out long before the cap is reached anyway.
    /// </remarks>
    public TimeSpan BackoffCap { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How long the pre-reboot countdown runs (§2.7 item 4).</summary>
    public TimeSpan Countdown { get; init; } = CountdownDuration.Default;

    /// <summary>How long the loop sleeps between passes when nothing needs doing.</summary>
    /// <remarks>
    /// Level-triggered means a pass on a converged frame is a sweep of cheap observations and
    /// nothing else, so this is a drift-detection interval rather than a work interval (§2.2).
    /// </remarks>
    public TimeSpan PassInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The per-resource retry schedule these options describe.</summary>
    public Backoff RetrySchedule() => new(InitialBackoff, BackoffCap);
}

/// <summary>
/// Resolution of the countdown duration — §2.7 and decision 25.
/// </summary>
/// <remarks>
/// <para>
/// §2.7 gives three levels, "most specific winning: install-flag/boot file → fleet default →
/// per-device override", and decision 25 repeats the same list. Read literally the two halves
/// disagree, because a per-device override is more specific than a fleet default while an
/// install flag is more specific than either — so the list cannot be both least-specific-first
/// and most-specific-wins.
/// </para>
/// <para>
/// <b>The reading taken here is strongest-first</b>, matching §4.3's identically shaped
/// sentence about discovery ("in order: an install flag, a boot-partition file, then mDNS"),
/// where the install flag is unambiguously the strongest. It is also the only reading under
/// which the flag does its stated job: "development runs use 0" is worthless if a fleet default
/// of 25 overrides the flag on an adopted development frame, and an adopted development frame
/// is exactly what a mule is.
/// </para>
/// <para>
/// So: <b>install flag → boot file → per-device override → fleet default → 25 s</b>.
/// </para>
/// </remarks>
public static class CountdownDuration
{
    /// <summary>The built-in default of §2.7 item 4.</summary>
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(25);

    /// <summary>Command-line flag carrying the countdown, in seconds.</summary>
    public const string Flag = "--countdown-seconds";

    /// <summary>Command-line flag that forces the countdown to zero.</summary>
    public const string DevelopmentFlag = "--development";

    /// <summary>Environment variable equivalent of <see cref="Flag"/>.</summary>
    public const string Variable = "FL_COUNTDOWN_SECONDS";

    /// <summary>Key read from the boot-partition file.</summary>
    public const string BootFileKey = "countdown-seconds";

    /// <summary>Fleet setting key carrying the same value (§3.4).</summary>
    public const string SettingKey = "display.countdownSeconds";

    /// <summary>Resolves the duration from the levels above, strongest first.</summary>
    /// <param name="installFlag">Value from the command line or environment.</param>
    /// <param name="development">Whether the development flag was passed.</param>
    /// <param name="bootFile">Value from the boot-partition file.</param>
    /// <param name="fleetValue">Effective value from the Fleet Manager, override already applied.</param>
    public static TimeSpan Resolve(
        string? installFlag = null,
        bool development = false,
        string? bootFile = null,
        string? fleetValue = null)
    {
        if (development)
        {
            return TimeSpan.Zero;
        }

        return TryParse(installFlag)
            ?? TryParse(bootFile)
            ?? TryParse(fleetValue)
            ?? Default;
    }

    /// <summary>Reads the flag out of a command line, honouring both spellings.</summary>
    public static (string? Seconds, bool Development) ReadFlags(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        string? seconds = null;
        var development = false;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            if (string.Equals(argument, DevelopmentFlag, StringComparison.Ordinal))
            {
                development = true;
            }
            else if (string.Equals(argument, Flag, StringComparison.Ordinal))
            {
                seconds = index + 1 < arguments.Count ? arguments[index + 1] : null;
            }
            else if (argument.StartsWith(Flag + "=", StringComparison.Ordinal))
            {
                seconds = argument[(Flag.Length + 1)..];
            }
        }

        return (seconds, development);
    }

    private static TimeSpan? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Negative and unparseable both fall through to the next level rather than becoming
        // zero. A typo must not silently remove the one pause a person has to read the screen.
        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds >= 0
            && seconds <= 3600
                ? TimeSpan.FromSeconds(seconds)
                : null;
    }
}
