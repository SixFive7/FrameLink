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
/// It reboots nothing and breaks nothing, which is the point: §5.1's walking skeleton retires
/// <i>integration</i> risk, and a resource that can brick a mule would add risk of a kind M1 is
/// not trying to carry. Real resources — <c>config.txt</c> lines, mixer controls, unit files —
/// arrive in M3 behind the reboot-verified apply.
/// </para>
/// </remarks>
public sealed class DeviceNameResource : IResource
{
    /// <summary>File name inside the state store.</summary>
    public const string FileName = "device-name";

    private readonly IStateStore _store;
    private readonly Func<string?> _desired;

    /// <summary>Creates the resource over <paramref name="store"/>.</summary>
    /// <param name="store">Where the observed value lives.</param>
    /// <param name="desired">
    /// The name the Fleet Manager assigned, read at observation time rather than captured, so a
    /// name changed while the frame is running is drift the next pass corrects.
    /// </param>
    public DeviceNameResource(IStateStore store, Func<string?> desired)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(desired);

        _store = store;
        _desired = desired;
    }

    /// <inheritdoc/>
    public string Name => "device-name";

    /// <inheritdoc/>
    public string Detected => "The name this frame has been given does not match the Fleet Manager.";

    /// <inheritdoc/>
    public string WhyItMatters => "The name is how you tell this frame apart from the others.";

    /// <inheritdoc/>
    public ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var expected = _desired() ?? string.Empty;
        var observed = _store.ReadText(FileName) ?? string.Empty;

        return ValueTask.FromResult(new ResourceObservation(
            string.Equals(expected, observed, StringComparison.Ordinal),
            expected,
            observed));
    }

    /// <inheritdoc/>
    public ValueTask<string> ActAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var expected = _desired() ?? string.Empty;
        _store.WriteText(FileName, expected);

        return ValueTask.FromResult($"write '{expected}' to {_store.PathOf(FileName)}");
    }
}
