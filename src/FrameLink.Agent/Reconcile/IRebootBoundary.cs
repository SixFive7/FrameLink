using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Reconcile;

/// <summary>What happened when the loop tried to cross a reboot boundary.</summary>
public enum RebootCrossing
{
    /// <summary>
    /// The machine has booted and this code is running on the other side. The loop may verify.
    /// </summary>
    /// <remarks>
    /// On a frame this value is never returned — the process does not survive the reboot, so the
    /// verify happens in the <i>next</i> process, resumed from the journal. It is returned by the
    /// in-process boundary, which is what makes the whole cross-reboot sequence testable.
    /// </remarks>
    Crossed,

    /// <summary>
    /// The reboot was accepted and this process is about to be killed. The loop must stop and
    /// claim nothing.
    /// </summary>
    Restarting,

    /// <summary>The reboot could not be requested at all.</summary>
    Refused,
}

/// <summary>The loop's request to restart the machine.</summary>
public sealed record RebootRequest
{
    /// <summary>Which resource's change is being proven.</summary>
    public required string Resource { get; init; }

    /// <summary>The change that was just written, verbatim, for the journal and the log.</summary>
    public required string Change { get; init; }

    /// <summary>Which attempt this is.</summary>
    public required int Attempt { get; init; }
}

/// <summary>The result of a crossing.</summary>
/// <param name="Crossing">What happened.</param>
/// <param name="Detail">Why, when it was refused.</param>
public readonly record struct RebootOutcome(RebootCrossing Crossing, string? Detail = null);

/// <summary>
/// <b>The reboot boundary.</b> The seam that makes §2.4's "every resource reboots" testable.
/// </summary>
/// <remarks>
/// <para>
/// §2.4 forbids claiming "applied" from a successful write: only an observation made after the
/// setting had to survive a boot counts. That rule puts a process death in the middle of the
/// resource contract, which would normally make the most important behaviour in the agent the
/// one behaviour no test can reach. This interface is the answer, and it is a deliverable in its
/// own right because everything in M3 depends on being able to test it.
/// </para>
/// <para>
/// It works because the loop is written to be <b>resumable rather than continuous</b>. Before
/// crossing, the loop writes its intent to the journal; after crossing, it verifies. Those two
/// halves are the same two halves whether the boundary killed the process or not:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="SystemRebootBoundary"/> returns <see cref="RebootCrossing.Restarting"/> and the
/// machine goes down. The verify happens in the next process, which reads the journal and
/// re-enters at the verify step.
/// </description></item>
/// <item><description>
/// <see cref="InProcessRebootBoundary"/> returns <see cref="RebootCrossing.Crossed"/> after
/// changing the boot identity and running whatever the machine's <i>other</i> owners do at boot.
/// The same loop then verifies inline.
/// </description></item>
/// </list>
/// <para>
/// The second one is not a mock of the first: both drive the identical journal-write, identical
/// boot-identity comparison and identical verify. What differs is only whether the process
/// survives — and the journal exists precisely so that it does not have to.
/// </para>
/// </remarks>
public interface IRebootBoundary
{
    /// <summary>Restarts the machine so the change has to survive a boot.</summary>
    Task<RebootOutcome> CrossAsync(RebootRequest request, CancellationToken cancellationToken);
}

/// <summary>Asks systemd to reboot the frame.</summary>
/// <remarks>
/// Returns rather than blocking forever. <c>systemctl reboot</c> is asynchronous — it returns
/// as soon as the job is queued — and the loop has to be able to stop cleanly in the seconds
/// between the request and the machine actually going down, or the last thing on the screen
/// before the panel dies is a frame mid-repaint.
/// </remarks>
public sealed class SystemRebootBoundary : IRebootBoundary
{
    private readonly ISystemControl _systemControl;
    private readonly IAgentLog _log;

    /// <summary>Creates the boundary over <paramref name="systemControl"/>.</summary>
    public SystemRebootBoundary(ISystemControl systemControl, IAgentLog log)
    {
        ArgumentNullException.ThrowIfNull(systemControl);
        ArgumentNullException.ThrowIfNull(log);

        _systemControl = systemControl;
        _log = log;
    }

