using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.Messages;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// The invocation frames the server sends to a client under test (P01b), observed as the server writes them; the
/// client answers each one with its unscripted default result.
/// </summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal partial class NexusServerTests_SendInvocation(LanguageHostPool hosts) : InvocationMatrixTests(hosts)
{
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ServerSendsInvocationFor_ServerVoid(ClientTarget target)
    {
        return InvokeFromServerAndVerifySent(target, new InvocationMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.IgnoreReturn,
            InvocationId = 0, // Invocations for void are always 0 as there is no returned value
            MethodId = 0
        }, nexus =>
        {
            nexus.Context.Clients.Caller.ClientVoid();
            return ValueTask.CompletedTask;
        });
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ServerSendsInvocationFor_ServerVoidWithParam(ClientTarget target)
    {
        return InvokeFromServerAndVerifySent(target, new InvocationMessage()
        {
            Arguments = TestSerialization.SerializeArguments(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.IgnoreReturn,
            InvocationId = 0, // Invocations for void are always 0 as there is no returned value
            MethodId = 1
        }, nexus =>
        {
            nexus.Context.Clients.Caller.ClientVoidWithParam(54321);
            return ValueTask.CompletedTask;
        });
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ServerSendsInvocationFor_ServerTask(ClientTarget target)
    {
        return InvokeFromServerAndVerifySent(target, new InvocationMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.None,
            InvocationId = 1,
            MethodId = 2
        }, nexus => nexus.Context.Clients.Caller.ClientTask());
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ServerSendsInvocationFor_ServerTaskWithParam(ClientTarget target)
    {
        return InvokeFromServerAndVerifySent(target, new InvocationMessage()
        {
            Arguments = TestSerialization.SerializeArguments(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1,
            MethodId = 3
        }, nexus => nexus.Context.Clients.Caller.ClientTaskWithParam(54321));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls])]
    public Task ServerSendsInvocationFor_ServerTaskValue(ClientTarget target)
    {
        return InvokeFromServerAndVerifySent(target, new InvocationMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.None,
            InvocationId = 1,
            MethodId = 4
        }, async nexus => await nexus.Context.Clients.Caller.ClientTaskValue());
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ServerSendsInvocationFor_ServerTaskValueWithParam(ClientTarget target)
    {
        return InvokeFromServerAndVerifySent(target, new InvocationMessage()
        {
            Arguments = TestSerialization.SerializeArguments(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1,
            MethodId = 5
        }, async nexus => await nexus.Context.Clients.Caller.ClientTaskValueWithParam(54321));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ServerSendsInvocationFor_ServerTaskWithCancellation(ClientTarget target)
    {
        return InvokeFromServerAndVerifySent(target, new InvocationMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.None,
            InvocationId = 1,
            MethodId = 6
        }, nexus => nexus.Context.Clients.Caller.ClientTaskWithCancellation(CancellationToken.None));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ServerSendsInvocationFor_ServerTaskWithValueAndCancellation(ClientTarget target)
    {
        return InvokeFromServerAndVerifySent(target, new InvocationMessage()
        {
            Arguments = TestSerialization.SerializeArguments(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1,
            MethodId = 7
        }, nexus => nexus.Context.Clients.Caller.ClientTaskWithValueAndCancellation(54321, CancellationToken.None));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ServerSendsInvocationFor_ServerTaskValueWithCancellation(ClientTarget target)
    {
        return InvokeFromServerAndVerifySent(target, new InvocationMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.None,
            InvocationId = 1,
            MethodId = 8
        }, async nexus => await nexus.Context.Clients.Caller.ClientTaskValueWithCancellation(CancellationToken.None));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public Task ServerSendsInvocationFor_ServerTaskValueWithValueAndCancellation(ClientTarget target)
    {
        return InvokeFromServerAndVerifySent(target, new InvocationMessage()
        {
            Arguments = TestSerialization.SerializeArguments(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1,
            MethodId = 9
        }, async nexus => await nexus.Context.Clients.Caller.ClientTaskValueWithValueAndCancellation(54321, CancellationToken.None));
    }

    private async Task InvokeFromServerAndVerifySent(ClientTarget target, InvocationMessage expectedMessage, Func<ServerNexus, ValueTask> action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartServer(
            nexus => nexus.OnConnectedEvent = async connected =>
            {
                try
                {
                    await action(connected);
                    invoked.TrySetResult();
                }
                catch (Exception e)
                {
                    invoked.TrySetException(e);
                }
            },
            config => ServerTaps.OnSend(config, (_, frame) =>
            {
                if (frame.Type != MessageType.Invocation)
                    return;

                var message = TestSerialization.DeserializeMessage<InvocationMessage>(frame.Body);
                CallbackAssert.NotNull(message);

                if (message == null)
                    return;

                CallbackAssert.Equal(message.Arguments.ToArray(), expectedMessage.Arguments.ToArray());
                CallbackAssert.Equal(message.Flags, expectedMessage.Flags);
                CallbackAssert.Equal(message.InvocationId, expectedMessage.InvocationId);
                CallbackAssert.Equal(message.MethodId, expectedMessage.MethodId);
                tcs.TrySetResult();
            }));
        await using var client = await Connect(target, server);

        await tcs.Task.Timeout(T(1));
        await invoked.Task.Timeout(T(1));
        await DisconnectAndAudit(client);
    }
}
