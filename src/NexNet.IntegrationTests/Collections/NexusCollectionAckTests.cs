 using System.Diagnostics;
using NexNet.Collections;
using NexNet.Pipes.Broadcast;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Collections;

internal class NexusCollectionAckTests : NexusCollectionBaseTests
{
    [TestCase(Type.Tcp)]
    [TestCase(Type.Uds)]
    public async Task UpdateAndWaitAsync_CompletesOnAcknowledgment(Type type)
    {
        var (server, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.EnableAsync();

        var stopwatch = Stopwatch.StartNew();
        var result = await client.Proxy.IntListBi.AddAsync(42);
        stopwatch.Stop();

        Assert.That(result, Is.True);
        Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(1000));
        Assert.That(client.Proxy.IntListBi.Contains(42), Is.True);
    }

    [TestCase(Type.Tcp)]
    [TestCase(Type.Uds)]
    public async Task UpdateAndWaitAsync_TimesOutOnNoAcknowledgment(Type type)
    {
        var (server, client, _) = await ConnectServerAndClient(type);
        var serverNexus = server.NexusCreatedQueue.First();
        await client.Proxy.IntListBi.EnableAsync();

        // Disable acknowledgments to simulate timeout
        var internalCollection = (NexusBroadcastServer)serverNexus.IntListBi;
        internalCollection.DoNotSendAck = true;

        var stopwatch = Stopwatch.StartNew();
        var addTask = client.Proxy.IntListBi.AddAsync(42);

        // Should not complete within reasonable time
        var completedTask = await Task.WhenAny(addTask, Task.Delay(100));
        stopwatch.Stop();

        Assert.That(completedTask, Is.Not.EqualTo(addTask));
        Assert.That(stopwatch.ElapsedMilliseconds, Is.GreaterThan(90));
    }

    [TestCase(Type.Tcp)]
    [TestCase(Type.Uds)]
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

        Assert.That(results, Is.All.True);
        Assert.That(client.Proxy.IntListBi.Count, Is.EqualTo(10));
    }

    [TestCase(Type.Tcp)]
    [TestCase(Type.Uds)]
    public async Task DisconnectDuringOperation_CompletesWithFalse(Type type)
    {
        var (server, client, _) = await ConnectServerAndClient(type);
        var serverNexus = server.NexusCreatedQueue.First();
        await client.Proxy.IntListBi.EnableAsync();

        var internalCollection = (NexusBroadcastServer)serverNexus.IntListBi;
        internalCollection.DoNotSendAck = true;

        var addTask = client.Proxy.IntListBi.AddAsync(42);

        // Disconnect while operation is pending
        await Task.Delay(100);
        await client.DisconnectAsync();

        var result = await addTask;
        Assert.That(result, Is.False);
    }
}
