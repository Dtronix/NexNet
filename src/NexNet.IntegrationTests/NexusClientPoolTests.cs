using NexNet.IntegrationTests.Pipes;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Transports;
using NUnit.Framework;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Tests for NexusClientPool functionality including connection pooling, health checking,
/// and rent/return operations.
/// </summary>
internal partial class NexusClientPoolTests : BaseTests
{
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Quic)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task RentClientAsync_CreatesAndConnectsNewClient(Type type)
    {
        // Arrange
        var (server, _, _, _) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(type);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 5 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            // Act
            using var rentedClient = await pool.RentClientAsync().Timeout(5);

            // Assert
            Assert.That(rentedClient, Is.Not.Null);
            Assert.That(rentedClient.State, Is.EqualTo(ConnectionState.Connected));
            Assert.That(rentedClient.Proxy, Is.Not.Null);
            Assert.That(pool.AvailableConnections, Is.EqualTo(0)); // Client is rented out
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Quic)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task RentClientAsync_ReusesReturnedClients(Type type)
    {
        // Arrange
        var (server, serverNexus, _, _) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(type);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 5 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        var invocationCount = 0;
        serverNexus.ServerVoidEvent = _ => invocationCount++;

        try
        {
            // Act - First rental
            ClientNexus.ServerProxy? firstProxy = null;
            using (var firstClient = await pool.RentClientAsync().Timeout(5))
            {
                firstProxy = firstClient.Proxy;
                firstClient.Proxy.ServerVoid();
                await Task.Delay(50); // Allow invocation to complete
            }

            Assert.That(pool.AvailableConnections, Is.EqualTo(1)); // Client should be returned

            // Act - Second rental (should reuse same client)
            using (var secondClient = await pool.RentClientAsync().Timeout(5))
            {
                secondClient.Proxy.ServerVoid();
                await Task.Delay(50); // Allow invocation to complete

                // Assert - Same proxy instance indicates client reuse
                Assert.That(secondClient.Proxy, Is.SameAs(firstProxy));
                Assert.That(pool.AvailableConnections, Is.EqualTo(0)); // Client is rented out again
            }

            Assert.That(invocationCount, Is.EqualTo(2));
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

  
    [Test]
    public async Task RentClientAsync_HandlesMaxConnectionsLimit()
    {
        // Arrange
        var (server, serverNexus, client, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 2 };
        
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);
        
        var invocationCount = 0;
        serverNexus.ServerTaskEvent = async _ =>
        {
            Interlocked.Increment(ref invocationCount);
            //await Task.Delay(100); // Hold connection briefly
        };

        try
        {
            //await client.ConnectAsync();
            // Act - Rent maximum connections
            var client1 = await pool.RentClientAsync().Timeout(1);
            var client2 = await pool.RentClientAsync().Timeout(1);

            // Start operations on both clients
            await client1.Proxy.ServerTask().Timeout(1);
            //await client2.Proxy.ServerTask().Timeout(1);

            Assert.That(pool.AvailableConnections, Is.EqualTo(0));

            // Try to rent a third client (should wait for return)
            var rentTask = pool.RentClientAsync();
            
            // Should not complete immediately due to max connections
            Assert.That(rentTask.IsCompleted, Is.False);

            // Return one client
            await client1.DisposeAsync();
            
            // Now third client should be available
            using var client3 = await rentTask.Timeout(5);
            Assert.That(client3, Is.Not.Null);
            
            //await client2.DisposeAsync().Timeout(1);
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [TestCase(Type.Tcp)]
    [TestCase(Type.Uds)]
    public async Task EnsureConnectedAsync_ReconnectsWhenNeeded(Type type)
    {
        // Arrange
        var (server, serverNexus, _, _) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(type);
        clientConfig.ReconnectionPolicy = new DefaultReconnectionPolicy(new[]
        {
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(400)
        }, continuousRetry: false);

        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 5 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            // Act
            using var rentedClient = await pool.RentClientAsync().Timeout(5);
            
            Assert.That(rentedClient.State, Is.EqualTo(ConnectionState.Connected));

            // Simulate disconnection by stopping server
            await server.StopAsync().Timeout(2);
            
            // Wait for disconnection to be detected
            await Task.Delay(200);

            // Start server again
            await server.StartAsync().Timeout(2);

            // Attempt to ensure connection
            var reconnected = await rentedClient.EnsureConnectedAsync().Timeout(5);

            // Assert
            Assert.That(reconnected, Is.True);
            Assert.That(rentedClient.State, Is.EqualTo(ConnectionState.Connected));
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task Dispose_DisposesAllClients()
    {
        // Arrange
        var (server, _, _, _) = CreateServerClient(
            CreateServerConfig(Type.Tcp),
            CreateClientConfig(Type.Tcp));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Tcp);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 3 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        // Rent and return some clients to populate the pool
        using (var client1 = await pool.RentClientAsync().Timeout(5)) { }
        using (var client2 = await pool.RentClientAsync().Timeout(5)) { }

        Assert.That(pool.AvailableConnections, Is.EqualTo(2));

        // Act
        await pool.DisposeAsync();

        // Assert
        Assert.That(pool.AvailableConnections, Is.EqualTo(0));

        // Should throw when trying to rent after disposal
        await AssertThrows<ObjectDisposedException>(async () => 
            await pool.RentClientAsync());
    }

    [Test]
    public async Task RentClientAsync_ThrowsOnConnectionFailure()
    {
        // Arrange - No server running
        var clientConfig = CreateClientConfig(Type.Tcp);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 5 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            // Act & Assert
            await AssertThrows<InvalidOperationException>(async () =>
                await pool.RentClientAsync().Timeout(2));
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }
    
    [Test]
    public async Task Pool_HandlesMultipleConcurrentRentals()
    {
        // Arrange
        var (server, serverNexus, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds, BasePipeTests.LogMode.Always),
            CreateClientConfig(Type.Uds, BasePipeTests.LogMode.Always));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds, BasePipeTests.LogMode.Always);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 10 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        var invocationCount = 0;
        serverNexus.ServerVoidEvent = _ => Interlocked.Increment(ref invocationCount);

        try
        {
            // Act - Multiple concurrent rentals and operations
            var tasks = Enumerable.Range(0, 20).Select(async i =>
            {
                using var client = await pool.RentClientAsync().Timeout(5);
                client.Proxy.ServerVoid();
                await Task.Delay(10); // Small delay to simulate work
                return i;
            }).ToArray();

            var results = await Task.WhenAll(tasks).Timeout(10);

            // Assert
            Assert.That(results.Length, Is.EqualTo(20));
            Assert.That(invocationCount, Is.EqualTo(20));
            
            // All clients should be returned to pool eventually
            await Task.Delay(100);
            Assert.That(pool.AvailableConnections, Is.LessThanOrEqualTo(10));
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task Pool_CustomNexusFactory()
    {
        // Arrange
        var (server, serverNexus, _, _) = CreateServerClient(
            CreateServerConfig(Type.Tcp),
            CreateClientConfig(Type.Tcp));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Tcp);
        var factoryCalled = 0;
        
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 5 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(
            poolConfig,
            nexusFactory: () =>
            {
                Interlocked.Increment(ref factoryCalled);
                return new ClientNexus();
            });

        try
        {
            // Act
            using (var client1 = await pool.RentClientAsync().Timeout(5)) { }
            using (var client2 = await pool.RentClientAsync().Timeout(5)) { }

            // Assert - Factory should be called for each unique client
            Assert.That(factoryCalled, Is.GreaterThanOrEqualTo(1));
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task Pool_PropertiesReflectCurrentState()
    {
        // Arrange
        var (server, _, _, _) = CreateServerClient(
            CreateServerConfig(Type.Tcp),
            CreateClientConfig(Type.Tcp));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Tcp);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 3 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            // Act & Assert
            Assert.That(pool.MaxConnections, Is.EqualTo(3));
            Assert.That(pool.AvailableConnections, Is.EqualTo(0));

            var client1 = await pool.RentClientAsync().Timeout(5);
            var client2 = await pool.RentClientAsync().Timeout(5);

            Assert.That(pool.AvailableConnections, Is.EqualTo(0)); // Both rented

            await client1.DisposeAsync();
            Assert.That(pool.AvailableConnections, Is.EqualTo(1)); // One returned

            await client2.DisposeAsync();
            Assert.That(pool.AvailableConnections, Is.EqualTo(2)); // Both returned
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }
}
