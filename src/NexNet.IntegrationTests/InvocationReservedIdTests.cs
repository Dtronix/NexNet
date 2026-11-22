using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.Messages;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// What the client's invocation counts mean, the same on every client language (S42): a call takes its invocation ID
/// only once it has invocation credit, so a call waiting for credit holds no ID and is not awaiting a result; a call
/// canceled after send is completed (no longer awaiting a result) but keeps its ID until the late reply releases it.
/// </summary>
/// <remarks>
/// The matrix test checks the reserved IDs (<see cref="IClientUnderTest.GetOutstandingInvocationsAsync"/>) on every
/// language. The snapshot's <c>AwaitingResults</c> is checked on the C# client only, until the harness can read a
/// remote client's snapshot (Q05).
/// </remarks>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class InvocationReservedIdTests(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task ReservedIds_LeaveOutACallWaitingForCredit_AndKeepACallCanceledAfterSend(ClientTarget target)
    {
        var release = Signal();
        var arrived = Signal();
        var invocations = 0;
        await using var server = await StartServerAsync(
            nexus => nexus.ServerTaskValueWithValueAndCancellationEvent = HoldFirstCall(release),
            config =>
            {
                // One credit and one slot: the first call runs, the second waits in the server's queue, and the
                // third waits for credit on the client.
                config.InvocationCredits = 1;
                config.MaxConcurrentConnectionInvocations = 1;
                ServerTaps.OnReceive(config, (_, frame) =>
                {
                    if (frame.Type == MessageType.Invocation && Interlocked.Increment(ref invocations) == 2)
                        arrived.TrySetResult();
                });
            });
        await using var client = await ConnectAsync(target, server);
        var proxy = Proxy(client);

        var first = proxy.ServerTaskValueWithValueAndCancellation(1, CancellationToken.None).AsTask();
        using var secondCts = new CancellationTokenSource();
        var second = proxy.ServerTaskValueWithValueAndCancellation(2, secondCts.Token).AsTask();
        await arrived.Task.Timeout(T(1));

        using var waitingCts = new CancellationTokenSource();
        var waiting = proxy.ServerTaskValueWithValueAndCancellation(3, waitingCts.Token).AsTask();
        await client.BarrierAsync().Timeout(T(1));

        // The first two calls hold IDs; the one waiting for credit holds none.
        await AssertReservedIds(client, 2);

        // Canceled while waiting for credit: never sent, and it never held an ID.
        await waitingCts.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await waiting.Timeout(T(1)));
        await AssertReservedIds(client, 2);

        // Canceled after send: the call completes at once, but its ID stays reserved until the server's reply, which
        // comes only once the call leaves the server's queue.
        await secondCts.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await second.Timeout(T(1)));
        await AssertReservedIds(client, 2);

        release.SetResult();
        await Assert.That(await first.Timeout(T(1))).IsEqualTo(1);

        // The second call starts with its token canceled and replies Canceled, which releases its ID.
        await AssertNotOutstanding(client, 2);
        await Assert.That(Volatile.Read(ref invocations)).IsEqualTo(2).Because("The call canceled while waiting for credit was never sent.");
        await EndAsync(client);
    }

    /// <summary>
    /// C#-only: the snapshot's <c>AwaitingResults</c> and <c>CreditWaits</c>, next to the session's reserved IDs. The
    /// harness cannot read a remote client's snapshot yet (Q05).
    /// </summary>
    [Test]
    public async Task Snapshot_CallWaitingForCreditIsNotAwaitingAResult()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        serverConfig.InvocationCredits = 1;
        serverConfig.MaxConcurrentConnectionInvocations = 1;
        var release = Signal();
        var server = CreateServer(serverConfig, nexus => nexus.ServerTaskValueWithValueAndCancellationEvent = HoldFirstCall(release));
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        var (client, _) = CreateClient(CreateClientConfig(Type.Tcp));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        var invocations = client.Session!.SessionInvocationStateManager;

        var first = client.Proxy.ServerTaskValueWithValueAndCancellation(1, CancellationToken.None).AsTask();
        using var secondCts = new CancellationTokenSource();
        var second = client.Proxy.ServerTaskValueWithValueAndCancellation(2, secondCts.Token).AsTask();
        await WaitUntil(() => client.GetSnapshot()!.Invocations is { AwaitingResults: 2, OutboundCredits: 0 });

        using var waitingCts = new CancellationTokenSource();
        var waiting = client.Proxy.ServerTaskValueWithValueAndCancellation(3, waitingCts.Token).AsTask();
        await WaitUntil(() => client.GetSnapshot()!.Invocations.CreditWaits == 1);
        await Assert.That(client.GetSnapshot()!.Invocations.AwaitingResults).IsEqualTo(2);
        await Assert.That(invocations.ReservedIdCount).IsEqualTo(2);

        await waitingCts.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await waiting.Timeout(T(1)));
        await Assert.That(client.GetSnapshot()!.Invocations.CreditWaits).IsEqualTo(0);
        await Assert.That(invocations.ReservedIdCount).IsEqualTo(2);

        // Canceled after send: no longer awaiting a result, but its ID is reserved until the late reply.
        await secondCts.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await second.Timeout(T(1)));
        await Assert.That(client.GetSnapshot()!.Invocations.AwaitingResults).IsEqualTo(1);
        await Assert.That(invocations.ReservedIdCount).IsEqualTo(2);

        release.SetResult();
        await Assert.That(await first.Timeout(T(1))).IsEqualTo(1);
        await WaitUntil(() => invocations.ReservedIdCount == 0);
        await Assert.That(invocations.ReservedIdCount).IsEqualTo(0);
        await Assert.That(client.GetSnapshot()!.Invocations.AwaitingResults).IsEqualTo(0);
    }

    /// <summary>The server method: call 1 runs until <paramref name="release"/>; any other waits for its cancellation.</summary>
    private static Func<ServerNexus, int, CancellationToken, ValueTask<int>> HoldFirstCall(TaskCompletionSource release)
        => async (_, value, token) =>
        {
            if (value == 1)
            {
                await release.Task;
                return value;
            }

            await Task.Delay(Timeout.Infinite, token);
            return value;
        };
}
