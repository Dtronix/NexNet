using System.Buffers;
using System.IO.Pipelines;
using MemoryPack;
using Newtonsoft.Json.Linq;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NUnit.Framework;

namespace NexNet.IntegrationTests;

internal partial class NexusClientTests_NexusDuplexPipe : BaseTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ClientPipeReaderReceivesData(Type type)
    {
        var tcs = new TaskCompletionSource();
        var data = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var (server, sNexus, client, cNexus) = CreateServerClient(
            CreateServerConfig(type, false),
            CreateClientConfig(type, false));

  

        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();
            Assert.AreEqual(data, result.Buffer.ToArray());
            tcs.SetResult();
        };
        
        server.Start();
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await client.ReadyTask.WaitAsync(TimeSpan.FromSeconds(1));


        var pipe = sNexus.Context.CreatePipe(async pipe =>
        {
            await pipe.Output.WriteAsync(data);
            await Task.Delay(10000);
        });

        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe!);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ClientPipeReaderCompletesUponMethodExit(Type type)
    {
        var tcs = new TaskCompletionSource();
        var data = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var (server, sNexus, client, cNexus) = CreateServerClient(
            CreateServerConfig(type, true),
            CreateClientConfig(type, true));

        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        server.Start();
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await client.ReadyTask.WaitAsync(TimeSpan.FromSeconds(1));

        var pipe = sNexus.Context.CreatePipe(async writer =>
        {
        });
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ClientPipeReaderCancelsUponClientDisconnection(Type type)
    {
        var tcs = new TaskCompletionSource();
        var data = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var (server, sNexus, client, cNexus) = CreateServerClient(
            CreateServerConfig(type, false),
            CreateClientConfig(type, false));

        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };
        
        server.Start();
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await client.ReadyTask.WaitAsync(TimeSpan.FromSeconds(1));

        var pipe = sNexus.Context.CreatePipe(async writer =>
        {
            await Task.Delay(100);
            await client.DisconnectAsync();
            await Task.Delay(10000);
        });

        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ClientWriterIsNotifiedOfReaderCompletion(Type type)
    {
        var tcs = new TaskCompletionSource();
        var data = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var (server, sNexus, client, cNexus) = CreateServerClient(
            CreateServerConfig(type, false),
            CreateClientConfig(type, false));
        
        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();
        };

        

        server.Start();
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await client.ReadyTask.WaitAsync(TimeSpan.FromSeconds(1));

        var pipe = sNexus.Context.CreatePipe(async writer =>
        {
            FlushResult? result = null;
            while (result == null || !result.Value.IsCompleted)
            {
                result = await writer.Output.WriteAsync(data);
                await Task.Delay(1);
            }

            Assert.IsTrue(result.Value.IsCompleted);
            tcs.SetResult();
        });

        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }
    
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ClientWriterPipeCancelsUponClientDisconnection(Type type)
    {
        var tcs = new TaskCompletionSource();
        var data = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var (server, sNexus, client, cNexus) = CreateServerClient(
            CreateServerConfig(type, false),
            CreateClientConfig(type, false));

        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await Task.Delay(10000);
        };


        server.Start();
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await client.ReadyTask.WaitAsync(TimeSpan.FromSeconds(1));

        var pipe = sNexus.Context.CreatePipe(async writer =>
        {
            sNexus.Context.Disconnect();
            FlushResult? result = null;
            while (result == null || !result.Value.IsCompleted)
            {
                result = await writer.Output.WriteAsync(data);

                if (result.Value.IsCanceled || result.Value.IsCompleted)
                    break;
            }

            Assert.IsTrue(result.Value.IsCompleted);
            tcs.SetResult();
        });

        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
