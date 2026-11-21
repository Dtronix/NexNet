using System.Diagnostics;
using NexNet.IntegrationTests.Security;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.Invocation;
using NexNet.Messages;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Invocation credits (docs/internals/flow-control.md, section 4.7). Tests of how a client spends and waits for credit
/// run on the client matrix (P01b); tests of the server's broadcasts to raw or stalled clients stay C#-only.
/// </summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class InvocationCreditTests(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    /// <inheritdoc />
    protected override bool CompleteServerMethodsByDefault => true;

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task CallerWaitsLocallyForCredit(ClientTarget target)
    {
        // Two credits and one slot: one call runs, two wait in the server's queue, the fourth waits on the client.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = 0;
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = ArrivedInvocations(3, out var tap);
        await using var server = await StartServerAsync(
            nexus => nexus.ServerTaskValueEvent = async _ =>
            {
                Interlocked.Increment(ref received);
                running.TrySetResult();
                await release.Task;
                return 1;
            },
            config =>
            {
                config.InvocationCredits = 2;
                config.MaxConcurrentConnectionInvocations = 1;
                tap(config);
            });
        await using var client = await ConnectAsync(target, server);
        var proxy = Proxy(client);

        var calls = Enumerable.Range(0, 4).Select(_ => proxy.ServerTaskValue().AsTask()).ToArray();
        // The tap sees frames as they arrive, before the session starts running them: wait for both.
        await sent.Timeout(T(1));
        await running.Task.Timeout(T(1));

        // Only the running call has been received; the server would disconnect a client that sent beyond its credit.
        await Assert.That(Volatile.Read(ref received)).IsEqualTo(1);
        await AssertState(client, ConnectionState.Connected);

        release.SetResult();
        await Assert.That(await Task.WhenAll(calls).Timeout(T(1))).All(x => Equals(x, 1));
        await Assert.That(received).IsEqualTo(4);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task CallCanceledWhileWaitingForCredit_IsNeverSent(ClientTarget target)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = 0;
        var sent = ArrivedInvocations(2, out var tap);
        await using var server = await StartServerAsync(
            nexus => nexus.ServerTaskValueWithCancellationEvent = async (_, _) =>
            {
                Interlocked.Increment(ref received);
                await release.Task;
                return 1;
            },
            config =>
            {
                config.InvocationCredits = 1;
                config.MaxConcurrentConnectionInvocations = 1;
                tap(config);
            });
        await using var client = await ConnectAsync(target, server);
        var proxy = Proxy(client);

        // The first runs; the second takes the returned credit and waits in the queue; the third waits for credit.
        var first = proxy.ServerTaskValueWithCancellation(CancellationToken.None).AsTask();
        var second = proxy.ServerTaskValueWithCancellation(CancellationToken.None).AsTask();
        await sent.Timeout(T(1));
        // Canceled (in the client's process) before it is sent: like any call canceled before sending, it throws
        // TaskCanceledException.
        var waiting = proxy.ServerTaskValueWithCancellation(RemoteCancel.After(100)).AsTask();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await waiting.Timeout(T(1)));

        release.SetResult();
        await Task.WhenAll(first, second).Timeout(T(1));
        await Assert.That(received).IsEqualTo(2).Because("The canceled call was never sent");
        await Assert.That(await proxy.ServerTaskValueWithCancellation(CancellationToken.None).Timeout(T(1))).IsEqualTo(1);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task ValueTaskCallCanceledWhileWaitingForCredit_Throws(ClientTarget target)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = 0;
        var sent = ArrivedInvocations(2, out var tap);
        await using var server = await StartServerAsync(
            nexus => nexus.ServerTaskWithCancellationEvent = async (_, _) =>
            {
                Interlocked.Increment(ref received);
                await release.Task;
            },
            config =>
            {
                config.InvocationCredits = 1;
                config.MaxConcurrentConnectionInvocations = 1;
                tap(config);
            });
        await using var client = await ConnectAsync(target, server);
        var proxy = Proxy(client);

        var first = proxy.ServerTaskWithCancellation(CancellationToken.None).AsTask();
        var second = proxy.ServerTaskWithCancellation(CancellationToken.None).AsTask();
        await sent.Timeout(T(1));
        var waiting = proxy.ServerTaskWithCancellation(RemoteCancel.After(100)).AsTask();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await waiting.Timeout(T(1)));

        release.SetResult();
        await Task.WhenAll(first, second).Timeout(T(1));
        await Assert.That(received).IsEqualTo(2).Because("The canceled call was never sent");
        await AssertState(client, ConnectionState.Connected);
        await EndAsync(client);
    }

    /// <summary>
    /// C#-only: sends through the C# session's internal <c>SendInvocation</c> with a token canceled before the send.
    /// </summary>
    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task CancellationAfterCreditIsTaken_StillSendsTheInvocation(Type type)
    {
        // One credit: if the canceled send kept it without writing the invocation, the next call would wait forever.
        var serverConfig = CreateServerConfig(type);
        serverConfig.InvocationCredits = 1;
        var voidCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(serverConfig, nexus =>
        {
            nexus.ServerVoidEvent = _ => voidCalled.TrySetResult();
            nexus.ServerTaskValueEvent = _ => ValueTask.FromResult(1);
        });
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var message = new InvocationMessage
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.IgnoreReturn,
            InvocationId = 0,
            MethodId = 0 // ServerVoid
        };
        await Assert.That(await client.Session!.SendInvocation(message, canceled.Token).AsTask().Timeout(T(1))).IsTrue();

        await voidCalled.Task.Timeout(T(1));
        await Assert.That(await client.Proxy.ServerTaskValue().AsTask().Timeout(T(1))).IsEqualTo(1);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task FireAndForgetBurst_StaysWithinCredit(ClientTarget target)
    {
        // Fire-and-forget calls use credit too: a burst much larger than the credits is paced, not a violation.
        const int count = 2000;
        var received = 0;
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartServerAsync(
            nexus => nexus.ServerVoidWithParamEvent = (_, _) =>
            {
                if (Interlocked.Increment(ref received) == count)
                    all.TrySetResult();
            },
            config => config.InvocationCredits = 4);
        await using var client = await ConnectAsync(target, server);
        var proxy = Proxy(client);

        for (var i = 0; i < count; i++)
            proxy.ServerVoidWithParam(i);

        await client.BarrierAsync().Timeout(T(10));
        await all.Task.Timeout(T(10));
        await AssertState(client, ConnectionState.Connected);
        await EndAsync(client);
    }

    /// <summary>C#-only: a raw protocol client that never returns credit; the server's broadcast is under test.</summary>
    [Test]
    public async Task BroadcastToAClientThatNeverReturnsCredit_DropsItsInvocationAndCompletes()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        serverConfig.ProgressTimeout = 300;
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();

        // A raw client that advertises one credit and never returns it.
        using var raw = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await raw.ConnectAsync();
        await raw.SendProtocolHeaderAsync();
        await raw.ReadProtocolHeaderAsync();
        await raw.SendMessageAsync(new ClientGreetingMessage
        {
            ServerNexusHash = IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = IInvocationMethodHash.GetMethodHash<ClientNexus>(),
            InvocationCredits = 1,
        }).Timeout(T(1));
        await raw.AssertReceiveMessageAsync<ServerGreetingMessage>().Timeout(T(1));

        using var context = server.ContextProvider.Rent();

        // The first uses the only credit; the second waits ProgressTimeout, is dropped, and the broadcast finishes.
        await context.Proxy.All.ClientTask().Timeout(T(1));
        await context.Proxy.All.ClientTask().Timeout(T(2));
        await Assert.That(raw.IsConnected).IsTrue();
    }

    /// <summary>
    /// C#-only: raw stalled clients and wall-clock bounds on the server's broadcast; the healthy clients are incidental.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task BroadcastWithStalledClients_ReachesHealthyClientsWithoutWaiting()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        serverConfig.ProgressTimeout = 1000;
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();

        using var stalled1 = await ConnectStalledClient(serverConfig);
        using var stalled2 = await ConnectStalledClient(serverConfig);

        var received = new List<Task>();
        for (var i = 0; i < 3; i++)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var (client, clientNexus) = CreateClient(CreateClientConfig(Type.Tcp));
            clientNexus.ClientTaskWithParamEvent = (_, data) =>
            {
                if (data == 1)
                    tcs.TrySetResult();
                return default;
            };
            await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
            received.Add(tcs.Task);
        }

        using var context = server.ContextProvider.Rent();

        // Uses the stalled clients' only credit.
        await context.Proxy.All.ClientTaskWithParam(0).Timeout(T(1));

        var elapsed = Stopwatch.StartNew();
        var broadcast = context.Proxy.All.ClientTaskWithParam(1).AsTask();

        // The healthy clients get it while the broadcast still waits for the stalled ones.
        await Task.WhenAll(received).Timeout(T(1));
        await Assert.That(elapsed.ElapsedMilliseconds).IsLessThan(500);
        await Assert.That(broadcast.IsCompleted).IsFalse();

        // Both stalled clients are waited for at the same time: one timeout, not two.
        await broadcast.Timeout(T(3));
        await Assert.That(elapsed.ElapsedMilliseconds).IsBetween(900, 1800);
        await Assert.That(stalled1.IsConnected && stalled2.IsConnected).IsTrue();
    }

    /// <summary>
    /// C#-only: a raw stalled client and wall-clock bounds on the server's broadcast; the healthy clients are incidental.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task BroadcastLargeArgumentWithStalledClient_ReachesHealthyClientsWithoutWaiting()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        serverConfig.ProgressTimeout = 1000;
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();

        using var stalled = await ConnectStalledClient(serverConfig);

        var received = new List<Task<int>>();
        for (var i = 0; i < 2; i++)
        {
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var (client, clientNexus) = CreateClient(CreateClientConfig(Type.Tcp));
            clientNexus.ClientTaskEvent = _ => default;
            clientNexus.ClientDataEvent = (_, data) =>
            {
                tcs.TrySetResult(data.Length);
                return default;
            };
            await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
            received.Add(tcs.Task);
        }

        using var context = server.ContextProvider.Rent();
        await context.Proxy.All.ClientTask().Timeout(T(1));

        // Streamed to each session under its own invocation ID; the stalled client holds up neither of the others.
        var elapsed = Stopwatch.StartNew();
        var broadcast = context.Proxy.All.ClientData(new byte[300 * 1024]).AsTask();
        await Assert.That(await Task.WhenAll(received).Timeout(T(1))).All(x => Equals(x, 300 * 1024));
        await Assert.That(elapsed.ElapsedMilliseconds).IsLessThan(500);

        await broadcast.Timeout(T(3));
        await Assert.That(stalled.IsConnected).IsTrue();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task Broadcasts_ArriveInOrderAtEachClient(ClientTarget target)
    {
        // Few credits and one execution slot: broadcasts often find a client busy, and the client runs them in the
        // order they arrive (each call is reported when its handler starts).
        const int count = 500;
        await using var server = await StartServerAsync();

        var clients = new List<IClientUnderTest>();
        try
        {
            for (var i = 0; i < 3; i++)
            {
                clients.Add(await ConnectAsync(target, server, configure: spec =>
                {
                    spec.InvocationCredits = 2;
                    spec.MaxConcurrentConnectionInvocations = 1;
                }));
            }

            using (var context = server.Server.ContextProvider.Rent())
            {
                for (var i = 0; i < count; i++)
                    await context.Proxy.All.ClientTaskWithParam(i).Timeout(T(1));
            }

            foreach (var client in clients)
            {
                var order = new List<int>(count);
                for (var i = 0; i < count; i++)
                    order.Add((await Script(client).ClientTaskWithParam.NextCallAsync().AsTask().Timeout(T(5))).data);
                await Assert.That(order).IsEquivalentTo(Enumerable.Range(0, count), CollectionOrdering.Matching);
            }

            foreach (var client in clients)
                await EndAsync(client);
        }
        finally
        {
            foreach (var client in clients)
                await client.DisposeAsync();
        }
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task Broadcast_WritesOneEncodedFrameToEveryClient(ClientTarget target)
    {
        var frames = new List<byte[]>();
        await using var server = await StartServerAsync(configure: config => ServerTaps.OnSend(config, (_, frame) =>
        {
            if (frame.Type == MessageType.Invocation)
                lock (frames)
                    frames.Add(frame.Frame);
        }));

        var clients = new List<IClientUnderTest>();
        try
        {
            for (var i = 0; i < 3; i++)
                clients.Add(await ConnectAsync(target, server));

            using (var context = server.Server.ContextProvider.Rent())
                await context.Proxy.All.ClientTaskWithParam(12345).Timeout(T(1));

            foreach (var client in clients)
                await Assert.That((await Script(client).ClientTaskWithParam.NextCallAsync().AsTask().Timeout(T(1))).data).IsEqualTo(12345);

            byte[][] sent;
            lock (frames)
                sent = frames.ToArray();

            await Assert.That(sent).Count().IsEqualTo(3);
            await Assert.That(sent[1]).IsEquivalentTo(sent[0], CollectionOrdering.Matching);
            await Assert.That(sent[2]).IsEquivalentTo(sent[0], CollectionOrdering.Matching);

            foreach (var client in clients)
                await EndAsync(client);
        }
        finally
        {
            foreach (var client in clients)
                await client.DisposeAsync();
        }
    }

    /// <summary>
    /// Connects a raw client that advertises one invocation credit and never returns it.
    /// </summary>
    private async Task<RawTcpClient> ConnectStalledClient(Transports.ServerConfig serverConfig)
    {
        var raw = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await raw.ConnectAsync();
        await raw.SendProtocolHeaderAsync();
        await raw.ReadProtocolHeaderAsync();
        await raw.SendMessageAsync(new ClientGreetingMessage
        {
            ServerNexusHash = IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = IInvocationMethodHash.GetMethodHash<ClientNexus>(),
            InvocationCredits = 1,
        }).Timeout(T(1));
        await raw.AssertReceiveMessageAsync<ServerGreetingMessage>().Timeout(T(1));
        return raw;
    }

    /// <summary>
    /// Completes once the server has received <paramref name="count"/> invocation frames: the calls the client was
    /// allowed to send have arrived. <paramref name="tap"/> installs the observer on an endpoint's configuration.
    /// </summary>
    private static Task ArrivedInvocations(int count, out Action<Transports.ServerConfig> tap)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;
        tap = config => ServerTaps.OnReceive(config, (_, frame) =>
        {
            if (frame.Type == MessageType.Invocation && Interlocked.Increment(ref invocations) == count)
                arrived.TrySetResult();
        });
        return arrived.Task;
    }
}
