using System.Net;
using System.Net.Sockets;

namespace NexNet.InteropServer;

/// <summary>
/// Keeps the TCP, TLS and QUIC ports of a stopped interop server bound (not listening) until the server starts again,
/// so no other server can listen on them meanwhile and a client's connection attempt is refused (TCP) or unanswered
/// (QUIC).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="InteropPorts"/> keeps ports unique within one process only. Without a hold, a server of another test
/// process can pick a stopped server's port and listen on it, and a "reconnect to the stopped server" attempt reaches
/// that server instead of failing (S50).
/// </para>
/// <para>
/// Each port is bound as the port allocators probe it: exclusively on Windows, where a plain binding does not keep
/// another socket that asks for address reuse off the port. When the exclusive binding is refused (Windows refuses
/// it while a closed connection of the port is in TIME_WAIT), a plain binding holds the port.
/// </para>
/// <para>
/// A stopped TCP listener frees its port at once, so a TCP or TLS port that cannot be bound within
/// <see cref="BindTimeout"/> fails the stop with <see cref="InvalidOperationException"/>: another process holds it,
/// and a test relying on the server being unreachable would be silently wrong. A stopped QUIC listener does not free
/// its UDP port at once: msquic keeps its binding (exclusive) for seconds after the listener stops, often until the
/// process reuses it for the restart. The UDP port is therefore taken in the background, as soon as msquic lets go of
/// it, until the hold is disposed.
/// </para>
/// </remarks>
internal sealed class StoppedPortHold : IDisposable
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
    /// <exception cref="InvalidOperationException">A TCP port could not be bound within <see cref="BindTimeout"/>.</exception>
    public static async Task<StoppedPortHold> TakeAsync(InteropEndpoints endpoints)
    {
        var hold = new StoppedPortHold();
        try
        {
            await hold.BindTcpAsync(endpoints.Tcp).ConfigureAwait(false);
            await hold.BindTcpAsync(endpoints.TcpTls).ConfigureAwait(false);
        }
        catch
        {
            hold.Dispose();
            throw;
        }

        if (endpoints.Quic is { } quic)
            _ = hold.BindUdpInBackgroundAsync(quic);

        return hold;
    }

    private async Task BindTcpAsync(IPEndPoint endPoint)
    {
        var deadline = DateTime.UtcNow + BindTimeout;
        SocketException? last;
        while (!TryBind(endPoint, SocketType.Stream, ProtocolType.Tcp, out last))
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException(
                    $"Could not hold TCP port {endPoint} of the stopped interop server ({last?.SocketErrorCode}: "
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
            // Released before msquic let go of the port.
        }
    }

    /// <summary>Binds a socket to <paramref name="endPoint"/> and keeps it; false while the port is taken.</summary>
    private bool TryBind(IPEndPoint endPoint, SocketType socketType, ProtocolType protocolType, out SocketException? error)
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
