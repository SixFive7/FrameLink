namespace FrameLink.Control.Storage;

/// <summary>
/// The device table, behind an interface.
/// </summary>
/// <remarks>
/// §3.1 keeps a repository seam in front of storage so that a later move to Postgres stays
/// contained. Nothing in this contract mentions SQLite, connections, transactions or SQL —
/// a second implementation only has to honour the behaviours the tests assert.
/// </remarks>
public interface IDeviceStore
{
    /// <summary>Reads one device, or null if this Fleet Manager has never met it.</summary>
    Task<DeviceRecord?> FindAsync(string deviceId, CancellationToken cancellationToken);

    /// <summary>
    /// Folds a proven contact into the table, creating the row as
    /// <see cref="DeviceState.Pending"/> if it is new.
    /// </summary>
    /// <param name="contact">The proven claim.</param>
    /// <param name="pendingCap">
    /// Ceiling on un-adopted rows. When a <i>new</i> device would exceed it, the least
    /// recently seen pending rows are evicted to make room (§3.3).
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The row as it now stands.</returns>
    /// <remarks>
    /// Adoption state is never changed here. A blocked device that reconnects stays blocked,
    /// and an adopted device that reconnects stays adopted — only the operator moves a device
    /// between states.
    /// </remarks>
    Task<DeviceRecord> RecordContactAsync(DeviceContact contact, int pendingCap, CancellationToken cancellationToken);

    /// <summary>
    /// Lists devices, newest contact first.
    /// </summary>
    /// <param name="includeBlocked">
    /// False by default in the GUI (§3.3): blocked devices are filtered from the list but
    /// remain visible behind a toggle, so an accidental block is reversible.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<IReadOnlyList<DeviceRecord>> ListAsync(bool includeBlocked, CancellationToken cancellationToken);

    /// <summary>Adopts a device and binds the operator's name to it.</summary>
    /// <returns>The adopted row, or null if the device is unknown.</returns>
    Task<DeviceRecord?> AdoptAsync(string deviceId, string? displayName, CancellationToken cancellationToken);

    /// <summary>Blocks a device. Its name is kept so the operator can see what they blocked.</summary>
    /// <returns>The blocked row, or null if the device is unknown.</returns>
    Task<DeviceRecord?> BlockAsync(string deviceId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns a device to <see cref="DeviceState.Pending"/>, dropping everything adoption
    /// granted it.
    /// </summary>
    /// <remarks>
    /// This is the reverse of adoption, so it must also revoke: the display name is cleared
    /// and every per-device setting override is deleted. Leaving overrides behind would mean
    /// a pending row owned resources, which §3.3 forbids.
    /// </remarks>
    /// <returns>The pending row, or null if the device is unknown.</returns>
    Task<DeviceRecord?> ReturnToPendingAsync(string deviceId, CancellationToken cancellationToken);

    /// <summary>Deletes a device and everything attached to it.</summary>
    /// <returns>True if a row was removed.</returns>
    Task<bool> ForgetAsync(string deviceId, CancellationToken cancellationToken);

    /// <summary>Counts un-adopted rows, for the abuse budget of §3.3.</summary>
    Task<int> CountPendingAsync(CancellationToken cancellationToken);

    /// <summary>Deletes pending rows last seen before <paramref name="cutoffUtc"/>.</summary>
    /// <returns>How many rows were expired.</returns>
    Task<int> ExpirePendingAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken);
}
