using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using System.Threading.Tasks;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexusServerTests_SendInvocation : BaseTests
{
    [Test]
    [Transports]
    public Task ServerSendsInvocationFor_ServerVoid(Type type)
    {
        return InvokeFromServerAndVerifySent(type, new InvocationMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.IgnoreReturn,
            InvocationId = 0, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 0
        }, nexus => nexus.Context.Clients.Caller.ClientVoid());
    }

    
    [Test]
    [Transports]
    public Task ServerSendsInvocationFor_ServerVoidWithParam(Type type)
    {
        return InvokeFromServerAndVerifySent(type, new InvocationMessage()
        {
            Arguments = TestSerialization.SerializeArguments(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.IgnoreReturn,
            InvocationId = 0, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 1
        }, nexus => nexus.Context.Clients.Caller.ClientVoidWithParam(54321));
    }
    
    
    [Test]
    [Transports]
    public Task ServerSendsInvocationFor_ServerTask(Type type)
    {
        return InvokeFromServerAndVerifySent(type, new InvocationMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.None,
            InvocationId = 1,
            MethodId = 2
        }, nexus => nexus.Context.Clients.Caller.ClientTask());
    }


    [Test]
    [Transports]
    public Task ServerSendsInvocationFor_ServerTaskWithParam(Type type)
    {
        return InvokeFromServerAndVerifySent(type, new InvocationMessage()
        {
            Arguments = TestSerialization.SerializeArguments(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 3
        }, nexus => nexus.Context.Clients.Caller.ClientTaskWithParam(54321));
    }


    [Test]
    [Transports(Type.Uds, Type.Tcp, Type.TcpTls)]
    public Task ServerSendsInvocationFor_ServerTaskValue(Type type)
    {
        return InvokeFromServerAndVerifySent(type, new InvocationMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 4
        }, nexus => nexus.Context.Clients.Caller.ClientTaskValue());
    }


    [Test]
    [Transports]
    public Task ServerSendsInvocationFor_ServerTaskValueWithParam(Type type)
    {
        return InvokeFromServerAndVerifySent(type, new InvocationMessage()
        {
            Arguments = TestSerialization.SerializeArguments(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 5
        }, nexus => nexus.Context.Clients.Caller.ClientTaskValueWithParam(54321));
    }



    [Test]
    [Transports]
    public Task ServerSendsInvocationFor_ServerTaskWithCancellation(Type type)
    {
        return InvokeFromServerAndVerifySent(type, new InvocationMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 6
        }, nexus => nexus.Context.Clients.Caller.ClientTaskWithCancellation(CancellationToken.None));
    }


    [Test]
    [Transports]
    public Task ServerSendsInvocationFor_ServerTaskWithValueAndCancellation(Type type)
    {
        return InvokeFromServerAndVerifySent(type, new InvocationMessage()
        {
            Arguments = TestSerialization.SerializeArguments(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 7
        }, nexus => nexus.Context.Clients.Caller.ClientTaskWithValueAndCancellation(54321, CancellationToken.None));
    }


    [Test]
    [Transports]
    public Task ServerSendsInvocationFor_ServerTaskValueWithCancellation(Type type)
    {
        return InvokeFromServerAndVerifySent(type, new InvocationMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 8
        }, nexus => nexus.Context.Clients.Caller.ClientTaskValueWithCancellation(CancellationToken.None));
    }


    [Test]
    [Transports]
    public Task ServerSendsInvocationFor_ServerTaskValueWithValueAndCancellation(Type type)
    {
        return InvokeFromServerAndVerifySent(type, new InvocationMessage()
        {
            Arguments = TestSerialization.SerializeArguments(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 9
        }, nexus => nexus.Context.Clients.Caller.ClientTaskValueWithValueAndCancellation(54321, CancellationToken.None));
    }
    
    private async Task InvokeFromServerAndVerifySent(Type type, InvocationMessage expectedMessage, Action<ServerNexus> action)
    {
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);
        
        server.OnNexusCreated = nexus => nexus.OnConnectedEvent = nexus2 =>
        {
            action(nexus2);
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);

        serverConfig.InternalOnSend = (_, bytes) =>
        {
            try
            {
                if (bytes[0] != (byte)MessageType.Invocation)
                    return;

                var message = TestSerialization.DeserializeMessage<InvocationMessage>(new ReadOnlySpan<byte>(bytes).Slice(3));
                CallbackAssert.NotNull(message);

                if (message == null)
                    return;

                CallbackAssert.Equal(message.Arguments.ToArray(), expectedMessage.Arguments.ToArray());
                CallbackAssert.Equal(message.Flags, expectedMessage.Flags);
                CallbackAssert.Equal(message.InvocationId, expectedMessage.InvocationId);
                CallbackAssert.Equal(message.MethodId, expectedMessage.MethodId);
                tcs.SetResult();
            }
            catch
            {
                // Not an invocationRequest.
            }

        };

        await client.ConnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }

}