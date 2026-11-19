using System;
using System.Net.Sockets;
using System.Net.WebSockets;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.RateLimiting;
using NexNet.Transports;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Security;

/// <summary>
/// Integration tests for connection rate limiting across different transport types.
/// </summary>
internal class ConnectionRateLimitingIntegrationTests : BaseTests
{
    /// <summary>
    /// Helper to assert that a client connection fails or gets disconnected.
    /// When rate limiting rejects a connection, it may either throw an exception
    /// or the connection may complete but then get disconnected immediately.
    /// </summary>
    private static async Task AssertConnectionRejected(NexusClient<ClientNexus, ClientNexus.ServerProxy> client)
    {
        try
        {
            await client.ConnectAsync().Timeout(1);
            // If no exception, wait a bit and check state
            await Task.Delay(100);
            await Assert.That(client.State).IsNotEqualTo(ConnectionState.Connected).Because("Connection should not remain connected when rate limited");
        }
        catch (Exception ex) when (ex is TransportException or IOException or SocketException or OperationCanceledException or WebSocketException or TimeoutException)
        {
            // Expected - connection was rejected (may timeout if server closes before handshake completes)
        }
    }

    [Test]
    [Transports(Type.Tcp, Type.TcpTls)]
    public async Task Server_RejectsConnectionsOverGlobalLimit(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.RateLimiting = new ConnectionRateLimitConfig
        {
            MaxConcurrentConnections = 2
        };

        var connectCount = 0;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var server = CreateServer(serverConfig, nx =>
        {
            nx.OnConnectedEvent = _ =>
            {
                if (Interlocked.Increment(ref connectCount) >= 2)
                    tcs.TrySetResult();
                return ValueTask.CompletedTask;
            };
        });
        await server.StartAsync().Timeout(1);

        // First two connections succeed
        var (client1, _) = CreateClient(CreateClientConfig(type));
        var (client2, _) = CreateClient(CreateClientConfig(type));

        await client1.ConnectAsync().Timeout(1);
        await client2.ConnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);

        await Assert.That(client1.State).IsEqualTo(ConnectionState.Connected);
        await Assert.That(client2.State).IsEqualTo(ConnectionState.Connected);

