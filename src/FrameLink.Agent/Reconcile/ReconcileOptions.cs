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
    /// <remarks>
    /// The fixed value, used when <see cref="CountdownSource"/> is not set. Tests set this
    /// directly; the agent sets the source instead, because on a frame the value moves.
    /// </remarks>
    public TimeSpan Countdown { get; init; } = CountdownDuration.Default;

    /// <summary>
    /// The countdown as the Fleet Manager currently has it, read at the moment of each reboot.
    /// </summary>
    /// <remarks>
    /// A delegate rather than a captured value, for the same reason <see cref="Resources.FleetValues"/>
    /// is one: the only configuration source left in the chain (decision 48) is the Fleet
    /// Manager, and its settings arrive <i>after</i> the agent has started and can change again
    /// while it runs. A duration resolved once at construction would read an empty settings map
    /// every time and pin every frame to <see cref="CountdownDuration.Default"/> for the life of
    /// the process, which would make the fleet setting dead configuration.
    /// </remarks>
    public Func<TimeSpan>? CountdownSource { get; init; }

    /// <summary>The countdown to run right now (§2.7 item 4).</summary>
    public TimeSpan CurrentCountdown() => CountdownSource is null ? Countdown : CountdownSource();

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
/// Resolution of the countdown duration — §2.7, decision 48 (which supersedes decision 25).
/// </summary>
/// <remarks>
/// <para>
/// <b>The chain, highest priority first: per-device override → fleet default → 60 s.</b> Both
/// configured levels are Fleet Manager settings, and both are resolved <i>on the server</i> by
/// <c>ISettingsStore.ResolveAsync</c>, which overlays a device's overrides onto the fleet
/// defaults before the values are pushed. So a frame receives one already-effective value, and
/// what this class resolves is that value against the built-in default.
/// </para>
/// <para>
/// <b>The install flag and the boot-partition file are gone.</b> Decision 25 put an install flag
/// above everything and a boot-partition file beside it; the operator considered that channel
/// and decided against it. The boot file went with it rather than surviving on its own — it
/// existed only as the local, pre-adoption sibling of the flag, so keeping it would have
/// preserved exactly the channel that was just removed. Configuration now has one source, which
/// is the plain reading of §3.4's "every setting is fleet-managed".
/// </para>
/// <para>
/// <b>The consequence, stated rather than hidden.</b> §3.3 gives a pending device nothing at
/// all, so an unadopted frame can only ever have the built-in 60 s. §2.7's "development runs
/// use 0" is therefore unreachable through configuration, and
/// <see cref="DevelopmentFlag"/> is what serves it instead — see its own remarks.
/// </para>
/// </remarks>
public static class CountdownDuration
{
    /// <summary>The built-in default of §2.7 item 4 — 60 s by decision 48.</summary>
    /// <remarks>
    /// This is the whole floor of the chain, and on an unadopted frame it is the entire chain
    /// (§3.3), so it has to be a duration that reads well with nothing else set: long enough for
    /// somebody who has just walked up to the frame to read what it is about to do.
    /// </remarks>
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(60);

    /// <summary>Command-line switch that forces the countdown to zero.</summary>
    /// <remarks>
    /// <para>
    /// <b>A local debugging switch, not a configuration channel.</b> It is an argument to the
    /// binary — chosen by whoever starts the process on the machine they are sitting at — so it
    /// carries none of what made the install flag a configuration channel: nothing writes it,
    /// nothing persists it, no operator sets it from the Fleet Manager, and it survives no
    /// restart the person did not perform themselves. That is why removing the flag (decision 48)
    /// and keeping this are not in tension.
    /// </para>
    /// <para>
    /// It exists because §2.7's "development runs use 0" is otherwise unreachable: both remaining
    /// levels are Fleet Manager settings and a pending frame receives none of them, so a mule
    /// being provisioned from scratch would sit through 60 s per resource with nobody watching.
    /// </para>
    /// </remarks>
    public const string DevelopmentFlag = "--development";

