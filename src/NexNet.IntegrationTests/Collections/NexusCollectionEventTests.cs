using NexNet.Collections;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Collections;

internal class NexusCollectionEventTests : NexusCollectionBaseTests
{
    [TestCase(Type.Tcp)]
    [TestCase(Type.Uds)]
    public async Task EventRacing_DoesNotCauseTimeout(Type type)
    {
        var (server, serverNexus, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.ConnectAsync();
        
        var completeTask = client.Proxy.IntListBi.WaitForEvent(NexusCollectionChangedAction.Add, 40);

        // Create rapid sequence of operations that might cause event racing
        var tasks = new List<Task>();
        for (int i = 0; i < 20; i++)
        {
            tasks.Add(serverNexus.IntListBi.AddAsync(i));
            tasks.Add(client.Proxy.IntListBi.AddAsync(i + 100));
        }

        await completeTask.Wait();

        Assert.That(client.Proxy.IntListBi.Count, Is.EqualTo(40));
        Assert.That(client.State, Is.EqualTo(ConnectionState.Connected));
    }

    [TestCase(Type.Tcp)]
    [TestCase(Type.Uds)]
    public async Task MultipleEventSubscribers_HandleConcurrency(Type type)
    {
        var (server, serverNexus, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.ConnectAsync();

        var subscriber1Events = 0;
        var subscriber2Events = 0;
        var subscriber3Events = 0;

        // Multiple event subscribers
        client.Proxy.IntListBi.Changed.Subscribe(args => Interlocked.Increment(ref subscriber1Events));
        client.Proxy.IntListBi.Changed.Subscribe(args => Interlocked.Increment(ref subscriber2Events));
        client.Proxy.IntListBi.Changed.Subscribe(args => Interlocked.Increment(ref subscriber3Events));

        // Perform operations
        for (int i = 0; i < 10; i++)
        {
            await serverNexus.IntListBi.AddAsync(i);
        }

        await Task.Delay(1000); // Allow events to propagate

        Assert.That(subscriber1Events, Is.EqualTo(10));
        Assert.That(subscriber2Events, Is.EqualTo(10));
        Assert.That(subscriber3Events, Is.EqualTo(10));
    }

    [TestCase(Type.Tcp)]
    [TestCase(Type.Uds)]
    public async Task EventException_DoesNotBreakSystem(Type type)
    {
        var (server, serverNexus, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.ConnectAsync();

        var goodEventCount = 0;

        // One subscriber that throws exceptions
        client.Proxy.IntListBi.Changed.Subscribe(args => throw new InvalidOperationException("Test exception"));

        // One subscriber that works normally
        client.Proxy.IntListBi.Changed.Subscribe(args => Interlocked.Increment(ref goodEventCount));

        // Operations should continue working despite exceptions
        await serverNexus.IntListBi.AddAsync(42);
        await Task.Delay(100);

        Assert.That(goodEventCount, Is.EqualTo(1));
        Assert.That(client.State, Is.EqualTo(ConnectionState.Connected));
    }
}
