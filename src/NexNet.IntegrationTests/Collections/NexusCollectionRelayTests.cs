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
}
