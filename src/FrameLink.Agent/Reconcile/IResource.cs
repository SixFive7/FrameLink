using FrameLink.Protocol;

namespace FrameLink.Agent.Reconcile;

/// <summary>The status vocabulary of §2.3.</summary>
/// <remarks>
/// All six are reachable as of M2. The paths are worth naming because several of them only exist
/// in combination: <see cref="AwaitingReboot"/> requires the journal, <see cref="Blocked"/>
/// requires the DAG, and <see cref="Escalated"/> is distinguished from <see cref="Degraded"/>
/// only by whether the notification actually reached the Fleet Manager rather than the frame's
/// offline buffer.
/// </remarks>
public enum ResourceStatusKind
{
    /// <summary>Observed state matches desired state, verified.</summary>
    InSync,

    /// <summary>Being acted on right now, or waiting out a retry backoff.</summary>
    Progressing,

    /// <summary>Written, but not yet proven to have survived a boot (§2.4).</summary>
    AwaitingReboot,

    /// <summary>Attempt budget exhausted; carries the exact delta and attempt count (§2.5).</summary>
    Degraded,

    /// <summary>A dependency is not <see cref="InSync"/>, so this was not attempted (§2.2).</summary>
    Blocked,

    /// <summary>The operator has been notified and offered retry or a shell (§2.5).</summary>
    /// <remarks>
    /// <b>The terminal status. There is no rung below it</b> (§2.5 rung 6, decision 66). Either a
    /// human retries after fixing the cause — from the Fleet Manager or from the frame's own screen
    /// — or the resource stays here. The <c>Halted</c> that used to sit below this is gone from the
    /// design outright: it was the one state nothing recovered from on its own, the action that
    /// cleared it was the same retry that clears this one, and it never had a single scope — §2.5
    /// called it device-level while this enum defined it per resource.
    /// </remarks>
    Escalated,
}

/// <summary>Questions about a <see cref="ResourceStatusKind"/> that more than one layer asks.</summary>
public static class ResourceStatuses
{
    /// <summary>
    /// Whether the loop has stopped touching this resource — §2.5 rung 2's "stop".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written as an exclusion rather than as a list of the statuses that mean "gave up", and that
    /// is deliberate: it fails in the safe direction. A status nobody has thought about yet is read
    /// as stopped, which puts a still screen and a contact sentence in front of a person, rather
    /// than as progress, which animates a bar for work that may not be happening.
    /// </para>
    /// <para>
    /// The four excluded statuses are the four the loop can be in while it is still trying:
    /// verified, working, written and awaiting its verifying reboot, and waiting on a dependency.
    /// Everything beyond those is produced <i>only</i> at budget exhaustion.
    /// </para>
    /// </remarks>
    public static bool HasGivenUp(this ResourceStatusKind kind) =>
        kind is not (ResourceStatusKind.InSync
            or ResourceStatusKind.Progressing
            or ResourceStatusKind.AwaitingReboot
            or ResourceStatusKind.Blocked);
}

/// <summary>
/// How an Observe ended. <b>Three outcomes, not two.</b>
/// </summary>
/// <remarks>
/// <para>
/// §2.6: <b>rejection is an answer; silence is not</b>. A resource whose desired or observed
/// value can only be learned from the Fleet Manager has a third possible outcome that a boolean
/// cannot hold — <i>I could not determine the observed value</i> — and collapsing it into
/// "observed something wrong" is what produces a false diagnosis. The agent said "did not
/// survive the reboot" of a frame that was adopted the whole time and simply could not ask, and
/// then spent its attempt budget, its escalations and four reboots in twelve minutes proving it.
/// </para>
/// <para>
/// The distinction lives here rather than in any one resource because the reason it exists is
/// not about adoption: it belongs to every resource that consults something off the device.
/// </para>
/// </remarks>
public enum ObservationOutcome
{
    /// <summary>Observed equals desired.</summary>
    InSync,

    /// <summary>Observed was read, and it is not what it should be. This is drift.</summary>
    Drifted,

    /// <summary>
    /// The observed value could not be determined at all, so nothing may be concluded.
    /// </summary>
    /// <remarks>
    /// <b>Reserved for an authority that did not answer</b> — the Fleet Manager being
    /// unreachable is the whole of it today. It is <i>not</i> for a local read that failed: an
    /// Observe that cannot read a file or a sysfs node has learned something real about this
    /// machine, and <see cref="ReconcileLoop"/> turns even a thrown exception into
    /// <see cref="Drifted"/> for exactly that reason. A resource that genuinely cannot be applied
    /// must still escalate on the ordinary schedule, and this outcome must never become the place
    /// a real failure goes to be quiet.
    /// </remarks>
    Unevaluable,
}

