using NexNet.Collections;
using NexNet.Collections.Lists;

namespace NexNet.IntegrationTests.Collections;

/// <summary>
/// Noop answers, client protocol errors and the client collection state (protocol specification §12).
/// </summary>
internal class NexusCollectionNoopTests : NexusCollectionBaseTests
{
    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task ConcurrentRemoveOfSameIndex_LoserCompletesFalse(Type type)
    {
        var (server, client1, _) = await ConnectServerAndClient(type);
        var serverNexus = server.NexusCreatedQueue.First();
        var (client2, _) = CreateClient(CreateClientConfig(type));
        await client2.ConnectAsync().Timeout(TestTimeouts.Connect);

        await serverNexus.IntListBi.AddAsync(1).Timeout(1);
        await serverNexus.IntListBi.AddAsync(2).Timeout(1);
        await serverNexus.IntListBi.AddAsync(3).Timeout(1);

        var list1 = client1.Proxy.IntListBi;
        var list2 = client2.Proxy.IntListBi;
        await Assert.That(await list1.EnableAsync().Timeout(1)).IsTrue();
        await Assert.That(await list2.EnableAsync().Timeout(1)).IsTrue();

        using var removed1 = list1.WaitForEvent(NexusCollectionChangedAction.Remove);
        using var removed2 = list2.WaitForEvent(NexusCollectionChangedAction.Remove);

        // Both requests are built from the same base version: each reads its version synchronously before sending,
        // and the other client's change cannot have been applied yet.
        var remove1 = list1.RemoveAtAsync(0);
        var remove2 = list2.RemoveAtAsync(0);

        var results = await Task.WhenAll(remove1, remove2).Timeout(1);

        await removed1.Wait();
        await removed2.Wait();

        using (Assert.Multiple())
        {
            await Assert.That(results).IsEquivalentTo(new[] { true, false });
            await Assert.That(serverNexus.IntListBi).IsEquivalentTo(new[] { 2, 3 }, CollectionOrdering.Matching);
            await Assert.That(list1).IsEquivalentTo(new[] { 2, 3 }, CollectionOrdering.Matching);
            await Assert.That(list2).IsEquivalentTo(new[] { 2, 3 }, CollectionOrdering.Matching);
            await Assert.That(list1.State).IsEqualTo(NexusCollectionState.Connected);
            await Assert.That(list2.State).IsEqualTo(NexusCollectionState.Connected);

        }

        // The collection still works after the Noop.
        await Assert.That(await list2.AddAsync(4).Timeout(1)).IsTrue();
    }

    [Test]
    [Arguments(Type.Tcp, 5)]
    [Arguments(Type.Tcp, 1)]
    [Arguments(Type.Uds, 5)]
    [Arguments(Type.Uds, 1)]
    public async Task MalformedResetValueCount_ClosesPipe(Type type, int announcedValues)
    {
        var (server, client, _) = await ConnectServerAndClient(type);
        var serverNexus = server.NexusCreatedQueue.First();

        await serverNexus.IntListBi.AddAsync(1).Timeout(1);
        await serverNexus.IntListBi.AddAsync(2).Timeout(1);
        await serverNexus.IntListBi.AddAsync(3).Timeout(1);

        // The server announces a value count that does not match the values it sends.
        ((NexusListServer<int>)serverNexus.IntListBi).TestResetTotalValuesOverride = announcedValues;

        var list = client.Proxy.IntListBi;
        var enabled = await list.EnableAsync().Timeout(1);
        await list.DisabledTask.Timeout(1);

        using (Assert.Multiple())
        {
            await Assert.That(enabled).IsFalse();
            await Assert.That(list.State).IsEqualTo(NexusCollectionState.Disconnected);
            await Assert.That(list.Count).IsEqualTo(0);

        }

        // The session stays up, and a correct reset succeeds.
        ((NexusListServer<int>)serverNexus.IntListBi).TestResetTotalValuesOverride = null;
        await Assert.That(await list.EnableAsync().Timeout(1)).IsTrue();
        await Assert.That(list).IsEquivalentTo(new[] { 1, 2, 3 }, CollectionOrdering.Matching);
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task State_FollowsEnableAndDisable(Type type)
    {
        var (_, client, _) = await ConnectServerAndClient(type);
        var list = client.Proxy.IntListBi;

        await Assert.That(list.State).IsEqualTo(NexusCollectionState.Disconnected);

        await Assert.That(await list.EnableAsync().Timeout(1)).IsTrue();
        await Assert.That(list.State).IsEqualTo(NexusCollectionState.Connected);

        await list.DisableAsync().Timeout(1);
        await Assert.That(list.State).IsEqualTo(NexusCollectionState.Disconnected);

        await Assert.That(await list.EnableAsync().Timeout(1)).IsTrue();
        await Assert.That(list.State).IsEqualTo(NexusCollectionState.Connected);

        await client.DisconnectAsync().Timeout(1);
        await list.DisabledTask.Timeout(1);
        await Assert.That(list.State).IsEqualTo(NexusCollectionState.Disconnected);
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task EnableTokenCanceledAfterEnable_DisableStillCleansUp(Type type)
    {
        var (_, client, _) = await ConnectServerAndClient(type);
        var list = client.Proxy.IntListBi;

        using var cts = new CancellationTokenSource();
        await Assert.That(await list.EnableAsync(cts.Token).Timeout(1)).IsTrue();

        // Canceling the enable token must not stop the cleanup that runs when the pipe closes.
        await cts.CancelAsync();

        await list.DisableAsync().Timeout(1);
        await Assert.That(list.State).IsEqualTo(NexusCollectionState.Disconnected);

        await Assert.That(await list.EnableAsync().Timeout(1)).IsTrue();
        await Assert.That(list.State).IsEqualTo(NexusCollectionState.Connected);
    }
}