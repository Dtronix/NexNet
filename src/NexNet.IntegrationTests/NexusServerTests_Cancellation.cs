using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.Messages;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Cancellation of invocations the C# server makes on the client under test: the client's methods are scripted to
/// wait for the cancellation, and the server's frames are observed with <see cref="ServerTaps"/>.
/// </summary>
/// <remarks>
/// The QUIC rows of the C# suite run on WebSocketTls: the interop server has no QUIC endpoint and no other client
/// language has QUIC.
/// </remarks>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class NexusServerTests_Cancellation(LanguageHostPool hosts) : CancellationMatrixTests(hosts)
{
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public Task SendsCancellationTokenOnTimeout_ClientTaskValueWithParam(ClientTarget target)
        => ServerSendsCancellationOnTimeout(
            target,
            script => script.ClientTaskWithCancellation.AwaitCancel(),
            caller => caller.ClientTaskWithCancellation(new CancellationTokenSource(100).Token));

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public Task SendsCancellationTokenOnTimeout_ServerTaskWithValueAndCancellation(ClientTarget target)
        => ServerSendsCancellationOnTimeout(
            target,
            script => script.ClientTaskWithValueAndCancellation.AwaitCancel(),
            caller => caller.ClientTaskWithValueAndCancellation(1234, new CancellationTokenSource(100).Token));

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public Task SendsCancellationTokenOnTimeout_ServerTaskValueWithCancellation(ClientTarget target)
        => ServerSendsCancellationOnTimeout(
            target,
            script => script.ClientTaskValueWithCancellation.AwaitCancel().Returns(12345),
            async caller => await caller.ClientTaskValueWithCancellation(new CancellationTokenSource(100).Token));

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public Task SendsCancellationTokenOnTimeout_ServerTaskValueWithValueAndCancellation(ClientTarget target)
        => ServerSendsCancellationOnTimeout(
            target,
            script => script.ClientTaskValueWithValueAndCancellation.AwaitCancel().Returns(12345),
            async caller => await caller.ClientTaskValueWithValueAndCancellation(1234, new CancellationTokenSource(100).Token));

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task ClientDoesNotSendCancellationAfterCompletion(ClientTarget target)
    {
        using var cts = new CancellationTokenSource();
        var cancellations = 0;
        var completed = Signal<ServerNexus>();
        await using var server = await StartServerAsync(
            nexus => nexus.OnConnectedEvent = connected =>
            {
                _ = CallThenSignal(connected);
                return ValueTask.CompletedTask;
            },
            config => ServerTaps.OnSend(config, (_, frame) =>
            {
                if (frame.Type == MessageType.InvocationCancellation)
                    Interlocked.Increment(ref cancellations);
            }));

        // Unscripted, the client method completes at once.
        await using var client = await ConnectAsync(target, server);
        var serverNexus = await completed.Task.Timeout(T(1));

        // Canceled after the call completed: there is nothing left to cancel.
        await cts.CancelAsync();
        await Barrier(serverNexus.Context.Clients.Caller);
        await Assert.That(Volatile.Read(ref cancellations)).IsEqualTo(0)
            .Because("The server must not send a cancellation after the call completed.");
        await EndAsync(client);

        async Task CallThenSignal(ServerNexus nexus)
        {
            try
            {
                await nexus.Context.Clients.Caller.ClientTaskWithCancellation(cts.Token);
                completed.TrySetResult(nexus);
            }
            catch (Exception e)
            {
                completed.TrySetException(e);
            }
        }
    }

    /// <summary>
    /// When the client connects, the server calls a client method that waits for its cancellation, with a token that
    /// cancels after 100 ms; the server's call throws and it sends the cancellation of invocation 1.
    /// </summary>
    private async Task ServerSendsCancellationOnTimeout(
        ClientTarget target,
        Action<ClientNexusScript> script,
        Func<ServerNexus.ClientProxy, ValueTask> call)
    {
        var cancellation = Signal<ushort>();
        var serverCall = Signal<Task>();
        await using var server = await StartServerAsync(
            nexus => nexus.OnConnectedEvent = connected =>
            {
                serverCall.TrySetResult(call(connected.Context.Clients.Caller).AsTask());
                return ValueTask.CompletedTask;
            },
            config => ServerTaps.OnSend(config, (_, frame) =>
            {
                if (frame.Type == MessageType.InvocationCancellation)
                    cancellation.TrySetResult(TestSerialization.DeserializeMessage<InvocationCancellationMessage>(frame.Body).InvocationId);
            }));
        await using var client = await ConnectAsync(target, server, script);

        var pending = await serverCall.Task.Timeout(T(1));
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await pending.Timeout(T(1)));
        await Assert.That(await cancellation.Task.Timeout(T(1))).IsEqualTo((ushort)1);
        await EndAsync(client);
    }
}
