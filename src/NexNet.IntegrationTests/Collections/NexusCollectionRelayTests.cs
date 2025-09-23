using NexNet.Collections;
using NexNet.Collections.Lists;
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
        var clSv = await CreateRelayCollectionClientServers(true);
        var list = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;
        Assert.That(list, Is.Not.Null);
    }
    
    [Test]
    public async Task ChildCanConnectToParentCollection()
    {
        await CreateRelayCollectionClientServers(true);
    }
    
    [Test]
    public async Task ServerReadyIsFiredOnConnection()
    {
        var clSv = await CreateRelayCollectionClientServers(true);
        
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay; 
        await relayList.ReadyTask.Timeout(1);
    }
    
    [Test]
    public async Task SourceServerReadyIsFiredImmediately()
    {
        var clSv = await CreateRelayCollectionClientServers(true);
        
        var relayList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi; 
        await relayList.ReadyTask.Timeout(1);
    }
    
    [Test]
    public async Task RelayServerDisconnectsUponServerClose()
    {
        var clSv = await CreateRelayCollectionClientServers(true);
        
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay; 
        await relayList.ReadyTask.Timeout(1);
        var disconnectTask = relayList.DisconnectedTask;
        
        await clSv.Server2.StopAsync();
        await disconnectTask.Timeout(1);
    }


    [Test]
    public async Task AddedParentItemsAreRelayedToRelayConnection()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        await clSv.Client2.ConnectAsync();
        var relayList = clSv.Client2.Proxy.IntListRelay; 
        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi; // First server
        
        await relayList.ReadyTask.Timeout(1);
        var addWait = relayList.WaitForEvent(NexusCollectionChangedAction.Add, 10);

        for (int i = 0; i < 10; i++)
        {
            await sourceList.AddAsync(1);
        }

        await addWait.Wait();
        
        Assert.That(sourceList, Is.EquivalentTo(relayList));
    }
    
    [Test]
    public async Task ManyAddedParentItemsAreRelayedToChildCollection()
    {
        var clSv = await CreateRelayCollectionClientServers(true);
        
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay; 
        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi; // First server
        
        await relayList.ReadyTask.Timeout(1);
        var addWait = relayList.WaitForEvent(NexusCollectionChangedAction.Add, 1000);

        for (int i = 0; i < 1000; i++)
        {
            await sourceList.AddAsync(i);
        }

        await addWait.Wait();
        
        Assert.That(sourceList, Is.EquivalentTo(relayList));
    }
    
    [Test]
    public async Task ParentItemsSentUponConnectionToRelayCollection()
    {
        var clSv = await CreateRelayCollectionClientServers();
        await clSv.Server1.StartAsync().Timeout(1);
        
        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi; // First server
        
        for (int i = 0; i < 10; i++)
            await sourceList.AddAsync(1);
        
        await clSv.Server2.StartAsync().Timeout(1);
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay; 
        await relayList.ReadyTask.Timeout(1);
        
        Assert.That(sourceList, Is.EquivalentTo(relayList));
    }
    
    [Test]
    public async Task RelayStaysDisconnectedUponServerStop()
    {
        var clSv = await CreateRelayCollectionClientServers(true);
        
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay; 
        await relayList.ReadyTask.Timeout(1);
        await clSv.Server2.StopAsync();

        // Delay to ensure re-connection doesn't happen automatically.
        await Task.Delay(100);
        await relayList.DisconnectedTask.Timeout(1);
    }
    
    [Test]
    public async Task RelayReconnectsUponOtherServerDisconnection()
    {
        var clSv = await CreateRelayCollectionClientServers(true);
        
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay; 
        await relayList.ReadyTask.Timeout(1);
        
        await clSv.Server1.StopAsync();
        
        await relayList.DisconnectedTask.Timeout(1);
        await clSv.Server1.StartAsync();
        await relayList.ReadyTask.Timeout(1);
    }
    
    [Test]
    public async Task RelayPreventsModifications()
    {
        var clSv = await CreateRelayCollectionClientServers(true);
        
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay; 
        await relayList.ReadyTask.Timeout(1);

        Assert.ThrowsAsync<InvalidOperationException>(async () => await relayList.ClearAsync());
        Assert.ThrowsAsync<InvalidOperationException>(async () => await relayList.InsertAsync(0, 99));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await relayList.RemoveAtAsync(0));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await relayList.RemoveAsync(99));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await relayList.AddAsync(99));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await relayList.MoveAsync(0, 1));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await relayList.ReplaceAsync(0, 1));
    }
    
    [Test]
    public async Task RelayEmptiesUponLostConnection()
    {
        var clSv = await CreateRelayCollectionClientServers(false);
        await clSv.Server1.StartAsync();
        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi; // First server

        for (int i = 0; i < 10; i++)
            await sourceList.AddAsync(1);

        await clSv.Server2.StartAsync().Timeout(1);
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;
        await relayList.ReadyTask.Timeout(1);

        Assert.That(sourceList, Is.EquivalentTo(relayList));

        await clSv.Server1.StopAsync();

        await relayList.DisconnectedTask.Timeout(1);
        Assert.That(relayList, Is.Empty);
    }

    [Test]
    public async Task ConfigureRelay_ThrowsWhenAlreadyLinked()
    {
        var clSv = await CreateRelayCollectionClientServers(false);
        await clSv.Server1.StartAsync();

        var relayCollection = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;
        Assert.That(relayCollection, Is.Not.Null);

        // Create another client pool to try configuring relay again
        var clientConfig = CreateClientConfig(Type.Uds);
        var anotherClientPool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(new NexusClientPoolConfig(clientConfig));
        var anotherConnector = anotherClientPool.GetCollectionConnector(n => n.IntListBi);

        // Try to configure relay again - should throw since it's already configured
        Assert.Throws<InvalidOperationException>(() =>
        {
            relayCollection.ConfigureRelay(anotherConnector);
        });
    }


    [Test]
    public async Task MultipleRelays_Connect()
    {
        var clSvs= await CreateRelayCollectionServers();
        await clSvs.SourceList.ReadyTask.Timeout(1);
        await clSvs.Child1Relay.ReadyTask.Timeout(1);
        await clSvs.Child2Relay.ReadyTask.Timeout(1);
    }
    
    [Test]
    public async Task MultipleRelays_RelayUpdates()
    {
        var clSvs= await CreateRelayCollectionServers();

        var child1Wait = clSvs.Child1Relay.WaitForEvent(NexusCollectionChangedAction.Add, 10);
        var child2Wait = clSvs.Child2Relay.WaitForEvent(NexusCollectionChangedAction.Add, 10);

        await clSvs.SourceList.ReadyTask.Timeout(1);
        await clSvs.Child1Relay.ReadyTask.Timeout(1);
        await clSvs.Child2Relay.ReadyTask.Timeout(1);
        for (int i = 0; i < 10; i++)
            await clSvs.SourceList.AddAsync(i);
        try
        {
            await child1Wait.Wait();
            await child2Wait.Wait();
        }
        catch (Exception e)
        {
            var ss = clSvs.Child1Relay.ToArray();
            var ss2 = clSvs.Child2Relay.ToArray();
            //Console.WriteLine(e);
            throw;
        }
        
        Assert.That(clSvs.SourceList, Is.EquivalentTo(clSvs.Child1Relay));
        Assert.That(clSvs.SourceList, Is.EquivalentTo(clSvs.Child2Relay));
    }
    
    
    [Test]
    public async Task MultipleRelays_Disconnect()
    {
        var clSvs= await CreateRelayCollectionServers();
        
        await clSvs.SourceList.ReadyTask.Timeout(1);
        await clSvs.Child1Relay.ReadyTask.Timeout(1);
        await clSvs.Child2Relay.ReadyTask.Timeout(1);

        await clSvs.Parent.StopAsync();

        // Both child relays should disconnect
        await clSvs.Child1Relay.DisconnectedTask.Timeout(1);
        await clSvs.Child2Relay.DisconnectedTask.Timeout(1);

        Assert.That(clSvs.Child1Relay.State, Is.EqualTo(NexusCollectionState.Disconnected));
        Assert.That(clSvs.Child2Relay.State, Is.EqualTo(NexusCollectionState.Disconnected));
    }


    [Test]
    public async Task RelayReconnection_DuringActiveOperations()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;

        await relayList.ReadyTask.Timeout(1);

        // Start adding items in background
        var addTask = Task.Run(async () =>
        {
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    await sourceList.AddAsync(i);
                    await Task.Delay(50);
                }
                catch
                {
                    // Ignore connection errors during reconnection
                }
            }
        });

        // Trigger reconnection during operations
        await Task.Delay(200);
        await clSv.Server1.StopAsync();
        await clSv.Server1.StartAsync();

        await addTask;
        await relayList.ReadyTask.Timeout(2);

        // Verify final state consistency
        Assert.That(sourceList, Is.EquivalentTo(relayList));
    }

    [Test]
    public async Task ConcurrentModifications_AcrossRelayHierarchy()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;

        await relayList.ReadyTask.Timeout(1);

        // Perform concurrent operations from multiple threads
        var tasks = new List<Task>();

        for (int i = 0; i < 5; i++)
        {
            int value = i;
            tasks.Add(Task.Run(async () =>
            {
                await sourceList.AddAsync(value);
                await sourceList.AddAsync(value + 100);
            }));
        }

        await Task.WhenAll(tasks);
        await Task.Delay(100); // Allow propagation

        Assert.That(sourceList.Count, Is.EqualTo(10));
        Assert.That(relayList.Count, Is.EqualTo(10));
        Assert.That(sourceList, Is.EquivalentTo(relayList));
    }

    [Test]
    public async Task RelayState_TransitionsDuringParentReconnection()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;
        await relayList.ReadyTask.Timeout(1);

        Assert.That(relayList.State, Is.EqualTo(NexusCollectionState.Connected));

        // Disconnect parent
        await clSv.Server1.StopAsync();
        await relayList.DisconnectedTask.Timeout(1);

        Assert.That(relayList.State, Is.EqualTo(NexusCollectionState.Disconnected));

        var state = relayList.ReadyTask.Status;
        // Reconnect parent
        await clSv.Server1.StartAsync();
        await relayList.ReadyTask.Timeout(2);

        Assert.That(relayList.State, Is.EqualTo(NexusCollectionState.Connected));
    }

    [Test]
    public async Task RelayedCollection_ReadOnlyEnforcement()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;
        await relayList.ReadyTask.Timeout(1);

        // Test all modification operations are blocked
        Assert.ThrowsAsync<InvalidOperationException>(async () => await relayList.ClearAsync());
        Assert.ThrowsAsync<InvalidOperationException>(async () => await relayList.InsertAsync(0, 99));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await relayList.RemoveAtAsync(0));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await relayList.RemoveAsync(99));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await relayList.AddAsync(99));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await relayList.MoveAsync(0, 1));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await relayList.ReplaceAsync(0, 1));

        // Verify read operations still work
        Assert.DoesNotThrow(() => { var count = relayList.Count; });
        Assert.DoesNotThrow(() => relayList.Contains(1));
    }

    [Test]
    public async Task RelayMessage_OrderingGuarantees()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;

        await relayList.ReadyTask.Timeout(1);

        // Perform rapid sequential operations
        for (int i = 0; i < 10; i++)
        {
            await sourceList.AddAsync(i);
        }

        // Wait for all operations to propagate
        await Task.Delay(200);

        // Verify ordering is preserved
        var sourceItems = sourceList.ToList();
        var relayItems = relayList.ToList();

        Assert.That(sourceItems, Is.EqualTo(relayItems));

        // Verify items are in correct order
        for (int i = 0; i < 10; i++)
        {
            Assert.That(relayItems[i], Is.EqualTo(i));
        }
    }

    [Test]
    public async Task RelayReset_SequenceHandling()
    {
        var clSv = await CreateRelayCollectionClientServers(false);
        await clSv.Server1.StartAsync();

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;

        // Add initial data to parent
        for (int i = 0; i < 15; i++)
            await sourceList.AddAsync(i);

        // Now start the relay - it should receive reset sequence
        await clSv.Server2.StartAsync();
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;

        await relayList.ReadyTask.Timeout(1);

        // Verify relay received all initial data
        Assert.That(relayList.Count, Is.EqualTo(15));
        Assert.That(sourceList, Is.EquivalentTo(relayList));

        // Test clear operation propagation
        await sourceList.ClearAsync();
        await Task.Delay(100);

        Assert.That(relayList.Count, Is.EqualTo(0));
        Assert.That(sourceList, Is.EquivalentTo(relayList));
    }

    [Test]
    public async Task RelayWithDifferentOperationTypes()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;
        var waitEvent = relayList.WaitForEvent(NexusCollectionChangedAction.Remove);

        await relayList.ReadyTask.Timeout(1);

        // Test various operation types
        await sourceList.AddAsync(1);
        await sourceList.AddAsync(2);
        await sourceList.AddAsync(3);
        await sourceList.InsertAsync(1, 99);
        await sourceList.MoveAsync(0, 3);
        await sourceList.ReplaceAsync(2, 88);
        await sourceList.RemoveAtAsync(1);

        await waitEvent.Wait();

        // Verify final state consistency
        Assert.That(sourceList, Is.EquivalentTo(relayList));
    }

    [Test]
    public async Task RelayRecovery_FromTransientNetworkFailures()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;

        await relayList.ReadyTask.Timeout(1);

        // Add some initial data
        await sourceList.AddAsync(1);
        await sourceList.AddAsync(2);
        await Task.Delay(50);

        // Simulate network failure
        await clSv.Server1.StopAsync();
        await relayList.DisconnectedTask.Timeout(1);

        // Add data while disconnected (to parent only)
        await clSv.Server1.StartAsync();
        await sourceList.AddAsync(3);
        await sourceList.AddAsync(4);

        // Relay should reconnect and sync
        await relayList.ReadyTask.Timeout(2);

        Assert.That(sourceList, Is.EquivalentTo(relayList));
    }
}
