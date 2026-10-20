using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace NexNetBenchmarks;

/// <summary>
/// Round trip of a byte array argument echoed back as the result. Values larger than one frame are streamed both
/// ways (docs/internals/flow-control.md, section 4.3). The MB/s column counts the value once.
/// </summary>
[Config(typeof(ThroughputConfig))]
public class LargeValueBenchmarks
{
    private BenchConnection _connection = null!;
    private byte[] _data = null!;

    // 4095 KiB stays under the default 4 MiB MaxMessageSize with the message's own overhead.
    [Params(128, 1024, 4095)]
    public int SizeKiB { get; set; }

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _connection = await BenchConnection.Start(BenchTransport.Uds, $"large-{SizeKiB}");
        _data = new byte[SizeKiB * 1024];
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        await _connection.DisposeAsync();
    }

    [Benchmark, BenchmarkCategory(ThroughputColumn.Category)]
    public async Task<int> Echo() => (await _connection.Client.Proxy.Echo(_data)).Length;
}
