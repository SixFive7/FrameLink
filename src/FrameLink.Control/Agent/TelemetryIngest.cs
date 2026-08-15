using FrameLink.Control.Storage;
using FrameLink.Protocol;

namespace FrameLink.Control.Agent;

/// <summary>
/// Takes what a frame says on the <c>telemetry</c> and <c>events</c> channels and stores it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The device id comes from the socket, never from the payload.</b> The connection is only
/// created after a handshake that proved a keypair (§3.3), so the id the server binds to is
/// authenticated; the one inside the message is merely claimed. Believing the claim would let
/// any adopted frame write history onto any other, which on an internet-exposed route is the
/// whole game.
/// </para>
/// <para>
/// Nothing here fails a connection. A malformed payload is dropped and logged: §4.2 freezes the
/// envelope precisely so that a newer or damaged peer stays legible, and closing a socket over
/// one unreadable report would take a working frame offline for the duration of a bug.
/// </para>
/// </remarks>
public sealed class TelemetryIngest(
    IFleetTelemetryStore telemetry,
    IPackageStore packages,
    FleetEvents events,
    ILogger<TelemetryIngest> logger)
{
    /// <summary>Handles one inbound message from an authenticated device.</summary>
    public async Task HandleAsync(string deviceId, WireEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (string.Equals(envelope.Kind, ControlWire.KindReconcileReport, StringComparison.Ordinal))
        {
            if (envelope.PayloadAs(ProtocolJson.Default.ReconcileReport) is not { } report)
            {
                logger.UnreadableTelemetry(deviceId, envelope.Kind);
                return;
            }

            await telemetry
                .RecordReportAsync(report with { DeviceId = deviceId }, cancellationToken)
                .ConfigureAwait(false);

            // The console re-reads the device on any change, so one nudge per report is enough
            // to make the live reconciliation screen live without a second serialisation.
            events.Publish(deviceId);
            return;
        }

        if (string.Equals(envelope.Kind, ControlWire.KindPackageInventory, StringComparison.Ordinal))
        {
            if (envelope.PayloadAs(ProtocolJson.Default.PackageInventory) is not { } inventory)
            {
                logger.UnreadableTelemetry(deviceId, envelope.Kind);
                return;
            }

            await packages
                .RecordInventoryAsync(inventory with { DeviceId = deviceId }, cancellationToken)
                .ConfigureAwait(false);

            events.Publish(deviceId);
            return;
        }

        if (string.Equals(envelope.Kind, ControlWire.KindDeviceEvent, StringComparison.Ordinal))
        {
            if (envelope.PayloadAs(ProtocolJson.Default.DeviceEvent) is not { } deviceEvent)
            {
                logger.UnreadableTelemetry(deviceId, envelope.Kind);
                return;
            }

            await telemetry
                .RecordEventAsync(deviceEvent with { DeviceId = deviceId }, cancellationToken)
                .ConfigureAwait(false);

            if (string.Equals(deviceEvent.Kind, DeviceEventKinds.Escalation, StringComparison.Ordinal)
                || string.Equals(deviceEvent.Kind, DeviceEventKinds.Halted, StringComparison.Ordinal)
                || string.Equals(deviceEvent.Kind, DeviceEventKinds.Display, StringComparison.Ordinal))
            {
                // §2.5 rung 3 is the Fleet Manager telling the operator. Home Assistant and SMTP
                // are Mn+2 work; what exists now is the record and the log line, which is what
                // an operator watching the container sees.
                logger.DeviceEscalated(deviceId, deviceEvent.Kind, deviceEvent.Resource, deviceEvent.Summary);
            }

            events.Publish(deviceId);
        }
    }
}
