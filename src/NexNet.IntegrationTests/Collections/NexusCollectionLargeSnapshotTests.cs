using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.InteropServer;
using RelayServer = NexNet.InteropServer.InteropServer<NexNet.IntegrationTests.TestInterfaces.ServerNexus, NexNet.IntegrationTests.TestInterfaces.ServerNexus.ClientProxy>;

namespace NexNet.IntegrationTests.Collections;

/// <summary>
/// The server writes a new client's snapshot with backpressure: a large list reaches a slow client whole, and a
/// client that falls too far behind the live changes is closed instead of silently losing items (F26).
/// </summary>
[NotInParallel("stress")]
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class NexusCollectionLargeSnapshotTests(LanguageHostPool hosts) : NexusCollectionBaseTests
{
    /// <summary>The language hosts of the session; their hello decides which rows are generated.</summary>
    public LanguageHostPool Hosts { get; } = hosts;

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task LargeSnapshot_SlowClient_ReceivesEveryItem(ClientTarget target)
    {
        const int count = 100_000;
        var (spec, nexuses) = await StartMatrixServerAsync(target);
        await using var client = await ConnectMatrixClientAsync(target, spec);
        var serverList = (NexusListServer<int>)nexuses.First().IntListBi;
        serverList.TestResetTo(Enumerable.Range(0, count));

        var list = IntListBi(client);

        // Throttle the client: it reads its snapshot far slower than the server can write it.
        await list.ThrottleAsync(SlowReader).AsTask().Timeout(T(1));
        try
        {
            await Assert.That(await list.EnableAsync().AsTask().Timeout(T(30))).IsTrue();
        }
        finally
        {
            await list.ThrottleAsync(null).AsTask().Timeout(T(1));
        }

        await AssertConnectedAsync(list);
        var items = await list.SnapshotAsync().AsTask().Timeout(T(10));
        await Assert.That(items.SequenceEqual(Enumerable.Range(0, count))).IsTrue();

        // Changes after the snapshot still arrive.
        await Assert.That(await list.AddAsync(-1).AsTask().Timeout(T(5))).IsTrue();
        await list.WaitForAsync(l => l.Count == count + 1 && l[count] == -1, TimeSpan.FromSeconds(T(TestTimeouts.CollectionEvent)));

        await list.DisableAsync().AsTask().Timeout(T(5));
        await MatrixTestBase.EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task LargeRelayedSnapshot_SlowClient_ReceivesEveryItem(ClientTarget target)
    {
        // A relay serves its clients the snapshot with the same backpressure as a server list (F27).
        const int count = 100_000;
        target.SkipIfUnsupported();
        await using var relay = await RelayServers.StartAsync(count);
        var sourceList = relay.Source();
        var serverRelay = relay.RelayList();
        await serverRelay.ReadyTask.Timeout(T(30));
        await Assert.That(serverRelay.Count).IsEqualTo(count);

        await using var client = await relay.ConnectLeafAsync(target);
        var list = client.List<int>(nameof(IServerNexus.IntListRelay));

        // Throttle the client: it reads its snapshot far slower than the relay can write it.
        await list.ThrottleAsync(SlowReader).AsTask().Timeout(T(1));
        try
        {
            await Assert.That(await list.EnableAsync().AsTask().Timeout(T(30))).IsTrue();
        }
        finally
        {
            await list.ThrottleAsync(null).AsTask().Timeout(T(1));
        }

        await AssertConnectedAsync(list);
        var items = await list.SnapshotAsync().AsTask().Timeout(T(10));
        await Assert.That(items.SequenceEqual(Enumerable.Range(0, count))).IsTrue();

        // Changes relayed after the snapshot still arrive.
        await sourceList.AddAsync(-1).Timeout(T(5));
        await list.WaitForAsync(l => l.Count == count + 1 && l[count] == -1, TimeSpan.FromSeconds(T(TestTimeouts.CollectionEvent)));

        await list.DisableAsync().AsTask().Timeout(T(5));
        await MatrixTestBase.EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task LargeRelayResync_SlowClient_ReceivesEveryItem(ClientTarget target)
    {
        // A relay resync resets the clients already connected with the same backpressure as a new client's
        // snapshot, so a large list reaches a slow client whole (F32).
        const int count = 100_000;
        target.SkipIfUnsupported();
        await using var relay = await RelayServers.StartAsync(count);
        var sourceList = relay.Source();
        await relay.RelayList().ReadyTask.Timeout(T(30));

        await using var client = await relay.ConnectLeafAsync(target);
        var list = client.List<int>(nameof(IServerNexus.IntListRelay));
        await Assert.That(await list.EnableAsync().AsTask().Timeout(T(30))).IsTrue();
        await list.WaitForAsync(l => l.Count == count, TimeSpan.FromSeconds(T(TestTimeouts.CollectionEvent)));

        // Throttle the client: it stalls at the start of the resync, then reads it far slower than the relay can
        // write it. Without backpressure the relay would overflow the client's buffer and close its pipe.
        await list.ThrottleAsync(SlowReader with { FirstDelayMs = 500 }).AsTask().Timeout(T(1));
        int[] expected;
        try
        {
            // A move in the parent resyncs the relay, which resets its connected clients with the whole list. The
            // change after it is applied on top of the reset.
            await Assert.That(await sourceList.MoveAsync(0, count - 1).Timeout(T(5))).IsTrue();
            await Assert.That(await sourceList.AddAsync(-1).Timeout(T(5))).IsTrue();
            expected = Enumerable.Range(1, count - 1).Append(0).Append(-1).ToArray();

            try
            {
                await list.WaitForAsync(l => l.Count == count + 1 && l[count] == -1 && l[count - 1] == 0, TimeSpan.FromSeconds(T(60)));
            }
            catch (TimeoutException)
            {
                // A client the relay disconnected from the collection during the resync never gets there.
                var state = await list.GetStateAsync().AsTask().Timeout(T(1));
                await Assert.That(state.IsDisabled).IsFalse().Because("the relay client was disconnected from the collection during the resync");
                throw;
            }
        }
        finally
        {
            await list.ThrottleAsync(null).AsTask().Timeout(T(1));
        }

        await AssertConnectedAsync(list);
        var items = await list.SnapshotAsync().AsTask().Timeout(T(10));
        await Assert.That(items.SequenceEqual(expected)).IsTrue();

        await list.DisableAsync().AsTask().Timeout(T(5));
        await MatrixTestBase.EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket])]
    public async Task VeryLargeSnapshot_ReceivesEveryItem(ClientTarget target)
    {
        const int count = 1_000_000;
        var (spec, nexuses) = await StartMatrixServerAsync(target);
        await using var client = await ConnectMatrixClientAsync(target, spec);
        var serverList = (NexusListServer<int>)nexuses.First().IntListBi;
        serverList.TestResetTo(Enumerable.Range(0, count));

        var list = IntListBi(client);
        await Assert.That(await list.EnableAsync().AsTask().Timeout(T(60))).IsTrue();

        var items = await list.WaitForAsync(l => l.Count == count, TimeSpan.FromSeconds(T(60)));
        await Assert.That(items.SequenceEqual(Enumerable.Range(0, count))).IsTrue();

        await list.DisableAsync().AsTask().Timeout(T(5));
        await MatrixTestBase.EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task ClientFallsBehindDuringSnapshot_PipeClosesAndReEnableSyncs(ClientTarget target)
    {
        const int count = 100_000;
        var (spec, nexuses) = await StartMatrixServerAsync(target);
        // The client may hold up to two receive windows of the snapshot while it is stalled: the window itself, plus
        // the batch its channel reader decoded before the stall. Keep both far below the snapshot (about 400 KB), so
        // the server can never finish writing it and every change waits in the client's buffer until it overflows.
        // With the default window (256 KB) a late first read could take in the whole snapshot, then the changes
        // drained into the free window and the client never fell behind.
        spec.PipeWindow = 64 * 1024;
        await using var client = await ConnectMatrixClientAsync(target, spec);
        var serverList = (NexusListServer<int>)nexuses.First().IntListBi;
        serverList.TestResetTo(Enumerable.Range(0, count));

        var list = IntListBi(client);

        // Stall the client at its first message until the server has made more changes than a client may lag.
        await list.ThrottleAsync(new RemoteListThrottle { Pause = true }).AsTask().Timeout(T(1));
        Task<bool> enable;
        try
        {
            enable = list.EnableAsync().AsTask();
            await WaitUntilAsync(async () => (await list.GetThrottleStateAsync().AsTask().Timeout(T(1))).Holding,
                T(5), "the client to stall at its first collection message");

            for (var i = 0; i < 1500; i++)
                await serverList.AddAsync(count + i).Timeout(T(5));
        }
        finally
        {
            // Releases the held message.
            await list.ThrottleAsync(null).AsTask().Timeout(T(1));
        }

        // The server closed the lagging client's pipe: the collection is disabled, never left with a partial list.
        await WaitUntilAsync(async () => (await list.GetStateAsync().AsTask().Timeout(T(1))).IsDisabled,
            T(10), "the collection to be disabled");
        try
        {
            // The enable ends without a synchronization: false, or the ready failure (the C# enable completes once
            // the pipe opens, then waits for the initial state).
            await Assert.That(await enable.Timeout(T(10))).IsFalse();
        }
        catch (InvalidOperationException)
        {
        }

        await Assert.That((await list.GetStateAsync().AsTask().Timeout(T(1))).State).IsEqualTo(NexusCollectionState.Disconnected);

        // The session stays up, and a new enable gets the whole list.
        await Assert.That(await list.EnableAsync().AsTask().Timeout(T(30))).IsTrue();
        var items = await list.SnapshotAsync().AsTask().Timeout(T(10));
        using (Assert.Multiple())
        {
            await Assert.That(items.Count).IsEqualTo(count + 1500);
            await Assert.That(items.SequenceEqual(serverList)).IsTrue();
        }

        await list.DisableAsync().AsTask().Timeout(T(5));
        await MatrixTestBase.EndAsync(client);
    }

    /// <summary>A client that reads its collection far slower than the server can write it.</summary>
    private static readonly RemoteListThrottle SlowReader = new() { Every = 25, DelayMs = 1 };

    /// <summary>Asserts that the client's list is connected and enabled.</summary>
    private static async Task AssertConnectedAsync(IRemoteList<int> list)
    {
        var state = await list.GetStateAsync().AsTask().Timeout(T(1));
        using (Assert.Multiple())
        {
            await Assert.That(state.State).IsEqualTo(NexusCollectionState.Connected);
            await Assert.That(state.IsDisabled).IsFalse();
        }
    }

    /// <summary>Polls <paramref name="condition"/> until it holds; fails after <paramref name="seconds"/>.</summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> condition, double seconds, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(10);
        }
    }

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
        /// Starts the parent, resets its source list to <paramref name="count"/> items (0 to count - 1) before the relay
        /// connects, then starts the relay.
        /// </summary>
        public static async Task<RelayServers> StartAsync(int count)
        {
            var parent = await InteropServerHost.StartAsync<ServerNexus, ServerNexus.ClientProxy>(() => new ServerNexus())
                .Timeout(T(TestTimeouts.Connect));
            NexusClientPool<ClientNexus, ClientNexus.ServerProxy>? pool = null;
            try
            {
                using (var owner = parent.Server.ContextProvider.Rent())
                    ((NexusListServer<int>)owner.Collections.IntListBi).TestResetTo(Enumerable.Range(0, count));

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
        public NexusListServer<int> Source()
        {
            using var owner = Parent.Server.ContextProvider.Rent();
            return (NexusListServer<int>)owner.Collections.IntListBi;
        }

        /// <summary>The relay server's relayed list.</summary>
        public INexusList<int> RelayList()
        {
            using var owner = Relay.Server.ContextProvider.Rent();
            return owner.Collections.IntListRelay;
        }

        /// <summary>Creates and connects a leaf client of <paramref name="target"/> to the relay.</summary>
        public Task<IClientUnderTest> ConnectLeafAsync(ClientTarget target)
            => MatrixTestBase.ConnectAsync<ClientNexus, ClientNexus.ServerProxy>(target, ClientSpec.ForEndpoint(Relay.Endpoints, target.Transport));

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
