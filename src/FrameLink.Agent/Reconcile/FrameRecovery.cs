using FrameLink.Agent.Hosting;
using FrameLink.Protocol;

namespace FrameLink.Agent.Reconcile;

/// <summary>
/// <b>One refused press, composed once and read by everything that reports it.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is an object rather than three strings written in three places.</b> A refusal has to
/// reach three surfaces that answer different questions — the frame's own journal (what happened
/// here), a <see cref="DeviceEvent"/> (what happened and when, kept for a month) and the self-report
/// the device row renders (why this frame is not doing what you asked, right now). The first two
/// used to be the whole of it and the third did not exist, so the operator who pressed the button
/// two hundred kilometres away could not be told at all: the HTTP call answers 200 the instant the
/// bytes leave, and there is nothing on that path to carry a "no" back down.
/// </para>
/// <para>
/// <b>They cannot disagree because none of them composes anything.</b> <see cref="Line"/> is
/// <see cref="FrameRecovery.RefusalLine"/>'s output, held once; <see cref="Summary"/> and
/// <see cref="Wire"/> are projections of it. A surface that reworded it would drop the half that
/// matters — <i>nothing has been queued and nothing is waiting its turn</i> — which is the half a
/// person acts on, because every other refusal in this product is answered by asking again later.
/// </para>
/// </remarks>
public sealed record FrameRefusal
{
    /// <summary>
    /// What the event's delta names as the interlock that turned the press down.
    /// </summary>
    /// <remarks>
    /// <b>There is exactly one thing that holds a frame's power state, so it is spelled once.</b>
    /// <see cref="FrameRecoveryServices.Held"/> takes a clause rather than a name because the clause
    /// is what a person reads and it names the image being written; this is the machine-readable
    /// other half, in the same <c>expected … observed …</c> shape the firmware refusal's delta uses.
    /// A second kind of hold would need its own name here, and adding one without it would file two
    /// different reasons under one token.
    /// </remarks>
    public const string HeldBy = "firmware-write-in-flight";

    /// <summary>Which button was refused: one of <see cref="PowerVerbs"/>.</summary>
    public required string Verb { get; init; }

    /// <summary>Who pressed it. For the journal and the trail, never for the frame's own screen.</summary>
    public required string Who { get; init; }

    /// <summary>The whole refusal sentence, from <see cref="FrameRecovery.RefusalLine"/>.</summary>
    public required string Line { get; init; }

    /// <summary>The one sentence the journal and the event trail carry.</summary>
    /// <remarks>
    /// Who asked and what for, then the frame's own words in full. The audit half is the prefix: six
    /// months later "did anybody press it, and which button" is the first question anybody asks
    /// about a frame that stayed on, and a record that only carried the refusal cannot answer it.
    /// </remarks>
    public string Summary => $"{Who} asked this frame to {PowerVerbs.Describe(Verb)} and it was refused. {Line}";

    /// <summary>The same refusal as the self-report carries it.</summary>
    /// <remarks>
    /// <see cref="Who"/> is deliberately not on it. The self-report is the current picture of a
    /// frame and is re-sent on every reconnect; who pressed a button belongs to the moment, which is
    /// what the event is for.
    /// </remarks>
    public PowerRefusalStatus Wire => new() { Verb = Verb, Why = Line };

    /// <summary>The same refusal as the events channel carries it.</summary>
    /// <param name="deviceId">The frame this happened on.</param>
    /// <param name="occurredUtc">When the press was refused, by the frame's own clock.</param>
    /// <remarks>
    /// <para>
    /// <b>Composed here rather than at the call site, so it is the same object the status is made
    /// from.</b> An event assembled beside a telemetry outbox would be a second author of the same
    /// sentence, and the two would drift the first time either was reworded — which is the whole
    /// failure this record exists to make impossible.
    /// </para>
    /// <para>
    /// It carries no resource. Nothing here is about one, nothing alerts on it, and
    /// <see cref="DeviceEvent.Attempts"/> stays at zero because a refusal spends no attempt against
    /// any budget: the frame did not try and fail, it declined.
    /// </para>
    /// </remarks>
    public DeviceEvent ToEvent(string deviceId, DateTimeOffset occurredUtc) => new()
    {
        DeviceId = deviceId,
        Kind = DeviceEventKinds.PowerRefused,
        OccurredUtc = occurredUtc,
        Summary = Summary,
        Delta = $"expected '{Verb}', observed 'refused: {HeldBy}'",
    };
}