    /// <summary>The fleet setting key carrying the countdown, in seconds (§3.4).</summary>
    /// <remarks>
    /// Matches the operator-facing catalog in the Fleet Manager GUI
    /// (<c>gui/src/lib/settings-catalog.ts</c>), which is the only place a human ever types this
    /// string. A key the agent reads but nobody can set is not a setting.
    /// </remarks>
    public const string SettingKey = "repair.countdownSeconds";

    /// <summary>Resolves the duration from the levels above, strongest first.</summary>
    /// <param name="fleetValue">
    /// The effective value from the Fleet Manager — the device's override if it has one,
    /// otherwise the fleet default, resolved server-side; null when the frame is unadopted or
    /// has never been told.
    /// </param>
    /// <param name="development">Whether <see cref="DevelopmentFlag"/> was passed.</param>
    public static TimeSpan Resolve(string? fleetValue = null, bool development = false) =>
        development ? TimeSpan.Zero : TryParse(fleetValue) ?? Default;

    /// <summary>Whether <see cref="DevelopmentFlag"/> appears in a command line.</summary>
    public static bool IsDevelopmentRun(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        foreach (var argument in arguments)
        {
            if (string.Equals(argument, DevelopmentFlag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static TimeSpan? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Negative and unparseable both fall through to the built-in default rather than
        // becoming zero. A typo must not silently remove the one pause a person has to read the
        // screen — and with the flag gone, a mistyped fleet setting is the only way this can be
        // reached, so falling through to 60 s is the whole safety net.
        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds >= 0
            && seconds <= 3600
                ? TimeSpan.FromSeconds(seconds)
                : null;
    }
}

/// <summary>
/// <b>Which reboots the countdown applies to</b> — §2.7, decision 51.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CountdownDuration"/> answers <i>how long</i>; this answers <i>whether at all</i>.
/// The two are separate because they have different reasons: the duration is configuration and
/// moves per fleet and per device, while the scope is a property of what the countdown is for.
/// </para>
/// <para>
/// <b>The countdown is a pause for a reader.</b> §2.7 item 4 puts it before the verifying reboot
/// so a person can read what is being done before the screen changes, with a "Reboot now" button
/// to skip once read. That argument is about somebody standing in front of a working frame
/// watching a repair — there is a viewer, and there is a product being taken away from them.
/// </para>
/// <para>
/// <b>Initial provisioning has neither.</b> A frame that has never been green has never displayed
/// anything, nobody is waiting in front of it, and no product is being interrupted. Sixty seconds
/// of reading time per resource is a pause with no reader, and at 79 resources it is 79 minutes
/// of it — roughly three quarters of the whole provision spent waiting for nobody. So a frame
/// that has never reached <c>InSync</c> reboots as soon as a resource is applied; once it has
/// been green, every later repair gets the full countdown, because then §2.7's transparency
/// argument holds completely.
/// </para>
/// <para>
/// <b>The condition is durable, not inferred.</b> It reads
/// <see cref="ReconcileJournalState.FirstInSyncUtc"/> — persisted beside the attempt ledger under
/// <c>/var/lib/fl-agent</c> — so it survives the reboot every resource takes and the version
/// change every update brings. Anything derived from process state would reset on every boot and
/// hand a living-room frame the provisioning behaviour.
/// </para>
/// <para>
/// <b>Reverting is one line.</b> Everything this decision changes is
/// <see cref="ForReboot"/>; returning its configured duration unconditionally restores the
/// behaviour decision 48 described, with no other edit anywhere.
/// </para>
/// </remarks>
public static class CountdownScope
{
    /// <summary>The countdown this reboot actually gets.</summary>
    /// <param name="configured">
    /// What decision 48's chain resolved to — the effective fleet value, the built-in 60 s, or
    /// zero when <see cref="CountdownDuration.DevelopmentFlag"/> was passed. That switch already
    /// forces zero upstream of this, and keeps doing so: this can only ever take a countdown
    /// away, never add one.
    /// </param>
    /// <param name="hasEverBeenInSync">
    /// Whether this frame has ever had every resource verified at once — <see
    /// cref="ReconcileJournalState.FirstInSyncUtc"/> being set.
    /// </param>
    public static TimeSpan ForReboot(TimeSpan configured, bool hasEverBeenInSync) =>
        hasEverBeenInSync ? configured : TimeSpan.Zero;
}
