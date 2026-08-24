using FrameLink.Agent.Identity;
using FrameLink.Protocol;

namespace FrameLink.Agent.Link;

/// <summary>How a handshake ended.</summary>
public sealed record HandshakeOutcome
{
    /// <summary>The server's verdict, when the exchange completed.</summary>
    public HandshakeResult? Result { get; init; }

    /// <summary>Why the exchange did not complete, when it did not.</summary>
    public string? Failure { get; init; }

    /// <summary>Whether the server answered.</summary>
    public bool Completed => Result is not null;

    /// <summary>A completed exchange.</summary>
    public static HandshakeOutcome From(HandshakeResult result) => new() { Result = result };

    /// <summary>An exchange that never produced a verdict.</summary>
    public static HandshakeOutcome Failed(string reason) => new() { Failure = reason };
}

/// <summary>
/// The frozen handshake of §4.2, run on <b>every</b> connect.
/// </summary>
/// <remarks>
/// <para>
/// Every connect, not just the first, because the answer can change underneath a frame that
/// never moved: an operator presses Adopt, or Block, or reverts the container tag. Re-asking is
/// how the frame finds out.
/// </para>
/// <para>
/// Nothing in here negotiates. §4.2 makes matching strict — a mismatch triggers an immediate
/// update instead of a compatibility shim — so this code speaks exactly one dialect and will
/// never grow a second.
/// </para>
/// </remarks>
public static class HandshakeExchange
{
    /// <summary>
    /// Whether a result is backpressure rather than a verdict about this frame.
    /// </summary>
    /// <remarks>
    /// <c>rate-limited</c> (§3.3) is the one status that says nothing about the device's state:
    /// it is the Fleet Manager asking a frame — usually a perfectly healthy adopted one that has
    /// been restarting — to come back in a minute. Everything that would otherwise treat a result
    /// as authoritative has to ask this first, because letting a throttle become the frame's last
    /// authoritative condition would blank a green screen the next time contact dropped, which is
    /// exactly what §2.6 forbids.
    /// </remarks>
    public static bool IsThrottle(HandshakeResult? result) =>
        string.Equals(result?.Status, HandshakeStatus.RateLimited, StringComparison.Ordinal);

    /// <summary>Runs hello → challenge → proof → result.</summary>
    public static async Task<HandshakeOutcome> PerformAsync(
        IControlTransport transport,
        DeviceKey identity,
        string agentVersion,
        string? hardwareSerial,
        string? agentStatus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(identity);

        var clientNonce = DeviceIdentity.NewNonce();

        var hello = new HandshakeHello
        {
            ProtocolVersion = ProtocolConstants.Version,
            AgentVersion = agentVersion,
            DeviceId = identity.DeviceId,
            PublicKey = identity.PublicKeyBase64,
            Nonce = clientNonce,
            HardwareSerial = hardwareSerial,
            AgentStatus = agentStatus,
        };

        await transport.SendAsync(
            WireMessage.Encode(WireMessage.KindHello, hello, ProtocolJson.Default.HandshakeHello),
            cancellationToken).ConfigureAwait(false);

        var challengeEnvelope = await ReceiveAsync(transport, cancellationToken).ConfigureAwait(false);
        if (challengeEnvelope is null)
        {
            return HandshakeOutcome.Failed("The Fleet Manager closed the connection before answering the hello.");
        }

        // A result arriving in place of a challenge is legitimate and is how a server rejects
        // before spending anything on an unknown device — §3.3's "a pending device receives
        // nothing", and the rate-limited open registration path behind it.
        if (string.Equals(challengeEnvelope.Kind, WireMessage.KindResult, StringComparison.Ordinal))
        {
            return ReadResult(challengeEnvelope);
        }

        if (!string.Equals(challengeEnvelope.Kind, WireMessage.KindChallenge, StringComparison.Ordinal))
        {
            return HandshakeOutcome.Failed($"Expected a challenge but the Fleet Manager sent '{challengeEnvelope.Kind}'.");
        }

        var challenge = challengeEnvelope.PayloadAs(ProtocolJson.Default.HandshakeChallenge);
        if (challenge is null || string.IsNullOrEmpty(challenge.Nonce))
        {
            return HandshakeOutcome.Failed("The Fleet Manager's challenge could not be read.");
        }

        var proof = new HandshakeProof
        {
            Signature = identity.Sign(
                DeviceIdentity.ChallengeBytes(clientNonce, challenge.Nonce, identity.DeviceId)),
        };

        await transport.SendAsync(
            WireMessage.Encode(WireMessage.KindProof, proof, ProtocolJson.Default.HandshakeProof),
            cancellationToken).ConfigureAwait(false);

        var resultEnvelope = await ReceiveAsync(transport, cancellationToken).ConfigureAwait(false);
        if (resultEnvelope is null)
        {
            return HandshakeOutcome.Failed("The Fleet Manager closed the connection before giving a verdict.");
        }

        if (!string.Equals(resultEnvelope.Kind, WireMessage.KindResult, StringComparison.Ordinal))
        {
            return HandshakeOutcome.Failed($"Expected a verdict but the Fleet Manager sent '{resultEnvelope.Kind}'.");
        }

        return ReadResult(resultEnvelope);
    }

    /// <summary>
    /// Turns a result frame into an outcome, and demotes the one status that is not a verdict.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>rate-limited</c> (§3.3) is backpressure, not an answer about this frame: the server is
    /// saying "not this minute", and the frame it says it to is usually a healthy adopted one
    /// that has been restarting. Carrying it through as a verdict would make it the frame's last
    /// authoritative condition, and the next time contact dropped §2.6's rule — a frame that was
    /// fully green when contact was lost keeps showing photos — would compute <i>was not
    /// green</i> and blank a living room because a server asked for a pause.
    /// </para>
    /// <para>
    /// So it is reported as a failed exchange instead, carrying the server's own sentence. That
    /// puts it on the silence rung, which is where "try again shortly, and keep doing whatever
    /// you were told last time" already lives, and it feeds the reconnect backoff so a
    /// well-behaved agent answers a throttle by slowing down.
    /// </para>
    /// </remarks>
    private static HandshakeOutcome ReadResult(WireEnvelope envelope)
    {
        var result = envelope.PayloadAs(ProtocolJson.Default.HandshakeResult);
        if (result is null || string.IsNullOrEmpty(result.Status))
        {
            return HandshakeOutcome.Failed("The Fleet Manager's verdict could not be read.");
        }

        return IsThrottle(result)
            ? HandshakeOutcome.Failed(
                result.Message ?? "The Fleet Manager asked this frame to reconnect less often.")
            : HandshakeOutcome.From(result);
    }

    private static async Task<WireEnvelope?> ReceiveAsync(
        IControlTransport transport,
        CancellationToken cancellationToken)
    {
        var frame = await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        return frame is null ? null : WireMessage.Decode(frame.Value.Span);
    }
}
