using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using NexNet;
using NexNet.Pipes;
using NexNet.Transports;
using NexNet.Transports.Uds;

namespace NexNetBenchmarks;

/// <summary>
/// Items per second over typed channels. The fragmented case forces small pipe flush chunks so items split across
/// frames, exercising incomplete-item handling in the channel reader.
/// </summary>
[MemoryDiagnoser]
public class ChannelThroughputBenchmarks
{
    private const int ItemCount = 2000;

    private NexusClient<ClientNexus, ClientNexus.ServerProxy> _client = null!;
    private NexusServer<ServerNexus, ServerNexus.ClientProxy> _server = null!;
    private BenchPerson[] _persons = null!;
    private int[][] _intArrays = null!;
    private int[] _ints = null!;

    [Params(false, true)]
    public bool Fragmented { get; set; }

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        var path = $"channel-{Fragmented}.sock";
        if (File.Exists(path))
            File.Delete(path);

        var chunkSize = Fragmented ? 1024 : 64 * 1024;
        var serverConfig = new UdsServerConfig { EndPoint = new UnixDomainSocketEndPoint(path), NexusPipeFlushChunkSize = chunkSize };
        var clientConfig = new UdsClientConfig { EndPoint = new UnixDomainSocketEndPoint(path), NexusPipeFlushChunkSize = chunkSize };

        _client = ClientNexus.CreateClient(clientConfig, new ClientNexus());
        _server = ServerNexus.CreateServer(serverConfig, static () => new ServerNexus());
        await _server.StartAsync();
        await _client.ConnectAsync();

        _persons = Enumerable.Range(0, ItemCount).Select(BenchPerson.Create).ToArray();
        _intArrays = Enumerable.Range(0, ItemCount / 20).Select(i => Enumerable.Range(i, 256).ToArray()).ToArray();
        _ints = Enumerable.Range(0, ItemCount).ToArray();
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        await _client.DisconnectAsync();
        await _server.StopAsync();
    }

    private static Task<int> ResetDone()
    {
        ServerNexus.ChannelDone = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        return ServerNexus.ChannelDone.Task;
    }

    [Benchmark]
    public async Task Persons()
    {
        var done = ResetDone();
        await using var channel = _client.CreateChannel<BenchPerson>();
        await _client.Proxy.ChannelPersons(channel);
        await channel.WriteAndComplete(_persons, 100);
        await done.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Benchmark]
    public async Task IntArrays256()
    {
        var done = ResetDone();
        await using var channel = _client.CreateChannel<int[]>();
        await _client.Proxy.ChannelIntArrays(channel);
        await channel.WriteAndComplete(_intArrays, 10);
        await done.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Benchmark]
    public async Task Ints()
    {
        var done = ResetDone();
        await using var channel = _client.CreateChannel<int>();
        await _client.Proxy.ChannelInts(channel);
        await channel.WriteAndComplete(_ints, 200);
        await done.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
