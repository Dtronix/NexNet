using NexNet.Collections.Lists;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.InteropServer;
using RelayServer = NexNet.InteropServer.InteropServer<NexNet.IntegrationTests.TestInterfaces.ServerNexus, NexNet.IntegrationTests.TestInterfaces.ServerNexus.ClientProxy>;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Interop;

/// <summary>
/// Leaf clients of a collection relay (<c>NexusCollectionMode.Relay</c>): a C# relay server mirrors the list of a C#
/// parent server, and clients of every language subscribe to the relayed list. The relay itself is server-side and
/// stays C#-only (<c>NexusCollectionRelayTests</c>); these rows check what a foreign leaf client sees.
/// </summary>
internal partial class CrossLanguageSmokeTests
{
    private const string RelayList = nameof(IServerNexus.IntListRelay);

    /// <summary>
    /// Two leaf clients receive the parent's items as a snapshot, then every change to the parent's list. Mirrors
    /// <c>NexusCollectionRelayTests.ParentItemsSentUponConnectionToRelayCollection</c>,
    /// <c>AddedParentItemsAreRelayedToRelayConnection</c> and <c>MultipleClientsConnectToSameRelay</c>.
    /// </summary>
    [Test]
    [CrossLanguageSmoke(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
    public async Task Relay_LeafClientsReceiveTheParentListAndItsChanges(ClientTarget target)
    {
        await using var relay = await RelayServers.StartAsync();
        var source = relay.Source();
        foreach (var item in new[] { 1, 2, 3 })
            await source.AddAsync(item).Timeout(T(1));
        await relay.RelayReady();

        await using var a = await relay.ConnectLeafAsync(target);
        await using var b = await relay.ConnectLeafAsync(target);
        var listA = a.List<int>(RelayList);
        var listB = b.List<int>(RelayList);
        await Assert.That(await listA.EnableAsync().Timeout(T(2))).IsTrue();
        await Assert.That(await listB.EnableAsync().Timeout(T(2))).IsTrue();
        await WaitForListAsync(listA, 1, 2, 3);
        await WaitForListAsync(listB, 1, 2, 3);

        await source.AddAsync(4).Timeout(T(1));
        await source.RemoveAtAsync(0).Timeout(T(1));
        await source.ReplaceAsync(0, 20).Timeout(T(1));
        await source.MoveAsync(0, 2).Timeout(T(1));
        await WaitForListAsync(listA, 3, 4, 20);
        await WaitForListAsync(listB, 3, 4, 20);

        await source.ClearAsync().Timeout(T(1));
        await WaitForListAsync(listA);
        await WaitForListAsync(listB);
        await EndAsync(a, b);
    }

    /// <summary>
    /// A leaf client that subscribes while the relay cannot reach its parent gets the parent's items once the parent
    /// is up. Mirrors <c>NexusCollectionRelayTests.ClientConnectsToRelayBeforeRelayConnectsToParent</c>.
    /// </summary>
    [Test]
    [CrossLanguageSmoke(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
    public async Task Relay_LeafClientSubscribedBeforeTheRelayReachesItsParent_ReceivesTheItems(ClientTarget target)
    {
        await using var relay = await RelayServers.StartAsync(parentRunning: false);

        await using var leaf = await relay.ConnectLeafAsync(target);
        var list = leaf.List<int>(RelayList);
        await list.EnableAsync().Timeout(T(2));
        await Assert.That((await list.SnapshotAsync().AsTask().Timeout(T(1))).Count).IsEqualTo(0);

        await relay.Parent.StartAsync().Timeout(T(TestTimeouts.Connect));
        await relay.RelayReady();
        var source = relay.Source();
        for (var i = 0; i < 5; i++)
            await source.AddAsync(i).Timeout(T(1));

        await WaitForListAsync(list, 0, 1, 2, 3, 4);
        await EndAsync(leaf);
    }

    /// <summary>
    /// A relayed list is read-only for its leaf clients: every change a leaf client asks for is refused on the client
    /// with the same error kind on every language (InvalidOperation), nothing reaches the relay, and the leaf keeps
    /// mirroring the parent. Mirrors &lt;c&gt;NexusCollectionRelayTests.RelayPreventsModifications&lt;/c&gt;, which checks the
    /// relay's own list. The refusal comes from the list's mode alone: it is the same for a missing item and for a
    /// list that is not enabled.
    /// </summary>
    [Test]
    [CrossLanguageSmoke(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
    public async Task Relay_LeafClientWritesAreRefused(ClientTarget target)
    {
        await using var relay = await RelayServers.StartAsync();
        var source = relay.Source();
        foreach (var item in new[] { 1, 2, 3 })
            await source.AddAsync(item).Timeout(T(1));
        await relay.RelayReady();

        await using var leaf = await relay.ConnectLeafAsync(target);
        var list = leaf.List<int>(RelayList);
        await Assert.That(await list.EnableAsync().Timeout(T(2))).IsTrue();
        await WaitForListAsync(list, 1, 2, 3);

        // Every operation targets an item or index that exists, so each one would reach the wire if it were allowed.
        await AssertRefusedAsync(() => list.AddAsync(99));
        await AssertRefusedAsync(() => list.InsertAsync(0, 99));
        await AssertRefusedAsync(() => list.RemoveAsync(2));
        await AssertRefusedAsync(() => list.RemoveAtAsync(0));
        await AssertRefusedAsync(() => list.ReplaceAsync(0, 99));
        await AssertRefusedAsync(() => list.MoveAsync(0, 2));
        await AssertRefusedAsync(() => list.ClearAsync());

        // A missing item is refused too, rather than answered with false.
        await AssertRefusedAsync(() => list.RemoveAsync(99));

        // A list that is not enabled refuses every change the same way.
        await using var idle = await relay.ConnectLeafAsync(target);
        var idleList = idle.List<int>(RelayList);
        await AssertRefusedAsync(() => idleList.AddAsync(99));
        await AssertRefusedAsync(() => idleList.InsertAsync(0, 99));
        await AssertRefusedAsync(() => idleList.RemoveAsync(1));
        await AssertRefusedAsync(() => idleList.RemoveAtAsync(0));
        await AssertRefusedAsync(() => idleList.ReplaceAsync(0, 99));
        await AssertRefusedAsync(() => idleList.MoveAsync(0, 1));
        await AssertRefusedAsync(() => idleList.ClearAsync());

        // Nothing changed, and the leaf's subscription is intact: it still receives the parent's changes.
        await source.AddAsync(4).Timeout(T(1));
        await WaitForListAsync(list, 1, 2, 3, 4);
        await Assert.That(source.ToArray()).IsEquivalentTo(new[] { 1, 2, 3, 4 }, CollectionOrdering.Matching);
        await EndAsync(leaf, idle);

        static async Task AssertRefusedAsync(Func<ValueTask<bool>> write)
            => await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await write().Timeout(T(1)));
    }

    /// <summary>Waits until the leaf client's list holds exactly <paramref name="expected"/>, in order.</summary>
    private static async Task WaitForListAsync(IRemoteList<int> list, params int[] expected)
        => await list.WaitForAsync(items => items.SequenceEqual(expected), TimeSpan.FromSeconds(T(TestTimeouts.CollectionEvent)));

    /// <summary>
    /// A parent server, a C# client pool connected to it, and a relay server whose <c>IntListRelay</c> mirrors the
    /// parent's <c>IntListBi</c> through the pool.
    /// </summary>
    private sealed class RelayServers : IAsyncDisposable
    {
        private readonly NexusClientPool<ClientNexus, ClientNexus.ServerProxy> _pool;

        private RelayServers(RelayServer parent, NexusClientPool<ClientNexus, ClientNexus.ServerProxy> pool, RelayServer relay)
        {
            Parent = parent;
            _pool = pool;
            Relay = relay;
        }

        /// <summary>The parent server; its <c>IntListBi</c> is the source.</summary>
        public RelayServer Parent { get; }

        /// <summary>The relay server the leaf clients connect to.</summary>
        public RelayServer Relay { get; }

        /// <summary>
        /// Starts the servers. With <paramref name="parentRunning"/> false the parent is stopped (after its endpoints
        /// were assigned) before the relay starts, so the relay keeps retrying until the test starts it.
        /// </summary>
        public static async Task<RelayServers> StartAsync(bool parentRunning = true)
        {
            var parent = await InteropServerHost.StartAsync<ServerNexus, ServerNexus.ClientProxy>(() => new ServerNexus())
                .Timeout(T(TestTimeouts.Connect));
            NexusClientPool<ClientNexus, ClientNexus.ServerProxy>? pool = null;
            try
            {
                if (!parentRunning)
                    await parent.StopAsync().Timeout(T(TestTimeouts.Connect));

                pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(
                    new NexusClientPoolConfig(parent.CreateClientConfig(InteropEndpointKind.Tcp)));
                var connector = pool.GetCollectionConnector(proxy => proxy.IntListBi);
                var relay = await InteropServerHost.StartAsync<ServerNexus, ServerNexus.ClientProxy>(
                    () => new ServerNexus(),
                    configureCollections: nexus => nexus.IntListRelay.ConfigureRelay(connector)).Timeout(T(TestTimeouts.Connect));
                return new RelayServers(parent, pool, relay);
            }
            catch
            {
                if (pool != null)
                    await pool.DisposeAsync();
                await parent.DisposeAsync();
                throw;
            }
        }

        /// <summary>The parent's source list.</summary>
        public INexusList<int> Source()
        {
            using var owner = Parent.Server.ContextProvider.Rent();
            return owner.Collections.IntListBi;
        }

        /// <summary>Waits until the relay is connected to the parent's list.</summary>
        public async Task RelayReady()
        {
            Task ready;
            using (var owner = Relay.Server.ContextProvider.Rent())
                ready = owner.Collections.IntListRelay.ReadyTask;
            await ready.Timeout(T(2));
        }

        /// <summary>Creates and connects a leaf client of <paramref name="target"/> to the relay.</summary>
        public async Task<IClientUnderTest> ConnectLeafAsync(ClientTarget target)
        {
            var client = await Harness.CreateClientAsync<ClientNexus, ClientNexus.ServerProxy>(
                target, ClientSpec.ForEndpoint(Relay.Endpoints, target.Transport));
            try
            {
                await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
                return client;
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Relay.DisposeAsync();
            }
            finally
            {
                try
                {
                    await _pool.DisposeAsync();
                }
                finally
                {
                    await Parent.DisposeAsync();
                }
            }
        }
    }
}
