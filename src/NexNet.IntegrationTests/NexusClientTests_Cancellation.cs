using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.Interop.InProc;
using NexNet.InteropServer;
using NexNet.IntegrationTests.Pipes;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Transports;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>Cancellation of invocations the client under test makes.</summary>
/// <remarks>
/// The C# suite's QUIC rows are kept as C#-only rows (<see cref="ClientMatrixAttribute.CSharpTransports"/>, H16): no
/// other client language has QUIC.
/// </remarks>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class NexusClientTests_Cancellation(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public Task ClientSendsCancellationTokenOnClientSideTimeout_ServerTaskValueWithParam(ClientTarget target)
        => ClientSendsCancellationOnTimeout(
            target,
            nexus => nexus.ServerTaskWithCancellationEvent = async (_, token) => await Task.Delay(10000, token),
            proxy => proxy.ServerTaskWithCancellation(RemoteCancel.After(100)));

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public Task ClientSendsCancellationTokenOnClientSideTimeout_ServerTaskWithValueAndCancellation(ClientTarget target)
        => ClientSendsCancellationOnTimeout(
            target,
            nexus => nexus.ServerTaskWithValueAndCancellationEvent = async (_, _, token) => await Task.Delay(10000, token),
            proxy => proxy.ServerTaskWithValueAndCancellation(1234, RemoteCancel.After(100)));

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public Task ClientSendsCancellationTokenOnClientSideTimeout_ServerTaskValueWithCancellation(ClientTarget target)
        => ClientSendsCancellationOnTimeout(
            target,
            nexus => nexus.ServerTaskValueWithCancellationEvent = async (_, token) =>
            {
                await Task.Delay(10000, token);
                return 12345;
            },
            async proxy => await proxy.ServerTaskValueWithCancellation(RemoteCancel.After(100)));

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public Task ClientSendsCancellationTokenOnClientSideTimeout_ServerTaskValueWithValueAndCancellation(ClientTarget target)
        => ClientSendsCancellationOnTimeout(
            target,
            nexus => nexus.ServerTaskValueWithValueAndCancellationEvent = async (_, _, token) =>
            {
                await Task.Delay(10000, token);
                return 12345;
            },
            async proxy => await proxy.ServerTaskValueWithValueAndCancellation(1234, RemoteCancel.After(100)));

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ClientDoesNotSendCancellationAfterCompletion(ClientTarget target)
    {
        var cancellations = 0;
        await using var server = await StartServerAsync(
            nexus => nexus.ServerTaskWithCancellationEvent = (_, _) => ValueTask.CompletedTask,
            config => ServerTaps.OnReceive(config, (_, frame) =>
            {
                if (frame.Type == MessageType.InvocationCancellation)
                    Interlocked.Increment(ref cancellations);
            }));
        await using var client = await ConnectAsync(target, server);

        using var cts = new CancellationTokenSource();
        await Proxy(client).ServerTaskWithCancellation(cts.Token).Timeout(T(1));

        // Canceled after the call completed: there is nothing left to cancel.
        await cts.CancelAsync();
        await RoundTripAsync(client);
        await Assert.That(Volatile.Read(ref cancellations)).IsEqualTo(0)
            .Because("The client must not send a cancellation after the call completed.");
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task LateResultOfCanceledInvocationDoesNotCompleteLaterInvocation(ClientTarget target)
    {
        // The server method ignores the token, so the canceled call still sends its result, after the next call starts.
        var firstStarted = Signal();
        var firstGate = Signal();
        var secondGate = Signal();
        var secondReceived = Signal();
        var firstResultSent = Signal();
        var invocations = 0;
        await using var server = await StartServerAsync(
            nexus => nexus.ServerTaskValueWithValueAndCancellationEvent = async (_, value, _) =>
            {
                if (value == 1)
                {
                    firstStarted.TrySetResult();
                    await firstGate.Task;
                }
                else
                {
                    await secondGate.Task;
                }

                return value * 10;
            },
            config =>
            {
                ServerTaps.OnReceive(config, (_, frame) =>
                {
                    if (frame.Type == MessageType.Invocation && Interlocked.Increment(ref invocations) == 2)
                        secondReceived.TrySetResult();
                });
                ServerTaps.OnSend(config, (_, frame) =>
                {
                    // Only the first call can have completed: the second waits for its gate.
                    if (frame.Type == MessageType.InvocationResult)
                        firstResultSent.TrySetResult();
                });
            });
        await using var client = await ConnectAsync(target, server);
        var proxy = Proxy(client);

        using var cts = new CancellationTokenSource();
        var first = proxy.ServerTaskValueWithValueAndCancellation(1, cts.Token).AsTask();
        await firstStarted.Task.Timeout(T(1));
        await cts.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await first.Timeout(T(1)));

        var second = proxy.ServerTaskValueWithValueAndCancellation(2, CancellationToken.None).AsTask();
        await secondReceived.Task.Timeout(T(1));
        firstGate.SetResult();

        // The late result is sent before the second call's own result, and frames arrive in order.
        await firstResultSent.Task.Timeout(T(1));
        secondGate.SetResult();

        await Assert.That(await second.Timeout(T(1))).IsEqualTo(20);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task CanceledInvocationReleasesItsIdOnTheCanceledReply(ClientTarget target)
    {
        // The server honors the token and replies Canceled; the id is reserved until then.
        var started = Signal();
        var canceledReply = Signal<ushort>();
        await using var server = await StartServerAsync(
            nexus => nexus.ServerTaskValueWithValueAndCancellationEvent = async (_, value, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return value;
            },
            config => ServerTaps.OnSend(config, (_, frame) =>
            {
                if (frame.Type != MessageType.InvocationResult)
                    return;
                var result = TestSerialization.DeserializeMessage<InvocationResultMessage>(frame.Body);
                if (result.State == InvocationResultMessage.StateType.Canceled)
                    canceledReply.TrySetResult(result.InvocationId);
            }));
        await using var client = await ConnectAsync(target, server);

        using var cts = new CancellationTokenSource();
        var call = Proxy(client).ServerTaskValueWithValueAndCancellation(1, cts.Token).AsTask();
        await started.Task.Timeout(T(1));
        await cts.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await call.Timeout(T(1)));

        await Assert.That(await canceledReply.Task.Timeout(T(1))).IsEqualTo((ushort)1);
        await AssertNotOutstandingInProc(client, 1);
        await WaitForStateAsync(client, ConnectionState.Connected);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task CancellationOfAQueuedInvocation_CancelsItWhenItStarts(ClientTarget target)
    {
        // One execution slot: the second call is still queued on the server when its cancellation arrives.
        var release = Signal();
        var started = Signal();
        var secondReceived = Signal();
        var cancellationReceived = Signal();
        var secondCanceledReply = Signal();
        var invocations = 0;
        await using var server = await StartServerAsync(
            nexus => nexus.ServerTaskValueWithValueAndCancellationEvent = async (_, value, token) =>
            {
                if (value == 1)
                {
                    started.TrySetResult();
                    await release.Task;
                    return value;
                }

                await Task.Delay(Timeout.Infinite, token);
                return value;
            },
            config =>
            {
                config.MaxConcurrentConnectionInvocations = 1;
                ServerTaps.OnReceive(config, (_, frame) =>
                {
                    if (frame.Type == MessageType.Invocation && Interlocked.Increment(ref invocations) == 2)
                        secondReceived.TrySetResult();
                    else if (frame.Type == MessageType.InvocationCancellation)
                        cancellationReceived.TrySetResult();
                });
                ServerTaps.OnSend(config, (_, frame) =>
                {
                    if (frame.Type != MessageType.InvocationResult)
                        return;
                    var result = TestSerialization.DeserializeMessage<InvocationResultMessage>(frame.Body);
                    if (result is { InvocationId: 2, State: InvocationResultMessage.StateType.Canceled })
                        secondCanceledReply.TrySetResult();
                });
            });
        await using var client = await ConnectAsync(target, server);
        var proxy = Proxy(client);

        var first = proxy.ServerTaskValueWithValueAndCancellation(1, CancellationToken.None).AsTask();
        await started.Task.Timeout(T(1));

        using var cts = new CancellationTokenSource();
        var second = proxy.ServerTaskValueWithValueAndCancellation(2, cts.Token).AsTask();
        // The second call is queued on the server before it is canceled.
        await secondReceived.Task.Timeout(T(1));
        await cts.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await second.Timeout(T(1)));
        await cancellationReceived.Task.Timeout(T(1));

        release.SetResult();
        await Assert.That(await first.Timeout(T(1))).IsEqualTo(1);

        // The second call starts with its token already canceled and replies Canceled, releasing its id.
        await secondCanceledReply.Task.Timeout(T(1));
        await AssertNotOutstandingInProc(client, 2);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task AlreadyCanceledToken_ValueTaskOfT_ThrowsAndSendsNothing(ClientTarget target)
    {
        var received = 0;
        var invocations = 0;
        await using var server = await StartServerAsync(
            nexus => nexus.ServerTaskValueWithCancellationEvent = (_, _) =>
            {
                Interlocked.Increment(ref received);
                return ValueTask.FromResult(1);
            },
            config => CountInvocations(config, () => Interlocked.Increment(ref invocations)));
        await using var client = await ConnectAsync(target, server);
        var proxy = Proxy(client);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
            await proxy.ServerTaskValueWithCancellation(cts.Token).Timeout(T(1)));

        // Never sent and no id taken: the next call is the only one the server sees.
        await AssertNotOutstandingInProc(client, 1);
        await Assert.That(await proxy.ServerTaskValueWithCancellation(CancellationToken.None).Timeout(T(1))).IsEqualTo(1);
        await Assert.That(Volatile.Read(ref received)).IsEqualTo(1);
        await Assert.That(Volatile.Read(ref invocations)).IsEqualTo(1);
        await WaitForStateAsync(client, ConnectionState.Connected);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task AlreadyCanceledToken_ValueTask_ThrowsAndSendsNothing(ClientTarget target)
    {
        var received = 0;
        var invocations = 0;
        await using var server = await StartServerAsync(
            nexus => nexus.ServerTaskWithCancellationEvent = (_, _) =>
            {
                Interlocked.Increment(ref received);
                return ValueTask.CompletedTask;
            },
            config => CountInvocations(config, () => Interlocked.Increment(ref invocations)));
        await using var client = await ConnectAsync(target, server);
        var proxy = Proxy(client);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
            await proxy.ServerTaskWithCancellation(cts.Token).Timeout(T(1)));

        await AssertNotOutstandingInProc(client, 1);
        await proxy.ServerTaskWithCancellation(CancellationToken.None).Timeout(T(1));
        await Assert.That(Volatile.Read(ref received)).IsEqualTo(1);
        await Assert.That(Volatile.Read(ref invocations)).IsEqualTo(1);
        await WaitForStateAsync(client, ConnectionState.Connected);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task RemoteMethodCanceledOnItsOwn_CancelsTheCaller(ClientTarget target)
    {
        // Without a reply the caller, which has no token, would wait forever.
        await using var server = await StartServerAsync(
            nexus => nexus.ServerTaskValueEvent = _ => throw new OperationCanceledException());
        await using var client = await ConnectAsync(target, server);

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await Proxy(client).ServerTaskValue().Timeout(T(1)));
        await WaitForStateAsync(client, ConnectionState.Connected);
        await EndAsync(client);
    }

    /// <summary>
    /// The client calls a server method with a cancellation timed in the client's host; the call throws and the server
    /// receives the cancellation of invocation 1.
    /// </summary>
    private async Task ClientSendsCancellationOnTimeout(
        ClientTarget target,
        Action<ServerNexus> setup,
        Func<Remote_IServerNexus, ValueTask> call)
    {
        var cancellation = Signal<ushort>();
        await using var server = await StartServerAsync(setup, config => ServerTaps.OnReceive(config, (_, frame) =>
        {
            if (frame.Type == MessageType.InvocationCancellation)
                cancellation.TrySetResult(TestSerialization.DeserializeMessage<InvocationCancellationMessage>(frame.Body).InvocationId);
        }));
        await using var client = await ConnectAsync(target, server);

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await call(Proxy(client)).Timeout(T(1)));
        await Assert.That(await cancellation.Task.Timeout(T(1))).IsEqualTo((ushort)1);
        await EndAsync(client);
    }

    /// <summary>Calls <paramref name="onInvocation"/> for every invocation frame the server receives.</summary>
    private static void CountInvocations(ServerConfig config, Action onInvocation)
        => ServerTaps.OnReceive(config, (_, frame) =>
        {
            if (frame.Type == MessageType.Invocation)
                onInvocation();
        });
}
