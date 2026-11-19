using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Pipes.Broadcast;

namespace NexNet.IntegrationTests.Collections;

/// <summary>
/// The server writes a new client's snapshot with backpressure: a large list reaches a slow client whole, and a
/// client that falls too far behind the live changes is closed instead of silently losing items (F26).
/// </summary>
[NotInParallel("stress")]
internal class NexusCollectionLargeSnapshotTests : NexusCollectionBaseTests
{
    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task LargeSnapshot_SlowClient_ReceivesEveryItem(Type type)
    {
        const int count = 100_000;
        var (server, client, _) = await ConnectServerAndClient(type);
        var serverList = (NexusListServer<int>)server.NexusCreatedQueue.First().IntListBi;
        serverList.TestResetTo(Enumerable.Range(0, count));

        var list = client.Proxy.IntListBi;
        var broadcaster = (NexusBroadcastClient<INexusCollectionListMessage>)(object)list;

        // Throttle the client: it reads its snapshot far slower than the server can write it.
        var processed = 0;
        broadcaster.ProcessTestHook = _ =>
        {
            if (Interlocked.Increment(ref processed) % 25 == 0)
                Thread.Sleep(1);
        };

        try
        {
            await Assert.That(await list.EnableAsync().Timeout(30)).IsTrue();
        }
        finally
        {
            broadcaster.ProcessTestHook = null;
        }

        using (Assert.Multiple())
        {
            await Assert.That(list.State).IsEqualTo(NexusCollectionState.Connected);
            await Assert.That(list.Count).IsEqualTo(count);
            await Assert.That(list.SequenceEqual(Enumerable.Range(0, count))).IsTrue();
        }

        // Changes after the snapshot still arrive.
        await Assert.That(await list.AddAsync(-1).Timeout(5)).IsTrue();
        await Assert.That(list.Count).IsEqualTo(count + 1);
    }

    [Test]
    public async Task VeryLargeSnapshot_ReceivesEveryItem()
    {
        const int count = 1_000_000;
        var (server, client, _) = await ConnectServerAndClient(Type.Uds);
        var serverList = (NexusListServer<int>)server.NexusCreatedQueue.First().IntListBi;
        serverList.TestResetTo(Enumerable.Range(0, count));

        var list = client.Proxy.IntListBi;
        await Assert.That(await list.EnableAsync().Timeout(60)).IsTrue();

        using (Assert.Multiple())
        {
            await Assert.That(list.Count).IsEqualTo(count);
            await Assert.That(list.SequenceEqual(Enumerable.Range(0, count))).IsTrue();
        }
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task ClientFallsBehindDuringSnapshot_PipeClosesAndReEnableSyncs(Type type)
    {
        const int count = 100_000;
        var (server, client, _) = await ConnectServerAndClient(type);
        var serverList = (NexusListServer<int>)server.NexusCreatedQueue.First().IntListBi;
        serverList.TestResetTo(Enumerable.Range(0, count));

        var list = client.Proxy.IntListBi;
        var broadcaster = (NexusBroadcastClient<INexusCollectionListMessage>)(object)list;

        // Stall the client at its first message until the server has made more changes than a client may lag.
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var armed = 1;
        broadcaster.ProcessTestHook = _ =>
        {
            if (Interlocked.Exchange(ref armed, 0) != 1)
                return;
            stalled.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(30));
        };

        Task<bool> enable;
        try
        {
            enable = list.EnableAsync().AsTask();
            await stalled.Task.Timeout(5);

            for (var i = 0; i < 1500; i++)
                await serverList.AddAsync(count + i).Timeout(5);
        }
        finally
        {
            release.Set();
            broadcaster.ProcessTestHook = null;
        }

        // The server closed the lagging client's pipe: the collection is disabled, never left with a partial list.
        await list.DisabledTask.Timeout(10);
        await enable.Timeout(10);
        await Assert.That(list.State).IsEqualTo(NexusCollectionState.Disconnected);

        // The session stays up, and a new enable gets the whole list.
        await Assert.That(await list.EnableAsync().Timeout(30)).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(list.Count).IsEqualTo(count + 1500);
            await Assert.That(list.SequenceEqual(serverList)).IsTrue();
        }
    }
}
