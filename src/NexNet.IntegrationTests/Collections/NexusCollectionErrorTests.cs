using NexNet.Collections;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Collections;

internal class NexusCollectionErrorTests : NexusCollectionBaseTests
{
    [TestCase(Type.Tcp)]
    [TestCase(Type.Uds)]
    public async Task ServerException_DoesNotCrashClient(Type type)
    {
        var (server, client, _) = await ConnectServerAndClient(type);
        var serverNexus = server.NexusCreatedQueue.First();
        await client.Proxy.IntListBi.ConnectAsync();

        // Simulate server processing error
        var internalList = (NexusCollection)serverNexus.IntListBi;

        // Try to add item - should handle gracefully
        var result = await client.Proxy.IntListBi.AddAsync(42);

        Assert.That(client.State, Is.EqualTo(ConnectionState.Connected));
        Assert.That(result, Is.True);
    }

    [TestCase(Type.Tcp)]
    [TestCase(Type.Uds)]
    public async Task RapidReconnection_HandledGracefully(Type type)
    {
        var (server, client, _) = await ConnectServerAndClient(type);
        var serverNexus = server.NexusCreatedQueue.First();
        await client.Proxy.IntListBi.ConnectAsync();

        // Rapid disconnect/reconnect cycle
        await client.Proxy.IntListBi.DisconnectAsync();
        await client.Proxy.IntListBi.ConnectAsync();
        await client.Proxy.IntListBi.DisconnectAsync();
        await client.Proxy.IntListBi.ConnectAsync();

        // Should work normally after reconnection
        var result = await client.Proxy.IntListBi.AddAsync(42);

        Assert.That(result, Is.True);
        Assert.That(client.Proxy.IntListBi.Contains(42), Is.True);
    }
}
