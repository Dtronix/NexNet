using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.InteropServer;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NexNet.TestNexus.Interop;
using NexNet.Transports;
using TestServer = NexNet.InteropServer.InteropServer<NexNet.TestNexus.Interop.InteropServerNexus, NexNet.TestNexus.Interop.InteropServerNexus.ClientProxy>;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Tests for the client pool: connection pooling, health checking, and rent/return operations, on every row of the
/// client matrix (the C# pool in process, and the pools of the language hosts of <c>NEXNET_TEST_LANGS</c>) through
/// <see cref="Harness.CreatePoolAsync(ClientTarget, ClientSpec, ClientPoolSpec?)"/>. The server is the C# interop server
/// (<see cref="InteropServerNexus"/>), one per test, as most tests stop and restart it.
/// </summary>
/// <remarks>
/// <para>
/// Reuse of a pooled client is checked with <see cref="IRentedClientUnderTest.ClientId"/>, not proxy identity. The
/// tests use UDS (the previous C# coverage) and WebSocket (every language, including the browser).
/// </para>
/// <para>
/// These stay on the C# pool (C#-runtime API, listed drops of P01g): argument validation of the
/// <see cref="NexusClientPool{TClientNexus, TServerProxy}"/> constructor (zero or negative maximum, negative minimum
/// idle, null config), the nexus-factory tests (custom factory throwing, returning null, call count, default factory),
/// and the thread safety of the synchronous pool properties.
/// </para>
/// </remarks>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class NexusClientPoolTests(LanguageHostPool hosts) : BaseTests
{
    /// <summary>The language hosts of the session; their hello decides which rows are skipped.</summary>
    public LanguageHostPool Hosts { get; } = hosts;

    /// <summary>A server with its own host.</summary>
    private static Task<TestServer> StartServer(InteropServerOptions? options = null)
        => InteropServerHost.StartAsync<InteropServerNexus, InteropServerNexus.ClientProxy>(() => new InteropServerNexus(), options);

    /// <summary>Creates a pool of <paramref name="target"/> clients for <paramref name="endpoints"/>; it holds no client yet.</summary>
    private static Task<IPoolUnderTest> CreatePool(ClientTarget target, InteropEndpoints endpoints, ClientPoolSpec pool, Action<ClientSpec>? configure = null)
    {
        var spec = ClientSpec.ForEndpoint(endpoints, target.Transport);
        configure?.Invoke(spec);
        return Harness.CreatePoolAsync(target, spec, pool);
    }

    /// <summary>
    /// A pool whose clients' address has nothing listening: the endpoints of a server that was started and then disposed
    /// with its host.
    /// </summary>
    private static async Task<IPoolUnderTest> CreatePoolWithNoServer(ClientTarget target, ClientPoolSpec pool)
    {
        target.SkipIfUnsupported();
        InteropEndpoints endpoints;
        await using (var server = await StartServer())
            endpoints = server.Endpoints;

        return await CreatePool(target, endpoints, pool);
    }

    private static Task<IRentedClientUnderTest> Rent(IPoolUnderTest pool) => pool.RentAsync().Timeout(T(TestTimeouts.Connect));

    private static IInteropServerNexus Proxy(IClientUnderTest client) => client.Proxy<IInteropServerNexus>();

    private static async Task AssertState(IPoolUnderTest pool, int max, int idle, int rented)
        => await Assert.That(await pool.GetStateAsync()).IsEqualTo(new ClientPoolState(max, idle, rented));

    private static async Task<int> Idle(IPoolUnderTest pool) => (await pool.GetStateAsync()).Idle;

    /// <summary>Disposes the pool and asserts it holds nothing: every rental must be returned first.</summary>
    private static async Task DisposeAndAssertNoLeaks(IPoolUnderTest pool)
    {
        await pool.DisposeAsync();
        var report = await pool.EndTestAuditAsync().Timeout(T(5));
        await Assert.That(report.IsClean).IsTrue().Because(report.ToString());
    }

    /// <summary>Takes the server-side notifications of the client's session until <paramref name="count"/> arrived.</summary>
    private static async Task<List<string>> TakeNotifications(IClientUnderTest client, int count)
    {
        var taken = new List<string>();
        var deadline = DateTime.UtcNow.AddSeconds(T(1));
        while (taken.Count < count && DateTime.UtcNow < deadline)
        {
            taken.AddRange(await Proxy(client).TakeNotifications().Timeout(T(1)));
            if (taken.Count < count)
                await Task.Delay(10);
        }

        return taken;
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task RentClientAsync_CreatesAndConnectsNewClient(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(target, server.Endpoints, new ClientPoolSpec { MaxConnections = 5 });

        await using (var rentedClient = await Rent(pool))
        {
            await Assert.That(rentedClient).IsNotNull();
            await rentedClient.WaitForStateAsync(ConnectionState.Connected, T(1));
            await Assert.That(await Proxy(rentedClient).EchoString("hi").Timeout(T(1))).IsEqualTo("hi");
            await AssertState(pool, 5, 0, 1); // Client is rented out
        }

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task RentClientAsync_ReusesReturnedClients(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(target, server.Endpoints, new ClientPoolSpec { MaxConnections = 5 });

        // First rental
        string firstId;
        await using (var firstClient = await Rent(pool))
        {
            firstId = firstClient.ClientId;
            Proxy(firstClient).Notify("first");
            await Assert.That(await TakeNotifications(firstClient, 1)).IsEquivalentTo(new[] { "first" });
        }

        await AssertState(pool, 5, 1, 0); // Client should be returned

        // Second rental reuses the same client, and its session.
        await using (var secondClient = await Rent(pool))
        {
            await Assert.That(secondClient.ClientId).IsEqualTo(firstId);
            Proxy(secondClient).Notify("second");
            await Assert.That(await TakeNotifications(secondClient, 1)).IsEquivalentTo(new[] { "second" });
            await AssertState(pool, 5, 0, 1); // Client is rented out again
        }

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task RentClientAsync_HandlesMaxConnectionsLimit(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(target, server.Endpoints, new ClientPoolSpec { MaxConnections = 2 });

        // Rent maximum connections
        var client1 = await Rent(pool);
        var client2 = await Rent(pool);
        await Proxy(client1).Ping().Timeout(T(1));
        await AssertState(pool, 2, 0, 2);

        // A third rent waits for a return
        var rentTask = pool.RentAsync();
        await Task.Delay(100);
        await Assert.That(rentTask.IsCompleted).IsFalse();

        // Return one client: the third rent gets it.
        await client1.ReturnAsync();
        var client3 = await rentTask.Timeout(T(TestTimeouts.Connect));
        await Assert.That(client3.ClientId).IsEqualTo(client1.ClientId);

        await client3.ReturnAsync();
        await client2.ReturnAsync();
        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task EnsureConnectedAsync_ReconnectsWhenNeeded(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(
            target,
            server.Endpoints,
            new ClientPoolSpec { MaxConnections = 5 },
            spec => spec.ReconnectDelaysMs = [100, 200, 400]);

        await using (var rentedClient = await Rent(pool))
        {
            await rentedClient.WaitForStateAsync(ConnectionState.Connected, T(1));

            // Disconnect by stopping the server
            await server.StopAsync().Timeout(T(TestTimeouts.Connect));
            await rentedClient.WhenDisconnectedAsync(T(TestTimeouts.Connect));

            // Start the server again
            await server.StartAsync().Timeout(T(TestTimeouts.Connect));

            var reconnected = await rentedClient.EnsureConnectedAsync().Timeout(T(TestTimeouts.Connect));

            await Assert.That(reconnected).IsTrue();
            await rentedClient.WaitForStateAsync(ConnectionState.Connected, T(1));
            await Assert.That(await Proxy(rentedClient).EchoString("back").Timeout(T(1))).IsEqualTo("back");
        }

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task Dispose_DisposesAllClients(ClientTarget target)
    {
        await using var server = await StartServer();
        var pool = await CreatePool(target, server.Endpoints, new ClientPoolSpec { MaxConnections = 3 });

        // Rent and return some clients to populate the pool
        var client1 = await Rent(pool);
        var client2 = await Rent(pool);
        await client1.ReturnAsync();
        await client2.ReturnAsync();

        await AssertState(pool, 3, 2, 0);

        // Disposing the pool disposes its idle clients: the audit finds no open client and no health check.
        await DisposeAndAssertNoLeaks(pool);

        // Should throw when trying to rent after disposal
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await pool.RentAsync());
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task Dispose_ReturningRentalAfterPoolDisposed_DoesNotThrow(ClientTarget target)
    {
        await using var server = await StartServer();
        var pool = await CreatePool(target, server.Endpoints, new ClientPoolSpec { MaxConnections = 2 });

        var rental = await Rent(pool);

        await pool.DisposeAsync();

        // A rental is not disposed with its pool.
        await Assert.That(await rental.GetStateAsync()).IsEqualTo(ConnectionState.Connected);
        await Assert.That(await Proxy(rental).EchoInt32(7)).IsEqualTo(7);

        // Returning a rental after the pool is disposed disposes its client and must not throw.
        await rental.ReturnAsync().Timeout(T(1));
        await rental.ReturnAsync().Timeout(T(1));

        // The audit finds the returned client disconnected and no health check.
        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task RentClientAsync_ThrowsOnConnectionFailure(ClientTarget target)
    {
        await using var pool = await CreatePoolWithNoServer(target, new ClientPoolSpec { MaxConnections = 5 });

        var error = await Assert.ThrowsExactlyAsync<ClientPoolConnectionException>(async () => await Rent(pool));
        await Assert.That(error!.ConnectionResult.Success).IsFalse();

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task Pool_HandlesMultipleConcurrentRentals(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(target, server.Endpoints, new ClientPoolSpec { MaxConnections = 10 });

        // Multiple concurrent rentals and operations
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            await using var client = await Rent(pool);
            var echoed = await Proxy(client).EchoInt32(i);
            await Task.Delay(10); // Small delay to simulate work
            return echoed;
        })).Timeout(T(5));

        await Assert.That(results).IsEquivalentTo(Enumerable.Range(0, 20));

        // Rented clients go back to the pool; it never holds more than its maximum.
        var state = await pool.GetStateAsync();
        await Assert.That(state.Rented).IsEqualTo(0);
        await Assert.That(state.Idle).IsLessThanOrEqualTo(10);

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task Pool_PropertiesReflectCurrentState(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(target, server.Endpoints, new ClientPoolSpec { MaxConnections = 3 });

        await AssertState(pool, 3, 0, 0);

        var client1 = await Rent(pool);
        var client2 = await Rent(pool);
        await AssertState(pool, 3, 0, 2); // Both rented

        await client1.ReturnAsync();
        await AssertState(pool, 3, 1, 1); // One returned

        await client2.ReturnAsync();
        await AssertState(pool, 3, 2, 0); // Both returned

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool | ClientCapabilities.FakeTime)]
    public async Task Pool_FakeTimeProvider_DisposesIdleClientsDeterministically(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(
            target,
            server.Endpoints,
            new ClientPoolSpec { MaxConnections = 5, MaxIdleTimeMs = 2000, MinIdleConnections = 0 },
            spec => spec.FakeTime = true);

        var client1 = await Rent(pool);
        var client2 = await Rent(pool);
        await client1.ReturnAsync();
        await client2.ReturnAsync();

        await AssertState(pool, 5, 2, 0);

        // Advance past MaxIdleTime + health-check interval (MaxIdleTime/4 = 500ms); timers due fire before it completes.
        await pool.AdvanceTimeAsync(TimeSpan.FromSeconds(3));

        await Assert.That(await Idle(pool)).IsEqualTo(0).Because("MinIdleConnections is 0, so all idle clients should be evicted once their idle time exceeds MaxIdleTime.");

        await DisposeAndAssertNoLeaks(pool);
    }

    // Idle Timeout Tests
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool | ClientCapabilities.FakeTime)]
    public async Task Pool_DisposesIdleClientsAfterTimeout(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(
            target,
            server.Endpoints,
            new ClientPoolSpec { MaxConnections = 5, MaxIdleTimeMs = 100 }, // Very short idle time
            spec => spec.FakeTime = true);

        // Create and return multiple clients
        var client1 = await Rent(pool);
        var client2 = await Rent(pool);
        var client3 = await Rent(pool);
        await client1.ReturnAsync();
        await client2.ReturnAsync();
        await client3.ReturnAsync();

        await AssertState(pool, 5, 3, 0);

        // Drive idle timeout deterministically.
        await pool.AdvanceTimeAsync(TimeSpan.FromMilliseconds(500));

        // Default MinIdleConnections (1) keeps one client; others evicted.
        await Assert.That(await Idle(pool)).IsEqualTo(ClientPoolSpec.DefaultMinIdleConnections);

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool | ClientCapabilities.FakeTime)]
    public async Task Pool_KeepsAtLeastOneClientDespiteIdleTimeout(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(
            target,
            server.Endpoints,
            new ClientPoolSpec { MaxConnections = 1, MaxIdleTimeMs = 50, MinIdleConnections = 1 },
            spec => spec.FakeTime = true);

        // Create and return one client
        await (await Rent(pool)).ReturnAsync();

        await AssertState(pool, 1, 1, 0);

        await pool.AdvanceTimeAsync(TimeSpan.FromMilliseconds(200));

        await Assert.That(await Idle(pool)).IsEqualTo(1).Because("MinIdleConnections=1 must retain the single pooled client past MaxIdleTime.");

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool | ClientCapabilities.FakeTime)]
    public async Task Pool_RespectsMinIdleConnections(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(
            target,
            server.Endpoints,
            new ClientPoolSpec { MaxConnections = 10, MaxIdleTimeMs = 100, MinIdleConnections = 3 },
            spec => spec.FakeTime = true);

        // Create and return multiple clients
        var clients = new List<IRentedClientUnderTest>();
        for (int i = 0; i < 5; i++)
            clients.Add(await Rent(pool));

        foreach (var client in clients)
            await client.ReturnAsync();

        await AssertState(pool, 10, 5, 0);

        await pool.AdvanceTimeAsync(TimeSpan.FromMilliseconds(200));

        await Assert.That(await Idle(pool)).IsEqualTo(3).Because("MinIdleConnections=3 must keep exactly the floor count; surplus idle clients are evicted.");

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool | ClientCapabilities.FakeTime)]
    public async Task Pool_MinIdleConnectionsDoesNotCreateNewClients(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(
            target,
            server.Endpoints,
            new ClientPoolSpec
            {
                MaxConnections = 10,
                MaxIdleTimeMs = 100, // Health check every 100 ms; MinIdleConnections keeps every idle client
                MinIdleConnections = 5, // Higher than what we'll create
            },
            spec => spec.FakeTime = true);

        // Create and return fewer clients than MinIdleConnections
        var clients = new List<IRentedClientUnderTest>();
        for (int i = 0; i < 2; i++)
            clients.Add(await Rent(pool));

        foreach (var client in clients)
            await client.ReturnAsync();

        await AssertState(pool, 10, 2, 0);

        // Advance time to trigger several health-check cycles; no pre-warming should occur.
        await pool.AdvanceTimeAsync(TimeSpan.FromMilliseconds(500));

        await Assert.That(await Idle(pool)).IsEqualTo(2).Because("Health check must not pre-warm new clients to reach MinIdleConnections.");

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool | ClientCapabilities.FakeTime)]
    public async Task Pool_MinIdleConnectionsWithZeroValue(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(
            target,
            server.Endpoints,
            new ClientPoolSpec { MaxConnections = 5, MaxIdleTimeMs = 100, MinIdleConnections = 0 }, // Allow all idle clients to be disposed
            spec => spec.FakeTime = true);

        // Create and return multiple clients
        var clients = new List<IRentedClientUnderTest>();
        for (int i = 0; i < 3; i++)
            clients.Add(await Rent(pool));

        foreach (var client in clients)
            await client.ReturnAsync();

        await AssertState(pool, 5, 3, 0);

        await pool.AdvanceTimeAsync(TimeSpan.FromMilliseconds(500));

        await Assert.That(await Idle(pool)).IsEqualTo(0).Because("MinIdleConnections=0 must allow every idle client to be evicted.");

        await DisposeAndAssertNoLeaks(pool);
    }

    // Configuration Validation Tests (C# pool constructor; see the class remarks)
    [Test]
    public async Task Pool_ThrowsOnZeroMaxConnections()
    {
        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 0 };

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => 
            new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig));
    }

    [Test]
    public async Task Pool_ThrowsOnNegativeMaxConnections()
    {
        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = -1 };

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => 
            new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig));
    }

    [Test]
    public async Task Pool_ThrowsOnNullConfig()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => 
            new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(null!));
    }

    [Test]
    public async Task Pool_ThrowsOnNegativeMinIdleConnections()
    {
        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MinIdleConnections = -1 };

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => 
            new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig));
    }

    // Error Handling Tests
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task Pool_ReleaseSemaphoreOnConnectionException(ClientTarget target)
    {
        // No server listening, to force connection failure
        await using var pool = await CreatePoolWithNoServer(target, new ClientPoolSpec { MaxConnections = 1 });

        // Try to rent when connection will fail
        await Assert.ThrowsExactlyAsync<ClientPoolConnectionException>(async () => await Rent(pool));

        // The slot is released, allowing another attempt (it would wait forever otherwise)
        await Assert.ThrowsExactlyAsync<ClientPoolConnectionException>(async () => await Rent(pool));
        await AssertState(pool, 1, 0, 0);

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task Pool_HandlesSafeMultipleDispose(ClientTarget target)
    {
        await using var server = await StartServer();
        var pool = await CreatePool(target, server.Endpoints, new ClientPoolSpec { MaxConnections = 1 });

        // Dispose multiple times
        await pool.DisposeAsync();
        await pool.DisposeAsync(); // Should not throw
        await pool.DisposeAsync(); // Should not throw

        // Operations should throw ObjectDisposedException
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await pool.RentAsync());

        var report = await pool.EndTestAuditAsync().Timeout(T(5));
        await Assert.That(report.IsClean).IsTrue().Because(report.ToString());
    }

    [Test]
    public async Task Pool_HandlesCustomNexusFactoryExceptions()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 1 };
        
        var factoryCallCount = 0;
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(
            poolConfig,
            nexusFactory: () =>
            {
                factoryCallCount++;
                if (factoryCallCount == 1)
                    throw new InvalidOperationException("Factory error");
                return new ClientNexus();
            });

        try
        {
            // First call should throw due to factory exception
            await AssertThrows<InvalidOperationException>(async () =>
                await pool.RentClientAsync().Timeout(T(1)));

            // Second call should succeed
            using var client = await pool.RentClientAsync().Timeout(T(1));
            await Assert.That(client).IsNotNull();
            await Assert.That(factoryCallCount).IsEqualTo(2);
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    // State Transition Tests
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task Pool_HandlesClientStateChanges(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(target, server.Endpoints, new ClientPoolSpec { MaxConnections = 1 });

        // Rent and return a client
        string firstId;
        await using (var client = await Rent(pool))
        {
            firstId = client.ClientId;
            await client.WaitForStateAsync(ConnectionState.Connected, T(1));
        }

        await AssertState(pool, 1, 1, 0);

        // Stop server to make the idle client unhealthy
        await server.StopAsync().Timeout(T(TestTimeouts.Connect));

        // Rent again: the unhealthy idle client is disposed on the way, and a new client is created.
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await RentNewClientAfterRestart(pool, firstId);

        await DisposeAndAssertNoLeaks(pool);
    }

    /// <summary>
    /// Rents after the server restarted under an idle client: the pool drops the idle client (it noticed the disconnect,
    /// or does when it tries it) and hands out a new, connected one.
    /// </summary>
    private static async Task RentNewClientAfterRestart(IPoolUnderTest pool, string oldId)
    {
        // The idle client notices the disconnect on its own time; until it does, the pool may still hand it out
        // as healthy. Return such a rental and rent again.
        var deadline = DateTime.UtcNow.AddSeconds(T(TestTimeouts.Connect));
        while (true)
        {
            var client = await Rent(pool);
            try
            {
                if (client.ClientId != oldId)
                {
                    await client.WaitForStateAsync(ConnectionState.Connected, T(1));
                    await Assert.That(await Proxy(client).EchoString("new").Timeout(T(1))).IsEqualTo("new");
                    return;
                }

                if (DateTime.UtcNow >= deadline)
                    Fail.Test("The pool kept handing out the client of the stopped server.");
                await client.WaitForStateAsync(ConnectionState.Disconnected, T(1));
            }
            finally
            {
                await client.ReturnAsync();
            }
        }
    }

    // Concurrency Tests
    [Test]
    public async Task Pool_ThreadSafePropertyAccess()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 5 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            // Act - Concurrent property access while renting/returning
            var tasks = Enumerable.Range(0, 20).Select(async _ =>
            {
                using var client = await pool.RentClientAsync().Timeout(T(1));
                var available = pool.AvailableConnections; // Should not throw
                var max = pool.MaxConnections; // Should not throw
                await Task.Delay(10);
                return available + max; // Just to use the values
            }).ToArray();

            var results = await Task.WhenAll(tasks).Timeout(T(1));
            await Assert.That(results.Length).IsEqualTo(20);
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    /*
    [Test]
    public async Task Pool_ConcurrentDisposalScenarios()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 3 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        // Start some rental operations
        var rentalTasks = Enumerable.Range(0, 10).Select(async i =>
        {
            try
            {
                using var client = await pool.RentClientAsync().Timeout(T(1));
                await Task.Delay(100 + i * 10); // Stagger the operations
            }
            catch (ObjectDisposedException)
            {
                // Expected when pool is disposed during operation
            }
        }).ToArray();

        // Wait a bit then dispose pool
        await Task.Delay(50);
        var disposeTask = pool.DisposeAsync();

        // Wait for all operations to complete
        await Task.WhenAll(rentalTasks).Timeout(T(1));
        await disposeTask.Timeout(T(1));

        // Subsequent operations should throw
        await AssertThrows<ObjectDisposedException>(async () =>
            await pool.RentClientAsync());
    }*/

    // Rented client Tests
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task RentedClient_ThrowsAfterDispose(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(target, server.Endpoints, new ClientPoolSpec { MaxConnections = 1 });

        var rentedClient = await Rent(pool);

        // Verify working state
        await Assert.That(await Proxy(rentedClient).EchoString("hi").Timeout(T(1))).IsEqualTo("hi");
        await rentedClient.WaitForStateAsync(ConnectionState.Connected, T(1));

        // Dispose the rented client
        await rentedClient.DisposeAsync();

        // Should throw ObjectDisposedException
        await Assert.That(() => rentedClient.Proxy<IInteropServerNexus>()).ThrowsExactly<ObjectDisposedException>();
        await rentedClient.WaitForStateAsync(ConnectionState.Disconnected, T(1));
        await Assert.That(await rentedClient.WhenDisconnectedAsync(T(1))).IsEqualTo(DisconnectReason.None);

        var result = await rentedClient.EnsureConnectedAsync();
        await Assert.That(result).IsFalse();

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task RentedClient_HandlesSafeMultipleDispose(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(target, server.Endpoints, new ClientPoolSpec { MaxConnections = 1 });

        var rentedClient = await Rent(pool);

        // Dispose multiple times - should not throw
        await rentedClient.DisposeAsync();
        await rentedClient.DisposeAsync();
        await rentedClient.ReturnAsync();
        await rentedClient.DisposeAsync();

        await AssertState(pool, 1, 1, 0); // Client returned once

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task Pool_ReturnsUnhealthyClientProperly(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(target, server.Endpoints, new ClientPoolSpec { MaxConnections = 1 });

        var rentedClient = await Rent(pool);
        await rentedClient.WaitForStateAsync(ConnectionState.Connected, T(1));
        var firstId = rentedClient.ClientId;

        // Disconnect server to make client unhealthy
        await server.StopAsync().Timeout(T(TestTimeouts.Connect));
        await rentedClient.WaitForStateAsync(ConnectionState.Disconnected, T(TestTimeouts.Connect));

        // Return the now-unhealthy client
        await rentedClient.ReturnAsync();

        // Pool should not have the unhealthy client
        await AssertState(pool, 1, 0, 0);

        // Restart server and try again - should create new client
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await using (var newClient = await Rent(pool))
        {
            await Assert.That(newClient.ClientId).IsNotEqualTo(firstId);
            await newClient.WaitForStateAsync(ConnectionState.Connected, T(1));
        }

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    public async Task Pool_DefaultNexusFactory()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 1 };
        
        // Use null factory to test default behavior
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig, nexusFactory: null);

        try
        {
            // Should create client successfully with default factory
            using var client = await pool.RentClientAsync().Timeout(T(1));
            await Assert.That(client).IsNotNull();
            await Assert.That(client.Proxy).IsNotNull();
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    // Additional Test Cases

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task Pool_RentClientAsync_WithCancellationToken_ThrowsWhenCancelled(ClientTarget target)
    {
        // Connection will fail since no server is running; a cancel during the connection attempt ends the same way.
        await using var pool = await CreatePoolWithNoServer(target, new ClientPoolSpec { MaxConnections = 1 });

        using (var cts = new CancellationTokenSource(50))
        {
            await Assert.ThrowsExactlyAsync<ClientPoolConnectionException>(
                async () => await pool.RentAsync(cts.Token).Timeout(T(TestTimeouts.Connect)));
        }

        await AssertState(pool, 1, 0, 0);
        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task Pool_RentClientAsync_RespectsMaxConnectionsWithHighConcurrency(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(target, server.Endpoints, new ClientPoolSpec { MaxConnections = 3 });

        // Start many concurrent rental attempts
        var rentalTasks = new List<Task<IRentedClientUnderTest>>();
        for (int i = 0; i < 10; i++)
            rentalTasks.Add(pool.RentAsync());

        // Let the first rentals connect and the others queue up
        await Task.WhenAny(rentalTasks).Timeout(T(TestTimeouts.Connect));
        await Task.Delay(50);

        // Only 3 can be out at once due to max connections
        var completedTasks = rentalTasks.Count(t => t.IsCompletedSuccessfully);
        await Assert.That(completedTasks).IsLessThanOrEqualTo(3);
        await Assert.That((await pool.GetStateAsync()).Rented).IsLessThanOrEqualTo(3);

        // Return each rental as it is granted: every waiting rent gets a slot in turn.
        var ids = await Task.WhenAll(rentalTasks.Select(async task =>
        {
            var client = await task;
            await client.ReturnAsync();
            return client.ClientId;
        })).Timeout(T(10));

        await Assert.That(ids.Distinct().Count()).IsLessThanOrEqualTo(3);
        await Assert.That((await pool.GetStateAsync()).Rented).IsEqualTo(0);

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    public async Task Pool_CustomNexusFactory_CalledForEachClient()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 3 };
        
        var factoryCallCount = 0;
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(
            poolConfig,
            nexusFactory: () =>
            {
                Interlocked.Increment(ref factoryCallCount);
                return new ClientNexus();
            });

        try
        {
            // Act
            var client1 = await pool.RentClientAsync().Timeout(T(1));
            var client2 = await pool.RentClientAsync().Timeout(T(1));
            var client3 = await pool.RentClientAsync().Timeout(T(1));

            // Assert
            await Assert.That(factoryCallCount).IsEqualTo(3);

            // Clean up
            client1.Dispose();
            client2.Dispose();
            client3.Dispose();
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task Pool_ReturnsHealthyClientFirst(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(target, server.Endpoints, new ClientPoolSpec { MaxConnections = 2 });

        // Create two clients and return them
        var client1 = await Rent(pool);
        var client2 = await Rent(pool);
        await client1.ReturnAsync();
        await client2.ReturnAsync();
        await AssertState(pool, 2, 2, 0);

        // Disconnect server to make the idle clients unhealthy
        await server.StopAsync().Timeout(T(TestTimeouts.Connect));

        // Start server again
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        // Rent should return a healthy client
        var deadline = DateTime.UtcNow.AddSeconds(T(TestTimeouts.Connect));
        while (true)
        {
            await using var newClient = await Rent(pool);
            if (newClient.ClientId != client1.ClientId && newClient.ClientId != client2.ClientId)
            {
                await newClient.WaitForStateAsync(ConnectionState.Connected, T(1));
                await Assert.That(await Proxy(newClient).EchoString("healthy").Timeout(T(1))).IsEqualTo("healthy");
                break;
            }

            // An old client the pool still took for healthy: it has not noticed the restart yet.
            if (DateTime.UtcNow >= deadline)
                Fail.Test("The pool kept handing out clients of the stopped server.");
            await newClient.WaitForStateAsync(ConnectionState.Disconnected, T(1));
        }

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool | ClientCapabilities.FakeTime)]
    public async Task Pool_HealthCheckRemovesUnhealthyClients(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(
            target,
            server.Endpoints,
            new ClientPoolSpec { MaxConnections = 3, MaxIdleTimeMs = 300_000 }, // Long idle time to focus on health checks
            spec => spec.FakeTime = true);

        // Create multiple clients and return them
        var clients = new List<IRentedClientUnderTest>();
        for (int i = 0; i < 3; i++)
            clients.Add(await Rent(pool));

        foreach (var client in clients)
            await client.ReturnAsync();

        await AssertState(pool, 3, 3, 0);

        // Disconnect server to make all clients unhealthy
        await server.StopAsync().Timeout(T(TestTimeouts.Connect));

        // Each health-check pass (every 30 s on the test clock here) disposes the idle clients that are not connected,
        // well before their idle time (5 min) is up. The clients notice the disconnect in real time.
        var deadline = DateTime.UtcNow.AddSeconds(T(TestTimeouts.Connect));
        var advanced = TimeSpan.Zero;
        while (await Idle(pool) > 0 && DateTime.UtcNow < deadline)
        {
            if (advanced < TimeSpan.FromMinutes(4))
            {
                await pool.AdvanceTimeAsync(TimeSpan.FromSeconds(30));
                advanced += TimeSpan.FromSeconds(30);
            }

            await Task.Delay(50);
        }

        await Assert.That(await Idle(pool)).IsEqualTo(0).Because("The health check disposes idle clients that are not connected.");

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool | ClientCapabilities.FakeTime)]
    public async Task Pool_MaxIdleTimeConfiguration_ZeroValue(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(
            target,
            server.Endpoints,
            new ClientPoolSpec { MaxConnections = 2, MaxIdleTimeMs = 0, MinIdleConnections = 0 }, // Immediate idle timeout
            spec => spec.FakeTime = true);

        // Create and return client
        await (await Rent(pool)).ReturnAsync();

        await AssertState(pool, 2, 1, 0);

        // Health-check interval has a 100ms floor (Math.Max(100, MaxIdleTime/4)); advance past it.
        await pool.AdvanceTimeAsync(TimeSpan.FromMilliseconds(200));

        await Assert.That(await Idle(pool)).IsEqualTo(0).Because("MaxIdleTime=Zero must evict the client on the very first health-check pass.");

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task Pool_MinIdleConnections_GreaterThanMaxConnections(ClientTarget target)
    {
        await using var server = await StartServer();

        // This should be handled gracefully - MinIdleConnections effectively capped at MaxConnections.
        // Should not throw during construction.
        await using var pool = await CreatePool(target, server.Endpoints, new ClientPoolSpec
        {
            MaxConnections = 2,
            MinIdleConnections = 5, // Greater than max
        });

        await AssertState(pool, 2, 0, 0);
        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task Pool_RentedClient_ProxyThrowsAfterReturn(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(target, server.Endpoints, new ClientPoolSpec { MaxConnections = 1 });

        IRentedClientUnderTest rentedClient;
        IInteropServerNexus proxy;

        // Rent and return client
        await using (rentedClient = await Rent(pool))
        {
            // Verify proxy works while rented
            proxy = Proxy(rentedClient);
            await Assert.That(await proxy.EchoString("rented").Timeout(T(1))).IsEqualTo("rented");
        }

        // After the return, taking the proxy and invoking through the proxy taken before throw
        await Assert.That(() => rentedClient.Proxy<IInteropServerNexus>()).ThrowsExactly<ObjectDisposedException>();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await proxy.EchoString("late"));

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task Pool_SimultaneousRentAndReturn_ThreadSafety(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(target, server.Endpoints, new ClientPoolSpec { MaxConnections = 5 });

        // Simultaneous rent/return operations, reading each rental's state and invoking through it
        var states = await Task.WhenAll(Enumerable.Range(0, 50).Select(async i =>
        {
            await using var client = await pool.RentAsync();
            await Task.Delay(Random.Shared.Next(1, 10)); // Random small delay
            await Assert.That(await Proxy(client).EchoInt32(i)).IsEqualTo(i);
            return await client.GetStateAsync();
        })).Timeout(T(10));

        await Assert.That(states.All(s => s == ConnectionState.Connected)).IsTrue().Because(string.Join(", ", states));
        await Assert.That((await pool.GetStateAsync()).Rented).IsEqualTo(0);

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task Pool_DisconnectedTask_ReflectsClientState(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(target, server.Endpoints, new ClientPoolSpec { MaxConnections = 1 });

        await using (var client = await Rent(pool))
        {
            var disconnected = client.WhenDisconnectedAsync(T(TestTimeouts.Connect));

            // Initially should not be completed
            await Task.Delay(50);
            await Assert.That(disconnected.IsCompleted).IsFalse();

            // Disconnect server
            await server.StopAsync().Timeout(T(TestTimeouts.Connect));

            // Wait for disconnection
            await disconnected;
            await Assert.That(await client.GetStateAsync()).IsEqualTo(ConnectionState.Disconnected);
        }

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task Pool_EnsureConnectedAsync_FailsWhenServerDown(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(target, server.Endpoints, new ClientPoolSpec { MaxConnections = 1 });

        await using (var client = await Rent(pool))
        {
            // Stop server
            await server.StopAsync().Timeout(T(TestTimeouts.Connect));
            await client.WaitForStateAsync(ConnectionState.Disconnected, T(TestTimeouts.Connect));

            // EnsureConnectedAsync should fail
            var result = await client.EnsureConnectedAsync().Timeout(T(TestTimeouts.Connect));
            await Assert.That(result).IsFalse();
        }

        // The failed connect attempt leaves the client's DisconnectedTask completed, so the audit sees no connected client.
        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    public async Task Client_DisconnectedTask_CompletedAfterFailedConnect()
    {
        // No server listening
        var (_, client, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds),
            startServer: false);

        var result = await client.TryConnectAsync().Timeout(T(TestTimeouts.Connect));
        await Assert.That(result.Success).IsFalse();
        await Assert.That(client.DisconnectedTask.IsCompleted).IsTrue();

        // A second failed attempt leaves it completed too.
        result = await client.TryConnectAsync().Timeout(T(TestTimeouts.Connect));
        await Assert.That(result.Success).IsFalse();
        await Assert.That(client.DisconnectedTask.IsCompleted).IsTrue();
    }

    [Test]
    public async Task Client_DisconnectedTask_CompletedAfterFailedReconnect()
    {
        var (server, client, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await Assert.That(client.DisconnectedTask.IsCompleted).IsFalse();

        // The server stops: the session ends and its DisconnectedTask completes.
        await server.StopAsync().Timeout(T(TestTimeouts.Connect));
        await client.DisconnectedTask.Timeout(T(TestTimeouts.Connect));

        // A connect attempt that fails at the transport leaves DisconnectedTask completed.
        var result = await client.TryConnectAsync().Timeout(T(TestTimeouts.Connect));
        await Assert.That(result.Success).IsFalse();
        await Assert.That(client.DisconnectedTask.IsCompleted).IsTrue();
    }

    [Test]
    public async Task Pool_RentedClient_DisconnectedTask_CompletedAfterFailedEnsureConnected()
    {
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(
            new NexusClientPoolConfig(CreateClientConfig(Type.Uds)) { MaxConnections = 1 });

        try
        {
            using var client = await pool.RentClientAsync().Timeout(T(TestTimeouts.Connect));
            await Assert.That(client.DisconnectedTask.IsCompleted).IsFalse();

            await server.StopAsync().Timeout(T(TestTimeouts.Connect));
            await client.DisconnectedTask.Timeout(T(TestTimeouts.Connect));

            await Assert.That(await client.EnsureConnectedAsync().Timeout(T(TestTimeouts.Connect))).IsFalse();
            await Assert.That(client.DisconnectedTask.IsCompleted).IsTrue();
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task Pool_EnsureConnectedAsync_SucceedsWhenAlreadyConnected(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(target, server.Endpoints, new ClientPoolSpec { MaxConnections = 1 });

        await using (var client = await Rent(pool))
        {
            // Should succeed when already connected
            var result = await client.EnsureConnectedAsync().Timeout(T(1));
            await Assert.That(result).IsTrue();
        }

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task Pool_SequentialRentReturn_MaintainsCorrectCounts(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(target, server.Endpoints, new ClientPoolSpec { MaxConnections = 3 });

        // Sequential rent/return operations reuse the one client
        var ids = new HashSet<string>();
        for (int i = 0; i < 10; i++)
        {
            await using var client = await Rent(pool);
            ids.Add(client.ClientId);
            await Assert.That(await pool.GetStateAsync()).IsEqualTo(new ClientPoolState(3, 0, 1)).Because($"Iteration {i}: Should have 0 available when rented");
        }

        // After all operations, should have 1 available client
        await AssertState(pool, 3, 1, 0);
        await Assert.That(ids.Count).IsEqualTo(1);

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool | ClientCapabilities.FakeTime)]
    public async Task Pool_HealthCheckTimer_ContinuesAfterExceptions(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(
            target,
            server.Endpoints,
            new ClientPoolSpec { MaxConnections = 2, MaxIdleTimeMs = 50 }, // Very short for quick test
            spec => spec.FakeTime = true);

        // Create clients to populate pool
        var client1 = await Rent(pool);
        var client2 = await Rent(pool);
        await client1.ReturnAsync();
        await client2.ReturnAsync();
        await AssertState(pool, 2, 2, 0);

        // Default MinIdleConnections (1) keeps one client; the surplus is evicted.
        await pool.AdvanceTimeAsync(TimeSpan.FromMilliseconds(100));
        await Assert.That(await Idle(pool)).IsEqualTo(1);

        // The timer re-arms and fires on each later pass: fill the pool again, and the next passes evict again.
        client1 = await Rent(pool);
        client2 = await Rent(pool);
        await client1.ReturnAsync();
        await client2.ReturnAsync();
        await AssertState(pool, 2, 2, 0);

        await pool.AdvanceTimeAsync(TimeSpan.FromMilliseconds(100));
        await pool.AdvanceTimeAsync(TimeSpan.FromMilliseconds(100));
        await Assert.That(await Idle(pool)).IsEqualTo(1);

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    public async Task Pool_RentClientAsync_HandlesFactoryReturningNull()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 1 };
        
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(
            poolConfig,
            nexusFactory: () => null!); // Return null to test error handling

        try
        {
            // Should handle null factory return gracefully
            await AssertThrows<NullReferenceException>(async () =>
                await pool.RentClientAsync().Timeout(T(1)));
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task Pool_ConnectionFailureDuringRent_ReleasesResources(ClientTarget target)
    {
        // No server listening
        await using var pool = await CreatePoolWithNoServer(target, new ClientPoolSpec { MaxConnections = 2 });

        // Multiple failed connection attempts
        for (int i = 0; i < 3; i++)
            await Assert.ThrowsExactlyAsync<ClientPoolConnectionException>(async () => await Rent(pool));

        // Pool should still be functional for future attempts
        await AssertState(pool, 2, 0, 0);

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task Pool_RentedClient_StateTransitions(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(target, server.Endpoints, new ClientPoolSpec { MaxConnections = 1 });

        await using (var client = await Rent(pool))
        {
            // Initial state
            await client.WaitForStateAsync(ConnectionState.Connected, T(1));

            // Disconnect server: the state reflects the disconnection
            await server.StopAsync().Timeout(T(TestTimeouts.Connect));
            await client.WaitForStateAsync(ConnectionState.Disconnected, T(TestTimeouts.Connect));

            // Reconnect server
            await server.StartAsync().Timeout(T(TestTimeouts.Connect));

            // Manual reconnect
            var reconnected = await client.EnsureConnectedAsync().Timeout(T(TestTimeouts.Connect));
            await Assert.That(reconnected).IsTrue();
            await client.WaitForStateAsync(ConnectionState.Connected, T(1));
        }

        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task Pool_LongRunningOperations_DoNotBlockOthers(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var pool = await CreatePool(target, server.Endpoints, new ClientPoolSpec { MaxConnections = 3 });

        var completedOperations = 0;

        // Operations with different durations, held on the server
        async Task Operation(int milliseconds, int value)
        {
            await using var client = await Rent(pool);
            await Assert.That(await Proxy(client).Delay(milliseconds, value, CancellationToken.None)).IsEqualTo(value);
            Interlocked.Increment(ref completedOperations);
        }

        // One long operation and five short ones
        var tasks = new List<Task> { Operation(500, -1) };
        for (int i = 0; i < 5; i++)
            tasks.Add(Operation(50, i));

        await Task.WhenAll(tasks).Timeout(T(2));

        await Assert.That(completedOperations).IsEqualTo(6);
        await DisposeAndAssertNoLeaks(pool);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.ClientPool)]
    public async Task Pool_RentClientAsync_ThrowsSpecificErrorOnAuthenticationRequired(ClientTarget target)
    {
        // A server that requires authentication and rejects every client
        await using var server = await StartServer(new InteropServerOptions
        {
            TokenValidator = (_, _) => ValueTask.FromResult<IIdentity?>(null),
        });

        // Clients without authentication configured
        await using var pool = await CreatePool(target, server.Endpoints, new ClientPoolSpec { MaxConnections = 1 });

        var exception = await Assert.ThrowsExactlyAsync<ClientPoolConnectionException>(async () => await Rent(pool));
        await Assert.That(exception!.ConnectionResult.DisconnectReason).IsEqualTo(DisconnectReason.Authentication);

        await DisposeAndAssertNoLeaks(pool);
    }
}
