using NexNet.IntegrationTests.TestInterfaces;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexNetServerTests_HubInvocations : BaseTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task InvokesViaHubContext(Type type)
    {
        var tcs = new TaskCompletionSource();
        var (server, _, client, clientHub) = CreateServerClient(
            CreateServerConfig(type, false),
            CreateClientConfig(type, false));

        clientHub.ClientTaskEvent = async _ => tcs.SetResult();

        server.Start();
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await clientHub.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));

        using var context = server.GetContext();
        await context.Clients.All.ClientTask();
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task InvokesViaHubContextAndDoesNotBlock(Type type)
    {
        bool completed = false;
        var (server, serverHub, client, clientHub) = CreateServerClient(
            CreateServerConfig(type, false),
            CreateClientConfig(type, false));

        clientHub.ClientTaskEvent = async _ =>
        {
            await Task.Delay(10000);
            completed = true;
        };

        serverHub.OnConnectedEvent = hub =>
        {
            hub.Context.Groups.Add("myGroup");
            return ValueTask.CompletedTask;
        };

        server.Start();
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await clientHub.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));

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
    public async Task InvokesViaHubAndDoesNotBlock(Type type)
    {
        bool completed = false;
        var tcs1 = new TaskCompletionSource();
        NexNetServer<ServerHub, ServerHub.ClientProxy> server = null;
        server = CreateServer(CreateServerConfig(type, false), connectedHub =>
        {
            connectedHub.OnConnectedEvent = async hub =>
            {
                hub.Context.Groups.Add("myGroup");

                await hub.Context.Clients.All.ClientTask();
                await hub.Context.Clients.Clients(server.GetContext().Clients.GetIds().ToArray()).ClientTask();
                await hub.Context.Clients.Group("myGroup").ClientTask();
                await hub.Context.Clients.Groups(new[] { "myGroup" }).ClientTask();
                Assert.IsFalse(completed);
                tcs1.SetResult();
            };
        });

        var (client1, clientHub1) = CreateClient(CreateClientConfig(type, false));

        clientHub1.ClientTaskEvent = async _ =>
        {
            await Task.Delay(10000);
            completed = true;
        };

        server.Start();

        await client1.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await clientHub1.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await tcs1.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task InvokesViaHubContextAndGetsReturnFromSingleClient(Type type)
    {
        var (server, _, client, clientHub) = CreateServerClient(
            CreateServerConfig(type, false),
            CreateClientConfig(type, false));

        clientHub.ClientTaskValueEvent = _ => ValueTask.FromResult(54321);

        server.Start();
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await clientHub.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));

        using var context = server.GetContext();

        var result = await context.Clients.Client(context.Clients.GetIds().First()).ClientTaskValue();

        Assert.AreEqual(54321, result);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task HubInvokesOnAll(Type type)
    {
        var tcs1 = new TaskCompletionSource();
        var tcs2 = new TaskCompletionSource();
        int connectedCount = 0;
        var server = CreateServer(CreateServerConfig(type, false), connectedHub =>
        {
            connectedHub.OnConnectedEvent = async hub =>
            {
                // Second connection
                if (++connectedCount == 2)
                {
                    await hub.Context.Clients.All.ClientTask();
                }
            };
        });

        var (client1, clientHub1) = CreateClient(CreateClientConfig(type, false));
        var (client2, clientHub2) = CreateClient(CreateClientConfig(type, false));

        clientHub1.ClientTaskEvent = async _ => tcs1.TrySetResult();
        clientHub2.ClientTaskEvent = async _ => tcs2.TrySetResult();

        server.Start();

        await client1.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await client2.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await clientHub1.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await clientHub2.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));
        
        await tcs1.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await tcs2.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task HubInvokesOnGroup(Type type)
    {
        var tcs1 = new TaskCompletionSource();
        var tcs2 = new TaskCompletionSource();
        int connectedCount = 0;
        var server = CreateServer(CreateServerConfig(type, false), connectedHub =>
        {
            connectedHub.OnConnectedEvent = async hub =>
            {
                hub.Context.Groups.Add("group");
                // Second connection
                if (++connectedCount == 2)
                {
                    await hub.Context.Clients.Group("group").ClientTask();
                }
            };
        });

        var (client1, clientHub1) = CreateClient(CreateClientConfig(type, false));
        var (client2, clientHub2) = CreateClient(CreateClientConfig(type, false));

        clientHub1.ClientTaskEvent = async _ => tcs1.TrySetResult();
        clientHub2.ClientTaskEvent = async _ => tcs2.TrySetResult();

        server.Start();

        await client1.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await client2.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await clientHub1.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await clientHub2.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await tcs1.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await tcs2.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task HubInvokesOnGroups(Type type)
    {
        var tcs1 = new TaskCompletionSource();
        var tcs2 = new TaskCompletionSource();
        int connectedCount = 0;
        var server = CreateServer(CreateServerConfig(type, false), connectedHub =>
        {
            connectedHub.OnConnectedEvent = async hub =>
            {
                if (++connectedCount == 1) {
                    hub.Context.Groups.Add("group");
                }
                // Second connection
                if (connectedCount == 2)
                {
                    hub.Context.Groups.Add("group2");
                    await hub.Context.Clients.Groups(new []{ "group" , "group2" }).ClientTask();
                }
            };
        });

        var (client1, clientHub1) = CreateClient(CreateClientConfig(type, false));
        var (client2, clientHub2) = CreateClient(CreateClientConfig(type, false));

        clientHub1.ClientTaskEvent = async _ => tcs1.TrySetResult();
        clientHub2.ClientTaskEvent = async _ => tcs2.TrySetResult();

        server.Start();

        await client1.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await client2.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await clientHub1.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await clientHub2.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await tcs1.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await tcs2.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task HubInvokesOnOthers(Type type)
    {
        var tcs = new TaskCompletionSource();

        int connectedCount = 0;
        int invocationCount = 0;
        var server = CreateServer(CreateServerConfig(type, false), connectedHub =>
        {
            connectedHub.OnConnectedEvent = async hub =>
            {
                // Second connection
                if (++connectedCount == 2)
                {
                    await hub.Context.Clients.Others.ClientTask();
                    await Task.Delay(10);
                    tcs.SetResult();
                }
            };
        });

        var (client1, clientHub1) = CreateClient(CreateClientConfig(type, false));
        var (client2, clientHub2) = CreateClient(CreateClientConfig(type, false));

        clientHub1.ClientTaskEvent = async _ => invocationCount++;
        clientHub2.ClientTaskEvent = async _ => invocationCount++;

        server.Start();

        await client1.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await client2.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await clientHub1.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await clientHub2.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(1, invocationCount);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task HubInvokesOnClient(Type type)
    {
        var tcs = new TaskCompletionSource();

        int connectedCount = 0;
        int invocationCount = 0;
        var server = CreateServer(CreateServerConfig(type, false), connectedHub =>
        {
            connectedHub.OnConnectedEvent = async hub =>
            {
                // Second connection
                if (++connectedCount == 2)
                {
                    await hub.Context.Clients.Client(hub.Context.Id).ClientTask();
                    await Task.Delay(10);
                    tcs.SetResult();
                }
            };
        });

        var (client1, clientHub1) = CreateClient(CreateClientConfig(type, false));
        var (client2, clientHub2) = CreateClient(CreateClientConfig(type, false));

        clientHub1.ClientTaskEvent = async _ => invocationCount++;
        clientHub2.ClientTaskEvent = async _ => invocationCount++;

        server.Start();

        await client1.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await client2.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await clientHub1.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await clientHub2.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(1, invocationCount);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task HubInvokesOnClients(Type type)
    {
        var tcs1 = new TaskCompletionSource();
        var tcs2 = new TaskCompletionSource();
        int connectedCount = 0;
        NexNetServer<ServerHub, ServerHub.ClientProxy> server = null;
        server = CreateServer(CreateServerConfig(type, false), connectedHub =>
        {
            connectedHub.OnConnectedEvent = async hub =>
            {
                // Second connection
                if (++connectedCount == 2)
                {
                    var clientIds = server!.GetContext().Clients.GetIds().ToArray();
                    await hub.Context.Clients.Clients(clientIds).ClientTask();
                }
            };
        });

        var (client1, clientHub1) = CreateClient(CreateClientConfig(type, false));
        var (client2, clientHub2) = CreateClient(CreateClientConfig(type, false));

        clientHub1.ClientTaskEvent = async _ => tcs1.TrySetResult();
        clientHub2.ClientTaskEvent = async _ => tcs2.TrySetResult();

        server.Start();

        await client1.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await client2.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await clientHub1.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await clientHub2.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await tcs1.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await tcs2.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
