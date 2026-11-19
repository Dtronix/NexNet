using System.Buffers;
using System.IO.Pipelines;

namespace NexNet.IntegrationTests.Pipes;

internal class NexusClientTests_NexusDuplexPipe : BasePipeTests
{
    [Test]
    [Transports]
    public async Task Client_PipeReceivesFullFrames(Type type)
    {
        // 4 MiB in maximum-size frames: exceeds the transport's receive pause threshold, the pipe's high watermark
        // and its cutoff, so every one of them has to resume.
        var (server, _, cNexus, tcs) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();
        const int total = 4 * 1024 * 1024;
        long received = 0;

        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            while (true)
            {
                var result = await pipe.Input.ReadAsync().Timeout(5);
                received += result.Buffer.Length;
                pipe.Input.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted || received >= total)
                    break;
            }

            tcs.SetResult();
        };

        await using var pipe = sNexus.Context.CreatePipe();
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(1);
        await pipe.ReadyTask.Timeout(1);
        await pipe.Output.WriteAsync(new byte[total]).Timeout(5);

        await tcs.Task.Timeout(10);
        await Assert.That(received).IsEqualTo(total);
    }

    [Test]
    [Transports]
    public async Task Client_PipeReaderReceivesDataMultipleTimes(Type type)
    {
        var (server, _, cNexus, tcs) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();
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
                await Assert.That(result.Buffer.ToArray()).IsEquivalentTo(Data, CollectionOrdering.Matching);
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

    [Test]
    [Transports]
    public async Task Client_PipeReaderReceivesDataMultipleTimesWithLargeData(Type type)
    {
        var (server, _, cNexus, tcs) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();
        int count = 0;
        var largeData = new byte[1024 * 32];
        // 1000 round trips, each step bounded: under a loaded parallel run one step can stall for over a second, so
        // the bounds catch a hang rather than a slow step.
        const int iterations = 1000;
        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAtLeastAsync(largeData.Length).Timeout(5);
            pipe.Input.AdvanceTo(result.Buffer.End);
            await pipe.CompleteAsync();

            if (Interlocked.Increment(ref count) == iterations)
                tcs.SetResult();
        };

        for (int i = 0; i < iterations; i++)
        {
            await using var pipe = sNexus.Context.CreatePipe();
            await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(5);
            await pipe.ReadyTask.Timeout(5);
            await pipe.Output.WriteAsync(largeData).Timeout(5);
            await pipe.CompleteTask.Timeout(5);
        }

        await tcs.Task.Timeout(10);
    }

    [Test]
    [Transports]
    public async Task Client_PipeReaderReceivesData(Type type)
    {
        var (server, _, cNexus, tcs) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();
        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync().Timeout(1);
            await Assert.That(result.Buffer.ToArray()).IsEquivalentTo(Data, CollectionOrdering.Matching);
            tcs.SetResult();
        };

        var pipe = sNexus.Context.CreatePipe();

        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe!).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        await pipe.Output.WriteAsync(Data).Timeout(1);

        await tcs.Task.Timeout(1);
    }

    [Test]
    [Transports]
    public async Task Client_PipeWriterSendsData(Type type)
    {
        var (server, _, cNexus, _) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();
        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await pipe.Output.WriteAsync(Data).Timeout(1);
            await Task.Delay(10000);
        };

        var pipe = sNexus.Context.CreatePipe();
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        var result = await pipe.Input.ReadAsync().Timeout(1);
        await Assert.That(result.Buffer.ToArray()).IsEquivalentTo(Data, CollectionOrdering.Matching);
    }

    [Test]
    [Transports]
    public async Task Client_PipeReaderCompletesUponPipeCompleteAsync(Type type)
    {
        var (server, _, cNexus, tcs) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();
        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync().Timeout(1);
            await Assert.That(result.IsCompleted).IsTrue();
            tcs.SetResult();
        };

        var pipe = sNexus.Context.CreatePipe();
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        await pipe.CompleteAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }

    [Test]
    [Transports]
    public async Task Client_PipeWriterCompletesUponCompleteAsync(Type type)
    {
        var (server, _, cNexus, tcs) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();
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

            await Assert.That(result.IsCompleted).IsTrue();
            tcs.SetResult();
        };

        var pipe = sNexus.Context.CreatePipe();
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        await pipe.CompleteAsync().Timeout(1);

        await tcs.Task.Timeout(3);
    }

    [Test]
    [Transports]
    public async Task Client_PipeReaderCompletesUponDisconnection(Type type)
    {
        var (server, _, cNexus, tcs) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();
        
        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync().Timeout(1);
            await Assert.That(result.IsCompleted).IsTrue();
            tcs.SetResult();
        };

        var pipe = sNexus.Context.CreatePipe();

        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        await cNexus.Context.DisconnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }

    [Test]
    [Transports]
    public async Task Client_PipeWriterCompletesUponDisconnection(Type type)
    {
        var tcsDisconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, _, cNexus, tcs) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();
        
        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await tcsDisconnected.Task.Timeout(1);
            await Task.Delay(150);
            var result = await pipe.Output.WriteAsync(Data).Timeout(1);
            await Assert.That(result.IsCompleted).IsTrue();
            tcs.SetResult();
        };

        var pipe = sNexus.Context.CreatePipe();

        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        await cNexus.Context.DisconnectAsync().Timeout(1);
        tcsDisconnected.SetResult();

        await tcs.Task.Timeout(1);
    }

    [Test]
    [Transports]
    public async Task Client_PipeReaderCompletesUponWriterCompletion(Type type)
    {
        var (server, _, cNexus, tcs) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();
        
        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync().Timeout(1);
            await Assert.That(result.IsCompleted).IsTrue();
            tcs.SetResult();
        };

        var pipe = sNexus.Context.CreatePipe();

        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        await pipe.Output.CompleteAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }


    [Test]
    [Transports]
    public async Task Client_PipeWriterCompletesUponWriterCompletion(Type type)
    {
        var (server, _, cNexus, tcs) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();
        
        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            FlushResult? result = null;
            while (result == null || !result.Value.IsCompleted)
            {
                result = await pipe.Output.WriteAsync(Data).Timeout(1);
                await Task.Delay(1);
            }

            await Assert.That(result.Value.IsCompleted).IsTrue();
            tcs.SetResult();

        };

        var pipe = sNexus.Context.CreatePipe();
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        await pipe.Input.CompleteAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }

    [Test]
    [Transports]
    public async Task Client_PipeWriterRemainsOpenUponOtherWriterCompletion(Type type)
    {
        var (server, _, cNexus, _) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();
        
        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync().Timeout(1);
            await Assert.That(result.IsCompleted).IsTrue();

            await pipe.Output.WriteAsync(Data).Timeout(1);
            await Task.Delay(1000);
        };

        var pipe = sNexus.Context.CreatePipe();
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
        await pipe.Output.CompleteAsync().Timeout(1);

        var result = await pipe.Input.ReadAsync().Timeout(1);
        await Assert.That(result.Buffer.ToArray()).IsEquivalentTo(Data, CollectionOrdering.Matching);
    }

    [Test]
    [Transports]
    public async Task Client_PipeReaderRemainsOpenUponOtherReaderCompletion(Type type)
    {
        var (server, _, cNexus, tcs) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();
        
        var outputComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await outputComplete.Task.Timeout(1);
            await Task.Delay(50);
            var result = await pipe.Output.WriteAsync(Data).Timeout(1);
            await Assert.That(result.IsCompleted).IsTrue();

            var buffer = await pipe.Input.ReadAsync().Timeout(1);
            pipe.Input.AdvanceTo(buffer.Buffer.Start);

            var readResult = await pipe.Input.ReadAsync().Timeout(1);
            await Assert.That(readResult.Buffer.ToArray()).IsEquivalentTo(Data, CollectionOrdering.Matching);
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

    [Test]
    [Transports]
    public async Task Client_PipeNotifiesWhenReady(Type type)
    {
        var (server, _, cNexus, _) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();
        
        cNexus.ClientTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await Task.Delay(10000);
        };

        var pipe = sNexus.Context.CreatePipe();

        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(1);

        await pipe.ReadyTask.Timeout(1);
    }


    [Test]
    [Transports]
    public async Task Client_PipeReadyCancelsOnDisconnection(Type type)
    {
        var (server, client, _, _) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();
        var pipe = client.CreatePipe();

        // Pause the receiving to test the cancellation
        server.Server.Config.InternalOnReceive = async (session, sequence) =>
        {
            await client.DisconnectAsync().Timeout(1);
            await Task.Delay(100000);
        };

        await client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);

        await AssertThrows<TaskCanceledException>(async () => await pipe.ReadyTask).Timeout(2);
    }

    [Test]
    [Transports]
    public async Task Client_PipeCompleteCancelsOnDisconnection(Type type)
    {
        var (server, client, _, _) = await Setup(type);

        var pipe = client.CreatePipe();

        // Pause the receiving to test the cancellation
        server.Server.Config.InternalOnReceive = async (session, sequence) =>
        {
            await client.DisconnectAsync().Timeout(1);
            await Task.Delay(100000);
        };

        await client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);

        await AssertThrows<TaskCanceledException>(async () => await pipe.CompleteTask).Timeout(2);
    }

    [Test]
    [Transports]
    public async Task Client_PipeNotifiesWhenComplete(Type type)
    {
        var (server, client, _, _) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();
        
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
        await Assert.That(completedInvocation).IsTrue();
    }

    [Test]
    [Transports]
    public async Task Client_ThrowsWhenPassingPipeFromWrongNexus(Type type)
    {
        var (server, client, _, _) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();
        
        sNexus.ServerTaskValueWithDuplexPipeEvent = (nexus, pipe) => ValueTask.CompletedTask;

        var pipe = sNexus.Context.CreatePipe();

        await AssertThrows<InvalidOperationException>(() =>
            client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1)).Timeout(1);
    }


    [Test]
    [Transports]
    public async Task Client_ThrowsWhenPassingUsedPipe(Type type)
    {
        var (server, client, _, _) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();

        sNexus.ServerTaskValueWithDuplexPipeEvent = (nexus, pipe) => ValueTask.CompletedTask;

        var pipe = client.CreatePipe();
        await client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);
        await pipe.CompleteTask.Timeout(1);

        await AssertThrows<InvalidOperationException>(() =>
            client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1)).Timeout(1);
    }



}