using NexNet.IntegrationTests.Security;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Invocation;
using NexNet.Messages;
using NUnit.Framework;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Invocation credits (docs/internals/flow-control-v2.md, section 4.7).
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
}
