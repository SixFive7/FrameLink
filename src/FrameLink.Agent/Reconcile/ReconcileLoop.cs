using System.Globalization;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.State;
using FrameLink.Agent.Telemetry;
using FrameLink.Protocol;

namespace FrameLink.Agent.Reconcile;

/// <summary>How one pass of the loop ended.</summary>
public enum PassResult
{
    /// <summary>Every resource is in sync. Nothing was changed.</summary>
    Converged,

    /// <summary>Something is drifted and waiting out a backoff, or blocked behind one.</summary>
    Pending,

    /// <summary>A change was made and the machine rebooted inside this process (§5.3).</summary>
    Rebooted,

    /// <summary>A change was made and the machine is going down. Nothing may be claimed.</summary>
    Restarting,

    /// <summary>
    /// A resource exhausted its budget; the operator has been told, or will be.
    /// </summary>
    /// <remarks>
    /// <b>Terminal, and the whole pass stops here</b> (§2.5 rungs 4 and 6, decisions 66 and 68).
    /// Nothing further is observed, acted on or rebooted for until a human retries — from the
    /// Fleet Manager or from the frame's own screen. There is deliberately no rung below this: a
    /// second, deader state added no recovery path the retry did not already provide, and gave a
    /// frame a way to become unreachable while nobody was watching.
    /// </remarks>
    Escalated,

    /// <summary>The agent is shutting down.</summary>
    Cancelled,
}

/// <summary>What one pass determined.</summary>
public sealed record PassOutcome
{
    /// <summary>How it ended.</summary>
    public required PassResult Result { get; init; }

    /// <summary>Every resource's standing, in dependency order.</summary>
    public required IReadOnlyList<ResourceStatus> Statuses { get; init; }

    /// <summary>The earliest moment a resource becomes eligible again, if any is waiting.</summary>
    public DateTimeOffset? NextAttemptUtc { get; init; }

    /// <summary>Plain-language elaboration, when there is one.</summary>
    public string? Detail { get; init; }
}

/// <summary>Everything the loop needs. Grouped so the constructor stays readable.</summary>
public sealed record ReconcileServices
{
    /// <summary>The validated, ordered catalog.</summary>
    public required ResourceGraph Graph { get; init; }

    /// <summary>The durable journal and attempt ledger.</summary>
    public required ReconcileJournal Journal { get; init; }

    /// <summary>How the agent tells a reboot from a service restart.</summary>
    public required IBootIdentity Boot { get; init; }

    /// <summary>How the machine is restarted (§2.4).</summary>
    public required IRebootBoundary Reboots { get; init; }

    /// <summary>The pre-reboot pause (§2.7 item 4).</summary>
    public required RebootCountdown Countdown { get; init; }

    /// <summary>Where loop state and events go (§4.1).</summary>
    public required IReconcileTelemetry Telemetry { get; init; }

    /// <summary>The shared status holder the console stage renders.</summary>
    public required AgentStatusHub Hub { get; init; }

    /// <summary>Source of time and of waiting.</summary>
    public required IAgentClock Clock { get; init; }

    /// <summary>The journal, in the second sense.</summary>
    public required IAgentLog Log { get; init; }

    /// <summary>Budgets and schedules.</summary>
    public ReconcileOptions Options { get; init; } = new();

    /// <summary>
    /// The interlock this loop shares with supervision (§2.10), or null where there is none.
    /// </summary>
    /// <remarks>
    /// Optional so that a test of the loop's own mechanics does not have to build a supervisor,
    /// and null-safe throughout: with no interlock the loop behaves exactly as it did before §2.10
    /// existed, which is the honest default for a catalog with nothing supervised in it.
    /// </remarks>
    public Supervise.SupervisionInterlock? Interlock { get; init; }
}

/// <summary>
/// <b>The reconciliation loop of §2.2</b> — level-triggered, sequential, single-threaded, and
/// resumable across the reboot every resource takes.
/// </summary>
/// <remarks>
/// <para>
/// <b>One code path.</b> §2.2 requires that provisioning a bare frame and repairing a drifted
/// one be the same code, and that is a structural property here rather than an intention: there
/// is no install mode, no first-run branch and no "already applied" shortcut. A pass observes,
/// acts only where it finds drift, and stops. On a bare frame it finds drift everywhere; on a
/// converged frame it finds none and changes nothing. Neither case is special.
/// </para>
/// <para>
/// <b>Every resource reboots (§2.4).</b> There is no per-resource opt-out and no heuristic for
/// which settings "need" one — that reasoning is exactly what produced v1's governor bug, where
/// the kernel parameter reached <c>/proc/cmdline</c> and the governor still came up
/// <c>ondemand</c>. The loop therefore writes its intent to the journal, crosses the reboot
/// boundary, and verifies on the other side. The boot identity is compared before anything is
/// claimed, so a process that merely restarted cannot pass itself off as a machine that booted.
/// </para>
/// <para>
/// <b>One diagnosis per change (§1.2.5).</b> A pass acts on at most one resource. It keeps
/// <i>observing</i> after that, so the status list and the telemetry report stay complete, but
/// nothing else is touched until the change that was made has been proven or has failed.
/// </para>
/// <para>
/// <b>The loop is willing to give up (§2.5).</b> Failure walks a ladder: retry with growing
/// delay, then the budget of three is exhausted → stop touching it and mark <c>Degraded</c> with
/// the exact delta and attempt count, then the notification → <c>Escalated</c>, which is where the
/// ladder ends (decision 66). Either a human retries after fixing the cause, or the resource stays
/// escalated. Continuing to reboot a persistently broken frame is damage, not diligence.
/// </para>
/// <para>
/// <b>An escalation stops the whole pass, not just that resource (decision 68).</b> §2.6 already
/// stops the product for any drift, so converging the remaining seventy resources delivers nothing
/// to the household — the frame is equally unusable at 47 in sync and at 68. And the attempt budget
/// is <i>per resource</i>, so one shared cause is multiplied by however many resources share it:
/// measured on the frame, one 350 ms race across five resources cost 41 reboots. Stopping at the
/// first escalation makes that multiplication structurally impossible rather than merely bounded.
/// The honest cost, stated rather than hidden: a first provision carrying N unrelated faults now
/// takes N round trips through a person.
/// </para>
/// <para>
/// <b>Stopping means stopping acting, not stopping looking (decision 76).</b> A stopped frame
/// walks the whole catalog exactly as a running one does with its one change already spent: every
/// resource is observed, nothing is acted on, no reboot is taken and no attempt is spent. So the
/// report an operator reads is what this frame <i>is</i> — which on the frame that prompted this
/// was 76 resources in sync behind one that had given up, reported as 0 of 79 because the pass
/// returned at the escalation and labelled every unreached row <c>Blocked</c> by the resource that
/// stopped it, dependency or not. <b>A stopped frame must not render as a broken one</b> any more
/// than a broken one may render as working (§2.6).
/// </para>
/// <para>
/// <b>The pass stopping is not the loop stopping (decision 75).</b> <see cref="RunAsync"/>
/// schedules another pass after an escalation, because that tick is the only thing that can notice
/// a budget a retry has reset — see <see cref="EndsTheLoop"/>.
/// </para>
/// <para>
/// <b>A repair that works and does not last is a different failure from one that never works
/// (decision 78).</b> Everything above counts attempts, and an attempt counter can only ever count
/// <i>failures</i> — so a change that applies, verifies across a reboot, and is undone afterwards by
/// a second owner leaves it nothing to count. Measured on the frame: a mixer value put back by the
/// login session a fraction of a second after the post-boot verify read it, with the verify winning
/// that race most boots and clearing the ledger every time it did. The loop therefore keeps a second
/// counter with the opposite lifetime — <see cref="ResourceLedgerEntry.Reversions"/>, which survives
/// a successful verify and is cleared only by a value that genuinely holds — and treats a run of
/// them as §2.6's <b>conflict drift</b>: maximally serious, not acted on again, straight to rung 2.
/// See <see cref="NoteDrift"/> and <see cref="GiveUpOnConflictAsync"/>.
/// </para>
/// <para>
/// <b>And underneath all of it, a floor that counts nothing but reboots</b> —
/// <see cref="RebootFloor"/>, decision 79. It is not in this class and shares no state with the
/// ladder, because a safety net keyed on the same fact as the thing it is protecting is not a
/// safety net.
/// </para>
/// </remarks>
public sealed class ReconcileLoop
{
    /// <summary>
    /// What a resource is <c>Blocked</c> on when its observation could not be made (§2.6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// An unevaluable observation lands on <see cref="ResourceStatusKind.Blocked"/> rather than
    /// on a new rung, and the fit is exact rather than convenient: §2.2 defines <c>Blocked</c> as
    /// "this was not attempted, because something it depends on is not in sync", which is
    /// precisely a resource waiting on an authority that has not answered. The one thing it is
    /// not is a dependency the DAG can express, because it is not on this device — so it is named
    /// here instead, in the words the console already renders as <i>"waiting for the Fleet
    /// Manager"</i>.
    /// </para>
    /// <para>
    /// <c>Blocked</c> carries the two properties this case needs and nothing else does: it
    /// consumes no attempt, and it propagates, so every resource that depends on adoption is
    /// blocked behind it for the length of an outage instead of half-applying settings it was
    /// never issued.
    /// </para>
    /// </remarks>
    public const string SilentAuthority = "the Fleet Manager";

    private readonly ReconcileServices _services;
    private readonly Link.Backoff _retry;
    private string _deviceId = "unknown";

    /// <summary>Creates the loop over <paramref name="services"/>.</summary>
    public ReconcileLoop(ReconcileServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _services = services;
        _retry = services.Options.RetrySchedule();
    }

    /// <summary>How many passes have completed.</summary>
    public int Passes { get; private set; }