/// <summary>What a single Observe found.</summary>
/// <param name="InSync">Whether observed equals desired.</param>
/// <param name="Expected">The desired value, as a person would read it.</param>
/// <param name="Observed">What is actually there.</param>
/// <remarks>
/// The positional constructor covers the two ordinary outcomes; <see cref="Unevaluable"/> is the
/// third. <see cref="InSync"/> stays <see langword="false"/> for an unevaluable observation on
/// purpose: anything that reads the flag and ignores <see cref="Outcome"/> then behaves as it did
/// before this type grew a third state — it treats the resource as drifted, which is noisy and
/// wrong but never silent. Failing towards a visible escalation is the safe direction to be wrong
/// in.
/// </remarks>
public sealed record ResourceObservation(bool InSync, string Expected, string Observed)
{
    /// <summary>Which of the three outcomes this is.</summary>
    public ObservationOutcome Outcome { get; private init; } =
        InSync ? ObservationOutcome.InSync : ObservationOutcome.Drifted;

    /// <summary>Expected-versus-observed in the one form §2.5 requires everywhere.</summary>
    /// <remarks>
    /// An unevaluable observation gets its own wording, and that wording is half the fix. This
    /// string is what reaches the log, the frame's screen and the Fleet Manager's device row, and
    /// the <i>observed</i> form of it — "expected 'adopted', observed 'waiting for adoption'" —
    /// asserts that a value was read and was wrong. Said of a frame that could not ask, it is the
    /// sentence that sends an operator hunting a persistence bug that does not exist.
    /// </remarks>
    public string Delta => Outcome is ObservationOutcome.Unevaluable
        ? $"expected '{Expected}', could not be determined: {Observed}"
        : $"expected '{Expected}', observed '{Observed}'";

    /// <summary>
    /// An observation that could not be made, because the authority that owns the value said
    /// nothing.
    /// </summary>
    /// <param name="expected">The desired value, as a person would read it.</param>
    /// <param name="why">
    /// Why it could not be determined, in plain language. It stands where the observed value
    /// would have gone, because there is no observed value — that is the point.
    /// </param>
    public static ResourceObservation Unevaluable(string expected, string why) =>
        new(false, expected, why) { Outcome = ObservationOutcome.Unevaluable };
}

/// <summary>
/// The change a resource made, in the two registers §2.7 item 3 asks for.
/// </summary>
/// <param name="Change">The exact command or file change, verbatim.</param>
/// <param name="Gloss">
/// One plain-language sentence for the person standing in front of the frame. Written by the
/// resource because only the resource knows what its change means; a gloss synthesised by the
/// loop could only ever restate the command in different words.
/// </param>
public readonly record struct ResourceAction(string Change, string Gloss);

/// <summary>The reconciler's verdict on one resource.</summary>
public sealed record ResourceStatus
{
    /// <summary>Stable identifier of the resource.</summary>
    public required string Name { get; init; }

    /// <summary>Where it stands.</summary>
    public required ResourceStatusKind Kind { get; init; }

    /// <summary>Expected-versus-observed, present whenever the kind is not <see cref="ResourceStatusKind.InSync"/>.</summary>
    public string? Delta { get; init; }

    /// <summary>How many times the resource has been acted on since it was last in sync.</summary>
    public int Attempts { get; init; }

    /// <summary>The budget those attempts count against, for "Attempt 2 of 5" (§2.7 item 5).</summary>
    public int AttemptBudget { get; init; }

    /// <summary>The exact change that was made, for the screen's "what is being done" (§2.7).</summary>
    public string? Action { get; init; }

    /// <summary>Plain-language gloss on <see cref="Action"/>.</summary>
    public string? Gloss { get; init; }

    /// <summary>Which dependency is holding this resource up (§2.2).</summary>
    public string? BlockedBy { get; init; }

    /// <summary>How many times the operator has been notified about this resource (§2.5).</summary>
    public int Escalations { get; init; }

    /// <summary>When the backoff ends, so a pause never reads as a hang (§2.7 item 6).</summary>
    public DateTimeOffset? NextAttemptUtc { get; init; }

    /// <summary>The wire spelling of <see cref="Kind"/>.</summary>
    public string WireStatus => Kind switch
    {
        ResourceStatusKind.InSync => ResourceStatusNames.InSync,
        ResourceStatusKind.Progressing => ResourceStatusNames.Progressing,
        ResourceStatusKind.AwaitingReboot => ResourceStatusNames.AwaitingReboot,
        ResourceStatusKind.Degraded => ResourceStatusNames.Degraded,
        ResourceStatusKind.Blocked => ResourceStatusNames.Blocked,
        _ => ResourceStatusNames.Escalated,
    };

    /// <summary>Turns this into the shape the Fleet Manager stores (§3.5).</summary>
    public ResourceReport ToReport() => new()
    {
        Name = Name,
        Status = WireStatus,
        Delta = Delta,
        Action = Action,
        BlockedBy = BlockedBy,
        Attempts = Attempts,
        AttemptBudget = AttemptBudget,
        Escalations = Escalations,
        NextAttemptUtc = NextAttemptUtc,
    };
}

