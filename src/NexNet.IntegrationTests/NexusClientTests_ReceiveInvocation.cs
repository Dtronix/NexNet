using NexNet.IntegrationTests.TestInterfaces;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexusClientTests_ReceiveInvocation : BaseTests
{
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public Task ClientReceivesInvocation_ClientVoid(Type type)
    {
        return ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = nexus =>
            {
                nexus.Context.Clients.Caller.ClientVoid();
                return ValueTask.CompletedTask;
            };
            cNexus.ClientVoidEvent = nexus => tcs.SetResult();
        });
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public Task ClientReceivesInvocation_ClientVoidWithParam(Type type)
    {
        return ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = nexus =>
            {
                nexus.Context.Clients.Caller.ClientVoidWithParam(12345);
                return ValueTask.CompletedTask;
            };
            cNexus.ClientVoidWithParamEvent = (nexus, param) =>
            {
                if (param == 12345)
                    tcs.SetResult();
            };
        });
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public Task ClientReceivesInvocation_ClientTask(Type type)
    {
        return ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = nexus => nexus.Context.Clients.Caller.ClientTask();
            cNexus.ClientTaskEvent = nexus =>
            {
                tcs.SetResult();
                return ValueTask.CompletedTask;
            };
        });
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public Task ClientReceivesInvocation_ClientTaskWithParam(Type type)
    {
        return ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = nexus => nexus.Context.Clients.Caller.ClientTaskWithParam(12345);
            cNexus.ClientTaskWithParamEvent = (nexus, param) =>
            {
                if (param == 12345)
                    tcs.SetResult();
                return ValueTask.CompletedTask;
            };
        });
    }


    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public Task ClientReceivesInvocation_ClientTaskValue(Type type)
    {
        return ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = async nexus => await nexus.Context.Clients.Caller.ClientTaskValue();
            cNexus.ClientTaskValueEvent = nexus =>
            {
                tcs.SetResult();
                return ValueTask.FromResult(54321);
            };
        });
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public Task ClientReceivesInvocation_ClientTaskValue_ReturnedValue(Type type)
    {
        return ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = async nexus =>
            {
                var value = await nexus.Context.Clients.Caller.ClientTaskValue();
                Assert.That(value, Is.EqualTo(54321));
                tcs.SetResult();
            };
            cNexus.ClientTaskValueEvent = nexus => ValueTask.FromResult(54321);
        });
    }


    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public Task ClientReceivesInvocation_ClientTaskValueWithParam(Type type)
    {
        return ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = async nexus => await nexus.Context.Clients.Caller.ClientTaskValueWithParam(12345);
            cNexus.ClientTaskValueWithParamEvent = (nexus, param) =>
            {
                if (param == 12345)
                    tcs.SetResult();

                return ValueTask.FromResult(54321);
            };
        });
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public Task ClientReceivesInvocation_ClientTaskValueWithParam_ReturnedValue(Type type)
    {
        return ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = async nexus =>
            {
                var value = await nexus.Context.Clients.Caller.ClientTaskValueWithParam(12345);
                Assert.That(value, Is.EqualTo(54321));
                tcs.SetResult();
            };
            cNexus.ClientTaskValueWithParamEvent = (nexus, param) => ValueTask.FromResult(54321);
        });
    }



    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public Task ClientReceivesInvocation_ClientTaskWithCancellation(Type type)
    {
        return ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = async nexus => await nexus.Context.Clients.Caller.ClientTaskWithCancellation(CancellationToken.None);
            cNexus.ClientTaskWithCancellationEvent = (nexus, ct) =>
            {
                tcs.SetResult();
                return ValueTask.CompletedTask;
            };
        });
    }


    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public Task ClientReceivesInvocation_ClientTaskWithValueAndCancellation(Type type)
    {
        return ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = async nexus => await nexus.Context.Clients.Caller.ClientTaskWithValueAndCancellation(12345, CancellationToken.None);
            cNexus.ClientTaskWithValueAndCancellationEvent = (nexus, param, ct) =>
            {
                if (param == 12345)
                    tcs.SetResult();
                return ValueTask.CompletedTask;
            };
        });
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public Task ClientReceivesInvocation_ClientTaskValueWithCancellation(Type type)
    {
        return ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = async nexus => await nexus.Context.Clients.Caller.ClientTaskValueWithCancellation(CancellationToken.None);
            cNexus.ClientTaskValueWithCancellationEvent = (nexus, ct) =>
            {
                tcs.SetResult();
                return ValueTask.FromResult(54321);
            };
        });
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public Task ClientReceivesInvocation_ClientTaskValueWithCancellation_ReturnedValue(Type type)
    {
        return ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = async nexus =>
            {
                var value = await nexus.Context.Clients.Caller.ClientTaskValueWithCancellation(CancellationToken.None);
                Assert.That(value, Is.EqualTo(54321));
                tcs.SetResult();
            };
            cNexus.ClientTaskValueWithCancellationEvent = (nexus, ct) => ValueTask.FromResult(54321);
        });
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public Task ClientReceivesInvocation_ClientTaskValueWithValueAndCancellation(Type type)
    {
        return ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = async nexus => await nexus.Context.Clients.Caller.ClientTaskValueWithValueAndCancellation(12345, CancellationToken.None);
            cNexus.ClientTaskValueWithValueAndCancellationEvent = (nexus, param, ct) =>
            {
                if (param == 12345)
                    tcs.SetResult();

                return ValueTask.FromResult(54321);
            };
        });
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public Task ClientReceivesInvocation_ClientTaskValueWithValueAndCancellation_ReturnedValue(Type type)
    {
        return ClientReceivesInvocation(type, (sNexus, cNexus, tcs) =>
        {
            sNexus.OnConnectedEvent = async nexus =>
            {
                var value = await nexus.Context.Clients.Caller.ClientTaskValueWithValueAndCancellation(12345, CancellationToken.None);
                Assert.That(value, Is.EqualTo(54321));
                tcs.SetResult();
            };
            cNexus.ClientTaskValueWithValueAndCancellationEvent = (nexus, param, ct) => ValueTask.FromResult(54321);
        });
    }

    private async Task ClientReceivesInvocation(Type type, Action<ServerNexus, ClientNexus, TaskCompletionSource> action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, serverNexus, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        await server.StartAsync().Timeout(1);

        action(serverNexus, clientNexus, tcs);

        await client.ConnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }
}
