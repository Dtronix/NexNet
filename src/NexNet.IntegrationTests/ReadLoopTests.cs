using NexNet.IntegrationTests.Security;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Invocation;
using NexNet.Messages;
using NUnit.Framework;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// The session read loop waits only on the transport (docs/internals/flow-control-v2.md, section 5.1).
/// </summary>
internal class ReadLoopTests : BaseTests
{
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ThreePipeMethodsAndACall_WithDefaultSlots(Type type)
    {
        // Two invocation slots by default. Pipe methods used to hold theirs for the pipe's lifetime, and the read loop
        // waited for a free slot, so the third pipe and the call never ran.
        var server = CreateServer(CreateServerConfig(type), nexus =>
        {
            nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) => await pipe.CompleteTask;
            nexus.ServerTaskValueEvent = _ => new ValueTask<int>(42);
        });
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(1);

        var pipes = new List<NexNet.Pipes.IRentedNexusDuplexPipe>();
        for (var i = 0; i < 3; i++)
        {
            var pipe = client.CreatePipe();
            pipes.Add(pipe);
            await client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);
            await pipe.ReadyTask.Timeout(1);
        }

        Assert.That(await client.Proxy.ServerTaskValue().Timeout(1), Is.EqualTo(42));

        foreach (var pipe in pipes)
            await pipe.DisposeAsync();
    }

    [Test]
    public async Task PingFloodWithoutReading_DoesNotStopTheReadLoop()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(serverConfig, nexus => nexus.OnDisconnectedEvent = _ =>
        {
            disconnected.TrySetResult();
            return default;
        });
        await server.StartAsync();

        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        await client.SendMessageAsync(new ClientGreetingMessage
        {
            ServerNexusHash = IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = IInvocationMethodHash.GetMethodHash<ClientNexus>(),
        }).Timeout(1);
        await client.AssertReceiveMessageAsync<ServerGreetingMessage>().Timeout(1);

        // 32 MiB of pings, never reading the replies. Each reply used to be sent and flushed by the read loop, which
        // stopped once the transport's send buffers filled.
        var pings = new byte[1024 * 1024];
        pings.AsSpan().Fill((byte)MessageType.Ping);
        for (var i = 0; i < 32; i++)
            await client.Stream!.WriteAsync(pings).AsTask().Timeout(5);

        // The read loop still reaches this.
        client.Write((byte)MessageType.DisconnectGraceful);
        await disconnected.Task.Timeout(5);
    }

    [Test]
    public async Task IdleTimeout_IsNotRefreshedByAnIncompleteFrame()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        serverConfig.Timeout = 1000;
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();

        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        await client.SendMessageAsync(new ClientGreetingMessage
        {
            ServerNexusHash = IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = IInvocationMethodHash.GetMethodHash<ClientNexus>(),
        }).Timeout(1);
        await client.AssertReceiveMessageAsync<ServerGreetingMessage>().Timeout(1);

        // One frame header, then its body a byte every 100 ms. Any received byte used to count as activity.
        client.Write([(byte)MessageType.InvocationCancellation, 0xFF, 0x00]);
        var disconnect = client.AssertDisconnectReason(DisconnectReason.Timeout);
        for (var i = 0; i < 40 && !disconnect.IsCompleted; i++)
        {
            await Task.Delay(100);
            try
            {
                client.Stream!.WriteByte(0);
            }
            catch (IOException)
            {
                break;
            }
        }

        await disconnect.Timeout(1);
    }
}
