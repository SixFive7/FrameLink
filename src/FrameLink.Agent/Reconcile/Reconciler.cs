using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Reconcile;

/// <summary>
/// Runs one resource through the §2.3 contract.
/// </summary>
/// <remarks>
/// <para>
/// This is <b>not</b> the reconciler engine. §5.1 puts the DAG, the retry and backoff schedule,
/// the reboot-verified apply and the escalation ladder in M2; M1 needs only enough of the loop
/// to prove the contract end to end against a real Fleet Manager, which is one resource,
/// applied once, verified once.
/// </para>
/// <para>
/// The one property that is not deferred is level-triggered behaviour (§2.2): this is safe to
/// run repeatedly, and on an already-converged frame it observes and does nothing. Act is
/// reached only through drift.
/// </para>
/// </remarks>
public sealed class Reconciler
{
    private readonly IAgentLog _log;

    /// <summary>Creates a reconciler that narrates to <paramref name="log"/>.</summary>
    public Reconciler(IAgentLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Observes, acts only if drifted, then verifies with the same observation.</summary>
    public async Task<ResourceStatus> ReconcileAsync(IResource resource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var before = await resource.ObserveAsync(cancellationToken).ConfigureAwait(false);
        if (before.InSync)
        {
            return new ResourceStatus { Name = resource.Name, Kind = ResourceStatusKind.InSync };
        }

        _log.Info($"{resource.Name}: drifted, expected '{before.Expected}', observed '{before.Observed}'.");
        var action = await resource.ActAsync(cancellationToken).ConfigureAwait(false);

        // Verify is Observe. Nothing is claimed from the success of the write itself (§2.4).
        var after = await resource.ObserveAsync(cancellationToken).ConfigureAwait(false);
        if (after.InSync)
        {
            _log.Info($"{resource.Name}: in sync after '{action}'.");
            return new ResourceStatus
            {
                Name = resource.Name,
                Kind = ResourceStatusKind.InSync,
                Attempts = 1,
                Action = action,
            };
        }

        var delta = $"expected '{after.Expected}', observed '{after.Observed}'";
        _log.Warn($"{resource.Name}: still drifted after '{action}' — {delta}.");
        return new ResourceStatus
        {
            Name = resource.Name,
            Kind = ResourceStatusKind.Degraded,
            Delta = delta,
            Attempts = 1,
            Action = action,
        };
    }
}
