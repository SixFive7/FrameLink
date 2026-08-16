using FrameLink.Control.Alerting;

namespace FrameLink.Control.Storage;

/// <summary>
/// The set of alert conditions currently open, behind the repository seam of §3.1.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only open conditions are stored, and that is the whole schema.</b> A closed alert is
/// history, and this server already has a place for history — the log line the delivery wrote.
/// Keeping a resolved-alert table would be a second, worse copy of it, would need §3.5's month
/// of retention applying to it, and would answer no question the log does not.
/// </para>
/// <para>
/// What the table <i>is</i> for is surviving a restart. Without it, every redeploy would
/// re-deliver every condition that was already true — so an operator who has a frame away for
/// repair would be told about it again on every deploy, which is exactly how a channel becomes
/// one people mute.
/// </para>
/// </remarks>
public interface IAlertStore
{
    /// <summary>Every condition currently open, oldest first.</summary>
    Task<IReadOnlyList<OpenAlert>> ListOpenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records a condition as open, or refreshes the wording of one that already is.
    /// </summary>
    /// <returns>
    /// The stored row. <see cref="OpenAlert.OpenedUtc"/> is the <i>first</i> time this condition
    /// was seen, never the time of this call — a frame that has been offline for a week must keep
    /// saying so rather than looking freshly broken at every tick.
    /// </returns>
    Task<OpenAlert> OpenAsync(FleetAlert alert, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    /// <summary>Marks a condition as successfully delivered.</summary>
    /// <remarks>
    /// Separate from <see cref="OpenAsync"/> because delivery can fail. A row whose
    /// <see cref="OpenAlert.NotifiedUtc"/> is still null is retried on the next pass, which is what
    /// makes an unreachable Home Assistant a delay rather than a lost alert.
    /// </remarks>
    Task MarkNotifiedAsync(string key, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    /// <summary>Removes a condition that is no longer true.</summary>
    /// <returns>True when a row was removed.</returns>
    Task<bool> CloseAsync(string key, CancellationToken cancellationToken);
}
