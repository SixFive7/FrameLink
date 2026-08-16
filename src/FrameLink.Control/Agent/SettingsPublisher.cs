using FrameLink.Control.Storage;
using FrameLink.Protocol;

namespace FrameLink.Control.Agent;

/// <summary>
/// Pushes resolved settings down the control channel when they change (§3.4).
/// </summary>
/// <remarks>
/// A push is an optimisation, not the mechanism — same shape as the handshake's relationship
/// to the hourly update check in §2.8. A frame that was offline when the operator changed a
/// value gets the new value in the settings frame it receives on its next connect, so
/// correctness never depends on a push landing.
/// </remarks>
public sealed class SettingsPublisher(
    ISettingsStore settings,
    AgentConnectionRegistry registry,
    TimeProvider clock,
    ILogger<SettingsPublisher> logger)
{
    /// <summary>Fleet setting holding who to contact about this fleet (§3.4, decision 71).</summary>
    public const string OperatorNameKey = "operator.name";

    /// <summary>Fleet setting holding how to reach them.</summary>
    public const string OperatorContactKey = "operator.contact";

    /// <summary>
    /// Resolves who to contact — <b>fleet defaults only, never a device override</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not <see cref="ISettingsStore.ResolveAsync"/>. That method overlays a device's
    /// own overrides, and there are none to overlay: who to contact about a fleet is a property of
    /// the fleet, and a per-frame answer would mean a household could be told to ring somebody
    /// their neighbour is not.
    /// </para>
    /// <para>
    /// It is nevertheless only ever sent to an <i>adopted</i> device (see
    /// <c>AgentSocketHandler</c>), so §3.3's "a pending device receives nothing" stands unchanged.
    /// </para>
    /// </remarks>
    public async Task<OperatorContact> ResolveContactAsync(CancellationToken cancellationToken)
    {
        var defaults = await settings.GetFleetDefaultsAsync(cancellationToken).ConfigureAwait(false);

        return new OperatorContact
        {
            Name = defaults.GetValueOrDefault(OperatorNameKey),
            Contact = defaults.GetValueOrDefault(OperatorContactKey),
            UpdatedUtc = clock.GetUtcNow(),
        };
    }

    /// <summary>Tells one open connection who to contact (§2.7 item 8).</summary>
    public async Task PushContactAsync(
        AgentConnection connection,
        OperatorContact contact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        try
        {
            await connection.SendAsync(
                    ControlWire.KindOperatorContact,
                    contact,
                    ProtocolJson.Default.OperatorContact,
                    ProtocolConstants.ChannelControl,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is System.Net.WebSockets.WebSocketException
                                              or ObjectDisposedException
                                              or OperationCanceledException)
        {
            logger.ContactPushMissed(exception, connection.DeviceId);
        }
    }

    /// <summary>Sends one device its effective settings, if it is online.</summary>
    public async Task PushAsync(string deviceId, CancellationToken cancellationToken)
    {
        var connection = registry.Find(deviceId);
        if (connection is null)
        {
            return;
        }

        var resolved = await settings.ResolveAsync(deviceId, cancellationToken).ConfigureAwait(false);
        await PushAsync(connection, resolved, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends every online device its effective settings.</summary>
    /// <remarks>Used after a fleet default changes, since that can move any device's values.</remarks>
    public async Task PushAllAsync(CancellationToken cancellationToken)
    {
        // Resolved once for the whole fleet, because it is a fleet value: §3.4 gives every other
        // setting a per-device override and this one deliberately has none, so a hundred frames
        // would otherwise read the same two rows a hundred times.
        var contact = await ResolveContactAsync(cancellationToken).ConfigureAwait(false);

        foreach (var connection in registry.All())
        {
            var resolved = await settings.ResolveAsync(connection.DeviceId, cancellationToken)
                .ConfigureAwait(false);
            await PushAsync(connection, resolved, cancellationToken).ConfigureAwait(false);

            // Sent on every settings change rather than only when the two keys move, for §3.4's
            // own reason: a route that knew one key by name would be the hard-coding "not a fixed
            // list but a generic mechanism" rules out. The agent writes nothing when the value has
            // not changed, so the cost of the unconditional send is one small frame.
            await PushContactAsync(connection, contact, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Sends a already-resolved settings frame on an open connection.</summary>
    public async Task PushAsync(
        AgentConnection connection,
        ResolvedSettings resolved,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(resolved);

        var push = new SettingsPush
        {
            DeviceId = resolved.DeviceId,
            Revision = resolved.Revision,
            Values = resolved.Values,
        };

        try
        {
            await connection.SendAsync(
                    ControlWire.KindSettings,
                    push,
                    ProtocolJson.Default.SettingsPush,
                    ProtocolConstants.ChannelControl,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is System.Net.WebSockets.WebSocketException
                                              or ObjectDisposedException
                                              or OperationCanceledException)
        {
            // The socket died between the lookup and the send. The device will resolve its
            // settings again on reconnect, so there is nothing to retry and nothing lost.
            logger.SettingsPushMissed(exception, resolved.DeviceId);
        }
    }
}
