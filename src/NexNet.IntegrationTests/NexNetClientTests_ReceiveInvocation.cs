using MemoryPack;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexNetClientTests_ReceiveInvocation : BaseTests
{
    
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public Task ClientReceivesInvocation_ClientVoid(Type type)
    {
        return ClientReceivesInvocation(type, (sHub, cHub, tcs) =>
        {
            sHub.OnConnectedEvent = hub =>
            {
                hub.Context.Clients.Caller.ClientVoid();
                return ValueTask.CompletedTask;
            };
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
            sHub.OnConnectedEvent = hub =>
            {
                hub.Context.Clients.Caller.ClientVoidWithParam(12345);
                return ValueTask.CompletedTask;
            };
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
            cHub.ClientTaskEvent = hub =>
            {
                tcs.SetResult();
                return ValueTask.CompletedTask;
            };
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
            cHub.ClientTaskWithParamEvent = (hub, param) =>
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
    public Task ClientReceivesInvocation_ClientTaskValue(Type type)
    {
        return ClientReceivesInvocation(type, (sHub, cHub, tcs) =>
        {
            sHub.OnConnectedEvent = async hub => await hub.Context.Clients.Caller.ClientTaskValue();
            cHub.ClientTaskValueEvent = hub =>
            {
                tcs.SetResult();
                return ValueTask.FromResult(54321);
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
            cHub.ClientTaskValueWithParamEvent = (hub, param) =>
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
    public Task ClientReceivesInvocation_ClientTaskWithCancellation(Type type)
    {
        return ClientReceivesInvocation(type, (sHub, cHub, tcs) =>
        {
            sHub.OnConnectedEvent = async hub => await hub.Context.Clients.Caller.ClientTaskWithCancellation(CancellationToken.None);
            cHub.ClientTaskWithCancellationEvent = (hub, ct) =>
            {
                tcs.SetResult();
                return ValueTask.CompletedTask;
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
            cHub.ClientTaskWithValueAndCancellationEvent = (hub, param, ct) =>
            {
                if (param == 12345)
                    tcs.SetResult();
                return ValueTask.CompletedTask;
            };
        });
    }

    private async Task ClientReceivesInvocation(Type type, Action<ServerHub, ClientHub, TaskCompletionSource> action)
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
