using NexNet.Collections;
using NexNet.Collections.Lists;

namespace NexNet.IntegrationTests.Collections;

/// <summary>
/// The server sends a new client its snapshot before any change, and answers a Noop after the change that made the
/// request obsolete (protocol specification §12).
/// </summary>
internal class NexusCollectionSnapshotOrderTests : NexusCollectionBaseTests
{
    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task ClientsJoiningMidStream_ReceiveSnapshotBeforeChanges(Type type)
    {
        const int writers = 4;
        const int writesPerWriter = 150;
        const int joiners = 6;

        var (server, client1, _) = await ConnectServerAndClient(type);
        var serverList = server.NexusCreatedQueue.First().IntListBi;

        var writerList = client1.Proxy.IntListBi;
        await Assert.That(await writerList.EnableAsync().Timeout(1)).IsTrue();

        var joinerClients = new NexusClient<TestInterfaces.ClientNexus, TestInterfaces.ClientNexus.ServerProxy>[joiners];
        for (var i = 0; i < joiners; i++)
        {
            (joinerClients[i], _) = CreateClient(CreateClientConfig(type));
            await joinerClients[i].ConnectAsync().Timeout(1);
        }

        using var start = new ManualResetEventSlim(false);

        // Server writers mix inserts, removes and replaces; one client writer goes through the processor too.
        var writerTasks = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            start.Wait();
            for (var i = 0; i < writesPerWriter; i++)
            {
                var value = w * 100_000 + i;
                switch (i % 5)
                {
                    case 3 when serverList.Count > 10:
                        await serverList.RemoveAtAsync(0).ConfigureAwait(false);
                        break;
                    case 4 when serverList.Count > 0:
                        await serverList.ReplaceAsync(0, value).ConfigureAwait(false);
                        break;
                    default:
                        await serverList.AddAsync(value).ConfigureAwait(false);
                        break;
                }
            }
        })).Append(Task.Run(async () =>
        {
            start.Wait();
            for (var i = 0; i < writesPerWriter; i++)
                await writerList.AddAsync(-i - 1).ConfigureAwait(false);
        })).ToArray();

        // Joiners enable while the writers run, then disable and enable again to take a second snapshot.
        var joinerTasks = joinerClients.Select((c, i) => Task.Run(async () =>
        {
            start.Wait();
            var list = c.Proxy.IntListBi;
            await Task.Delay(i * 3).ConfigureAwait(false);
            if (!await list.EnableAsync().ConfigureAwait(false))
                throw new InvalidOperationException("Enable failed.");
            await Task.Delay(5).ConfigureAwait(false);
            await list.DisableAsync().ConfigureAwait(false);
            if (!await list.EnableAsync().ConfigureAwait(false))
                throw new InvalidOperationException("Second enable failed.");
        })).ToArray();

        start.Set();
        await Task.WhenAll(writerTasks.Concat(joinerTasks)).Timeout(20);

        var expected = serverList.ToArray();
        var lists = joinerClients.Select(c => c.Proxy.IntListBi).Append(writerList).ToArray();

        // Every broadcast is queued; wait for the clients to apply them.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !lists.All(l => l.SequenceEqual(expected)))
            await Task.Delay(10);

        for (var i = 0; i < lists.Length; i++)
        {
            var list = lists[i];
            var listClient = (NexusListClient<int>)list;
            using (Assert.Multiple())
            {
                await Assert.That(list.State).IsEqualTo(NexusCollectionState.Connected).Because($"list {i} state");
                await Assert.That(list.ToArray()).IsEquivalentTo(expected, CollectionOrdering.Matching)
                    .Because($"list {i} contents");
                await Assert.That(listClient.TestChangesReceivedBeforeSnapshot).IsEqualTo(0)
                    .Because($"list {i} must not receive changes before its snapshot");
            }
        }
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task ConcurrentRemove_LoserSeesWinningChangeBeforeFalse(Type type)
    {
        const int rounds = 30;

        var (server, client1, _) = await ConnectServerAndClient(type);
        var serverList = server.NexusCreatedQueue.First().IntListBi;
        var (client2, _) = CreateClient(CreateClientConfig(type));
        await client2.ConnectAsync().Timeout(1);

        for (var i = 0; i < rounds; i++)
            await serverList.AddAsync(i).Timeout(1);

        var list1 = client1.Proxy.IntListBi;
        var list2 = client2.Proxy.IntListBi;
        await Assert.That(await list1.EnableAsync().Timeout(1)).IsTrue();
        await Assert.That(await list2.EnableAsync().Timeout(1)).IsTrue();

        var noops = 0;
        for (var round = 0; round < rounds / 2; round++)
        {
            // Both lists hold every change made so far: each request completes after its own change, or after the
            // change that made it obsolete.
            var deadline = DateTime.UtcNow.AddSeconds(1);
            while (DateTime.UtcNow < deadline && (list1.Count != serverList.Count || list2.Count != serverList.Count))
                await Task.Delay(1);

            var results = await Task.WhenAll(list1.RemoveAtAsync(0), list2.RemoveAtAsync(0)).Timeout(1);

            // Read before anything else runs. A request that completed with false (a Noop) must already see the
            // winning change. When both win, the other client's change may still be on its way, so nothing is checked.
            int count1 = list1.Count, count2 = list2.Count, serverCount = serverList.Count;
            if (results.Contains(false))
                noops++;

            using (Assert.Multiple())
            {
                await Assert.That(results.Contains(true)).IsTrue().Because($"round {round}: one request wins");
                if (!results[0])
                    await Assert.That(count1).IsEqualTo(serverCount).Because($"round {round}: list 1 got its Noop before the winning change");
                if (!results[1])
                    await Assert.That(count2).IsEqualTo(serverCount).Because($"round {round}: list 2 got its Noop before the winning change");
            }
        }

        await Assert.That(noops).IsGreaterThan(0).Because("at least one round must produce a Noop");
    }
}
