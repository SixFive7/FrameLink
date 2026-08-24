using FrameLink.Protocol;

namespace FrameLink.Control.Agent;

/// <summary>Why a retry did or did not reach a frame.</summary>
public enum RetryOutcome
{
    /// <summary>The command went down a live socket.</summary>
    Sent,

    /// <summary>There is no device with that id.</summary>
    NoSuchDevice,

    /// <summary>The device exists and is not holding a socket, so nothing could be delivered.</summary>
    Offline,
}

/// <summary>
/// Sends §2.5 rung 3's <b>retry</b> down the control channel.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one server-to-agent message that is not an optimisation.</b> A settings push may be
/// missed with no consequence, because the frame resolves its settings again on the next connect —
/// so <see cref="SettingsPublisher"/> swallows a dead socket and says nothing. A retry has no such
/// fallback: nothing about reconnecting clears an attempt budget, and there is nowhere for an
/// undelivered one to be picked up from later. So this reports whether it landed, and the route
/// above it turns "it did not" into a status code rather than into a 200 with a sad field.
/// </para>
/// <para>
/// <b>It is not queued for an offline frame, deliberately.</b> The operator is pressing this while
/// looking at a device row, at a specific escalation, with a delta in front of them. Delivering
/// that decision hours later to a frame whose situation has moved on is worse than refusing it now
/// — and refusing is honest, because §3.5 makes presence the socket, so "offline" is a fact the
/// screen is already showing rather than something this has to guess.
/// </para>
/// </remarks>
public sealed class RetryPublisher(AgentConnectionRegistry registry, ILogger<RetryPublisher> logger)
{
    /// <summary>Asks one frame to try again.</summary>
    /// <param name="deviceId">The device, which the agent checks the message against.</param>
    /// <param name="resource">
    /// The resource whose budget to reset, or null for every resource that has given up.
    /// </param>
    /// <param name="requestedUtc">When the operator asked.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="reboot">
    /// Whether the frame should restart once the budget is reset — the remote half of the stopped
    /// screen's second button.
    /// </param>
    /// <remarks>
    /// <b>"Given the agent is connected" is enforced here and nowhere else.</b> The lookup below is
    /// the whole of it: a frame with no socket in the registry cannot be reached, so the operator
    /// is told rather than being left believing a frame is restarting. There is no queue and no
    /// replay — the same reasoning as the plain retry, and sharper, because a restart delivered
    /// hours later would take the frame's photographs away for a minute for a decision nobody
    /// remembers making.
    /// </remarks>
    public async Task<RetryOutcome> RetryAsync(
        string deviceId,
        string? resource,
        DateTimeOffset requestedUtc,
        CancellationToken cancellationToken,
        bool reboot = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        var connection = registry.Find(deviceId);
        if (connection is null)
        {
            return RetryOutcome.Offline;
        }

        var request = new RetryRequest
        {
            DeviceId = deviceId,

            // Whitespace becomes absence here rather than travelling as a resource name no
            // catalog has. The device-wide form is the more destructive of the two, so the
            // normalisation is deliberately in the direction the operator's own URL implies: a
            // route with no resource segment means all of them.
            Resource = string.IsNullOrWhiteSpace(resource) ? null : resource.Trim(),
            RequestedUtc = requestedUtc,
            Reboot = reboot,
        };

        try
        {
            await connection.SendAsync(
                    ControlWire.KindRetry,
                    request,
                    ProtocolJson.Default.RetryRequest,
                    ProtocolConstants.ChannelControl,
                    cancellationToken)
                .ConfigureAwait(false);

            logger.RetrySent(
                deviceId,
                (request.Resource ?? "everything that gave up") + (reboot ? ", and to restart" : string.Empty));
            return RetryOutcome.Sent;
        }
        catch (Exception exception) when (exception is System.Net.WebSockets.WebSocketException
                                              or ObjectDisposedException
                                              or OperationCanceledException)
        {
            // The socket died between the lookup and the send. Reported as offline rather than
            // as sent, because the operator has to know to press it again — there is no reconnect
            // path that replays this.
            logger.RetryMissed(exception, deviceId);
            return RetryOutcome.Offline;
        }
    }
}
