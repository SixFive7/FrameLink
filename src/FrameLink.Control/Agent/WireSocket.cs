using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json.Serialization.Metadata;
using FrameLink.Protocol;

namespace FrameLink.Control.Agent;

/// <summary>Reading and writing frozen envelopes over a WebSocket.</summary>
/// <remarks>
/// A static helper rather than a method on the connection, because the handshake happens
/// before there is a connection object to hang it on.
/// </remarks>
public static class WireSocket
{
    /// <summary>
    /// Reads one complete WebSocket message.
    /// </summary>
    /// <returns>
    /// The decoded envelope, or null if the peer closed, sent something that is not FrameLink
    /// traffic, or exceeded <paramref name="maxBytes"/>.
    /// </returns>
    /// <remarks>
    /// A WebSocket message can arrive in any number of fragments, so the caller cannot assume
    /// one receive is one message. The size ceiling matters on a route that is open to the
    /// internet: without it a peer can make the server allocate as much as it likes by never
    /// ending a message.
    /// </remarks>
    public static async Task<WireEnvelope?> ReceiveAsync(
        WebSocket socket,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);

        var rented = ArrayPool<byte>.Shared.Rent(8 * 1024);
        var assembled = new ArrayBufferWriter<byte>(8 * 1024);
        try
        {
            while (true)
            {
                var received = await socket
                    .ReceiveAsync(new ArraySegment<byte>(rented), cancellationToken)
                    .ConfigureAwait(false);

                if (received.MessageType is WebSocketMessageType.Close)
                {
                    return null;
                }

                if (assembled.WrittenCount + received.Count > maxBytes)
                {
                    return null;
                }

                assembled.Write(rented.AsSpan(0, received.Count));

                if (received.EndOfMessage)
                {
                    return WireMessage.Decode(assembled.WrittenSpan);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Wraps a payload in the frozen envelope and sends it as one text message.</summary>
    public static Task SendAsync<TPayload>(
        WebSocket socket,
        string kind,
        TPayload payload,
        JsonTypeInfo<TPayload> payloadTypeInfo,
        string? channel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);

        var bytes = WireMessage.Encode(kind, payload, payloadTypeInfo, channel);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }
}
