using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace NexNet.InteropServer;

/// <summary>
/// An HTTP endpoint that breaks the upgrade a NexNet HttpSocket or WebSocket client makes (protocol specification
/// §13.6/§13.7), for the client conformance tests of every language: it answers the upgrade with a status other than
/// <c>101</c>, with a redirect, or accepts a WebSocket and then sends it a text message. It listens on
/// <c>127.0.0.1</c>, plain HTTP only, on a port of its own.
/// </summary>
/// <remarks>
/// Each accepted connection gets the same answer. <see cref="Requests"/> counts the requests that reached the
/// endpoint and <see cref="RedirectTargetConnections"/> the connections the redirect target accepted, so a test can
/// check that a client neither retried nor followed a redirect.
/// </remarks>
public sealed class MisbehavingEndpoint : IAsyncDisposable
{
    /// <summary>The path of the URLs of <see cref="GetAddress"/>.</summary>
    public const string Path = "/misbehaving";

    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly Behavior _behavior;
    private readonly int _status;
    private readonly TcpListener _listener;
    private readonly TcpListener? _redirectTarget;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private readonly Task _redirectLoop;
    private readonly TaskCompletionSource<int?> _closeStatus = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _requests;
    private int _redirectTargetConnections;

    private enum Behavior
    {
        Status,
        Redirect,
        WebSocketTextMessage,
    }

    private MisbehavingEndpoint(Behavior behavior, int status)
    {
        _behavior = behavior;
        _status = status;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        if (behavior == Behavior.Redirect)
        {
            _redirectTarget = new TcpListener(IPAddress.Loopback, 0);
            _redirectTarget.Start();
        }

        _acceptLoop = Task.Run(AcceptLoop);
        _redirectLoop = _redirectTarget == null ? Task.CompletedTask : Task.Run(RedirectTargetLoop);
    }

    /// <summary>
    /// An endpoint that answers every upgrade request with <paramref name="status"/> instead of <c>101</c> and closes
    /// the connection. A status other than 204 and 304 carries a short text body.
    /// </summary>
    /// <param name="status">An HTTP status from 100 to 599, other than 101.</param>
    public static MisbehavingEndpoint AnswerUpgradeWith(int status)
    {
        if (status is < 100 or > 599 or 101)
            throw new ArgumentOutOfRangeException(nameof(status), status, "A status from 100 to 599, other than 101.");
        return new MisbehavingEndpoint(Behavior.Status, status);
    }

    /// <summary>
    /// An endpoint that answers every upgrade request with the redirect <paramref name="status"/>, whose
    /// <c>Location</c> points to a second listener (<see cref="RedirectTargetConnections"/>), and closes the connection.
    /// </summary>
    /// <param name="status">A redirect status: 301, 302, 303, 307 or 308.</param>
    public static MisbehavingEndpoint RedirectUpgrade(int status)
    {
        if (status is not (301 or 302 or 303 or 307 or 308))
            throw new ArgumentOutOfRangeException(nameof(status), status, "A redirect status: 301, 302, 303, 307 or 308.");
        return new MisbehavingEndpoint(Behavior.Redirect, status);
    }

    /// <summary>
    /// An endpoint that accepts a WebSocket upgrade (RFC 6455, no subprotocol, no extension), sends a text message,
    /// then reads what the client sends until its Close frame (<see cref="ClientCloseStatus"/>).
    /// </summary>
    public static MisbehavingEndpoint SendWebSocketTextMessage() => new(Behavior.WebSocketTextMessage, 101);

    /// <summary>Where the endpoint listens.</summary>
    public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;

    /// <summary>How many requests reached the endpoint (request headers read in full).</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>How many connections the target of the redirect accepted; always 0 for other behaviors.</summary>
    public int RedirectTargetConnections => Volatile.Read(ref _redirectTargetConnections);

