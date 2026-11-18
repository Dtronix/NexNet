using NexNet.Collections;
using NexNet.Collections.Lists;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Collections;

/// <summary>
/// Noop answers, client protocol errors and the client collection state (protocol specification §12).
/// </summary>
internal class NexusCollectionNoopTests : NexusCollectionBaseTests
{
    [TestCase(Type.Tcp)]
    [TestCase(Type.Uds)]
    public async Task ConcurrentRemoveOfSameIndex_LoserCompletesFalse(Type type)
    {
        var (server, client1, _) = await ConnectServerAndClient(type);
        var serverNexus = server.NexusCreatedQueue.First();
        var (client2, _) = CreateClient(CreateClientConfig(type));
        await client2.ConnectAsync().Timeout(1);

        await serverNexus.IntListBi.AddAsync(1).Timeout(1);
        await serverNexus.IntListBi.AddAsync(2).Timeout(1);
        await serverNexus.IntListBi.AddAsync(3).Timeout(1);

        var list1 = client1.Proxy.IntListBi;
        var list2 = client2.Proxy.IntListBi;
        Assert.That(await list1.EnableAsync().Timeout(1), Is.True);
        Assert.That(await list2.EnableAsync().Timeout(1), Is.True);

        using var removed1 = list1.WaitForEvent(NexusCollectionChangedAction.Remove);
        using var removed2 = list2.WaitForEvent(NexusCollectionChangedAction.Remove);

        // Both requests are built from the same base version: each reads its version synchronously before sending,
        // and the other client's change cannot have been applied yet.
        var remove1 = list1.RemoveAtAsync(0);
        var remove2 = list2.RemoveAtAsync(0);

        var results = await Task.WhenAll(remove1, remove2).Timeout(1);

        await removed1.Wait();
        await removed2.Wait();

        Assert.Multiple(() =>
        {
            Assert.That(results, Is.EquivalentTo(new[] { true, false }));
            Assert.That(serverNexus.IntListBi, Is.EqualTo(new[] { 2, 3 }));
            Assert.That(list1, Is.EqualTo(new[] { 2, 3 }));
            Assert.That(list2, Is.EqualTo(new[] { 2, 3 }));
            Assert.That(list1.State, Is.EqualTo(NexusCollectionState.Connected));
            Assert.That(list2.State, Is.EqualTo(NexusCollectionState.Connected));
        });

        // The collection still works after the Noop.
        Assert.That(await list2.AddAsync(4).Timeout(1), Is.True);
    }

    [TestCase(Type.Tcp, 5)]
    [TestCase(Type.Tcp, 1)]
    [TestCase(Type.Uds, 5)]
    [TestCase(Type.Uds, 1)]
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

        Assert.Multiple(() =>
        {
            Assert.That(enabled, Is.False);
            Assert.That(list.State, Is.EqualTo(NexusCollectionState.Disconnected));
            Assert.That(list.Count, Is.EqualTo(0));
        });

        // The session stays up, and a correct reset succeeds.
        ((NexusListServer<int>)serverNexus.IntListBi).TestResetTotalValuesOverride = null;
        Assert.That(await list.EnableAsync().Timeout(1), Is.True);
        Assert.That(list, Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [TestCase(Type.Tcp)]
    [TestCase(Type.Uds)]
    public async Task State_FollowsEnableAndDisable(Type type)
    {
        var (_, client, _) = await ConnectServerAndClient(type);
        var list = client.Proxy.IntListBi;

        Assert.That(list.State, Is.EqualTo(NexusCollectionState.Disconnected));

        Assert.That(await list.EnableAsync().Timeout(1), Is.True);
        Assert.That(list.State, Is.EqualTo(NexusCollectionState.Connected));

        await list.DisableAsync().Timeout(1);
        Assert.That(list.State, Is.EqualTo(NexusCollectionState.Disconnected));

        Assert.That(await list.EnableAsync().Timeout(1), Is.True);
        Assert.That(list.State, Is.EqualTo(NexusCollectionState.Connected));

        await client.DisconnectAsync().Timeout(1);
        await list.DisabledTask.Timeout(1);
        Assert.That(list.State, Is.EqualTo(NexusCollectionState.Disconnected));
    }

    [TestCase(Type.Tcp)]
    [TestCase(Type.Uds)]
    public async Task EnableTokenCanceledAfterEnable_DisableStillCleansUp(Type type)
    {
        var (_, client, _) = await ConnectServerAndClient(type);
        var list = client.Proxy.IntListBi;

        using var cts = new CancellationTokenSource();
        Assert.That(await list.EnableAsync(cts.Token).Timeout(1), Is.True);

        // Canceling the enable token must not stop the cleanup that runs when the pipe closes.
        await cts.CancelAsync();

        await list.DisableAsync().Timeout(1);
        Assert.That(list.State, Is.EqualTo(NexusCollectionState.Disconnected));

        Assert.That(await list.EnableAsync().Timeout(1), Is.True);
        Assert.That(list.State, Is.EqualTo(NexusCollectionState.Connected));
    }
}
