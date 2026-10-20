using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Pipes;
using NUnit.Framework;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Pipes;

/// <summary>
/// Per-pipe credit, the session budget and two-sided close (docs/internals/flow-control-v2.md, section 4.4 and 4.6).
/// </summary>
internal class FlowControlTests : BaseTests
{
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
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
                await foreach (var item in await pipe.GetChannel<byte[]>().GetReaderAsync())
                {
                    await Task.Delay(5);
                    if (Interlocked.Increment(ref received) == count)
                        allReceived.TrySetResult();
                }
            };
            nexus.ServerTaskValueEvent = _ => new ValueTask<int>(1);
        });
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(1);

        await using var pipe = client.CreatePipe();
        await client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);
        await pipe.ReadyTask.Timeout(1);
        var writer = await pipe.GetChannel<byte[]>().GetWriterAsync();
        var writing = Task.Run(async () =>
        {
            for (var i = 0; i < count; i++)
                await writer.WriteAsync(new byte[40 * 1024]);
        });

        // The call is answered while the burst is still being read.
        Assert.That(await client.Proxy.ServerTaskValue().Timeout(1), Is.EqualTo(1));

        await writing.Timeout(10);
        await allReceived.Task.Timeout(10);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.WebSocket)]
    public async Task PipeCompletedWhileThePeersBufferIsFull_SessionStaysUsable(Type type)
    {
        // The application's reproduction: the reader never reads, the writer fills the window and completes.
        var server = CreateServer(CreateServerConfig(type), nexus =>
        {
            nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) => await pipe.CompleteTask;
            nexus.ServerTaskValueEvent = _ => new ValueTask<int>(1);
        });
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(1);

        var pipe = client.CreatePipe();
        await client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);
        await pipe.ReadyTask.Timeout(1);

        // More than the window: the write waits for credit that never comes, in the writer's own task.
        using var cts = new CancellationTokenSource(500);
        var result = await pipe.Output.WriteAsync(new byte[1024 * 1024], cts.Token).AsTask().Timeout(2);
        Assert.That(result.IsCanceled, Is.True);

        await pipe.CompleteAsync().Timeout(1);
        await pipe.DisposeAsync();

        Assert.That(await client.Proxy.ServerTaskValue().Timeout(1), Is.EqualTo(1));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
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
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(1);

        for (var i = 0; i < 300; i++)
        {
            await using var pipe = client.CreatePipe();
            await client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);
            await pipe.ReadyTask.Timeout(1);
            await pipe.Output.WriteAsync(new byte[] { 1 }).Timeout(1);
            await pipe.CompleteTask.Timeout(1);
        }

        // Every window reserved by the client was released.
        var budget = client.Session!.Budget;
        for (var i = 0; i < 50 && budget.Reserved != 0; i++)
            await Task.Delay(20);
        Assert.That(budget.Reserved, Is.Zero);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task OpeningMoreThanMaxPipes_IsRefusedAndTheSessionStaysUp(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.MaxPipes = 2;
        var server = CreateServer(serverConfig, nexus =>
        {
            nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) => await pipe.CompleteTask;
            nexus.ServerTaskValueEvent = _ => new ValueTask<int>(1);
        });
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(1);

        var pipes = new List<IRentedNexusDuplexPipe>();
        for (var i = 0; i < 2; i++)
        {
            var open = client.CreatePipe();
            pipes.Add(open);
            await client.Proxy.ServerTaskValueWithDuplexPipe(open).Timeout(1);
            await open.ReadyTask.Timeout(1);
        }

        // The server's limit is reached: the third open is refused.
        var refused = client.CreatePipe();
        await client.Proxy.ServerTaskValueWithDuplexPipe(refused).Timeout(1);
        await Assert.ThrowsAsync<NexusBusyException>(async () => await refused.ReadyTask.Timeout(1));
        await refused.DisposeAsync();

        Assert.That(await client.Proxy.ServerTaskValue().Timeout(1), Is.EqualTo(1));
        foreach (var pipe in pipes)
            await pipe.DisposeAsync();
    }
}
