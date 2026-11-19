using System.Buffers;
using System.IO.Pipelines;
using NexNet.Messages;
using NexNet.Pipes;

namespace NexNet.IntegrationTests.Pipes;

internal class NexusServerTests_NexusDuplexPipe : BasePipeTests
{
    [Test]
    [Transports]
    public async Task Server_PipeReaderReceivesDataMultipleTimes(Type type)
    {
        var (server, _, cNexus, tcs) = await Setup(type);
        var count = 0;

        const int iterations = 1000;
        server.NexusCreatedQueue.First().ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync().Timeout(T(1));

            // If the connection is still alive, the buffer should contain the data.
            if (!result.IsCompleted)
            {
                await Assert.That(result.Buffer.ToArray()).IsEquivalentTo(Data, CollectionOrdering.Matching);
            }

            if (Interlocked.Increment(ref count) == iterations)
                tcs.SetResult();
        };

        for (var i = 0; i < iterations; i++)
        {
            await using var pipe = cNexus.Context.CreatePipe();
            await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.ReadyTask.Timeout(T(1));
            await pipe.Output.WriteAsync(Data).Timeout(T(1));
        }

        await tcs.Task.Timeout(T(1));
    }
    
    [Test]
    [Transports]
    public async Task Server_PipeReaderReceivesDataMultipleTimesWithLargeData(Type type)
    {
        var (server, _, cNexus, tcs) = await Setup(type);
        var count = 0;
        var largeData = new byte[1024 * 32];
        // TODO: Review adding a test for increased iterations as this has been found to sometimes fail on CI.
        const int iterations = 100;
        server.NexusCreatedQueue.First().ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            bool complete = false;
            do
            {
                var result = await pipe.Input.ReadAsync().Timeout(T(1));
                pipe.Input.AdvanceTo(result.Buffer.End);
                complete = result.IsCompleted;
                
            } while (!complete);
            
            if (Interlocked.Increment(ref count) == iterations)
                tcs.SetResult();
            
        };
        
        for (var i = 0; i < iterations; i++)
        {
            await using var pipe = cNexus.Context.CreatePipe();
            await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.ReadyTask.Timeout(T(1));
            await pipe.Output.WriteAsync(largeData).Timeout(T(1));
            await pipe.CompleteAsync();
        }

        await tcs.Task.Timeout(T(1));
    }
    
    [Test]
    [Transports]
    public async Task Server_PipeReaderCreatesAndDestroysPipeMultipleTimes(Type type)
    {
        var (server, _, cNexus, tcs) = await Setup(type);
        var count = 0;

        // TODO: Review adding a test for increased iterations as this has been found to sometimes fail on CI.
        const int iterations = 50;
        server.NexusCreatedQueue.First().ServerTaskValueWithDuplexPipeEvent = (nexus, pipe) =>
        {
            if (Interlocked.Increment(ref count) == iterations)
                tcs.SetResult();

            return ValueTask.CompletedTask;
        };

        for (var i = 0; i < iterations; i++)
        {
            await using var pipe = cNexus.Context.CreatePipe();
            await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.ReadyTask.Timeout(T(1));
        }

        await tcs.Task.Timeout(T(2));
    }

    [Test]
    [Transports]
    public async Task Server_PipeReaderReceivesData(Type type)
    {
        var (server, _, cNexus, tcs) = await Setup(type);
        server.NexusCreatedQueue.First().ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync().Timeout(T(1));
            await Assert.That(result.Buffer.ToArray()).IsEquivalentTo(Data, CollectionOrdering.Matching);
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe();
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));

        await pipe.ReadyTask.Timeout(T(1));
        await pipe.Output.WriteAsync(Data).Timeout(T(1));

        await tcs.Task.Timeout(T(1)).Timeout(T(1));
    }

    [Test]
    [Transports]
    public async Task Server_PipeWriterSendsData(Type type)
    {
        var (server, _, cNexus, _) = await Setup(type);
        server.NexusCreatedQueue.First().ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await pipe.Output.WriteAsync(Data).Timeout(T(1));
            await Task.Delay(10000);
        };

        var pipe = cNexus.Context.CreatePipe();
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));

        await pipe.ReadyTask.Timeout(T(1));
        var result = await pipe.Input.ReadAsync().Timeout(T(1));
        await Assert.That(result.Buffer.ToArray()).IsEquivalentTo(Data, CollectionOrdering.Matching);
    }

    [Test]
    [Transports]
    public async Task Server_PipeReaderCompletesUponCompleteAsync(Type type)
    {
        var (server, _, cNexus, tcs) = await Setup(type);

        server.NexusCreatedQueue.First().ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync().Timeout(T(1));
            await Assert.That(result.IsCompleted).IsTrue();
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe();
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));

        await pipe.ReadyTask.Timeout(T(1));
        await pipe.CompleteAsync().Timeout(T(1));

        await tcs.Task.Timeout(T(1));
    }

    [Test]
    [Transports]
    public async Task Server_PipeWriterCompletesUponCompleteAsync(Type type)
    {
        var (server, _, cNexus, tcs) = await Setup(type);
        var completedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        server.NexusCreatedQueue.First().ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            // Wait for the client to complete the pipe
            await completedTcs.Task.Timeout(T(1));

            FlushResult result = default;
            for (var i = 0; i < 20; i++)
            {
                result = await pipe.Output.WriteAsync(Data).Timeout(T(1));

                if (result.IsCompleted)
                    break;

                await Task.Delay(100);
            }

            await Assert.That(result.IsCompleted).IsTrue();
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe();
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));

        await pipe.ReadyTask.Timeout(T(1));
        await pipe.CompleteAsync().Timeout(T(1));
        completedTcs.SetResult();

        await tcs.Task.Timeout(T(1));
    }

    [Test]
    [Transports]
    public async Task Server_PipeReaderCompletesUponDisconnection(Type type)
    {
        var (server, _, cNexus, tcs) = await Setup(type);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        server.NexusCreatedQueue.First().ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            reading.TrySetResult();
            var result = await pipe.Input.ReadAsync();
            await Assert.That(result.IsCompleted).IsTrue();
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe();
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));

        await pipe.ReadyTask.Timeout(T(1));
        await reading.Task.Timeout(T(1));
        await server.NexusCreatedQueue.First().Context.DisconnectAsync().Timeout(T(1));

        await tcs.Task.Timeout(T(1));
    }

    [Test]
    [Transports]
    public async Task Server_PipeWriterCompletesUponDisconnection(Type type)
    {
        var tcsDisconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, _, cNexus, tcs) = await Setup(type);

        server.NexusCreatedQueue.First().ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await tcsDisconnected.Task.Timeout(T(1));
            await Task.Delay(150);
            var result = await pipe.Output.WriteAsync(Data).Timeout(T(1));
            await Assert.That(result.IsCompleted).IsTrue();
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe();

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).AsTask()
            .Timeout(T(1));

        await pipe.ReadyTask.Timeout(T(1));
        await cNexus.Context.DisconnectAsync().Timeout(T(1));
        tcsDisconnected.SetResult();

        await tcs.Task.Timeout(T(1));
    }

    [Test]
    [Transports]
    public async Task Server_PipeReaderCompletesUponWriterCompletion(Type type)
    {
        var (server, _, cNexus, tcs) = await Setup(type);

        server.NexusCreatedQueue.First().ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync().Timeout(T(1));
            await Assert.That(result.IsCompleted).IsTrue();
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe();
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));

        await pipe.ReadyTask.Timeout(T(1));
        await pipe.Output.CompleteAsync().Timeout(T(1));

        await tcs.Task.Timeout(T(1));
    }


    [Test]
    [Transports]
    public async Task Server_PipeWriterCompletesUponWriterCompletion(Type type)
    {
        var (server, _, cNexus, tcs) = await Setup(type);

        server.NexusCreatedQueue.First().ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            FlushResult? result = null;
            while (result == null || !result.Value.IsCompleted)
            {
                result = await pipe.Output.WriteAsync(Data).Timeout(T(1));
                await Task.Delay(1);
            }

            await Assert.That(result.Value.IsCompleted).IsTrue();
            tcs.SetResult();

        };

        var pipe = cNexus.Context.CreatePipe();

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));

        await pipe.ReadyTask.Timeout(T(1));
        await pipe.Input.CompleteAsync().Timeout(T(1));

        await tcs.Task.Timeout(T(1));
    }

    [Test]
    [Transports]
    public async Task Server_PipeWriterRemainsOpenUponOtherWriterCompletion(Type type)
    {
        var (server, _, cNexus, tcs) = await Setup(type);

        server.NexusCreatedQueue.First().ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync().Timeout(T(1));
            await Assert.That(result.IsCompleted).IsTrue();

            await pipe.Output.WriteAsync(Data).Timeout(T(1));
            await Task.Delay(10000);
        };

        var pipe = cNexus.Context.CreatePipe();

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));

        await pipe.ReadyTask.Timeout(T(1));
        await pipe.Output.CompleteAsync().Timeout(T(1));

        var result = await pipe.Input.ReadAsync().Timeout(T(1));
        await Assert.That(result.Buffer.ToArray()).IsEquivalentTo(Data, CollectionOrdering.Matching);
    }

    [Test]
    [Transports]
    public async Task Server_PipeReaderRemainsOpenUponOtherReaderCompletion(Type type)
    {
        var (server, _, cNexus, tcs) = await Setup(type);
        var outputComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        server.NexusCreatedQueue.First().ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await outputComplete.Task.Timeout(T(1));
            await Task.Delay(50);
            var result = await pipe.Output.WriteAsync(Data).Timeout(T(1));
            await Assert.That(result.IsCompleted).IsTrue();

            await pipe.Input.ReadAsync();
            tcs.SetResult();

            var readResult = await pipe.Input.ReadAsync().Timeout(T(1));
            await Assert.That(readResult.Buffer.ToArray()).IsEquivalentTo(Data, CollectionOrdering.Matching);
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe();

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));

        await pipe.ReadyTask.Timeout(T(1));
        await pipe.Input.CompleteAsync().Timeout(T(1));
        outputComplete.TrySetResult();

        await pipe.Output.WriteAsync(Data).Timeout(T(1));

        await tcs.Task.Timeout(T(1));
    }

    [Test]
    [Transports]
    public async Task Server_PipeNotifiesWhenReady(Type type)
    {
        var (server, _, cNexus, _) = await Setup(type);

        server.NexusCreatedQueue.First().ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await Task.Delay(10000);
        };

        var pipe = cNexus.Context.CreatePipe();

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));

        await pipe.ReadyTask.Timeout(T(1)).Timeout(T(1));
    }


    [Test]
    [Transports]
    public async Task Server_PipeReadyCancelsOnDisconnection(Type type)
    {
        var (server, client, _, _) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();

        var pipe = sNexus.Context.CreatePipe();

        // The server holds the client's pipe-ready answer and disconnects, so the pipe never becomes ready on the
        // server.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ServerTaps.OnReceive(server.Server.Config, async (session, frame) =>
        {
            if (frame.Type != MessageType.DuplexPipeUpdateState)
                return;

            _ = sNexus.Context.DisconnectAsync();
            await release.Task;
        });

        try
        {
            await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(T(1));

            await AssertThrows<TaskCanceledException>(async () => await pipe.ReadyTask).Timeout(T(1));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Test]
    [Transports]
    public async Task Server_PipeCompleteCancelsOnDisconnection(Type type)
    {
        var (server, client, _, _) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();

        var pipe = sNexus.Context.CreatePipe();

        // The server holds the client's pipe-ready answer and disconnects, so the pipe never becomes ready on the
        // server.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ServerTaps.OnReceive(server.Server.Config, async (session, frame) =>
        {
            if (frame.Type != MessageType.DuplexPipeUpdateState)
                return;

            _ = sNexus.Context.DisconnectAsync();
            await release.Task;
        });

        try
        {
            await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(T(1));

            await AssertThrows<TaskCanceledException>(async () => await pipe.CompleteTask).Timeout(T(1));
        }
        finally
        {
            release.TrySetResult();
        }
    }


    [Test]
    [Transports]
    public async Task Server_PipeNotifiesWhenComplete(Type type)
    {
        var (server, _, cNexus, _) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();

        bool completedInvocation = false;
        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await Task.Delay(100);
            completedInvocation = true;
        };

        var pipe = sNexus.Context.CreatePipe();

        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(T(1));
        await pipe.ReadyTask.Timeout(T(1));

        await pipe.CompleteTask.Timeout(T(1));
        await Assert.That(completedInvocation).IsTrue();
    }

    [Test]
    public async Task Server_PipesThrowWhenInvokingOnMultipleConnections()
    {
        var (server, _, cNexus, tcs) = await Setup(Type.Uds);
        var sNexus = server.NexusCreatedQueue.First();
        await AssertThrows<InvalidOperationException>(async () =>
        {
            await sNexus.Context.Clients.All.ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe()).Timeout(T(1));
        }).Timeout(T(1));

        await AssertThrows<InvalidOperationException>(async () =>
        {
            await sNexus.Context.Clients.Others.ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe()).Timeout(T(1));
        }).Timeout(T(1));

        await AssertThrows<InvalidOperationException>(async () =>
        {
            await sNexus.Context.Clients.Clients(new long[] { 1 }).ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe()).Timeout(T(1));
        }).Timeout(T(1));

        await AssertThrows<InvalidOperationException>(async () =>
        {
            await sNexus.Context.Clients.Group("").ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe()).Timeout(T(1));
        }).Timeout(T(1));

        await AssertThrows<InvalidOperationException>(async () =>
        {
            await sNexus.Context.Clients.Groups(new string[] { "" }).ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe()).Timeout(T(1));
        }).Timeout(T(1));
    }

    [Test]
    public async Task Server_PipesAllowInvocationOnSingleConnections()
    {
        var (server, _, cNexus, tcs) = await Setup(Type.Uds);
        var sNexus = server.NexusCreatedQueue.First();
        await sNexus.Context.Clients.Client(1).ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe()).Timeout(T(1));
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe()).Timeout(T(1));
    }

    [Test]
    [Transports]
    public async Task Server_ThrowsWhenPassingPipeFromWrongNexus(Type type)
    {
        var (server, _, cNexus, _) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();
        cNexus.ClientTaskValueWithDuplexPipeEvent = (nexus, pipe) => ValueTask.CompletedTask;

        var pipe = cNexus.Context.CreatePipe();

        await AssertThrows<InvalidOperationException>(() =>
            sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(T(1))).Timeout(T(1));
    }


    [Test]
    [Transports]
    public async Task Server_ThrowsWhenPassingUsedPipe(Type type)
    {
        var (server, _, cNexus, _) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();

        cNexus.ClientTaskValueWithDuplexPipeEvent = (nexus, pipe) => ValueTask.CompletedTask;

        var pipe = sNexus.Context.CreatePipe();
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(T(1));
        await pipe.CompleteTask.Timeout(T(1));

        await AssertThrows<InvalidOperationException>(() =>
            sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(T(1))).Timeout(T(1));
    }
}