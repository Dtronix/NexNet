using NexNet.Collections;
using NexNet.Diagnostics;
using NexNet.IntegrationTests.TestInterfaces;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Metrics;

/// <summary>
/// Collection state in session snapshots (<see cref="NexusSessionSnapshot.Collections"/>), on the server and the client.
/// </summary>
internal class CollectionSnapshotTests : BaseTests
{
    [Test]
    public async Task Snapshot_ListsEveryConfiguredCollection()
    {
        var (server, client) = await StartAsync();

        var clientCollections = client.GetSnapshot()!.Collections;
        var serverCollections = server.GetSnapshot().Sessions.Single().Collections;
        foreach (var collections in new[] { clientCollections, serverCollections })
        {
            await Assert.That(collections.Select(c => c.Mode)).IsEquivalentTo(
                [NexusCollectionMode.BiDirectional, NexusCollectionMode.Relay, NexusCollectionMode.ServerToClient]);
            await Assert.That(collections.Select(c => c.Id)).IsOrderedBy(id => id);
            await Assert.That(collections.Select(c => c.Id).Distinct().Count()).IsEqualTo(3);
            await Assert.That(collections.All(c => c.Kind == NexusCollectionKind.List)).IsTrue();

            // Nothing is enabled yet: no session is linked to any collection.
            await Assert.That(collections.All(c => c.State == NexusCollectionState.Disconnected && c.PipeIds.Count == 0)).IsTrue();
        }

        // The IDs are the same on both sides: they are the IDs on the wire.
        await Assert.That(clientCollections.Select(c => (c.Id, c.Mode))).IsEquivalentTo(serverCollections.Select(c => (c.Id, c.Mode)));
    }

    [Test]
    public async Task Snapshot_ServerOwnedList_TracksAddsRemovesAndClear()
    {
        var (server, client) = await StartAsync();
        using var context = server.ContextProvider.Rent();
        var serverList = context.Collections.IntListBi;
        var clientList = client.Proxy.IntListBi;
        await Assert.That(await clientList.EnableAsync().Timeout(1)).IsTrue();

        var (clientEntry, serverEntry) = await BothAsync(server, client, NexusCollectionMode.BiDirectional, 0);
        await Assert.That(clientEntry.State).IsEqualTo(NexusCollectionState.Connected);
        await Assert.That(serverEntry.State).IsEqualTo(NexusCollectionState.Connected);
        await Assert.That(clientEntry.PipeIds).Count().IsEqualTo(1);
        await Assert.That(serverEntry.PipeIds).IsEquivalentTo(clientEntry.PipeIds);
        await Assert.That(client.GetSnapshot()!.Pipes.Select(p => p.Id)).Contains(clientEntry.PipeIds[0]);
        var version = serverEntry.Version;

        await Assert.That(await serverList.AddAsync(1).Timeout(1)).IsTrue();
        await Assert.That(await serverList.AddAsync(2).Timeout(1)).IsTrue();
        await Assert.That(await serverList.AddAsync(3).Timeout(1)).IsTrue();
        (clientEntry, serverEntry) = await BothAsync(server, client, NexusCollectionMode.BiDirectional, 3);
        await Assert.That(serverEntry.Version).IsEqualTo(version + 3);
        await Assert.That(clientEntry.Version).IsEqualTo(serverEntry.Version);

        await Assert.That(await serverList.RemoveAsync(2).Timeout(1)).IsTrue();
        (clientEntry, serverEntry) = await BothAsync(server, client, NexusCollectionMode.BiDirectional, 2);
        await Assert.That(serverEntry.Version).IsEqualTo(version + 4);
        await Assert.That(clientEntry.Version).IsEqualTo(serverEntry.Version);

        // A change from the client counts the same.
        await Assert.That(await clientList.AddAsync(9).Timeout(1)).IsTrue();
        (clientEntry, serverEntry) = await BothAsync(server, client, NexusCollectionMode.BiDirectional, 3);
        await Assert.That(serverEntry.Version).IsEqualTo(version + 5);
        await Assert.That(clientEntry.Version).IsEqualTo(serverEntry.Version);

        await Assert.That(await serverList.ClearAsync().Timeout(1)).IsTrue();
        (clientEntry, serverEntry) = await BothAsync(server, client, NexusCollectionMode.BiDirectional, 0);
        await Assert.That(serverEntry.Version).IsEqualTo(version + 6);
        await Assert.That(clientEntry.Version).IsEqualTo(serverEntry.Version);
        await Assert.That(clientEntry.State).IsEqualTo(NexusCollectionState.Connected);
    }

