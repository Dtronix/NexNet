using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Pipes.Broadcast;

namespace NexNet.IntegrationTests.Collections;

/// <summary>
/// A relay applies each change from its parent on its processing loop, so a client that connects while a change is
/// relayed gets it exactly once: in its snapshot or as a broadcast after it, never both (protocol specification §12).
/// </summary>
internal class NexusCollectionRelayOrderingTests : NexusCollectionBaseTests
{
    [Test]
    public async Task ClientConnectingDuringRelayedChange_ReceivesItOnce()
    {
        var clSv = await CreateRelayCollectionClientServers(true);
        var serverRelay = (NexusListRelay<int>)clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;
        await serverRelay.ReadyTask.Timeout(1);
        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;

        await clSv.Client2.ConnectAsync().Timeout(TestTimeouts.Connect);
        var clientList = clSv.Client2.Proxy.IntListRelay;

        // Counts every insert the client receives, including one its snapshot already holds.
        var inserts = 0;
        var broadcaster = (NexusBroadcastClient<INexusCollectionListMessage>)(object)clientList;
        broadcaster.ProcessTestHook = message =>
        {
            if (message is NexusCollectionListInsertMessage)
                Interlocked.Increment(ref inserts);
        };

        Task<bool>? enable = null;
        var hookRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        serverRelay.RelayChangeTestHook = async () =>
        {
            serverRelay.RelayChangeTestHook = null;

            // The relay holds the change but has not broadcast it yet: the client connects in this gap. Its
            // registration waits for the processing loop, so it can only complete once the change is broadcast;
            // give it the chance to complete here anyway.
            enable = clientList.EnableAsync().AsTask();
            await Task.WhenAny(enable, Task.Delay(500)).ConfigureAwait(false);
            hookRan.SetResult();
        };

        await sourceList.AddAsync(42);
        await hookRan.Task.Timeout(5);
        await Assert.That(await enable!.Timeout(5)).IsTrue();

        // Every broadcast reaches the client in order: once this later change arrives, any duplicate has too.
        using var waitAdd = clientList.WaitForEvent(NexusCollectionChangedAction.Add);
        await sourceList.AddAsync(43);
        await waitAdd.Wait();

        using (Assert.Multiple())
        {
            await Assert.That(inserts).IsEqualTo(1).Because("the first change is in the snapshot and must not be broadcast to the client too");
            await Assert.That(clientList.ToArray()).IsEquivalentTo(new[] { 42, 43 }, CollectionOrdering.Matching);
            await Assert.That(serverRelay.ToArray()).IsEquivalentTo(new[] { 42, 43 }, CollectionOrdering.Matching);
        }
    }
}
