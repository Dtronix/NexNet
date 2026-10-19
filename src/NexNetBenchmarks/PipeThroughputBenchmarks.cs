using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using NexNet.Pipes;

namespace NexNetBenchmarks;

/// <summary>
/// Uploads through a duplex pipe to a server that reads as fast as it can. Each operation opens a pipe, writes
/// <see cref="SizeMiB"/> in 64 KiB writes, completes it and waits until the server has read everything.
/// </summary>
[Config(typeof(ThroughputConfig))]
public class PipeThroughputBenchmarks
{
    private const int WriteSize = 64 * 1024;

    private BenchConnection _connection = null!;
    private readonly ReadOnlyMemory<byte> _chunk = new byte[WriteSize];

    [Params(1, 64)]
    public int SizeMiB { get; set; }

    [Params(BenchTransport.Uds, BenchTransport.Tcp)]
    public BenchTransport Transport { get; set; }

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _connection = await BenchConnection.Start(Transport, $"pipe-{SizeMiB}-{Transport}");
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        await _connection.DisposeAsync();
    }

    [Benchmark, BenchmarkCategory(ThroughputColumn.Category)]
    public Task Upload() => PipeUpload.Run(_connection, SizeMiB, _chunk);
}

internal static class PipeUpload
{
    public static async Task Run(BenchConnection connection, int sizeMiB, ReadOnlyMemory<byte> chunk)
    {
        var done = ServerNexus.ResetPipeDone();
        await using var pipe = connection.Client.CreatePipe();
        await connection.Client.Proxy.PipeDrain(pipe);
        await pipe.ReadyTask.WaitAsync(TimeSpan.FromSeconds(10));

        var remaining = (long)sizeMiB * 1024 * 1024;
        while (remaining > 0)
        {
            var write = chunk[..(int)Math.Min(chunk.Length, remaining)];
            await pipe.Output.WriteAsync(write);
            remaining -= write.Length;
        }

        await pipe.Output.CompleteAsync();
        await done.WaitAsync(TimeSpan.FromSeconds(120));
    }
}
