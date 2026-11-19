using NexNet.Collections;
using NexNet.IntegrationTests.Collections.Lists;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.IntegrationTests.TestInterfaces;

namespace NexNet.IntegrationTests.Collections;

/// <summary>Change events and concurrent changes on the client under test's lists.</summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class NexusCollectionEventTests(LanguageHostPool hosts) : CollectionMatrixTests(hosts)
{
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp])]
    public async Task MultipleEventSubscribers_HandleConcurrency(ClientTarget target)
    {
        RequireInProc(target, NoListEvents);
        await using var s = await ConnectAsync(target);
        var list = s.List;
        var clientList = InProc(s.Client).Client.Proxy.IntListBi;
        await list.EnableAsync().Timeout(T(1));

        var subscriber1Events = 0;
        var subscriber2Events = 0;
        var subscriber3Events = 0;

        // Multiple event subscribers
        using var subscription1 = clientList.Changed.Subscribe(args => Interlocked.Increment(ref subscriber1Events));
        using var subscription2 = clientList.Changed.Subscribe(args => Interlocked.Increment(ref subscriber2Events));
        using var subscription3 = clientList.Changed.Subscribe(args => Interlocked.Increment(ref subscriber3Events));

        using var waitEvent = clientList.WaitForEvent(NexusCollectionChangedAction.Add, 10);

        // Perform operations
        for (int i = 0; i < 10; i++)
        {
            await s.ServerList.AddAsync(i).Timeout(T(1));
        }

        await waitEvent.Wait();

        // Every subscriber sees each event; the waiter may complete before the others ran for the last one.
        await WaitUntil(() => Volatile.Read(ref subscriber1Events) == 10
                              && Volatile.Read(ref subscriber2Events) == 10
                              && Volatile.Read(ref subscriber3Events) == 10);

        await Assert.That(subscriber1Events).IsEqualTo(10);
        await Assert.That(subscriber2Events).IsEqualTo(10);
        await Assert.That(subscriber3Events).IsEqualTo(10);
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp])]
    public async Task EventException_DoesNotBreakSystem(ClientTarget target)
    {
        RequireInProc(target, NoListEvents);
        await using var s = await ConnectAsync(target);
        var list = s.List;
        var clientList = InProc(s.Client).Client.Proxy.IntListBi;
        await list.EnableAsync().Timeout(T(1));

        var goodEventCount = 0;

        // One subscriber that throws exceptions
        using var throwing = clientList.Changed.Subscribe(args => throw new InvalidOperationException("Test exception"));

        // One subscriber that works normally
        using var counting = clientList.Changed.Subscribe(args => Interlocked.Increment(ref goodEventCount));

        // Operations should continue working despite exceptions
        await s.ServerList.AddAsync(42).Timeout(T(1));
        await WaitUntil(() => Volatile.Read(ref goodEventCount) >= 1);

        await Assert.That(goodEventCount).IsEqualTo(1);
        await s.Client.WaitForStateAsync(ConnectionState.Connected, T(1));
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp])]
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
