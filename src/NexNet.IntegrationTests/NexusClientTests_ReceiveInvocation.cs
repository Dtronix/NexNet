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

    // ----- C#-only QUIC companions: the interop server has no QUIC endpoint yet, so the C# client keeps its QUIC
    // coverage of these tests here until the client matrix gains C#-only rows (H16). -----

    
    [Test]
    [Transports(Type.Quic)]
    public Task ClientReceivesInvocation_ClientVoid_Quic(Type type)
    {
        return ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = nexus =>
            {
                nexus.Context.Clients.Caller.ClientVoid();
                return ValueTask.CompletedTask;
            };
            cNexus.ClientVoidEvent = nexus => tcs.SetResult();
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public Task ClientReceivesInvocation_ClientVoidWithParam_Quic(Type type)
    {
        return ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = nexus =>
            {
                nexus.Context.Clients.Caller.ClientVoidWithParam(12345);
                return ValueTask.CompletedTask;
            };
            cNexus.ClientVoidWithParamEvent = (nexus, param) =>
            {
                if (param == 12345)
                    tcs.SetResult();
            };
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public Task ClientReceivesInvocation_ClientTask_Quic(Type type)
    {
        return ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = nexus => nexus.Context.Clients.Caller.ClientTask();
            cNexus.ClientTaskEvent = nexus =>
            {
                tcs.SetResult();
                return ValueTask.CompletedTask;
            };
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public Task ClientReceivesInvocation_ClientTaskWithParam_Quic(Type type)
    {
        return ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = nexus => nexus.Context.Clients.Caller.ClientTaskWithParam(12345);
            cNexus.ClientTaskWithParamEvent = (nexus, param) =>
            {
                if (param == 12345)
                    tcs.SetResult();
                return ValueTask.CompletedTask;
            };
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public Task ClientReceivesInvocation_ClientTaskValue_Quic(Type type)
    {
        return ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = async nexus => await nexus.Context.Clients.Caller.ClientTaskValue();
            cNexus.ClientTaskValueEvent = nexus =>
            {
                tcs.SetResult();
                return ValueTask.FromResult(54321);
            };
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ClientReceivesInvocation_ClientTaskValue_ReturnedValue_Quic(Type type)
    {
        await ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = async nexus =>
            {
                var value = await nexus.Context.Clients.Caller.ClientTaskValue();
                await Assert.That(value).IsEqualTo(54321);
                tcs.SetResult();
            };
            cNexus.ClientTaskValueEvent = nexus => ValueTask.FromResult(54321);
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public Task ClientReceivesInvocation_ClientTaskValueWithParam_Quic(Type type)
    {
        return ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = async nexus => await nexus.Context.Clients.Caller.ClientTaskValueWithParam(12345);
            cNexus.ClientTaskValueWithParamEvent = (nexus, param) =>
            {
                if (param == 12345)
                    tcs.SetResult();

                return ValueTask.FromResult(54321);
            };
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ClientReceivesInvocation_ClientTaskValueWithParam_ReturnedValue_Quic(Type type)
    {
        await ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = async nexus =>
            {
                var value = await nexus.Context.Clients.Caller.ClientTaskValueWithParam(12345);
                await Assert.That(value).IsEqualTo(54321);
                tcs.SetResult();
            };
            cNexus.ClientTaskValueWithParamEvent = (nexus, param) => ValueTask.FromResult(54321);
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public Task ClientReceivesInvocation_ClientTaskWithCancellation_Quic(Type type)
    {
        return ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = async nexus => await nexus.Context.Clients.Caller.ClientTaskWithCancellation(CancellationToken.None);
            cNexus.ClientTaskWithCancellationEvent = (nexus, ct) =>
            {
                tcs.SetResult();
                return ValueTask.CompletedTask;
            };
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public Task ClientReceivesInvocation_ClientTaskWithValueAndCancellation_Quic(Type type)
    {
        return ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = async nexus => await nexus.Context.Clients.Caller.ClientTaskWithValueAndCancellation(12345, CancellationToken.None);
            cNexus.ClientTaskWithValueAndCancellationEvent = (nexus, param, ct) =>
            {
                if (param == 12345)
                    tcs.SetResult();
                return ValueTask.CompletedTask;
            };
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public Task ClientReceivesInvocation_ClientTaskValueWithCancellation_Quic(Type type)
    {
        return ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = async nexus => await nexus.Context.Clients.Caller.ClientTaskValueWithCancellation(CancellationToken.None);
            cNexus.ClientTaskValueWithCancellationEvent = (nexus, ct) =>
            {
                tcs.SetResult();
                return ValueTask.FromResult(54321);
            };
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ClientReceivesInvocation_ClientTaskValueWithCancellation_ReturnedValue_Quic(Type type)
    {
        await ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = async nexus =>
            {
                var value = await nexus.Context.Clients.Caller.ClientTaskValueWithCancellation(CancellationToken.None);
                await Assert.That(value).IsEqualTo(54321);
                tcs.SetResult();
            };
            cNexus.ClientTaskValueWithCancellationEvent = (nexus, ct) => ValueTask.FromResult(54321);
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public Task ClientReceivesInvocation_ClientTaskValueWithValueAndCancellation_Quic(Type type)
    {
        return ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = async nexus => await nexus.Context.Clients.Caller.ClientTaskValueWithValueAndCancellation(12345, CancellationToken.None);
            cNexus.ClientTaskValueWithValueAndCancellationEvent = (nexus, param, ct) =>
            {
                if (param == 12345)
                    tcs.SetResult();

                return ValueTask.FromResult(54321);
            };
        });
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ClientReceivesInvocation_ClientTaskValueWithValueAndCancellation_ReturnedValue_Quic(Type type)
    {
        await ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = async nexus =>
            {
                var value = await nexus.Context.Clients.Caller.ClientTaskValueWithValueAndCancellation(12345, CancellationToken.None);
                await Assert.That(value).IsEqualTo(54321);
                tcs.SetResult();
            };
            cNexus.ClientTaskValueWithValueAndCancellationEvent = (nexus, param, ct) => ValueTask.FromResult(54321);
        });
    }

    private async Task ClientReceivesInvocation(Type type, Action<ServerNexus, ClientNexus, TaskCompletionSource> action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        
        server.OnNexusCreated = nexus => action(nexus, clientNexus, tcs);
        
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        await tcs.Task.Timeout(T(1));
    }
}
