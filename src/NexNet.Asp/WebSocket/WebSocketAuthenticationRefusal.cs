using System;
using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using NexNet.Logging;
using NexNet.Messages;

namespace NexNet.Asp.WebSocket;

/// <summary>
/// Refuses a WebSocket request that failed ASP.NET authentication inside the NexNet protocol: the upgrade is accepted,
/// the server sends its preamble, reads the client's greeting and answers it with the disconnect byte
/// <see cref="DisconnectReason.Authentication"/>, then closes the WebSocket (protocol specification §5.1, §5.6).
/// </summary>
/// <remarks>
/// A refused upgrade (HTTP 401) would be invisible to a browser, whose <c>WebSocket</c> hides the status of a failed
/// handshake and reports every connect failure the same way. Refusing over the open socket gives every client, browsers
/// included, the same <c>AuthenticationError</c> as a greeting token refused by <c>OnAuthenticate</c>. No session is
/// created and the nexus is never involved.
/// </remarks>
internal static class WebSocketAuthenticationRefusal
{
    /// <summary>Length of the connection preamble (protocol specification §1).</summary>
    private const int PreambleLength = 8;

    /// <summary>Type byte plus the little-endian body length (protocol specification §2).</summary>
    private const int GreetingHeaderLength = 3;

    /// <summary>Largest body accepted before the connection completes (protocol specification §2).</summary>
    private const int MaxPreAuthBodyLength = 16 * 1024;

    private const byte ClientGreeting = 100;
    private const byte ClientGreetingReconnection = 101;
    private const byte FirstDisconnect = 20;
    private const byte LastDisconnect = 38;

    /// <summary>
    /// The server preamble: <c>NnP</c>, DC4, three reserved bytes and protocol version 1, as a session sends it.
    /// </summary>
    private static readonly byte[] Preamble = [(byte)'N', (byte)'n', (byte)'P', 0x14, 0, 0, 0, 1];

    private static readonly byte[] Disconnect = [(byte)DisconnectReason.Authentication];

    /// <summary>
    /// Accepts the WebSocket of <paramref name="context"/> and refuses the NexNet session on it with
    /// <see cref="DisconnectReason.Authentication"/>. Returns once the WebSocket is closed.
    /// </summary>
    /// <param name="context">The upgrade request that failed authentication.</param>
    /// <param name="config">Endpoint configuration: its <see cref="NexNet.Transports.ConfigBase.HandshakeTimeout"/>
    /// bounds the wait for the client's greeting and for the close handshake, and its
    /// <see cref="NexNet.Transports.ConfigBase.DisconnectDelay"/> is waited after the disconnect byte, as a session
    /// does.</param>
    /// <param name="cancellationToken">Canceled when the request is aborted or the application stops.</param>
    public static async Task RefuseAsync(HttpContext context, WebSocketServerConfig config, CancellationToken cancellationToken)
    {
        using var websocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        try
        {
            bool refuse;
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                // As a session does, the preamble goes out at once and the greeting is answered.
                handshake.CancelAfter(config.HandshakeTimeout);
                await websocket.SendAsync(Preamble, WebSocketMessageType.Binary, endOfMessage: true, handshake.Token)
                    .ConfigureAwait(false);
                refuse = await ReadGreetingAsync(websocket, handshake.Token).ConfigureAwait(false);
            }

            if (!refuse)
            {
                // The client went away, disconnected itself or broke the WebSocket binding.
                websocket.Abort();
                return;
            }

            await websocket.SendAsync(Disconnect, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
            config.Logger?.LogDebug("Refused the WebSocket session with DisconnectReason.Authentication: ASP.NET authentication failed.");

            if (config.DisconnectDelay > 0)
                await Task.Delay(config.DisconnectDelay, cancellationToken).ConfigureAwait(false);

            using var close = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            close.CancelAfter(config.HandshakeTimeout);
            if (websocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, close.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException)
        {
            // Handshake timeout, request aborted, or the client closed the connection first.
            websocket.Abort();
        }
    }

    /// <summary>
    /// Reads the client's preamble and greeting frame. Returns false when the WebSocket closes or carries a text message
    /// first, or when the client sends a disconnect byte instead of a greeting; true otherwise, including for a frame
    /// that is not a greeting (whose remaining bytes are not read).
    /// </summary>
    private static async Task<bool> ReadGreetingAsync(System.Net.WebSockets.WebSocket websocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[PreambleLength + GreetingHeaderLength + MaxPreAuthBodyLength];
        var length = 0;
        var needed = PreambleLength + GreetingHeaderLength;
        var bodyLengthRead = false;

        while (length < needed)
        {
            var result = await websocket.ReceiveAsync(buffer.AsMemory(length, needed - length), cancellationToken)
                .ConfigureAwait(false);
            if (result.MessageType != WebSocketMessageType.Binary)
                return false;

            length += result.Count;
            if (length <= PreambleLength)
                continue;

            var type = buffer[PreambleLength];
            if (type is >= FirstDisconnect and <= LastDisconnect)
                return false;

            // Only a greeting is read to its end.
            if (type is not (ClientGreeting or ClientGreetingReconnection))
                return true;

            if (!bodyLengthRead && length >= PreambleLength + GreetingHeaderLength)
            {
                bodyLengthRead = true;
                var bodyLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(PreambleLength + 1));
                if (bodyLength > MaxPreAuthBodyLength)
                    return true;
                needed += bodyLength;
            }
        }

        return true;
    }
}
