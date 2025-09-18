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
    public async Task AddedParentItemsAreRelayedToChildCollection()
    {
        var clSv = await CreateRelayCollectionClientServers(true);
        
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay; 
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
    public async Task RelayConnection_RecoversFromTransientFailures()
    {
        var clSv = await CreateRelayCollectionClientServers(false);

        // Start relay first (will fail to connect initially)
        await clSv.Server2.StartAsync();
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;

        // Parent not available yet - relay should be disconnected
        await Task.Delay(100);
        Assert.That(relayList.State, Is.EqualTo(NexusCollectionState.Disconnected));

        // Now start parent - relay should recover
        await clSv.Server1.StartAsync();
        await relayList.ReadyTask.Timeout(2);

        Assert.That(relayList.State, Is.EqualTo(NexusCollectionState.Connected));
    }

    [Test]
    public async Task RelayChain_ThreeLevelHierarchy()
    {
        // Create three servers: Parent -> Child1 -> Child2
        var parent = await CreateRelayCollectionClientServers(false);
        var child1 = await CreateRelayCollectionClientServers(false);
        var child2 = await CreateRelayCollectionClientServers(false);

        await parent.Server1.StartAsync();
        await child1.Server1.StartAsync();
        await child1.Server2.StartAsync();
        await child2.Server1.StartAsync();
        await child2.Server2.StartAsync();

        var parentList = parent.Server1.ContextProvider.Rent().Collections.IntListBi;
        var child1Relay = child1.Server2.ContextProvider.Rent().Collections.IntListRelay;
        var child2Relay = child2.Server2.ContextProvider.Rent().Collections.IntListRelay;

        await child1Relay.ReadyTask.Timeout(1);
        await child2Relay.ReadyTask.Timeout(1);

        // Add items to parent
        for (int i = 0; i < 5; i++)
            await parentList.AddAsync(i);

        // Wait for propagation through the chain
        await Task.Delay(100);

        // Verify all collections have the same items
        Assert.That(parentList.Count, Is.EqualTo(5));
        Assert.That(child1Relay.Count, Is.EqualTo(5));
        Assert.That(child2Relay.Count, Is.EqualTo(5));

        Assert.That(parentList, Is.EquivalentTo(child1Relay));
        Assert.That(parentList, Is.EquivalentTo(child2Relay));
    }

    [Test]
    public async Task MultipleRelays_Connect()
    {
        var clSvs= await CreateRelayCollectionServers();
        await clSvs.ParentRelay.ReadyTask.Timeout(1);
        await clSvs.Child1Relay.ReadyTask.Timeout(1);
        await clSvs.Child2Relay.ReadyTask.Timeout(1);
    }
    
    [Test]
    public async Task MultipleRelays_RelayUpdates()
    {
        var clSvs= await CreateRelayCollectionServers();

        var child1Wait = clSvs.Child1Relay.WaitForEvent(NexusCollectionChangedAction.Add, 10);
        var child2Wait = clSvs.Child2Relay.WaitForEvent(NexusCollectionChangedAction.Add, 10);

        await clSvs.ParentRelay.ReadyTask.Timeout(1);
        await clSvs.Child1Relay.ReadyTask.Timeout(1);
        await clSvs.Child2Relay.ReadyTask.Timeout(1);
        for (int i = 0; i < 10; i++)
            await clSvs.ParentRelay.AddAsync(i);
        try
        {
            await child1Wait.Wait();
            await child2Wait.Wait();
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
        
        Assert.That(clSvs.ParentRelay, Is.EquivalentTo(clSvs.Child1Relay));
        Assert.That(clSvs.ParentRelay, Is.EquivalentTo(clSvs.Child2Relay));
    }
    
    
    [Test]
    public async Task MultipleRelays_Disconnect()
    {
        var clSvs= await CreateRelayCollectionServers();
        
        await clSvs.ParentRelay.ReadyTask.Timeout(1);
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
    public async Task RelayStress_MultipleChildCollections()
    {
        // Create one parent with multiple child relays
        var parent = await CreateRelayCollectionClientServers(false);
        var child1 = await CreateRelayCollectionClientServers(false);
        var child2 = await CreateRelayCollectionClientServers(false);
        var child3 = await CreateRelayCollectionClientServers(false);

        await parent.Server1.StartAsync();
        await child1.Server1.StartAsync();
        await child1.Server2.StartAsync();
        await child2.Server1.StartAsync();
        await child2.Server2.StartAsync();
        await child3.Server1.StartAsync();
        await child3.Server2.StartAsync();

        var parentList = parent.Server1.ContextProvider.Rent().Collections.IntListBi;
        var relay1 = child1.Server2.ContextProvider.Rent().Collections.IntListRelay;
        var relay2 = child2.Server2.ContextProvider.Rent().Collections.IntListRelay;
        var relay3 = child3.Server2.ContextProvider.Rent().Collections.IntListRelay;

        await relay1.ReadyTask.Timeout(1);
        await relay2.ReadyTask.Timeout(1);
        await relay3.ReadyTask.Timeout(1);

        // Add items to parent
        for (int i = 0; i < 20; i++)
        {
            await parentList.AddAsync(i);
        }

        // Wait for propagation
        await Task.Delay(200);

        // Verify all relays have the same data
        Assert.That(relay1, Is.EquivalentTo(parentList));
        Assert.That(relay2, Is.EquivalentTo(parentList));
        Assert.That(relay3, Is.EquivalentTo(parentList));
    }

    [Test]
    public async Task RelayEvents_ChangedEventPropagation()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;

        await relayList.ReadyTask.Timeout(1);

        var eventReceived = false;
        NexusCollectionChangedAction? receivedAction = null;

        relayList.Changed.Subscribe(args =>
        {
            eventReceived = true;
            receivedAction = args.ChangedAction;
        });

        // Trigger an add operation
        await sourceList.AddAsync(42);
        await Task.Delay(100);

        Assert.That(eventReceived, Is.True);
        Assert.That(receivedAction, Is.EqualTo(NexusCollectionChangedAction.Add));
    }

    [Test]
    public async Task RelayWithDifferentOperationTypes()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;

        await relayList.ReadyTask.Timeout(1);

        // Test various operation types
        await sourceList.AddAsync(1);
        await sourceList.AddAsync(2);
        await sourceList.AddAsync(3);
        await Task.Delay(50);

        await sourceList.InsertAsync(1, 99);
        await Task.Delay(50);

        await sourceList.MoveAsync(0, 3);
        await Task.Delay(50);

        await sourceList.ReplaceAsync(2, 88);
        await Task.Delay(50);

        await sourceList.RemoveAtAsync(1);
        await Task.Delay(50);

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

    [Test]
    public async Task RelayDisconnection_GracefulShutdown()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;
        await relayList.ReadyTask.Timeout(1);

        // Normal shutdown
        await clSv.Server2.StopAsync();

        // Should disconnect gracefully without exceptions
        await relayList.DisconnectedTask.Timeout(1);
        Assert.That(relayList.State, Is.EqualTo(NexusCollectionState.Disconnected));
    }

    [Test]
    public async Task RelayCancellation_ProperResourceCleanup()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;
        await relayList.ReadyTask.Timeout(1);

        var relayCollection = relayList as NexusCollection;
        Assert.That(relayCollection, Is.Not.Null);

        // Stop relay and verify cleanup
        relayCollection.StopRelay();
        await relayList.DisconnectedTask.Timeout(1);

        Assert.That(relayList.State, Is.EqualTo(NexusCollectionState.Disconnected));

        // Verify no memory leaks or hanging resources
        await Task.Delay(100);

        // Should not throw during cleanup
        Assert.DoesNotThrow(() => relayCollection.StopRelay());
    }
}
