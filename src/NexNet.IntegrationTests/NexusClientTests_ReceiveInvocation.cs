using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Interop.Hosts;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// A client under test receives invocations of its client nexus from the server (P01b): the client's script records
/// each call and its arguments, and returns the scripted result to the server.
/// </summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal partial class NexusClientTests_ReceiveInvocation(LanguageHostPool hosts) : InvocationMatrixTests(hosts)
{
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ClientReceivesInvocation_ClientVoid(ClientTarget target)
    {
        return ClientReceivesInvocation(
            target,
            nexus =>
            {
                nexus.Context.Clients.Caller.ClientVoid();
                return ValueTask.CompletedTask;
            },
            async script => await script.ClientVoid.NextCallAsync());
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ClientReceivesInvocation_ClientVoidWithParam(ClientTarget target)
    {
        return ClientReceivesInvocation(
            target,
            nexus =>
            {
                nexus.Context.Clients.Caller.ClientVoidWithParam(12345);
                return ValueTask.CompletedTask;
            },
            async script => await Assert.That((await script.ClientVoidWithParam.NextCallAsync()).id).IsEqualTo(12345));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ClientReceivesInvocation_ClientTask(ClientTarget target)
    {
        return ClientReceivesInvocation(
            target,
            nexus => nexus.Context.Clients.Caller.ClientTask(),
            async script => await script.ClientTask.NextCallAsync());
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ClientReceivesInvocation_ClientTaskWithParam(ClientTarget target)
    {
        return ClientReceivesInvocation(
            target,
            nexus => nexus.Context.Clients.Caller.ClientTaskWithParam(12345),
            async script => await Assert.That((await script.ClientTaskWithParam.NextCallAsync()).data).IsEqualTo(12345));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ClientReceivesInvocation_ClientTaskValue(ClientTarget target)
    {
        return ClientReceivesInvocation(
            target,
            async nexus => await nexus.Context.Clients.Caller.ClientTaskValue(),
            async script => await script.ClientTaskValue.NextCallAsync(),
            script => script.ClientTaskValue.Returns(54321));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public async Task ClientReceivesInvocation_ClientTaskValue_ReturnedValue(ClientTarget target)
    {
        var value = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await ClientReceivesInvocation(
            target,
            async nexus => value.TrySetResult(await nexus.Context.Clients.Caller.ClientTaskValue()),
            async _ => await Assert.That(await value.Task.Timeout(T(1))).IsEqualTo(54321),
            script => script.ClientTaskValue.Returns(54321));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ClientReceivesInvocation_ClientTaskValueWithParam(ClientTarget target)
    {
        return ClientReceivesInvocation(
            target,
            async nexus => await nexus.Context.Clients.Caller.ClientTaskValueWithParam(12345),
            async script => await Assert.That((await script.ClientTaskValueWithParam.NextCallAsync()).data).IsEqualTo(12345),
            script => script.ClientTaskValueWithParam.Returns(54321));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public async Task ClientReceivesInvocation_ClientTaskValueWithParam_ReturnedValue(ClientTarget target)
    {
        var value = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await ClientReceivesInvocation(
            target,
            async nexus => value.TrySetResult(await nexus.Context.Clients.Caller.ClientTaskValueWithParam(12345)),
            async _ => await Assert.That(await value.Task.Timeout(T(1))).IsEqualTo(54321),
            script => script.ClientTaskValueWithParam.Returns(54321));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ClientReceivesInvocation_ClientTaskWithCancellation(ClientTarget target)
    {
        return ClientReceivesInvocation(
            target,
            nexus => nexus.Context.Clients.Caller.ClientTaskWithCancellation(CancellationToken.None),
            async script => await script.ClientTaskWithCancellation.NextCallAsync());
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ClientReceivesInvocation_ClientTaskWithValueAndCancellation(ClientTarget target)
    {
        return ClientReceivesInvocation(
            target,
            nexus => nexus.Context.Clients.Caller.ClientTaskWithValueAndCancellation(12345, CancellationToken.None),
            async script => await Assert.That((await script.ClientTaskWithValueAndCancellation.NextCallAsync()).value).IsEqualTo(12345));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ClientReceivesInvocation_ClientTaskValueWithCancellation(ClientTarget target)
    {
        return ClientReceivesInvocation(
            target,
            async nexus => await nexus.Context.Clients.Caller.ClientTaskValueWithCancellation(CancellationToken.None),
            async script => await script.ClientTaskValueWithCancellation.NextCallAsync(),
            script => script.ClientTaskValueWithCancellation.Returns(54321));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public async Task ClientReceivesInvocation_ClientTaskValueWithCancellation_ReturnedValue(ClientTarget target)
    {
        var value = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await ClientReceivesInvocation(
            target,
            async nexus => value.TrySetResult(await nexus.Context.Clients.Caller.ClientTaskValueWithCancellation(CancellationToken.None)),
            async _ => await Assert.That(await value.Task.Timeout(T(1))).IsEqualTo(54321),
            script => script.ClientTaskValueWithCancellation.Returns(54321));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ClientReceivesInvocation_ClientTaskValueWithValueAndCancellation(ClientTarget target)
    {
        return ClientReceivesInvocation(
            target,
            async nexus => await nexus.Context.Clients.Caller.ClientTaskValueWithValueAndCancellation(12345, CancellationToken.None),
            async script => await Assert.That((await script.ClientTaskValueWithValueAndCancellation.NextCallAsync()).value).IsEqualTo(12345),
            script => script.ClientTaskValueWithValueAndCancellation.Returns(54321));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public async Task ClientReceivesInvocation_ClientTaskValueWithValueAndCancellation_ReturnedValue(ClientTarget target)
    {
        var value = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await ClientReceivesInvocation(
            target,
            async nexus => value.TrySetResult(await nexus.Context.Clients.Caller.ClientTaskValueWithValueAndCancellation(12345, CancellationToken.None)),
            async _ => await Assert.That(await value.Task.Timeout(T(1))).IsEqualTo(54321),
            script => script.ClientTaskValueWithValueAndCancellation.Returns(54321));
    }

    /// <summary>
    /// Connects a client whose script <paramref name="configureScript"/> set up, has the server run
    /// <paramref name="onConnected"/> once the client is connected, then runs <paramref name="verify"/> on the
    /// script.
    /// </summary>
    private async Task ClientReceivesInvocation(
        ClientTarget target,
        Func<ServerNexus, ValueTask> onConnected,
        Func<ClientNexusScript, Task> verify,
        Action<ClientNexusScript>? configureScript = null)
    {
        await using var server = await StartServer(nexus => nexus.OnConnectedEvent = onConnected);
        await using var client = await CreateClient(target, server);
        var script = Script(client);
        configureScript?.Invoke(script);
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        await verify(script).Timeout(T(1));
        await DisconnectAndAudit(client);
    }
}