/// <summary>Everything the two buttons on a stopped frame need.</summary>
public sealed record FrameRecoveryServices
{
    /// <summary>
    /// Clears every exhausted attempt budget and returns what it cleared — the one reset in the
    /// agent (§2.5 rung 3).
    /// </summary>
    /// <remarks>
    /// A delegate rather than the loop itself, because this class must not be able to reach
    /// anything else on it. What a person pressing a button is allowed to change is the budget and
    /// the power state, and nothing else.
    /// </remarks>
    public required Func<IReadOnlyList<string>> ResetBudgets { get; init; }

    /// <summary>How the machine is restarted or switched off.</summary>
    public required ISystemControl SystemControl { get; init; }

    /// <summary>The journal.</summary>
    public required IAgentLog Log { get; init; }

    /// <summary>
    /// Why the frame must not change power state right now, or null (decision 91).
    /// </summary>
    /// <remarks>
    /// The same delegate <c>RebootHold</c> is built with, and it is here for a sharper version of
    /// the same reason: a reboot in the middle of a firmware write leaves an unbootable microphone
    /// unit, and a <i>power-off</i> in the middle of one leaves the same unit with no chance of the
    /// write ever finishing. The screen that asks for the hold owns the panel while a write runs,
    /// so this is nearly unreachable by hand — which is exactly why it is checked rather than
    /// assumed.
    /// </remarks>
    public Func<string?>? Held { get; init; }

    /// <summary>
    /// Where a refusal goes besides the journal — one call, at the moment of the press.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A delegate for the same reason <see cref="ResetBudgets"/> is one</b>: this class must be
    /// able to say a press was refused without being able to reach a telemetry outbox, a device id
    /// or a clock. What it hands over is the composed <see cref="FrameRefusal"/> and nothing else,
    /// so the caller files it and cannot reword it.
    /// </para>
    /// <para>
    /// <b>Called once per refused press, never on a timer and never on the clear.</b> An event is
    /// history — a moment that happened — so a second press that is refused again is a second event,
    /// and a write finishing is not an event at all. What the <i>current</i> picture does about the
    /// clear is <see cref="FrameRecovery.Refusal"/>'s job, and it needs no notification because it
    /// is not a latch.
    /// </para>
    /// </remarks>
    public Func<FrameRefusal, CancellationToken, ValueTask>? OnRefused { get; init; }
}

/// <summary>
/// <b>The two buttons a person may press on a frame that has stopped</b> — §2.5 rung 5, in the
/// operator's own words: "Shutdown -> stops everything. Or reboot -> forces a new retry."
/// </summary>
/// <remarks>
/// <para>
/// <b>Restart is the retry, not a second mechanism.</b> It clears the attempt budgets through the
/// one reset the agent has — the same one the Fleet Manager's retry and the console's hold call —
/// and then restarts the machine so the fresh budget is spent from a known state rather than from
/// whatever the frame was left in. That ordering is the whole design: the reset is written to the
/// journal <i>before</i> the reboot is asked for, so a frame that goes down between the two comes
/// back with the budget already cleared and reconciles on its own.
/// </para>
/// <para>
/// <b>Neither verb goes through <see cref="IRebootBoundary"/>, and that is deliberate.</b> That
/// boundary exists to prove a change survived a boot, and it carries decision 79's floor, which
/// counts automatic reboots so a livelock cannot wear the card out. A person standing in front of a
/// stopped frame pressing a button is the recovery the floor exists to preserve, not the loop the
/// floor exists to stop — and the reset above has already cleared the floor's history in any case.
/// What is <i>not</i> bypassed is the firmware hold: a write in flight refuses both verbs.
/// </para>
/// <para>
/// <b>Nothing here is on a timer and nothing here retries.</b> The stopped screen sits until
/// somebody acts, locally or from the Fleet Manager. A refusal is recorded and reported rather than
/// re-attempted, because the only thing that should ever produce a second attempt is a second
/// press.
/// </para>
/// <para>
/// <b>A refused press reaches the operator, and it does so twice on purpose.</b>
/// <see cref="FrameRecoveryServices.OnRefused"/> files a <see cref="DeviceEvent"/> — the record of
/// what happened and when, which is what the reconcile screen's trail is — and
/// <see cref="Refusal"/> is what the self-report carries, which is the answer to "why is this frame
/// not doing what I asked, right now". Two questions, two surfaces, one
/// <see cref="FrameRefusal"/>: the HTTP call the operator made has already answered 200 and cannot
/// carry a "no" back down, so without both of these a refused shutdown and a delivered one look
/// identical from a desk.
/// </para>
/// </remarks>
public sealed class FrameRecovery
{
    /// <summary>What <c>systemctl</c> is asked to do for a restart.</summary>
    public const string RestartVerb = "reboot";

