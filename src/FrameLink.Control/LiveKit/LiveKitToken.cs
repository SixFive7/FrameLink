using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FrameLink.Control.Storage;

namespace FrameLink.Control.LiveKit;

/// <summary>The one grant a frame is ever given: join this room, publish, subscribe.</summary>
/// <remarks>
/// <para>
/// Deliberately the narrowest thing that lets a frame be in a call. There is no
/// <c>roomCreate</c>, no <c>roomList</c>, no <c>roomAdmin</c>, no <c>roomRecord</c> and no
/// <c>ingressAdmin</c>: a leaked frame token joins one room and can do nothing to the server.
/// v1's token had the same shape by accident of the CLI's <c>--join</c> default; here it is the
/// shape because it is written down.
/// </para>
/// <para>
/// Every property name is spelled as LiveKit's own Go <c>VideoGrant</c> spells it. They were not
/// guessed: a token in exactly this shape was minted and offered to a running
/// <c>livekit-server</c> 1.13.5, whose <c>/rtc/validate</c> answered <c>200 success</c>.
/// </para>
/// </remarks>
public sealed record LiveKitVideoGrant
{
    /// <summary>Permission to join a room at all.</summary>
    [JsonPropertyName("roomJoin")]
    public bool RoomJoin { get; init; } = true;

    /// <summary>The one room this token is good for.</summary>
    [JsonPropertyName("room")]
    public required string Room { get; init; }

    /// <summary>Whether the frame may send its camera and microphone.</summary>
    [JsonPropertyName("canPublish")]
    public bool CanPublish { get; init; } = true;

    /// <summary>Whether the frame may receive other participants.</summary>
    [JsonPropertyName("canSubscribe")]
    public bool CanSubscribe { get; init; } = true;

    /// <summary>Whether the frame may use the data channel.</summary>
    [JsonPropertyName("canPublishData")]
    public bool CanPublishData { get; init; } = true;
}

/// <summary>The JWT body LiveKit verifies against the API secret.</summary>
public sealed record LiveKitClaims
{
    /// <summary>Issuer — the API key, which is how LiveKit knows which secret to check.</summary>
    [JsonPropertyName("iss")]
    public required string Issuer { get; init; }

    /// <summary>Subject — the participant identity, unique within the room.</summary>
    [JsonPropertyName("sub")]
    public required string Subject { get; init; }

    /// <summary>The display name other participants see. Absent when the frame has no name yet.</summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    /// <summary>Not valid before, in seconds since the epoch.</summary>
    [JsonPropertyName("nbf")]
    public required long NotBefore { get; init; }

    /// <summary>Expiry, in seconds since the epoch. The claim the July-23 incident turned on.</summary>
    [JsonPropertyName("exp")]
    public required long Expires { get; init; }

    /// <summary>JWT id. The identity, matching what upstream's own minter does.</summary>
    [JsonPropertyName("jti")]
    public required string TokenId { get; init; }

    /// <summary>The grant.</summary>
    [JsonPropertyName("video")]
    public required LiveKitVideoGrant Video { get; init; }
}

/// <summary>What an existing token claims, as read back without verifying it.</summary>
public sealed record LiveKitTokenFacts
{
    /// <summary>The API key it was signed for, from <c>iss</c>.</summary>
    public string? Issuer { get; init; }

    /// <summary>The participant identity, from <c>sub</c>.</summary>
    public string? Identity { get; init; }

    /// <summary>The display name other participants see, or null when the token carries none.</summary>
    public string? Name { get; init; }

    /// <summary>The room the grant names.</summary>
    public string? Room { get; init; }

    /// <summary>When it stops being accepted.</summary>
    public DateTimeOffset? Expires { get; init; }
}

/// <summary>The JOSE header. Fixed — HS256 is the only algorithm LiveKit accepts.</summary>
public sealed record LiveKitJoseHeader
{
    /// <summary>Signing algorithm.</summary>
    [JsonPropertyName("alg")]
    public string Algorithm { get; init; } = "HS256";

    /// <summary>Token type.</summary>
    [JsonPropertyName("typ")]
    public string Type { get; init; } = "JWT";
}

/// <summary>Source-generated serialisation for the two halves of a call token.</summary>
/// <remarks>
/// Its own context rather than a few more entries on <c>ControlJson</c>, which is documented as
/// what the operator's <i>browser</i> exchanges with this server. A JWT body is neither that nor
/// the frozen agent protocol: it is a document a third-party Go server parses, and its field
/// names are that server's contract rather than this project's.
/// </remarks>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LiveKitJoseHeader))]
[JsonSerializable(typeof(LiveKitClaims))]
public sealed partial class LiveKitJson : JsonSerializerContext;

