using System.Buffers;
using System.IO.Pipelines;
using MemoryPack;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexusClientTests_NexusPipe : BaseTests
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

        server.Start();

        cNexus.ClientTaskValueWithPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Reader.ReadAsync();
            Assert.AreEqual(data, result.Buffer.ToArray());
            tcs.SetResult();
        };

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await sNexus.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var pipe = NexusPipe.Create(async (writer, token) =>
        {
            await writer.WriteAsync(data, token);
            await Task.Delay(10000);
        });
        await sNexus.Context.Clients.Caller.ClientTaskValueWithPipe(pipe);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ClientPipeReaderCompletesUponMethodExit(Type type)
    {
        var tcs = new TaskCompletionSource();
        var data = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var (server, sNexus, client, cNexus) = CreateServerClient(
            CreateServerConfig(type, false),
            CreateClientConfig(type, false));

        server.Start();

        cNexus.ClientTaskValueWithPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Reader.ReadAsync();
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await sNexus.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var pipe = NexusPipe.Create(async (writer, token) =>
        {
            
        });
        await sNexus.Context.Clients.Caller.ClientTaskValueWithPipe(pipe);

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

        server.Start();

        cNexus.ClientTaskValueWithPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Reader.ReadAsync();
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await sNexus.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var pipe = NexusPipe.Create(async (writer, token) =>
        {
            client.DisconnectAsync();
            await Task.Delay(10000);
        });

        Assert.ThrowsAsync<TaskCanceledException>(async() =>await sNexus.Context.Clients.Caller.ClientTaskValueWithPipe(pipe));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(100));
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

        server.Start();

        cNexus.ClientTaskValueWithPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Reader.ReadAsync();
        };

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await sNexus.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var pipe = NexusPipe.Create(async (writer, token) =>
        {
            FlushResult? result = null;
            while (result == null || !result.Value.IsCompleted)
            {
                result = await writer.WriteAsync(data);
            }

            Assert.IsTrue(result.Value.IsCompleted);
            tcs.SetResult();
        });
        await sNexus.Context.Clients.Caller.ClientTaskValueWithPipe(pipe);

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

        server.Start();

        cNexus.ClientTaskValueWithPipeEvent = async (nexus, pipe) =>
        {
            sNexus.Context.Disconnect();
            await Task.Delay(10000);
        };

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await sNexus.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var pipe = NexusPipe.Create(async (writer, token) =>
        {
            FlushResult? result = null;
            while (result == null || !result.Value.IsCompleted)
            {
                result = await writer.WriteAsync(data);
                
                if(result.Value.IsCanceled || result.Value.IsCompleted)
                    break;
            }

            Console.WriteLine(result.Value.IsCanceled);
            Assert.IsTrue(result.Value.IsCompleted);
            tcs.SetResult();
        });

        Assert.ThrowsAsync<TaskCanceledException>(async () =>
        {
            await sNexus.Context.Clients.Caller.ClientTaskValueWithPipe(pipe).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        });

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }



}
