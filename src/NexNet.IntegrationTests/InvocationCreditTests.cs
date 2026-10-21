using System.Diagnostics;
using NexNet.IntegrationTests.Security;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Invocation;
using NexNet.Messages;
using NUnit.Framework;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Invocation credits (docs/internals/flow-control.md, section 4.7).
/// </summary>
internal class InvocationCreditTests : BaseTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.WebSocket)]
    public async Task CallerWaitsLocallyForCredit(Type type)
    {
        // Two credits and one slot: one call runs, two wait in the server's queue, the fourth waits on the client.
        var serverConfig = CreateServerConfig(type);
        serverConfig.InvocationCredits = 2;
        serverConfig.MaxConcurrentConnectionInvocations = 1;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = 0;
        var server = CreateServer(serverConfig, nexus => nexus.ServerTaskValueEvent = async _ =>
        {
            Interlocked.Increment(ref received);
            await release.Task;
            return 1;
        });
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(1);

        var calls = Enumerable.Range(0, 4).Select(_ => client.Proxy.ServerTaskValue().AsTask()).ToArray();
        await Task.Delay(200);

        // Only the running call has been received; the server would disconnect a client that sent beyond its credit.
        Assert.That(received, Is.EqualTo(1));
        Assert.That(client.State, Is.EqualTo(ConnectionState.Connected));

        release.SetResult();
        Assert.That(await Task.WhenAll(calls).Timeout(1), Is.All.EqualTo(1));
        Assert.That(received, Is.EqualTo(4));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task CallCanceledWhileWaitingForCredit_IsNeverSent(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.InvocationCredits = 1;
        serverConfig.MaxConcurrentConnectionInvocations = 1;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = 0;
        var server = CreateServer(serverConfig, nexus => nexus.ServerTaskValueWithCancellationEvent = async (_, _) =>
        {
            Interlocked.Increment(ref received);
            await release.Task;
            return 1;
        });
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(1);

        // The first runs; the second takes the returned credit and waits in the queue; the third waits for credit.
        var first = client.Proxy.ServerTaskValueWithCancellation(CancellationToken.None).AsTask();
        var second = client.Proxy.ServerTaskValueWithCancellation(CancellationToken.None).AsTask();
        await Task.Delay(100);
        // Canceled before it is sent: like any call canceled before sending, it returns the default value.
        using var cts = new CancellationTokenSource(100);
        Assert.That(await client.Proxy.ServerTaskValueWithCancellation(cts.Token).AsTask().Timeout(1), Is.Zero);

        release.SetResult();
        await Task.WhenAll(first, second).Timeout(1);
        Assert.That(received, Is.EqualTo(2), "The canceled call was never sent");
        Assert.That(await client.Proxy.ServerTaskValueWithCancellation(CancellationToken.None).Timeout(1), Is.EqualTo(1));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
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
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(1);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var message = new InvocationMessage
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.IgnoreReturn,
            InvocationId = 0,
            MethodId = 0 // ServerVoid
        };
        Assert.That(await client.Session!.SendInvocation(message, canceled.Token).AsTask().Timeout(1), Is.True);

        await voidCalled.Task.Timeout(1);
        Assert.That(await client.Proxy.ServerTaskValue().AsTask().Timeout(1), Is.EqualTo(1));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task FireAndForgetBurst_StaysWithinCredit(Type type)
    {
        // Fire-and-forget calls use credit too: a burst much larger than the credits is paced, not a violation.
        const int count = 2000;
        var serverConfig = CreateServerConfig(type);
        serverConfig.InvocationCredits = 4;
        var received = 0;
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(serverConfig, nexus => nexus.ServerVoidWithParamEvent = (_, _) =>
        {
            if (Interlocked.Increment(ref received) == count)
                all.TrySetResult();
        });
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(1);

        for (var i = 0; i < count; i++)
            client.Proxy.ServerVoidWithParam(i);

        await all.Task.Timeout(10);
        Assert.That(client.State, Is.EqualTo(ConnectionState.Connected));
    }

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
        }).Timeout(1);
        await raw.AssertReceiveMessageAsync<ServerGreetingMessage>().Timeout(1);

        using var context = server.ContextProvider.Rent();

        // The first uses the only credit; the second waits ProgressTimeout, is dropped, and the broadcast finishes.
        await context.Proxy.All.ClientTask().Timeout(1);
        await context.Proxy.All.ClientTask().Timeout(2);
        Assert.That(raw.IsConnected, Is.True);
    }

    [Test]
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
            await client.ConnectAsync().Timeout(1);
            received.Add(tcs.Task);
        }

        using var context = server.ContextProvider.Rent();

        // Uses the stalled clients' only credit.
        await context.Proxy.All.ClientTaskWithParam(0).Timeout(1);

        var elapsed = Stopwatch.StartNew();
        var broadcast = context.Proxy.All.ClientTaskWithParam(1).AsTask();

        // The healthy clients get it while the broadcast still waits for the stalled ones.
        await Task.WhenAll(received).Timeout(1);
        Assert.That(elapsed.ElapsedMilliseconds, Is.LessThan(500));
        Assert.That(broadcast.IsCompleted, Is.False);

        // Both stalled clients are waited for at the same time: one timeout, not two.
        await broadcast.Timeout(3);
        Assert.That(elapsed.ElapsedMilliseconds, Is.InRange(900, 1800));
        Assert.That(stalled1.IsConnected && stalled2.IsConnected, Is.True);
    }

    [Test]
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
            await client.ConnectAsync().Timeout(1);
            received.Add(tcs.Task);
        }

        using var context = server.ContextProvider.Rent();
        await context.Proxy.All.ClientTask().Timeout(1);

        // Streamed to each session under its own invocation ID; the stalled client holds up neither of the others.
        var elapsed = Stopwatch.StartNew();
        var broadcast = context.Proxy.All.ClientData(new byte[300 * 1024]).AsTask();
        Assert.That(await Task.WhenAll(received).Timeout(1), Is.All.EqualTo(300 * 1024));
        Assert.That(elapsed.ElapsedMilliseconds, Is.LessThan(500));

        await broadcast.Timeout(3);
        Assert.That(stalled.IsConnected, Is.True);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task Broadcasts_ArriveInOrderAtEachClient(Type type)
    {
        // Few credits and one execution slot: broadcasts often find a client busy, and the client runs them in the
        // order they arrive.
        const int count = 500;
        var server = CreateServer(CreateServerConfig(type), null);
        await server.StartAsync().Timeout(1);

        var orders = new List<List<int>>();
        var done = new List<Task>();
        for (var i = 0; i < 3; i++)
        {
            var clientConfig = CreateClientConfig(type);
            clientConfig.InvocationCredits = 2;
            clientConfig.MaxConcurrentConnectionInvocations = 1;
            var order = new List<int>();
            var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var (client, clientNexus) = CreateClient(clientConfig);
            clientNexus.ClientTaskWithParamEvent = (_, id) =>
            {
                lock (order)
                {
                    order.Add(id);
                    if (order.Count == count)
                        all.TrySetResult();
                }

                return default;
            };
            await client.ConnectAsync().Timeout(1);
            orders.Add(order);
            done.Add(all.Task);
        }

        using var context = server.ContextProvider.Rent();
        for (var i = 0; i < count; i++)
            await context.Proxy.All.ClientTaskWithParam(i).Timeout(1);

        await Task.WhenAll(done).Timeout(5);
        foreach (var order in orders)
            Assert.That(order, Is.EqualTo(Enumerable.Range(0, count)));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task Broadcast_WritesOneEncodedFrameToEveryClient(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        var frames = new List<byte[]>();
        serverConfig.InternalOnSend = (_, bytes) =>
        {
            if (bytes.Length > 0 && bytes[0] == (byte)MessageType.Invocation)
                lock (frames)
                    frames.Add(bytes);
        };
        var server = CreateServer(serverConfig, null);
        await server.StartAsync().Timeout(1);

        var received = new List<Task<int>>();
        for (var i = 0; i < 3; i++)
        {
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var (client, clientNexus) = CreateClient(CreateClientConfig(type));
            clientNexus.ClientTaskWithParamEvent = (_, data) =>
            {
                tcs.TrySetResult(data);
                return default;
            };
            await client.ConnectAsync().Timeout(1);
            received.Add(tcs.Task);
        }

        using var context = server.ContextProvider.Rent();
        await context.Proxy.All.ClientTaskWithParam(12345).Timeout(1);

        Assert.That(await Task.WhenAll(received).Timeout(1), Is.All.EqualTo(12345));
        lock (frames)
        {
            Assert.That(frames, Has.Count.EqualTo(3));
            Assert.That(frames[1], Is.EqualTo(frames[0]));
            Assert.That(frames[2], Is.EqualTo(frames[0]));
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
        }).Timeout(1);
        await raw.AssertReceiveMessageAsync<ServerGreetingMessage>().Timeout(1);
        return raw;
    }
}
