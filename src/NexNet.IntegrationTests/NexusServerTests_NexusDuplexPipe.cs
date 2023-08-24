using System.Buffers;
using System.IO.Pipelines;
using NUnit.Framework;

namespace NexNet.IntegrationTests;

internal class NexusServerTests_NexusDuplexPipe : BasePipeTests
{

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task PipeReaderReceivesDataMultipleTimes(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);
        int count = 0;

        // Ensure that the ids will properly wrap around.
        const int iterations = 400;
        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();

            // If the connection is still alive, the buffer should contain the data.
            if (!result.IsCompleted)
            {
                Assert.AreEqual(Data, result.Buffer.ToArray());
            }

            if (++count == iterations)
                tcs.SetResult();
        };

        for (int i = 0; i < iterations; i++)
        {
            await Task.Delay(1);
            await using var pipe = cNexus.Context.CreatePipe();
            await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe);
            await pipe.ReadyTask;
            await pipe.Output.WriteAsync(Data);
        }


        await tcs.Task.Timeout(20);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task PipeReaderReceivesData(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();
            Assert.AreEqual(Data, result.Buffer.ToArray());
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe();
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe);

        await pipe.ReadyTask;
        await pipe.Output.WriteAsync(Data);

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task PipeWriterSendsData(Type type)
    {
        var (_, sNexus, _, cNexus, _) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await pipe.Output.WriteAsync(Data);
            await Task.Delay(10000);
        };

        var pipe = cNexus.Context.CreatePipe();
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe);

        await pipe.ReadyTask;
        var result = await pipe.Input.ReadAsync();
        Assert.AreEqual(Data, result.Buffer.ToArray());
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task PipeReaderCompletesUponCompleteAsync(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe();
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe);

        await pipe.ReadyTask;
        await pipe.CompleteAsync();

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task PipeWriterCompletesUponCompleteAsync(Type type)
    {
        //Console.WriteLine("Starting test");
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);
        var completedTcs = new TaskCompletionSource();

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            // Wait for the client to complete the pipe
            await completedTcs.Task;

            FlushResult result = default;
            for (int i = 0; i < 20; i++)
            {
                result = await pipe.Output.WriteAsync(Data);

                //Console.WriteLine($"Result Comp:{result.IsCompleted}, Can:{result.IsCanceled}");
                if (result.IsCompleted)
                    break;

                await Task.Delay(100);
            }

            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe();
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe);

        await pipe.ReadyTask;
        await pipe.CompleteAsync();
        completedTcs.SetResult();

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task PipeReaderCompletesUponDisconnection(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe();
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).AsTask()
            .Timeout(1);

        await pipe.ReadyTask;
        await Task.Delay(100);
        await sNexus.Context.DisconnectAsync();

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task PipeWriterCompletesUponDisconnection(Type type)
    {
        var tcsDisconnected = new TaskCompletionSource();
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await tcsDisconnected.Task;
            await Task.Delay(150);
            var result = await pipe.Output.WriteAsync(Data);
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe();

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).AsTask()
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
    public async Task PipeReaderCompletesUponWriterCompletion(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe();
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe);

        await pipe.ReadyTask;
        await pipe.Output.CompleteAsync();

        await tcs.Task.Timeout(1);
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task PipeWriterCompletesUponWriterCompletion(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
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

        var pipe = cNexus.Context.CreatePipe();

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe);

        await pipe.ReadyTask;
        await pipe.Input.CompleteAsync();

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task PipeWriterRemainsOpenUponOtherWriterCompletion(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();
            Assert.IsTrue(result.IsCompleted);

            await pipe.Output.WriteAsync(Data);
            await Task.Delay(10000);
        };

        var pipe = cNexus.Context.CreatePipe();

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe);

        await pipe.ReadyTask;
        await pipe.Output.CompleteAsync();

        var result = await pipe.Input.ReadAsync();
        Assert.AreEqual(Data, result.Buffer.ToArray());
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task PipeReaderRemainsOpenUponOtherReaderCompletion(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);
        var outputComplete = new TaskCompletionSource();

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await outputComplete.Task.Timeout(1);
            await Task.Delay(50);
            var result = await pipe.Output.WriteAsync(Data);
            Assert.IsTrue(result.IsCompleted);

            await pipe.Input.ReadAsync();
            tcs.SetResult();

            var readResult = await pipe.Input.ReadAsync();
            Assert.AreEqual(Data, readResult.Buffer.ToArray());
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe();

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe);

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
    public async Task PipeNotifiesWhenReady(Type type)
    {
        var (_, sNexus, _, cNexus, _) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await Task.Delay(10000);
        };

        var pipe = cNexus.Context.CreatePipe();

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe);

        await pipe.ReadyTask.Timeout(1);
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task PipeReadyCancelsOnDisconnection(Type type)
    {
        var (_, sNexus, client, _, _) = await Setup(type);

        var pipe = sNexus.Context.CreatePipe();

        // Pause the receiving to test the cancellation
        client.Config.InternalOnReceive = async (session, sequence) =>
        {
            await sNexus.Context.DisconnectAsync();
            await Task.Delay(100000);
        };

        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe);

        await AssertThrows<TaskCanceledException>(async () => await pipe.ReadyTask).Timeout(1);
    }

    [Test]
    public async Task PipesThrowWhenInvokingOnMultipleConnections()
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(Type.Uds);
        await AssertThrows<InvalidOperationException>(async () =>
        {
            await sNexus.Context.Clients.All.ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe());
        });

        await AssertThrows<InvalidOperationException>(async () =>
        {
            await sNexus.Context.Clients.Others.ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe());
        });

        await AssertThrows<InvalidOperationException>(async () =>
        {
            await sNexus.Context.Clients.Clients(new long[]{1}).ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe());
        });

        await AssertThrows<InvalidOperationException>(async () =>
        {
            await sNexus.Context.Clients.Group("").ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe());
        });

        await AssertThrows<InvalidOperationException>(async () =>
        {
            await sNexus.Context.Clients.Groups(new string[]{ ""}).ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe());
        });
    }

    [Test]
    public async Task PipesAllowInvocationOnSingleConnections()
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(Type.Uds);

        await sNexus.Context.Clients.Client(1).ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe());
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe());
    }
}
