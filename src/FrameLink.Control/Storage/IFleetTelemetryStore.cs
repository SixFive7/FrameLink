using FrameLink.Protocol;

namespace FrameLink.Control.Storage;

/// <summary>
/// Reconciliation reports and device events, behind the repository seam of §3.1.
/// </summary>
/// <remarks>
/// <para>
/// Two shapes with two lifetimes, kept apart for that reason. A <see cref="ReconcileReport"/> is
/// the current picture and only the newest one is worth anything, so it is stored one row per
/// device and replaced. A <see cref="DeviceEvent"/> is history, and §3.5 keeps a month of it and
/// then rolls it off.
/// </para>
/// <para>
/// Neither ever carries photo or call content, which §3.5 states as a flat prohibition rather
/// than a policy — the schema simply has nowhere to put any.
/// </para>
/// </remarks>
public interface IFleetTelemetryStore
{
    /// <summary>Replaces a device's latest reconciliation report.</summary>
    /// <remarks>
    /// A report older than the one already stored is ignored. §4.1 buffers telemetry on disk
    /// when a frame is offline and drains it on reconnect, so an out-of-order arrival is
    /// ordinary rather than exceptional, and the newest picture must win.
    /// </remarks>
    Task RecordReportAsync(ReconcileReport report, CancellationToken cancellationToken);

    /// <summary>Reads a device's latest report, or null if it has never sent one.</summary>
    Task<ReconcileReport?> GetReportAsync(string deviceId, CancellationToken cancellationToken);

    /// <summary>Appends one event.</summary>
    Task RecordEventAsync(DeviceEvent deviceEvent, CancellationToken cancellationToken);

    /// <summary>Reads a device's events, newest first.</summary>
    Task<IReadOnlyList<DeviceEvent>> ListEventsAsync(string deviceId, int limit, CancellationToken cancellationToken);

    /// <summary>Deletes events older than <paramref name="cutoffUtc"/> (§3.5's one month).</summary>
    /// <returns>How many rows were removed.</returns>
    Task<int> ExpireEventsAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken);
}
