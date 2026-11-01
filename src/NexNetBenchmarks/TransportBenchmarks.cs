using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace NexNetBenchmarks;

/// <summary>
/// Compares the socket transport (TCP: the socket feeds its own send and receive pipes, so a flush returns once
/// the frame is in the send pipe) with the stream transports (TCP + TLS 1.3 and QUIC: <c>PipeWriter.Create</c> over
/// the stream, so a flush awaits the stream write while the session's write mutex is held). Mean is per call.
/// </summary>
public class TransportBenchmarks
{
    private const int Calls = 3200;
    private const int ConcurrentCallers = 32;

    private BenchConnection _connection = null!;
    private readonly byte[] _payload = new byte[16 * 1024];

    [Params(BenchTransport.Tcp, BenchTransport.TcpTls, BenchTransport.Quic)]
    public BenchTransport Transport { get; set; }

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _connection = await BenchConnection.Start(Transport, $"transport-{Transport}");
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        await _connection.DisposeAsync();
    }

    /// <summary>One caller; each call waits for its result before the next is sent.</summary>
    [Benchmark(OperationsPerInvoke = Calls)]
    public async Task RoundTrip()
    {
        var proxy = _connection.Client.Proxy;
        for (var i = 0; i < Calls; i++)
            await proxy.InvocationNoArgumentWithResult();
    }

    /// <summary>Callers waiting for results concurrently: contention on the write mutex on both peers.</summary>
    [Benchmark(OperationsPerInvoke = Calls)]
    public Task RoundTripConcurrent() => RunConcurrent(static async proxy =>
    {
        for (var i = 0; i < Calls / ConcurrentCallers; i++)
            await proxy.InvocationNoArgumentWithResult();
    });

    /// <summary>Calls awaiting completion (no value) from one caller.</summary>
    [Benchmark(OperationsPerInvoke = Calls)]
    public async Task AwaitedVoid()
    {
        var proxy = _connection.Client.Proxy;
        for (var i = 0; i < Calls; i++)
            await proxy.InvocationUnmanagedArgument(i);
    }

    /// <summary>
    /// One caller sending <c>void</c> (fire-and-forget) calls back to back, then one round trip as a barrier: the
    /// result arrives after the server has read every earlier frame. Bounded by the send path and invocation credit.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Calls)]
    public async Task FireAndForget()
    {
        var proxy = _connection.Client.Proxy;
        for (var i = 0; i < Calls; i++)
            proxy.InvocationFireAndForget(i);
        await proxy.InvocationNoArgumentWithResult();
    }

    /// <summary>As <see cref="FireAndForget"/> from concurrent callers: each send takes the client's write mutex.</summary>
    [Benchmark(OperationsPerInvoke = Calls)]
    public Task FireAndForgetConcurrent() => RunConcurrent(static async proxy =>
    {
        for (var i = 0; i < Calls / ConcurrentCallers; i++)
            proxy.InvocationFireAndForget(i);
        await proxy.InvocationNoArgumentWithResult();
    });

    /// <summary>16 KiB argument echoed back as the result; one caller.</summary>
    [Benchmark(OperationsPerInvoke = Calls / 8)]
    public async Task Echo16KiB()
    {
        var proxy = _connection.Client.Proxy;
        for (var i = 0; i < Calls / 8; i++)
            await proxy.Echo(_payload);
    }

    private Task RunConcurrent(Func<ClientNexus.ServerProxy, Task> caller)
    {
        var proxy = _connection.Client.Proxy;
        var callers = new Task[ConcurrentCallers];
        for (var i = 0; i < callers.Length; i++)
            callers[i] = Task.Run(() => caller(proxy));
        return Task.WhenAll(callers);
    }
}
