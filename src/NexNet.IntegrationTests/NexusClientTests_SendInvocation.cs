using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.Messages;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// The invocation frames a client under test sends (P01b): observed on the server as they arrive, so every language
/// must encode them byte for byte like the C# client.
/// </summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal partial class NexusClientTests_SendInvocation(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    /// <inheritdoc />
    protected override bool CompleteServerMethodsByDefault => true;

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public Task ClientSendsInvocationFor_ServerVoid(ClientTarget target)
    {
        return InvokeFromClientAndVerifySent(target, new InvocationMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.IgnoreReturn,
            InvocationId = 0, // Invocations for void are always 0 as there is no returned value
            MethodId = 0
        }, p => { p.ServerVoid(); return ValueTask.CompletedTask; });
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public Task ClientSendsInvocationFor_ServerVoidWithParam(ClientTarget target)
    {
        return InvokeFromClientAndVerifySent(target, new InvocationMessage()
        {
            Arguments = TestSerialization.SerializeArguments(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.IgnoreReturn,
            InvocationId = 0, // Invocations for void are always 0 as there is no returned value
            MethodId = 1
        }, p => { p.ServerVoidWithParam(54321); return ValueTask.CompletedTask; });
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public Task ClientSendsInvocationFor_ServerTask(ClientTarget target)
    {
        return InvokeFromClientAndVerifySent(target, new InvocationMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.None,
            InvocationId = 1,
            MethodId = 2
        }, p => p.ServerTask());
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public Task ClientSendsInvocationFor_ServerTaskWithParam(ClientTarget target)
    {
        return InvokeFromClientAndVerifySent(target, new InvocationMessage()
        {
            Arguments = TestSerialization.SerializeArguments(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1,
            MethodId = 3
        }, p => p.ServerTaskWithParam(54321));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public Task ClientSendsInvocationFor_ServerTaskValue(ClientTarget target)
    {
        return InvokeFromClientAndVerifySent(target, new InvocationMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.None,
            InvocationId = 1,
            MethodId = 4
        }, async p => await p.ServerTaskValue());
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public Task ClientSendsInvocationFor_ServerTaskValueWithParam(ClientTarget target)
    {
        return InvokeFromClientAndVerifySent(target, new InvocationMessage()
        {
            Arguments = TestSerialization.SerializeArguments(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1,
            MethodId = 5
        }, async p => await p.ServerTaskValueWithParam(54321));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public Task ClientSendsInvocationFor_ServerTaskWithCancellation(ClientTarget target)
    {
        return InvokeFromClientAndVerifySent(target, new InvocationMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.None,
            InvocationId = 1,
            MethodId = 6
        }, p => p.ServerTaskWithCancellation(CancellationToken.None));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public Task ClientSendsInvocationFor_ServerTaskWithValueAndCancellation(ClientTarget target)
    {
        return InvokeFromClientAndVerifySent(target, new InvocationMessage()
        {
            Arguments = TestSerialization.SerializeArguments(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1,
            MethodId = 7
        }, p => p.ServerTaskWithValueAndCancellation(54321, CancellationToken.None));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public Task ClientSendsInvocationFor_ServerTaskValueWithCancellation(ClientTarget target)
    {
        return InvokeFromClientAndVerifySent(target, new InvocationMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.None,
            InvocationId = 1,
            MethodId = 8
        }, async p => await p.ServerTaskValueWithCancellation(CancellationToken.None));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public Task ClientSendsInvocationFor_ServerTaskValueWithValueAndCancellation(ClientTarget target)
    {
        return InvokeFromClientAndVerifySent(target, new InvocationMessage()
        {
            Arguments = TestSerialization.SerializeArguments(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1,
            MethodId = 9
        }, async p => await p.ServerTaskValueWithValueAndCancellation(54321, CancellationToken.None));
    }

    private async Task InvokeFromClientAndVerifySent(ClientTarget target, InvocationMessage expectedMessage, Func<Remote_IServerNexus, ValueTask> action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Observed on the server: the invocation frame as it arrives.
        await using var server = await StartServerAsync(configure: config => ServerTaps.OnReceive(config, (_, frame) =>
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
        await using var client = await ConnectAsync(target, server);

        await action(Proxy(client)).AsTask().Timeout(T(1));
        await tcs.Task.Timeout(T(1));
        await client.BarrierAsync().Timeout(T(1));
        await EndAsync(client);
    }
}
