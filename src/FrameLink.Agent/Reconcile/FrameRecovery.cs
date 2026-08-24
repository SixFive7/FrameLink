using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Reconcile;

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
    public string? LastRefusal { get; private set; }

    /// <summary>
    /// <b>Restart and try again</b> — clears every exhausted budget, then restarts the frame.
    /// </summary>
    /// <param name="who">Who pressed it, for the journal. Never shown on the screen.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether the restart was actually requested.</returns>
    public async Task<bool> RestartAsync(string who, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(who);

        if (Refused(who, "restart") is { Length: > 0 })
        {
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

        if (Refused(who, "shut down") is { Length: > 0 })
        {
            return false;
        }

        // No budget is touched. A frame that is switched off has not been told to try again, and
        // clearing the ledger here would mean a household that decided to stop found the frame
        // mid-provision when it was next switched on.
        _services.Log.Info($"{who} asked this frame to shut down. Nothing else will happen until somebody switches it on.");

        Shutdowns++;

        return await RunAsync(ShutdownVerb, who, cancellationToken).ConfigureAwait(false);
    }

    private string? Refused(string who, string verb)
    {
        var held = _services.Held?.Invoke();

        if (held is not { Length: > 0 })
        {
            LastRefusal = null;
            return null;
        }

        // The composed sentence rather than the bare clause, because LastRefusal is what a surface
        // shows and what a person reads. It is the return value too, so the caller's "was this
        // refused" check and the words the refusal is explained in cannot drift apart.
        var refusal = RefusalLine(held);

        LastRefusal = refusal;
        _services.Log.Warn($"{who} asked this frame to {verb} and it was refused. {refusal}");
        return refusal;
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