    /// <inheritdoc/>
    public async Task<RebootOutcome> CrossAsync(RebootRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        _log.Info($"{request.Resource}: rebooting to prove '{request.Change}' stuck (attempt {request.Attempt}).");

        var result = await _systemControl.RunAsync(["reboot"], cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return new RebootOutcome(RebootCrossing.Restarting);
        }

        // A refused reboot is a first-class failure, not an exception. The frame carries on
        // running and the resource stays AwaitingReboot with a reason on screen — which is the
        // honest picture, because the change really has been written and really has not been
        // proven.
        _log.Fail($"{request.Resource}: the reboot was refused — {result.Output}");
        return new RebootOutcome(RebootCrossing.Refused, result.Output);
    }
}

/// <summary>
/// <b>The device-level reboot floor of §2.4</b> — decision 79.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not the escalation ladder, and could not be.</b> §2.5's ladder counts
/// <i>failures</i>: a resource fails post-boot verification, spends an attempt, and escalates when
/// the budget is gone. A livelock is made of successes. Measured on the frame, one mixer value was
/// applied, verified across a reboot, and reverted afterwards by a second owner — so the attempt
/// counter never passed <c>1/3</c>, nothing ever escalated, decision 68 never stopped the pass, and
/// the frame took ~25 reboots in eleven minutes with every surface reporting it as working. Every
/// protection §2.4 and §2.5 provide is keyed on something failing, and nothing was.
/// </para>
/// <para>
/// <b>So this counts the thing itself.</b> It sits in front of whatever boundary actually restarts
/// the machine, keeps a durable list of when the recent reboots were requested, and refuses past
/// <see cref="ReconcileOptions.RebootFloorCount"/> inside
/// <see cref="ReconcileOptions.RebootFloorWindow"/> — whatever any resource, ledger or status
/// claims. It is a floor rather than a diagnosis, and it is deliberately dumb: the diagnosis is
/// decision 78's conflict-drift rule, which stops the measured fault inside a minute, and this
/// exists for the livelocks that rule does not model.
/// </para>
/// <para>
/// <b>A refusal reaches a person through the ladder, which is the one thing it does borrow.</b>
/// <see cref="RebootCrossing.Refused"/> is already a first-class outcome — the change is written and
/// cannot be proven — so the resource spends an attempt and escalates on the ordinary schedule, at
/// no cost in reboots because none of them happen. What travels with it is
/// <see cref="RebootOutcome.Detail"/>, and it is written as a whole sentence because that string
/// becomes the delta on the frame's own screen and in the operator's notification.
/// </para>
/// <para>
/// <b>It fails open, on purpose.</b> A clock that has jumped — a Pi that came up before NTP — can
/// only make this <i>forget</i> reboots, never invent them, because entries dated in the future are
/// dropped along with entries older than the window. A floor that broke a provision would be worse
/// than no floor at all, and decision 78 is the mechanism that is allowed to be strict.
/// </para>
/// </remarks>
public sealed class RebootFloor : IRebootBoundary
{
    private readonly IRebootBoundary _inner;
    private readonly ReconcileJournal _journal;
    private readonly IAgentClock _clock;
    private readonly IAgentLog _log;
    private readonly int _limit;
    private readonly TimeSpan _window;

    /// <summary>Wraps <paramref name="inner"/> in the floor.</summary>
    public RebootFloor(
        IRebootBoundary inner,
        ReconcileJournal journal,
        IAgentClock clock,
        IAgentLog log,
        int limit,
        TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(log);

        _inner = inner;
        _journal = journal;
        _clock = clock;
        _log = log;
        _limit = limit;
        _window = window;
    }

