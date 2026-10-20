using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using NexNet.Pipes;

namespace NexNetBenchmarks;

/// <summary>
/// Call round trip on a connection that is also streaming through a pipe as fast as the server reads it.
/// Measures how much pipe traffic delays invocations and results on the same session.
/// </summary>
public class PipeLoadLatencyBenchmarks
{
    private BenchConnection _connection = null!;
    private IRentedNexusDuplexPipe _pipe = null!;
    private CancellationTokenSource _stop = null!;
    private Task _load = null!;

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _connection = await BenchConnection.Start(BenchTransport.Uds, "pipe-load");
        _pipe = _connection.Client.CreatePipe();
        await _connection.Client.Proxy.PipeDrain(_pipe);
        await _pipe.ReadyTask.WaitAsync(TimeSpan.FromSeconds(10));

        _stop = new CancellationTokenSource();
        _load = Task.Run(async () =>
        {
            var chunk = new byte[64 * 1024];
            while (!_stop.IsCancellationRequested)
                await _pipe.Output.WriteAsync(chunk);
        });
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        _stop.Cancel();
        await _load;
        await _pipe.Output.CompleteAsync();
        await _pipe.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Benchmark]
    public async ValueTask InvocationNoArgumentWithResult()
    {
        await _connection.Client.Proxy.InvocationNoArgumentWithResult();
    }
}
