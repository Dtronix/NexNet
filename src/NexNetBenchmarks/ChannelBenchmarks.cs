using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using MemoryPack;
using NexNet;
using NexNet.Logging;
using NexNet.Pipes;
using NexNet.Pipes.Channels;
using NexNet.Transports;
using NexNet.Transports.Uds;

namespace NexNetBenchmarks;

public class ChannelBenchmarks
{
    private NexusClient<ClientNexus, ClientNexus.ServerProxy> _client = null!;
    private NexusServer<ServerNexus, ServerNexus.ClientProxy> _server = null!;
    private ReadOnlyMemory<byte> _uploadBuffer;
    private ConsoleLogger _log = null!;
    private ServerNexus _serverNexus;

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _uploadBuffer = new byte[(1024 * 16)];
        var path = $"test.sock";
        if (File.Exists(path))
            File.Delete(path);

        _log = new ConsoleLogger();
        
        var serverConfig = new UdsServerConfig()
        {
            EndPoint = new UnixDomainSocketEndPoint(path),
            //Logger = _log.CreateLogger(null, "SV"),
        };
        var clientConfig = new UdsClientConfig()
        {
            EndPoint = new UnixDomainSocketEndPoint(path),
            //Logger = _log.CreateLogger(null, "CL"),
        };

        _client = ClientNexus.CreateClient(clientConfig, new ClientNexus());
        _serverNexus = new ServerNexus();
        _server = ServerNexus.CreateServer(serverConfig, () => _serverNexus);
        await _server.StartAsync();
        await _client.ConnectAsync();
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        await _client.DisconnectAsync();
        await _server.StopAsync();
    }

    [Benchmark]
    public async ValueTask PooledMessage_Standalone()
    {
        const int iterations = 10;
        var completion = new TaskCompletionSource();
        _serverNexus.InvocationWithDuplexPipe_ChannelFunc = async duplexPipe =>
        {
            var reader = await duplexPipe.GetPooledMessageChannelReader<StandAloneMessage>();
            var count = 0;
            await foreach (var message in reader)
            {
                if(++count == iterations)
                    completion.SetResult();
            }
        };
        await using var pipe = _client.CreatePipe();
        await _client.Proxy.InvocationWithDuplexPipe_Channel(pipe);
        var writer = await pipe.GetPooledMessageChannelWriter<StandAloneMessage>();

        var message = StandAloneMessage.Rent();
        for (int i = 0; i < iterations; i++)
        {
            message.Id++;
            await writer.WriteAsync(message);
        }
        
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }
}


abstract class NetworkMessageUnion : INexusPooledMessageUnion<NetworkMessageUnion>
{
    public static void RegisterMessages(INexusPooledMessageUnionBuilder<NetworkMessageUnion> registerer)
    {
        registerer.Add<LoginMessage>();
        registerer.Add<ChatMessage>();
    }
}

[MemoryPackable]
partial class LoginMessage : NetworkMessageUnion, INexusPooledMessage<LoginMessage>
{
    public static byte UnionId => 0;
    
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

[MemoryPackable]
partial class ChatMessage : NetworkMessageUnion, INexusPooledMessage<ChatMessage>
{
    public static byte UnionId => 1;
    public string Sender { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public long Timestamp { get; set; }
}

[MemoryPackable]
partial class StandAloneMessage : NexusPooledMessageBase<StandAloneMessage>
{
    public int Id { get; set; }
}
