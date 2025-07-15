﻿using NexNet.Collections;
using NexNet.IntegrationTests.TestInterfaces;
using NUnit.Framework;
using System.Threading.Channels;

namespace NexNet.IntegrationTests.Collections;

internal class NexusCollectionChannelTests : NexusCollectionBaseTests
{
    [TestCase(Type.Tcp)]
    [TestCase(Type.Uds)]
    public async Task ServerProcessChannel_HandlesCapacityLimit(Type type)
    {
        var (server, serverNexus, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.ConnectAsync();

        // Create 60 rapid operations to exceed 50-item channel capacity
        var tasks = new List<Task>();
        for (int i = 0; i < 60; i++)
        {
            tasks.Add(serverNexus.IntListBi.AddAsync(i));
        }

        // Should not throw or timeout
        await Task.WhenAll(tasks).Timeout(10);

        Assert.That(serverNexus.IntListBi.Count, Is.EqualTo(60));
        Assert.That(client.Proxy.IntListBi.Count, Is.EqualTo(60));
    }

    [TestCase(Type.Tcp)]
    [TestCase(Type.Uds)]
    public async Task ClientMessageChannel_HandlesSaturation(Type type)
    {
        var (server, serverNexus, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.ConnectAsync();

        // Flood client with 20 rapid server operations to test 10-item client channel
        var tasks = new List<Task>();
        for (int i = 0; i < 20; i++)
        {
            tasks.Add(serverNexus.IntListBi.AddAsync(i));
        }

        // Client should not disconnect due to channel saturation
        await Task.WhenAll(tasks).Timeout(5);

        await Task.Delay(20000);


        //Assert.That(client.State, Is.EqualTo(ConnectionState.Connected));
        //Assert.That(client.Proxy.IntListBi.Count, Is.EqualTo(20));
        Assert.Fail();
    }

    [TestCase(Type.Tcp)]
    [TestCase(Type.Uds)]
    public async Task ConcurrentOperations_DoNotBlockChannel(Type type)
    {
        var (server, serverNexus, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.ConnectAsync();

        // Mix of client and server operations
        var clientTasks = Enumerable.Range(0, 25).Select(i =>
            client.Proxy.IntListBi.AddAsync(i * 2)).ToArray();
        var serverTasks = Enumerable.Range(0, 25).Select(i =>
            serverNexus.IntListBi.AddAsync(i * 2 + 1)).ToArray();

        await Task.WhenAll(clientTasks.Concat(serverTasks)).Timeout(10);

        Assert.That(client.Proxy.IntListBi.Count, Is.EqualTo(50));
        Assert.That(serverNexus.IntListBi.Count, Is.EqualTo(50));
    }

    [TestCase(Type.Tcp)]
    [TestCase(Type.Uds)]
    public async Task SlowClient_GetsDisconnectedOnChannelFull(Type type)
    {
        var (server, serverNexus, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.ConnectAsync();

        // Access internal collection to simulate slow client
        var internalCollection = (NexusCollection)client.Proxy.IntListBi;
        internalCollection.DoNotSendAck = true; // Simulate slow processing

        // Send many operations to overflow client channel
        var tasks = new List<Task>();
        for (int i = 0; i < 15; i++)
        {
            tasks.Add(serverNexus.IntListBi.AddAsync(i));
        }

        await Task.WhenAll(tasks).Timeout(5);

        // Client should be disconnected due to slow processing
        await Task.Delay(100);
        Assert.That(client.State, Is.EqualTo(ConnectionState.Disconnected));
    }
}
