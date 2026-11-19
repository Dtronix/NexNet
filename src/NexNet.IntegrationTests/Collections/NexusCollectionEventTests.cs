using NexNet.Collections;

namespace NexNet.IntegrationTests.Collections;

internal class NexusCollectionEventTests : NexusCollectionBaseTests
{
    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task EventRacing_DoesNotCauseTimeout(Type type)
    {
        var (server, client, _) = await ConnectServerAndClient(type);
        var serverNexus = server.NexusCreatedQueue.First();
        await client.Proxy.IntListBi.EnableAsync();
        
        var completeTask = client.Proxy.IntListBi.WaitForEvent(NexusCollectionChangedAction.Add, 40);

        // Create rapid sequence of operations that might cause event racing
        var tasks = new List<Task>();
        for (int i = 0; i < 20; i++)
        {
            tasks.Add(serverNexus.IntListBi.AddAsync(i));
            tasks.Add(client.Proxy.IntListBi.AddAsync(i + 100));
        }

        await completeTask.Wait();

        await Assert.That(client.Proxy.IntListBi.Count).IsEqualTo(40);
        await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task MultipleEventSubscribers_HandleConcurrency(Type type)
    {
        var (server, client, _) = await ConnectServerAndClient(type);
        var serverNexus = server.NexusCreatedQueue.First();
        await client.Proxy.IntListBi.EnableAsync();

        var subscriber1Events = 0;
        var subscriber2Events = 0;
        var subscriber3Events = 0;

        // Multiple event subscribers
        client.Proxy.IntListBi.Changed.Subscribe(args => Interlocked.Increment(ref subscriber1Events));
        client.Proxy.IntListBi.Changed.Subscribe(args => Interlocked.Increment(ref subscriber2Events));
        client.Proxy.IntListBi.Changed.Subscribe(args => Interlocked.Increment(ref subscriber3Events));
        
        var waitEvent = client.Proxy.IntListBi.WaitForEvent(NexusCollectionChangedAction.Add, 10);

        // Perform operations
        for (int i = 0; i < 10; i++)
        {
            await serverNexus.IntListBi.AddAsync(i);
        }

        await waitEvent.Wait();

        await Task.Delay(100); // Allow events to propagate

        await Assert.That(subscriber1Events).IsEqualTo(10);
        await Assert.That(subscriber2Events).IsEqualTo(10);
        await Assert.That(subscriber3Events).IsEqualTo(10);
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task EventException_DoesNotBreakSystem(Type type)
    {
        var (server, client, _) = await ConnectServerAndClient(type);
        var serverNexus = server.NexusCreatedQueue.First();
        await client.Proxy.IntListBi.EnableAsync();

        var goodEventCount = 0;

        // One subscriber that throws exceptions
        client.Proxy.IntListBi.Changed.Subscribe(args => throw new InvalidOperationException("Test exception"));

        // One subscriber that works normally
        client.Proxy.IntListBi.Changed.Subscribe(args => Interlocked.Increment(ref goodEventCount));

        // Operations should continue working despite exceptions
        await serverNexus.IntListBi.AddAsync(42);
        await Task.Delay(100);

        await Assert.That(goodEventCount).IsEqualTo(1);
        await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task ServerToClientCollection_ServerCanModify(Type type)
    {
        var (server, client, _) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(2));

        await client.Proxy.IntListSC.EnableAsync().Timeout(T(2));
        await client.Proxy.IntListSC.ReadyTask.Timeout(T(2));

        var serverNexus = server.NexusCreatedQueue.First();
        await serverNexus.IntListSC.AddAsync(100);

        await Task.Delay(200);

        await Assert.That(client.Proxy.IntListSC.Count).IsEqualTo(1);
        await Assert.That(client.Proxy.IntListSC[0]).IsEqualTo(100);
        await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
    }
}