using System.Buffers;
using System.IO.Pipelines;
using MemoryPack;
using Newtonsoft.Json.Linq;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NUnit.Framework;

namespace NexNet.IntegrationTests;

internal partial class NexusServerTests_NexusDuplexPipe : BaseTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ServerPipeReaderReceivesData(Type type)
    {
        var tcs = new TaskCompletionSource();
        var data = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var (server, sNexus, client, cNexus) = CreateServerClient(
            CreateServerConfig(type, false),
            CreateClientConfig(type, false));

  

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();
            Assert.AreEqual(data, result.Buffer.ToArray());
            tcs.SetResult();
        };
        
        server.Start();
        await client.ConnectAsync().Timeout(1);
        await client.ReadyTask.Timeout(1);


        var pipe = cNexus.Context.CreatePipe(async pipe =>
        {
            await pipe.Output.WriteAsync(data);
            await Task.Delay(10000);
        });

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe!);

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ServerPipeReaderCompletesUponMethodExit(Type type)
    {
        var tcs = new TaskCompletionSource();
        var data = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var (server, sNexus, client, cNexus) = CreateServerClient(
            CreateServerConfig(type, false),
            CreateClientConfig(type, false));

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        server.Start();
        await client.ConnectAsync().Timeout(1);
        await client.ReadyTask.Timeout(1);

        var pipe = cNexus.Context.CreatePipe(async writer =>
        {
        });
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe);

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ServerPipeReaderCancelsUponClientDisconnection(Type type)
    {
        var tcs = new TaskCompletionSource();
        var data = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var (server, sNexus, client, cNexus) = CreateServerClient(
            CreateServerConfig(type, false),
            CreateClientConfig(type, false));

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();
            //Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };
        
        server.Start();
        await client.ConnectAsync().Timeout(1);
        await client.ReadyTask.Timeout(1);

        var pipe = cNexus.Context.CreatePipe(async writer =>
        {
            await Task.Delay(100);
            await client.DisconnectAsync();
            await Task.Delay(10000);
        });

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).AsTask().Timeout(1);

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ServerWriterIsNotifiedOfReaderCompletion(Type type)
    {
        var tcs = new TaskCompletionSource();
        var data = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var (server, sNexus, client, cNexus) = CreateServerClient(
            CreateServerConfig(type, false),
            CreateClientConfig(type, false));

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {

            var result = await pipe.Input.ReadAsync();
        };


        server.Start();
        await client.ConnectAsync().Timeout(1);
        await client.ReadyTask.Timeout(1);

        var pipe = cNexus.Context.CreatePipe(async writer =>
        {
            FlushResult? result = null;
            while (result == null || !result.Value.IsCompleted)
            {
                result = await writer.Output.WriteAsync(data);
            }

            Assert.IsTrue(result.Value.IsCompleted);
            tcs.SetResult();
        });
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe);

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ServerWriterPipeCancelsUponClientDisconnection(Type type)
    {
        var tcs = new TaskCompletionSource();
        var data = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var (server, sNexus, client, cNexus) = CreateServerClient(
            CreateServerConfig(type, false),
            CreateClientConfig(type, false));

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            
            await Task.Delay(10000);
        };

        server.Start();
        await client.ConnectAsync().Timeout(1);
        await client.ReadyTask.Timeout(1);

        var pipe = cNexus.Context.CreatePipe(async writer =>
        {
            cNexus.Context.Disconnect();
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

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).AsTask().Timeout(1);

        await tcs.Task.Timeout(1);
    }
}
