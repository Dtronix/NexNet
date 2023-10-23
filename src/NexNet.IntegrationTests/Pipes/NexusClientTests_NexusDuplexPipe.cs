using System.Buffers;
using System.IO.Pipelines;
using NUnit.Framework;
using NUnit.Framework.Internal;

namespace NexNet.IntegrationTests.Pipes;

internal class NexusClientTests_NexusDuplexPipe : BasePipeTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Client_PipeReaderReceivesDataMultipleTimes(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);
        int count = 0;

        // TODO: Review adding a test for increased iterations as this has been found to sometimes fail on CI.
        const int iterations = 21;
        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync().Timeout(1);

            if (Interlocked.Increment(ref count) == iterations)
                tcs.SetResult();

            // If the connection is still alive, the buffer should contain the data.
            if (!result.IsCompleted)
            {
                Assert.AreEqual(Data, result.Buffer.ToArray());
            }
        };

        for (int i = 0; i < iterations; i++)
        {
            await using var pipe = sNexus.Context.CreatePipe();
            await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(1);
            await pipe.ReadyTask.Timeout(1);
            await pipe.Output.WriteAsync(Data).Timeout(1);
        }

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Client_PipeReaderReceivesDataMultipleTimesWithLargeData(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);
        int count = 0;
        var largeData = new byte[1024 * 32];
        // TODO: Review adding a test for increased iterations as this has been found to sometimes fail on CI.
        const int iterations = 1000;
        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync().Timeout(1);
            pipe.Input.AdvanceTo(result.Buffer.End);

            if (Interlocked.Increment(ref count) == iterations)
                tcs.SetResult();
        };

        for (int i = 0; i < iterations; i++)
        {
            await using var pipe = sNexus.Context.CreatePipe();
            await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(1);
            await pipe.ReadyTask.Timeout(1);
            await pipe.Output.WriteAsync(largeData).Timeout(1);
        }

        await tcs.Task.Timeout(1);
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
            var result = await pipe.Input.ReadAsync().Timeout(1);
            Assert.AreEqual(Data, result.Buffer.ToArray());
            tcs.SetResult();
        };

        var pipe = sNexus.Context.CreatePipe();

        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe!).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        await pipe.Output.WriteAsync(Data).Timeout(1);

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
            await pipe.Output.WriteAsync(Data).Timeout(1);
            await Task.Delay(10000);
        };

        var pipe = sNexus.Context.CreatePipe();
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        var result = await pipe.Input.ReadAsync().Timeout(1);
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
            var result = await pipe.Input.ReadAsync().Timeout(1);
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        var pipe = sNexus.Context.CreatePipe();
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        await pipe.CompleteAsync().Timeout(1);

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
                result = await pipe.Output.WriteAsync(Data).Timeout(1);

                if (result.IsCompleted)
                    break;

                await Task.Delay(100);
            }

            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        var pipe = sNexus.Context.CreatePipe();
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        await pipe.CompleteAsync().Timeout(1);

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
            var result = await pipe.Input.ReadAsync().Timeout(1);
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        var pipe = sNexus.Context.CreatePipe();

        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        await cNexus.Context.DisconnectAsync().Timeout(1);

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
            await tcsDisconnected.Task.Timeout(1);
            await Task.Delay(150);
            var result = await pipe.Output.WriteAsync(Data).Timeout(1);
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        var pipe = sNexus.Context.CreatePipe();

        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        await cNexus.Context.DisconnectAsync().Timeout(1);
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
            var result = await pipe.Input.ReadAsync().Timeout(1);
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        var pipe = sNexus.Context.CreatePipe();

        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        await pipe.Output.CompleteAsync().Timeout(1);

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
                result = await pipe.Output.WriteAsync(Data).Timeout(1);
                await Task.Delay(1);
            }

            Assert.IsTrue(result.Value.IsCompleted);
            tcs.SetResult();

        };

        var pipe = sNexus.Context.CreatePipe();
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        await pipe.Input.CompleteAsync().Timeout(1);

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
            var result = await pipe.Input.ReadAsync().Timeout(1);
            Assert.IsTrue(result.IsCompleted);

            await pipe.Output.WriteAsync(Data).Timeout(1);
            await Task.Delay(1000);
        };

        var pipe = sNexus.Context.CreatePipe();
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        await pipe.Output.CompleteAsync().Timeout(1);

        var result = await pipe.Input.ReadAsync().Timeout(1);
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
            var result = await pipe.Output.WriteAsync(Data).Timeout(1);
            Assert.IsTrue(result.IsCompleted);

            var buffer = await pipe.Input.ReadAsync().Timeout(1);
            pipe.Input.AdvanceTo(buffer.Buffer.Start);

            var readResult = await pipe.Input.ReadAsync().Timeout(1);
            Assert.AreEqual(Data, readResult.Buffer.ToArray());
            tcs.SetResult();
        };

        var pipe = sNexus.Context.CreatePipe();
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        await pipe.Input.CompleteAsync().Timeout(1);
        outputComplete.TrySetResult();

        await pipe.Output.WriteAsync(Data).Timeout(1);

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

        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(1);

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
            await client.DisconnectAsync().Timeout(1);
            await Task.Delay(100000);
        };

        await client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);

        await AssertThrows<TaskCanceledException>(async () => await pipe.ReadyTask).Timeout(2);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Client_PipeCompleteCancelsOnDisconnection(Type type)
    {
        var (server, _, client, _, _) = await Setup(type);

        var pipe = client.CreatePipe();

        // Pause the receiving to test the cancellation
        server.Config.InternalOnReceive = async (session, sequence) =>
        {
            await client.DisconnectAsync().Timeout(1);
            await Task.Delay(100000);
        };

        await client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);

        await AssertThrows<TaskCanceledException>(async () => await pipe.CompleteTask).Timeout(2);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Client_PipeNotifiesWhenComplete(Type type)
    {
        var (_, sNexus, client, _, _) = await Setup(type);

        bool completedInvocation = false;
        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await Task.Delay(100);
            completedInvocation = true;
        };

        var pipe = client.CreatePipe();

        await client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);
        await pipe.ReadyTask.Timeout(1);

        await pipe.CompleteTask.Timeout(1);
        Assert.IsTrue(completedInvocation);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Client_ThrowsWhenPassingPipeFromWrongNexus(Type type)
    {
        var (_, sNexus, client, _, _) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = (nexus, pipe) => ValueTask.CompletedTask;

        var pipe = sNexus.Context.CreatePipe();

        await AssertThrows<InvalidOperationException>(() =>
            client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1)).Timeout(1);
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Client_ThrowsWhenPassingUsedPipe(Type type)
    {
        var (_, sNexus, client, _, _) = await Setup(type);


        sNexus.ServerTaskValueWithDuplexPipeEvent = (nexus, pipe) => ValueTask.CompletedTask;

        var pipe = client.CreatePipe();
        await client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);
        await pipe.CompleteTask.Timeout(1);

        await AssertThrows<InvalidOperationException>(() =>
            client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1)).Timeout(1);
    }



}
