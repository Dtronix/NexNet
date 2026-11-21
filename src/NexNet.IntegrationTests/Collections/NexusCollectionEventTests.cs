using NexNet.Collections;
using NexNet.IntegrationTests.Collections.Lists;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.IntegrationTests.TestInterfaces;

namespace NexNet.IntegrationTests.Collections;

/// <summary>Change events and concurrent changes on the client under test's lists.</summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class NexusCollectionEventTests(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task EventRacing_DoesNotCauseTimeout(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await list.EnableAsync().Timeout(T(1));

        // Create rapid sequence of operations that might cause event racing
        var tasks = new List<Task>();
        for (int i = 0; i < 20; i++)
        {
            tasks.Add(s.ServerList.AddAsync(i));
            tasks.Add(list.AddAsync(i + 100).AsTask());
        }

        var items = await WaitForCountAsync(list, 40);
        await Task.WhenAll(tasks).Timeout(T(2));

        await Assert.That(items.Length).IsEqualTo(40);
        await s.Client.WaitForStateAsync(ConnectionState.Connected, T(1));
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task MultipleEventSubscribers_HandleConcurrency(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await list.EnableAsync().Timeout(T(1));

        // Multiple event subscribers
        await using var subscriber1 = await WatchAsync(list);
        await using var subscriber2 = await WatchAsync(list);
        await using var subscriber3 = await WatchAsync(list);

        // Perform operations
        for (int i = 0; i < 10; i++)
        {
            await s.ServerList.AddAsync(i).Timeout(T(1));
        }

        // Every subscriber sees each event.
        foreach (var subscriber in new[] { subscriber1, subscriber2, subscriber3 })
        {
            await WaitForChangesAsync(subscriber, NexusCollectionChangedAction.Add, 10);
            await Assert.That(subscriber.Changes.Count(c => c.Action == NexusCollectionChangedAction.Add)).IsEqualTo(10);
        }

        await subscriber1.DisposeAsync();
        await subscriber2.DisposeAsync();
        await subscriber3.DisposeAsync();
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task EventException_DoesNotBreakSystem(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await list.EnableAsync().Timeout(T(1));

        // One subscriber that throws a native exception after recording each change (attached first)
        await using var throwing = await list.WatchThrowingAsync().AsTask().Timeout(T(1));

        // One subscriber that works normally
        await using var counting = await WatchAsync(list);

        // Operations should continue working despite exceptions
        await s.ServerList.AddAsync(42).Timeout(T(1));
        await WaitForChangesAsync(counting, NexusCollectionChangedAction.Add);
        await WaitForChangesAsync(throwing, NexusCollectionChangedAction.Add);
        await Assert.That(counting.Changes.Count(c => c.Action == NexusCollectionChangedAction.Add)).IsEqualTo(1);

        await s.ServerList.AddAsync(43).Timeout(T(1));
        await WaitForChangesAsync(counting, NexusCollectionChangedAction.Add, 2);
        await Assert.That(await ItemsAsync(list)).IsEquivalentTo(new[] { 42, 43 });

        await s.Client.WaitForStateAsync(ConnectionState.Connected, T(1));
        await throwing.DisposeAsync();
        await counting.DisposeAsync();
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task ServerToClientCollection_ServerCanModify(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.Client.List<int>(nameof(IServerNexus.IntListSC));

        await Assert.That(await list.EnableAsync().Timeout(T(2))).IsTrue();

        await s.Nexus.IntListSC.AddAsync(100).Timeout(T(1));

        var items = await WaitForCountAsync(list, 1);

        await Assert.That(items.Length).IsEqualTo(1);
        await Assert.That(items[0]).IsEqualTo(100);
        await s.Client.WaitForStateAsync(ConnectionState.Connected, T(1));
        await s.EndAsync();
    }
}
