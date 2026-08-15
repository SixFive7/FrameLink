using FrameLink.Control.Authentication;
using FrameLink.Control.Storage;
using FrameLink.Control.Updates;
using FrameLink.Protocol;

namespace FrameLink.Control.Agent;

/// <summary>
/// The verdict half of the frozen handshake, with no socket in sight.
/// </summary>
/// <remarks>
/// Separated from the transport on purpose. Every rejection this milestone has to get right —
/// bad signature, unconfigured server, blocked device, pending device, version mismatch —
/// is a pure function of a hello, a proof and the stored state, so it is tested as one
/// instead of through a WebSocket.
/// </remarks>
/// <param name="devices">The device table.</param>
/// <param name="credential">The operator password, which decides the outermost rung of §2.6.</param>
/// <param name="releases">The served agent build, carried on every answer.</param>
/// <param name="options">Paths and budgets.</param>
/// <param name="events">
/// Told whenever a proven contact touches a row. This is the moment §3.3 is designed around —
/// a frame plugged in on the bench appearing in the list — and it is <i>not</i> the moment the
/// connection registry sees, because a pending device is answered and closed and never
/// registers at all.
/// </param>
/// <param name="logger">Where refusals are recorded.</param>
public sealed class DeviceHandshake(
    IDeviceStore devices,
    OperatorCredential credential,
    AgentReleaseCatalog releases,
    ControlOptions options,
    FleetEvents events,
    ILogger<DeviceHandshake> logger)
{
    /// <summary>
    /// Verifies the proof and decides what the frame is told.
    /// </summary>
    /// <param name="hello">The agent's unauthenticated opening claim.</param>
    /// <param name="serverNonce">The nonce this server issued for this connection.</param>
    /// <param name="proof">The agent's signature over the challenge bytes.</param>
    /// <param name="remoteAddress">Source address, recorded on a proven contact.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<HandshakeDecision> DecideAsync(
        HandshakeHello hello,
        string serverNonce,
        HandshakeProof proof,
        string? remoteAddress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hello);
        ArgumentNullException.ThrowIfNull(proof);

        // The proof round trip runs before the version check, not after. The handshake
        // envelope is frozen forever precisely so that a hopelessly outdated agent can still
        // complete it, which means identity is provable even when nothing else is compatible
        // — and answering "version-mismatch" to an unproven claim would let anyone rewrite
        // the row of a frame they do not own.
        var proven = DeviceIdentity.VerifyProof(
            hello.PublicKey,
            hello.DeviceId,
            hello.Nonce,
            serverNonce,
            proof.Signature);

        if (!proven)
        {
            logger.ProofRejected(remoteAddress);

            // Nothing is written. An unproven claim must never create or mutate a row, or the
            // open registration path becomes a way to edit somebody else's device.
            return new HandshakeDecision
            {
                Result = new HandshakeResult
                {
                    Status = HandshakeStatus.BadSignature,
                    ProtocolVersion = ProtocolConstants.Version,
                    Message = "The signature did not match the public key presented with it.",
                },
                KeepOpen = false,
            };
        }

        var release = releases.TryGetDefault();

        // The server has no operator, so it can adopt nothing. This is the outermost rung of
        // the device state ladder in §2.6 and therefore outranks every answer below it: the
        // frame renders "connected to a Fleet Manager, but it is not set up yet", and the
        // operator — who is usually the person holding the frame — sees which variable to set.
        if (!credential.IsConfigured)
        {
            // The contact is still recorded. The moment the operator sets the password and
            // reloads, the frame they are holding is already in the adoption queue.
            await devices.RecordContactAsync(
                ContactFrom(hello, remoteAddress),
                options.PendingDeviceCap,
                cancellationToken).ConfigureAwait(false);

            events.Publish(hello.DeviceId);

            return new HandshakeDecision
            {
                Result = Answer(HandshakeStatus.NotConfigured, release, credential.Problem),
                KeepOpen = false,
            };
        }

        var device = await devices.RecordContactAsync(
            ContactFrom(hello, remoteAddress),
            options.PendingDeviceCap,
            cancellationToken).ConfigureAwait(false);

        events.Publish(device.DeviceId);

        if (device.State is DeviceState.Blocked)
        {
            return new HandshakeDecision
            {
                Result = Answer(
                    HandshakeStatus.Blocked,
                    release,
                    "This Fleet Manager has been told to refuse this device."),
                KeepOpen = false,
                Device = device,
            };
        }

        if (device.State is DeviceState.Pending)
        {
            // A pending device receives nothing (§3.3): no name, no configuration, no token,
            // no commands. The one thing it is given is an answer, because §2.6 requires
            // rejection to be legible rather than silent.
            return new HandshakeDecision
            {
                Result = Answer(
                    HandshakeStatus.Pending,
                    release,
                    "This device is healthy and waiting to be adopted in the Fleet Manager."),
                KeepOpen = false,
                Device = device,
            };
        }

        // Strict version matching (§4.2). Affordable because the answer carries the served
        // version and the update URL, so a mismatch triggers an immediate update instead of
        // needing a compatibility dialect. The socket is answered, never dropped.
        if (hello.ProtocolVersion != ProtocolConstants.Version)
        {
            logger.ProtocolMismatch(device.DeviceId, hello.ProtocolVersion, ProtocolConstants.Version);

            return new HandshakeDecision
            {
                Result = Answer(
                    HandshakeStatus.VersionMismatch,
                    release,
                    $"This server speaks protocol version {ProtocolConstants.Version} and the "
                    + $"agent speaks {hello.ProtocolVersion}. Update to the served agent build."),
                KeepOpen = false,
                Device = device,
            };
        }

        return new HandshakeDecision
        {
            Result = Answer(HandshakeStatus.Ok, release, message: null) with
            {
                DeviceName = device.DisplayName,
            },
            KeepOpen = true,
            Device = device,
        };
    }

    private static HandshakeResult Answer(string status, AgentRelease? release, string? message) => new()
    {
        Status = status,
        ProtocolVersion = ProtocolConstants.Version,

        // Carried on every answer except bad-signature, including pending and blocked. The
        // update feed is versionless, open and polled hourly regardless of socket state
        // (§2.8), so withholding it here would buy nothing and would strand a brand-new frame
        // whose agent is too old to be adopted in the first place.
        ServedAgentVersion = release?.Version,
        UpdateUrl = release?.Url,
        Message = message,
    };

    private static DeviceContact ContactFrom(HandshakeHello hello, string? remoteAddress) => new()
    {
        DeviceId = hello.DeviceId,
        PublicKey = hello.PublicKey,
        ProtocolVersion = hello.ProtocolVersion,
        AgentVersion = hello.AgentVersion,
        AgentStatus = hello.AgentStatus,
        HardwareSerial = hello.HardwareSerial,
        RemoteAddress = remoteAddress,
    };
}

/// <summary>What the server answers, and whether the conversation continues.</summary>
public sealed record HandshakeDecision
{
    /// <summary>The frozen result message to send.</summary>
    public required HandshakeResult Result { get; init; }

    /// <summary>
    /// True only for <c>ok</c>.
    /// </summary>
    /// <remarks>
    /// Every other outcome is answered and then closed. An open WebSocket is a real
    /// allocation — a socket, a receive buffer, a registry slot, a ping timer — and §3.3
    /// requires that a pending record allocate nothing, on an endpoint that is deliberately
    /// exposed to the internet. The frame is not left guessing: it has an authoritative
    /// answer to render, and its capped exponential backoff (§4.1) brings it back to learn
    /// about its own adoption.
    /// </remarks>
    public required bool KeepOpen { get; init; }

    /// <summary>The stored row, when the proof succeeded and a row was touched.</summary>
    public DeviceRecord? Device { get; init; }
}
