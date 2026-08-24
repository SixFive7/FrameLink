using FrameLink.Protocol;

namespace FrameLink.Control.Agent;

/// <summary>
/// Sends §2.5 rung 5's <b>shutdown</b> down the control channel (decision 94).
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own publisher rather than another argument on <see cref="RetryPublisher"/>, because it is
/// not a retry.</b> Nothing about it resets a budget, nothing about it is aimed at a resource, and
/// it is offered against a frame with nothing wrong — an off switch that only worked on broken
/// frames would be no off switch at all. Sharing the retry's method would have meant a parameter
/// that turned every other parameter into a lie.
/// </para>
/// <para>
/// <b>It is the one server-to-agent message whose success is a frame this server can never reach
/// again.</b> Everything else on this socket is answered by the frame's next report; this one is
/// answered by silence, permanently, and the silence is the intended outcome. So there is no
/// confirmation to wait for and nothing to reconcile afterwards, and the honest thing the API can
/// report is whether the bytes left down a live socket.
/// </para>
/// <para>
/// <b>Not queued for an offline frame, and here the reason is sharper than the retry's.</b> A frame
/// with no socket is either already off or has lost its network, and nothing on this side can tell
/// which — so a queued shutdown would either do nothing or switch off a frame that had since come
/// back and been put to use. The operator is told instead.
/// </para>
/// </remarks>
public sealed class ShutdownPublisher(AgentConnectionRegistry registry, ILogger<ShutdownPublisher> logger)
{
    /// <summary>Asks one frame to switch off.</summary>
    /// <param name="deviceId">The device, which the agent checks the message against.</param>
    /// <param name="requestedUtc">When the operator asked.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <remarks>
    /// <b>"Given the agent is connected" is enforced here and nowhere else.</b> The lookup below is
    /// the whole of it: a frame with no socket in the registry cannot be reached, so the operator is
    /// told rather than being left believing a frame is going down.
    /// </remarks>
    public async Task<RetryOutcome> ShutdownAsync(
        string deviceId,
        DateTimeOffset requestedUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        var connection = registry.Find(deviceId);
        if (connection is null)
        {
            return RetryOutcome.Offline;
        }

        var request = new ShutdownRequest
        {
            DeviceId = deviceId,
            RequestedUtc = requestedUtc,
        };

        try
        {
            await connection.SendAsync(
                    ControlWire.KindShutdown,
                    request,
                    ProtocolJson.Default.ShutdownRequest,
                    ProtocolConstants.ChannelControl,
                    cancellationToken)
                .ConfigureAwait(false);

            logger.ShutdownSent(deviceId);
            return RetryOutcome.Sent;
        }
        catch (Exception exception) when (exception is System.Net.WebSockets.WebSocketException
                                              or ObjectDisposedException
                                              or OperationCanceledException)
        {
            // The socket died between the lookup and the send. Reported as offline rather than as
            // sent, because there is no reconnect path that replays this and the operator has to
            // know the frame is still on.
            logger.ShutdownMissed(exception, deviceId);
            return RetryOutcome.Offline;
        }
    }
}