    /// <summary>What <c>systemctl</c> is asked to do for a shutdown.</summary>
    public const string ShutdownVerb = "poweroff";

    /// <summary>
    /// <b>Why a press did nothing, and what to wait for</b> — the whole of what a refused button
    /// gets to say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A refusal that only says no is the failure this screen exists to prevent, wearing a
    /// different hat.</b> The hold or the click has already happened; something has to explain
    /// itself, and "it was refused" explains nothing a person can act on. So the sentence carries
    /// three things and in this order: what is happening instead (<paramref name="held"/>, which
    /// arrives already worded from whoever is holding the power state), that nothing has been
    /// queued, and the one thing to do next.
    /// </para>
    /// <para>
    /// <b>"Nothing has been queued" is the load-bearing half.</b> Every other refusal in this
    /// product is answered by pressing again later, and a person who assumes a refused shutdown is
    /// waiting its turn will walk away from a frame that is still on — or, worse, reach for the
    /// plug, which is the exact hazard the refusal was protecting the microphone unit from.
    /// </para>
    /// <para>
    /// <b>It is composed here rather than at the hold, because both verbs and every caller share
    /// it.</b> A hold on the panel, a click in the Fleet Manager and a button on the repair page
    /// are refused by the same predicate for the same reason, so they are refused in the same
    /// words.
    /// </para>
    /// </remarks>
    /// <param name="held">Why the power state must not change, already worded as a clause.</param>
    public static string RefusalLine(string held)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(held);