    /// <summary>The device id stamped onto telemetry.</summary>
    public string DeviceId
    {
        get => _deviceId;
        set => _deviceId = value ?? "unknown";
    }

    /// <summary>
    /// Whether this device has stopped reconciling and is waiting for a person (§2.5 rung 4).
    /// </summary>
    /// <remarks>
    /// One resource that has given up stops the whole frame (decision 68), so this reads the
    /// ledger for <i>any</i> entry in that state rather than for a device-level flag. There is no
    /// such flag any more, and its absence is the point: a stored "this device is halted" bit was
    /// a second source of truth about the same fact, and the fact is already durable in the
    /// attempts and escalations the ledger keeps per resource.
    /// </remarks>
    public bool HasStopped =>
        _services.Journal.Read().Ledger.Any(entry => HasGivenUp(entry, _services.Options.AttemptBudget));

    /// <summary>
    /// Resets one resource's attempt budget — §2.5 rung 3's <b>retry</b> action.
    /// </summary>
    /// <remarks>
    /// Resetting the attempts is the whole of it, and it is what un-stops the frame: the loop
    /// decides a resource has given up from its attempts against the budget, so zeroing them puts
    /// it back in the ordinary walk with a fresh three. The escalation <i>count</i> is deliberately
    /// kept, so a frame that has already been given up on once does not start the ladder from the
    /// bottom — the second escalation still says "this has happened before".
    /// </remarks>
    public void ResetBudget(string resource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);

        _services.Journal.Update(state =>
        {
            var entry = ReconcileJournal.EntryFor(state, resource);
            return ReconcileJournal.WithEntry(state, entry with
            {
                Attempts = 0,
                NextAttemptUtc = null,

                // Decision 78's counter is cleared by a retry for the same reason the attempts
                // are, and leaving it would be worse than pointless: a resource carrying its
                // conflict count into the next pass re-escalates before it is ever acted on, so
                // the retry would visibly do nothing on exactly the frame it was pressed for —
                // which is the failure decision 75 records at length.
                Reversions = 0,
                HeldExpected = null,
                HeldSinceUtc = null,
            });
        });

        ForgetReboots();