    /// <summary>The reboots inside the window, as of <paramref name="now"/>.</summary>
    /// <remarks>
    /// Entries dated after <paramref name="now"/> are dropped as well as entries older than
    /// <paramref name="window"/>: a clock that went backwards would otherwise leave a frame holding
    /// timestamps it can never age out, and the floor would stick shut for as long as the window.
    /// </remarks>
    public static IReadOnlyList<DateTimeOffset> Within(
        IReadOnlyList<DateTimeOffset> reboots,
        DateTimeOffset now,
        TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(reboots);

        var kept = new List<DateTimeOffset>(reboots.Count);
        foreach (var at in reboots)
        {
            if (at <= now && now - at < window)
            {
                kept.Add(at);
            }
        }

        return kept;
    }

    /// <summary>How many reboots this frame has taken inside the window.</summary>
    public int Recent() => Within(_journal.Read().Reboots, _clock.UtcNow, _window).Count;

    /// <summary>
    /// Forgets every recorded reboot — what a human pressing <b>retry</b> means for this counter.
    /// </summary>
    /// <remarks>
    /// The same reasoning decision 67 gives for a retry granting a fresh attempt budget: a person
    /// has arrived, and the frame is being asked to try again by somebody who can see it. Leaving
    /// the history in place would make the retry silently powerless on exactly the frame that has
    /// reached the floor, which is the failure decision 75 records at length.
    /// </remarks>
    public void Forget()
    {
        _journal.Update(state => state with { Reboots = [] });

        // The same person, in the same act, clearing the same kind of doubt: an unreadable journal
        // is a reason to stop rebooting and a retry is somebody standing at the frame saying try
        // anyway. Leaving the fault set here would make the button visibly powerless on the one
        // frame that most needs it, which is the failure decision 75 records at length.
        _journal.Forgive();
    }

    /// <inheritdoc/>
    public async Task<RebootOutcome> CrossAsync(RebootRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _clock.UtcNow;

        // A floor that cannot count must not permit. An unreadable journal means the list below is
        // empty because the record was lost, not because this frame has not rebooted — and an
        // empty list is indistinguishable from a frame that has taken none, which is precisely the
        // silent reset this closes. Refusing costs a stalled provision; permitting costs the
        // unbounded reboot loop §2.4 calls the more damaging of the two, and costs it on a frame
        // that has already given one hard piece of evidence that its card is failing.
        if (_journal.Unreadable)
        {
            var lost =
                "this frame could not read its own record of recent reboots, so it has stopped "
                + "rebooting until that record can be trusted again";

            _log.Fail(
                $"{request.Resource}: {lost}. Restarting the agent on a journal that parses, or a "
                + "person pressing retry, gives the reboots back.");

            return new RebootOutcome(RebootCrossing.Refused, lost);
        }

        var recent = Within(_journal.Read().Reboots, now, _window);

        if (_limit > 0 && recent.Count >= _limit)
        {
            var detail = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"this frame has already taken {recent.Count} reboots in the last {(int)_window.TotalHours} hours and has stopped rebooting");

            _log.Fail($"{request.Resource}: {detail}. Nothing further will be rebooted for until a person asks it to try again.");

            // The list is left exactly as it is. Recording a reboot that did not happen would push
            // the frame further past a floor it is already holding at, and the window has to be able
            // to age out on its own for a frame nobody comes to.
            return new RebootOutcome(RebootCrossing.Refused, detail);
        }

        // Written before the machine is asked to go down, never after, for the same reason the
        // pending-apply record is: on a frame the process does not come back, so anything recorded
        // after the request is recorded never.
        _journal.Update(state => state with { Reboots = [.. recent, now] });

