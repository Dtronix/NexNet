
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Cancellation tokens of invoked methods: per invocation for calls that expect a result, shared per session for
/// fire-and-forget calls.
/// </summary>
internal class InvocationCancellationTests : BaseTests
{
    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task ConcurrentCallsToOneMethod_AreCanceledIndividually(Type type)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new List<CancellationToken>();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(CreateServerConfig(type), nexus => nexus.ServerTaskValueWithCancellationEvent = async (_, ct) =>
        {
            lock (tokens)
            {
                tokens.Add(ct);
                if (tokens.Count == 2)
                    bothStarted.TrySetResult();
            }

            try
            {
                await release.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return -1;
            }

            return 1;
        });
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        using var cancel = new CancellationTokenSource();
        var first = client.Proxy.ServerTaskValueWithCancellation(cancel.Token).AsTask();
        await Task.Delay(50);
        var second = client.Proxy.ServerTaskValueWithCancellation(CancellationToken.None).AsTask();
        await bothStarted.Task.Timeout(T(1));

        cancel.Cancel();
        await Task.Delay(200);

        // Only the canceled call's token fired; the other call is still running.
        await Assert.That(tokens[0].IsCancellationRequested).IsTrue();
        await Assert.That(tokens[1].IsCancellationRequested).IsFalse();
        await Assert.That(second.IsCompleted).IsFalse();

        release.SetResult();
        await Assert.That(await second.Timeout(T(1))).IsEqualTo(1);
        await Assert.That(first.IsCanceled || first.IsCompleted).IsTrue();
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task OverlappingFireAndForgetCalls_ShareTheSessionToken_CanceledOnDisconnect(Type type)
    {
        var server = CreateServer(CreateServerConfig(type), null);
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        var tokens = new List<CancellationToken>();
        var secondDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (client, clientNexus) = CreateClient(CreateClientConfig(type));
        clientNexus.ClientTaskWithCancellationEvent = async (_, ct) =>
        {
            int index;
            lock (tokens)
            {
                tokens.Add(ct);
                index = tokens.Count;
            }

            if (index == 2)
            {
                secondDone.TrySetResult();
                return;
            }

            // The first call runs until its token is canceled.
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                firstCanceled.TrySetResult();
            }
        };
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        // Both are fire-and-forget with invocation ID 0.
        using var context = server.ContextProvider.Rent();
        await context.Proxy.All.ClientTaskWithCancellation(CancellationToken.None).Timeout(T(1));
        await Task.Delay(50);
        await context.Proxy.All.ClientTaskWithCancellation(CancellationToken.None).Timeout(T(1));
        await secondDone.Task.Timeout(T(1));
        await Task.Delay(100);

        // The second finishing does not affect the first.
        await Assert.That(tokens).Count().IsEqualTo(2);
        await Assert.That(tokens[0].IsCancellationRequested).IsFalse();

        // The session ending cancels it.
        await client.DisconnectAsync().Timeout(T(1));
        await firstCanceled.Task.Timeout(T(1));
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task FireAndForgetCalls_UnaffectedByPreSendCancellation(Type type)
    {
        // Broadcasts and void methods do not wait for a result: an already canceled token neither throws nor stops
        // the call.
        var voidCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(CreateServerConfig(type), nexus => nexus.ServerVoidEvent = _ => voidCalled.TrySetResult());
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        var clientCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (client, clientNexus) = CreateClient(CreateClientConfig(type));
        clientNexus.ClientTaskWithCancellationEvent = (_, _) =>
        {
            clientCalled.TrySetResult();
            return ValueTask.CompletedTask;
        };
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // A canceled call that waits for its result throws, and leaves the session usable.
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
            await client.Proxy.ServerTaskWithCancellation(canceled.Token).AsTask().Timeout(T(1)));

        client.Proxy.ServerVoid();
        await voidCalled.Task.Timeout(T(1));

        using var context = server.ContextProvider.Rent();
        await context.Proxy.All.ClientTaskWithCancellation(canceled.Token).Timeout(T(1));
        await clientCalled.Task.Timeout(T(1));
        await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
    }
}