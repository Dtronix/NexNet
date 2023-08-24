using NexNet.IntegrationTests.TestInterfaces;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal class NexusServerTests_NexusInvocations : BaseTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task InvokesViaNexusContext(Type type)
    {
        var tcs = new TaskCompletionSource();
        var (server, _, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));
#pragma warning disable CS1998
        clientNexus.ClientTaskEvent = async _ => tcs.SetResult();
#pragma warning restore CS1998
        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        using var context = server.GetContext();
        await context.Clients.All.ClientTask();
        await tcs.Task.Timeout(1);
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task InvokesViaNexusContextAndDoesNotBlock(Type type)
    {
        bool completed = false;
        var (server, serverNexus, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        clientNexus.ClientTaskEvent = async _ =>
        {
            await Task.Delay(10000);
            completed = true;
        };

        serverNexus.OnConnectedEvent = nexus =>
        {
            nexus.Context.Groups.Add("myGroup");
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        using var context = server.GetContext();
        await context.Clients.All.ClientTask();
        await context.Clients.Clients(context.Clients.GetIds().ToArray()).ClientTask();
        await context.Clients.Group("myGroup").ClientTask();
        await context.Clients.Groups(new[] { "myGroup" }).ClientTask();
        Assert.IsFalse(completed);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task InvokesViaNexusAndDoesNotBlock(Type type)
    {
        bool completed = false;
        var tcs1 = new TaskCompletionSource();
        NexusServer<ServerNexus, ServerNexus.ClientProxy> server = null!;
        server = CreateServer(CreateServerConfig(type), connectedNexus =>
        {
            connectedNexus.OnConnectedEvent = async nexus =>
            {
                nexus.Context.Groups.Add("myGroup");

                await nexus.Context.Clients.All.ClientTask();
                // ReSharper disable once AccessToModifiedClosure
                await nexus.Context.Clients.Clients(server!.GetContext().Clients.GetIds().ToArray()).ClientTask();
                await nexus.Context.Clients.Group("myGroup").ClientTask();
                await nexus.Context.Clients.Groups(new[] { "myGroup" }).ClientTask();
                Assert.IsFalse(completed);
                tcs1.SetResult();
            };
        });

        var (client, clientNexus) = CreateClient(CreateClientConfig(type));

        clientNexus.ClientTaskEvent = async _ =>
        {
            await Task.Delay(10000);
            completed = true;
        };

        await server.StartAsync().Timeout(1);

        await client.ConnectAsync().Timeout(1);

        await tcs1.Task.Timeout(1);
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    [Repeat(100)]
    public async Task InvokesViaNexusContextAndGetsReturnFromSingleClient(Type type)
    {
        var (server, _, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type, true),
            CreateClientConfig(type, true));

        clientNexus.ClientTaskValueEvent = _ => ValueTask.FromResult(54321);

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        using var context = server.GetContext();

        var result = await context.Clients.Client(context.Clients.GetIds().First()).ClientTaskValue();

        Assert.AreEqual(54321, result);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task NexusInvokesOnAll(Type type)
    {
        var tcs1 = new TaskCompletionSource();
        var tcs2 = new TaskCompletionSource();
        int connectedCount = 0;
        var server = CreateServer(CreateServerConfig(type), connectedNexus =>
        {
            connectedNexus.OnConnectedEvent = async nexus =>
            {
                // Second connection
                if (++connectedCount == 2)
                {
                    await nexus.Context.Clients.All.ClientTask();
                }
            };
        });

        var (client1, clientNexus1) = CreateClient(CreateClientConfig(type));
        var (client2, clientNexus2) = CreateClient(CreateClientConfig(type));

#pragma warning disable CS1998
        clientNexus1.ClientTaskEvent = async _ => tcs1.TrySetResult();
        clientNexus2.ClientTaskEvent = async _ => tcs2.TrySetResult();
#pragma warning restore CS1998
        await server.StartAsync().Timeout(1);

        await client1.ConnectAsync().Timeout(1);
        await client2.ConnectAsync().Timeout(1);

        await tcs1.Task.Timeout(1);
        await tcs2.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task NexusInvokesOnGroup(Type type)
    {
        var tcs1 = new TaskCompletionSource();
        var tcs2 = new TaskCompletionSource();
        int connectedCount = 0;
        var server = CreateServer(CreateServerConfig(type), connectedNexus =>
        {
            connectedNexus.OnConnectedEvent = async nexus =>
            {
                nexus.Context.Groups.Add("group");
                // Second connection
                if (++connectedCount == 2)
                {
                    await nexus.Context.Clients.Group("group").ClientTask();
                }
            };
        });

        var (client1, clientNexus1) = CreateClient(CreateClientConfig(type));
        var (client2, clientNexus2) = CreateClient(CreateClientConfig(type));
#pragma warning disable CS1998
        clientNexus1.ClientTaskEvent = async _ => tcs1.TrySetResult();
        clientNexus2.ClientTaskEvent = async _ => tcs2.TrySetResult();
#pragma warning restore CS1998
        await server.StartAsync().Timeout(1);

        await client1.ConnectAsync().Timeout(1);
        await client2.ConnectAsync().Timeout(1);

        await tcs1.Task.Timeout(1);
        await tcs2.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task NexusInvokesOnGroups(Type type)
    {
        var tcs1 = new TaskCompletionSource();
        var tcs2 = new TaskCompletionSource();
        int connectedCount = 0;
        var server = CreateServer(CreateServerConfig(type), connectedNexus =>
        {
            connectedNexus.OnConnectedEvent = async nexus =>
            {
                if (++connectedCount == 1) {
                    nexus.Context.Groups.Add("group");
                }
                // Second connection
                if (connectedCount == 2)
                {
                    nexus.Context.Groups.Add("group2");
                    await nexus.Context.Clients.Groups(new []{ "group" , "group2" }).ClientTask();
                }
            };
        });

        var (client1, clientNexus1) = CreateClient(CreateClientConfig(type));
        var (client2, clientNexus2) = CreateClient(CreateClientConfig(type));
#pragma warning disable CS1998
        clientNexus1.ClientTaskEvent = async _ => tcs1.TrySetResult();
        clientNexus2.ClientTaskEvent = async _ => tcs2.TrySetResult();
#pragma warning restore CS1998
        await server.StartAsync().Timeout(1);

        await client1.ConnectAsync().Timeout(1);
        await client2.ConnectAsync().Timeout(1);

        await tcs1.Task.Timeout(1);
        await tcs2.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task NexusInvokesOnOthers(Type type)
    {
        var tcs = new TaskCompletionSource();

        int connectedCount = 0;
        int invocationCount = 0;
        var server = CreateServer(CreateServerConfig(type), connectedNexus =>
        {
            connectedNexus.OnConnectedEvent = async nexus =>
            {
                // Second connection
                if (++connectedCount == 2)
                {
                    await nexus.Context.Clients.Others.ClientTask();
                    await Task.Delay(10);
                    tcs.SetResult();
                }
            };
        });

        var (client1, clientNexus1) = CreateClient(CreateClientConfig(type));
        var (client2, clientNexus2) = CreateClient(CreateClientConfig(type));
#pragma warning disable CS1998
        clientNexus1.ClientTaskEvent = async _ => invocationCount++;
        clientNexus2.ClientTaskEvent = async _ => invocationCount++;
#pragma warning restore CS1998
        await server.StartAsync().Timeout(1);

        await client1.ConnectAsync().Timeout(1);
        await client2.ConnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);

        Assert.AreEqual(1, invocationCount);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task NexusInvokesOnClient(Type type)
    {
        var tcs = new TaskCompletionSource();

        int connectedCount = 0;
        int invocationCount = 0;
        var server = CreateServer(CreateServerConfig(type), connectedNexus =>
        {
            connectedNexus.OnConnectedEvent = async nexus =>
            {
                // Second connection
                if (++connectedCount == 2)
                {
                    await nexus.Context.Clients.Client(nexus.Context.Id).ClientTask();
                    await Task.Delay(10);
                    tcs.SetResult();
                }
            };
        });

        var (client1, clientNexus1) = CreateClient(CreateClientConfig(type));
        var (client2, clientNexus2) = CreateClient(CreateClientConfig(type));
#pragma warning disable CS1998
        clientNexus1.ClientTaskEvent = async _ => invocationCount++;
        clientNexus2.ClientTaskEvent = async _ => invocationCount++;
#pragma warning restore CS1998
        await server.StartAsync().Timeout(1);

        await client1.ConnectAsync().Timeout(1);
        await client2.ConnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);

        Assert.AreEqual(1, invocationCount);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task NexusInvokesOnClients(Type type)
    {
        var tcs1 = new TaskCompletionSource();
        var tcs2 = new TaskCompletionSource();
        int connectedCount = 0;
        NexusServer<ServerNexus, ServerNexus.ClientProxy> server = null!;
        server = CreateServer(CreateServerConfig(type), connectedNexus =>
        {
            connectedNexus.OnConnectedEvent = async nexus =>
            {
                // Second connection
                if (++connectedCount == 2)
                {
                    // ReSharper disable once AccessToModifiedClosure
                    var clientIds = server!.GetContext().Clients.GetIds().ToArray();
                    await nexus.Context.Clients.Clients(clientIds).ClientTask();
                }
            };
        });

        var (client1, clientNexus1) = CreateClient(CreateClientConfig(type));
        var (client2, clientNexus2) = CreateClient(CreateClientConfig(type));
#pragma warning disable CS1998
        clientNexus1.ClientTaskEvent = async _ => tcs1.TrySetResult();
        clientNexus2.ClientTaskEvent = async _ => tcs2.TrySetResult();
#pragma warning restore CS1998
        await server.StartAsync().Timeout(1);

        await client1.ConnectAsync().Timeout(1);
        await client2.ConnectAsync().Timeout(1);

        await tcs1.Task.Timeout(1);
        await tcs2.Task.Timeout(1);
    }
}