        _services.Log.Info($"{resource}: the attempt budget was reset by the Fleet Manager.");
    }

    /// <summary>
    /// Clears the device's reboot history — decision 79's floor, reset by the same press.
    /// </summary>
    /// <remarks>
    /// It lives here rather than on <see cref="RebootFloor"/> because the journal is what both
    /// read, and reaching through <see cref="ReconcileServices.Reboots"/> would mean type-testing an
    /// interface for one implementation. A frame that has not reached the floor loses nothing by
    /// this, and a frame that has is precisely the one whose retry has to work.
    /// </remarks>
    private void ForgetReboots()
    {
        if (_services.Journal.Read().Reboots.Count == 0)
        {
            return;
        }

        _services.Journal.Update(state => state with { Reboots = [] });
        _services.Log.Info("The reboot floor was cleared: this frame may take a full window of reboots again.");
    }

    /// <summary>
    /// Resets every resource that has given up — the device-wide form of §2.5 rung 3's
    /// <b>retry</b>.
    /// </summary>
    /// <returns>The resources whose budgets were reset, in ledger order.</returns>
    /// <remarks>
    /// <para>
    /// Rung 4 stops the <i>frame</i>, and a stopped frame can have more than one resource that
    /// gave up — <see cref="WalkAsync"/> deliberately reports all of them, because clearing one
    /// while another remains would otherwise look like it had done nothing. This is the verb that
    /// matches that noun: an operator looking at a frame that has stopped reconciling asks it to
    /// try again, without having to name each setting that contributed.
    /// </para>
    /// <para>
    /// Its membership test is <see cref="HasGivenUp"/>, the same predicate the walk skips on, so
    /// the two cannot drift into a state where something is skipped forever and never appears in
    /// the set a retry would clear.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> ResetExhaustedBudgets()
    {
        List<string>? names = null;

        foreach (var entry in _services.Journal.Read().Ledger)
        {
            if (HasGivenUp(entry, _services.Options.AttemptBudget))
            {
                (names ??= []).Add(entry.Resource);
            }
        }

        if (names is null)
        {
            // The reboot floor is still cleared. A frame can be holding at decision 79's floor with
            // nothing yet escalated — the refusal spends attempts one pass at a time — and that is
            // exactly the moment an operator watching it reboot presses retry.
            ForgetReboots();

            _services.Log.Info("The Fleet Manager asked this frame to try again; nothing had given up.");
            return [];
        }

        foreach (var name in names)
        {
            ResetBudget(name);
        }

        return names;
    }

    /// <summary>
    /// Whether the loop has stopped touching this resource — §2.5 rung 2's "stop".
    /// </summary>
    /// <remarks>
    /// <para>
    /// One predicate with three readers: the walk, which must not observe or act on a resource in
    /// this state; <see cref="ResetExhaustedBudgets"/>, which must be able to name every resource
    /// in it; and <see cref="HasStopped"/>, which decides whether the frame does anything at all
    /// this pass. Written three times they could disagree, and the disagreement has one direction
    /// — something the walk refuses to touch that a retry cannot reach — which is a frame nothing
    /// can recover.
    /// </para>
    /// <para>
    /// Both halves are required. An escalation on the record alone would be wrong after a retry,
    /// which resets the attempts and deliberately keeps the escalation count; spent attempts alone
    /// would be wrong for a resource mid-ladder that has not reached rung 2 yet.
    /// </para>
    /// </remarks>
    public static bool HasGivenUp(ResourceLedgerEntry entry, int attemptBudget)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.Escalations > 0 && AttemptsWithin(entry, attemptBudget) >= attemptBudget;
    }

    /// <summary>
    /// A stored attempt count as the <i>current</i> budget can express it (decision 74).
    /// </summary>
    /// <param name="attempts">What the ledger holds.</param>
    /// <param name="attemptBudget">The budget in force right now.</param>
    /// <remarks>
    /// <para>
    /// <b>The attempt ledger is durable and the budget is not.</b> §2.1 persists attempts across
    /// the reboot every resource takes, so a frame provisioned under decision 7's budget of five
    /// carries counts of four and five into decision 67's budget of three. Measured on the frame,
    /// that produced <c>att=5/3</c> in the live report — a pair that cannot be true, and one that
    /// an operator reasonably reads as a counter that has run away.
    /// </para>
    /// <para>
    /// <b>Clamping is on read only; nothing rewrites the journal to fit.</b> Resetting stored
    /// counters would silently un-escalate frames whose operator has already been notified, which
    /// is a worse failure than an incoherent number. What this does is narrower: every place that
    /// <i>compares</i> a count against the budget or <i>shows</i> it beside the budget uses the
    /// value the budget can express, so the two halves of every "attempt N of M" agree.
    /// </para>
    /// <para>
    /// <b>A budget reduction is therefore retroactive by design.</b> A resource that has already
    /// spent four attempts escalates on its next failure under a policy allowing three — it does
    /// not receive three fresh ones. That is the new policy applied rather than a defect: the
    /// operator lowered the budget because attempts cost card wear (decision 67), and a resource
    /// that has already spent more than the new budget is precisely the one that policy is about.
    /// The recovery from being wrong about it is unchanged and is one press: a retry grants a
    /// fresh, whole budget.
    /// </para>
    /// <para>
    /// It can only ever <i>understate</i> what a frame has spent, never overstate it, which is the
    /// safe direction: the true history stays in the journal and in the escalation events on the
    /// <c>events</c> channel, neither of which this touches.
    /// </para>
    /// </remarks>
    public static int AttemptsWithin(int attempts, int attemptBudget) =>
        attempts < 0 ? 0
        : attemptBudget > 0 && attempts > attemptBudget ? attemptBudget
        : attempts;

    /// <summary>One ledger entry's attempts, as <paramref name="attemptBudget"/> can express them.</summary>
    public static int AttemptsWithin(ResourceLedgerEntry entry, int attemptBudget)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return AttemptsWithin(entry.Attempts, attemptBudget);
    }

    /// <summary>
    /// Whether a pass result ends the loop, rather than scheduling another pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two results end it, and both end it because the process itself is going away.</b>
    /// <see cref="PassResult.Restarting"/> means the machine is going down to prove a change, so
    /// there is nothing left to run and §2.4 forbids claiming anything on the other side of it from
    /// this process. <see cref="PassResult.Cancelled"/> is the agent shutting down. Everything else
    /// schedules another pass.
    /// </para>
    /// <para>
    /// <b><see cref="PassResult.Escalated"/> is emphatically not one of them, and this predicate
    /// exists because it used to be</b> (decision 75). It inherited the slot <c>Halted</c> held
    /// before decision 66 removed that state, and for <c>Halted</c> returning was right: it was
    /// device-level and terminal by definition. <c>Escalated</c> is §2.5 rung 3 — the rung whose
    /// entire purpose is that an operator presses <b>retry</b> and the frame tries again — so a
    /// loop that returns on it deletes the recovery path the rung was built for. Measured on the
    /// frame: thirty-three minutes of log holding one startup pass, then the retry lines, then
    /// silence against a five-minute <see cref="ReconcileOptions.PassInterval"/>; the ledger's
    /// telemetry sequence frozen at the server's last report; the process sleeping, unrestarted,
    /// its socket established. Nine other loops kept the agent alive and the Fleet Manager
    /// therefore reported a device that was <i>online</i> and permanently inert, with a retry
    /// button that visibly did nothing.
    /// </para>
    /// <para>
    /// <b>Written as a named predicate rather than inline, because the failure was a list.</b> The
    /// terminal set was three enum members in a pattern, and removing a state left a non-terminal
    /// one sitting in the terminal position with nothing to notice. A predicate with this remark
    /// attached is the thing that makes the next such edit visible.
    /// </para>
    /// </remarks>
    public static bool EndsTheLoop(PassResult result) =>
        result is PassResult.Restarting or PassResult.Cancelled;

    /// <summary>Runs passes until the machine restarts or the agent stops.</summary>
    /// <remarks>
    /// The wait between passes is the shortest of the pending backoffs, so a frame that is
    /// retrying in thirty seconds does not sit idle for the whole drift-detection interval. A frame
    /// that has given up has no backoff to shorten it, so it ticks at the full
    /// <see cref="ReconcileOptions.PassInterval"/> — which is what picks up a retry, and what keeps
    /// it reporting what it is while it waits for one (§2.6, decisions 68 and 75).
    /// </remarks>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var outcome = await RunPassAsync(cancellationToken).ConfigureAwait(false);

            if (EndsTheLoop(outcome.Result))
            {
                return;
            }

            // A pass that rebooted has more to do and nothing to wait for: on a frame the
            // process would already be gone, so the immediate next pass is what the next boot
            // would have run.
            if (outcome.Result == PassResult.Rebooted)
            {
                continue;
            }

            var wait = _services.Options.PassInterval;
            if (outcome.NextAttemptUtc is { } next)
            {
                var until = next - _services.Clock.UtcNow;
                if (until > TimeSpan.Zero && until < wait)
                {
                    wait = until;
                }
                else if (until <= TimeSpan.Zero)
                {
                    continue;
                }
            }

            try
            {
                await _services.Clock.DelayAsync(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Runs exactly one pass.</summary>
    public async Task<PassOutcome> RunPassAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return await FinishAsync(PassResult.Cancelled, [], null, null, CancellationToken.None).ConfigureAwait(false);
        }

        Passes++;

        await AnnounceBootAsync(cancellationToken).ConfigureAwait(false);

        // Before anything is resumed or acted on. An escalation stops the *frame* (decision 68),
        // so one inherited from an earlier process has to be known at this pass's very first
        // instruction: a resume acts and reboots, and a per-resource check inside the walk would
        // let every resource ordered ahead of the escalated one be acted on and rebooted for, on
        // every boot, forever. That is the durability the ledger is for.
        //
        // What it does *not* do any more is end the pass. The walk runs, in observe-only mode, and
        // publishes what it actually finds — see WalkAsync (decision 76).
        if (!HasStopped)
        {
            var resumed = await ResumePendingAsync(cancellationToken).ConfigureAwait(false);
            if (resumed is not null)
            {
                return resumed;
            }
        }

        return await WalkAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rows for resources that gave up under a catalog this build no longer has.
    /// </summary>
    /// <remarks>
    /// They keep the frame stopped, because <see cref="HasStopped"/> reads the ledger rather than
    /// the graph, so they have to be named: a frame held by a resource nothing renders is a frame
    /// whose screen cannot explain itself. They cannot be observed — there is no resource to ask —
    /// so the ledger's own last record is the whole of what can be said about them.
    /// </remarks>
    private List<ResourceStatus>? OrphanedGiveUps()
    {
        List<ResourceStatus>? orphans = null;

        foreach (var entry in _services.Journal.Read().Ledger)
        {
            if (!HasGivenUp(entry, _services.Options.AttemptBudget)
                || _services.Graph.Find(entry.Resource) is not null)
            {
                continue;
            }

            (orphans ??= []).Add(Terminal(
                entry.Resource,
                entry.EscalationNotified ? ResourceStatusKind.Escalated : ResourceStatusKind.Degraded,
                entry));
        }

        return orphans;
    }

    /// <summary>
    /// Says on the <c>events</c> channel that this is a new boot.
    /// </summary>
    /// <remarks>
    /// §4.1 lists boot alongside drift and escalation as an event, and it is the one that makes
    /// the other two readable: an escalation two boots after a drift means something different
    /// from two escalations in one boot, and only a boot marker in the stream distinguishes them.
    /// </remarks>
    private async Task AnnounceBootAsync(CancellationToken cancellationToken)
    {
        var state = _services.Journal.Read();
        var current = _services.Boot.Current;

        if (string.Equals(state.LastBootId, current, StringComparison.Ordinal))
        {
            return;
        }

        _services.Journal.Update(existing => existing with { LastBootId = current });

        var crossing = state.Pending is { } pending
            && !string.Equals(pending.BootId, current, StringComparison.Ordinal);

        await EmitAsync(
            DeviceEventKinds.Boot,
            resource: state.Pending?.Resource,
            summary: crossing
                ? $"Booted, and came back to verify '{state.Pending!.Resource}'."
                : "Booted.",
            delta: null,
            attempts: AttemptsWithin(state.Pending?.Attempt ?? 0, _services.Options.AttemptBudget),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Picks up a change that was written before the machine went down (§2.4).
    /// </summary>
    /// <returns>
    /// A completed pass when the reboot has to be requested again, otherwise null — in which
    /// case the ledger has been updated and the ordinary walk continues.
    /// </returns>
    private async Task<PassOutcome?> ResumePendingAsync(CancellationToken cancellationToken)
    {
        var state = _services.Journal.Read();
        if (state.Pending is not { } pending)
        {
            return null;
        }

        var resource = _services.Graph.Find(pending.Resource);
        if (resource is null)
        {
            // The catalog no longer contains it — an agent update dropped or renamed the
            // resource. There is nothing to verify and nothing to claim.
            _services.Log.Warn($"The journal names '{pending.Resource}', which this build's catalog does not have. Forgetting it.");
            _services.Journal.Update(existing => existing with { Pending = null });
            return null;
        }

        if (string.Equals(pending.BootId, _services.Boot.Current, StringComparison.Ordinal))
        {
            // The machine did not reboot; this process merely restarted. §2.4 forbids claiming
            // anything from that, so the reboot is requested again rather than the change being
            // verified against a value that has never had to survive anything.
            _services.Log.Warn(
                $"{resource.Name}: the agent restarted without a reboot, so '{pending.Change}' is still unproven. Asking again.");

            return await CrossAndVerifyAsync(
                    resource,
                    AttemptsWithin(pending.Attempt, _services.Options.AttemptBudget),
                    pending.Change,
                    pending.Gloss,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        _services.Journal.Update(existing => existing with { Pending = null });

        var observation = await ObserveAsync(resource, "verify", cancellationToken).ConfigureAwait(false);

        if (observation.Outcome is ObservationOutcome.Unevaluable)
        {
            // The change is neither proven nor failed — it cannot be looked at. §2.4 forbids
            // claiming it stuck; §2.6 forbids calling silence a failure. So the ledger is left
            // exactly as it was, no attempt is spent, and the walk carries on to observe this
            // resource again a few lines below, where it reports as blocked on the server. The
            // resources that need nothing from the Fleet Manager keep converging meanwhile,
            // which is §1.2.2's offline autonomy.
            _services.Log.Warn(
                $"{resource.Name}: '{pending.Change}' cannot be checked — {observation.Observed}. Nothing has been concluded.");

            return null;
        }

        if (observation.InSync)
        {
            _services.Log.Info($"{resource.Name}: '{pending.Change}' survived the reboot.");
            MarkHeld(resource.Name, observation.Expected);
        }
        else
        {
            _services.Log.Warn($"{resource.Name}: did not survive the reboot — {observation.Delta}.");

            var entry = NoteDrift(resource.Name, observation.Expected);

            var failed = IsConflict(entry)
                ? await GiveUpOnConflictAsync(resource, entry, observation, cancellationToken).ConfigureAwait(false)
                : await RecordFailureAsync(
                        resource,
                        AttemptsWithin(pending.Attempt, _services.Options.AttemptBudget),
                        observation.Delta,
                        pending.Change,
                        cancellationToken,
                        pending.Gloss)
                    .ConfigureAwait(false);

            if (failed.Kind.HasGivenUp())
            {
                // The verify that gave up is the last thing this pass *acts* on (decision 68). It
                // is not the last thing the pass does: the walk below runs in observe-only mode and
                // publishes what this frame actually is, which is the difference between a stopped
                // frame and a frame nobody can see (decision 76). The ledger now carries the
                // escalation, so WalkAsync reads it and needs telling nothing.
                _services.Log.Fail($"'{resource.Name}' has been given up on.");
            }
        }

        return null;
    }

    /// <summary>
    /// Walks the whole catalog — observing everything, acting on at most one thing, and acting on
    /// nothing at all once this frame has given up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A stopped frame walks exactly like a running one with its one change already spent</b>
    /// (decision 76). §2.5 rung 4 is "the agent performs no further Act and takes no further
    /// reboot", and decision 68 spells out what that leaves: <i>stopping means stopping acting, not
    /// stopping looking</i>, reusing "the one-change-per-pass rule that turns the remainder of a
    /// walk into pure observation" rather than inventing a second shape. So the stop is applied by
    /// starting the walk with its change already spent, and every branch below is the branch it
    /// always was.
    /// </para>
    /// <para>
    /// <b>What that replaces was a walk that returned at the first escalation and then invented the
    /// rest of the catalog.</b> Every unreached resource was labelled <c>Blocked</c> with the
    /// escalated resource as its <c>blockedBy</c>, whether or not it depended on it — measured in
    /// the payload from the frame, all 77 remaining rows claimed to be waiting on
    /// <c>tool.xvf-host.installed</c>, including <c>boot.config.dtoverlay-waveshare-panel</c>, which
    /// had been in sync since M2 and has no dependency on it whatsoever. The device reported
    /// <b>0 of 79 in sync</b> while being almost entirely configured. Both halves of that were
    /// fabrication: the dependency claim, and the census.
    /// </para>
    /// <para>
    /// <b>Observing instead of inventing answers both at once, and needs no new machinery.</b>
    /// <see cref="Blocker"/> already computes the DAG-true <c>Blocked(dependency)</c> for anything
    /// genuinely downstream of the resource that gave up, because that resource is not
    /// <c>InSync</c>; everything else reports what it is. There is nothing left to carry forward
    /// from an earlier pass and nothing to mark stale, because every row is established by looking,
    /// this pass, at this frame.
    /// </para>
    /// <para>
    /// <b>The cost is one observation sweep per pass on a stopped frame, which is exactly what a
    /// converged frame already pays.</b> Observe is side-effect-free by §2.3's contract — that is
    /// the same guarantee decision 68 leans on — so the sweep spends no attempt, writes nothing to
    /// the machine and takes no reboot.
    /// </para>
    /// </remarks>
    private async Task<PassOutcome> WalkAsync(CancellationToken cancellationToken)
    {
        var statuses = new Dictionary<string, ResourceStatus>(_services.Graph.Count, StringComparer.Ordinal);
        var ordered = new List<ResourceStatus>(_services.Graph.Count);

        // §2.5 rung 4 as one boolean, read once before anything is looked at. A per-resource check
        // would let everything ordered ahead of the escalated resource be acted on and rebooted for
        // on every boot, which is the failure the durable ledger exists to prevent.
        var stopped = HasStopped;
        var acted = stopped;

        List<string>? gaveUp = null;
        DateTimeOffset? earliest = null;
        var result = stopped ? PassResult.Escalated : PassResult.Converged;
        string? detail = null;

        foreach (var resource in _services.Graph.Ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = _services.Journal.Read();
            var entry = ReconcileJournal.EntryFor(state, resource.Name);

            if (Blocker(resource, statuses) is { } blocker)
            {
                Record(new ResourceStatus
                {
                    Name = resource.Name,
                    Kind = ResourceStatusKind.Blocked,
                    BlockedBy = blocker,
                    Delta = $"waiting for '{blocker}'",
                    Attempts = Spent(entry),
                    AttemptBudget = _services.Options.AttemptBudget,
                });

                result = Worst(result, PassResult.Pending);
                continue;
            }

            if (HasGivenUp(entry, _services.Options.AttemptBudget))
            {
                // §2.5 rung 2: stop touching it. The resource is not observed and not acted on
                // until somebody resets the budget, which is what "stop" has to mean if the frame
                // is to stop rebooting. Rung 4 stops the frame around it — which `stopped` above
                // has already applied to every resource in this walk, including the ones ordered
                // ahead of this one.
                (gaveUp ??= []).Add(resource.Name);
                Record(await RefreshEscalationAsync(resource, entry, cancellationToken).ConfigureAwait(false));

                stopped = true;
                acted = true;
                result = Worst(result, PassResult.Escalated);
                continue;
            }

            // A backoff is a promise that this resource will be tried again shortly, and on a
            // stopped frame nothing will be tried again until a person acts. So the countdown is
            // skipped and the resource is observed like any other: what it *is* is knowable, and
            // "trying again in 30s" is not true.
            if (!stopped && entry.NextAttemptUtc is { } next && _services.Clock.UtcNow < next)
            {
                earliest = Sooner(earliest, next);
                Record(new ResourceStatus
                {
                    Name = resource.Name,
                    Kind = ResourceStatusKind.Progressing,
                    Delta = entry.Delta,
                    Action = entry.Change,
                    Attempts = Spent(entry),
                    AttemptBudget = _services.Options.AttemptBudget,
                    NextAttemptUtc = next,
                });

                result = Worst(result, PassResult.Pending);
                continue;
            }

            var observation = await ObserveAsync(resource, "observe", cancellationToken).ConfigureAwait(false);

            if (observation.Outcome is ObservationOutcome.Unevaluable)
            {
                // §2.6: silence is not an answer, so it is not drift either. Nothing is acted on,
                // nothing reboots, and the ledger is not touched in either direction — an attempt
                // is not spent, and one already spent is not forgiven. The resource is simply
                // waiting, and it says what for.
                var recheck = Recheck();
                earliest = Sooner(earliest, recheck);

                Record(new ResourceStatus
                {
                    Name = resource.Name,
                    Kind = ResourceStatusKind.Blocked,
                    BlockedBy = SilentAuthority,
                    Delta = observation.Delta,
                    Attempts = Spent(entry),
                    AttemptBudget = _services.Options.AttemptBudget,
                    NextAttemptUtc = recheck,
                });

                result = Worst(result, PassResult.Pending);
                continue;
            }

            if (observation.InSync)
            {
                // Written only when it would change something, so a converged frame does not
                // rewrite its journal every five minutes for ever. The comparison is structural
                // rather than a list of fields, because the list was the thing that would rot: the
                // ladder's counters and decision 78's counters have opposite lifetimes and a
                // hand-written condition would eventually forget one of them.
                var held = Held(entry, observation.Expected);
                if (held != entry)
                {
                    _services.Journal.Update(state => ReconcileJournal.WithEntry(state, held));
                }

                Record(new ResourceStatus { Name = resource.Name, Kind = ResourceStatusKind.InSync });
                continue;
            }

            if (_services.Interlock?.ExcusedBy(resource.Name, _services.Clock.UtcNow) is { } behaviour)
            {
                // §2.10 clause 2: "While a supervision window is open, the transient wrongness it
                // causes — a kiosk process that is briefly not running — is expected rather than
                // drift, so it never trips §2.6." Not acted on either, because acting would race
                // the restart that is already under way — the other half of the same interference.
                // The window's own deadline is what ends this: once it expires the resource is
                // observed exactly as any other, "and everything §2.6 and §2.7 prescribe takes
                // over".
                Record(new ResourceStatus
                {
                    Name = resource.Name,
                    Kind = ResourceStatusKind.Progressing,
                    Delta = $"{observation.Delta} — expected while supervision restarts it ({behaviour})",
                    Attempts = Spent(entry),
                    AttemptBudget = _services.Options.AttemptBudget,
                });

                result = Worst(result, PassResult.Pending);
                continue;
            }

            // §2.6's **conflict drift** (decision 78). Deliberately below the supervision clause
            // above, so a kiosk process that is briefly not running because §2.10 is restarting it
            // is not counted as somebody fighting the desired state — it is the one drift the frame
            // already knows the cause of.
            entry = NoteDrift(resource.Name, observation.Expected);

            if (IsConflict(entry))
            {
                var conflict = await GiveUpOnConflictAsync(resource, entry, observation, cancellationToken)
                    .ConfigureAwait(false);

                Record(conflict);
                (gaveUp ??= []).Add(resource.Name);

                stopped = true;
                acted = true;
                result = Worst(result, PassResult.Escalated);
                continue;
            }

            // A **gate** — a precondition with no Act that could converge it (see
            // <see cref="IResource.IsGate"/>). Straight to §2.5 rung 2 with the budget declared
            // spent, for the same reason the conflict path above takes that route: every remaining
            // attempt is known in advance to buy nothing, and each of them would cost a reboot on a
            // frame whose problem is that somebody has to look at its hardware. It is placed here,
            // below the conflict clause and above `acted`, deliberately: a gate is not a change and
            // must not be held back by this pass having already spent its one change, because the
            // whole point of a gate is that it stops the pass rather than joining it.
            if (resource.IsGate)
            {
                var gate = await RecordFailureAsync(
                        resource,
                        _services.Options.AttemptBudget,
                        observation.Delta,
                        observation.Expected,
                        cancellationToken)
                    .ConfigureAwait(false);

                _services.Log.Fail(
                    $"{resource.Name}: this is a precondition rather than something the frame can repair, so nothing "
                    + "will be attempted and a person has to look.");

                Record(gate);
                (gaveUp ??= []).Add(resource.Name);

                stopped = true;
                acted = true;
                result = Worst(result, PassResult.Escalated);
                continue;
            }

            if (acted)
            {
                // Drifted, but this pass has already spent its one change (§1.2.5). Observed and
                // reported so the picture is complete; acted on next pass.
                Record(new ResourceStatus
                {
                    Name = resource.Name,
                    Kind = ResourceStatusKind.Progressing,
                    Delta = observation.Delta,
                    Attempts = Spent(entry),
                    AttemptBudget = _services.Options.AttemptBudget,
                });

                result = Worst(result, PassResult.Pending);
                continue;
            }

            acted = true;
            var applied = await ApplyAsync(resource, entry, observation, statuses, ordered, cancellationToken)
                .ConfigureAwait(false);

            Record(applied.Status);

            if (applied.Result is PassResult.Restarting)
            {
                return await FinishAsync(PassResult.Restarting, ordered, null, applied.Detail, cancellationToken).ConfigureAwait(false);
            }

            result = Worst(result, applied.Result);
            detail ??= applied.Detail;

            if (applied.Status.NextAttemptUtc is { } retryAt)
            {
                earliest = Sooner(earliest, retryAt);
            }

            if (applied.Status.Kind.HasGivenUp())
            {
                // A resource gave up while this pass was running (decision 68). Nothing after it is
                // acted on — `acted` is already true and `stopped` makes that permanent for the
                // rest of the walk — but everything after it is still *observed*, so the operator
                // sees what this frame is rather than a list of claims about what it is waiting for
                // (decision 76).
                (gaveUp ??= []).Add(resource.Name);
                stopped = true;
            }
        }

        if (OrphanedGiveUps() is { } orphans)
        {
            foreach (var orphan in orphans)
            {
                (gaveUp ??= []).Add(orphan.Name);
                Record(orphan);
                result = Worst(result, PassResult.Escalated);
            }
        }

        if (gaveUp is not null)
        {
            // Nothing is scheduled on a stopped frame, so a backoff or a recheck left behind by
            // another resource must not shorten the wait: the only thing that changes now is a
            // person, and the ordinary pass interval is the cadence for noticing one (decision 75).
            earliest = null;

            // Every resource that gave up, not merely the first. An operator deciding whether to
            // press retry needs to know how many settings gave up, and clearing one while another
            // remained would otherwise look like it had done nothing.
            detail = $"'{string.Join("', '", gaveUp)}' has been given up on.";
        }

        return await FinishAsync(result, ordered, earliest, detail, cancellationToken).ConfigureAwait(false);

        void Record(ResourceStatus status)
        {
            statuses[status.Name] = status;
            ordered.Add(status);
        }
    }

    /// <summary>Act, journal, count down, cross the boundary, verify.</summary>
    private async Task<(ResourceStatus Status, PassResult Result, string? Detail)> ApplyAsync(
        IResource resource,
        ResourceLedgerEntry entry,
        ResourceObservation observation,
        Dictionary<string, ResourceStatus> statuses,
        List<ResourceStatus> ordered,
        CancellationToken cancellationToken)
    {
        var attempt = NextAttempt(entry);

        _services.Log.Info($"{resource.Name}: drifted — {observation.Delta}. Attempt {attempt} of {_services.Options.AttemptBudget}.");

        PublishNarration(
            resource,
            phase: "act",
            attempt,
            entry,
            action: null,
            gloss: null,
            countdown: null);

        await EmitAsync(
            DeviceEventKinds.Drift,
            resource.Name,
            $"{resource.Detected} {resource.WhyItMatters}",
            observation.Delta,
            attempt,
            cancellationToken).ConfigureAwait(false);

        await PublishReportAsync(
            LoopStateNames.Reconciling,
            resource.Name,
            "act",
            Merge(ordered, statuses, resource.Name, new ResourceStatus
            {
                Name = resource.Name,
                Kind = ResourceStatusKind.Progressing,
                Delta = observation.Delta,
                Attempts = attempt,
                AttemptBudget = _services.Options.AttemptBudget,
            }),
            cancellationToken).ConfigureAwait(false);

        // §2.10 clause 1: the reconciler holds a lock on what it is applying. Taken before the Act
        // rather than at the end of the pass, because the race this prevents is inside the Act —
        // supervision restarting the very unit being rewritten.
        _services.Interlock?.Applying(resource.Name);

        ResourceAction action;
        try
        {
            action = await resource.ActAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Released here and not after the reboot: what follows is journalled, and the status
            // list published from it carries AwaitingReboot, which PublishHolds turns into a hold
            // of its own. Holding across the reboot boundary in this field would instead leak on a
            // process that comes back without one.
            _services.Interlock?.Applying(null);
        }

        // Journalled before the reboot is requested, never after. This write is the only thing
        // standing between a frame that goes down mid-contract and a frame that comes back with
        // no idea what it changed.
        _services.Journal.Update(state => ReconcileJournal.WithEntry(
            state with
            {
                Pending = new PendingApply
                {
                    Resource = resource.Name,
                    Attempt = attempt,
                    Expected = observation.Expected,
                    Change = action.Change,
                    Gloss = action.Gloss,
                    BootId = _services.Boot.Current,
                    WrittenUtc = _services.Clock.UtcNow,
                },
            },
            ReconcileJournal.EntryFor(state, resource.Name) with
            {
                Attempts = attempt,
                Delta = observation.Delta,
                Change = action.Change,

                // The gloss is deliberately *not* written here, and the reason is worth recording
                // because writing it looks obviously right. Every path that reaches a give-up
                // carries the gloss with it — the failure paths pass it to RecordFailureAsync, and
                // a process that died mid-apply reads it back off PendingApply — while a change
                // that succeeds has its whole entry rebuilt by Held(), which keeps four fields and
                // drops the rest. So a copy here would be superseded on failure and erased on
                // success: a line no behaviour depends on, which is a line that will eventually be
                // wrong without anything noticing.
                NextAttemptUtc = null,
            }));

        var outcome = await CrossAndVerifyAsync(resource, attempt, action.Change, action.Gloss, cancellationToken)
            .ConfigureAwait(false);

        // By name, not by position. It used to take the last row, which was the same thing while
        // every inner outcome carried exactly one status — and stopped being the same thing the
        // moment a stopped pass began completing its list from the catalog (decision 68). The walk
        // then read a *blocked* row as the verdict on the resource it had just applied, decided
        // nothing had given up, and carried on: the one bug this whole rung exists to prevent,
        // reintroduced by an index.
        var status = LastFor(outcome.Statuses, resource.Name)
            ?? new ResourceStatus { Name = resource.Name, Kind = ResourceStatusKind.AwaitingReboot };

        return (status, outcome.Result, outcome.Detail);
    }

    /// <summary>
    /// Runs the countdown, crosses the boundary and verifies on the other side.
    /// </summary>
    /// <remarks>
    /// Shared by the ordinary act path and by the resume path, so a change that was written
    /// before a restart that turned out not to be a reboot gets the identical treatment rather
    /// than a second, subtly different one.
    /// </remarks>
    private async Task<PassOutcome> CrossAndVerifyAsync(
        IResource resource,
        int attempt,
        string change,
        string? gloss,
        CancellationToken cancellationToken)
    {
        var entry = ReconcileJournal.EntryFor(_services.Journal.Read(), resource.Name);

        PublishNarration(resource, "reboot", attempt, entry, change, gloss, countdown: null);

        var skipped = await _services.Countdown
            .RunAsync(
                CountdownForThisReboot(),
                state => PublishNarration(resource, "reboot", attempt, entry, change, gloss, state),
                cancellationToken)
            .ConfigureAwait(false);

        if (skipped)
        {
            _services.Log.Info($"{resource.Name}: the countdown was skipped; rebooting now.");
        }

        PublishNarration(resource, "reboot", attempt, entry, change, gloss, countdown: null);

        var awaiting = new ResourceStatus
        {
            Name = resource.Name,
            Kind = ResourceStatusKind.AwaitingReboot,
            Delta = entry.Delta,
            Action = change,
            Gloss = gloss,
            Attempts = attempt,
            AttemptBudget = _services.Options.AttemptBudget,
        };

        await PublishReportAsync(
            LoopStateNames.AwaitingReboot,
            resource.Name,
            "reboot",
            [awaiting],
            cancellationToken).ConfigureAwait(false);

        var crossing = await _services.Reboots
            .CrossAsync(
                new RebootRequest { Resource = resource.Name, Change = change, Attempt = attempt },
                cancellationToken)
            .ConfigureAwait(false);

        switch (crossing.Crossing)
        {
            case RebootCrossing.Restarting:
                return await FinishAsync(
                    PassResult.Restarting,
                    [awaiting],
                    null,
                    "The frame is restarting to prove the change.",
                    cancellationToken).ConfigureAwait(false);

            case RebootCrossing.Refused:
            {
                // The change is written and cannot be proven. That consumes an attempt: a frame
                // that can never reboot must reach a human rather than sit forever claiming to
                // be mid-apply.
                var delta = $"expected a reboot to prove '{change}', observed: {crossing.Detail ?? "the reboot was refused"}";
                _services.Journal.Update(state => state with { Pending = null });
                var failed = await RecordFailureAsync(resource, attempt, delta, change, cancellationToken, gloss)
                    .ConfigureAwait(false);

                return await FinishAsync(
                    failed.Kind.HasGivenUp() ? PassResult.Escalated : PassResult.Pending,
                    [failed],
                    failed.NextAttemptUtc,
                    crossing.Detail,
                    cancellationToken).ConfigureAwait(false);
            }

            default:
            {
                _services.Journal.Update(state => state with { Pending = null });

                var after = await ObserveAsync(resource, "verify", cancellationToken).ConfigureAwait(false);

                if (after.Outcome is ObservationOutcome.Unevaluable)
                {
                    // Crossed the boundary and still cannot look. Nothing is claimed and nothing
                    // is charged: the attempt this apply already recorded stands, no failure is
                    // added to it, and the resource waits on the server exactly as it would have
                    // had the pass never acted at all.
                    _services.Log.Warn(
                        $"{resource.Name}: '{change}' cannot be checked — {after.Observed}. Nothing has been concluded.");

                    var recheck = Recheck();

                    return await FinishAsync(
                        PassResult.Pending,
                        [
                            new ResourceStatus
                            {
                                Name = resource.Name,
                                Kind = ResourceStatusKind.Blocked,
                                BlockedBy = SilentAuthority,
                                Delta = after.Delta,
                                Action = change,
                                Gloss = gloss,
                                Attempts = attempt,
                                AttemptBudget = _services.Options.AttemptBudget,
                                NextAttemptUtc = recheck,
                            },
                        ],
                        recheck,
                        null,
                        cancellationToken).ConfigureAwait(false);
                }

                if (after.InSync)
                {
                    _services.Log.Info($"{resource.Name}: in sync after '{change}', verified across a reboot.");
                    MarkHeld(resource.Name, after.Expected);

                    return await FinishAsync(
                        PassResult.Rebooted,
                        [
                            new ResourceStatus
                            {
                                Name = resource.Name,
                                Kind = ResourceStatusKind.InSync,
                                Attempts = attempt,
                                AttemptBudget = _services.Options.AttemptBudget,
                                Action = change,
                                Gloss = gloss,
                            },
                        ],
                        null,
                        null,
                        cancellationToken).ConfigureAwait(false);
                }

                _services.Log.Warn($"{resource.Name}: '{change}' did not survive the reboot — {after.Delta}.");

                var reverted = NoteDrift(resource.Name, after.Expected);

                var failed = IsConflict(reverted)
                    ? await GiveUpOnConflictAsync(resource, reverted, after, cancellationToken).ConfigureAwait(false)
                    : await RecordFailureAsync(resource, attempt, after.Delta, change, cancellationToken, gloss)
                        .ConfigureAwait(false);

                return await FinishAsync(
                    failed.Kind.HasGivenUp() ? PassResult.Escalated : PassResult.Rebooted,
                    [failed],
                    failed.NextAttemptUtc,
                    null,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// How long this reboot pauses first — the single decision point of decisions 51 and 53.
    /// </summary>
    /// <remarks>
    /// The whole of "the countdown is for drift repair, not for initial provisioning" is this one
    /// call, and so is "unless an operator asked to watch a provision". It is deliberately not a
    /// condition spread through the loop: nothing else in here asks whether the frame has been
    /// green, so changing the rule is an edit to <see cref="CountdownScope.ForReboot"/> and
    /// nothing more. Both durations are read here rather than captured, because both are fleet
    /// settings that arrive after the agent starts and can move while it runs.
    /// </remarks>
    private TimeSpan CountdownForThisReboot() => CountdownScope.ForReboot(
        _services.Options.CurrentCountdown(),
        _services.Journal.Read().FirstInSyncUtc is not null,
        _services.Options.CurrentProvisioningPace());

    /// <summary>
    /// Records the first moment this frame had everything verified at once (decision 51).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Convergence is the honest in-loop reading of §2.6's <c>InSync</c> rung. Adoption is a
    /// resource (decision 34) and so is the applied agent version (§2.8), so a converged pass has
    /// already cleared <c>NotAdopted</c> and <c>VersionMismatch</c> along with everything else.
    /// The two rungs it does not speak for — <c>NoContact</c> and <c>ControlNotConfigured</c> —
    /// are properties of the link at this instant rather than of the frame, which is exactly what
    /// a durable "has this frame ever been green" must not be built on.
    /// </para>
    /// <para>
    /// Written once and then left alone: the read guards the write, so a frame that stays
    /// converged does not rewrite the journal on every pass of a five-minute drift sweep.
    /// </para>
    /// </remarks>
    private void MarkGreen()
    {
        if (_services.Journal.Read().FirstInSyncUtc is not null)
        {
            return;
        }

        var at = _services.Clock.UtcNow;
        _services.Journal.Update(state => state with { FirstInSyncUtc = at });

        _services.Log.Info(
            "Every resource is verified. This frame has been green, so repairs from here on pause for the countdown before the verifying reboot (§2.7).");
    }

    /// <summary>The escalation ladder of §2.5, one rung at a time.</summary>
    /// <param name="resource">The resource that failed.</param>
    /// <param name="attempt">Which attempt this was.</param>
    /// <param name="delta">Expected versus observed, in the one form §2.5 rung 2 requires.</param>
    /// <param name="change">The exact change that was tried.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="gloss">
    /// The plain-language gloss on <paramref name="change"/>, where the caller has one.
    /// </param>
    /// <remarks>
    /// The gloss is optional because two of the four callers genuinely have none: a gate never
    /// acted, and a conflict give-up is reporting a change that <i>worked</i>. Where it is absent
    /// the stored one is kept rather than overwritten with null - the ledger's copy is the only one
    /// that survives the reboot between the attempt and the verdict.
    /// </remarks>
    private async Task<ResourceStatus> RecordFailureAsync(
        IResource resource,
        int attempt,
        string delta,
        string change,
        CancellationToken cancellationToken,
        string? gloss = null)
    {
        var options = _services.Options;

        if (attempt < options.AttemptBudget)
        {
            // Rung 1: retry with a growing delay. The wait exists to stop a reboot loop wearing
            // the hardware (§2.4), not to be polite about it. The delay is a pure function of
            // the attempt number - the jitter that used to shave it is gone, so two runs of the
            // same fault on the same frame wait exactly the same amount of time.
            var wait = _retry.Delay(attempt);
            var next = _services.Clock.UtcNow + wait;

            _services.Journal.Update(state => ReconcileJournal.WithEntry(
                state,
                ReconcileJournal.EntryFor(state, resource.Name) with
                {
                    Attempts = attempt,
                    Delta = delta,
                    Change = change,
                    Gloss = gloss ?? ReconcileJournal.EntryFor(state, resource.Name).Gloss,
                    NextAttemptUtc = next,
                }));

            _services.Log.Info(string.Create(
                CultureInfo.InvariantCulture,
                $"{resource.Name}: attempt {attempt} of {options.AttemptBudget} failed; next try in {(int)wait.TotalSeconds}s."));

            return new ResourceStatus
            {
                Name = resource.Name,
                Kind = ResourceStatusKind.Progressing,
                Delta = delta,
                Action = change,
                Attempts = attempt,
                AttemptBudget = options.AttemptBudget,
                NextAttemptUtc = next,
            };
        }

        // Rung 2: the budget is gone. Stop touching it, and say exactly what is wrong. Rung 4
        // then stops the pass around it, which the caller does — this method's whole job is the
        // ledger and the notification.
        var previous = ReconcileJournal.EntryFor(_services.Journal.Read(), resource.Name);
        var escalations = previous.Escalations + 1;

        _services.Log.Fail(string.Create(
            CultureInfo.InvariantCulture,
            $"{resource.Name}: giving up after {attempt} attempts — {delta}."));

        var notified = await EmitAsync(
            DeviceEventKinds.Escalation,
            resource.Name,
            EscalationSummary(resource, attempt, change),
            delta,
            attempt,
            cancellationToken).ConfigureAwait(false);

        _services.Log.Fail($"{resource.Name}: this frame has stopped reconciling and is waiting for a person.");

        var kept = gloss ?? previous.Gloss;

        _services.Journal.Update(state => ReconcileJournal.WithEntry(
            state,
            ReconcileJournal.EntryFor(state, resource.Name) with
            {
                Attempts = attempt,
                Escalations = escalations,
                EscalationNotified = notified,
                Delta = delta,
                Change = change,
                Gloss = kept,
                NextAttemptUtc = null,
            }));

        return new ResourceStatus
        {
            Name = resource.Name,

            // §2.3: the only thing separating the two is whether the notification actually reached
            // the Fleet Manager rather than the frame's offline buffer. Both mean the loop has
            // stopped touching this resource.
            Kind = notified ? ResourceStatusKind.Escalated : ResourceStatusKind.Degraded,
            Delta = delta,
            Action = change,
            Gloss = kept,

            // The two sentences the person in front of the frame can act on, carried on the row
            // that gave up so the screen never has to pair one resource's words with another's
            // numbers. This is the pass that stops the frame, so this is where they have to be.
            Detected = resource.Detected,
            WhyItMatters = resource.WhyItMatters,
            Attempted = !resource.IsGate,
            Attempts = attempt,
            AttemptBudget = options.AttemptBudget,
            Escalations = escalations,
        };
    }

    /// <summary>
    /// The escalation notification's one sentence — the symptom <b>and</b> the cause (§2.5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The cause is the half that decides what the operator does.</b> Rung 3 offers exactly two
    /// actions, <i>retry</i> and <i>open a remote shell</i>, and which one is right turns on
    /// something the symptom cannot express. "labwc is missing" is equally true when the Debian
    /// archive could not be reached — where retrying is the entire fix — and when the package name
    /// is wrong, where retrying forever is the wrong answer and somebody has to look. A
    /// notification carrying only <see cref="ResourceObservation.Delta"/> cannot tell them apart,
    /// so it asks for a decision while withholding what the decision depends on.
    /// </para>
    /// <para>
    /// The resource's own <see cref="ResourceAction.Change"/> already draws that distinction
    /// verbatim — it is where a refused Act records what it tried and why it was refused — and
    /// before this it reached the device row in the GUI and stopped there, which is the one place
    /// an operator reading an alert on their phone is not.
    /// </para>
    /// <para>
    /// It travels in <see cref="DeviceEvent.Summary"/> rather than in a field of its own because
    /// <see cref="DeviceEvent"/> is frozen, and because the summary is the part a notification is
    /// guaranteed to carry: §2.5's channel is Home Assistant or SMTP, neither of which renders a
    /// record. Both escalation emissions share this method so the two spellings cannot drift —
    /// <see cref="RefreshEscalationAsync"/> rebuilds the same event for a frame that gave up while
    /// its server was unreachable, and that frame's operator needs the cause just as much.
    /// </para>
    /// </remarks>
    private static string EscalationSummary(IResource resource, int attempts, string? change) =>
        string.IsNullOrWhiteSpace(change)
            ? $"{resource.Detected} It has been tried {attempts} times and is still wrong."
            : $"{resource.Detected} It has been tried {attempts} times and is still wrong. The last attempt was: {change}";

    /// <summary>
    /// Re-offers an escalation the Fleet Manager never received.
    /// </summary>
    /// <remarks>
    /// This is what turns <c>Degraded</c> into <c>Escalated(admin-notified)</c> when a frame
    /// that gave up while offline gets its server back. The event is not re-sent — the outbox
    /// already holds it and drains on reconnect — the ledger simply catches up with the fact
    /// that it went.
    /// </remarks>
    private async ValueTask<ResourceStatus> RefreshEscalationAsync(
        IResource resource,
        ResourceLedgerEntry entry,
        CancellationToken cancellationToken)
    {
        var notified = entry.EscalationNotified;

        if (!notified)
        {
            notified = await EmitAsync(
                DeviceEventKinds.Escalation,
                resource.Name,
                EscalationSummary(resource, Spent(entry), entry.Change),
                entry.Delta,
                Spent(entry),
                cancellationToken).ConfigureAwait(false);

            if (notified)
            {
                _services.Journal.Update(state => ReconcileJournal.WithEntry(
                    state,
                    ReconcileJournal.EntryFor(state, resource.Name) with { EscalationNotified = true }));
            }
        }

        return new ResourceStatus
        {
            Name = resource.Name,
            Kind = notified ? ResourceStatusKind.Escalated : ResourceStatusKind.Degraded,
            Delta = entry.Delta,
            Action = entry.Change,
            Gloss = entry.Gloss,

            // Every later pass on a stopped frame comes through here, including the first pass of a
            // process that booted into an escalation it inherited from the ledger. The row has to
            // carry the plain half then too, or the screen would say less after a restart than it
            // said before one.
            Detected = resource.Detected,
            WhyItMatters = resource.WhyItMatters,
            Attempted = !resource.IsGate,
            Attempts = Spent(entry),
            AttemptBudget = _services.Options.AttemptBudget,
            Escalations = entry.Escalations,
        };
    }

    /// <summary>When the loop should ask again about something it could not evaluate.</summary>
    /// <remarks>
    /// It is a separate interval from <see cref="ReconcileOptions.PassInterval"/> because it
    /// measures a different thing. The pass interval is a drift sweep over the filesystem, where
    /// five minutes is generous; this is a wait for a network round trip that has already failed,
    /// where five minutes would stall a bare frame's provisioning by five minutes per boot for no
    /// reason. Publishing it as <c>NextAttemptUtc</c> also keeps §2.7 item 6 satisfied: the pause
    /// has a visible end, so it never reads as a hang.
    /// </remarks>
    private DateTimeOffset Recheck() => _services.Clock.UtcNow + _services.Options.UnevaluableRecheck;

    /// <summary>What this resource has spent, as the current budget can express it (decision 74).</summary>
    private int Spent(ResourceLedgerEntry entry) => AttemptsWithin(entry, _services.Options.AttemptBudget);

    /// <summary>The number the next attempt on this resource carries.</summary>
    /// <remarks>
    /// Bounded by the budget, so a resource carrying more spent attempts than the budget allows
    /// narrates <i>attempt 3 of 3</i> and escalates, rather than narrating <i>attempt 5 of 3</i>
    /// and escalating anyway. The escalation is the same either way — see
    /// <see cref="AttemptsWithin(int, int)"/> on why a budget reduction is retroactive — and the
    /// difference is entirely in whether the frame says something a person can believe.
    /// </remarks>
    private int NextAttempt(ResourceLedgerEntry entry)
    {
        var budget = _services.Options.AttemptBudget;
        var next = AttemptsWithin(entry, budget) + 1;
        return budget > 0 && next > budget ? budget : next;
    }

    private static DateTimeOffset Sooner(DateTimeOffset? earliest, DateTimeOffset candidate) =>
        earliest is { } current && current <= candidate ? current : candidate;

    /// <summary>The most recent status for one resource in a list, or null.</summary>
    private static ResourceStatus? LastFor(IReadOnlyList<ResourceStatus> statuses, string name)
    {
        for (var index = statuses.Count - 1; index >= 0; index--)
        {
            if (string.Equals(statuses[index].Name, name, StringComparison.Ordinal))
            {
                return statuses[index];
            }
        }

        return null;
    }

    /// <summary>
    /// The row for a resource that gave up and that this build's catalog no longer contains.
    /// </summary>
    /// <remarks>
    /// It carries the budget as well as the count, which it did not before decision 74. A row with
    /// attempts and no budget beside them renders as a bare "attempt 5" with nothing to read it
    /// against — which is the same incoherence <see cref="AttemptsWithin(int, int)"/> exists to
    /// remove, one field short of it.
    /// </remarks>
    private ResourceStatus Terminal(string name, ResourceStatusKind kind, ResourceLedgerEntry entry) => new()
    {
        Name = name,
        Kind = kind,
        Delta = entry.Delta,
        Action = entry.Change,
        Attempts = Spent(entry),
        AttemptBudget = _services.Options.AttemptBudget,
        Escalations = entry.Escalations,
    };

    private static string? Blocker(IResource resource, Dictionary<string, ResourceStatus> statuses)
    {
        foreach (var dependency in resource.DependsOn)
        {
            if (!statuses.TryGetValue(dependency, out var status) || status.Kind != ResourceStatusKind.InSync)
            {
                return dependency;
            }
        }

        return null;
    }

    /// <summary>
    /// Ordering over pass results, so a pass reports the one thing its driver has to act on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not severity — <i>urgency</i>. <see cref="PassResult.Rebooted"/> outranks
    /// <see cref="PassResult.Pending"/> because it is the one that means "something changed, run
    /// again now". A pass that acts on one resource and finds another blocked behind it has made
    /// progress, and reporting the blocked one would stall the driver into a wait for a backoff
    /// that does not exist.
    /// </para>
    /// <para>
    /// <b><see cref="PassResult.Escalated"/> travels through here and outranks everything the walk
    /// can produce.</b> A stopped frame keeps walking — observing, never acting (decision 76) — so
    /// the escalation is merged with whatever else that walk found rather than returned from the
    /// point it happened. The rank is what makes that safe: no number of in-sync, blocked or
    /// backing-off rows found afterwards can turn a stopped frame back into a running one.
    /// </para>
    /// </remarks>
    private static PassResult Worst(PassResult current, PassResult candidate) =>
        Rank(candidate) > Rank(current) ? candidate : current;

    private static int Rank(PassResult result) => result switch
    {
        PassResult.Converged => 0,
        PassResult.Pending => 1,
        PassResult.Rebooted => 2,
        PassResult.Restarting => 3,
        PassResult.Escalated => 4,
        _ => 5,
    };

    private static List<ResourceStatus> Merge(
        List<ResourceStatus> ordered,
        Dictionary<string, ResourceStatus> statuses,
        string name,
        ResourceStatus replacement)
    {
        var merged = new List<ResourceStatus>(ordered.Count + 1);
        merged.AddRange(ordered);

        if (statuses.ContainsKey(name))
        {
            for (var index = 0; index < merged.Count; index++)
            {
                if (string.Equals(merged[index].Name, name, StringComparison.Ordinal))
                {
                    merged[index] = replacement;
                    return merged;
                }
            }
        }

        merged.Add(replacement);
        return merged;
    }

    private async ValueTask<ResourceObservation> ObserveAsync(
        IResource resource,
        string phase,
        CancellationToken cancellationToken)
    {
        try
        {
            return await resource.ObserveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // An Observe that throws is drift, not a crash. §1.2.3 forbids repairing anything
            // invisibly and forbids a generic failure bucket, so the exception becomes the
            // observed value and travels all the way to the screen and the Fleet Manager.
            _services.Log.Warn($"{resource.Name}: {phase} failed — {exception.Message}");
            return new ResourceObservation(false, "a readable value", $"{phase} failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Records that this resource is currently holding <paramref name="expected"/>, clearing the
    /// ladder and keeping decision 78's history.
    /// </summary>
    /// <remarks>
    /// <b>This replaced a method that cleared the whole entry, and that clearing was the livelock.</b>
    /// A successful verify wiped every trace the resource had ever been repaired, so the next pass
    /// began from nothing and the loop had no way of telling a value that had never converged from
    /// one that had converged and been taken away — which is the difference between drift and
    /// §2.6's conflict drift, and the reason a frame could reboot indefinitely with its attempt
    /// counter never passing <c>1/3</c>. What is cleared is exactly the ladder: attempts,
    /// escalations, the notification flag, the delta, the last change and the backoff. What is kept
    /// is how many times this value has already been put back.
    /// </remarks>
    private void MarkHeld(string resource, string expected) =>
        _services.Journal.Update(state => ReconcileJournal.WithEntry(
            state,
            Held(ReconcileJournal.EntryFor(state, resource), expected)));

    /// <summary>One ledger entry as an in-sync observation of <paramref name="expected"/> leaves it.</summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="ResourceLedgerEntry.HeldSinceUtc"/> is when the run began, not when it was last
    /// seen</b> — so a value observed correct on twenty consecutive passes keeps the timestamp of
    /// the first of them, and <see cref="ReconcileOptions.ConflictHold"/> measures the whole run
    /// rather than the gap between two readings. Written the other way the hold could never be
    /// reached and a reversion count could never be forgiven.
    /// </para>
    /// <para>
    /// A changed expectation starts a new run, because the frame has not yet seen the <i>new</i>
    /// value hold for anything.
    /// </para>
    /// </remarks>
    private ResourceLedgerEntry Held(ResourceLedgerEntry entry, string expected)
    {
        var now = _services.Clock.UtcNow;
        var since = entry.HeldSinceUtc is { } began
            && string.Equals(entry.HeldExpected, expected, StringComparison.Ordinal)
                ? began
                : now;

        return new ResourceLedgerEntry
        {
            Resource = entry.Resource,
            Reversions = entry.Reversions > 0 && now - since >= _services.Options.ConflictHold
                ? 0
                : entry.Reversions,
            HeldExpected = expected,
            HeldSinceUtc = since,
        };
    }

    /// <summary>
    /// Counts a <b>reversion</b> if this drift is one, and answers with the entry as it now stands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Called from all three places a drift can be discovered</b> — the post-boot verify in
    /// <see cref="ResumePendingAsync"/>, the in-process verify in <see cref="CrossAndVerifyAsync"/>,
    /// and the ordinary walk. That is not tidiness; it is what makes the bound hold. The frame's
    /// post-boot verify races the login session by a margin decision 65 measured at 0.03–0.7 s, so
    /// which of those three finds the drift is a coin flip, and a rule implemented in only one of
    /// them would count on some cycles and not others.
    /// </para>
    /// <para>
    /// <b>The two things §2.6 calls conflict drift are separated here rather than conflated.</b> A
    /// value this frame observed correct and is now observing wrong, against an expectation that has
    /// <i>not moved</i>, is a reversion — something put it back. A value whose expectation has moved
    /// is a desired-value change pushed from the Fleet Manager, which §2.6 names in the same
    /// sentence and which must never accumulate towards a give-up: an operator tuning
    /// <c>audio.playbackVolume</c> would otherwise stop their own frame for using the product as
    /// designed.
    /// </para>
    /// <para>
    /// <b>The hold is cleared either way</b>, so one episode of drift is counted once however many
    /// passes observe it before it is repaired.
    /// </para>
    /// </remarks>
    private ResourceLedgerEntry NoteDrift(string resource, string expected)
    {
        var entry = ReconcileJournal.EntryFor(_services.Journal.Read(), resource);

        if (entry.HeldExpected is null)
        {
            return entry;
        }

        var updated = string.Equals(entry.HeldExpected, expected, StringComparison.Ordinal)
            ? entry with
            {
                Reversions = entry.Reversions + 1,
                HeldExpected = null,
                HeldSinceUtc = null,
            }
            : entry with { HeldExpected = null, HeldSinceUtc = null };

        _services.Journal.Update(state => ReconcileJournal.WithEntry(state, updated));

        return updated;
    }

    /// <summary>Whether this resource has crossed §2.6's conflict-drift threshold.</summary>
    private bool IsConflict(ResourceLedgerEntry entry) =>
        _services.Options.ConflictThreshold > 0
        && entry.Reversions >= _services.Options.ConflictThreshold;

    /// <summary>
    /// Stops touching a resource something else keeps changing back — §2.6's "maximally serious".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Straight to §2.5 rung 2 with the budget declared spent, rather than another turn of the
    /// ladder. The repair demonstrably <i>works</i> — it was applied and verified across a reboot,
    /// more than once — and demonstrably does not last, so every remaining attempt buys exactly one
    /// more reboot and one more revert. Decision 68 then stops the frame around it and a person is
    /// told what is fighting it.
    /// </para>
    /// <para>
    /// It reuses <see cref="RecordFailureAsync"/> rather than writing its own escalation so that
    /// there is one place that notifies, one place that increments <c>Escalations</c>, and one
    /// definition of the <c>Degraded</c>/<c>Escalated</c> split.
    /// </para>
    /// </remarks>
    private async Task<ResourceStatus> GiveUpOnConflictAsync(
        IResource resource,
        ResourceLedgerEntry entry,
        ResourceObservation observation,
        CancellationToken cancellationToken)
    {
        _services.Log.Fail(string.Create(
            CultureInfo.InvariantCulture,
            $"{resource.Name}: applied and verified {entry.Reversions} times and put back every time. Something on this frame is fighting it."));

        return await RecordFailureAsync(
                resource,
                _services.Options.AttemptBudget,
                ConflictDelta(observation, entry.Reversions),
                entry.Change ?? observation.Expected,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The delta an operator reads when the frame has stopped because something is fighting it.
    /// </summary>
    /// <remarks>
    /// The ordinary expected-versus-observed is kept verbatim in front — §2.5 rung 2 requires it and
    /// decision 70 requires it rendered rather than re-derived — and the sentence after it is the
    /// part that changes what a person does. "Expected 60, observed 37" sends somebody looking for a
    /// setting that will not apply; the same line followed by <i>it applied three times and was put
    /// back three times</i> sends them looking for the other owner, which is where the fault is.
    /// </remarks>
    private static string ConflictDelta(ResourceObservation observation, int reversions) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{observation.Delta} — applied and verified {reversions} times and put back every time, so something else on this frame is changing it after the agent does");

    private async Task<PassOutcome> FinishAsync(
        PassResult result,
        IReadOnlyList<ResourceStatus> statuses,
        DateTimeOffset? next,
        string? detail,
        CancellationToken cancellationToken)
    {
        if (result is PassResult.Converged)
        {
            // Every pass funnels through here and only WalkAsync can produce Converged, so this
            // is the one place the frame can be observed turning green.
            MarkGreen();
        }

        await PublishStatusesAsync(result, statuses, next, cancellationToken).ConfigureAwait(false);

        return new PassOutcome
        {
            Result = result,
            Statuses = statuses,
            NextAttemptUtc = next,
            Detail = detail,
        };
    }

    private async Task PublishStatusesAsync(
        PassResult result,
        IReadOnlyList<ResourceStatus> statuses,
        DateTimeOffset? next,
        CancellationToken cancellationToken)
    {
        // A resource that has given up sorts worst, which decision 70 states as a fact about this
        // method and which the enum's own order does not deliver: `Blocked` is declared after
        // `Degraded`, so a frame that gave up while its server was unreachable narrated a *blocked*
        // row as its headline — losing the resource name, the attempt count and the "has anybody
        // been told" line that §2.7 item 7 is made of. Asked explicitly here rather than by
        // renumbering the enum, whose order is a wire-adjacent detail nothing else should depend on.
        var worst = statuses
            .Where(status => status.Kind != ResourceStatusKind.InSync)
            .OrderByDescending(status => status.Kind.HasGivenUp())
            .ThenByDescending(status => (int)status.Kind)
            .FirstOrDefault();

        // §2.10 clause 1, the reconciler's half: supervision must not touch anything this loop is
        // Progressing, AwaitingReboot or Blocked on, and this is where it learns which those are.
        _services.Interlock?.PublishHolds(statuses);

        // §2.6's "any drift stops the product", with §2.10 clause 2's one exclusion applied. The
        // exclusion is subtracted here rather than at the point each status is recorded, so there
        // is exactly one place in the agent that decides whether the product runs.
        var now = _services.Clock.UtcNow;
        var drifted = false;
        foreach (var status in statuses)
        {
            if (status.Kind is ResourceStatusKind.InSync || _services.Interlock?.Excuses(status.Name, now) == true)
            {
                continue;
            }

            drifted = true;
            break;
        }

        _services.Hub.Publish(status => status with
        {
            Resources = statuses,
            Drifted = drifted,
            Reconcile = status.Reconcile with
            {
                LoopState = LoopStateFor(result),
                Resource = worst?.Name,
                Phase = null,
                Attempt = worst?.Attempts ?? 0,
                AttemptBudget = _services.Options.AttemptBudget,
                BackoffTotal = next is { } at && worst?.NextAttemptUtc is not null
                    ? at - _services.Clock.UtcNow
                    : TimeSpan.Zero,
                BackoffEndsAt = worst?.NextAttemptUtc,
                Countdown = null,
                Escalations = worst?.Escalations ?? 0,
                AdminNotified = worst?.Kind is ResourceStatusKind.Escalated,
            },
        });

        await PublishReportAsync(LoopStateFor(result), null, null, statuses, cancellationToken).ConfigureAwait(false);
    }

    private static string LoopStateFor(PassResult result) => result switch
    {
        PassResult.Converged => LoopStateNames.Converged,
        PassResult.Restarting or PassResult.Rebooted => LoopStateNames.AwaitingReboot,
        PassResult.Pending => LoopStateNames.BackingOff,
        PassResult.Escalated => LoopStateNames.Escalated,
        _ => LoopStateNames.Reconciling,
    };

    private void PublishNarration(
        IResource resource,
        string phase,
        int attempt,
        ResourceLedgerEntry entry,
        string? action,
        string? gloss,
        CountdownState? countdown) =>
        _services.Hub.Publish(status => status with
        {
            Narration = new Narration
            {
                Detected = resource.Detected,
                WhyItMatters = resource.WhyItMatters,
                Action = action,
                ActionGloss = gloss,
            },
            Reconcile = new ReconcileNarration
            {
                LoopState = countdown is null ? LoopStateNames.Reconciling : LoopStateNames.AwaitingReboot,
                Resource = resource.Name,
                Phase = phase,
                Attempt = attempt,
                AttemptBudget = _services.Options.AttemptBudget,
                Countdown = countdown,
                Escalations = entry.Escalations,
                AdminNotified = entry.EscalationNotified,
            },
        });

    private async ValueTask<bool> EmitAsync(
        string kind,
        string? resource,
        string summary,
        string? delta,
        int attempts,
        CancellationToken cancellationToken)
    {
        var sequence = _services.Journal
            .Update(state => state with { TelemetrySequence = state.TelemetrySequence + 1 })
            .TelemetrySequence;

        _ = sequence;

        return await _services.Telemetry.EventAsync(
            new DeviceEvent
            {
                DeviceId = DeviceId,
                Kind = kind,
                OccurredUtc = _services.Clock.UtcNow,
                Resource = resource,
                Summary = summary,
                Delta = delta,
                Attempts = attempts,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PublishReportAsync(
        string loopState,
        string? currentResource,
        string? phase,
        IReadOnlyList<ResourceStatus> statuses,
        CancellationToken cancellationToken)
    {
        var sequence = _services.Journal
            .Update(state => state with { TelemetrySequence = state.TelemetrySequence + 1 })
            .TelemetrySequence;

        var inSync = 0;
        var blocked = 0;
        var drifted = 0;

        foreach (var status in statuses)
        {
            switch (status.Kind)
            {
                case ResourceStatusKind.InSync:
                    inSync++;
                    break;
                case ResourceStatusKind.Blocked:
                    blocked++;
                    break;
                default:
                    drifted++;
                    break;
            }
        }

        // Every resource reboots (§2.4), so the number of boots still to come is simply the
        // number that are not yet verified — including the blocked ones, which will each need
        // their own once their dependency clears.
        var rebootsExpected = _services.Graph.Count - inSync;

        await _services.Telemetry.ReportAsync(
            new ReconcileReport
            {
                DeviceId = DeviceId,
                Sequence = sequence,
                GeneratedUtc = _services.Clock.UtcNow,
                LoopState = loopState,
                CurrentResource = currentResource,
                CurrentPhase = phase,
                InSync = inSync,
                Drifted = drifted,
                Blocked = blocked,
                RebootsExpected = rebootsExpected,
                Resources = [.. statuses.Select(status => status.ToReport())],
            },
            cancellationToken).ConfigureAwait(false);
    }
}
