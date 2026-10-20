using NUnit.Framework;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Cancellation tokens of invoked methods: per invocation for calls that expect a result, shared per session for
/// fire-and-forget calls.
/// </summary>
internal class InvocationCancellationTests : BaseTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
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
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(1);

        using var cancel = new CancellationTokenSource();
        var first = client.Proxy.ServerTaskValueWithCancellation(cancel.Token).AsTask();
        await Task.Delay(50);
        var second = client.Proxy.ServerTaskValueWithCancellation(CancellationToken.None).AsTask();
        await bothStarted.Task.Timeout(1);

        cancel.Cancel();
        await Task.Delay(200);

        // Only the canceled call's token fired; the other call is still running.
        Assert.That(tokens[0].IsCancellationRequested, Is.True);
        Assert.That(tokens[1].IsCancellationRequested, Is.False);
        Assert.That(second.IsCompleted, Is.False);

        release.SetResult();
        Assert.That(await second.Timeout(1), Is.EqualTo(1));
        Assert.That(first.IsCanceled || first.IsCompleted, Is.True);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task OverlappingFireAndForgetCalls_ShareTheSessionToken_CanceledOnDisconnect(Type type)
    {
        var server = CreateServer(CreateServerConfig(type), null);
        await server.StartAsync().Timeout(1);

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
        await client.ConnectAsync().Timeout(1);

        // Both are fire-and-forget with invocation ID 0.
        using var context = server.ContextProvider.Rent();
        await context.Proxy.All.ClientTaskWithCancellation(CancellationToken.None).Timeout(1);
        await Task.Delay(50);
        await context.Proxy.All.ClientTaskWithCancellation(CancellationToken.None).Timeout(1);
        await secondDone.Task.Timeout(1);
        await Task.Delay(100);

        // The second finishing does not affect the first.
        Assert.That(tokens, Has.Count.EqualTo(2));
        Assert.That(tokens[0].IsCancellationRequested, Is.False);

        // The session ending cancels it.
        await client.DisconnectAsync().Timeout(1);
        await firstCanceled.Task.Timeout(1);
    }
}
