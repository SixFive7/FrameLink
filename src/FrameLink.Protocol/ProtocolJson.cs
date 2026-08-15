using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace FrameLink.Protocol;

/// <summary>
/// Source-generated serialisation for every wire type.
/// </summary>
/// <remarks>
/// <para>
/// Native AOT has no reflection-based serialiser, so this context is not an optimisation —
/// it is the only way these types cross the wire. <c>Directory.Build.props</c> enables the
/// trim and AOT analysers at error severity precisely so that a reflection-based
/// <c>JsonSerializer</c> call fails the build (IL2026/IL3050) instead of failing on a frame.
/// </para>
/// <para>
/// Serialisation options are pinned here rather than passed per call, because the wire
/// format is frozen: camelCase naming and null-omission are part of the contract, not a
/// local formatting preference.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WireEnvelope))]
[JsonSerializable(typeof(HandshakeHello))]
[JsonSerializable(typeof(HandshakeChallenge))]
[JsonSerializable(typeof(HandshakeProof))]
[JsonSerializable(typeof(HandshakeResult))]
[JsonSerializable(typeof(AgentRelease))]
public sealed partial class ProtocolJson : JsonSerializerContext;

/// <summary>Envelope construction and payload extraction, shared by both programs.</summary>
public static class WireMessage
{
    /// <summary>Kind for <see cref="HandshakeHello"/>.</summary>
    public const string KindHello = "hello";

    /// <summary>Kind for <see cref="HandshakeChallenge"/>.</summary>
    public const string KindChallenge = "challenge";

    /// <summary>Kind for <see cref="HandshakeProof"/>.</summary>
    public const string KindProof = "proof";

    /// <summary>Kind for <see cref="HandshakeResult"/>.</summary>
    public const string KindResult = "result";

    /// <summary>Wraps a payload in the frozen envelope and serialises it to UTF-8.</summary>
    public static byte[] Encode<TPayload>(
        string kind,
        TPayload payload,
        JsonTypeInfo<TPayload> payloadTypeInfo,
        string? channel = null,
        string? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(payloadTypeInfo);

        var envelope = new WireEnvelope
        {
            Magic = ProtocolConstants.Magic,
            Kind = kind,
            Channel = channel,
            CorrelationId = correlationId,
            Payload = JsonSerializer.SerializeToElement(payload, payloadTypeInfo),
        };
        return JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJson.Default.WireEnvelope);
    }

    /// <summary>
    /// Parses the frozen envelope, or returns <see langword="null"/> if the bytes are not
    /// FrameLink traffic at all.
    /// </summary>
    /// <remarks>
    /// Returns null rather than throwing for the wrong-endpoint case — connecting to a
    /// captive portal or an unrelated WebSocket service is a configuration mistake to
    /// report on screen, not an exception to propagate through the reconnect loop.
    /// </remarks>
    public static WireEnvelope? Decode(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize(utf8, ProtocolJson.Default.WireEnvelope);
            return envelope is null || !string.Equals(envelope.Magic, ProtocolConstants.Magic, StringComparison.Ordinal)
                ? null
                : envelope;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Deserialises an envelope payload, or <see langword="null"/> if it does not fit.</summary>
    public static TPayload? PayloadAs<TPayload>(this WireEnvelope envelope, JsonTypeInfo<TPayload> typeInfo)
        where TPayload : class
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(typeInfo);

        try
        {
            return envelope.Payload.Deserialize(typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
