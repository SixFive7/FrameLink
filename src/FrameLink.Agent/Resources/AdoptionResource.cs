using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Agent.Resources;

/// <summary>
/// <c>agent.adoption</c> — the frame holds an adoption record from its Fleet Manager.
/// </summary>
/// <remarks>
/// <para>
/// Decision 34 makes adoption "a reconciled resource; an unadopted frame runs no product", and
/// the catalog puts it at the root of everything the Fleet Manager supplies a value for. It is
/// therefore the DAG root that makes <c>Blocked(dependency)</c> mean something: a frame that has
/// been blocked, forgotten by a rebuilt server, or never adopted does not half-apply the
/// settings it was never issued — its dependents are named as blocked, on its own screen and in
/// the Fleet Manager.
/// </para>
/// <para>
/// <b>Rejection is an answer; silence is not (§2.6).</b> The record is written from the last
/// <i>authoritative</i> answer, so an unreachable server does not un-adopt a frame that was
/// green when contact dropped — the file simply stays where it is, which is the whole reason the
/// answer is persisted rather than held in memory.
/// </para>
/// <para>
/// <b>And silence is not a failed observation either.</b> A frame with no <c>adopted</c> record
/// and no answer does not know whether it is adopted, so it says so: the observation is
/// <see cref="ResourceObservation.Unevaluable"/>, the loop leaves the attempt budget alone, and
/// nothing reboots. Measured on the mule with the Fleet Manager deliberately stopped, the
/// previous two-state reading produced <i>"did not survive the reboot — expected 'adopted',
/// observed 'waiting for adoption'"</i> on a frame that was adopted throughout, burned three of
/// five attempts on a server outage, and would have stopped the whole frame given a long enough
/// one. Rebooting cannot make an unreachable server reachable.
/// </para>
/// </remarks>
public sealed class AdoptionResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "agent.adoption";

    /// <summary>File name inside the state store.</summary>
    public const string FileName = "adoption";

    /// <summary>What the file contains once the frame is adopted.</summary>
    public const string AdoptedMarker = "adopted";

    private readonly IStateStore _store;
    private readonly Func<ServerAnswer> _answer;

    /// <summary>Creates the resource.</summary>
    /// <param name="store">Where the record lives (§2.1, §3.3).</param>
    /// <param name="answer">
    /// What the Fleet Manager last actually said — including whether it said anything at all.
    /// Read at observation time rather than captured, so a frame blocked while it is running
    /// notices on the next pass, and a frame whose server comes back mid-outage converges on the
    /// pass after it answers.
    /// </param>
    public AdoptionResource(IStateStore store, Func<ServerAnswer> answer)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(answer);

        _store = store;
        _answer = answer;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public string Detected => "This frame is not adopted by a Fleet Manager.";

    /// <inheritdoc/>
    public string WhyItMatters => "Until someone adopts it, this frame is not allowed to be given any settings.";

    /// <inheritdoc/>
    public ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var recorded = _store.ReadText(FileName)?.Trim() ?? string.Empty;
        var onRecord = string.Equals(recorded, AdoptedMarker, StringComparison.Ordinal);
        var answer = _answer();

        if (answer is ServerAnswer.Silence)
        {
            // The persisted record is the last authoritative answer, so when it says adopted the
            // frame stands on it and stays green through the outage — that is §2.6's "a frame
            // that was green when contact dropped keeps running", and it is why the answer is
            // written to disk at all. Anything else is genuinely unknown: this frame may have
            // been adopted a minute ago and cannot ask.
            return ValueTask.FromResult(onRecord
                ? new ResourceObservation(true, AdoptedMarker, recorded)
                : ResourceObservation.Unevaluable(
                    AdoptedMarker,
                    "the Fleet Manager has not answered, so whether this frame is adopted is not known"));
        }

        var observed = answer is ServerAnswer.Rejected && onRecord
            ? "adopted here, but the Fleet Manager says otherwise"
            : recorded.Length == 0 ? "no adoption record" : recorded;

        return ValueTask.FromResult(new ResourceObservation(
            answer is ServerAnswer.Adopted && onRecord,
            AdoptedMarker,
            observed));
    }

    /// <inheritdoc/>
    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (_answer())
        {
            case ServerAnswer.Adopted:
                _store.WriteText(FileName, AdoptedMarker);

                return ValueTask.FromResult(new ResourceAction(
                    $"record '{AdoptedMarker}' in {_store.PathOf(FileName)}",
                    "Remembering that this frame has been adopted, so it still knows that if the Fleet Manager goes away."));

            case ServerAnswer.Rejected:
                // The agent cannot adopt itself, and pretending otherwise would write a record
                // that §3.3 says a pending device may not hold. Writing what is actually true is
                // the honest Act: it fails verification, the resource retries and escalates, and
                // the frame's screen keeps saying "adopt me" throughout.
                _store.WriteText(FileName, "waiting for adoption");

                return ValueTask.FromResult(new ResourceAction(
                    $"record 'waiting for adoption' in {_store.PathOf(FileName)}",
                    "Waiting for someone to press Adopt in the Fleet Manager. Nothing on this frame can be set up until then."));

            default:
                // Unreachable through the loop, which never acts on an unevaluable observation.
                // It is written out anyway because the one thing that must not happen here is
                // overwriting a good record on the strength of silence — which is precisely the
                // way this resource used to destroy its own adoption during an outage.
                return ValueTask.FromResult(new ResourceAction(
                    $"nothing was written to {_store.PathOf(FileName)}",
                    "Waiting for the Fleet Manager to answer before deciding anything about this frame."));
        }
    }
}