    /// <summary>
    /// The status code of the Close frame the client sent after the text message, or null when the client closed the
    /// connection without one (or sent one without a status code). Only set by
    /// <see cref="SendWebSocketTextMessage"/>.
    /// </summary>
    public Task<int?> ClientCloseStatus => _closeStatus.Task;

    /// <summary>
    /// The URL of the endpoint for a client of <paramref name="kind"/>: <c>http://</c> for
    /// <see cref="InteropEndpointKind.HttpSocket"/>, <c>ws://</c> for <see cref="InteropEndpointKind.WebSocket"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Another kind: the endpoint speaks plain HTTP only.</exception>
    public string GetAddress(InteropEndpointKind kind) => kind switch
    {
        InteropEndpointKind.HttpSocket => $"http://{EndPoint}{Path}",
        InteropEndpointKind.WebSocket => $"ws://{EndPoint}{Path}",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The endpoint speaks plain HTTP only."),
    };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        _redirectTarget?.Stop();
        try
        {
            await Task.WhenAll(_acceptLoop, _redirectLoop).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Stopped.
        }

        _closeStatus.TrySetResult(null);
        _stop.Dispose();
    }

    private async Task AcceptLoop()
    {
        var connections = new List<Task>();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var socket = await _listener.AcceptSocketAsync(_stop.Token).ConfigureAwait(false);
                connections.Add(Task.Run(() => Serve(socket)));
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
            // Stopped.
        }
        catch (SocketException)
        {
            // Stopped.
        }

        await Task.WhenAll(connections).ConfigureAwait(false);
    }

    private async Task RedirectTargetLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var socket = await _redirectTarget!.AcceptSocketAsync(_stop.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _redirectTargetConnections);
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
            // Stopped.
        }
        catch (SocketException)
        {
            // Stopped.
        }
    }

    private async Task Serve(Socket socket)
    {
        using var _ = socket;
        try
        {
            var (headers, extra) = await ReadRequestHeaders(socket, _stop.Token).ConfigureAwait(false);
            if (headers == null)
                return;

            Interlocked.Increment(ref _requests);
            switch (_behavior)
            {
                case Behavior.Status:
                {
                    var body = _status is 204 or 304 ? "" : "not a nexnet stream";
                    var response = $"HTTP/1.1 {_status} {ReasonPhrase(_status)}\r\n"
                                   + (body.Length == 0 ? "" : $"Content-Type: text/plain\r\nContent-Length: {body.Length}\r\n")
                                   + $"Connection: close\r\n\r\n{body}";
                    await SendAndClose(socket, response).ConfigureAwait(false);
                    break;
                }
                case Behavior.Redirect:
                {
                    var target = (IPEndPoint)_redirectTarget!.LocalEndpoint;
                    var response = $"HTTP/1.1 {_status} {ReasonPhrase(_status)}\r\n"
                                   + $"Location: http://{target}/redirected\r\n"
                                   + "Content-Length: 0\r\nConnection: close\r\n\r\n";
                    await SendAndClose(socket, response).ConfigureAwait(false);
                    break;
                }
                case Behavior.WebSocketTextMessage:
                    await ServeWebSocket(socket, headers, extra).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
            // Stopped.
        }
        catch (SocketException)
        {
            // The client went away.
            _closeStatus.TrySetResult(null);
        }
        catch (IOException)
        {
            _closeStatus.TrySetResult(null);
        }
    }

    private async Task ServeWebSocket(Socket socket, string headers, List<byte> received)
    {
        var key = HeaderValue(headers, "Sec-WebSocket-Key");
        if (key == null)
        {
            await SendAndClose(socket, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
            return;
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
        var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                       + $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await socket.SendAsync(Encoding.ASCII.GetBytes(response), _stop.Token).ConfigureAwait(false);

        // An unmasked text message (a server never masks).
        var text = Encoding.UTF8.GetBytes("hello");
        var frame = new byte[2 + text.Length];
        frame[0] = 0x81;
        frame[1] = (byte)text.Length;
        text.CopyTo(frame, 2);
        await socket.SendAsync(frame, _stop.Token).ConfigureAwait(false);

        // Skip the client's data frames until its Close.
        var buffer = new byte[16 * 1024];
        while (true)
        {
            if (TryReadFrame(received, out var opcode, out var payload))
            {
                if (opcode != 0x8)
                    continue;

                int? status = payload.Length >= 2 ? (payload[0] << 8) | payload[1] : null;
                _closeStatus.TrySetResult(status);

                // Answer the Close, then end the connection.
                var close = new byte[2 + Math.Min(payload.Length, 2)];
                close[0] = 0x88;
                close[1] = (byte)(close.Length - 2);
                payload.AsSpan(0, close.Length - 2).CopyTo(close.AsSpan(2));

                // RFC 6455 §7.1.1: the server closes the TCP connection first.
                await socket.SendAsync(close, _stop.Token).ConfigureAwait(false);
                socket.Shutdown(SocketShutdown.Both);
                return;
            }

            var count = await socket.ReceiveAsync(buffer, _stop.Token).ConfigureAwait(false);
            if (count == 0)
            {
                _closeStatus.TrySetResult(null);
                return;
            }

            received.AddRange(buffer.AsSpan(0, count));
        }
    }

    /// <summary>Removes one complete client frame from the start of <paramref name="data"/> and unmasks it.</summary>
    private static bool TryReadFrame(List<byte> data, out int opcode, out byte[] payload)
    {
        opcode = 0;
        payload = [];
        if (data.Count < 2)
            return false;

        var masked = (data[1] & 0x80) != 0;
        long length = data[1] & 0x7F;
        var offset = 2;
        if (length == 126)
        {
            if (data.Count < 4)
                return false;
            length = (data[2] << 8) | data[3];
            offset = 4;
        }
        else if (length == 127)
        {
            if (data.Count < 10)
                return false;
            length = 0;
            for (var i = 0; i < 8; i++)
                length = (length << 8) | data[2 + i];
            offset = 10;
        }

        var maskOffset = offset;
        if (masked)
            offset += 4;
        if (data.Count < offset + length)
            return false;

        opcode = data[0] & 0x0F;
        payload = new byte[length];
        for (var i = 0; i < length; i++)
        {
            var b = data[offset + i];
            payload[i] = masked ? (byte)(b ^ data[maskOffset + (i % 4)]) : b;
        }

        data.RemoveRange(0, offset + (int)length);
        return true;
    }

    private static async Task<(string? Headers, List<byte> Extra)> ReadRequestHeaders(Socket socket, CancellationToken cancellationToken)
    {
        var received = new List<byte>();
        var buffer = new byte[4096];
        while (true)
        {
            var text = Encoding.ASCII.GetString(received.ToArray());
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0)
            {
                received.RemoveRange(0, end + 4);
                return (text[..end], received);
            }

            var count = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                return (null, received);
            received.AddRange(buffer.AsSpan(0, count));
        }
    }

    private static string? HeaderValue(string headers, string name)
    {
        foreach (var line in headers.Split("\r\n"))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line.AsSpan(0, colon).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }

        return null;
    }

    private Task SendAndClose(Socket socket, string response) => SendAndClose(socket, Encoding.ASCII.GetBytes(response));

    private async Task SendAndClose(Socket socket, byte[] data)
    {
        await socket.SendAsync(data, _stop.Token).ConfigureAwait(false);
        socket.Shutdown(SocketShutdown.Send);

        // Let the client read the answer and close first, so it sees the answer rather than a reset.
        var buffer = new byte[1024];
        using var linger = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        linger.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (await socket.ReceiveAsync(buffer, linger.Token).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (OperationCanceledException)
        {
            // The client kept the connection open; close it.
        }
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        301 => "Moved Permanently",
        302 => "Found",
        303 => "See Other",
        307 => "Temporary Redirect",
        308 => "Permanent Redirect",
        _ => "Status",
    };
}
