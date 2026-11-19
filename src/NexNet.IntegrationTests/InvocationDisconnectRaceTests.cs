using NexNet.Invocation;
using NUnit.Framework;

namespace NexNet.IntegrationTests;

/// <summary>
/// A call that registers after the session's disconnect has already canceled every pending call (S30). It used to be
/// sent anyway and, with no result ever coming, stayed pending forever (about 1 in 3000 server-closed sessions in
/// TransportBufferIsolationStressTests). It must complete as canceled at once and send nothing.
/// </summary>
internal class InvocationDisconnectRaceTests : BaseTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.WebSocket)]
    public async Task CallRegisteredAfterCancelAll_ValueTask_CompletesCanceled(Type type)
    {
        var received = 0;
        var (server, client, _) = CreateServerClient(CreateServerConfig(type), CreateClientConfig(type));
        server.OnNexusCreated = nexus => nexus.ServerDataEvent = async (_, _) =>
        {
            // Never replies: only the client can complete the call.
            Interlocked.Increment(ref received);
            await Task.Delay(Timeout.Infinite);
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var manager = CancelAllBeforeNextRegistration(client.Session!.SessionInvocationStateManager);

        await AssertThrows<TaskCanceledException>(async () =>
            await client.Proxy.ServerData([1, 2, 3]).AsTask().Timeout(1));

        AssertNothingOutstanding(manager);
        Assert.That(received, Is.EqualTo(0));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.WebSocket)]
    public async Task CallRegisteredAfterCancelAll_ValueTaskOfT_WithToken_CompletesCanceled(Type type)
    {
        var received = 0;
        var (server, client, _) = CreateServerClient(CreateServerConfig(type), CreateClientConfig(type));
        server.OnNexusCreated = nexus => nexus.ServerTaskValueWithValueAndCancellationEvent = async (_, value, _) =>
        {
            Interlocked.Increment(ref received);
            await Task.Delay(Timeout.Infinite);
            return value;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var manager = CancelAllBeforeNextRegistration(client.Session!.SessionInvocationStateManager);

        using var cts = new CancellationTokenSource();
        await AssertThrows<TaskCanceledException>(async () =>
            await client.Proxy.ServerTaskValueWithValueAndCancellation(1, cts.Token).AsTask().Timeout(1));

        // A later cancellation of the token finds nothing to cancel and sends nothing.
        cts.Cancel();
        AssertNothingOutstanding(manager);
        Assert.That(received, Is.EqualTo(0));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task CallsAfterCancelAll_AllCompleteCanceled(Type type)
    {
        // Once the session is closing, every later call is canceled without being sent, not only the one in the race.
        var (server, client, _) = CreateServerClient(CreateServerConfig(type), CreateClientConfig(type));
        server.OnNexusCreated = nexus => nexus.ServerDataEvent = async (_, _) => await Task.Delay(Timeout.Infinite);

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var manager = (SessionInvocationStateManager)client.Session!.SessionInvocationStateManager;
        manager.CancelAll();

        var calls = Enumerable.Range(0, 8).Select(_ => client.Proxy.ServerData([1]).AsTask()).ToArray();
        foreach (var call in calls)
            await AssertThrows<TaskCanceledException>(async () => await call.Timeout(1));

        AssertNothingOutstanding(manager);
    }

    /// <summary>
    /// Runs CancelAll, as a disconnect does, inside the window between the call taking its id and registering.
    /// </summary>
    private static SessionInvocationStateManager CancelAllBeforeNextRegistration(ISessionInvocationStateManager stateManager)
    {
        var manager = (SessionInvocationStateManager)stateManager;
        manager.InternalBeforeRegister = () =>
        {
            manager.InternalBeforeRegister = null;
            manager.CancelAll();
        };
        return manager;
    }

    private static void AssertNothingOutstanding(SessionInvocationStateManager manager)
    {
        Assert.That(manager.OutstandingCount, Is.EqualTo(0));
        Assert.That(manager.IsOutstanding(1), Is.False);
    }
}
