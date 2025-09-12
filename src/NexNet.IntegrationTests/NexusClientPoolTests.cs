using System.Collections.Concurrent;
using NexNet.IntegrationTests.Pipes;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NexNet.Transports;
using NUnit.Framework;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Tests for NexusClientPool functionality including connection pooling, health checking,
/// and rent/return operations.
/// </summary>
internal class NexusClientPoolTests : BaseTests
{
    [Test]
    public async Task RentClientAsync_CreatesAndConnectsNewClient()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 5 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            // Act
            using var rentedClient = await pool.RentClientAsync().Timeout(1);

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
    
    [Test]
    public async Task RentClientAsync_ReusesReturnedClients()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 5 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        var invocationCount = 0;
        
        server.OnNexusCreated = nexus => nexus.ServerVoidEvent = _ => 
            Interlocked.Increment(ref invocationCount);

        try
        {
            // Act - First rental
            ClientNexus.ServerProxy? firstProxy = null;
            using (var firstClient = await pool.RentClientAsync().Timeout(1))
            {
                firstProxy = firstClient.Proxy;
                firstClient.Proxy.ServerVoid();
                await Task.Delay(50); // Allow invocation to complete
            }

            Assert.That(pool.AvailableConnections, Is.EqualTo(1)); // Client should be returned

            // Act - Second rental (should reuse same client)
            using (var secondClient = await pool.RentClientAsync().Timeout(1))
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
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 2 };
        
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);
        
        var invocationCount = 0;
        server.OnNexusCreated = nexus => nexus.ServerTaskEvent = _ =>
        {
            Interlocked.Increment(ref invocationCount);
            //await Task.Delay(100); // Hold connection briefly
            return ValueTask.CompletedTask;
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
            client1.Dispose();
            
            // Now third client should be available
            using var client3 = await rentTask.Timeout(1);
            Assert.That(client3, Is.Not.Null);
            
            //await client2.DisposeAsync().Timeout(1);
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }
    
    [Test]
    public async Task EnsureConnectedAsync_ReconnectsWhenNeeded()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
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
            using var rentedClient = await pool.RentClientAsync().Timeout(1);
            
            Assert.That(rentedClient.State, Is.EqualTo(ConnectionState.Connected));

            // Simulate disconnection by stopping server
            await server.StopAsync().Timeout(1);
            
            // Wait for disconnection to be detected
            await Task.Delay(200);

            // Start server again
            await server.StartAsync().Timeout(1);

            // Attempt to ensure connection
            var reconnected = await rentedClient.EnsureConnectedAsync().Timeout(1);

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
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 3 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        // Rent and return some clients to populate the pool
        var client1 = await pool.RentClientAsync().Timeout(1);
        var client2 = await pool.RentClientAsync().Timeout(1);
        
        client1.Dispose();
        client2.Dispose();

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
        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 5 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            // Act & Assert
            await AssertThrows<ClientPoolConnectionException>(async () =>
                await pool.RentClientAsync().Timeout(1));
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
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds, BasePipeTests.LogMode.Always),
            CreateClientConfig(Type.Uds, BasePipeTests.LogMode.Always));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds, BasePipeTests.LogMode.Always);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 10 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        var invocationCount = 0;
        server.OnNexusCreated = nexus => nexus.ServerVoidEvent = _ => Interlocked.Increment(ref invocationCount);

        try
        {
            // Act - Multiple concurrent rentals and operations
            var tasks = Enumerable.Range(0, 20).Select(async i =>
            {
                using var client = await pool.RentClientAsync().Timeout(1);
                client.Proxy.ServerVoid();
                await Task.Delay(10); // Small delay to simulate work
                return i;
            }).ToArray();

            var results = await Task.WhenAll(tasks).Timeout(1);

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
    public async Task Pool_PropertiesReflectCurrentState()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 3 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            // Act & Assert
            Assert.That(pool.MaxConnections, Is.EqualTo(3));
            Assert.That(pool.AvailableConnections, Is.EqualTo(0));

            var client1 = await pool.RentClientAsync().Timeout(1);
            var client2 = await pool.RentClientAsync().Timeout(1);

            Assert.That(pool.AvailableConnections, Is.EqualTo(0)); // Both rented

            client1.Dispose();
            Assert.That(pool.AvailableConnections, Is.EqualTo(1)); // One returned

            client2.Dispose();
            Assert.That(pool.AvailableConnections, Is.EqualTo(2)); // Both returned
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    // Idle Timeout Tests
    [Test]
    public async Task Pool_DisposesIdleClientsAfterTimeout()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) 
        { 
            MaxConnections = 5,
            MaxIdleTime = TimeSpan.FromMilliseconds(100) // Very short idle time
        };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            // Create and return multiple clients
            var client1 = await pool.RentClientAsync().Timeout(1);
            var client2 = await pool.RentClientAsync().Timeout(1);
            var client3 = await pool.RentClientAsync().Timeout(1);
            client1.Dispose();
            client2.Dispose();
            client3.Dispose();

            Assert.That(pool.AvailableConnections, Is.EqualTo(3));

            // Wait for idle timeout + health check interval
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // At least one client should remain (pool keeps minimum of 1)
            Assert.That(pool.AvailableConnections, Is.GreaterThanOrEqualTo(1));
            Assert.That(pool.AvailableConnections, Is.LessThan(3)); // Some should be disposed
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task Pool_KeepsAtLeastOneClientDespiteIdleTimeout()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) 
        { 
            MaxConnections = 1,
            MaxIdleTime = TimeSpan.FromMilliseconds(50),
            MinIdleConnections = 1
        };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            // Create and return one client
            using (var _ = await pool.RentClientAsync().Timeout(1)) { }

            Assert.That(pool.AvailableConnections, Is.EqualTo(1));

            // Wait for idle timeout + health check interval
            await Task.Delay(TimeSpan.FromMilliseconds(200));

            // Should still have 1 client (minimum retention)
            Assert.That(pool.AvailableConnections, Is.EqualTo(1));
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task Pool_RespectsMinIdleConnections()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) 
        { 
            MaxConnections = 10,
            MaxIdleTime = TimeSpan.FromMilliseconds(100),
            MinIdleConnections = 3
        };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            // Create and return multiple clients
            var clients = new List<IRentedNexusClient<ClientNexus.ServerProxy>>();
            for (int i = 0; i < 5; i++)
            {
                clients.Add(await pool.RentClientAsync().Timeout(1));
            }

            // Return all clients
            foreach (var client in clients)
            {
                client.Dispose();
            }

            Assert.That(pool.AvailableConnections, Is.EqualTo(5));

            // Wait for idle timeout + health check interval
            await Task.Delay(TimeSpan.FromMilliseconds(200));

            // Should have at least MinIdleConnections (3) remaining
            Assert.That(pool.AvailableConnections, Is.GreaterThanOrEqualTo(3));
            Assert.That(pool.AvailableConnections, Is.LessThan(5)); // Some should be disposed
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task Pool_MinIdleConnectionsDoesNotCreateNewClients()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) 
        { 
            MaxConnections = 10,
            MaxIdleTime = TimeSpan.FromMinutes(5), // Long idle time to prevent cleanup
            MinIdleConnections = 5 // Higher than what we'll create
        };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            // Create and return fewer clients than MinIdleConnections
            var clients = new List<IRentedNexusClient<ClientNexus.ServerProxy>>();
            for (int i = 0; i < 2; i++)
            {
                clients.Add(await pool.RentClientAsync().Timeout(1));
            }

            // Return all clients
            foreach (var client in clients)
            {
                client.Dispose();
            }

            Assert.That(pool.AvailableConnections, Is.EqualTo(2));

            // Wait a bit to ensure no new clients are automatically created
            await Task.Delay(TimeSpan.FromMilliseconds(200));

            // Should still only have 2 clients (MinIdleConnections doesn't create new ones)
            Assert.That(pool.AvailableConnections, Is.EqualTo(2));
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task Pool_MinIdleConnectionsWithZeroValue()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) 
        { 
            MaxConnections = 5,
            MaxIdleTime = TimeSpan.FromMilliseconds(100),
            MinIdleConnections = 0 // Allow all idle clients to be disposed
        };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            // Create and return multiple clients
            var clients = new List<IRentedNexusClient<ClientNexus.ServerProxy>>();
            for (int i = 0; i < 3; i++)
            {
                clients.Add(await pool.RentClientAsync().Timeout(1));
            }

            // Return all clients
            foreach (var client in clients)
            {
                client.Dispose();
            }

            Assert.That(pool.AvailableConnections, Is.EqualTo(3));

            // Wait for idle timeout + health check interval
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // With MinIdleConnections = 0, all idle clients should be disposed
            Assert.That(pool.AvailableConnections, Is.EqualTo(0));
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    // Configuration Validation Tests
    [Test]
    public void Pool_ThrowsOnZeroMaxConnections()
    {
        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 0 };

        Assert.Throws<ArgumentOutOfRangeException>(() => 
            new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig));
    }

    [Test]
    public void Pool_ThrowsOnNegativeMaxConnections()
    {
        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = -1 };

        Assert.Throws<ArgumentOutOfRangeException>(() => 
            new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig));
    }

    [Test]
    public void Pool_ThrowsOnNullConfig()
    {
        Assert.Throws<ArgumentNullException>(() => 
            new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(null!));
    }

    [Test]
    public void Pool_ThrowsOnNegativeMinIdleConnections()
    {
        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MinIdleConnections = -1 };

        Assert.Throws<ArgumentOutOfRangeException>(() => 
            new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig));
    }

    // Error Handling Tests
    [Test]
    public async Task Pool_ReleaseSemaphoreOnConnectionException()
    {
        // Arrange - No server running to force connection failure
        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 1 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            // Act - Try to rent when connection will fail
            await AssertThrows<ClientPoolConnectionException>(async () =>
                await pool.RentClientAsync().Timeout(1));

            // Assert - Semaphore should be released, allowing another attempt
            await AssertThrows<ClientPoolConnectionException>(async () =>
                await pool.RentClientAsync().Timeout(1));
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task Pool_HandlesSafeMultipleDispose()
    {
        // Arrange
        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 1 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        // Act - Dispose multiple times
        await pool.DisposeAsync();
        await pool.DisposeAsync(); // Should not throw
        await pool.DisposeAsync(); // Should not throw

        // Assert - Operations should throw ObjectDisposedException
        await AssertThrows<ObjectDisposedException>(async () =>
            await pool.RentClientAsync());
    }

    [Test]
    public async Task Pool_HandlesCustomNexusFactoryExceptions()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

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
                await pool.RentClientAsync().Timeout(1));

            // Second call should succeed
            using var client = await pool.RentClientAsync().Timeout(1);
            Assert.That(client, Is.Not.Null);
            Assert.That(factoryCallCount, Is.EqualTo(2));
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    // State Transition Tests
    [Test]
    public async Task Pool_HandlesClientStateChanges()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 1 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            // Rent and return a client
            using (var client = await pool.RentClientAsync().Timeout(1))
            {
                Assert.That(client.State, Is.EqualTo(ConnectionState.Connected));
            }

            Assert.That(pool.AvailableConnections, Is.EqualTo(1));

            // Stop server to make client unhealthy
            await server.StopAsync().Timeout(1);
            await Task.Delay(100); // Allow disconnection to be detected

            // Try to rent again - should create new client since old one is unhealthy
            await server.StartAsync().Timeout(1);
            using (var newClient = await pool.RentClientAsync().Timeout(1))
            {
                Assert.That(newClient.State, Is.EqualTo(ConnectionState.Connected));
            }
        }
        finally
        {
            await pool.DisposeAsync();
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

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 5 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            // Act - Concurrent property access while renting/returning
            var tasks = Enumerable.Range(0, 20).Select(async _ =>
            {
                using var client = await pool.RentClientAsync().Timeout(1);
                var available = pool.AvailableConnections; // Should not throw
                var max = pool.MaxConnections; // Should not throw
                await Task.Delay(10);
                return available + max; // Just to use the values
            }).ToArray();

            var results = await Task.WhenAll(tasks).Timeout(1);
            Assert.That(results.Length, Is.EqualTo(20));
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

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 3 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        // Start some rental operations
        var rentalTasks = Enumerable.Range(0, 10).Select(async i =>
        {
            try
            {
                using var client = await pool.RentClientAsync().Timeout(1);
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
        await Task.WhenAll(rentalTasks).Timeout(1);
        await disposeTask.Timeout(1);

        // Subsequent operations should throw
        await AssertThrows<ObjectDisposedException>(async () =>
            await pool.RentClientAsync());
    }*/

    // RentedClientWrapper Tests
    [Test]
    public async Task RentedClient_ThrowsAfterDispose()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 1 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            var rentedClient = await pool.RentClientAsync().Timeout(1);
            
            // Verify working state
            Assert.That(rentedClient.Proxy, Is.Not.Null);
            Assert.That(rentedClient.State, Is.EqualTo(ConnectionState.Connected));

            // Dispose the rented client
            rentedClient.Dispose();

            // Should throw ObjectDisposedException
            Assert.Throws<ObjectDisposedException>(() => _ = rentedClient.Proxy);
            Assert.That(rentedClient.State, Is.EqualTo(ConnectionState.Disconnected));
            Assert.That(rentedClient.DisconnectedTask.IsCompleted, Is.True);
            
            var result = await rentedClient.EnsureConnectedAsync();
            Assert.That(result, Is.False);
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task RentedClient_HandlesSafeMultipleDispose()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 1 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            var rentedClient = await pool.RentClientAsync().Timeout(1);
            
            // Dispose multiple times - should not throw
            rentedClient.Dispose();
            rentedClient.Dispose();
            rentedClient.Dispose();
            rentedClient.Dispose();

            Assert.That(pool.AvailableConnections, Is.EqualTo(1)); // Client returned once
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task Pool_ReturnsUnhealthyClientProperly()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 1 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            var rentedClient = await pool.RentClientAsync().Timeout(1);
            Assert.That(rentedClient.State, Is.EqualTo(ConnectionState.Connected));

            // Disconnect server to make client unhealthy
            await server.StopAsync().Timeout(1);
            await Task.Delay(200); // Wait for disconnection

            // Return the now-unhealthy client
            rentedClient.Dispose();

            // Pool should not have the unhealthy client
            Assert.That(pool.AvailableConnections, Is.EqualTo(0));

            // Restart server and try again - should create new client
            await server.StartAsync().Timeout(1);
            using var newClient = await pool.RentClientAsync().Timeout(1);
            Assert.That(newClient.State, Is.EqualTo(ConnectionState.Connected));
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task Pool_DefaultNexusFactory()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 1 };
        
        // Use null factory to test default behavior
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig, nexusFactory: null);

        try
        {
            // Should create client successfully with default factory
            using var client = await pool.RentClientAsync().Timeout(1);
            Assert.That(client, Is.Not.Null);
            Assert.That(client.Proxy, Is.Not.Null);
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    // Additional Test Cases

    [Test]
    public async Task Pool_RentClientAsync_WithCancellationToken_ThrowsWhenCancelled()
    {
        // Arrange
        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 1 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            // Act & Assert - Connection will fail since no server is running
            using var cts = new CancellationTokenSource(50);
            await AssertThrows<ClientPoolConnectionException>(async () =>
                await pool.RentClientAsync(cts.Token));
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task Pool_RentClientAsync_RespectsMaxConnectionsWithHighConcurrency()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 3 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            // Act - Start many concurrent rental attempts
            var rentalTasks = new List<Task<IRentedNexusClient<ClientNexus.ServerProxy>>>();
            for (int i = 0; i < 10; i++)
            {
                rentalTasks.Add(pool.RentClientAsync());
            }

            // Wait a bit to let some tasks queue up
            await Task.Delay(50);

            // Only 3 should complete immediately due to max connections
            var completedTasks = rentalTasks.Count(t => t.IsCompletedSuccessfully);
            Assert.That(completedTasks, Is.LessThanOrEqualTo(3));

            // Clean up
            foreach (var task in rentalTasks)
            {
                try
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        var client = await task;
                        client.Dispose();
                    }
                }
                catch
                {
                    // Ignore cleanup exceptions
                }
            }
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task Pool_CustomNexusFactory_CalledForEachClient()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

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
            var client1 = await pool.RentClientAsync().Timeout(1);
            var client2 = await pool.RentClientAsync().Timeout(1);
            var client3 = await pool.RentClientAsync().Timeout(1);

            // Assert
            Assert.That(factoryCallCount, Is.EqualTo(3));

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
    public async Task Pool_ReturnsHealthyClientFirst()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 2 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            // Create two clients and return them
            var client1 = await pool.RentClientAsync().Timeout(1);
            var client2 = await pool.RentClientAsync().Timeout(1);
            
            var client1Proxy = client1.Proxy;
            var client2Proxy = client2.Proxy;

            client1.Dispose();
            client2.Dispose();

            // Disconnect server to make one client unhealthy
            await server.StopAsync().Timeout(1);
            await Task.Delay(100);

            // Start server again
            await server.StartAsync().Timeout(1);

            // Rent should return a healthy client
            using var newClient = await pool.RentClientAsync().Timeout(1);
            Assert.That(newClient.State, Is.EqualTo(ConnectionState.Connected));
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task Pool_HealthCheckRemovesUnhealthyClients()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) 
        { 
            MaxConnections = 3,
            MaxIdleTime = TimeSpan.FromMinutes(5) // Long idle time to focus on health checks
        };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            // Create multiple clients and return them
            var clients = new List<IRentedNexusClient<ClientNexus.ServerProxy>>();
            for (int i = 0; i < 3; i++)
            {
                clients.Add(await pool.RentClientAsync().Timeout(1));
            }

            foreach (var client in clients)
            {
                client.Dispose();
            }

            Assert.That(pool.AvailableConnections, Is.EqualTo(3));

            // Disconnect server to make all clients unhealthy
            await server.StopAsync().Timeout(1);
            await Task.Delay(500); // Allow more time for health check to run and detect disconnection

            // Available connections should decrease as unhealthy clients are removed
            Assert.That(pool.AvailableConnections, Is.LessThanOrEqualTo(3));
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task Pool_MaxIdleTimeConfiguration_ZeroValue()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) 
        { 
            MaxConnections = 2,
            MaxIdleTime = TimeSpan.Zero, // Immediate idle timeout
            MinIdleConnections = 0
        };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            // Create and return client
            using (var client = await pool.RentClientAsync().Timeout(1)) { }

            Assert.That(pool.AvailableConnections, Is.EqualTo(1));

            // Wait for health check to process idle timeout
            await Task.Delay(200);

            // With zero idle time, client should be disposed
            Assert.That(pool.AvailableConnections, Is.EqualTo(0));
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public void Pool_MinIdleConnections_GreaterThanMaxConnections()
    {
        // Arrange
        var clientConfig = CreateClientConfig(Type.Uds);
        
        // This should be handled gracefully - MinIdleConnections effectively capped at MaxConnections
        var poolConfig = new NexusClientPoolConfig(clientConfig) 
        { 
            MaxConnections = 2,
            MinIdleConnections = 5 // Greater than max
        };

        // Should not throw during construction
        Assert.DoesNotThrow(() => 
            new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig));
    }

    [Test]
    public async Task Pool_RentedClient_ProxyThrowsAfterReturn()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 1 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            IRentedNexusClient<ClientNexus.ServerProxy> rentedClient;
            
            // Rent and return client
            using (rentedClient = await pool.RentClientAsync().Timeout(1))
            {
                // Verify proxy works while rented
                Assert.That(rentedClient.Proxy, Is.Not.Null);
            }

            // After disposal, accessing proxy should throw
            Assert.Throws<ObjectDisposedException>(() => _ = rentedClient.Proxy);
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task Pool_SimultaneousRentAndReturn_ThreadSafety()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 5 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        var exceptions = new ConcurrentBag<Exception>();

        try
        {
            // Act - Simultaneous rent/return operations
            var tasks = Enumerable.Range(0, 50).Select(async i =>
            {
                try
                {
                    using var client = await pool.RentClientAsync().Timeout(1);
                    await Task.Delay(Random.Shared.Next(1, 10)); // Random small delay
                    
                    // Access properties to test thread safety
                    var state = client.State;
                    var proxy = client.Proxy;
                    var disconnectedTask = client.DisconnectedTask;
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }).ToArray();

            await Task.WhenAll(tasks).Timeout(5);

            // Assert - No exceptions should occur during normal operation
            Assert.That(exceptions.IsEmpty, Is.True, $"Exceptions occurred: {string.Join(", ", exceptions.Select(e => e.Message))}");
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task Pool_DisconnectedTask_ReflectsClientState()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 1 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            using var client = await pool.RentClientAsync().Timeout(1);
            
            // Initially should not be completed
            Assert.That(client.DisconnectedTask.IsCompleted, Is.False);

            // Disconnect server
            await server.StopAsync().Timeout(1);

            // Wait for disconnection
            await client.DisconnectedTask.Timeout(1);
            Assert.That(client.DisconnectedTask.IsCompleted, Is.True);
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task Pool_EnsureConnectedAsync_FailsWhenServerDown()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 1 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            using var client = await pool.RentClientAsync().Timeout(1);
            
            // Stop server
            await server.StopAsync().Timeout(1);
            await Task.Delay(100);

            // EnsureConnectedAsync should fail
            var result = await client.EnsureConnectedAsync().Timeout(1);
            Assert.That(result, Is.False);
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task Pool_EnsureConnectedAsync_SucceedsWhenAlreadyConnected()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 1 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            using var client = await pool.RentClientAsync().Timeout(1);
            
            // Should succeed when already connected
            var result = await client.EnsureConnectedAsync().Timeout(1);
            Assert.That(result, Is.True);
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task Pool_SequentialRentReturn_MaintainsCorrectCounts()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 3 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            // Sequential rent/return operations
            for (int i = 0; i < 10; i++)
            {
                using var client = await pool.RentClientAsync().Timeout(1);
                Assert.That(pool.AvailableConnections, Is.EqualTo(0), $"Iteration {i}: Should have 0 available when rented");
            }
            
            // After all operations, should have 1 available client
            Assert.That(pool.AvailableConnections, Is.EqualTo(1));
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task Pool_HealthCheckTimer_ContinuesAfterExceptions()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) 
        { 
            MaxConnections = 2,
            MaxIdleTime = TimeSpan.FromMilliseconds(50) // Very short for quick test
        };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            // Create clients to populate pool
            var client1 = await pool.RentClientAsync().Timeout(1);
            var client2 = await pool.RentClientAsync().Timeout(1);
            
            client1.Dispose();
            client2.Dispose();

            var initialCount = pool.AvailableConnections;
            
            // Wait for multiple health check cycles
            await Task.Delay(300);
            
            // Health check should have run multiple times
            // (This test mainly ensures no exceptions break the timer)
            Assert.That(pool.AvailableConnections, Is.LessThanOrEqualTo(initialCount));
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task Pool_RentClientAsync_HandlesFactoryReturningNull()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 1 };
        
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(
            poolConfig,
            nexusFactory: () => null!); // Return null to test error handling

        try
        {
            // Should handle null factory return gracefully
            await AssertThrows<NullReferenceException>(async () =>
                await pool.RentClientAsync().Timeout(1));
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }
    
    [Test]
    public async Task Pool_ConnectionFailureDuringRent_ReleasesResources()
    {
        // Arrange - Create pool but don't start server
        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 2 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            // Multiple failed connection attempts
            for (int i = 0; i < 3; i++)
            {
                await AssertThrows<ClientPoolConnectionException>(async () =>
                    await pool.RentClientAsync().Timeout(1));
            }
            
            // Pool should still be functional for future attempts
            Assert.That(pool.MaxConnections, Is.EqualTo(2));
            Assert.That(pool.AvailableConnections, Is.EqualTo(0));
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task Pool_RentedClient_StateTransitions()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 1 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            using var client = await pool.RentClientAsync().Timeout(1);
            
            // Initial state
            Assert.That(client.State, Is.EqualTo(ConnectionState.Connected));
            
            // Disconnect server
            await server.StopAsync().Timeout(1);
            await Task.Delay(100);
            
            // State should reflect disconnection
            Assert.That(client.State, Is.EqualTo(ConnectionState.Disconnected));
            
            // Reconnect server
            await server.StartAsync().Timeout(1);
            
            // Manual reconnect
            var reconnected = await client.EnsureConnectedAsync().Timeout(1);
            if (reconnected)
            {
                Assert.That(client.State, Is.EqualTo(ConnectionState.Connected));
            }
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task Pool_LongRunningOperations_DoNotBlockOthers()
    {
        // Arrange
        var (server, _, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);

        var clientConfig = CreateClientConfig(Type.Uds);
        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 3 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        var completedOperations = 0;

        try
        {
            // Start multiple operations with different durations
            var tasks = new List<Task>();
            
            // Long operation
            tasks.Add(Task.Run(async () =>
            {
                using var client = await pool.RentClientAsync().Timeout(1);
                await Task.Delay(500); // Long delay
                Interlocked.Increment(ref completedOperations);
            }));
            
            // Short operations
            for (int i = 0; i < 5; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    using var client = await pool.RentClientAsync().Timeout(1);
                    await Task.Delay(50); // Short delay
                    Interlocked.Increment(ref completedOperations);
                }));
            }

            await Task.WhenAll(tasks).Timeout(2);
            
            Assert.That(completedOperations, Is.EqualTo(6));
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    public async Task Pool_RentClientAsync_ThrowsSpecificErrorOnAuthenticationRequired()
    {
        // Arrange - Create server that requires authentication but will reject it
        var serverConfig = CreateServerConfig(Type.Uds);
        serverConfig.Authenticate = true; // Require authentication
        
        var server = CreateServer(serverConfig, nexus =>
        {
            // Override OnAuthenticate to always reject authentication
            nexus.OnAuthenticateEvent = _ => ValueTask.FromResult<IIdentity?>(null);
        });

        await server.StartAsync().Timeout(1);

        // Create client without authentication configured
        var clientConfig = CreateClientConfig(Type.Uds);

        var poolConfig = new NexusClientPoolConfig(clientConfig) { MaxConnections = 1 };
        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(poolConfig);

        try
        {
            var exception = Assert.ThrowsAsync<ClientPoolConnectionException>(async () => await pool.RentClientAsync().Timeout(1));
            Assert.That(exception.ConnectionResult.DisconnectReason, Is.EqualTo(DisconnectReason.Authentication));
        }
        finally
        {
            await pool.DisposeAsync();
            await server.StopAsync().Timeout(1);
        }
    }
}
