namespace FrameLink.Control.Storage;

/// <summary>
/// The settings mechanism of §3.4: a fleet default with a per-device override, the override
/// always winning.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately generic. The store knows nothing about volume, backlight schedules, album
/// ids or countdown durations — a setting is an opaque key and an opaque string value,
/// because §3.4 is explicit that the list will grow and that the mechanism matters more than
/// its current contents.
/// </para>
/// <para>
/// The one thing this contract is <i>not</i> neutral about is who may hold settings. Every
/// read and write is conditioned on the device being adopted, which is where "a pending
/// device receives nothing" (§3.3) is enforced structurally rather than by remembering to
/// check at each call site.
/// </para>
/// </remarks>
public interface ISettingsStore
{
    /// <summary>Every fleet default.</summary>
    Task<IReadOnlyDictionary<string, string>> GetFleetDefaultsAsync(CancellationToken cancellationToken);

    /// <summary>Every per-device override for one device.</summary>
    Task<IReadOnlyDictionary<string, string>> GetDeviceOverridesAsync(
        string deviceId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the effective settings for a device: fleet defaults overlaid by that device's
    /// overrides.
    /// </summary>
    /// <returns>
    /// An empty value set for any device that is not adopted — including one that does not
    /// exist. This is the structural half of "a pending device receives nothing".
    /// </returns>
    Task<ResolvedSettings> ResolveAsync(string deviceId, CancellationToken cancellationToken);

    /// <summary>Writes a fleet default.</summary>
    Task SetFleetDefaultAsync(string key, string value, CancellationToken cancellationToken);

    /// <summary>Removes a fleet default.</summary>
    /// <returns>True if the key existed.</returns>
    Task<bool> RemoveFleetDefaultAsync(string key, CancellationToken cancellationToken);

    /// <summary>Writes a per-device override.</summary>
    /// <returns>
    /// False, having written nothing, if the device is not adopted. A pending record allocates
    /// no resources (§3.3), and a settings row is a resource.
    /// </returns>
    Task<bool> SetDeviceOverrideAsync(
        string deviceId,
        string key,
        string value,
        CancellationToken cancellationToken);

    /// <summary>Removes a per-device override, restoring the fleet default.</summary>
    /// <returns>True if the override existed.</returns>
    Task<bool> RemoveDeviceOverrideAsync(string deviceId, string key, CancellationToken cancellationToken);

    /// <summary>
    /// A counter bumped by every settings write anywhere in the fleet.
    /// </summary>
    /// <remarks>
    /// Lets an agent tell "nothing changed" from "changed back to what it was" without
    /// diffing, and gives the GUI something cheap to poll.
    /// </remarks>
    Task<long> GetRevisionAsync(CancellationToken cancellationToken);
}

/// <summary>The effective settings of one device at one revision.</summary>
public sealed record ResolvedSettings
{
    /// <summary>Device the values were resolved for.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Settings revision the values were read at.</summary>
    public required long Revision { get; init; }

    /// <summary>Fleet defaults overlaid by per-device overrides. Empty unless adopted.</summary>
    public required IReadOnlyDictionary<string, string> Values { get; init; }
}
