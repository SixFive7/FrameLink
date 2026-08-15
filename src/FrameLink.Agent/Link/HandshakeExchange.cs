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

    private static HandshakeOutcome ReadResult(WireEnvelope envelope)
    {
        var result = envelope.PayloadAs(ProtocolJson.Default.HandshakeResult);
        return result is null || string.IsNullOrEmpty(result.Status)
            ? HandshakeOutcome.Failed("The Fleet Manager's verdict could not be read.")
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