        return $"Not now — {held}. Nothing has been queued and nothing is waiting its turn: "
            + "wait until this frame's own screen says the microphone update has finished, then ask "
            + "again. Do not unplug it in the meantime.";
    }

    private readonly FrameRecoveryServices _services;

    private FrameRefusal? _refusal;

    /// <summary>Creates the pair of actions over <paramref name="services"/>.</summary>
    public FrameRecovery(FrameRecoveryServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>How many restarts have been asked for since this process started.</summary>
    public int Restarts { get; private set; }

    /// <summary>How many shutdowns have been asked for since this process started.</summary>
    public int Shutdowns { get; private set; }

    /// <summary>Why the last press did nothing, or null when the last one was carried out.</summary>
    /// <remarks>
    /// Every way a press can come to nothing lands here — the firmware hold, a <c>systemctl</c> that
    /// answered no, an exception — because it is the frame's own "what happened when I pressed it".
    /// <see cref="Refusal"/> is the narrower one, and the two are deliberately not the same field:
    /// only the hold is a <i>state</i> that clears itself, and only the hold has a sentence written
    /// for somebody who is not standing at the frame.
    /// </remarks>
    public string? LastRefusal { get; private set; }

    /// <summary>
    /// <b>What this frame is refusing right now</b>, or null when it is refusing nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A refusal is a moment, and this is the part of it that is a state — so it clears
    /// itself.</b> The sentence tells the reader to wait until the frame's own screen says the
    /// firmware write has finished and then ask again, so it is true for exactly as long as that
    /// write is. This re-asks <see cref="FrameRecoveryServices.Held"/> — the same delegate the
    /// sentence was composed from — and answers null the instant the write's window shuts. Nothing
    /// has to remember to clear it, nothing is on a timer, and there is no path on which a frame
    /// that is happily rebooting on request still reports that it refused to.
    /// </para>
    /// <para>
    /// <b>The refusal that is shown is the one that was composed, not a fresh one.</b> The held
    /// clause names the image being written and the attempt it is on, so re-composing the sentence
    /// on every read would let the words drift under a reader while the same write ran. The gate is
    /// re-asked; the sentence is not.
    /// </para>
    /// <para>
    /// <b>It is dropped when the hold goes, not merely hidden behind it.</b> A frame can write
    /// firmware more than once — three attempts inside one operation, and again the next time an
    /// operator authorises one — and a refusal that was only hidden would come back the moment the
    /// next window opened, reporting a press nobody had made about a write it does not name.
    /// </para>
    /// <para>
    /// A refusal from <c>systemctl</c> or an exception never appears here. Those are not held
    /// states — nothing is going to stop being true about them — and the honest thing to show for
    /// one is the journal, which is where they go.
    /// </para>
    /// </remarks>
    public FrameRefusal? Refusal
    {
        get
        {
            if (_services.Held?.Invoke() is { Length: > 0 })
            {
                return _refusal;
            }

            _refusal = null;
            return null;
        }
    }

    /// <summary>
    /// <b>Restart and try again</b> — clears every exhausted budget, then restarts the frame.
    /// </summary>
    /// <param name="who">Who pressed it, for the journal. Never shown on the screen.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether the restart was actually requested.</returns>
    public async Task<bool> RestartAsync(string who, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(who);

        if (Refused(who, PowerVerbs.Restart) is { } refusal)
        {
            await ReportAsync(refusal, cancellationToken).ConfigureAwait(false);
            return false;
        }

        // Before the reboot, never after. A frame that goes down between the two comes back with a
        // fresh budget and reconciles; a frame that went down before the reset would come back
        // exactly as stopped as it was, having spent a reboot to learn nothing.
        var reset = _services.ResetBudgets();

        _services.Log.Info(reset.Count > 0
            ? $"{who} asked this frame to restart and try again: {string.Join(", ", reset)}."
            : $"{who} asked this frame to restart and try again; nothing had given up.");

        Restarts++;

        return await RunAsync(RestartVerb, who, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><b>Shut down</b> — stops everything, and nothing brings it back but a person.</summary>
    /// <param name="who">Who pressed it, for the journal.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether the shutdown was actually requested.</returns>
    public async Task<bool> ShutdownAsync(string who, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(who);

        if (Refused(who, PowerVerbs.Shutdown) is { } refusal)
        {
            await ReportAsync(refusal, cancellationToken).ConfigureAwait(false);
            return false;
        }

        // No budget is touched. A frame that is switched off has not been told to try again, and
        // clearing the ledger here would mean a household that decided to stop found the frame
        // mid-provision when it was next switched on.
        _services.Log.Info($"{who} asked this frame to shut down. Nothing else will happen until somebody switches it on.");

        Shutdowns++;

        return await RunAsync(ShutdownVerb, who, cancellationToken).ConfigureAwait(false);
    }

    private FrameRefusal? Refused(string who, string verb)
    {
        var held = _services.Held?.Invoke();

        if (held is not { Length: > 0 })
        {
            // Only LastRefusal is cleared here. The held refusal is dropped by the one thing that
            // can decide it: the Refusal property asks the same delegate this line just asked, so
            // clearing it a second time here would be a second clearing rule to keep in step with
            // the first — and there is no interleaving in which the two could answer differently.
            LastRefusal = null;
            return null;
        }

        // <b>Composed once, here, and this object is what every surface reads.</b> The journal line
        // below, the event the Fleet Manager files and the token the self-report carries are all
        // projections of it — so the caller's "was this refused" check, the words the refusal is
        // explained in, and the words the operator two hundred kilometres away reads cannot drift
        // apart. There is nowhere left in this product that writes a second version of this
        // sentence.
        var refusal = new FrameRefusal
        {
            Verb = verb,
            Who = who,
            Line = RefusalLine(held),
        };

        _refusal = refusal;
        LastRefusal = refusal.Line;
        _services.Log.Warn(refusal.Summary);
        return refusal;
    }

    /// <summary>Hands a refusal to whoever files it, and never lets that change what it did.</summary>
    /// <remarks>
    /// The reporting is downstream of the interlock in every sense: the press has already been
    /// turned down, the journal already has it, and the frame is already doing exactly what it was
    /// doing before. So a telemetry outbox that throws costs the operator one line in a trail and
    /// nothing else — whereas letting it out of here would fault a task nobody awaits, on a path
    /// reached from a channel event and from an inbound control message, neither of which has
    /// anywhere to put an exception.
    /// </remarks>
    private async Task ReportAsync(FrameRefusal refusal, CancellationToken cancellationToken)
    {
        if (_services.OnRefused is not { } report)
        {
            return;
        }

        try
        {
            await report(refusal, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            _services.Log.Warn(
                $"{refusal.Who}'s refused {PowerVerbs.Describe(refusal.Verb)} could not be reported to the "
                + $"Fleet Manager: {exception.Message}. The refusal itself stands and is in this journal.");
        }
    }

    private async Task<bool> RunAsync(string verb, string who, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _services.SystemControl
                .RunAsync([verb], cancellationToken)
                .ConfigureAwait(false);

            if (result.Succeeded)
            {
                return true;
            }

            // A refused power change is the one failure a person can see the consequence of
            // directly — they pressed a button and the frame stayed exactly as it was — so it is
            // recorded loudly rather than swallowed. There is nothing to retry: the next press is
            // the retry.
            LastRefusal = result.Output;
            _services.Log.Fail($"{who} asked for '{verb}' and systemd refused it: {result.Output}");
            return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // These are called from a channel event and from an inbound control message, neither of
            // which has anywhere to put an exception. An unobserved fault here would be a button
            // that silently did nothing, which is the failure this whole screen exists to prevent.
            LastRefusal = exception.Message;
            _services.Log.Fail($"{who} asked for '{verb}' and it could not be carried out: {exception}");
            return false;
        }
    }
}