/// <summary>
/// Mints the call tokens §3.7 makes "artifacts the Fleet Manager mints at adoption and can
/// rotate at will".
/// </summary>
/// <remarks>
/// <para>
/// <b>Sixty lines and no dependency, which is the right size for what this is.</b> A LiveKit
/// access token is an HS256 JWT with six registered claims and one custom object; the whole of
/// the work is two base64url-encoded JSON documents and one HMAC. Taking a JWT library for it
/// would add a package to a Native AOT binary, a reflection-based serialiser to argue with, and
/// a §7.1 ledger entry, in exchange for an API over <c>HMACSHA256.HashData</c>.
/// </para>
/// <para>
/// <b>This is the class that retires the credential-expiry failure class.</b> Not by making
/// tokens longer — v1 already tried that, and a ten-year token is a longer fuse rather than a
/// fix — but by making minting free. The secret is here, every adopted frame is reachable, and
/// <see cref="NeedsRenewal"/> is the whole renewal policy: a token past its renewal threshold is
/// replaced on the frame's next contact, unattended, for as long as the fleet exists.
/// </para>
/// <para>
/// <b>Nothing here verifies a token, and that is correct.</b> Verification belongs to the server
/// that holds the secret and enforces the grant, which is LiveKit. What this project does with
/// somebody else's token is read the <c>exp</c> claim without trusting it — see
/// <c>JwtExpiry</c> on the agent side, which turns a silent future failure into visible drift.
/// </para>
/// </remarks>
public static class LiveKitToken
{
    /// <summary>Mints a join token for one frame.</summary>
    /// <param name="credential">The key and secret every token in this fleet is signed with.</param>
    /// <param name="identity">The participant identity. Must be unique in the room.</param>
    /// <param name="room">The room the token is good for.</param>
    /// <param name="displayName">What other participants see, or null.</param>
    /// <param name="issuedAt">Now, as the caller's clock reports it.</param>
    /// <param name="lifetime">How long the token is valid for.</param>
    public static string Mint(
        LiveKitCredential credential,
        string identity,
        string room,
        string? displayName,
        DateTimeOffset issuedAt,
        TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(room);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        var claims = new LiveKitClaims
        {
            Issuer = credential.Key,
            Subject = identity,
            Name = NormaliseName(displayName),

            // A minute of backdating. Two machines whose clocks differ by seconds is ordinary,
            // and a token that is not yet valid is refused exactly as an expired one is — with
            // the added cruelty that it starts working later, which is the hardest possible
            // fault to diagnose from a frame on somebody's wall.
            NotBefore = (issuedAt - TimeSpan.FromMinutes(1)).ToUnixTimeSeconds(),
            Expires = (issuedAt + lifetime).ToUnixTimeSeconds(),
            TokenId = identity,
            Video = new LiveKitVideoGrant { Room = room },
        };

        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(
            new LiveKitJoseHeader(),
            LiveKitJson.Default.LiveKitJoseHeader));

        var body = Encode(JsonSerializer.SerializeToUtf8Bytes(claims, LiveKitJson.Default.LiveKitClaims));

        var signingInput = Encoding.ASCII.GetBytes(header + "." + body);
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(credential.Secret), signingInput);

        return header + "." + body + "." + Encode(signature);
    }

    /// <summary>
    /// Whether a token is close enough to expiry to be worth replacing.
    /// </summary>
    /// <remarks>
    /// True for an absent token, an unreadable one and an already-expired one, all of which are
    /// answered the same way — mint a new one. Being wrong in that direction costs one signature;
    /// being wrong in the other direction is the July-23 incident.
    /// </remarks>
    /// <param name="token">The token the frame currently holds, if any.</param>
    /// <param name="now">The current time.</param>
    /// <param name="threshold">Remaining life below which a token is renewed.</param>
    public static bool NeedsRenewal(string? token, DateTimeOffset now, TimeSpan threshold) =>
        ExpiryOf(token) is not { } expiry || expiry - now <= threshold;

    /// <summary>The <c>exp</c> claim of a token, or null when there is not one to read.</summary>
    public static DateTimeOffset? ExpiryOf(string? token) => Inspect(token)?.Expires;

    /// <summary>
    /// A display name as it appears in a token, so that minting and comparing agree.
    /// </summary>
    /// <remarks>
    /// Blank and absent are the same name, and a token omits the claim entirely for both. Without
    /// one shared rule, an operator adopting a frame with a trailing space in its name would have
    /// it re-minted on every review forever, each one reporting that the name had changed.
    /// </remarks>
    public static string? NormaliseName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    /// <summary>
    /// Reads what a token claims, without checking whether any of it is true.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not a validation. The signature is not checked and could not usefully be:
    /// the question being asked is "should this be replaced", and the four answers that matter —
    /// unreadable, wrong issuer, wrong identity or room, near expiry — are all reasons to mint a
    /// new one whatever the signature says. Verification belongs to the server that enforces the
    /// grant, which is LiveKit.
    /// </para>
    /// <para>
    /// The agent reads the same <c>exp</c> claim the same way in <c>JwtExpiry</c>, and the
    /// duplication is on purpose: they run in separate programs, one of which must keep working
    /// when the other is unreachable, so neither trusts the other's reading.
    /// </para>
    /// </remarks>
    public static LiveKitTokenFacts? Inspect(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        byte[] decoded;
        try
        {
            decoded = Decode(parts[1]);
        }
        catch (FormatException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(decoded);
            var root = document.RootElement;

            if (root.ValueKind is not JsonValueKind.Object)
            {
                return null;
            }

            return new LiveKitTokenFacts
            {
                Issuer = Text(root, "iss"),
                Identity = Text(root, "sub"),
                Name = Text(root, "name"),
                Room = root.TryGetProperty("video", out var video) && video.ValueKind is JsonValueKind.Object
                    ? Text(video, "room")
                    : null,
                Expires = root.TryGetProperty("exp", out var expiry) && expiry.TryGetInt64(out var seconds)
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                    : null,
            };
        }
        catch (JsonException)
        {
            return null;
        }

        static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
                ? value.GetString()
                : null;
    }

    /// <summary>Base64url without padding, as every JWT segment is encoded.</summary>
    private static string Encode(ReadOnlySpan<byte> value) => Base64Url.EncodeToString(value);

    private static byte[] Decode(string segment) => Base64Url.DecodeFromChars(segment);
}
