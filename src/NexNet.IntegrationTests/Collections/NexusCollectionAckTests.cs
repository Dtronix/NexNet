 using System.Diagnostics;
using NexNet.Collections;
using NexNet.Pipes.Broadcast;
using System.Threading.Tasks;

namespace NexNet.IntegrationTests.Collections;

internal class NexusCollectionAckTests : NexusCollectionBaseTests
{
    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task UpdateAndWaitAsync_CompletesOnAcknowledgment(Type type)
    {
        var (server, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.EnableAsync();

        var stopwatch = Stopwatch.StartNew();
        var result = await client.Proxy.IntListBi.AddAsync(42);
        stopwatch.Stop();

        await Assert.That(result).IsTrue();
        await Assert.That(stopwatch.ElapsedMilliseconds).IsLessThan(1000);
        await Assert.That(client.Proxy.IntListBi.Contains(42)).IsTrue();
    }
    
    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task UpdateAndWaitAsync_TimesOutOnNoAcknowledgment(Type type)
    {
        var (server, client, _) = await ConnectServerAndClient(type);
        var serverNexus = server.NexusCreatedQueue.First();
        await client.Proxy.IntListBi.EnableAsync();

        // Disable acknowledgments to simulate timeout
        var internalCollection = (INexusBroadcastServerTestModifier)serverNexus.IntListBi;
        internalCollection.DoNotSendAck = true;

        var stopwatch = Stopwatch.StartNew();
        var addTask = client.Proxy.IntListBi.AddAsync(42);

        // Should not complete within reasonable time
        var completedTask = await Task.WhenAny(addTask, Task.Delay(100));
        stopwatch.Stop();

        await Assert.That(completedTask).IsNotEqualTo(addTask);
        await Assert.That(stopwatch.ElapsedMilliseconds).IsGreaterThan(90);
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task MultipleOperations_ReceiveCorrectAcknowledgments(Type type)
    {
        var (server, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.EnableAsync();

        // Send multiple operations concurrently
        var tasks = new List<Task<bool>>();
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(client.Proxy.IntListBi.AddAsync(i));
        }

        var results = await Task.WhenAll(tasks);
        // TODO: TUnit migration - Complex NUnit constraint. Manual conversion required.

        Assert.That(results, Is.All.True);
        await Assert.That(client.Proxy.IntListBi.Count).IsEqualTo(10);
    }
    
    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task DisconnectDuringOperation_CompletesWithFalse(Type type)
    {
        var (server, client, _) = await ConnectServerAndClient(type);
        var serverNexus = server.NexusCreatedQueue.First();
        await client.Proxy.IntListBi.EnableAsync();

        var internalCollection = (INexusBroadcastServerTestModifier)serverNexus.IntListBi;
        internalCollection.DoNotSendAck = true;

        var addTask = client.Proxy.IntListBi.AddAsync(42);

        // Disconnect while operation is pending
        await Task.Delay(100);
        await client.DisconnectAsync();

        var result = await addTask;
        await Assert.That(result).IsFalse();
    }
}