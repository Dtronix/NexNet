using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace NexNetBenchmarks;

/// <summary>
/// TCP through a <see cref="LatencyProxy"/> that adds a 20 ms round-trip time. Shows whether the pipe window limits
/// throughput on a link with latency (window / RTT).
/// </summary>
[Config(typeof(ThroughputConfig))]
public class TcpLatencyBenchmarks
{
    private static readonly TimeSpan OneWayDelay = TimeSpan.FromMilliseconds(10);

    private BenchConnection _connection = null!;
    private readonly ReadOnlyMemory<byte> _chunk = new byte[64 * 1024];

    [Params(16)]
    public int SizeMiB { get; set; }

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _connection = await BenchConnection.Start(BenchTransport.Tcp, "tcp-latency", OneWayDelay);
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        await _connection.DisposeAsync();
    }

    [Benchmark]
    public async ValueTask InvocationNoArgumentWithResult()
    {
        await _connection.Client.Proxy.InvocationNoArgumentWithResult();
    }

    [Benchmark, BenchmarkCategory(ThroughputColumn.Category)]
    public Task PipeUpload() => NexNetBenchmarks.PipeUpload.Run(_connection, SizeMiB, _chunk);
}
