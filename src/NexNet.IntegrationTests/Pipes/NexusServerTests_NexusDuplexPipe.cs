using System.Buffers;
using System.IO.Pipelines;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes;

internal class NexusServerTests_NexusDuplexPipe : BasePipeTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Server_PipeReaderReceivesDataMultipleTimes(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);
        var count = 0;

        // TODO: Review adding a test for increased iterations as this has been found to sometimes fail on CI.
        const int iterations = 10000;
        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync().Timeout(1);

            // If the connection is still alive, the buffer should contain the data.
            if (!result.IsCompleted)
            {
                Assert.AreEqual(Data, result.Buffer.ToArray());
            }

            if (Interlocked.Increment(ref count) == iterations)
                tcs.SetResult();
        };

        for (var i = 0; i < iterations; i++)
        {
            await using var pipe = cNexus.Context.CreatePipe();
            await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);
            await pipe.ReadyTask.Timeout(1);
            await pipe.Output.WriteAsync(Data).Timeout(1);
        }

        await tcs.Task.Timeout(1);
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Server_PipeReaderReceivesDataMultipleTimesWithLargeData(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);
        var count = 0;
        var largeData = new byte[1024 * 32];
        // TODO: Review adding a test for increased iterations as this has been found to sometimes fail on CI.
        const int iterations = 1000;
        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync().Timeout(1);
            pipe.Input.AdvanceTo(result.Buffer.End);

            if (Interlocked.Increment(ref count) == iterations)
                tcs.SetResult();
        };

        for (var i = 0; i < iterations; i++)
        {
            await using var pipe = cNexus.Context.CreatePipe();
            await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);
            await pipe.ReadyTask.Timeout(1);
            await pipe.Output.WriteAsync(largeData).Timeout(1);
        }

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Server_PipeReaderCreatesAndDestroysPipeMultipleTimes(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);
        var count = 0;

        // TODO: Review adding a test for increased iterations as this has been found to sometimes fail on CI.
        const int iterations = 10000;
        sNexus.ServerTaskValueWithDuplexPipeEvent = (nexus, pipe) =>
        {
            if (Interlocked.Increment(ref count) == iterations)
                tcs.SetResult();

            return ValueTask.CompletedTask;
        };

        for (var i = 0; i < iterations; i++)
        {
            await using var pipe = cNexus.Context.CreatePipe();
            await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);
            await pipe.ReadyTask.Timeout(1);
        }

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Server_PipeReaderReceivesData(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync().Timeout(1);
            Assert.AreEqual(Data, result.Buffer.ToArray());
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe();
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        await pipe.Output.WriteAsync(Data).Timeout(1);

        await tcs.Task.Timeout(1).Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Server_PipeWriterSendsData(Type type)
    {
        var (_, sNexus, _, cNexus, _) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await pipe.Output.WriteAsync(Data).Timeout(1);
            await Task.Delay(10000);
        };

        var pipe = cNexus.Context.CreatePipe();
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        var result = await pipe.Input.ReadAsync().Timeout(1);
        Assert.AreEqual(Data, result.Buffer.ToArray());
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Server_PipeReaderCompletesUponCompleteAsync(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync().Timeout(1);
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe();
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        await pipe.CompleteAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Server_PipeWriterCompletesUponCompleteAsync(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);
        var completedTcs = new TaskCompletionSource();

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            // Wait for the client to complete the pipe
            await completedTcs.Task.Timeout(1);

            FlushResult result = default;
            for (var i = 0; i < 20; i++)
            {
                result = await pipe.Output.WriteAsync(Data).Timeout(1);

                if (result.IsCompleted)
                    break;

                await Task.Delay(100);
            }

            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe();
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        await pipe.CompleteAsync().Timeout(1);
        completedTcs.SetResult();

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Server_PipeReaderCompletesUponDisconnection(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe();
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        await Task.Delay(100);
        await sNexus.Context.DisconnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Server_PipeWriterCompletesUponDisconnection(Type type)
    {
        var tcsDisconnected = new TaskCompletionSource();
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await tcsDisconnected.Task.Timeout(1);
            await Task.Delay(150);
            var result = await pipe.Output.WriteAsync(Data).Timeout(1);
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe();

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).AsTask()
            .Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        await cNexus.Context.DisconnectAsync().Timeout(1);
        tcsDisconnected.SetResult();

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Server_PipeReaderCompletesUponWriterCompletion(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync().Timeout(1);
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe();
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        await pipe.Output.CompleteAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Server_PipeWriterCompletesUponWriterCompletion(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
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

        var pipe = cNexus.Context.CreatePipe();

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        await pipe.Input.CompleteAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Server_PipeWriterRemainsOpenUponOtherWriterCompletion(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync().Timeout(1);
            Assert.IsTrue(result.IsCompleted);

            await pipe.Output.WriteAsync(Data).Timeout(1);
            await Task.Delay(10000);
        };

        var pipe = cNexus.Context.CreatePipe();

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        await pipe.Output.CompleteAsync().Timeout(1);

        var result = await pipe.Input.ReadAsync().Timeout(1);
        Assert.AreEqual(Data, result.Buffer.ToArray());
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Server_PipeReaderRemainsOpenUponOtherReaderCompletion(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);
        var outputComplete = new TaskCompletionSource();

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await outputComplete.Task.Timeout(1);
            await Task.Delay(50);
            var result = await pipe.Output.WriteAsync(Data).Timeout(1);
            Assert.IsTrue(result.IsCompleted);

            await pipe.Input.ReadAsync();
            tcs.SetResult();

            var readResult = await pipe.Input.ReadAsync().Timeout(1);
            Assert.AreEqual(Data, readResult.Buffer.ToArray());
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe();

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);

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
    public async Task Server_PipeNotifiesWhenReady(Type type)
    {
        var (_, sNexus, _, cNexus, _) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await Task.Delay(10000);
        };

        var pipe = cNexus.Context.CreatePipe();

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1).Timeout(1);
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Server_PipeReadyCancelsOnDisconnection(Type type)
    {
        var (_, sNexus, client, _, _) = await Setup(type);

        var pipe = sNexus.Context.CreatePipe();

        // Pause the receiving to test the cancellation
        client.Config.InternalOnReceive = async (session, sequence) =>
        {
            await sNexus.Context.DisconnectAsync().Timeout(1);
            await Task.Delay(100000);
        };

        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(1);

        await AssertThrows<TaskCanceledException>(async () => await pipe.ReadyTask).Timeout(1);
    }

    [Test]
    public async Task Server_PipesThrowWhenInvokingOnMultipleConnections()
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(Type.Uds);
        await AssertThrows<InvalidOperationException>(async () =>
        {
            await sNexus.Context.Clients.All.ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe()).Timeout(1);
        }).Timeout(1);

        await AssertThrows<InvalidOperationException>(async () =>
        {
            await sNexus.Context.Clients.Others.ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe()).Timeout(1);
        }).Timeout(1);

        await AssertThrows<InvalidOperationException>(async () =>
        {
            await sNexus.Context.Clients.Clients(new long[] { 1 }).ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe()).Timeout(1);
        }).Timeout(1);

        await AssertThrows<InvalidOperationException>(async () =>
        {
            await sNexus.Context.Clients.Group("").ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe()).Timeout(1);
        }).Timeout(1);

        await AssertThrows<InvalidOperationException>(async () =>
        {
            await sNexus.Context.Clients.Groups(new string[] { "" }).ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe()).Timeout(1);
        }).Timeout(1);
    }

    [Test]
    public async Task Server_PipesAllowInvocationOnSingleConnections()
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(Type.Uds);

        await sNexus.Context.Clients.Client(1).ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe()).Timeout(1);
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe()).Timeout(1);
    }
}
