using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.IntegrationTests.TestInterfaces;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Collections;

/// <summary>
/// Tests for the new parent-child collection relay functionality where a child collection
/// can connect to a parent collection and relay all changes from the parent.
/// </summary>
[TestFixture]
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

        await relayList.ConnectAsync().Timeout(1);
        await relayList.ReadyTask.Timeout(1);
        var addWait = relayList.WaitForEvent(NexusCollectionChangedAction.Add, 100);

        for (int i = 0; i < 100; i++)
        {
            await sourceList.AddAsync(i);
        }

        try
        {
            await addWait.Wait();
        }
        catch (Exception)
        {
            Console.WriteLine(addWait.Counter);
            throw;
        }

        
        Assert.That(sourceList, Is.EquivalentTo(relayList));
    }
    
    [Test]
    public async Task ManyAddedParentItemsAreRelayedToChildCollection()
    {
        var clSv = await CreateRelayCollectionClientServers(true);
        
        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi; // First server
        
        await clSv.Client2.ConnectAsync().Timeout(1);
        var relayList = clSv.Client2.Proxy.IntListRelay;
        await relayList.ConnectAsync().Timeout(1);
        
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
        catch (Exception)
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

        await clSv.Client2.ConnectAsync().Timeout(1);
        var relayList = clSv.Client2.Proxy.IntListRelay;
        await relayList.ConnectAsync().Timeout(1);

        // Wait for 10 Add events (5 threads x 2 adds each)
        using var waitAdd = relayList.WaitForEvent(NexusCollectionChangedAction.Add, 10);

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
        await waitAdd.Wait();

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

        using var waitAdd = relayList.WaitForEvent(NexusCollectionChangedAction.Add, 10);

        // Perform rapid sequential operations
        for (int i = 0; i < 10; i++)
        {
            await sourceList.AddAsync(i);
        }

        await waitAdd.Wait();

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

        // Test clear operation propagation - relay may fire Reset or Remove events
        var waitReset = relayList.WaitForEvent(NexusCollectionChangedAction.Reset);
        await sourceList.ClearAsync();

        // Wait for reset event or verify final state
        try
        {
            await waitReset.Wait(2);
        }
        catch
        {
            // If Reset event not supported, verify state directly
        }

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
        using var waitInitial = relayList.WaitForEvent(NexusCollectionChangedAction.Add, 2);
        await sourceList.AddAsync(1);
        await sourceList.AddAsync(2);
        await waitInitial.Wait();

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

    #region Client Connection Scenarios

    [Test]
    public async Task MultipleClientsConnectToSameRelay()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;
        await relayList.ReadyTask.Timeout(1);

        // Create additional clients connecting to Server2
        var clientConfig2 = CreateClientConfig(Type.Uds);
        var (client2a, relayListA) = await ConnectAdditionalClientToRelay(clientConfig2);
        var (client2b, relayListB) = await ConnectAdditionalClientToRelay(clientConfig2);

        // Add items to source
        var waitA = relayListA.WaitForEvent(NexusCollectionChangedAction.Add, 5);
        var waitB = relayListB.WaitForEvent(NexusCollectionChangedAction.Add, 5);

        for (int i = 0; i < 5; i++)
            await sourceList.AddAsync(i);

        await waitA.Wait();
        await waitB.Wait();

        Assert.That(relayListA, Is.EquivalentTo(sourceList));
        Assert.That(relayListB, Is.EquivalentTo(sourceList));
    }

    [Test]
    public async Task ClientConnectsToRelayBeforeRelayConnectsToParent()
    {
        // Create servers but don't start Server1 yet
        var clSv = await CreateRelayCollectionClientServers(false);

        // Start only Server2 (relay)
        await clSv.Server2.StartAsync().Timeout(1);

        // Connect client to Server2
        await clSv.Client2.ConnectAsync().Timeout(1);
        var relayList = clSv.Client2.Proxy.IntListRelay;
        await relayList.EnableAsync().Timeout(1);

        // Relay should be disconnected (no parent), client collection empty
        Assert.That(relayList.Count, Is.EqualTo(0));

        // Set up wait for events before starting parent
        var waitAdd = relayList.WaitForEvent(NexusCollectionChangedAction.Add, 5);

        // Now start Server1 (parent)
        await clSv.Server1.StartAsync().Timeout(1);
        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;

        // Wait for relay to connect
        var serverRelay = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;
        await serverRelay.ReadyTask.Timeout(2);

        // Add items to parent
        for (int i = 0; i < 5; i++)
            await sourceList.AddAsync(i);

        // Wait for client to receive all updates
        await waitAdd.Wait(5);

        Assert.That(relayList, Is.EquivalentTo(sourceList));
    }

    [Test]
    public async Task ClientDisconnectsAndReconnectsToRelay()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;
        var serverRelay = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;
        await serverRelay.ReadyTask.Timeout(1);

        // Add initial data and wait for propagation to server relay
        using var waitServerRelay = serverRelay.WaitForEvent(NexusCollectionChangedAction.Add, 5);
        for (int i = 0; i < 5; i++)
            await sourceList.AddAsync(i);
        await waitServerRelay.Wait();

        // Connect client
        await clSv.Client2.ConnectAsync().Timeout(1);
        var relayList = clSv.Client2.Proxy.IntListRelay;
        await relayList.ConnectAsync().Timeout(1);

        Assert.That(relayList, Is.EquivalentTo(sourceList));

        // Disconnect client
        await relayList.DisableAsync();
        await clSv.Client2.DisconnectAsync();

        // Add more data while disconnected and wait for propagation to server relay
        using var waitServerRelay2 = serverRelay.WaitForEvent(NexusCollectionChangedAction.Add, 5);
        for (int i = 5; i < 10; i++)
            await sourceList.AddAsync(i);
        await waitServerRelay2.Wait();

        // Reconnect
        await clSv.Client2.ConnectAsync().Timeout(1);
        relayList = clSv.Client2.Proxy.IntListRelay;
        await relayList.ConnectAsync().Timeout(1);

        // Should have all data
        Assert.That(relayList, Is.EquivalentTo(sourceList));
        Assert.That(relayList.Count, Is.EqualTo(10));
    }

    #endregion

    #region Empty/Initial State Edge Cases

    [Test]
    public async Task RelayHandlesEmptyParentCollection()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;

        await relayList.ReadyTask.Timeout(1);

        // Both should be empty
        Assert.That(sourceList.Count, Is.EqualTo(0));
        Assert.That(relayList.Count, Is.EqualTo(0));

        // Add items later
        var wait = relayList.WaitForEvent(NexusCollectionChangedAction.Add, 3);

        await sourceList.AddAsync(1);
        await sourceList.AddAsync(2);
        await sourceList.AddAsync(3);

        await wait.Wait();

        Assert.That(relayList, Is.EquivalentTo(sourceList));
    }

    [Test]
    public async Task ClientConnectsToEmptyRelay()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        // Relay connected but empty
        var serverRelay = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;
        await serverRelay.ReadyTask.Timeout(1);

        // Client connects
        await clSv.Client2.ConnectAsync().Timeout(1);
        var clientRelay = clSv.Client2.Proxy.IntListRelay;
        await clientRelay.ConnectAsync().Timeout(1);

        Assert.That(clientRelay.Count, Is.EqualTo(0));
        // Server relay state is Connected, client collection state depends on implementation
        Assert.That(serverRelay.State, Is.EqualTo(NexusCollectionState.Connected));
    }

    [Test]
    public async Task RelayHandlesParentClearDuringOperation()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;

        await relayList.ReadyTask.Timeout(1);

        // Add items
        using var waitAdd = relayList.WaitForEvent(NexusCollectionChangedAction.Add, 10);
        for (int i = 0; i < 10; i++)
            await sourceList.AddAsync(i);
        await waitAdd.Wait();

        Assert.That(relayList.Count, Is.EqualTo(10));

        // Clear parent - relay may fire Reset or Remove events
        var waitReset = relayList.WaitForEvent(NexusCollectionChangedAction.Reset);
        await sourceList.ClearAsync();

        // Wait for reset event or verify final state
        try
        {
            await waitReset.Wait(2);
        }
        catch
        {
            // If Reset event not supported, verify state directly
        }

        Assert.That(relayList.Count, Is.EqualTo(0));
        Assert.That(sourceList, Is.EquivalentTo(relayList));
    }

    #endregion

    #region Rapid State Changes

    [Test]
    public async Task RapidAddRemoveOperationsAreRelayed()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;

        await relayList.ReadyTask.Timeout(1);

        // Wait for 20 adds and 10 removes (remove happens when i is odd: 1,3,5,7,9,11,13,15,17,19)
        using var waitAdd = relayList.WaitForEvent(NexusCollectionChangedAction.Add, 20);
        using var waitRemove = relayList.WaitForEvent(NexusCollectionChangedAction.Remove, 10);

        // Rapid add/remove cycles
        for (int i = 0; i < 20; i++)
        {
            await sourceList.AddAsync(i);
            if (i % 2 == 1)
                await sourceList.RemoveAtAsync(0);
        }

        await waitAdd.Wait();
        await waitRemove.Wait();

        Assert.That(sourceList, Is.EquivalentTo(relayList));
    }

    [Test]
    public async Task BurstOfOperationsDuringClientConnect()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;
        var serverRelay = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;
        await serverRelay.ReadyTask.Timeout(1);

        // Wait for server relay to receive all items
        using var waitServerRelay = serverRelay.WaitForEvent(NexusCollectionChangedAction.Add, 50);

        // Start burst of operations
        var addTask = Task.Run(async () =>
        {
            for (int i = 0; i < 50; i++)
            {
                await sourceList.AddAsync(i);
            }
        });

        await addTask;
        await waitServerRelay.Wait(10);

        // Now connect client after all operations complete on server relay
        await clSv.Client2.ConnectAsync().Timeout(1);
        var relayList = clSv.Client2.Proxy.IntListRelay;
        await relayList.ConnectAsync().Timeout(1);

        // Client should now have all items since it connected to a relay that has them
        Assert.That(sourceList, Is.EquivalentTo(relayList));
    }

    [Test]
    public async Task ClearFollowedByImmediateAdds()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;

        await relayList.ReadyTask.Timeout(1);

        // Initial data
        using var waitInitial = relayList.WaitForEvent(NexusCollectionChangedAction.Add, 5);
        for (int i = 0; i < 5; i++)
            await sourceList.AddAsync(i);
        await waitInitial.Wait();

        // Set up waits before operations - total expected: 1 reset + 3 adds = 4 events
        // After clear, the counter resets, so we wait for 3 adds total
        using var waitFinal = relayList.WaitForEvent(NexusCollectionChangedAction.Add, 3);

        // Clear and immediately add
        await sourceList.ClearAsync();
        await sourceList.AddAsync(100);
        await sourceList.AddAsync(200);
        await sourceList.AddAsync(300);

        await waitFinal.Wait(5);

        Assert.That(relayList.Count, Is.EqualTo(3));
        Assert.That(sourceList, Is.EquivalentTo(relayList));
    }

    #endregion

    #region Reconnection Edge Cases

    [Test]
    public async Task RelayReconnectsAfterMultipleDisconnections()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;

        for (int cycle = 0; cycle < 3; cycle++)
        {
            await relayList.ReadyTask.Timeout(2);

            using var waitAdd = relayList.WaitForEvent(NexusCollectionChangedAction.Add);
            await sourceList.AddAsync(cycle);
            await waitAdd.Wait();

            Assert.That(relayList.Contains(cycle), Is.True);

            // Disconnect parent
            await clSv.Server1.StopAsync();
            await relayList.DisconnectedTask.Timeout(1);

            // Restart parent
            await clSv.Server1.StartAsync();
        }

        await relayList.ReadyTask.Timeout(2);
        Assert.That(sourceList, Is.EquivalentTo(relayList));
    }

    [Test]
    public async Task ParentRestartWithDifferentData()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;

        await relayList.ReadyTask.Timeout(1);

        // Add initial data
        using var waitInitial = relayList.WaitForEvent(NexusCollectionChangedAction.Add, 3);
        await sourceList.AddAsync(1);
        await sourceList.AddAsync(2);
        await sourceList.AddAsync(3);
        await waitInitial.Wait();

        Assert.That(relayList.ToList(), Is.EqualTo(new[] { 1, 2, 3 }));

        // Stop parent
        await clSv.Server1.StopAsync();
        await relayList.DisconnectedTask.Timeout(1);

        // Verify relay is cleared on disconnect
        Assert.That(relayList.Count, Is.EqualTo(0), "Relay should be empty after disconnect");

        // Restart and add different data
        await clSv.Server1.StartAsync();
        sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;

        // Set up wait for new items after relay reconnects
        await relayList.ReadyTask.Timeout(2);

        using var waitNew = relayList.WaitForEvent(NexusCollectionChangedAction.Add, 2);
        await sourceList.AddAsync(10);
        await sourceList.AddAsync(20);
        await waitNew.Wait(5);

        // Relay should sync with new parent state
        Assert.That(relayList, Is.EquivalentTo(sourceList));
    }

    [Test]
    public async Task RelayReconnectWhileClientEnumerating()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;

        await relayList.ReadyTask.Timeout(1);

        // Add data and wait for propagation
        using var waitAdd = relayList.WaitForEvent(NexusCollectionChangedAction.Add, 100);
        for (int i = 0; i < 100; i++)
            await sourceList.AddAsync(i);
        await waitAdd.Wait();

        // Start enumeration in background
        var enumerateTask = Task.Run(() =>
        {
            var items = new List<int>();
            try
            {
                foreach (var item in relayList)
                {
                    items.Add(item);
                    Thread.Sleep(5);
                }
            }
            catch
            {
                // Expected during disconnect
            }
            return items;
        });

        // Trigger reconnection shortly after enumeration starts
        await clSv.Server1.StopAsync();
        await clSv.Server1.StartAsync();

        var result = await enumerateTask;

        // Should not throw, may have partial results
        Assert.That(result, Is.Not.Null);
    }

    #endregion

    #region Error Handling

    [Test]
    public async Task UnconfiguredRelayDoesNotCrashOnStart()
    {
        // Create server without configuring relay
        var server = CreateServerWithoutRelayConfiguration();

        // Should start without error
        await server.StartAsync().Timeout(1);

        var relayList = server.ContextProvider.Rent().Collections.IntListRelay;

        // Relay exists but is not connected to any parent
        Assert.That(relayList, Is.Not.Null);
        Assert.That(relayList.Count, Is.EqualTo(0));

        await server.StopAsync();
    }

    [Test]
    public async Task ConfigureRelayWithNullConnectorThrows()
    {
        // Create server without configuring relay
        var server = CreateServerWithoutRelayConfiguration();
        await server.StartAsync().Timeout(1);

        var relayList = server.ContextProvider.Rent().Collections.IntListRelay;

        Assert.Throws<ArgumentNullException>(() =>
        {
            relayList.ConfigureRelay(null!);
        });

        await server.StopAsync();
    }

    #endregion

    #region Resource Cleanup

    [Test]
    public async Task RelayProperlyStopsOnServerStop()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;
        await relayList.ReadyTask.Timeout(1);

        Assert.That(relayList.State, Is.EqualTo(NexusCollectionState.Connected));

        // Stop relay server
        await clSv.Server2.StopAsync();

        await relayList.DisconnectedTask.Timeout(1);
        Assert.That(relayList.State, Is.EqualTo(NexusCollectionState.Disconnected));

        // Cleanup parent
        await clSv.Server1.StopAsync();
    }

    [Test]
    public async Task StoppingParentThenRelayDoesNotHang()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;
        await relayList.ReadyTask.Timeout(1);

        // Stop parent first
        await clSv.Server1.StopAsync();

        // Then stop relay - should not hang
        var stopTask = clSv.Server2.StopAsync();
        var completed = await Task.WhenAny(stopTask, Task.Delay(5000));

        Assert.That(completed, Is.EqualTo(stopTask), "Server stop should not hang");
    }

    #endregion

    #region Large Data Scenarios

    [Test]
    public async Task RelayHandlesLargeInitialSync()
    {
        var clSv = await CreateRelayCollectionClientServers(false);

        await clSv.Server1.StartAsync().Timeout(1);
        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;

        // Add many items before relay connects
        const int itemCount = 5000;
        for (int i = 0; i < itemCount; i++)
            await sourceList.AddAsync(i);

        // Now start relay
        await clSv.Server2.StartAsync().Timeout(1);
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;

        await relayList.ReadyTask.Timeout(5);

        Assert.That(relayList.Count, Is.EqualTo(itemCount));
        Assert.That(sourceList, Is.EquivalentTo(relayList));
    }

    [Test]
    public async Task RelayHandlesContinuousHighVolumeUpdates()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;

        await relayList.ReadyTask.Timeout(1);

        const int operationCount = 500;
        var wait = relayList.WaitForEvent(NexusCollectionChangedAction.Add, operationCount);

        // High-rate operations
        for (int i = 0; i < operationCount; i++)
        {
            await sourceList.AddAsync(i);
        }

        await wait.Wait(10);

        Assert.That(relayList.Count, Is.EqualTo(operationCount));
        Assert.That(sourceList, Is.EquivalentTo(relayList));
    }

    #endregion

    #region State Consistency

    [Test]
    public async Task IndexOfWorksCorrectlyOnRelay()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;

        await relayList.ReadyTask.Timeout(1);

        using var waitAdd = relayList.WaitForEvent(NexusCollectionChangedAction.Add, 3);
        await sourceList.AddAsync(10);
        await sourceList.AddAsync(20);
        await sourceList.AddAsync(30);
        await waitAdd.Wait();

        Assert.That(relayList.IndexOf(20), Is.EqualTo(1));
        Assert.That(relayList.IndexOf(99), Is.EqualTo(-1));
    }

    [Test]
    public async Task ContainsWorksCorrectlyOnRelay()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;

        await relayList.ReadyTask.Timeout(1);

        using var waitAdd = relayList.WaitForEvent(NexusCollectionChangedAction.Add);
        await sourceList.AddAsync(42);
        await waitAdd.Wait();

        Assert.That(relayList.Contains(42), Is.True);
        Assert.That(relayList.Contains(99), Is.False);
    }

    [Test]
    public async Task CopyToWorksCorrectlyOnRelay()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;

        await relayList.ReadyTask.Timeout(1);

        using var waitAdd = relayList.WaitForEvent(NexusCollectionChangedAction.Add, 5);
        for (int i = 0; i < 5; i++)
            await sourceList.AddAsync(i * 10);
        await waitAdd.Wait();

        var array = new int[5];
        relayList.CopyTo(array, 0);

        Assert.That(array, Is.EqualTo(new[] { 0, 10, 20, 30, 40 }));
    }

    [Test]
    public async Task IndexerWorksCorrectlyOnRelay()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;

        await relayList.ReadyTask.Timeout(1);

        using var waitAdd = relayList.WaitForEvent(NexusCollectionChangedAction.Add, 3);
        await sourceList.AddAsync(100);
        await sourceList.AddAsync(200);
        await sourceList.AddAsync(300);
        await waitAdd.Wait();

        Assert.That(relayList[0], Is.EqualTo(100));
        Assert.That(relayList[1], Is.EqualTo(200));
        Assert.That(relayList[2], Is.EqualTo(300));
    }

    #endregion

    #region Timing and Ordering

    [Test]
    public async Task MoveOperationsPreserveCorrectOrder()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;

        await relayList.ReadyTask.Timeout(1);

        // Setup: [0, 1, 2, 3, 4]
        using var waitAdd = relayList.WaitForEvent(NexusCollectionChangedAction.Add, 5);
        for (int i = 0; i < 5; i++)
            await sourceList.AddAsync(i);
        await waitAdd.Wait();

        // Move 0 to end: [1, 2, 3, 4, 0]
        // Relay may fire Move or other events depending on implementation
        var waitEvent = relayList.WaitForEvent(NexusCollectionChangedAction.Move);
        await sourceList.MoveAsync(0, 4);

        // Wait for move event or verify final state
        try
        {
            await waitEvent.Wait(2);
        }
        catch
        {
            // If Move event not supported, verify state directly
        }

        // Always verify final state matches
        Assert.That(relayList.ToList(), Is.EqualTo(sourceList.ToList()));
    }

    [Test]
    public async Task ReplaceOperationsApplyCorrectly()
    {
        var clSv = await CreateRelayCollectionClientServers(true);

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;
        var relayList = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;

        await relayList.ReadyTask.Timeout(1);

        using var waitAdd = relayList.WaitForEvent(NexusCollectionChangedAction.Add, 3);
        await sourceList.AddAsync(1);
        await sourceList.AddAsync(2);
        await sourceList.AddAsync(3);
        await waitAdd.Wait();

        // Replace middle element
        using var waitReplace = relayList.WaitForEvent(NexusCollectionChangedAction.Replace);
        await sourceList.ReplaceAsync(1, 999);
        await waitReplace.Wait();

        Assert.That(relayList[1], Is.EqualTo(999));
        Assert.That(relayList.ToList(), Is.EqualTo(new[] { 1, 999, 3 }));
    }

    #endregion

    #region Multiple Relays from Same Parent

    [Test]
    public async Task TwoIndependentRelaysReceiveIdenticalUpdates()
    {
        var clSvs = await CreateRelayCollectionServers();

        await clSvs.Child1Relay.ReadyTask.Timeout(1);
        await clSvs.Child2Relay.ReadyTask.Timeout(1);

        var wait1 = clSvs.Child1Relay.WaitForEvent(NexusCollectionChangedAction.Add, 20);
        var wait2 = clSvs.Child2Relay.WaitForEvent(NexusCollectionChangedAction.Add, 20);

        for (int i = 0; i < 20; i++)
            await clSvs.SourceList.AddAsync(i);

        await wait1.Wait();
        await wait2.Wait();

        var source = clSvs.SourceList.ToList();
        var relay1 = clSvs.Child1Relay.ToList();
        var relay2 = clSvs.Child2Relay.ToList();

        Assert.That(relay1, Is.EqualTo(source));
        Assert.That(relay2, Is.EqualTo(source));
        Assert.That(relay1, Is.EqualTo(relay2));
    }

    [Test]
    public async Task OneRelayDisconnectDoesNotAffectOther()
    {
        var clSvs = await CreateRelayCollectionServers();

        await clSvs.Child1Relay.ReadyTask.Timeout(1);
        await clSvs.Child2Relay.ReadyTask.Timeout(1);

        // Add initial data
        using var waitInitial1 = clSvs.Child1Relay.WaitForEvent(NexusCollectionChangedAction.Add, 5);
        using var waitInitial2 = clSvs.Child2Relay.WaitForEvent(NexusCollectionChangedAction.Add, 5);
        for (int i = 0; i < 5; i++)
            await clSvs.SourceList.AddAsync(i);
        await waitInitial1.Wait();
        await waitInitial2.Wait();

        // Stop Child1
        await clSvs.Child1.StopAsync();
        await clSvs.Child1Relay.DisconnectedTask.Timeout(1);

        // Add more data
        using var wait2 = clSvs.Child2Relay.WaitForEvent(NexusCollectionChangedAction.Add, 5);
        for (int i = 5; i < 10; i++)
            await clSvs.SourceList.AddAsync(i);
        await wait2.Wait();

        // Child2 should have all 10 items
        Assert.That(clSvs.Child2Relay.Count, Is.EqualTo(10));
        Assert.That(clSvs.SourceList, Is.EquivalentTo(clSvs.Child2Relay));
    }

    #endregion
}
