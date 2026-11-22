using System.Net;
using System.Net.Sockets;

namespace NexNet.InteropServer;

/// <summary>
/// Keeps the ports of a stopped test server bound (not listening) until it starts again, so no server of another test
/// process can listen on them meanwhile and a client's connection attempt is refused (TCP) or unanswered (QUIC). Also
/// gives a test an address with nothing listening on it (no server at all).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="InteropPorts"/> and the integration tests' <c>TestPorts</c> keep ports unique within one process only.
/// Without a hold, a server of another test process can pick a stopped server's port and listen on it, and a
/// "reconnect to the stopped server" attempt reaches that server instead of failing (S50).
/// </para>
/// <para>
/// Each TCP port is bound as the port allocators probe it: exclusively on Windows, where a plain binding does not keep
/// another socket that asks for address reuse off the port. When the exclusive binding is refused (Windows refuses
/// it while a closed connection of the port is in TIME_WAIT), a plain binding holds the port.
/// </para>
/// <para>
/// On Linux and macOS, .NET binds every TCP socket with <c>SO_REUSEADDR</c>, so a TCP socket that does not listen keeps
/// no other socket off its port, not even a listener (H53). The allocators only hand out a port whose TCP and UDP
/// ports are both free, so the hold also binds the UDP port of the same number, which no other socket can share there;
/// a client's TCP connection attempt is still refused, as nothing listens on the port. On Windows the UDP port of a TCP
/// port is taken when it is free, and skipped otherwise: the exclusive TCP binding already keeps the allocators off.
/// </para>
/// <para>
/// A stopped TCP listener frees its port at once, so a TCP port that cannot be held within <see cref="BindTimeout"/>
/// fails the hold with <see cref="InvalidOperationException"/>: another process holds it, and a test relying on the
/// server being unreachable would be silently wrong. A QUIC port is taken in the background, retried until the
/// hold is disposed, because its UDP port may still be bound when the hold is taken: before S51, the QUIC transport
/// left its connections undisposed and msquic kept the port for seconds after a stop. Once a stopped QUIC server frees
/// its port at once, the first attempt takes it.
/// </para>
/// </remarks>
public sealed class StoppedPortHold : IDisposable
{
    /// <summary>How long a TCP port that is taken is retried before the hold fails.</summary>
    internal static readonly TimeSpan BindTimeout = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(20);

    private readonly Lock _lock = new();
    private readonly List<Socket> _sockets = [];
    // Not disposed: the background UDP loop may still read its token after the hold is released.
    private readonly CancellationTokenSource _disposed = new();

    private StoppedPortHold()
    {
    }

    /// <summary>
    /// Binds the TCP and TLS ports of <paramref name="endpoints"/>, starts taking its QUIC port (UDP), and returns the
    /// hold.
    /// </summary>
    /// <exception cref="InvalidOperationException">A TCP port could not be held within <see cref="BindTimeout"/>.</exception>
    public static Task<StoppedPortHold> TakeAsync(InteropEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return TakeAsync([endpoints.Tcp, endpoints.TcpTls], endpoints.Quic is { } quic ? [quic] : []);
    }

