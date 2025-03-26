using NexNet.IntegrationTests.Pipes;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Invocation;
using NUnit.Framework;

namespace NexNet.IntegrationTests;

internal class NexusServerTests_NexusGroupInvocations : BaseTests
{
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task NexusInvokesOnGroup(Type type)
    {
        await RunGroupTest(type, ["group"], 3, 3, nexus =>
            nexus.Context.Clients.Group("group").ClientTask()
        );
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task NexusInvokesOnGroupExceptCaller(Type type)
    {
        await RunGroupTest(type, ["group"], 3, 2, nexus =>
            nexus.Context.Clients.GroupExceptCaller("group").ClientTask()
        );
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task NexusInvokesOnGroups(Type type)
    {
        await RunGroupTest(type, ["group1", "group2"], 3, 6, nexus =>
            nexus.Context.Clients.Groups(["group1", "group2"]).ClientTask()
        );
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task NexusInvokesOnGroupsExceptCaller(Type type)
    {
        await RunGroupTest(type, ["group1", "group2"], 3, 4, nexus =>
            nexus.Context.Clients.GroupsExceptCaller(["group1", "group2"]).ClientTask()
        );
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task DirectNexusProxyInvokesOnGroupIncludingCurrent(Type type)
    {
        await RunServerContextGroupTest(type, ["group"], 3, 3, proxy => 
            proxy.Group("group").ClientTask());
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task DirectNexusProxyInvokesOnGroupsIncludingCurrent(Type type)
    {
        await RunServerContextGroupTest(type, ["group1","group2"], 3, 6, proxy => 
            proxy.Groups(["group1","group2"]).ClientTask());
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task DirectNexusProxyInvokesOnGroupIgnoresExcludingCurrent(Type type)
    {
        await RunServerContextGroupTest(type, ["group"], 3, 3, proxy => 
            proxy.GroupExceptCaller("group").ClientTask());
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task DirectNexusProxyInvokesOnGroupsIgnoresIncludingCurrent(Type type)
    {
        await RunServerContextGroupTest(type, ["group1","group2"], 3, 6, proxy => 
            proxy.GroupsExceptCaller(["group1","group2"]).ClientTask());
    }
    
    
    private async ValueTask RunGroupTest(
        Type type, 
        string[] addGroups,
        int clientCount,
        int targetInvocations,
        Func<ServerNexus, ValueTask> nexusInvocation)
    {
        var groupInvokedCount = 0;
        var tcs1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var clients = new List<(NexusClient<ClientNexus, ClientNexus.ServerProxy> Client, ClientNexus ClientNexus)>();

        for (int i = 0; i < clientCount; i++)
            clients.Add(CreateClient(CreateClientConfig(type, BasePipeTests.LogMode.OnTestFail)));

        var server = CreateServer(CreateServerConfig(type, BasePipeTests.LogMode.OnTestFail), connectedNexus =>
        {
            connectedNexus.OnConnectedEvent = nexus =>
            {
                if (addGroups.Length == 1)
                    nexus.Context.Groups.Add(addGroups[0]);
                else
                    nexus.Context.Groups.Add(addGroups);
                
                return ValueTask.CompletedTask;
            };

            connectedNexus.ServerTaskEvent = nexusInvocation;
        });

        foreach (var client in clients)
        {
            client.ClientNexus.ClientTaskEvent = async _ =>
            {
                Interlocked.Increment(ref groupInvokedCount);
                // Delay to let all other calls go through.
                await Task.Delay(100);
            
                if (groupInvokedCount >= targetInvocations)
                    tcs1.SetResult();
            };
        }

        await server.StartAsync().Timeout(1);
        await Task.WhenAll(clients.Select(c => c.Client.ConnectAsync())).Timeout(1);
        
        await clients.First().Client.Proxy.ServerTask();

        await tcs1.Task.Timeout(1);
        Assert.That(groupInvokedCount, Is.EqualTo(targetInvocations));
    }
    
    private async ValueTask RunServerContextGroupTest(
        Type type, 
        string[] addGroups,
        int clientCount,
        int targetInvocations,
        Func<IProxyBase<ServerNexus.ClientProxy>, ValueTask> onReady)
    {
        var groupInvokedCount = 0;
        var tcs1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var clients = new List<(NexusClient<ClientNexus, ClientNexus.ServerProxy> Client, ClientNexus ClientNexus)>();

        for (int i = 0; i < clientCount; i++)
            clients.Add(CreateClient(CreateClientConfig(type, BasePipeTests.LogMode.OnTestFail)));

        var server = CreateServer(CreateServerConfig(type, BasePipeTests.LogMode.OnTestFail), connectedNexus =>
        {
            connectedNexus.OnConnectedEvent = nexus =>
            {
                if (addGroups.Length == 1)
                    nexus.Context.Groups.Add(addGroups[0]);
                else
                    nexus.Context.Groups.Add(addGroups);
                
                return ValueTask.CompletedTask;
            };
        });

        foreach (var client in clients)
        {
            client.ClientNexus.ClientTaskEvent = async _ =>
            {
                Interlocked.Increment(ref groupInvokedCount);
                // Delay to let all other calls go through.
                await Task.Delay(100);
            
                if (groupInvokedCount >= targetInvocations)
                    tcs1.SetResult();
            };
        }

        await server.StartAsync().Timeout(1);
        await Task.WhenAll(clients.Select(c => c.Client.ConnectAsync())).Timeout(1);

        await onReady.Invoke(server.GetContext().Clients);

        await tcs1.Task.Timeout(1);
        Assert.That(groupInvokedCount, Is.EqualTo(targetInvocations));
    }
}
