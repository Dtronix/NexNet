using MemoryPack;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NUnit.Framework;

namespace NexNet.IntegrationTests;

internal partial class NexNetClientTests : BaseTests
{
    
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ClientReceivesInvocation_ClientVoid(Type type)
    {
        return ClientReceivesInvocation(type, (sHub, cHub, tcs) =>
        {
            sHub.OnConnectedEvent = async hub => hub.Context.Clients.Caller.ClientVoid();
            cHub.ClientVoidEvent = hub => tcs.SetResult();
        });
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ClientReceivesInvocation_ClientVoidWithParam(Type type)
    {
        return ClientReceivesInvocation(type, (sHub, cHub, tcs) =>
        {
            sHub.OnConnectedEvent = async hub => hub.Context.Clients.Caller.ClientVoidWithParam(12345);
            cHub.ClientVoidWithParamEvent = (hub, param) =>
            {
                if (param == 12345)
                    tcs.SetResult();
            };
        });
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ClientReceivesInvocation_ClientTask(Type type)
    {
        return ClientReceivesInvocation(type, (sHub, cHub, tcs) =>
        {
            sHub.OnConnectedEvent = hub => hub.Context.Clients.Caller.ClientTask();
            cHub.ClientTaskEvent = async hub => tcs.SetResult();
        });
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ClientReceivesInvocation_ClientTaskWithParam(Type type)
    {
        return ClientReceivesInvocation(type, (sHub, cHub, tcs) =>
        {
            sHub.OnConnectedEvent = hub => hub.Context.Clients.Caller.ClientTaskWithParam(12345);
            cHub.ClientTaskWithParamEvent = async (hub, param) =>
            {
                if (param == 12345)
                    tcs.SetResult();
            };
        });
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ClientReceivesInvocation_ClientTaskValue(Type type)
    {
        return ClientReceivesInvocation(type, (sHub, cHub, tcs) =>
        {
            sHub.OnConnectedEvent = async hub => await hub.Context.Clients.Caller.ClientTaskValue();
            cHub.ClientTaskValueEvent = async hub =>
            {
                tcs.SetResult();
                return 54321;
            };
        });
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ClientReceivesInvocation_ClientTaskValueWithParam(Type type)
    {
        return ClientReceivesInvocation(type, (sHub, cHub, tcs) =>
        {
            sHub.OnConnectedEvent = async hub => await hub.Context.Clients.Caller.ClientTaskValueWithParam(12345);
            cHub.ClientTaskValueWithParamEvent = async (hub, param) =>
            {
                if (param == 12345)
                    tcs.SetResult();

                return 54321;
            };
        });
    }



    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ClientReceivesInvocation_ClientTaskWithCancellation(Type type)
    {
        return ClientReceivesInvocation(type, (sHub, cHub, tcs) =>
        {
            sHub.OnConnectedEvent = async hub => await hub.Context.Clients.Caller.ClientTaskWithCancellation(CancellationToken.None);
            cHub.ClientTaskWithCancellationEvent = async (hub, ct) =>
            {
                tcs.SetResult();
            };
        });
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ClientReceivesInvocation_ClientTaskWithValueAndCancellation(Type type)
    {
        return ClientReceivesInvocation(type, (sHub, cHub, tcs) =>
        {
            sHub.OnConnectedEvent = async hub => await hub.Context.Clients.Caller.ClientTaskWithValueAndCancellation(12345, CancellationToken.None);
            cHub.ClientTaskWithValueAndCancellationEvent = async (hub, param, ct) =>
            {
                if (param == 12345)
                    tcs.SetResult();
            };
        });
    }

    private async Task ClientReceivesInvocation(Type type, Action<ServerHub, ClientHub, TaskCompletionSource> action)
    {
        var tcs = new TaskCompletionSource();
        var (server, serverHub, client, clientHub) = CreateServerClient(
            CreateServerConfig(type, true),
            CreateClientConfig(type, true));

        server.Start();

        action(serverHub, clientHub, tcs);

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ClientSendsInvocationFor_ServerVoid(Type type)
    {
        return InvokeFromClientAndVerifySent(type, new InvocationRequestMessage()
        {
            Arguments = Memory<byte>.Empty,
            Flags = InvocationRequestMessage.InvocationFlags.IgnoreReturn,
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
            Flags = InvocationRequestMessage.InvocationFlags.IgnoreReturn,
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
            Flags = InvocationRequestMessage.InvocationFlags.None,
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
            Flags = InvocationRequestMessage.InvocationFlags.None,
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
            Flags = InvocationRequestMessage.InvocationFlags.None,
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
            Flags = InvocationRequestMessage.InvocationFlags.None,
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
            Flags = InvocationRequestMessage.InvocationFlags.None,
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
            Flags = InvocationRequestMessage.InvocationFlags.None,
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
            Flags = InvocationRequestMessage.InvocationFlags.None,
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
            Flags = InvocationRequestMessage.InvocationFlags.None,
            InvocationId = 1, // Invocations for void area always 0 as there is not to be a returned value
            MethodId = 9
        }, client => client.Proxy.ServerTaskValueWithValueAndCancellation(54321, CancellationToken.None));
    }

    private async Task InvokeFromClientAndVerifySent(Type type, InvocationRequestMessage expectedMessage, Action<NexNetClient<ClientHub, ServerHubProxyImpl>> action)
    {
        var clientConfig = CreateClientConfig(type, true);
        var tcs = new TaskCompletionSource();
        var (server, serverHub, client, clientHub) = CreateServerClient(
            CreateServerConfig(type, true),
            clientConfig);

        server.Start();

        clientConfig.InternalOnSend = (_, bytes) =>
        {
            try
            {
                var message = MemoryPackSerializer.Deserialize<InvocationRequestMessage>(new ReadOnlySpan<byte>(bytes).Slice(3));
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

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        action.Invoke(client);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

}
