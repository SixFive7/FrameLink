using FrameLink.Protocol;

namespace FrameLink.Agent.Telemetry;

/// <summary>
/// Where the loop puts what it has to say — the <c>telemetry</c> and <c>events</c> channels
/// of §4.1.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EventAsync"/> returns whether the event actually reached the Fleet Manager, and
/// that boolean is load-bearing rather than informational. §2.5's rung 3 is "the Fleet Manager
/// notifies the operator", and §2.3's vocabulary spells the resulting status
/// <c>Escalated(admin-notified)</c>. A frame that buffered an escalation while its server was
/// unreachable has notified nobody, so it stays <c>Degraded</c> — and becomes
/// <c>Escalated</c> only when the buffer drains. Collapsing the two would let a frame claim an
/// administrator had been told while the message sat on its own SD card.
/// </para>
/// <para>
/// <see cref="ReportAsync"/> returns nothing, because a report is the current picture and a
/// lost one is replaced by the next.
/// </para>
/// </remarks>
public interface IReconcileTelemetry
{
    /// <summary>Publishes the whole loop state (§3.5).</summary>
    ValueTask ReportAsync(ReconcileReport report, CancellationToken cancellationToken);

    /// <summary>Publishes one event, reporting whether it was delivered rather than buffered.</summary>
    ValueTask<bool> EventAsync(DeviceEvent deviceEvent, CancellationToken cancellationToken);
}

/// <summary>Accepts everything and remembers it. Used where no link exists.</summary>
/// <remarks>
/// Not a null object: it reports <see langword="false"/> from <see cref="EventAsync"/>, because
/// an agent with nowhere to send an escalation has not notified anybody, and saying otherwise
/// would make an offline frame claim it had.
/// </remarks>
public sealed class NullReconcileTelemetry : IReconcileTelemetry
{
    /// <summary>Every report handed over, newest last.</summary>
    public List<ReconcileReport> Reports { get; } = [];

    /// <summary>Every event handed over, newest last.</summary>
    public List<DeviceEvent> Events { get; } = [];

    /// <inheritdoc/>
    public ValueTask ReportAsync(ReconcileReport report, CancellationToken cancellationToken)
    {
        Reports.Add(report);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<bool> EventAsync(DeviceEvent deviceEvent, CancellationToken cancellationToken)
    {
        Events.Add(deviceEvent);
        return ValueTask.FromResult(false);
    }
}
