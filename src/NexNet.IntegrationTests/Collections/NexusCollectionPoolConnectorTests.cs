using NexNet.Collections.Lists;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.InteropServer;

namespace NexNet.IntegrationTests.Collections;

/// <summary>
/// The collection connector of the client pool (C# <c>GetCollectionConnector</c>, Java/JS <c>collectionConnector</c>) on
/// every row of the client matrix, through <see cref="IPoolUnderTest.GetCollectionConnector{T}"/>: what a relay server does
/// with it (<see cref="NexusCollectionRelayTests"/>), as a client of the pool. The connector's collection synchronizes
/// both ways, and after the server restarts under its client the connector rents a new client whose collection
/// synchronizes again.
/// </summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class NexusCollectionPoolConnectorTests(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task PoolConnector_SynchronizesTheCollection(ClientTarget target)
    {
        await using var server = await StartServerAsync();
        var serverList = ServerList(server);
        await serverList.AddAsync(1).Timeout(T(1));
        await serverList.AddAsync(2).Timeout(T(1));

        await using var pool = await CreatePoolAsync(target, server);
        var connector = pool.GetCollectionConnector<int>(IntListBi);
        var collection = await connector.GetCollectionAsync().Timeout(T(TestTimeouts.Connect));
        var list = collection.List;

        // The snapshot, then changes both ways.
        await Assert.That(await list.EnableAsync().AsTask().Timeout(T(1))).IsTrue();
        await WaitForItemsAsync(list, 1, 2);
        await serverList.AddAsync(3).Timeout(T(1));
        await WaitForItemsAsync(list, 1, 2, 3);
        await Assert.That(await list.AddAsync(4).AsTask().Timeout(T(1))).IsTrue();
        await WaitForItemsAsync(list, 1, 2, 3, 4);
        await Assert.That(serverList).IsEquivalentTo([1, 2, 3, 4], CollectionOrdering.Matching);

        // The connector's rentals are not pool rentals; its release gives the client back to the pool.
        await Assert.That((await pool.GetStateAsync()).Rented).IsEqualTo(0);
        await connector.ReleaseAsync();
        await WaitForIdleAsync(pool, 1);

        await DisposeAndAssertNoLeaksAsync(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task PoolConnector_RentsANewClientAfterTheServerRestarts(ClientTarget target)
    {
        await using var server = await StartServerAsync();
        var serverList = ServerList(server);
        await serverList.AddAsync(1).Timeout(T(1));
        await serverList.AddAsync(2).Timeout(T(1));

        await using var pool = await CreatePoolAsync(target, server);
        var connector = pool.GetCollectionConnector<int>(IntListBi);
        var first = await connector.GetCollectionAsync().Timeout(T(TestTimeouts.Connect));
        await Assert.That(await first.List.EnableAsync().AsTask().Timeout(T(1))).IsTrue();
        await WaitForItemsAsync(first.List, 1, 2);

        // The server restarts under the connector's client: its collection is disabled with the session.
        await server.StopAsync().Timeout(T(TestTimeouts.Connect));
        await WaitForDisabledAsync(first.List);
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        // As a relay does, the connector is asked again: it rents a new client, whose collection synchronizes.
        var second = await connector.GetCollectionAsync().Timeout(T(TestTimeouts.Connect));
        await Assert.That(second.ClientId).IsNotEqualTo(first.ClientId);
        await Assert.That(await second.List.EnableAsync().AsTask().Timeout(T(1))).IsTrue();
        serverList = ServerList(server);
        await WaitForItemsAsync(second.List, serverList.ToArray());
        await serverList.AddAsync(5).Timeout(T(1));
        await WaitForItemsAsync(second.List, serverList.ToArray());
        await Assert.That(serverList.Last()).IsEqualTo(5);

        // The release gives back the connected client; the client of the stopped server is not kept idle.
        await connector.ReleaseAsync();
        await WaitForIdleAsync(pool, 1);
        await Assert.That((await pool.GetStateAsync()).Rented).IsEqualTo(0);

        await DisposeAndAssertNoLeaksAsync(pool);
    }

    /// <summary>The server's <c>IntListBi</c>.</summary>
    private static INexusList<int> ServerList(InteropServer<ServerNexus, ServerNexus.ClientProxy> server)
    {
        using var owner = server.Server.ContextProvider.Rent();
        return owner.Collections.IntListBi;
    }

    /// <summary>A pool of <paramref name="target"/> clients of <paramref name="server"/>, with the runtime defaults.</summary>
    private Task<IPoolUnderTest> CreatePoolAsync(ClientTarget target, InteropServer<ServerNexus, ServerNexus.ClientProxy> server)
    {
        var spec = ClientSpec.ForEndpoint(server.Endpoints, target.Transport);
        spec.Logger = Logger;
        return Harness.CreatePoolAsync<ClientNexus, ClientNexus.ServerProxy>(target, spec);
    }

    /// <summary>Waits until the pool holds <paramref name="idle"/> idle clients (a return can complete after the release).</summary>
    private static async Task WaitForIdleAsync(IPoolUnderTest pool, int idle)
    {
        var deadline = DateTime.UtcNow.AddSeconds(T(1));
        var state = await pool.GetStateAsync();
        while (state.Idle != idle && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
            state = await pool.GetStateAsync();
        }

        await Assert.That(state.Idle).IsEqualTo(idle).Because(state.ToString());
    }

    /// <summary>Waits until the collection's disabled signal completed.</summary>
    private static async Task WaitForDisabledAsync(IRemoteList<int> list)
    {
        var deadline = DateTime.UtcNow.AddSeconds(T(TestTimeouts.Connect));
        while (!(await list.GetStateAsync().AsTask().Timeout(T(1))).IsDisabled)
        {
            if (DateTime.UtcNow >= deadline)
                Fail.Test("The collection of the client of the stopped server was not disabled.");
            await Task.Delay(10);
        }
    }

    /// <summary>Disposes the pool and asserts it holds nothing.</summary>
    private static async Task DisposeAndAssertNoLeaksAsync(IPoolUnderTest pool)
    {
        await pool.DisposeAsync();
        var report = await pool.EndTestAuditAsync().Timeout(T(5));
        await Assert.That(report.IsClean).IsTrue().Because(report.ToString());
    }
}
