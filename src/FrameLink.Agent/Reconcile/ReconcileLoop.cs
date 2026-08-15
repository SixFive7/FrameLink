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

    /// <summary>A resource exhausted its budget; the operator has been told, or will be.</summary>
    Escalated,

    /// <summary>This device has stopped reconciling (§2.5 rung 4).</summary>
    Halted,

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
/// delay, then budget exhausted → stop touching it and mark <c>Degraded</c> with the exact delta
/// and attempt count, then the notification → <c>Escalated</c>, then a second exhaustion after
/// the operator's retry → <c>Halted</c> for the device. Continuing to reboot a persistently
/// broken frame is damage, not diligence.
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

    /// <summary>Whether this device has stopped reconciling (§2.5 rung 4).</summary>
    public bool IsHalted =>
        _services.Journal.Read().Ledger.Any(entry => entry.Halted);

    /// <summary>
    /// Resets one resource's attempt budget — §2.5 rung 3's <b>retry</b> action.
    /// </summary>
    /// <remarks>
    /// Clears the halt as well as the budget. The operator pressing retry on a halted frame has
    /// explicitly asked for another go, and refusing would leave no way back short of a
    /// re-flash. The escalation <i>count</i> is deliberately kept, so a frame that has already
    /// been given up on once halts again the moment the fresh budget runs out rather than
    /// starting the ladder from the bottom.
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
                Halted = false,
                NextAttemptUtc = null,
            });
        });

        _services.Log.Info($"{resource}: the attempt budget was reset by the Fleet Manager.");
    }

    /// <summary>Runs passes until the frame converges, halts, or the agent stops.</summary>
    /// <remarks>
    /// The wait between passes is the shortest of the pending backoffs, so a frame that is
    /// retrying in thirty seconds does not sit idle for the whole drift-detection interval.
    /// </remarks>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var outcome = await RunPassAsync(cancellationToken).ConfigureAwait(false);

            if (outcome.Result is PassResult.Restarting or PassResult.Halted or PassResult.Cancelled)
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

        // Before anything is observed, resumed or acted on. §2.5 rung 4 halts the *device*, so a
        // halt inherited from an earlier process has to stop this pass at its very first
        // instruction — a per-resource check inside the walk would let every resource ordered
        // ahead of the halted one be acted on and rebooted for, on every boot, forever.
        if (HaltedDevice() is { } halted)
        {
            return await FinishAsync(
                PassResult.Halted,
                halted.Statuses,
                null,
                halted.Detail,
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
    /// The device-level halt of §2.5 rung 4, as a pass outcome — or null if this device is still
    /// allowed to reconcile.
    /// </summary>
    /// <remarks>
    /// Every halted resource is reported, not merely the first. An operator deciding whether to
    /// press <b>retry</b> needs to know how many settings gave up, and clearing one halt while
    /// another remains would otherwise look like it had done nothing.
    /// </remarks>
    private (IReadOnlyList<ResourceStatus> Statuses, string Detail)? HaltedDevice()
    {
        List<ResourceStatus>? statuses = null;

        foreach (var entry in _services.Journal.Read().Ledger)
        {
            if (entry.Halted)
            {
                (statuses ??= []).Add(Terminal(entry.Resource, ResourceStatusKind.Halted, entry));
            }
        }

        return statuses is null
            ? null
            : (statuses, $"'{string.Join("', '", statuses.Select(status => status.Name))}' has been given up on.");
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
            attempts: state.Pending?.Attempt ?? 0,
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

            return await CrossAndVerifyAsync(resource, pending.Attempt, pending.Change, pending.Gloss, cancellationToken)
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
            var failed = await RecordFailureAsync(resource, pending.Attempt, observation.Delta, pending.Change, cancellationToken)
                .ConfigureAwait(false);

            if (failed.Kind is ResourceStatusKind.Halted)
            {
                // The verify that halted the device is the last thing this pass does. Walking on
                // would act on whatever is ordered ahead of the halted resource, which is the
                // resource-level reading §2.5 rung 4 rejects.
                return await FinishAsync(
                    PassResult.Halted,
                    [failed],
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
                    Attempts = entry.Attempts,
                    AttemptBudget = _services.Options.AttemptBudget,
                });

                result = Worst(result, PassResult.Pending);
                continue;
            }

            if (entry.Escalations > 0 && entry.Attempts >= _services.Options.AttemptBudget)
            {
                // §2.5 rung 2: stop touching it. The resource is not observed and not acted on
                // until an operator resets the budget, which is what "stop" has to mean if the
                // frame is to stop rebooting.
                var escalated = await RefreshEscalationAsync(resource, entry, cancellationToken).ConfigureAwait(false);
                Record(escalated);
                result = Worst(result, PassResult.Escalated);
                continue;
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
                    Attempts = entry.Attempts,
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
                    Attempts = entry.Attempts,
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
                    Attempts = entry.Attempts,
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
                    Attempts = entry.Attempts,
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

            if (applied.Status.Kind is ResourceStatusKind.Halted)
            {
                // The device halted while this pass was running. Everything after it in the
                // order is not merely unattempted but must stay so — §2.5's rung 4 is "Halted
                // for that device", and rebooting it for a different setting is the same damage
                // under another name.
                detail ??= $"'{resource.Name}' has been given up on.";
                break;
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
        var attempt = entry.Attempts + 1;

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

        var status = outcome.Statuses.Count > 0
            ? outcome.Statuses[^1]
            : new ResourceStatus { Name = resource.Name, Kind = ResourceStatusKind.AwaitingReboot };

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
                    failed.Kind is ResourceStatusKind.Halted ? PassResult.Halted
                        : failed.Kind is ResourceStatusKind.Degraded or ResourceStatusKind.Escalated ? PassResult.Escalated
                        : PassResult.Pending,
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
                    failed.Kind is ResourceStatusKind.Halted ? PassResult.Halted
                        : failed.Kind is ResourceStatusKind.Degraded or ResourceStatusKind.Escalated ? PassResult.Escalated
                        : PassResult.Rebooted,
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

        // Rung 2: the budget is gone. Stop touching it, and say exactly what is wrong.
        var previous = ReconcileJournal.EntryFor(_services.Journal.Read(), resource.Name);
        var escalations = previous.Escalations + 1;
        var halted = escalations >= options.EscalationLimit;

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

        if (halted)
        {
            // Rung 4: an administrator has been told more than once. Continuing to reboot a
            // persistently broken frame is damage, not diligence.
            _services.Log.Fail($"{resource.Name}: escalated {escalations} times. This frame has stopped reconciling.");

            await EmitAsync(
                DeviceEventKinds.Halted,
                resource.Name,
                "This frame has stopped reconciling after repeated escalation on the same setting.",
                delta,
                attempt,
                cancellationToken).ConfigureAwait(false);
        }

        _services.Journal.Update(state => ReconcileJournal.WithEntry(
            state,
            ReconcileJournal.EntryFor(state, resource.Name) with
            {
                Attempts = attempt,
                Escalations = escalations,
                EscalationNotified = notified,
                Halted = halted,
                Delta = delta,
                Change = change,
                NextAttemptUtc = null,
            }));

        return new ResourceStatus
        {
            Name = resource.Name,
            Kind = halted ? ResourceStatusKind.Halted
                : notified ? ResourceStatusKind.Escalated
                : ResourceStatusKind.Degraded,
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
                EscalationSummary(resource, entry.Attempts, entry.Change),
                entry.Delta,
                entry.Attempts,
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
            Attempts = entry.Attempts,
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

    private static DateTimeOffset Sooner(DateTimeOffset? earliest, DateTimeOffset candidate) =>
        earliest is { } current && current <= candidate ? current : candidate;

    private static ResourceStatus Terminal(string name, ResourceStatusKind kind, ResourceLedgerEntry entry) => new()
    {
        Name = name,
        Kind = kind,
        Delta = entry.Delta,
        Action = entry.Change,
        Attempts = entry.Attempts,
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
    /// Not severity — <i>urgency</i>. <see cref="PassResult.Rebooted"/> outranks
    /// <see cref="PassResult.Pending"/> and <see cref="PassResult.Escalated"/> because it is the
    /// only one of the three that means "something changed, run again now". A pass that acts on
    /// one resource and finds another blocked behind it has made progress, and reporting the
    /// blocked one would stall the driver into a wait for a backoff that does not exist.
    /// </remarks>
    private static PassResult Worst(PassResult current, PassResult candidate) =>
        Rank(candidate) > Rank(current) ? candidate : current;

    private static int Rank(PassResult result) => result switch
    {
        PassResult.Converged => 0,
        PassResult.Pending => 1,
        PassResult.Escalated => 2,
        PassResult.Rebooted => 3,
        PassResult.Restarting => 4,
        PassResult.Halted => 5,
        _ => 6,
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
                Halted = result == PassResult.Halted,
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
        PassResult.Halted => LoopStateNames.Halted,
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
                Halted = entry.Halted,
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
