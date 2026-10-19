using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NexNetBenchmarks;

/// <summary>
/// Loopback TCP forwarder that delays every received segment by a fixed time in each direction, without limiting
/// bandwidth. Models a link with a round-trip time of twice the delay.
/// </summary>
/// <remarks>
/// Each direction has a sender thread that spins until a segment is due: OS timers on Windows have a resolution of
/// about 15 ms, too coarse for the delays used here. It only spins while segments are in flight.
/// </remarks>
internal sealed class LatencyProxy : IDisposable
{
    private readonly TcpListener _listener;
    private readonly IPEndPoint _target;
    private readonly long _delayTicks;
    private readonly CancellationTokenSource _cts = new();

    public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;

    private LatencyProxy(IPEndPoint target, TimeSpan oneWayDelay)
    {
        _target = target;
        _delayTicks = (long)(oneWayDelay.TotalSeconds * Stopwatch.Frequency);
        _listener = new TcpListener(IPAddress.Loopback, 0);
    }

    public static LatencyProxy Start(IPEndPoint target, TimeSpan oneWayDelay)
    {
        var proxy = new LatencyProxy(target, oneWayDelay);
        proxy._listener.Start();
        _ = proxy.AcceptLoop();
        return proxy;
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptSocketAsync(_cts.Token);
                var server = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                client.NoDelay = true;
                await server.ConnectAsync(_target, _cts.Token);
                StartDirection(client, server);
                StartDirection(server, client);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private void StartDirection(Socket from, Socket to)
    {
        var queue = new BlockingCollection<(long Due, byte[] Buffer, int Length)>();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                    var read = await from.ReceiveAsync(buffer, SocketFlags.None, _cts.Token);
                    if (read == 0)
                        break;
                    queue.Add((Stopwatch.GetTimestamp() + _delayTicks, buffer, read));
                }
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
            queue.CompleteAdding();
        });

        var sender = new Thread(() =>
        {
            try
            {
                foreach (var (due, buffer, length) in queue.GetConsumingEnumerable())
                {
                    var spin = new SpinWait();
                    while (Stopwatch.GetTimestamp() < due)
                        spin.SpinOnce(sleep1Threshold: -1);
                    to.Send(buffer, 0, length, SocketFlags.None);
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                to.Shutdown(SocketShutdown.Send);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
            }
        }) { IsBackground = true, Name = "LatencyProxy" };
        sender.Start();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
