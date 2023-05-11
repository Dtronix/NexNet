using MemoryPack;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexNetClientTests_SendInvocation : BaseTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ClientSendsInvocationFor_ServerVoid(Type type)
    {
        return InvokeFromClientAndVerifySent(type, new InvocationRequestMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.IgnoreReturn,
            InvocationId = 0, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 0
        }, client => client.Proxy.ServerVoid());
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ClientSendsInvocationFor_ServerVoidWithParam(Type type)
    {
        return InvokeFromClientAndVerifySent(type, new InvocationRequestMessage()
        {
            Arguments = MemoryPackSerializer.Serialize(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.IgnoreReturn,
            InvocationId = 0, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 1
        }, client => client.Proxy.ServerVoidWithParam(54321));
    }
    

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ClientSendsInvocationFor_ServerTask(Type type)
    {
        return InvokeFromClientAndVerifySent(type, new InvocationRequestMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.None,
            InvocationId = 1,
            MethodId = 2
        }, client => client.Proxy.ServerTask());
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ClientSendsInvocationFor_ServerTaskWithParam(Type type)
    {
        return InvokeFromClientAndVerifySent(type, new InvocationRequestMessage()
        {
            Arguments = MemoryPackSerializer.Serialize(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 3
        }, client => client.Proxy.ServerTaskWithParam(54321));
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ClientSendsInvocationFor_ServerTaskValue(Type type)
    {
        return InvokeFromClientAndVerifySent(type, new InvocationRequestMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 4
        }, client => client.Proxy.ServerTaskValue());
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ClientSendsInvocationFor_ServerTaskValueWithParam(Type type)
    {
        return InvokeFromClientAndVerifySent(type, new InvocationRequestMessage()
        {
            Arguments = MemoryPackSerializer.Serialize(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 5
        }, client => client.Proxy.ServerTaskValueWithParam(54321));
    }



    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ClientSendsInvocationFor_ServerTaskWithCancellation(Type type)
    {
        return InvokeFromClientAndVerifySent(type, new InvocationRequestMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 6
        }, client => client.Proxy.ServerTaskWithCancellation(CancellationToken.None));
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ClientSendsInvocationFor_ServerTaskWithValueAndCancellation(Type type)
    {
        return InvokeFromClientAndVerifySent(type, new InvocationRequestMessage()
        {
            Arguments = MemoryPackSerializer.Serialize(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 7
        }, client => client.Proxy.ServerTaskWithValueAndCancellation(54321, CancellationToken.None));
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ClientSendsInvocationFor_ServerTaskValueWithCancellation(Type type)
    {
        return InvokeFromClientAndVerifySent(type, new InvocationRequestMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 8
        }, client => client.Proxy.ServerTaskValueWithCancellation(CancellationToken.None));
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ClientSendsInvocationFor_ServerTaskValueWithValueAndCancellation(Type type)
    {
        return InvokeFromClientAndVerifySent(type, new InvocationRequestMessage()
        {
            Arguments = MemoryPackSerializer.Serialize(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 9
        }, client => client.Proxy.ServerTaskValueWithValueAndCancellation(54321, CancellationToken.None));
    }

    private async Task InvokeFromClientAndVerifySent(Type type, InvocationRequestMessage expectedMessage, Action<NexNetClient<ClientHub, ClientHub.ServerProxy>> action)
    {
        var clientConfig = CreateClientConfig(type, false);
        var tcs = new TaskCompletionSource();
        var (server, serverHub, client, clientHub) = CreateServerClient(
            CreateServerConfig(type, false),
            clientConfig);

        server.Start();

        clientConfig.InternalOnSend = (_, bytes) =>
        {
            try
            {
                if (bytes[0] != 101)
                    return;

                var message = MemoryPackSerializer.Deserialize<InvocationRequestMessage>(new ReadOnlySpan<byte>(bytes).Slice(3));
                Assert.NotNull(message);

                if (message == null)
                    return;

                Assert.AreEqual(expectedMessage.Arguments.ToArray(), message.Arguments.ToArray());
                Assert.AreEqual(expectedMessage.Flags, message.Flags);
                Assert.AreEqual(expectedMessage.InvocationId, message.InvocationId);
                Assert.AreEqual(expectedMessage.MethodId, message.MethodId);
                tcs.SetResult();
            }
            catch
            {
                // Not an invocationRequest.
            }

        };

        clientHub.OnConnectedEvent = (hub, b) =>
        {
            action.Invoke(client);
            return ValueTask.CompletedTask;
        };

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));


        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

}
