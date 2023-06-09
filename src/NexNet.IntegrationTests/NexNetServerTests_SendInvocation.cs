﻿using MemoryPack;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexNetServerTests_SendInvocation : BaseTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerSendsInvocationFor_ServerVoid(Type type)
    {
        return InvokeFromServerAndVerifySent(type, new InvocationRequestMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.IgnoreReturn,
            InvocationId = 0, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 0
        }, hub => hub.Context.Clients.Caller.ClientVoid());
    }

    
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerSendsInvocationFor_ServerVoidWithParam(Type type)
    {
        return InvokeFromServerAndVerifySent(type, new InvocationRequestMessage()
        {
            Arguments = MemoryPackSerializer.Serialize(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.IgnoreReturn,
            InvocationId = 0, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 1
        }, hub => hub.Context.Clients.Caller.ClientVoidWithParam(54321));
    }
    
    
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerSendsInvocationFor_ServerTask(Type type)
    {
        return InvokeFromServerAndVerifySent(type, new InvocationRequestMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.None,
            InvocationId = 1,
            MethodId = 2
        }, hub => hub.Context.Clients.Caller.ClientTask());
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerSendsInvocationFor_ServerTaskWithParam(Type type)
    {
        return InvokeFromServerAndVerifySent(type, new InvocationRequestMessage()
        {
            Arguments = MemoryPackSerializer.Serialize(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 3
        }, hub => hub.Context.Clients.Caller.ClientTaskWithParam(54321));
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerSendsInvocationFor_ServerTaskValue(Type type)
    {
        return InvokeFromServerAndVerifySent(type, new InvocationRequestMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 4
        }, hub => hub.Context.Clients.Caller.ClientTaskValue());
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerSendsInvocationFor_ServerTaskValueWithParam(Type type)
    {
        return InvokeFromServerAndVerifySent(type, new InvocationRequestMessage()
        {
            Arguments = MemoryPackSerializer.Serialize(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 5
        }, hub => hub.Context.Clients.Caller.ClientTaskValueWithParam(54321));
    }



    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerSendsInvocationFor_ServerTaskWithCancellation(Type type)
    {
        return InvokeFromServerAndVerifySent(type, new InvocationRequestMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 6
        }, hub => hub.Context.Clients.Caller.ClientTaskWithCancellation(CancellationToken.None));
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerSendsInvocationFor_ServerTaskWithValueAndCancellation(Type type)
    {
        return InvokeFromServerAndVerifySent(type, new InvocationRequestMessage()
        {
            Arguments = MemoryPackSerializer.Serialize(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 7
        }, hub => hub.Context.Clients.Caller.ClientTaskWithValueAndCancellation(54321, CancellationToken.None));
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerSendsInvocationFor_ServerTaskValueWithCancellation(Type type)
    {
        return InvokeFromServerAndVerifySent(type, new InvocationRequestMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 8
        }, hub => hub.Context.Clients.Caller.ClientTaskValueWithCancellation(CancellationToken.None));
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerSendsInvocationFor_ServerTaskValueWithValueAndCancellation(Type type)
    {
        return InvokeFromServerAndVerifySent(type, new InvocationRequestMessage()
        {
            Arguments = MemoryPackSerializer.Serialize(new ValueTuple<int>(54321)),
            Flags = InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 9
        }, hub => hub.Context.Clients.Caller.ClientTaskValueWithValueAndCancellation(54321, CancellationToken.None));
    }
    
    private async Task InvokeFromServerAndVerifySent(Type type, InvocationRequestMessage expectedMessage, Action<ServerHub> action)
    {
        var clientConfig = CreateClientConfig(type, false);
        var serverConfig = CreateServerConfig(type, false);
        var tcs = new TaskCompletionSource();
        var (server, serverHub, client, clientHub) = CreateServerClient(serverConfig, clientConfig);

        server.Start();

        serverConfig.InternalOnSend = (_, bytes) =>
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

        serverHub.OnConnectedEvent = hub =>
        {
            action(hub);
            return ValueTask.CompletedTask;
        };

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

}
