using System.Buffers;
using System.IO.Pipelines;
using NUnit.Framework;

namespace NexNet.IntegrationTests;

internal class NexusClientTests_NexusDuplexPipe : BasePipeTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Client_PipeReaderReceivesDataMultipleTimes(Type type)
    {
        //BlockForClose = true;
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);
        int count = 0;
        
        // Ensure that the ids will properly wrap around.
        const int iterations = 50;
        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();

            // If the connection is still alive, the buffer should contain the data.
            if (!result.IsCompleted)
            {
                Assert.AreEqual(Data, result.Buffer.ToArray());
            }

            if(++count == iterations)
                tcs.SetResult();
        };

        for (int i = 0; i < iterations; i++)
        {
            await using var pipe = sNexus.Context.CreatePipe();
            await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe);
            await pipe.ReadyTask;
            await pipe.Output.WriteAsync(Data);
        }


        await tcs.Task.Timeout(20);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Client_PipeReaderReceivesData(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();
            Assert.AreEqual(Data, result.Buffer.ToArray());
            tcs.SetResult();
        };

        var pipe = sNexus.Context.CreatePipe();

        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe!);

        await pipe.ReadyTask;
        await pipe.Output.WriteAsync(Data);

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Client_PipeWriterSendsData(Type type)
    {
        var (_, sNexus, _, cNexus, _) = await Setup(type);

        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await pipe.Output.WriteAsync(Data);
            await Task.Delay(10000);
        };

        var pipe = sNexus.Context.CreatePipe();
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe);

        await pipe.ReadyTask;
        var result = await pipe.Input.ReadAsync();
        Assert.AreEqual(Data, result.Buffer.ToArray());
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Client_PipeReaderCompletesUponPipeCompleteAsync(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        var pipe = sNexus.Context.CreatePipe();
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe);

        await pipe.ReadyTask;
        await pipe.CompleteAsync();

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Client_PipeWriterCompletesUponCompleteAsync(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);
        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            FlushResult result = default;
            for (int i = 0; i < 20; i++)
            {
                result = await pipe.Output.WriteAsync(Data);

                if (result.IsCompleted)
                    break;

                await Task.Delay(100);
            }

            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        var pipe = sNexus.Context.CreatePipe();
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe);

        await pipe.ReadyTask;
        await pipe.CompleteAsync();

        await tcs.Task.Timeout(3);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Client_PipeReaderCompletesUponDisconnection(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        var pipe = sNexus.Context.CreatePipe();

        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).AsTask().Timeout(1);

        await pipe.ReadyTask;
        await cNexus.Context.DisconnectAsync();

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Client_PipeWriterCompletesUponDisconnection(Type type)
    {
        var tcsDisconnected = new TaskCompletionSource();
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await tcsDisconnected.Task;
            await Task.Delay(150);
            var result = await pipe.Output.WriteAsync(Data);
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        var pipe = sNexus.Context.CreatePipe();

        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).AsTask()
            .Timeout(1);

        await pipe.ReadyTask;
        await cNexus.Context.DisconnectAsync();
        tcsDisconnected.SetResult();

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Client_PipeReaderCompletesUponWriterCompletion(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        var pipe = sNexus.Context.CreatePipe();

        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe);

        await pipe.ReadyTask;
        await pipe.Output.CompleteAsync();

        await tcs.Task.Timeout(1);
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Client_PipeWriterCompletesUponWriterCompletion(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            FlushResult? result = null;
            while (result == null || !result.Value.IsCompleted)
            {
                result = await pipe.Output.WriteAsync(Data);
                await Task.Delay(1);
            }

            Assert.IsTrue(result.Value.IsCompleted);
            tcs.SetResult();

        };

        var pipe = sNexus.Context.CreatePipe();
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe);

        await pipe.ReadyTask;
        await pipe.Input.CompleteAsync();

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Client_PipeWriterRemainsOpenUponOtherWriterCompletion(Type type)
    {
        var (_, sNexus, _, cNexus, _) = await Setup(type);

        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();
            Assert.IsTrue(result.IsCompleted);

            await pipe.Output.WriteAsync(Data);
            await Task.Delay(1000);
        };

        var pipe = sNexus.Context.CreatePipe();
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe);

        await pipe.ReadyTask;
        await pipe.Output.CompleteAsync();

        var result = await pipe.Input.ReadAsync();
        Assert.AreEqual(Data, result.Buffer.ToArray());
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Client_PipeReaderRemainsOpenUponOtherReaderCompletion(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);
        var outputComplete = new TaskCompletionSource();

        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await outputComplete.Task.Timeout(1);
            await Task.Delay(50);
            var result = await pipe.Output.WriteAsync(Data);
            Assert.IsTrue(result.IsCompleted);

            var buffer = await pipe.Input.ReadAsync();
            pipe.Input.AdvanceTo(buffer.Buffer.Start);

            var readResult = await pipe.Input.ReadAsync();
            Assert.AreEqual(Data, readResult.Buffer.ToArray());
            tcs.SetResult();
        };

        var pipe = sNexus.Context.CreatePipe();
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe);

        await pipe.ReadyTask;
        await pipe.Input.CompleteAsync();
        outputComplete.TrySetResult();

        await pipe.Output.WriteAsync(Data);

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Client_PipeNotifiesWhenReady(Type type)
    {
        var (_, sNexus, _, cNexus, _) = await Setup(type);

        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await Task.Delay(10000);
        };

        var pipe = sNexus.Context.CreatePipe();

        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe);

        await pipe.ReadyTask.Timeout(1);
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Client_PipeReadyCancelsOnDisconnection(Type type)
    {
        var (server, _, client, _, _) = await Setup(type);

        var pipe = client.CreatePipe();

        // Pause the receiving to test the cancellation
        server.Config.InternalOnReceive = async (session, sequence) =>
        {
            await client.DisconnectAsync();
            await Task.Delay(100000);
        };

        await client.Proxy.ServerTaskValueWithDuplexPipe(pipe);

        await AssertThrows<TaskCanceledException>(async () => await pipe.ReadyTask).Timeout(2);
    }

}