        // Third connection should fail
        var (client3, _) = CreateClient(CreateClientConfig(type));
        await AssertConnectionRejected(client3);
    }

    [Test]
    [Transports(Type.Tcp, Type.TcpTls)]
    public async Task Server_ReleasesSlotOnDisconnect(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.RateLimiting = new ConnectionRateLimitConfig
        {
            MaxConcurrentConnections = 1
        };

        var server = CreateServer(serverConfig, null);
        await server.StartAsync().Timeout(1);

        var (client1, _) = CreateClient(CreateClientConfig(type));
        await client1.ConnectAsync().Timeout(1);

        var disconnectedTask = client1.DisconnectedTask;
        await client1.DisconnectAsync().Timeout(1);
        await disconnectedTask.Timeout(1);

        // Wait for disconnect to propagate to server
        await Task.Delay(100);

        // New connection should succeed after disconnect
        var (client2, _) = CreateClient(CreateClientConfig(type));
        await client2.ConnectAsync().Timeout(1);

        await Assert.That(client2.State).IsEqualTo(ConnectionState.Connected);
    }

    [Test]
    [Transports(Type.Tcp, Type.TcpTls)]
    public async Task Server_PerIpLimit_BlocksSameIp(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.RateLimiting = new ConnectionRateLimitConfig
        {
            MaxConnectionsPerIp = 1,
            MaxConcurrentConnections = 100 // High global limit
        };

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var server = CreateServer(serverConfig, nx =>
        {
            nx.OnConnectedEvent = _ =>
            {
                tcs.TrySetResult();
                return ValueTask.CompletedTask;
            };
        });
        await server.StartAsync().Timeout(1);

        // First connection from localhost succeeds
        var (client1, _) = CreateClient(CreateClientConfig(type));
        await client1.ConnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);
        await Assert.That(client1.State).IsEqualTo(ConnectionState.Connected);

        // Second connection from same IP should fail
        var (client2, _) = CreateClient(CreateClientConfig(type));
        await AssertConnectionRejected(client2);
    }

    [Test]
    [Transports(Type.Uds)]
    public async Task Server_UdsSkipsPerIpLimits(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.RateLimiting = new ConnectionRateLimitConfig
        {
            MaxConnectionsPerIp = 1,
            MaxConcurrentConnections = 100
        };

        var connectCount = 0;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var server = CreateServer(serverConfig, nx =>
        {
            nx.OnConnectedEvent = _ =>
            {
                if (Interlocked.Increment(ref connectCount) >= 2)
                    tcs.TrySetResult();
                return ValueTask.CompletedTask;
            };
        });
        await server.StartAsync().Timeout(1);

        // UDS connections should not be subject to per-IP limits
        var (client1, _) = CreateClient(CreateClientConfig(type));
        var (client2, _) = CreateClient(CreateClientConfig(type));

        await client1.ConnectAsync().Timeout(1);
        await client2.ConnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);

        await Assert.That(client1.State).IsEqualTo(ConnectionState.Connected);
        await Assert.That(client2.State).IsEqualTo(ConnectionState.Connected);
    }

    [Test]
    [Transports(Type.Tcp)]
    public async Task Server_NoRateLimiting_AllowsUnlimitedConnections(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        // RateLimiting is null by default

        var connectCount = 0;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var server = CreateServer(serverConfig, nx =>
        {
            nx.OnConnectedEvent = _ =>
            {
                if (Interlocked.Increment(ref connectCount) >= 5)
                    tcs.TrySetResult();
                return ValueTask.CompletedTask;
            };
        });
        await server.StartAsync().Timeout(1);

        var clients = new List<NexusClient<ClientNexus, ClientNexus.ServerProxy>>();

        // Create multiple connections
        for (int i = 0; i < 5; i++)
        {
            var (client, _) = CreateClient(CreateClientConfig(type));
            await client.ConnectAsync().Timeout(1);
            clients.Add(client);
        }

        await tcs.Task.Timeout(2);

        // All should be connected
        foreach (var client in clients)
        {
            await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
        }
    }

    [Test]
    [Transports(Type.Tcp)]
    public async Task Server_DisabledRateLimiting_AllowsUnlimitedConnections(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.RateLimiting = new ConnectionRateLimitConfig(); // All zeros = disabled

        var connectCount = 0;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var server = CreateServer(serverConfig, nx =>
        {
            nx.OnConnectedEvent = _ =>
            {
                if (Interlocked.Increment(ref connectCount) >= 5)
                    tcs.TrySetResult();
                return ValueTask.CompletedTask;
            };
        });
        await server.StartAsync().Timeout(1);

        var clients = new List<NexusClient<ClientNexus, ClientNexus.ServerProxy>>();

        // Create multiple connections
        for (int i = 0; i < 5; i++)
        {
            var (client, _) = CreateClient(CreateClientConfig(type));
            await client.ConnectAsync().Timeout(1);
            clients.Add(client);
        }

        await tcs.Task.Timeout(2);

        // All should be connected
        foreach (var client in clients)
        {
            await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
        }
    }

    [Test]
    [Transports(Type.Tcp)]
    public async Task Server_RateLimiter_TracksStatistics(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        var rateLimitConfig = new ConnectionRateLimitConfig
        {
            MaxConcurrentConnections = 10
        };
        serverConfig.RateLimiting = rateLimitConfig;

        // Create rate limiter to track stats (server creates its own, but we can verify the config)
        using var limiter = new ConnectionRateLimiter(rateLimitConfig, TimeProvider.System);

        // Acquire some connections
        limiter.TryAcquire("127.0.0.1");
        limiter.TryAcquire("127.0.0.2");

        var stats = limiter.GetStats();

        await Assert.That(stats.CurrentConnections).IsEqualTo(2);
        await Assert.That(stats.TotalAccepted).IsEqualTo(2);
        await Assert.That(stats.UniqueIpCount).IsEqualTo(2);
    }

    [Test]
    [Transports(Type.Tcp, Type.TcpTls)]
    public async Task Server_MultipleDisconnects_ReleasesAllSlots(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.RateLimiting = new ConnectionRateLimitConfig
        {
            MaxConcurrentConnections = 2
        };

        var connectCount = 0;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var server = CreateServer(serverConfig, nx =>
        {
            nx.OnConnectedEvent = _ =>
            {
                if (Interlocked.Increment(ref connectCount) >= 2)
                    tcs.TrySetResult();
                return ValueTask.CompletedTask;
            };
        });
        await server.StartAsync().Timeout(1);

        // Connect two clients
        var (client1, _) = CreateClient(CreateClientConfig(type));
        var (client2, _) = CreateClient(CreateClientConfig(type));

        await client1.ConnectAsync().Timeout(1);
        await client2.ConnectAsync().Timeout(1);
        await tcs.Task.Timeout(1);

        // Disconnect both
        var disconnectedTask1 = client1.DisconnectedTask;
        var disconnectedTask2 = client2.DisconnectedTask;

        await client1.DisconnectAsync().Timeout(1);
        await client2.DisconnectAsync().Timeout(1);

        await disconnectedTask1.Timeout(1);
        await disconnectedTask2.Timeout(1);

        await Task.Delay(100);

        // Should be able to connect two new clients
        var (client3, _) = CreateClient(CreateClientConfig(type));
        var (client4, _) = CreateClient(CreateClientConfig(type));

        await client3.ConnectAsync().Timeout(1);
        await client4.ConnectAsync().Timeout(1);

        await Assert.That(client3.State).IsEqualTo(ConnectionState.Connected);
        await Assert.That(client4.State).IsEqualTo(ConnectionState.Connected);
    }

    [Test]
    [Transports(Type.Uds)]
    public async Task Server_UdsGlobalLimit_StillApplies(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.RateLimiting = new ConnectionRateLimitConfig
        {
            MaxConcurrentConnections = 2
        };

        var connectCount = 0;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var server = CreateServer(serverConfig, nx =>
        {
            nx.OnConnectedEvent = _ =>
            {
                if (Interlocked.Increment(ref connectCount) >= 2)
                    tcs.TrySetResult();
                return ValueTask.CompletedTask;
            };
        });
        await server.StartAsync().Timeout(1);

        // First two connections succeed
        var (client1, _) = CreateClient(CreateClientConfig(type));
        var (client2, _) = CreateClient(CreateClientConfig(type));

        await client1.ConnectAsync().Timeout(1);
        await client2.ConnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);

        await Assert.That(client1.State).IsEqualTo(ConnectionState.Connected);
        await Assert.That(client2.State).IsEqualTo(ConnectionState.Connected);

        // Third connection should fail - global limit applies to UDS too
        var (client3, _) = CreateClient(CreateClientConfig(type));
        await AssertConnectionRejected(client3);
    }

    #region ASP.NET WebSocket and HttpSocket Tests

    [Test]
    [Transports(Type.WebSocket, Type.HttpSocket)]
    public async Task AspServer_RejectsConnectionsOverGlobalLimit(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.RateLimiting = new ConnectionRateLimitConfig
        {
            MaxConcurrentConnections = 2
        };

        var connectCount = 0;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var server = CreateServer(serverConfig, nx =>
        {
            nx.OnConnectedEvent = _ =>
            {
                if (Interlocked.Increment(ref connectCount) >= 2)
                    tcs.TrySetResult();
                return ValueTask.CompletedTask;
            };
        });
        await server.StartAsync().Timeout(1);

        // First two connections succeed
        var (client1, _) = CreateClient(CreateClientConfig(type));
        var (client2, _) = CreateClient(CreateClientConfig(type));

        await client1.ConnectAsync().Timeout(1);
        await client2.ConnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);

        await Assert.That(client1.State).IsEqualTo(ConnectionState.Connected);
        await Assert.That(client2.State).IsEqualTo(ConnectionState.Connected);

        // Third connection should fail
        var (client3, _) = CreateClient(CreateClientConfig(type));
        await AssertConnectionRejected(client3);
    }

    [Test]
    [Transports(Type.WebSocket, Type.HttpSocket)]
    public async Task AspServer_ReleasesSlotOnDisconnect(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.RateLimiting = new ConnectionRateLimitConfig
        {
            MaxConcurrentConnections = 1
        };

        var server = CreateServer(serverConfig, null);
        await server.StartAsync().Timeout(1);

        var (client1, _) = CreateClient(CreateClientConfig(type));
        await client1.ConnectAsync().Timeout(1);

        var disconnectedTask = client1.DisconnectedTask;
        await client1.DisconnectAsync().Timeout(1);
        await disconnectedTask.Timeout(1);

        // Wait for disconnect to propagate to server
        await Task.Delay(200);

        // New connection should succeed after disconnect
        var (client2, _) = CreateClient(CreateClientConfig(type));
        await client2.ConnectAsync().Timeout(1);

        await Assert.That(client2.State).IsEqualTo(ConnectionState.Connected);
    }

    [Test]
    [Transports(Type.WebSocket, Type.HttpSocket)]
    public async Task AspServer_PerIpLimit_BlocksSameIp(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.RateLimiting = new ConnectionRateLimitConfig
        {
            MaxConnectionsPerIp = 1,
            MaxConcurrentConnections = 100 // High global limit
        };

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var server = CreateServer(serverConfig, nx =>
        {
            nx.OnConnectedEvent = _ =>
            {
                tcs.TrySetResult();
                return ValueTask.CompletedTask;
            };
        });
        await server.StartAsync().Timeout(1);

        // First connection from localhost succeeds
        var (client1, _) = CreateClient(CreateClientConfig(type));
        await client1.ConnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);
        await Assert.That(client1.State).IsEqualTo(ConnectionState.Connected);

        // Second connection from same IP should fail
        var (client2, _) = CreateClient(CreateClientConfig(type));
        await AssertConnectionRejected(client2);
    }

    [Test]
    [Transports(Type.WebSocket, Type.HttpSocket)]
    public async Task AspServer_NoRateLimiting_AllowsUnlimitedConnections(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        // RateLimiting is null by default

        var connectCount = 0;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var server = CreateServer(serverConfig, nx =>
        {
            nx.OnConnectedEvent = _ =>
            {
                if (Interlocked.Increment(ref connectCount) >= 5)
                    tcs.TrySetResult();
                return ValueTask.CompletedTask;
            };
        });
        await server.StartAsync().Timeout(1);

        var clients = new List<NexusClient<ClientNexus, ClientNexus.ServerProxy>>();

        // Create multiple connections
        for (int i = 0; i < 5; i++)
        {
            var (client, _) = CreateClient(CreateClientConfig(type));
            await client.ConnectAsync().Timeout(1);
            clients.Add(client);
        }

        await tcs.Task.Timeout(2);

        // All should be connected
        foreach (var client in clients)
        {
            await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
        }
    }

    [Test]
    [Transports(Type.WebSocket, Type.HttpSocket)]
    public async Task AspServer_MultipleDisconnects_ReleasesAllSlots(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.RateLimiting = new ConnectionRateLimitConfig
        {
            MaxConcurrentConnections = 2
        };

        var connectCount = 0;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var server = CreateServer(serverConfig, nx =>
        {
            nx.OnConnectedEvent = _ =>
            {
                if (Interlocked.Increment(ref connectCount) >= 2)
                    tcs.TrySetResult();
                return ValueTask.CompletedTask;
            };
        });
        await server.StartAsync().Timeout(1);

        // Connect two clients
        var (client1, _) = CreateClient(CreateClientConfig(type));
        var (client2, _) = CreateClient(CreateClientConfig(type));

        await client1.ConnectAsync().Timeout(1);
        await client2.ConnectAsync().Timeout(1);
        await tcs.Task.Timeout(1);

        // Disconnect both
        var disconnectedTask1 = client1.DisconnectedTask;
        var disconnectedTask2 = client2.DisconnectedTask;

        await client1.DisconnectAsync().Timeout(1);
        await client2.DisconnectAsync().Timeout(1);

        await disconnectedTask1.Timeout(1);
        await disconnectedTask2.Timeout(1);

        await Task.Delay(200);

        // Should be able to connect two new clients
        var (client3, _) = CreateClient(CreateClientConfig(type));
        var (client4, _) = CreateClient(CreateClientConfig(type));

        await client3.ConnectAsync().Timeout(1);
        await client4.ConnectAsync().Timeout(1);

        await Assert.That(client3.State).IsEqualTo(ConnectionState.Connected);
        await Assert.That(client4.State).IsEqualTo(ConnectionState.Connected);
    }

    #endregion
}