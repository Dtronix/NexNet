using System.Collections.Generic;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.IntegrationTests.Pipes;
using NexNet.IntegrationTests.TestInterfaces;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Collections;

/// <summary>
/// Tests for the new parent-child collection relay functionality where a child collection
/// can connect to a parent collection and relay all changes from the parent.
/// </summary>
internal class NexusCollectionRelayTests : NexusCollectionBaseTests
{
    
    
    [TestCase(Type.Uds)]
    public async Task AccessesCollectionListFromContextProviderSuccessfully(Type type)
    {
        var server = CreateServer(CreateServerConfig(type), nexus => { });
        await server.StartAsync();
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(1);
        var list = server.ContextProvider.Rent().Collections.IntListBi;
        Assert.That(list, Is.Not.Null);
    }
    
    [Test]
    public async Task ChildCanConnectToParentCollection()
    {

        var config1 = CreateServerConfig(Type.Tcp);
        var clientConfig1 = CreateClientConfig(Type.Tcp);
        var server1Port = CurrentTcpPort;

        var clientPool =
            new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(new NexusClientPoolConfig(clientConfig1));
        
        // Reset the port to get a new port.
        CurrentTcpPort = null;
        var config2 = CreateServerConfig(Type.Tcp);
        var server2Port = CurrentTcpPort;
        var server1 = CreateServer(config1, nexus => { });
        server1.ContextProvider.Rent();
        var server2 = CreateServer(config2, nexus => { }, configureCollections: nexus =>
        {
            nexus.IntListSvToCl.ConfigureRelay(clientPool.GetCollectionConnector(n => n.IntListBi));
        });
        await server1.StartAsync().Timeout(1);
        await server2.StartAsync().Timeout(1);
        await Task.Delay(800);

        var collection = server1.ContextProvider.Rent().Collections.IntListBi;
        await collection.AddAsync(1);
        
        await Task.Delay(500);
        
        Assert.Fail();
        //server1.Config
        
        //await server2.StartAsync();
        
        
        //server2.

        return;
        /*try
        {
            //// Connect parent collection
            //await client1.Proxy.IntListBi.ConnectAsync().Timeout(1);
            //
            //// Connect child collection to parent
            //var success = await client2.Proxy.IntListBi.ConnectAsync(client1.Proxy.IntListBi).Timeout(1);
            //
            //Assert.That(success, Is.True);
            //Assert.That(client2.Proxy.IntListBi.State, Is.EqualTo(NexusCollectionState.Connected));
        }
        finally
        {
            await server1.DisposeAsync();
            await server2.DisposeAsync();
        }*/
    }
    
