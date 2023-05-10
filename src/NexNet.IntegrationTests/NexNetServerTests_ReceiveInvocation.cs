using MemoryPack;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexNetServerTests_ReceiveInvocation : BaseTests
{
    
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerReceivesInvocation_ServerVoid(Type type)
    {
        return ServerReceivesInvocation(type, (sHub, cHub, tcs) =>
        {
            cHub.OnConnectedEvent = (hub, _) =>
            {
                hub.Context.Proxy.ServerVoid();
                return ValueTask.CompletedTask;
            };
            sHub.ServerVoidEvent = hub => tcs.SetResult();
        });
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerReceivesInvocation_ServerVoidWithParam(Type type)
    {
        return ServerReceivesInvocation(type, (sHub, cHub, tcs) =>
        {
            cHub.OnConnectedEvent = (hub, _) =>
            {
                hub.Context.Proxy.ServerVoidWithParam(12345);
                return ValueTask.CompletedTask;
            };
            sHub.ServerVoidWithParamEvent = (hub, param) =>
            {
                if (param == 12345)
                    tcs.SetResult();
            };
        });
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerReceivesInvocation_ServerTask(Type type)
    {
        return ServerReceivesInvocation(type, (sHub, cHub, tcs) =>
        {
            cHub.OnConnectedEvent = (hub, _) => hub.Context.Proxy.ServerTask();
            sHub.ServerTaskEvent = hub =>
            {
                tcs.SetResult();
                return ValueTask.CompletedTask;
            };
        });
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerReceivesInvocation_ServerTaskWithParam(Type type)
    {
        return ServerReceivesInvocation(type, (sHub, cHub, tcs) =>
        {
            cHub.OnConnectedEvent = (hub, _) => hub.Context.Proxy.ServerTaskWithParam(12345);
            sHub.ServerTaskWithParamEvent = (hub, param) =>
            {
                if (param == 12345)
                    tcs.SetResult();
                return ValueTask.CompletedTask;
            };
        });
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerReceivesInvocation_ServerTaskValue(Type type)
    {
        return ServerReceivesInvocation(type, (sHub, cHub, tcs) =>
        {
            cHub.OnConnectedEvent = async (hub, _) => await hub.Context.Proxy.ServerTaskValue();
            sHub.ServerTaskValueEvent = hub =>
            {
                tcs.SetResult();
                return ValueTask.FromResult(54321);
            };
        });
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerReceivesInvocation_ServerTaskValue_ReturnedValue(Type type)
    {
        return ServerReceivesInvocation(type, (sHub, cHub, tcs) =>
        {
            cHub.OnConnectedEvent = async (hub, _) =>
            {
                var value = await hub.Context.Proxy.ServerTaskValue();
                Assert.AreEqual(54321, value);
                tcs.SetResult();
            };
            sHub.ServerTaskValueEvent = hub => ValueTask.FromResult(54321);
        });
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerReceivesInvocation_ServerTaskValueWithParam(Type type)
    {
        return ServerReceivesInvocation(type, (sHub, cHub, tcs) =>
        {
            cHub.OnConnectedEvent = async (hub, _) => await hub.Context.Proxy.ServerTaskValueWithParam(12345);
            sHub.ServerTaskValueWithParamEvent = (hub, param) =>
            {
                if (param == 12345)
                    tcs.SetResult();

                return ValueTask.FromResult(54321);
            };
        });
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerReceivesInvocation_ServerTaskValueWithParam_ReturnedValue(Type type)
    {
        return ServerReceivesInvocation(type, (sHub, cHub, tcs) =>
        {
            cHub.OnConnectedEvent = async (hub, _) =>
            {
                var value = await hub.Context.Proxy.ServerTaskValueWithParam(12345);
                Assert.AreEqual(54321, value);
                tcs.SetResult();
            };
            sHub.ServerTaskValueWithParamEvent = (hub, param) => ValueTask.FromResult(54321);
        });
    }



    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerReceivesInvocation_ServerTaskWithCancellation(Type type)
    {
        return ServerReceivesInvocation(type, (sHub, cHub, tcs) =>
        {
            cHub.OnConnectedEvent = async (hub, _) => await hub.Context.Proxy.ServerTaskWithCancellation(CancellationToken.None);
            sHub.ServerTaskWithCancellationEvent = (hub, ct) =>
            {
                tcs.SetResult();
                return ValueTask.CompletedTask;
            };
        });
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerReceivesInvocation_ServerTaskWithValueAndCancellation(Type type)
    {
        return ServerReceivesInvocation(type, (sHub, cHub, tcs) =>
        {
            cHub.OnConnectedEvent = async (hub, _) => await hub.Context.Proxy.ServerTaskWithValueAndCancellation(12345, CancellationToken.None);
            sHub.ServerTaskWithValueAndCancellationEvent = (hub, param, ct) =>
            {
                if (param == 12345)
                    tcs.SetResult();
                return ValueTask.CompletedTask;
            };
        });
    }


















    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerReceivesInvocation_ClientTaskValueWithCancellation(Type type)
    {
        return ServerReceivesInvocation(type, (sHub, cHub, tcs) =>
        {
            cHub.OnConnectedEvent = async (hub, _) => await hub.Context.Proxy.ServerTaskValueWithCancellation(CancellationToken.None);
            sHub.ServerTaskValueWithCancellationEvent = (hub, ct) =>
            {
                tcs.SetResult();
                return ValueTask.FromResult(54321);
            };
        });
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerReceivesInvocation_ClientTaskValueWithCancellation_ReturnedValue(Type type)
    {
        return ServerReceivesInvocation(type, (sHub, cHub, tcs) =>
        {
            cHub.OnConnectedEvent = async (hub, _) =>
            {
                var value = await hub.Context.Proxy.ServerTaskValueWithCancellation(CancellationToken.None);
                Assert.AreEqual(54321, value);
                tcs.SetResult();
            };
            sHub.ServerTaskValueWithCancellationEvent = (hub, ct) => ValueTask.FromResult(54321);
        });
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerReceivesInvocation_ClientTaskValueWithValueAndCancellation(Type type)
    {
        return ServerReceivesInvocation(type, (sHub, cHub, tcs) =>
        {
            cHub.OnConnectedEvent = async (hub, _) => await hub.Context.Proxy.ServerTaskValueWithValueAndCancellation(12345, CancellationToken.None);
            sHub.ServerTaskValueWithValueAndCancellationEvent = (hub, param, ct) =>
            {
                if (param == 12345)
                    tcs.SetResult();

                return ValueTask.FromResult(54321);
            };
        });
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerReceivesInvocation_ClientTaskValueWithValueAndCancellation_ReturnedValue(Type type)
    {
        return ServerReceivesInvocation(type, (sHub, cHub, tcs) =>
        {
            cHub.OnConnectedEvent = async (hub, _) =>
            {
                var value = await hub.Context.Proxy.ServerTaskValueWithValueAndCancellation(12345, CancellationToken.None);
                Assert.AreEqual(54321, value);
                tcs.SetResult();
            };
            sHub.ServerTaskValueWithValueAndCancellationEvent = (hub, param, ct) => ValueTask.FromResult(54321);
        });
    }






















    private async Task ServerReceivesInvocation(Type type, Action<ServerHub, ClientHub, TaskCompletionSource> action)
    {
        var tcs = new TaskCompletionSource();
        var (server, serverHub, client, clientHub) = CreateServerClient(
            CreateServerConfig(type, false),
            CreateClientConfig(type, false));

        server.Start();

        action(serverHub, clientHub, tcs);

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }
    
}
