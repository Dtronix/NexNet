using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexusClientTests_SendInvocation : BaseTests
{
    [Test]
    [Transports]
    public Task ClientSendsInvocationFor_ServerVoid(Type type)
    {
        return InvokeFromClientAndVerifySent(type, new InvocationMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.IgnoreReturn,
            InvocationId = 0, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 0
        }, client => client.Proxy.ServerVoid());
    }


    [Test]
    [Transports]
    public Task ClientSendsInvocationFor_ServerVoidWithParam(Type type)
    {
        return InvokeFromClientAndVerifySent(type, new InvocationMessage()
        {
            Arguments = TestSerialization.SerializeArguments(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.IgnoreReturn,
            InvocationId = 0, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 1
        }, client => client.Proxy.ServerVoidWithParam(54321));
    }
    

    [Test]
    [Transports]
    public Task ClientSendsInvocationFor_ServerTask(Type type)
    {
        return InvokeFromClientAndVerifySent(type, new InvocationMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.None,
            InvocationId = 1,
            MethodId = 2
        }, client => client.Proxy.ServerTask());
    }


    [Test]
    [Transports]
    public Task ClientSendsInvocationFor_ServerTaskWithParam(Type type)
    {
        return InvokeFromClientAndVerifySent(type, new InvocationMessage()
        {
            Arguments = TestSerialization.SerializeArguments(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 3
        }, client => client.Proxy.ServerTaskWithParam(54321));
    }


    [Test]
    [Transports]
    public Task ClientSendsInvocationFor_ServerTaskValue(Type type)
    {
        return InvokeFromClientAndVerifySent(type, new InvocationMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 4
        }, client => client.Proxy.ServerTaskValue());
    }


    [Test]
    [Transports]
    public Task ClientSendsInvocationFor_ServerTaskValueWithParam(Type type)
    {
        return InvokeFromClientAndVerifySent(type, new InvocationMessage()
        {
            Arguments = TestSerialization.SerializeArguments(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 5
        }, client => client.Proxy.ServerTaskValueWithParam(54321));
    }



    [Test]
    [Transports]
    public Task ClientSendsInvocationFor_ServerTaskWithCancellation(Type type)
    {
        return InvokeFromClientAndVerifySent(type, new InvocationMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 6
        }, client => client.Proxy.ServerTaskWithCancellation(CancellationToken.None));
    }


    [Test]
    [Transports]
    public Task ClientSendsInvocationFor_ServerTaskWithValueAndCancellation(Type type)
    {
        return InvokeFromClientAndVerifySent(type, new InvocationMessage()
        {
            Arguments = TestSerialization.SerializeArguments(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 7
        }, client => client.Proxy.ServerTaskWithValueAndCancellation(54321, CancellationToken.None));
    }


    [Test]
    [Transports]
    public Task ClientSendsInvocationFor_ServerTaskValueWithCancellation(Type type)
    {
        return InvokeFromClientAndVerifySent(type, new InvocationMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 8
        }, client => client.Proxy.ServerTaskValueWithCancellation(CancellationToken.None));
    }


    [Test]
    [Transports]
    public Task ClientSendsInvocationFor_ServerTaskValueWithValueAndCancellation(Type type)
    {
        return InvokeFromClientAndVerifySent(type, new InvocationMessage()
        {
            Arguments = TestSerialization.SerializeArguments(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 9
        }, client => client.Proxy.ServerTaskValueWithValueAndCancellation(54321, CancellationToken.None));
    }

    private async Task InvokeFromClientAndVerifySent(Type type, InvocationMessage expectedMessage, Action<NexusClient<ClientNexus, ClientNexus.ServerProxy>> action)
    {
        var clientConfig = CreateClientConfig(type);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            clientConfig);

        await server.StartAsync().Timeout(1);

        clientConfig.InternalOnSend = (_, bytes) =>
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

        clientNexus.OnConnectedEvent = (nexus, b) =>
        {
            action.Invoke(client);
            return ValueTask.CompletedTask;
        };

        await client.ConnectAsync().Timeout(1);


        await tcs.Task.Timeout(1);
    }

}