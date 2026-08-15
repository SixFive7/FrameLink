using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Reconcile;

/// <summary>
/// M1's one trivial resource: the frame's display name, as the Fleet Manager set it.
/// </summary>
/// <remarks>
/// <para>
/// Chosen because it exercises everything M1 has to prove and risks nothing. The desired value
/// arrives from the server (<c>HandshakeResult.DeviceName</c>), which is §2.2's "static logic,
/// dynamic values" in its smallest possible form — the agent holds the logic, the Fleet Manager
/// supplies the value, and nothing executable crosses the wire. The observed value is a file
/// the operator can <c>cat</c>, so adoption in the GUI and convergence on the frame are
/// visible in the same second.
/// </para>
/// <para>
/// It survives into M2 unchanged in behaviour but no longer unchanged in treatment: it now
/// reboots like everything else (§2.4, no exceptions) and it depends on
/// <see cref="Resources.AdoptionResource"/>, because the value it converges on is one the Fleet
/// Manager issues and §3.3 gives a pending device none.
/// </para>
/// </remarks>
public sealed class DeviceNameResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "app.config.identity";

    /// <summary>File name inside the state store.</summary>
    public const string FileName = "device-name";

    private readonly IStateStore _store;
    private readonly Func<string?> _desired;

    /// <summary>Creates the resource over <paramref name="store"/>.</summary>
    /// <param name="store">Where the observed value lives.</param>
    /// <param name="desired">
    /// The name the Fleet Manager assigned, read at observation time rather than captured, so a
    /// name changed while the frame is running is drift the next pass corrects.
    /// <b>Null and empty are different answers.</b> Empty is the server saying this frame has no
    /// name; null is the server not having said anything, and the two must not be collapsed —
    /// collapsing them is how an outage used to erase a name the Fleet Manager had issued, write
    /// the empty string over it, and then report the resource green for agreeing with itself.
    /// </param>
    public DeviceNameResource(IStateStore store, Func<string?> desired)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(desired);

        _store = store;
        _desired = desired;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn => [Resources.AdoptionResource.ResourceName];

    /// <inheritdoc/>
    public string Detected => "The name this frame has been given does not match the Fleet Manager.";

    /// <inheritdoc/>
    public string WhyItMatters => "The name is how you tell this frame apart from the others.";

    /// <inheritdoc/>
    public ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var expected = _desired();
        if (expected is null)
        {
            return ValueTask.FromResult(ResourceObservation.Unevaluable(
                "the name the Fleet Manager assigned",
                "the Fleet Manager has not answered, so the name it assigned is not known"));
        }

        var observed = _store.ReadText(FileName) ?? string.Empty;

        return ValueTask.FromResult(new ResourceObservation(
            string.Equals(expected, observed, StringComparison.Ordinal),
            expected,
            observed));
    }

    /// <inheritdoc/>
    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var expected = _desired();
        if (expected is null)
        {
            // Unreachable through the loop, which does not act on an unevaluable observation.
            // Writing nothing is still the right behaviour if it ever is reached: the file holds
            // a value the Fleet Manager issued, and an outage is not a reason to delete it.
            return ValueTask.FromResult(new ResourceAction(
                $"nothing was written to {_store.PathOf(FileName)}",
                "Waiting for the Fleet Manager to say what this frame is called."));
        }

        _store.WriteText(FileName, expected);

        return ValueTask.FromResult(new ResourceAction(
            $"write '{expected}' to {_store.PathOf(FileName)}",
            $"Remembering that this frame is called '{expected}'."));
    }
}
