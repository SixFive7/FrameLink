using FrameLink.Protocol;

namespace FrameLink.Agent.Telemetry;

/// <summary>
/// Where the package inventory goes — the <c>telemetry</c> channel of §4.1.
/// </summary>
/// <remarks>
/// <para>
/// A seam of its own rather than a third member on <see cref="IReconcileTelemetry"/>, because the
/// two answer to different owners. That interface is the reconciliation loop's, and everything on
/// it is produced by a pass: a report is where the loop stands, an event is something the loop
/// did. An inventory is neither — it is an observation about the machine that nothing converges,
/// taken on its own schedule by something outside the loop entirely.
/// </para>
/// <para>
/// Returning nothing, for the same reason <see cref="IReconcileTelemetry.ReportAsync"/> does: an
/// inventory is the current picture, so a lost one is replaced by the next rather than mattering
/// on its own. <see cref="IReconcileTelemetry.EventAsync"/>'s boolean exists only because §2.5
/// makes "did an administrator actually hear about this" a state the frame has to render, and
/// nothing here has that property.
/// </para>
/// </remarks>
public interface IPackageTelemetry
{
    /// <summary>Publishes one inventory, sending it or buffering it for the next reconnect.</summary>
    ValueTask InventoryAsync(PackageInventory inventory, CancellationToken cancellationToken);
}
