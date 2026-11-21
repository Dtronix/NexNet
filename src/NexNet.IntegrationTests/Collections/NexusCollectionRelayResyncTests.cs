using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Collections.Lists;

namespace NexNet.IntegrationTests.Collections;

/// <summary>
/// A relay that resyncs from its parent (Ready after a reconnect, Reset, or Move) resets the clients already connected
/// to it, so they converge to the parent's state without reconnecting.
/// </summary>
internal class NexusCollectionRelayResyncTests : NexusCollectionBaseTests
{
    [Test]
    public async Task ParentReconnect_ResetsConnectedRelayClients()
    {
        // Fake time on the relay server holds its 500 ms reconnect delay, so the parent changes while the relay is
        // disconnected from it.
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var clSv = await CreateRelayCollectionClientServers(true, server2Time: fakeTime);
        var (sourceList, serverRelay, clientList) = await ConnectRelayClient(clSv);

        for (var i = 0; i < 5; i++)
            await sourceList.AddAsync(i);
        await WaitForContents(clientList, [0, 1, 2, 3, 4]);

        await clSv.Server1.StopAsync().Timeout(TestTimeouts.Connect);
        await serverRelay.DisconnectedTask.Timeout(TestTimeouts.Connect);
        await clSv.Server1.StartAsync().Timeout(TestTimeouts.Connect);

        await sourceList.RemoveAtAsync(0);
        await sourceList.AddAsync(100);
        await sourceList.AddAsync(101);
        var expected = sourceList.ToArray();
        await Assert.That(expected).IsEquivalentTo(new[] { 1, 2, 3, 4, 100, 101 });

        // Release the reconnect delay until the relay is connected to the parent again.
        var ready = serverRelay.ReadyTask;
        var sw = Stopwatch.StartNew();
        while (!ready.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(TestTimeouts.Connect))
        {
            fakeTime.Advance(TimeSpan.FromMilliseconds(500));
            await Task.WhenAny(ready, Task.Delay(50));
        }

        await ready.Timeout(1);

        await WaitForContents(clientList, expected);
        await Assert.That(clientList.DisabledTask.IsCompleted).IsFalse();
    }

    [Test]
    public async Task ParentReset_ResetsConnectedRelayClients()
    {
        var clSv = await CreateRelayCollectionClientServers(true);
        var (sourceList, _, clientList) = await ConnectRelayClient(clSv);

        for (var i = 0; i < 5; i++)
            await sourceList.AddAsync(i);
        await WaitForContents(clientList, [0, 1, 2, 3, 4]);

        await sourceList.ClearAsync();
        await WaitForContents(clientList, []);

        // Changes after the reset apply on top of it.
        await sourceList.AddAsync(10);
        await sourceList.AddAsync(11);
        await WaitForContents(clientList, [10, 11]);
        await Assert.That(clientList.DisabledTask.IsCompleted).IsFalse();
    }

    [Test]
    public async Task ParentMove_ResetsConnectedRelayClients()
    {
        var clSv = await CreateRelayCollectionClientServers(true);
        var (sourceList, _, clientList) = await ConnectRelayClient(clSv);

        for (var i = 0; i < 5; i++)
            await sourceList.AddAsync(i);
        await WaitForContents(clientList, [0, 1, 2, 3, 4]);

        await sourceList.MoveAsync(0, 4);
        await WaitForContents(clientList, [1, 2, 3, 4, 0]);

        // Changes after the reset apply on top of it.
        await sourceList.ReplaceAsync(0, 50);
        await WaitForContents(clientList, [50, 2, 3, 4, 0]);
        await Assert.That(clientList.DisabledTask.IsCompleted).IsFalse();
    }

    private static async Task<(INexusList<int> Source, INexusList<int> ServerRelay, INexusList<int> Client)>
        ConnectRelayClient(RelayedCollectionServerClient clSv)
    {
        var serverRelay = clSv.Server2.ContextProvider.Rent().Collections.IntListRelay;
        await serverRelay.ReadyTask.Timeout(TestTimeouts.Connect);

        var sourceList = clSv.Server1.ContextProvider.Rent().Collections.IntListBi;

        await clSv.Client2.ConnectAsync().Timeout(TestTimeouts.Connect);
        var clientList = clSv.Client2.Proxy.IntListRelay;
        await clientList.EnableAsync().Timeout(TestTimeouts.Connect);
        await clientList.ReadyTask.Timeout(TestTimeouts.Connect);
        return (sourceList, serverRelay, clientList);
    }

    private static async Task WaitForContents(INexusList<int> list, int[] expected)
    {
        var sw = Stopwatch.StartNew();
        while (!list.SequenceEqual(expected))
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(TestTimeouts.CollectionEvent))
                throw new TimeoutException(
                    $"Relay client list is [{string.Join(", ", list)}], expected [{string.Join(", ", expected)}].");
            await Task.Delay(10);
        }
    }
}
