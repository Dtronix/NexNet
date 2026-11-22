using System.Net;
using System.Net.Sockets;

namespace NexNet.InteropServer;

/// <summary>
/// A TCP proxy in front of one endpoint of an interop server that can stall mid-transfer: once armed
/// (<see cref="StallToClientAfter"/>), it forwards a set number of further bytes from the server to the client and
/// then forwards nothing more that way, while it keeps reading (and dropping) what the server sends and keeps
/// forwarding what the client sends. The client sees a server that stopped sending in the middle of whatever was in
/// flight, the case its progress timeout (<c>ProgressTimeout</c>, protocol specification §9) detects.
/// </summary>
/// <remarks>
/// It relays bytes and knows nothing of the protocol above TCP, so it fronts the plain endpoints (TCP, HttpSocket,
/// WebSocket) alike; the client connects to <see cref="Address"/>. It listens on <c>127.0.0.1</c>, on a port of its
/// own, and the limit applies to every connection together.
/// </remarks>
public sealed class StallingProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly IPEndPoint _target;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private readonly TaskCompletionSource _stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly List<Socket> _sockets = [];

    // Bytes still forwarded to the client; -1 forwards everything.
    private long _remaining = -1;

    private StallingProxy(IPEndPoint target, string address)
    {
        _target = target;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Address = Rewrite(address, (IPEndPoint)_listener.LocalEndpoint);
        _acceptLoop = Task.Run(AcceptLoop);
    }

    /// <summary>
    /// Starts a proxy in front of the <paramref name="kind"/> endpoint of <paramref name="endpoints"/>: TCP, HttpSocket
    /// or WebSocket.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Another kind.</exception>
    public static StallingProxy Start(InteropEndpoints endpoints, InteropEndpointKind kind)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var target = kind switch
        {
            InteropEndpointKind.Tcp => endpoints.Tcp,
            InteropEndpointKind.HttpSocket => ToEndPoint(endpoints.HttpSocket),
            InteropEndpointKind.WebSocket => ToEndPoint(endpoints.WebSocket),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The proxy fronts the TCP, HttpSocket and WebSocket endpoints."),
        };
        return new StallingProxy(target, endpoints.GetAddress(kind));
    }

    /// <summary>
    /// The address a client connects to, in the form of <see cref="InteropEndpoints.GetAddress"/>: the endpoint's
    /// address with the proxy's port.
    /// </summary>
    public string Address { get; }

    /// <summary>Completes once the proxy stopped forwarding to the client (<see cref="StallToClientAfter"/>).</summary>
    public Task Stalled => _stalled.Task;

    /// <summary>
    /// Forwards at most <paramref name="bytes"/> more bytes from the server to the client, then stops forwarding that
    /// way for good. 0 stalls at once.
    /// </summary>
    public void StallToClientAfter(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        Interlocked.Exchange(ref _remaining, bytes);
        if (bytes == 0)
            _stalled.TrySetResult();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        lock (_gate)
        {
            foreach (var socket in _sockets)
                socket.Dispose();
            _sockets.Clear();
        }

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Stopped.
        }

        _stop.Dispose();
    }

    private async Task AcceptLoop()
    {
        var connections = new List<Task>();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptSocketAsync(_stop.Token).ConfigureAwait(false);
                connections.Add(Task.Run(() => Relay(client)));
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

    private async Task Relay(Socket client)
    {
        var server = new Socket(_target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        client.NoDelay = true;
        lock (_gate)
        {
            _sockets.Add(client);
            _sockets.Add(server);
        }

        try
        {
            await server.ConnectAsync(_target, _stop.Token).ConfigureAwait(false);
            var toServer = Pump(client, server, limited: false);
            var toClient = Pump(server, client, limited: true);
            await Task.WhenAny(toServer, toClient).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The connection ended.
        }
        finally
        {
            lock (_gate)
            {
                _sockets.Remove(client);
                _sockets.Remove(server);
            }

            client.Dispose();
            server.Dispose();
        }
    }

    private async Task Pump(Socket from, Socket to, bool limited)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var count = await from.ReceiveAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    // Pass the end of the stream on, unless the client side is stalled.
                    if (!limited || Interlocked.Read(ref _remaining) != 0)
                        to.Shutdown(SocketShutdown.Send);
                    return;
                }

                var forward = limited ? Take(count) : count;
                if (forward > 0)
                    await to.SendAsync(buffer.AsMemory(0, forward), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Either side closed.
        }
    }

    /// <summary>How many of <paramref name="count"/> bytes from the server may still go to the client.</summary>
    private int Take(int count)
    {
        while (true)
        {
            var remaining = Interlocked.Read(ref _remaining);
            if (remaining < 0)
                return count;

            var take = (int)Math.Min(remaining, count);
            if (Interlocked.CompareExchange(ref _remaining, remaining - take, remaining) != remaining)
                continue;

            if (remaining - take == 0)
                _stalled.TrySetResult();
            return take;
        }
    }

    private static IPEndPoint ToEndPoint(Uri url) => new(IPAddress.Parse(url.Host), url.Port);

    private static string Rewrite(string address, IPEndPoint proxy)
    {
        if (address.StartsWith("tcp://", StringComparison.Ordinal))
            return "tcp://" + proxy;

        var builder = new UriBuilder(address) { Host = proxy.Address.ToString(), Port = proxy.Port };
        return builder.Uri.ToString();
    }
}
