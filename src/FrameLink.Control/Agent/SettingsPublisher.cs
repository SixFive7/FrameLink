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
    ILogger<SettingsPublisher> logger)
{
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
        foreach (var connection in registry.All())
        {
            var resolved = await settings.ResolveAsync(connection.DeviceId, cancellationToken)
                .ConfigureAwait(false);
            await PushAsync(connection, resolved, cancellationToken).ConfigureAwait(false);
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
                    ControlJson.Default.SettingsPush,
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
