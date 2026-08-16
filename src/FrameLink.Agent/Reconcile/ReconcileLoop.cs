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
/// <b>Stopping means stopping acting, not stopping looking.</b> The status list a stopped pass
/// publishes is completed from the static dependency graph, so it always carries every resource in
/// the catalog: the ones already observed keep their real verdict and the rest are
/// <c>Blocked</c> behind the resource that gave up. That needs no observation at all, which is why
/// it is safe to do at the moment everything else stops — and without it, everything downstream
/// vanishes from the operator's view at exactly the moment they need to see what is queued up
/// behind the failure.
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
            });
        });

        _services.Log.Info($"{resource}: the attempt budget was reset by the Fleet Manager.");
    }

    /// <summary>
    /// Resets every resource that has given up — the device-wide form of §2.5 rung 3's
    /// <b>retry</b>.
    /// </summary>
    /// <returns>The resources whose budgets were reset, in ledger order.</returns>
    /// <remarks>
    /// <para>
    /// Rung 4 stops the <i>frame</i>, and a stopped frame can have more than one resource that
    /// gave up — <see cref="StoppedDeviceAsync"/> deliberately reports all of them, because clearing one
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

        // Before anything is observed, resumed or acted on. An escalation stops the *frame*
        // (decision 68), so one inherited from an earlier process has to stop this pass at its
        // very first instruction — a per-resource check inside the walk would let every resource
        // ordered ahead of the escalated one be acted on and rebooted for, on every boot, forever.
        // That is the durability the ledger is for: the frame holds the failure and waits.
        if (await StoppedDeviceAsync(cancellationToken).ConfigureAwait(false) is { } stopped)
        {
            return await FinishAsync(
                PassResult.Escalated,
                stopped.Statuses,
                null,
                stopped.Detail,
                cancellationToken).ConfigureAwait(false);
        }

        var resumed = await ResumePendingAsync(cancellationToken).ConfigureAwait(false);
        if (resumed is not null)
        {
            return resumed;
        }

        return await WalkAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// §2.5 rung 4 as a pass outcome — or null if this frame is still allowed to reconcile.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every resource that gave up is reported, not merely the first. An operator deciding whether
    /// to press <b>retry</b> needs to know how many settings gave up, and clearing one while
    /// another remains would otherwise look like it had done nothing. The list is then completed
    /// from the catalog, so a stopped frame's report is the whole catalog rather than the row or
    /// two that happen to be in the ledger.
    /// </para>
    /// <para>
    /// <b>It re-offers an escalation the Fleet Manager never received, and that is not "acting".</b>
    /// §2.3 distinguishes <c>Degraded</c> from <c>Escalated</c> by exactly one thing — whether the
    /// notification reached the server rather than the frame's offline buffer — and the promotion
    /// used to happen in the walk. Under decision 68 the walk is never reached on a stopped frame,
    /// so without this a frame that gave up during an outage would stay <c>Degraded</c> for ever,
    /// telling its operator that nobody had been told long after somebody had. Nothing is observed,
    /// nothing is acted on, no attempt is spent and nothing reboots: it is a message going out.
    /// </para>
    /// </remarks>
    private async Task<(IReadOnlyList<ResourceStatus> Statuses, string Detail)?> StoppedDeviceAsync(
        CancellationToken cancellationToken)
    {
        List<ResourceStatus>? given = null;

        foreach (var entry in _services.Journal.Read().Ledger)
        {
            if (!HasGivenUp(entry, _services.Options.AttemptBudget))
            {
                continue;
            }

            given ??= [];

            given.Add(_services.Graph.Find(entry.Resource) is { } resource
                ? await RefreshEscalationAsync(resource, entry, cancellationToken).ConfigureAwait(false)
                : Terminal(
                    entry.Resource,
                    entry.EscalationNotified ? ResourceStatusKind.Escalated : ResourceStatusKind.Degraded,
                    entry));
        }

        if (given is null)
        {
            return null;
        }

        var detail = $"'{string.Join("', '", given.Select(status => status.Name))}' has been given up on.";
        return (Complete(given, given[0].Name), detail);
    }

    /// <summary>
    /// Fills a partial status list out to the whole catalog, from the static dependency graph.
    /// </summary>
    /// <param name="observed">
    /// What this pass actually looked at, in the order it looked. Every entry is preserved exactly
    /// as it stands — nothing here overwrites an observation.
    /// </param>
    /// <param name="stoppedBy">
    /// The resource that gave up, named as what everything else is waiting for.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>The measured trap this exists for.</b> <c>Blocked</c> is not persisted; it is recomputed
    /// from the resources a walk actually reached. A pass that stops at the first escalation
    /// therefore reached 37 to 56 of the catalog's 79, and the other twenty-odd silently vanished
    /// from the Fleet Manager's view — at exactly the moment an operator needs to see what is
    /// queued up behind the failure. An empty row is not safer than an informative one; it is a
    /// frame that looks smaller than it is.
    /// </para>
    /// <para>
    /// <b>It needs no observation, which is what makes it safe here.</b> Everything it uses is
    /// static: the catalog's own order, and the name of the resource that stopped the pass. Nothing
    /// is read off the machine, nothing is acted on, and no attempt is spent — so completing the
    /// list breaks none of decision 68's "stop acting". Stopping means stopping acting, not
    /// stopping looking.
    /// </para>
    /// <para>
    /// <b>Every unreached resource is blocked by the one that gave up, whether or not it depends
    /// on it.</b> That is true in both readings: a dependent is blocked by the DAG, and everything
    /// else is blocked because nothing at all will be attempted until a human retries. Splitting
    /// the two would put a distinction on screen that changes nothing about what anybody does next.
    /// </para>
    /// </remarks>
    private List<ResourceStatus> Complete(List<ResourceStatus> observed, string stoppedBy)
    {
        var seen = new Dictionary<string, ResourceStatus>(observed.Count, StringComparer.Ordinal);
        foreach (var status in observed)
        {
            seen[status.Name] = status;
        }

        var state = _services.Journal.Read();
        var complete = new List<ResourceStatus>(_services.Graph.Count);

        foreach (var resource in _services.Graph.Ordered)
        {
            if (seen.Remove(resource.Name, out var status))
            {
                complete.Add(status);
                continue;
            }

            var entry = ReconcileJournal.EntryFor(state, resource.Name);

            complete.Add(new ResourceStatus
            {
                Name = resource.Name,
                Kind = ResourceStatusKind.Blocked,
                BlockedBy = stoppedBy,
                Delta = $"not attempted: waiting for '{stoppedBy}'",
                Attempts = Spent(entry),
                AttemptBudget = _services.Options.AttemptBudget,
                Escalations = entry.Escalations,
            });
        }

        // A status whose resource the catalog no longer has. It cannot be reconciled and it is not
        // dropped either: silently losing a row is the failure this whole method exists to fix.
        complete.AddRange(seen.Values);

        return complete;
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
            ClearLedger(resource.Name);
        }
        else
        {
            _services.Log.Warn($"{resource.Name}: did not survive the reboot — {observation.Delta}.");
            var failed = await RecordFailureAsync(
                    resource,
                    AttemptsWithin(pending.Attempt, _services.Options.AttemptBudget),
                    observation.Delta,
                    pending.Change,
                    cancellationToken)
                .ConfigureAwait(false);

            if (failed.Kind.HasGivenUp())
            {
                // The verify that gave up is the last thing this pass does (decision 68). Walking
                // on would act on whatever is ordered ahead of it, and the budget of the next
                // resource to fail for the same underlying cause would be spent on the same fault.
                return await FinishAsync(
                    PassResult.Escalated,
                    Complete([failed], resource.Name),
                    null,
                    $"'{resource.Name}' has been given up on.",
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    private async Task<PassOutcome> WalkAsync(CancellationToken cancellationToken)
    {
        var statuses = new Dictionary<string, ResourceStatus>(_services.Graph.Count, StringComparer.Ordinal);
        var ordered = new List<ResourceStatus>(_services.Graph.Count);
        var acted = false;
        DateTimeOffset? earliest = null;
        var result = PassResult.Converged;
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
                // is to stop rebooting — and rung 4: the pass stops with it, so nothing ordered
                // after this is attempted either.
                var escalated = await RefreshEscalationAsync(resource, entry, cancellationToken).ConfigureAwait(false);
                Record(escalated);

                return await FinishAsync(
                    PassResult.Escalated,
                    Complete(ordered, resource.Name),
                    null,
                    $"'{resource.Name}' has been given up on.",
                    cancellationToken).ConfigureAwait(false);
            }

            if (entry.NextAttemptUtc is { } next && _services.Clock.UtcNow < next)
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
                if (entry.Attempts > 0 || entry.Escalations > 0 || entry.NextAttemptUtc is not null)
                {
                    ClearLedger(resource.Name);
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
                // A resource gave up while this pass was running (decision 68). Everything after
                // it in the order is not merely unattempted but must stay so, and the list is
                // completed from the catalog on the way out so the operator still sees all of it.
                return await FinishAsync(
                    PassResult.Escalated,
                    Complete(ordered, resource.Name),
                    earliest,
                    detail ?? $"'{resource.Name}' has been given up on.",
                    cancellationToken).ConfigureAwait(false);
            }
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
                var failed = await RecordFailureAsync(resource, attempt, delta, change, cancellationToken)
                    .ConfigureAwait(false);

                return await FinishAsync(
                    failed.Kind.HasGivenUp() ? PassResult.Escalated : PassResult.Pending,
                    failed.Kind.HasGivenUp() ? Complete([failed], resource.Name) : [failed],
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
                    ClearLedger(resource.Name);

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
                var failed = await RecordFailureAsync(resource, attempt, after.Delta, change, cancellationToken)
                    .ConfigureAwait(false);

                return await FinishAsync(
                    failed.Kind.HasGivenUp() ? PassResult.Escalated : PassResult.Rebooted,
                    failed.Kind.HasGivenUp() ? Complete([failed], resource.Name) : [failed],
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
    private async Task<ResourceStatus> RecordFailureAsync(
        IResource resource,
        int attempt,
        string delta,
        string change,
        CancellationToken cancellationToken)
    {
        var options = _services.Options;

        if (attempt < options.AttemptBudget)
        {
            // Rung 1: retry with a growing delay. The wait exists to stop a reboot loop wearing
            // the hardware (§2.4), not to be polite about it.
            var wait = _retry.Delay(attempt);
            var next = _services.Clock.UtcNow + wait;

            _services.Journal.Update(state => ReconcileJournal.WithEntry(
                state,
                ReconcileJournal.EntryFor(state, resource.Name) with
                {
                    Attempts = attempt,
                    Delta = delta,
                    Change = change,
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

        _services.Journal.Update(state => ReconcileJournal.WithEntry(
            state,
            ReconcileJournal.EntryFor(state, resource.Name) with
            {
                Attempts = attempt,
                Escalations = escalations,
                EscalationNotified = notified,
                Delta = delta,
                Change = change,
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
    /// <b><see cref="PassResult.Escalated"/> no longer travels through here at all</b> (decision
    /// 68): an escalation ends the pass where it happens, so it is returned directly rather than
    /// merged with whatever else the walk found. It is nevertheless ranked above everything the
    /// walk can produce, so that a future <c>Worst(result, Escalated)</c> added by somebody who has
    /// not read this cannot quietly turn a stopped frame back into a running one.
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

    private void ClearLedger(string resource) =>
        _services.Journal.Update(state => ReconcileJournal.WithEntry(
            state,
            new ResourceLedgerEntry { Resource = resource }));

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
        var worst = statuses
            .Where(status => status.Kind != ResourceStatusKind.InSync)
            .OrderByDescending(status => (int)status.Kind)
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