    /// <summary>
    /// Binds the TCP ports of <paramref name="tcpEndPoints"/> (TCP, TLS, HTTP), starts taking the QUIC ports of
    /// <paramref name="quicEndPoints"/> (UDP), keeps the allocators off the ports of
    /// <paramref name="refusedQuicEndPoints"/>, and returns the hold.
    /// </summary>
    /// <param name="tcpEndPoints">TCP ports to hold: an attempt is refused.</param>
    /// <param name="quicEndPoints">
    /// UDP ports of a stopped QUIC server to hold: an attempt goes unanswered and lasts the client's connection timeout.
    /// </param>
    /// <param name="refusedQuicEndPoints">
    /// UDP ports left unbound, so an attempt fails at once (port unreachable); the TCP port of the same number listens
    /// without accepting, which keeps the allocators, and other listeners, off the number on every platform. For an
    /// address with no server at all.
    /// </param>
    /// <exception cref="InvalidOperationException">A TCP port could not be held within <see cref="BindTimeout"/>.</exception>
    public static async Task<StoppedPortHold> TakeAsync(
        IEnumerable<IPEndPoint> tcpEndPoints,
        IEnumerable<IPEndPoint> quicEndPoints,
        IEnumerable<IPEndPoint>? refusedQuicEndPoints = null)
    {
        ArgumentNullException.ThrowIfNull(tcpEndPoints);
        ArgumentNullException.ThrowIfNull(quicEndPoints);
        var hold = new StoppedPortHold();
        try
        {
            foreach (var endPoint in tcpEndPoints)
                await hold.BindTcpAsync(endPoint).ConfigureAwait(false);
            foreach (var endPoint in refusedQuicEndPoints ?? [])
                await hold.BindUntilAsync(endPoint, SocketType.Stream, ProtocolType.Tcp, DateTime.UtcNow + BindTimeout, listen: true).ConfigureAwait(false);
        }
        catch
        {
            hold.Dispose();
            throw;
        }

        foreach (var endPoint in quicEndPoints)
            _ = hold.BindUdpInBackgroundAsync(endPoint);

        return hold;
    }

    private async Task BindTcpAsync(IPEndPoint endPoint)
    {
        var deadline = DateTime.UtcNow + BindTimeout;
        await BindUntilAsync(endPoint, SocketType.Stream, ProtocolType.Tcp, deadline).ConfigureAwait(false);

        // The UDP port of the same number: the only binding that keeps the port allocators off it on Linux and macOS.
        if (OperatingSystem.IsWindows())
            TryBind(endPoint, SocketType.Dgram, ProtocolType.Udp, out _);
        else
            await BindUntilAsync(endPoint, SocketType.Dgram, ProtocolType.Udp, deadline).ConfigureAwait(false);
    }

    private async Task BindUntilAsync(IPEndPoint endPoint, SocketType socketType, ProtocolType protocolType, DateTime deadline, bool listen = false)
    {
        SocketException? last;
        while (!TryBind(endPoint, socketType, protocolType, out last, listen))
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException(
                    $"Could not hold {protocolType} port {endPoint} of the stopped test server ({last?.SocketErrorCode}: "
                    + $"{last?.Message}). Another process holds it, so a client could reach that process instead of "
                    + "being refused.",
                    last);
            }

            // Another process may be probing the port (the allocators bind it for a moment); wait for it to let go.
            await Task.Delay(RetryDelay).ConfigureAwait(false);
        }
    }

    private async Task BindUdpInBackgroundAsync(IPEndPoint endPoint)
    {
        try
        {
            while (!TryBind(endPoint, SocketType.Dgram, ProtocolType.Udp, out _))
                await Task.Delay(RetryDelay, _disposed.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Released before the port was free (a QUIC server that kept it after its stop, before S51).
        }
    }

    /// <summary>Binds a socket to <paramref name="endPoint"/> and keeps it; false while the port is taken.</summary>
    private bool TryBind(IPEndPoint endPoint, SocketType socketType, ProtocolType protocolType, out SocketException? error, bool listen = false)
    {
        error = null;
        if (_disposed.IsCancellationRequested)
            return true;

        foreach (var exclusive in (ReadOnlySpan<bool>)[true, false])
        {
            var socket = new Socket(endPoint.AddressFamily, socketType, protocolType);
            try
            {
                if (exclusive && OperatingSystem.IsWindows())
                    socket.ExclusiveAddressUse = true;
                socket.Bind(endPoint);
                if (listen)
                    socket.Listen(1);
            }
            catch (SocketException e)
            {
                socket.Dispose();
                error = e;
                continue;
            }

            lock (_lock)
            {
                if (!_disposed.IsCancellationRequested)
                {
                    _sockets.Add(socket);
                    return true;
                }
            }

            // Released meanwhile: the port must be free for the restart.
            socket.Dispose();
            return true;
        }

        return false;
    }

    /// <summary>Releases every port and stops taking the QUIC port.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed.IsCancellationRequested)
                return;
            _disposed.Cancel();
            foreach (var socket in _sockets)
                socket.Dispose();
            _sockets.Clear();
        }
    }
}
