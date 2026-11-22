using NexNet.Collections;
using NexNet.IntegrationTests.Collections.Lists;
using NexNet.Interop;
using NexNet.Interop.Hosts;

namespace NexNet.IntegrationTests.Collections;

/// <summary>Bursts of list operations through the collection's message channels.</summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class NexusCollectionChannelTests(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task ServerProcessChannel_HandlesCapacityLimit(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await list.EnableAsync().Timeout(T(1));

        // Create 60 rapid operations to exceed 50-item channel capacity
        var tasks = new List<Task>();
        for (int i = 0; i < 60; i++)
        {
            tasks.Add(s.ServerList.AddAsync(i));
        }

        // Should not throw or timeout
        await Task.WhenAll(tasks).Timeout(T(10));

        var items = await WaitForCountAsync(list, 60);

        await Assert.That(s.ServerList.Count).IsEqualTo(60);
        await Assert.That(items.Length).IsEqualTo(60);
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task ClientMessageChannel_HandlesSaturation(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await list.EnableAsync().Timeout(T(1));

        // Flood the client with 50 rapid server operations to test its message channel
        var tasks = new List<Task>();
        for (int i = 0; i < 50; i++)
        {
            tasks.Add(s.ServerList.AddAsync(i));
        }

        // Client should not disconnect due to channel saturation
        await Task.WhenAll(tasks).Timeout(T(2));

        var items = await WaitForCountAsync(list, 50);

        await s.Client.WaitForStateAsync(ConnectionState.Connected, T(1));
        await Assert.That(items.Length).IsEqualTo(50);
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task ConcurrentOperations_DoNotBlockChannel(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await list.EnableAsync().Timeout(T(1));

        // Mix of client and server operations
        var clientTasks = Enumerable.Range(0, 25).Select(i =>
            list.AddAsync(i * 2).AsTask()).ToArray();
        var serverTasks = Enumerable.Range(0, 25).Select(i =>
            s.ServerList.AddAsync(i * 2 + 1)).ToArray();

        await Task.WhenAll(clientTasks.Concat(serverTasks)).Timeout(T(10));

        var items = await WaitForCountAsync(list, 50);

        await Assert.That(items.Length).IsEqualTo(50);
        await Assert.That(s.ServerList.Count).IsEqualTo(50);
        await Assert.That(s.ServerList).IsEquivalentTo(items, CollectionOrdering.Matching);
        await s.EndAsync();
    }
}