/// <summary>
/// The resource contract of §2.3: <b>Observe → Compare → Act (only on drift) → Verify → Status</b>.
/// </summary>
/// <remarks>
/// <para>
/// Observe and Verify are deliberately the same method. §2.3 requires it, and the reason is
/// v1's governor bug: a check written against "did the write succeed" reports success while the
/// setting is quietly wrong. There is only one way to ask what is true, so a verify cannot
/// drift from an observe — and every guide CHECKPOINT becomes such a check for free, which is
/// what M3's state-diff harness consumes.
/// </para>
/// <para>
/// Compare is not a method either — it is the <see cref="ResourceObservation.InSync"/> flag the
/// observation already carries, because only the resource knows what equality means for its own
/// value.
/// </para>
/// <para>
/// There is no Reboot member and there never will be. §2.4: <b>every resource reboots, no
/// exceptions, no per-resource cleverness</b> — deciding which settings need one is exactly the
/// reasoning that produced the governor bug, so the decision is taken away from the resource
/// and lives in the loop.
/// </para>
/// </remarks>
public interface IResource
{
    /// <summary>Stable identifier, used in telemetry and on screen.</summary>
    string Name { get; }

    /// <summary>What was detected, in plain language, for §2.7's repair screen.</summary>
    string Detected { get; }

    /// <summary>Why it matters, in one short sentence.</summary>
    string WhyItMatters { get; }

    /// <summary>
    /// Ids that must be <see cref="ResourceStatusKind.InSync"/> before this is attempted (§2.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty is the catalog's <c>—</c>: the agent-version root and nothing else. It does
    /// <b>not</b> imply adoption. A resource names <c>agent.adoption</c> when its desired value is
    /// issued by the Fleet Manager and the catalog holds no default that is correct without it —
    /// when the frame would otherwise have to guess. A value the catalog fixes never names it, and
    /// neither does a fleet setting whose catalog default is right on an unadopted frame: it
    /// applies the default, and a later override is ordinary drift.
    /// </para>
    /// <para>
    /// The distinction is not pedantry. Read the other way it gates the package set on adoption,
    /// and §2.7's browser stage needs <c>chromium</c> and <c>labwc</c> to render the repair screen
    /// that a <i>pending</i> frame is required to be showing — fingerprint and serial included, so
    /// §3.3's operator can match a row to a frame on the bench. §3.3 withholds configuration from a
    /// pending device; a package set the catalog fixes is not configuration.
    /// </para>
    /// <para>
    /// Dependents of a resource that is not in sync are marked
    /// <see cref="ResourceStatusKind.Blocked"/> rather than being let loose to fail confusingly on
    /// their own.
    /// </para>
    /// </remarks>
    IReadOnlyList<string> DependsOn => [];

    /// <summary>Reads the world. Used both to decide and, unchanged, to verify.</summary>
    ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken);

    /// <summary>Applies the desired value, returning the exact change made and its gloss.</summary>
    ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Whether this resource is a <b>gate</b> — a precondition with no Act that could ever converge
    /// it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The shape §2.3's contract does not obviously have, made explicit rather than faked.</b>
    /// Observe → Compare → Act (only on drift) → Verify assumes that drift is something the frame
    /// can do something about. A few things are not: <i>is the hardware in this frame hardware this
    /// build has been told about?</i> has no command behind it, and the answer changes only when a
    /// person establishes what the hardware is and a maintainer ships a release. A resource like
    /// that has exactly one honest Act, which is none.
    /// </para>
    /// <para>
    /// <b>What the flag buys is the cost, not the conclusion.</b> Left as an ordinary resource, a
    /// gate drifts, is acted on, fails to converge, and spends the whole attempt budget with a
    /// reboot per attempt on its way to an escalation it had already earned on the first Observe —
    /// which is precisely the objection decision 90 raised against putting firmware in the graph,
    /// and it was an objection about waste rather than about the verdict. So the loop takes a
    /// drifted gate straight to §2.5 rung 2 with the budget declared spent: no Act, no reboot, one
    /// escalation, and decision 68 stops the pass around it exactly as it would for any other
    /// resource that has given up. That is the same route §2.6's conflict drift already takes, for
    /// the same reason — every remaining attempt is known in advance to buy nothing.
    /// </para>
    /// <para>
    /// <b>A gate's <see cref="ActAsync"/> is never called, and a gate should make sure of it.</b>
    /// Returning a no-op action would tell the loop a repair was applied; the verify would then read
    /// the same unchanged world and record a failed attempt, which is the cost this flag exists to
    /// avoid, arrived at by a different road. Throwing is the shape that fails loudly if the loop
    /// ever changes underneath it.
    /// </para>
    /// </remarks>
    bool IsGate => false;
}
