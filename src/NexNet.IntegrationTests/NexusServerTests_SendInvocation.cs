using MemoryPack;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexusServerTests_SendInvocation : BaseTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
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

    
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public Task ServerSendsInvocationFor_ServerVoidWithParam(Type type)
    {
        return InvokeFromServerAndVerifySent(type, new InvocationMessage()
        {
            Arguments = MemoryPackSerializer.Serialize(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.IgnoreReturn,
            InvocationId = 0, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 1
        }, nexus => nexus.Context.Clients.Caller.ClientVoidWithParam(54321));
    }
    
    
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
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


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public Task ServerSendsInvocationFor_ServerTaskWithParam(Type type)
    {
        return InvokeFromServerAndVerifySent(type, new InvocationMessage()
        {
            Arguments = MemoryPackSerializer.Serialize(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 3
        }, nexus => nexus.Context.Clients.Caller.ClientTaskWithParam(54321));
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
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


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public Task ServerSendsInvocationFor_ServerTaskValueWithParam(Type type)
    {
        return InvokeFromServerAndVerifySent(type, new InvocationMessage()
        {
            Arguments = MemoryPackSerializer.Serialize(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 5
        }, nexus => nexus.Context.Clients.Caller.ClientTaskValueWithParam(54321));
    }



    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
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


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public Task ServerSendsInvocationFor_ServerTaskWithValueAndCancellation(Type type)
    {
        return InvokeFromServerAndVerifySent(type, new InvocationMessage()
        {
            Arguments = MemoryPackSerializer.Serialize(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 7
        }, nexus => nexus.Context.Clients.Caller.ClientTaskWithValueAndCancellation(54321, CancellationToken.None));
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
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


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public Task ServerSendsInvocationFor_ServerTaskValueWithValueAndCancellation(Type type)
    {
        return InvokeFromServerAndVerifySent(type, new InvocationMessage()
        {
            Arguments = MemoryPackSerializer.Serialize(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 9
        }, nexus => nexus.Context.Clients.Caller.ClientTaskValueWithValueAndCancellation(54321, CancellationToken.None));
    }
    
    private async Task InvokeFromServerAndVerifySent(Type type, InvocationMessage expectedMessage, Action<ServerNexus> action)
    {
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        var tcs = new TaskCompletionSource();
        var (server, serverNexus, client, clientNexus) = CreateServerClient(serverConfig, clientConfig);

        await server.StartAsync().Timeout(1);

        serverConfig.InternalOnSend = (_, bytes) =>
        {
            try
            {
                if (bytes[0] != (byte)MessageType.Invocation)
                    return;

                var message = MemoryPackSerializer.Deserialize<InvocationMessage>(new ReadOnlySpan<byte>(bytes).Slice(3));
                Assert.That(message, Is.Not.Null);

                if (message == null)
                    return;

                Assert.That(message.Arguments.ToArray(), Is.EqualTo(expectedMessage.Arguments.ToArray()));
                Assert.That(message.Flags, Is.EqualTo(expectedMessage.Flags));
                Assert.That(message.InvocationId, Is.EqualTo(expectedMessage.InvocationId));
                Assert.That(message.MethodId, Is.EqualTo(expectedMessage.MethodId));
                tcs.SetResult();
            }
            catch
            {
                // Not an invocationRequest.
            }

        };

        serverNexus.OnConnectedEvent = nexus =>
        {
            action(nexus);
            return ValueTask.CompletedTask;
        };

        await client.ConnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }

}
