using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Interop.Hosts;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Cancellation tokens of invoked methods: per invocation for calls that expect a result, shared per session for
/// fire-and-forget calls. Runs on the client matrix.
/// </summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class InvocationCancellationTests(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task ConcurrentCallsToOneMethod_AreCanceledIndividually(ClientTarget target)
    {
        var release = Signal();
        var tokens = new List<CancellationToken>();
        var firstStarted = Signal();
        var bothStarted = Signal();
        var firstCanceled = Signal();
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueWithCancellationEvent = async (_, ct) =>
        {
            int index;
            lock (tokens)
            {
                tokens.Add(ct);
                index = tokens.Count;
                if (index == 1)
                    firstStarted.TrySetResult();
                if (index == 2)
                    bothStarted.TrySetResult();
            }

            try
            {
                await release.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                if (index == 1)
                    firstCanceled.TrySetResult();
                return -1;
            }

            return 1;
        });
        await using var client = await ConnectAsync(target, server);
        var proxy = Proxy(client);

        using var cancel = new CancellationTokenSource();
        var first = proxy.ServerTaskValueWithCancellation(cancel.Token).AsTask();
        await firstStarted.Task.Timeout(T(1));
        var second = proxy.ServerTaskValueWithCancellation(CancellationToken.None).AsTask();
        await bothStarted.Task.Timeout(T(1));

        await cancel.CancelAsync();
        await firstCanceled.Task.Timeout(T(1));

        // Only the canceled call's token fired; the other call is still running.
        await Assert.That(tokens[0].IsCancellationRequested).IsTrue();
        await Assert.That(tokens[1].IsCancellationRequested).IsFalse();
        await Assert.That(second.IsCompleted).IsFalse();

        release.SetResult();
        await Assert.That(await second.Timeout(T(1))).IsEqualTo(1);
        await Task.WhenAny(first, Task.Delay(TimeSpan.FromSeconds(T(1))));
        await Assert.That(first.IsCanceled || first.IsCompleted).IsTrue();
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task OverlappingFireAndForgetCalls_ShareTheSessionToken_CanceledOnDisconnect(ClientTarget target)
    {
        await using var server = await StartServerAsync();

        // The first call runs until its token is canceled; the second completes at once.
        var firstCanceled = Signal();
        await using var client = await ConnectAsync(target, server, script => script.ClientTaskWithCancellation
            .AwaitCancel()
            .Forward(_ =>
            {
                firstCanceled.TrySetResult();
                return new ValueTask<NoResult>(NoResult.Value);
            })
            .Times(1));
        var calls = client.Callbacks<ClientNexusScript>().ClientTaskWithCancellation;

        // Both are fire-and-forget with invocation ID 0.
        using var context = server.Server.ContextProvider.Rent();
        await context.Proxy.All.ClientTaskWithCancellation(CancellationToken.None).Timeout(T(1));
        await calls.NextCallAsync().AsTask().Timeout(T(1));
        await context.Proxy.All.ClientTaskWithCancellation(CancellationToken.None).Timeout(T(1));
        await calls.NextCallAsync().AsTask().Timeout(T(1));

        // The second call's completion runs after its handler returns, and nothing observable marks its end: give
        // it a moment to (not) cancel the shared token.
        await Task.Delay(100);

        // The second finishing does not affect the first.
        await Assert.That(firstCanceled.Task.IsCompleted).IsFalse();

        // The session ending cancels it.
        await client.DisconnectAsync().Timeout(T(1));
        await firstCanceled.Task.Timeout(T(1));
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task FireAndForgetCalls_UnaffectedByPreSendCancellation(ClientTarget target)
    {
        // Broadcasts and void methods do not wait for a result: an already canceled token neither throws nor stops
        // the call.
        var voidCalled = Signal();
        await using var server = await StartServerAsync(nexus => nexus.ServerVoidEvent = _ => voidCalled.TrySetResult());
        await using var client = await ConnectAsync(target, server);
        var proxy = Proxy(client);

        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        // A canceled call that waits for its result throws, and leaves the session usable.
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
            await proxy.ServerTaskWithCancellation(canceled.Token).Timeout(T(1)));

        proxy.ServerVoid();
        await voidCalled.Task.Timeout(T(1));

        using var context = server.Server.ContextProvider.Rent();
        await context.Proxy.All.ClientTaskWithCancellation(canceled.Token).Timeout(T(1));
        await client.Callbacks<ClientNexusScript>().ClientTaskWithCancellation.NextCallAsync().AsTask().Timeout(T(1));
        await WaitForStateAsync(client, ConnectionState.Connected);
        await EndAsync(client);
    }
}