    [Test]
    public async Task Snapshot_ServerToClientList_ReportsItsCountOnBothSides()
    {
        var (server, client) = await StartAsync();
        using var context = server.ContextProvider.Rent();
        var serverList = context.Collections.IntListSC;
        await Assert.That(await serverList.AddAsync(1).Timeout(1)).IsTrue();
        await Assert.That(await serverList.AddAsync(2).Timeout(1)).IsTrue();

        // The server holds the items before any client enables the list.
        var serverEntry = Entry(server.GetSnapshot().Sessions.Single(), NexusCollectionMode.ServerToClient);
        await Assert.That(serverEntry.Count).IsEqualTo(2);
        await Assert.That(serverEntry.PipeIds).IsEmpty();
        await Assert.That(Entry(client.GetSnapshot()!, NexusCollectionMode.ServerToClient).Count).IsEqualTo(0);

        await Assert.That(await client.Proxy.IntListSC.EnableAsync().Timeout(1)).IsTrue();
        var (clientEntry, linked) = await BothAsync(server, client, NexusCollectionMode.ServerToClient, 2);
        await Assert.That(clientEntry.Version).IsEqualTo(linked.Version);
        await Assert.That(clientEntry.State).IsEqualTo(NexusCollectionState.Connected);
        await Assert.That(linked.PipeIds).IsEquivalentTo(clientEntry.PipeIds);

        // The other lists stay unlinked.
        var others = client.GetSnapshot()!.Collections.Where(c => c.Mode != NexusCollectionMode.ServerToClient);
        await Assert.That(others.All(c => c.PipeIds.Count == 0)).IsTrue();
    }

    [Test]
    public async Task Snapshot_DisabledList_IsNoLongerLinked()
    {
        var (server, client) = await StartAsync();
        using var context = server.ContextProvider.Rent();
        await Assert.That(await context.Collections.IntListBi.AddAsync(5).Timeout(1)).IsTrue();
        var clientList = client.Proxy.IntListBi;
        await Assert.That(await clientList.EnableAsync().Timeout(1)).IsTrue();
        await BothAsync(server, client, NexusCollectionMode.BiDirectional, 1);

        await clientList.DisableAsync().Timeout(1);

        var clientEntry = Entry(client.GetSnapshot()!, NexusCollectionMode.BiDirectional);
        await Assert.That(clientEntry.State).IsEqualTo(NexusCollectionState.Disconnected);
        await Assert.That(clientEntry.PipeIds).IsEmpty();
        // A disabled client list drops its items; the server keeps them.
        await Assert.That(clientEntry.Count).IsEqualTo(0);

        NexusCollectionSnapshot? serverEntry = null;
        await MetricsSessionTests.WaitFor(() =>
        {
            serverEntry = Entry(server.GetSnapshot().Sessions.Single(), NexusCollectionMode.BiDirectional);
            return serverEntry.PipeIds.Count == 0;
        }).Timeout(2);
        await Assert.That(serverEntry!.State).IsEqualTo(NexusCollectionState.Disconnected);
        await Assert.That(serverEntry.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Snapshot_FromSessionContext_IncludesCollections()
    {
        var snapshot = new TaskCompletionSource<NexusSessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(CreateServerConfig(Type.Uds), nexus =>
            nexus.ServerTaskEvent = n =>
            {
                snapshot.TrySetResult(n.Context.GetSnapshot());
                return ValueTask.CompletedTask;
            });
        await server.StartAsync().Timeout(TestTimeouts.Connect);
        var (client, _) = CreateClient(CreateClientConfig(Type.Uds));
        await client.ConnectAsync().Timeout(TestTimeouts.Connect);
        await Assert.That(await client.Proxy.IntListSC.EnableAsync().Timeout(1)).IsTrue();

        await client.Proxy.ServerTask().Timeout(1);
        var entry = Entry(await snapshot.Task.Timeout(1), NexusCollectionMode.ServerToClient);
        await Assert.That(entry.State).IsEqualTo(NexusCollectionState.Connected);
        await Assert.That(entry.PipeIds).Count().IsEqualTo(1);
    }

    private async Task<(NexusServer<ServerNexus, ServerNexus.ClientProxy> Server, NexusClient<ClientNexus, ClientNexus.ServerProxy> Client)> StartAsync()
    {
        var server = CreateServer(CreateServerConfig(Type.Uds), _ => { });
        await server.StartAsync().Timeout(TestTimeouts.Connect);
        var (client, _) = CreateClient(CreateClientConfig(Type.Uds));
        await client.ConnectAsync().Timeout(TestTimeouts.Connect);
        return (server, client);
    }

    private static NexusCollectionSnapshot Entry(NexusSessionSnapshot snapshot, NexusCollectionMode mode)
        => snapshot.Collections.Single(c => c.Mode == mode);

    /// <summary>
    /// Waits until the client's and the server session's entries both hold <paramref name="count"/> items at the same
    /// version: the client applies a change after the server, and confirms its own changes before the broadcast ends.
    /// </summary>
    private static async Task<(NexusCollectionSnapshot Client, NexusCollectionSnapshot Server)> BothAsync(
        NexusServer<ServerNexus, ServerNexus.ClientProxy> server,
        NexusClient<ClientNexus, ClientNexus.ServerProxy> client,
        NexusCollectionMode mode,
        int count)
    {
        NexusCollectionSnapshot? clientEntry = null;
        NexusCollectionSnapshot? serverEntry = null;
        await MetricsSessionTests.WaitFor(() =>
        {
            clientEntry = Entry(client.GetSnapshot()!, mode);
            serverEntry = Entry(server.GetSnapshot().Sessions.Single(), mode);
            return clientEntry.Count == count && serverEntry.Count == count && clientEntry.Version == serverEntry.Version;
        }).Timeout(2);
        return (clientEntry!, serverEntry!);
    }
}