        return await _inner.CrossAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// <b>A boundary that refuses while something on this frame must not be interrupted.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not an exception to §2.4, and the distinction is the whole point.</b> "Every resource
/// reboots, no exceptions, no per-resource cleverness" is about a resource deciding whether
/// <i>its own</i> change needs proving, and that decision stays exactly where it is —
/// <see cref="IResource"/> has no reboot member and still never will. What this adds is a
/// device-level condition under which the machine cannot be restarted at all, which §2.4 already
/// has a first-class answer for: <see cref="RebootCrossing.Refused"/>. The change is written and
/// cannot be proven, so it spends an attempt and reaches a person on §2.5's ordinary schedule. That
/// is the identical shape decision 79's reboot floor uses, and it is why a firmware flash needed no
/// new vocabulary anywhere in the loop.
/// </para>
/// <para>
/// <b>What it exists for</b> (decision 91): a DFU write to the microphone array takes about thirty
/// seconds, is deliberately not a resource, and can therefore never be the thing that triggers a
/// reboot — but any <i>other</i> resource's Act, on a pass that happens to overlap it, would take
/// the machine down mid-write. Rebooting an array immediately after a firmware write is separately
/// known to be a bad idea: upstream issue #20 reports a soft reboot leaving capture broken until a
/// physical replug. So the hold is on the boundary, where it covers every caller and needs to know
/// nothing about resources.
/// </para>
/// <para>
/// It takes a predicate rather than the flash itself, so nothing in the reconcile layer has to know
/// what a firmware image is, and a second reason to hold the machine still later costs one more
/// delegate rather than a second decorator.
/// </para>
/// </remarks>
public sealed class RebootHold : IRebootBoundary
{
    private readonly IRebootBoundary _inner;
    private readonly Func<string?> _held;
    private readonly IAgentLog _log;

    /// <summary>Wraps <paramref name="inner"/>, refusing whenever <paramref name="held"/> answers.</summary>
    /// <param name="inner">The boundary that actually restarts the machine.</param>
    /// <param name="held">
    /// A whole sentence saying why the machine must stay up, or null when it may go down.
    /// </param>
    /// <param name="log">Where a refusal is recorded.</param>
    public RebootHold(IRebootBoundary inner, Func<string?> held, IAgentLog log)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(log);

        _inner = inner;
        _held = held;
        _log = log;
    }

    /// <inheritdoc/>
    public async Task<RebootOutcome> CrossAsync(RebootRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_held() is { Length: > 0 } reason)
        {
            _log.Fail($"{request.Resource}: the reboot was refused — {reason}.");
            return new RebootOutcome(RebootCrossing.Refused, reason);
        }

        return await _inner.CrossAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// A boundary that crosses without restarting anything.
/// </summary>
/// <remarks>
/// <para>
/// Two uses, and neither is a stub. In the test suite it is how the full
/// write-journal-reboot-verify sequence runs on a workstation, including the case that matters
/// most: <see cref="OnBoot"/> is where the machine's <i>other</i> owners get their turn, so a
/// test can model cloud-init putting the hostname back exactly as the mule does.
/// </para>
/// <para>
/// In a container — a virtual agent per §5.3 — it is also the correct production behaviour,
/// because a container has no machine to reboot and a <c>systemctl reboot</c> there would simply
/// fail. A virtual agent that reconciles without rebooting is a faithful model of every
/// non-hardware resource.
/// </para>
/// </remarks>
public sealed class InProcessRebootBoundary : IRebootBoundary
{
    private readonly MutableBootIdentity _boot;

    /// <summary>Creates a boundary that advances <paramref name="boot"/> on every crossing.</summary>
    public InProcessRebootBoundary(MutableBootIdentity boot)
    {
        ArgumentNullException.ThrowIfNull(boot);
        _boot = boot;
    }

    /// <summary>Every crossing so far, in order.</summary>
    public List<RebootRequest> Crossings { get; } = [];

    /// <summary>
    /// What happens during the boot, before the loop is allowed to verify.
    /// </summary>
    /// <remarks>
    /// This is the whole reason the abstraction pays for itself. cloud-init, WirePlumber's
    /// device-state restore, <c>alsa-restore</c>, a package postinst — all of them run here on a
    /// real frame, and all of them can put a setting back. Modelling them as a callback makes
    /// "the write succeeded and the setting is still wrong" an ordinary, assertable outcome.
    /// </remarks>
    public Func<RebootRequest, CancellationToken, Task>? OnBoot { get; set; }

    /// <inheritdoc/>
    public async Task<RebootOutcome> CrossAsync(RebootRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Crossings.Add(request);
        _boot.Advance();

        if (OnBoot is not null)
        {
            await OnBoot(request, cancellationToken).ConfigureAwait(false);
        }

        return new RebootOutcome(RebootCrossing.Crossed);
    }
}
