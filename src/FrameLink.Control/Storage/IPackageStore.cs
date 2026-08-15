using FrameLink.Protocol;

namespace FrameLink.Control.Storage;

/// <summary>One frame's currently installed package set, as the Fleet Manager holds it.</summary>
/// <param name="DeviceId">The frame.</param>
/// <param name="Sequence">The agent's own counter for this frame's inventories.</param>
/// <param name="ObservedUtc">When the frame read its dpkg database.</param>
/// <param name="ContentHash">The key the set is stored under, recomputed by this server.</param>
/// <param name="ObservedCount">How many packages the frame said it had installed.</param>
/// <param name="Packages">The set itself, name to version.</param>
public sealed record DevicePackageSet(
    string DeviceId,
    long Sequence,
    DateTimeOffset ObservedUtc,
    string ContentHash,
    int ObservedCount,
    IReadOnlyDictionary<string, string> Packages);

/// <summary>One entry of a frame's package history.</summary>
/// <param name="ObservedUtc">When the frame read the database.</param>
/// <param name="ContentHash">Which set it observed.</param>
/// <param name="Packages">That set, name to version.</param>
/// <remarks>
/// History rows exist only where the set actually changed, because §4.1's agent only reports on
/// change. So consecutive entries are guaranteed to differ, and the diff between two of them is
/// never empty — which is what makes "what changed on this frame recently" a list of real events
/// rather than a list of heartbeats.
/// </remarks>
public sealed record DevicePackageHistoryEntry(
    DateTimeOffset ObservedUtc,
    string ContentHash,
    IReadOnlyDictionary<string, string> Packages);

/// <summary>
/// Per-device package inventories, behind the repository seam of §3.1.
/// </summary>
/// <remarks>
/// <para>
/// <b>Content-addressed, because ~930 rows per device per report is not affordable and is not
/// necessary.</b> The set itself is stored once per <i>distinct</i> set across the whole fleet,
/// keyed by the hash of its canonical rendering; a device's current inventory and every history
/// entry are small rows carrying that key. Ten frames that agree therefore cost one blob, and a
/// frame that reports the same set twice costs nothing at all.
/// </para>
/// <para>
/// <b>Two lifetimes, as everywhere else in this schema.</b> The current set is one row per device,
/// replaced. The history is §3.5's month, rolled off by the same sweep that rolls off events —
/// after which any blob nothing references any more is collected.
/// </para>
/// </remarks>
public interface IPackageStore
{
    /// <summary>Records one inventory as this device's current set and appends it to the history.</summary>
    /// <remarks>
    /// An inventory older than the one already stored is ignored, for the same reason a stale
    /// reconciliation report is: §4.1 buffers on disk while a frame is offline, so an out-of-order
    /// arrival is ordinary and the newest picture has to win.
    /// </remarks>
    Task RecordInventoryAsync(PackageInventory inventory, CancellationToken cancellationToken);

    /// <summary>Reads a device's current set, or null when it has never reported one.</summary>
    Task<DevicePackageSet?> GetAsync(string deviceId, CancellationToken cancellationToken);

    /// <summary>Reads every device's current set, for the fleet-wide comparison.</summary>
    Task<IReadOnlyList<DevicePackageSet>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Reads a device's recent sets, newest first, for "what changed here".</summary>
    Task<IReadOnlyList<DevicePackageHistoryEntry>> ListHistoryAsync(
        string deviceId,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Deletes history entries older than <paramref name="cutoffUtc"/> (§3.5's month).</summary>
    /// <returns>How many entries were removed.</returns>
    Task<int> ExpireHistoryAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken);

    /// <summary>Deletes stored sets that no device and no history entry references any more.</summary>
    /// <returns>How many blobs were collected.</returns>
    Task<int> CollectUnreferencedSetsAsync(CancellationToken cancellationToken);
}
