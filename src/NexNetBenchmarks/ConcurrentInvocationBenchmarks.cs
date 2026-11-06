using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace NexNetBenchmarks;

/// <summary>
/// Calls from several callers at once on one connection. Mean is per call. Measures contention on the send path
/// and the server's invocation dispatch.
/// </summary>
public class ConcurrentInvocationBenchmarks
{
    private const int TotalCalls = 3200;

    private BenchConnection _connection = null!;

    [Params(1, 8, 32)]
    public int Callers { get; set; }

    private MetricsListenerScope? _metrics;

    /// <summary>
    /// With a listener attached to the NexNet meter, collecting once a second.
    /// </summary>
    [Params(false, true)]
    public bool Metrics { get; set; }

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        if (Metrics)
            _metrics = new MetricsListenerScope();

        _connection = await BenchConnection.Start(BenchTransport.Uds, $"concurrent-{Callers}");
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        await _connection.DisposeAsync();
        _metrics?.Dispose();
        _metrics = null;
    }

    [Benchmark(OperationsPerInvoke = TotalCalls)]
    public async Task InvocationNoArgumentWithResult()
    {
        var callers = new Task[Callers];
        for (var i = 0; i < callers.Length; i++)
            callers[i] = Task.Run(Caller);
        await Task.WhenAll(callers);
    }

    private async Task Caller()
    {
        var proxy = _connection.Client.Proxy;
        for (var i = 0; i < TotalCalls / Callers; i++)
            await proxy.InvocationNoArgumentWithResult();
    }
}
