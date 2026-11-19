using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Internals;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.Pipes;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Pipes;

/// <summary>
/// Per-pipe credit, the session budget and two-sided close (docs/internals/flow-control.md, section 4.4 and 4.6), with
/// the client under test as the writer.
/// </summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class FlowControlTests(LanguageHostPool hosts) : PipeMatrixTests(hosts)
{
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task BurstOfItemsWithASlowReader_ThenACall(ClientTarget target)
    {
        // The application's reproduction: 40 items of 40 KiB, read slowly, then a call on the same session. The
        // read loop used to wait for the reader, which could not take a partial item, so the whole session hung.
        const int count = 40;
        var received = 0;
        var allReceived = Signal();
        await using var server = await StartServerAsync(nexus =>
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
        await using var client = await ConnectAsync(target, server);

        await using (var pipe = await client.CreatePipeAsync())
        {
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.WhenReadyAsync().AsTask().Timeout(T(1));
            var writer = await pipe.GetChannelWriterAsync<byte[]>();
            var writing = Task.Run(async () =>
            {
                for (var i = 0; i < count; i++)
                    await writer.WriteAsync(PipePattern.Create(40 * 1024, i));
            });

            // The call is answered while the burst is still being read.
            await Assert.That(await Proxy(client).ServerTaskValue().Timeout(T(1))).IsEqualTo(1);

            await writing.Timeout(T(10));
            await allReceived.Task.Timeout(T(10));
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task OneItemLargerThanTheWindow_IsDelivered(ClientTarget target)
    {
        // The application's reproduction: a single 300 KiB item against a 256 KiB window. The reader gets an extra
        // grant from the session budget for the difference.
        var item = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartServerAsync(nexus =>
        {
            nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
            {
                await foreach (var received in await ChannelReader<byte[]>(pipe))
                    item.TrySetResult(received.Length);
            };
            nexus.ServerTaskValueEvent = _ => new ValueTask<int>(1);
        });
        await using var client = await ConnectAsync(target, server);

        await using (var pipe = await client.CreatePipeAsync())
        {
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.WhenReadyAsync().AsTask().Timeout(T(1));
            var writer = await pipe.GetChannelWriterAsync<byte[]>();
            await writer.WriteAsync(PipePattern.Create(300 * 1024, 1)).AsTask().Timeout(T(5));

            await Assert.That(await item.Task.Timeout(T(5))).IsEqualTo(300 * 1024);
            await Assert.That(await Proxy(client).ServerTaskValue().Timeout(T(1))).IsEqualTo(1);
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp])]
    public async Task ItemLargerThanThePeerAccepts_ThrowsAndTheChannelStaysUsable(ClientTarget target)
    {
        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartServerAsync(
            nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
            {
                await foreach (var value in await ChannelReader<byte[]>(pipe))
                    received.TrySetResult(value.Length);
            },
            config =>
            {
                config.MaxMessageSize = 64 * 1024;
                config.SessionBudget = 64 * 1024 * 1024;
            });
        await using var client = await ConnectAsync(target, server);

        await using (var pipe = await client.CreatePipeAsync())
        {
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.WhenReadyAsync().AsTask().Timeout(T(1));
            var writer = await pipe.GetChannelWriterAsync<byte[]>();

            // The server accepts 64 KiB; the client checks before sending.
            await Assert.ThrowsExactlyAsync<NexusMessageTooLargeException>(async () => await writer.WriteAsync(new byte[100 * 1024]));
            await writer.WriteAsync(new byte[10]).AsTask().Timeout(T(1));
            await Assert.That(await received.Task.Timeout(T(1))).IsEqualTo(10);
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task PipeCompletedWhileThePeersBufferIsFull_SessionStaysUsable(ClientTarget target)
    {
        // The application's reproduction: the reader never reads, the writer fills the window and completes.
        await using var server = await StartServerAsync(nexus =>
        {
            nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) => await pipe.CompleteTask;
            nexus.ServerTaskValueEvent = _ => new ValueTask<int>(1);
        });
        await using var client = await ConnectAsync(target, server);

        var pipe = await client.CreatePipeAsync();
        await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
        await pipe.WhenReadyAsync().AsTask().Timeout(T(1));

        // More than the window: the write waits for credit that never comes, in the writer's own task.
        var writing = pipe.WritePatternAsync(1, 1024 * 1024).AsTask();
        await Assert.That(await Task.WhenAny(writing, Task.Delay(500))).IsNotSameReferenceAs(writing)
            .Because("The write must wait for credit.");

        // Closing the pipe ends the blocked write, which reports that the pipe completed.
        await pipe.DisposeAsync().AsTask().Timeout(T(1));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await writing.Timeout(T(1)));

        await Assert.That(await Proxy(client).ServerTaskValue().Timeout(T(1))).IsEqualTo(1);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp])]
    public async Task ClosedPipes_FreeTheirIdsAndBudget(ClientTarget target)
    {
        // More pipes than there are pipe IDs, one after another: each ID returns once both sides closed it.
        var serverSession = new TaskCompletionSource<INexusSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartServerAsync(
            nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
            {
                var read = await pipe.Input.ReadAsync();
                pipe.Input.AdvanceTo(read.Buffer.End);
            },
            config => ServerTaps.OnSessionCreated(config, session => serverSession.TrySetResult(session)));
        await using var client = await ConnectAsync(target, server);

        // Reserved for the session's lifetime: the invocation queue (InvocationCredits x 64 KiB).
        var serverBudget = (await serverSession.Task.Timeout(T(1))).Budget;
        var serverBaseline = serverBudget.Reserved;
        var clientBudget = target.Language == ClientLanguages.CSharp ? InProc(client).Client.Session!.Budget : null;
        var clientBaseline = clientBudget?.Reserved;

        for (var i = 0; i < 300; i++)
        {
            await using var pipe = await client.CreatePipeAsync();
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.WhenReadyAsync().AsTask().Timeout(T(1));
            await pipe.WriteAsync(new byte[] { 1 }).AsTask().Timeout(T(1));
            await pipe.WhenCompletedAsync().AsTask().Timeout(T(1));
        }

        // Every window reserved by the server (and by the C# client) was released.
        await WaitUntil(() => serverBudget.Reserved == serverBaseline && (clientBudget == null || clientBudget.Reserved == clientBaseline));
        await Assert.That(serverBudget.Reserved).IsEqualTo(serverBaseline);
        if (clientBudget != null)
            await Assert.That(clientBudget.Reserved).IsEqualTo(clientBaseline!.Value);

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp])]
    public async Task OpeningMoreThanMaxPipes_IsRefusedAndTheSessionStaysUp(ClientTarget target)
    {
        await using var server = await StartServerAsync(
            nexus =>
            {
                nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) => await pipe.CompleteTask;
                nexus.ServerTaskValueEvent = _ => new ValueTask<int>(1);
            },
            config => config.MaxPipes = 2);
        await using var client = await ConnectAsync(target, server);

        var pipes = new List<IRemotePipe>();
        for (var i = 0; i < 2; i++)
        {
            var open = await client.CreatePipeAsync();
            pipes.Add(open);
            await Proxy(client).ServerTaskValueWithDuplexPipe(open).Timeout(T(1));
            await open.WhenReadyAsync().AsTask().Timeout(T(1));
        }

        // The server's limit is reached: the third open is refused.
        var refused = await client.CreatePipeAsync();
        await Proxy(client).ServerTaskValueWithDuplexPipe(refused).Timeout(T(1));
        await Assert.ThrowsExactlyAsync<NexusBusyException>(async () => await refused.WhenReadyAsync().AsTask().Timeout(T(1)));
        await refused.DisposeAsync();

        await Assert.That(await Proxy(client).ServerTaskValue().Timeout(T(1))).IsEqualTo(1);
        foreach (var pipe in pipes)
            await pipe.DisposeAsync();

        await EndAsync(client);
    }
}
