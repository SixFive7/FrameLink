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
    private readonly Func<bool> _authoritativelyAdopted;

    /// <summary>Creates the resource.</summary>
    /// <param name="store">Where the record lives (§2.1, §3.3).</param>
    /// <param name="authoritativelyAdopted">
    /// Whether the last answer the Fleet Manager actually gave was <c>ok</c>. Read at
    /// observation time rather than captured, so a frame blocked while it is running notices on
    /// the next pass.
    /// </param>
    public AdoptionResource(IStateStore store, Func<bool> authoritativelyAdopted)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(authoritativelyAdopted);

        _store = store;
        _authoritativelyAdopted = authoritativelyAdopted;
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
        var adopted = string.Equals(recorded, AdoptedMarker, StringComparison.Ordinal);

        return ValueTask.FromResult(new ResourceObservation(
            adopted,
            AdoptedMarker,
            recorded.Length == 0 ? "no adoption record" : recorded));
    }

    /// <inheritdoc/>
    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_authoritativelyAdopted())
        {
            // The agent cannot adopt itself, and pretending otherwise would write a record that
            // §3.3 says a pending device may not hold. Writing what is actually true is the
            // honest Act: it fails verification, the resource retries and escalates, and the
            // frame's screen keeps saying "adopt me" throughout.
            _store.WriteText(FileName, "waiting for adoption");

            return ValueTask.FromResult(new ResourceAction(
                $"record 'waiting for adoption' in {_store.PathOf(FileName)}",
                "Waiting for someone to press Adopt in the Fleet Manager. Nothing on this frame can be set up until then."));
        }

        _store.WriteText(FileName, AdoptedMarker);

        return ValueTask.FromResult(new ResourceAction(
            $"record '{AdoptedMarker}' in {_store.PathOf(FileName)}",
            "Remembering that this frame has been adopted, so it still knows that if the Fleet Manager goes away."));
    }
}
