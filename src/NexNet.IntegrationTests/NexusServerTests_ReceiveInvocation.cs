using NexNet.IntegrationTests.TestInterfaces;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexusServerTests_ReceiveInvocation : BaseTests
{
    
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerReceivesInvocation_ServerVoid(Type type)
    {
        return ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = (nexus, _) =>
            {
                nexus.Context.Proxy.ServerVoid();
                return ValueTask.CompletedTask;
            };
            sNexus.ServerVoidEvent = nexus => tcs.SetResult();
        });
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerReceivesInvocation_ServerVoidWithParam(Type type)
    {
        return ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = (nexus, _) =>
            {
                nexus.Context.Proxy.ServerVoidWithParam(12345);
                return ValueTask.CompletedTask;
            };
            sNexus.ServerVoidWithParamEvent = (nexus, param) =>
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
        return ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = (nexus, _) => nexus.Context.Proxy.ServerTask();
            sNexus.ServerTaskEvent = nexus =>
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
        return ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = (nexus, _) => nexus.Context.Proxy.ServerTaskWithParam(12345);
            sNexus.ServerTaskWithParamEvent = (nexus, param) =>
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
        return ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = async (nexus, _) => await nexus.Context.Proxy.ServerTaskValue();
            sNexus.ServerTaskValueEvent = nexus =>
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
        return ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = async (nexus, _) =>
            {
                var value = await nexus.Context.Proxy.ServerTaskValue();
                Assert.AreEqual(54321, value);
                tcs.SetResult();
            };
            sNexus.ServerTaskValueEvent = nexus => ValueTask.FromResult(54321);
        });
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerReceivesInvocation_ServerTaskValueWithParam(Type type)
    {
        return ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = async (nexus, _) => await nexus.Context.Proxy.ServerTaskValueWithParam(12345);
            sNexus.ServerTaskValueWithParamEvent = (nexus, param) =>
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
        return ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = async (nexus, _) =>
            {
                var value = await nexus.Context.Proxy.ServerTaskValueWithParam(12345);
                Assert.AreEqual(54321, value);
                tcs.SetResult();
            };
            sNexus.ServerTaskValueWithParamEvent = (nexus, param) => ValueTask.FromResult(54321);
        });
    }



    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerReceivesInvocation_ServerTaskWithCancellation(Type type)
    {
        return ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = async (nexus, _) => await nexus.Context.Proxy.ServerTaskWithCancellation(CancellationToken.None);
            sNexus.ServerTaskWithCancellationEvent = (nexus, ct) =>
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
        return ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = async (nexus, _) => await nexus.Context.Proxy.ServerTaskWithValueAndCancellation(12345, CancellationToken.None);
            sNexus.ServerTaskWithValueAndCancellationEvent = (nexus, param, ct) =>
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
        return ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = async (nexus, _) => await nexus.Context.Proxy.ServerTaskValueWithCancellation(CancellationToken.None);
            sNexus.ServerTaskValueWithCancellationEvent = (nexus, ct) =>
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
        return ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = async (nexus, _) =>
            {
                var value = await nexus.Context.Proxy.ServerTaskValueWithCancellation(CancellationToken.None);
                Assert.AreEqual(54321, value);
                tcs.SetResult();
            };
            sNexus.ServerTaskValueWithCancellationEvent = (nexus, ct) => ValueTask.FromResult(54321);
        });
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ServerReceivesInvocation_ClientTaskValueWithValueAndCancellation(Type type)
    {
        return ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = async (nexus, _) => await nexus.Context.Proxy.ServerTaskValueWithValueAndCancellation(12345, CancellationToken.None);
            sNexus.ServerTaskValueWithValueAndCancellationEvent = (nexus, param, ct) =>
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
        return ServerReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            cNexus.OnConnectedEvent = async (nexus, _) =>
            {
                var value = await nexus.Context.Proxy.ServerTaskValueWithValueAndCancellation(12345, CancellationToken.None);
                Assert.AreEqual(54321, value);
                tcs.SetResult();
            };
            sNexus.ServerTaskValueWithValueAndCancellationEvent = (nexus, param, ct) => ValueTask.FromResult(54321);
        });
    }






















    private async Task ServerReceivesInvocation(Type type, Action<ServerNexus, ClientNexus, TaskCompletionSource> action)
    {
        var tcs = new TaskCompletionSource();
        var (server, serverNexus, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type, false),
            CreateClientConfig(type, false));

        server.Start();

        action(serverNexus, clientNexus, tcs);

        await client.ConnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }
    
}