    /*

    [TestCase(Type.Tcp)]
    public async Task ChildRelaysAddOperationsFromParent(Type type)
    {
        var (server1, serverNexus1, client1, _) = await ConnectServerAndClient(type);
        var (server2, serverNexus2, client2, _) = await ConnectServerAndClient(type);

        try
        {
            // Connect parent collection
            await client1.Proxy.IntListBi.ConnectAsync().Timeout(1);
            
            // Connect child collection to parent
            await client2.Proxy.IntListBi.ConnectAsync(client1.Proxy.IntListBi).Timeout(1);
            
            // Add items to server1 (parent's server)
            await serverNexus1.IntListBi.AddAsync(1).Timeout(1);
            await serverNexus1.IntListBi.AddAsync(2).Timeout(1);
            await serverNexus1.IntListBi.AddAsync(3).Timeout(1);
            
            // Wait a bit for relay to propagate
            await Task.Delay(100);
            
            // Child should have relayed the changes
            Assert.That(client2.Proxy.IntListBi, Is.EquivalentTo([1, 2, 3]));
        }
        finally
        {
            await server1.DisposeAsync();
            await server2.DisposeAsync();
        }
    }

    [TestCase(Type.Tcp)]
    public async Task ChildRelaysRemoveOperationsFromParent(Type type)
    {
        var (server1, serverNexus1, client1, _) = await ConnectServerAndClient(type);
        var (server2, serverNexus2, client2, _) = await ConnectServerAndClient(type);

        try
        {
            // Connect parent collection and add initial data
            await client1.Proxy.IntListBi.ConnectAsync().Timeout(1);
            await serverNexus1.IntListBi.AddAsync(1).Timeout(1);
            await serverNexus1.IntListBi.AddAsync(2).Timeout(1);
            await serverNexus1.IntListBi.AddAsync(3).Timeout(1);
            
            // Connect child collection to parent
            await client2.Proxy.IntListBi.ConnectAsync(client1.Proxy.IntListBi).Timeout(1);
            
            // Remove item from parent's server
            await serverNexus1.IntListBi.RemoveAsync(2).Timeout(1);
            
            // Wait for relay to propagate
            await Task.Delay(100);
            
            // Child should have relayed the removal
            Assert.That(client2.Proxy.IntListBi, Is.EquivalentTo([1, 3]));
        }
        finally
        {
            await server1.DisposeAsync();
            await server2.DisposeAsync();
        }
    }

    [TestCase(Type.Tcp)]
    public async Task ChildRelaysClearOperationFromParent(Type type)
    {
        var (server1, serverNexus1, client1, _) = await ConnectServerAndClient(type);
        var (server2, serverNexus2, client2, _) = await ConnectServerAndClient(type);

        // Connect parent collection and add initial data
        await client1.Proxy.IntListBi.ConnectAsync().Timeout(1);
        await serverNexus1.IntListBi.AddAsync(1).Timeout(1);
        await serverNexus1.IntListBi.AddAsync(2).Timeout(1);
        
        // Connect child collection to parent
        await client2.Proxy.IntListBi.ConnectAsync(client1.Proxy.IntListBi).Timeout(1);
        
        // Clear parent collection
        await serverNexus1.IntListBi.ClearAsync().Timeout(1);
        
        // Wait for relay to propagate
        await Task.Delay(100);
        
        // Child should have relayed the clear
        Assert.That(client2.Proxy.IntListBi, Is.Empty);
        
        await server1.DisposeAsync();
        await server2.DisposeAsync();
    }

    [TestCase(Type.Tcp)]
    public async Task ChildCannotModifyParentCollection(Type type)
    {
        var (server1, serverNexus1, client1, _) = await ConnectServerAndClient(type);
        var (server2, serverNexus2, client2, _) = await ConnectServerAndClient(type);

        // Connect parent collection
        await client1.Proxy.IntListBi.ConnectAsync().Timeout(1);
        
        // Connect child collection to parent
        await client2.Proxy.IntListBi.ConnectAsync(client1.Proxy.IntListBi).Timeout(1);
        
        // Verify child is in read-only mode
        Assert.That(client2.Proxy.IntListBi.IsReadOnly, Is.True);
        
        // Attempt to modify through child should throw
        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await client2.Proxy.IntListBi.AddAsync(42).Timeout(1));
        
        await server1.DisposeAsync();
        await server2.DisposeAsync();
    }

    [TestCase(Type.Tcp)]
    public async Task ChildReceivesChangeEventsFromParent(Type type)
    {
        var (server1, serverNexus1, client1, _) = await ConnectServerAndClient(type);
        var (server2, serverNexus2, client2, _) = await ConnectServerAndClient(type);

        // Connect parent collection
        await client1.Proxy.IntListBi.ConnectAsync().Timeout(1);
        
        // Connect child collection to parent
        await client2.Proxy.IntListBi.ConnectAsync(client1.Proxy.IntListBi).Timeout(1);
        
        var changeEventReceived = false;
        client2.Proxy.IntListBi.Changed.Subscribe(_ => changeEventReceived = true);
        
        // Modify parent collection
        await serverNexus1.IntListBi.AddAsync(42).Timeout(1);
        
        // Wait for events to propagate
        await Task.Delay(100);
        
        // Child should have received change event
        Assert.That(changeEventReceived, Is.True);
        
        await server1.DisposeAsync();
        await server2.DisposeAsync();
    }

    [TestCase(Type.Tcp)]
    public async Task ChildDisconnectsWhenParentDisconnects(Type type)
    {
        var (server1, serverNexus1, client1, _) = await ConnectServerAndClient(type);
        var (server2, serverNexus2, client2, _) = await ConnectServerAndClient(type);

        // Connect parent collection
        await client1.Proxy.IntListBi.ConnectAsync().Timeout(1);
        
        // Connect child collection to parent
        await client2.Proxy.IntListBi.ConnectAsync(client1.Proxy.IntListBi).Timeout(1);
        
        // Disconnect parent
        await client1.Proxy.IntListBi.DisconnectAsync().Timeout(1);
        
        // Wait for child disconnection to propagate
        await Task.Delay(200);
        
        // Child should be disconnected
        Assert.That(client2.Proxy.IntListBi.State, Is.EqualTo(NexusCollectionState.Disconnected));
        
        await server1.DisposeAsync();
        await server2.DisposeAsync();
    }

    [TestCase(Type.Tcp)]
    public async Task ConnectToParentFailsWhenTypesDoNotMatch(Type type)
    {
        var (server1, serverNexus1, client1, _) = await ConnectServerAndClient(type);
        var (server2, serverNexus2, client2, _) = await ConnectServerAndClient(type);

        // Connect parent collection  
        await client1.Proxy.IntListBi.ConnectAsync().Timeout(1);
        
        // Try to connect different collection types (should fail)
        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await client2.Proxy.IntListSvToCl.ConnectAsync(client1.Proxy.IntListBi).Timeout(1));
        
        await server1.DisposeAsync();
        await server2.DisposeAsync();
    }

    [TestCase(Type.Tcp)]
    public async Task ConnectToParentFailsWhenAlreadyConnected(Type type)
    {
        var (server1, serverNexus1, client1, _) = await ConnectServerAndClient(type);
        var (server2, serverNexus2, client2, _) = await ConnectServerAndClient(type);

        // Connect parent collection
        await client1.Proxy.IntListBi.ConnectAsync().Timeout(1);
        
        // Connect child to server first
        await client2.Proxy.IntListBi.ConnectAsync().Timeout(1);
        
        // Try to connect to parent when already connected (should fail)
        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await client2.Proxy.IntListBi.ConnectAsync(client1.Proxy.IntListBi).Timeout(1));
        
        await server1.DisposeAsync();
        await server2.DisposeAsync();
    }

    [TestCase(Type.Tcp)]
    public async Task ConnectToParentFailsWithNullParent(Type type)
    {
        var (server2, serverNexus2, client2, _) = await ConnectServerAndClient(type);
        
        try
        {
            // Try to connect with null parent (should fail)
            Assert.ThrowsAsync<ArgumentNullException>(
                async () => await client2.Proxy.IntListBi.ConnectAsync(null!).Timeout(1));
        }
        finally
        {
            await server2.DisposeAsync();
        }
    }*/
}
