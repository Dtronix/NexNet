using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Pipes;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Pipes;

/// <summary>
/// Per-pipe credit, the session budget and two-sided close (docs/internals/flow-control.md, section 4.4 and 4.6).
/// </summary>
internal class FlowControlTests : BaseTests
{
    [Test]
    [Transports]
    public async Task BurstOfItemsWithASlowReader_ThenACall(Type type)
    {
        // The application's reproduction: 40 items of 40 KiB, read slowly, then a call on the same session. The
        // read loop used to wait for the reader, which could not take a partial item, so the whole session hung.
        const int count = 40;
        var received = 0;
        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(CreateServerConfig(type), nexus =>
        {
            nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
            {
                await foreach (var item in await ChannelReader<byte[]>(pipe))
                {
                    await Task.Delay(5);
                    if (Interlocked.Increment(ref received) == count)
                        allReceived.TrySetResult();
                }
            };
            nexus.ServerTaskValueEvent = _ => new ValueTask<int>(1);
        });
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        await using var pipe = client.CreatePipe();
        await client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
        await pipe.ReadyTask.Timeout(T(1));
        var writer = await ChannelWriter<byte[]>(pipe);
        var writing = Task.Run(async () =>
        {
            for (var i = 0; i < count; i++)
                await writer.WriteAsync(new byte[40 * 1024]);
        });

        // The call is answered while the burst is still being read.
        await Assert.That(await client.Proxy.ServerTaskValue().Timeout(T(1))).IsEqualTo(1);

        await writing.Timeout(T(10));
        await allReceived.Task.Timeout(T(10));
    }

    [Test]
    [Transports]
    public async Task OneItemLargerThanTheWindow_IsDelivered(Type type)
    {
        // The application's reproduction: a single 300 KiB item against a 256 KiB window. The reader gets an extra
        // grant from the session budget for the difference.
        var item = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(CreateServerConfig(type), nexus =>
        {
            nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
            {
                await foreach (var received in await ChannelReader<byte[]>(pipe))
                    item.TrySetResult(received.Length);
            };
            nexus.ServerTaskValueEvent = _ => new ValueTask<int>(1);
        });
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        await using var pipe = client.CreatePipe();
        await client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
        await pipe.ReadyTask.Timeout(T(1));
        var writer = await ChannelWriter<byte[]>(pipe);
        await writer.WriteAsync(new byte[300 * 1024]).Timeout(T(5));

        await Assert.That(await item.Task.Timeout(T(5))).IsEqualTo(300 * 1024);
        await Assert.That(await client.Proxy.ServerTaskValue().Timeout(T(1))).IsEqualTo(1);
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task ItemLargerThanThePeerAccepts_ThrowsAndTheChannelStaysUsable(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.MaxMessageSize = 64 * 1024;
        serverConfig.SessionBudget = 64 * 1024 * 1024;
        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(serverConfig, nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            await foreach (var value in await ChannelReader<byte[]>(pipe))
                received.TrySetResult(value.Length);
        });
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        await using var pipe = client.CreatePipe();
        await client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
        await pipe.ReadyTask.Timeout(T(1));
        var writer = await ChannelWriter<byte[]>(pipe);

        // The server accepts 64 KiB; the client checks before sending.
        await Assert.ThrowsAsync<NexusMessageTooLargeException>(async () => await writer.WriteAsync(new byte[100 * 1024]));
        await writer.WriteAsync(new byte[10]).Timeout(T(1));
        await Assert.That(await received.Task.Timeout(T(1))).IsEqualTo(10);
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp, Type.WebSocket)]
    public async Task PipeCompletedWhileThePeersBufferIsFull_SessionStaysUsable(Type type)
    {
        // The application's reproduction: the reader never reads, the writer fills the window and completes.
        var server = CreateServer(CreateServerConfig(type), nexus =>
        {
            nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) => await pipe.CompleteTask;
            nexus.ServerTaskValueEvent = _ => new ValueTask<int>(1);
        });
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        var pipe = client.CreatePipe();
        await client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
        await pipe.ReadyTask.Timeout(T(1));

        // More than the window: the write waits for credit that never comes, in the writer's own task.
        using var cts = new CancellationTokenSource(500);
        var result = await pipe.Output.WriteAsync(new byte[1024 * 1024], cts.Token).AsTask().Timeout(T(2));
        await Assert.That(result.IsCanceled).IsTrue();

        await pipe.CompleteAsync().Timeout(T(1));
        await pipe.DisposeAsync();

        await Assert.That(await client.Proxy.ServerTaskValue().Timeout(T(1))).IsEqualTo(1);
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task ClosedPipes_FreeTheirIdsAndBudget(Type type)
    {
        // More pipes than there are pipe IDs, one after another: each ID returns once both sides closed it.
        var server = CreateServer(CreateServerConfig(type), nexus =>
        {
            nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
            {
                var read = await pipe.Input.ReadAsync();
                pipe.Input.AdvanceTo(read.Buffer.End);
            };
        });
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        // Reserved for the session's lifetime: the invocation queue (InvocationCredits x 64 KiB).
        var budget = client.Session!.Budget;
        var baseline = budget.Reserved;

        for (var i = 0; i < 300; i++)
        {
            await using var pipe = client.CreatePipe();
            await client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.ReadyTask.Timeout(T(1));
            await pipe.Output.WriteAsync(new byte[] { 1 }).Timeout(T(1));
            await pipe.CompleteTask.Timeout(T(1));
        }

        // Every window reserved by the client was released.
        for (var i = 0; i < 50 && budget.Reserved != baseline; i++)
            await Task.Delay(20);
        await Assert.That(budget.Reserved).IsEqualTo(baseline);
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task OpeningMoreThanMaxPipes_IsRefusedAndTheSessionStaysUp(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.MaxPipes = 2;
        var server = CreateServer(serverConfig, nexus =>
        {
            nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) => await pipe.CompleteTask;
            nexus.ServerTaskValueEvent = _ => new ValueTask<int>(1);
        });
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        var pipes = new List<IRentedNexusDuplexPipe>();
        for (var i = 0; i < 2; i++)
        {
            var open = client.CreatePipe();
            pipes.Add(open);
            await client.Proxy.ServerTaskValueWithDuplexPipe(open).Timeout(T(1));
            await open.ReadyTask.Timeout(T(1));
        }

        // The server's limit is reached: the third open is refused.
        var refused = client.CreatePipe();
        await client.Proxy.ServerTaskValueWithDuplexPipe(refused).Timeout(T(1));
        await Assert.ThrowsExactlyAsync<NexusBusyException>(async () => await refused.ReadyTask.Timeout(T(1)));
        await refused.DisposeAsync();

        await Assert.That(await client.Proxy.ServerTaskValue().Timeout(T(1))).IsEqualTo(1);
        foreach (var pipe in pipes)
            await pipe.DisposeAsync();
    }
}