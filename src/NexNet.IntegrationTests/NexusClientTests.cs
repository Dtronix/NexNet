using MemoryPack;
using NexNet.Internals;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Transports;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexusClientTests : BaseTests
{
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task NexusFiresOnConnected(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, _, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        clientNexus.OnConnectedEvent = (_, _) =>
        {
            tcs.SetResult();
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ConnectsToServer(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, _, client, _) = CreateServerClient(
            CreateServerConfig(type),
            clientConfig);


        clientConfig.InternalOnClientConnect = () => tcs.SetResult();

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientFailsGracefullyWithNoServer(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var (_, _, client, _, _, _) = CreateServerClientWithStoppedServer(
            CreateServerConfig(type),
            clientConfig);

        clientConfig.ConnectionTimeout = 100;

        await AssertThrows<TransportException>(() => client.ConnectAsync().Timeout(10));
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientTimesOutWithNoServer(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var (_, _, client, _, _, _) = CreateServerClientWithStoppedServer(
            CreateServerConfig(type),
            clientConfig);

        clientConfig.ConnectionTimeout = 50;

        await AssertThrows<TransportException>(() => client.ConnectAsync().Timeout(100));
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ConnectsAndDisconnectsMultipleTimesFromServer(Type type)
    {
        var (server, _, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        await server.StartAsync().Timeout(1);

        for (int i = 0; i < 5; i++)
        {
            await client.ConnectAsync().Timeout(1);
            var disconnected = client.DisconnectedTask;
            await client.DisconnectAsync().Timeout(1);
            await disconnected.Timeout(1);
        }
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ConnectTimesOutWithNoServer(Type type)
    {
        var (_, _, client, _, _, _) = CreateServerClientWithStoppedServer(
            CreateServerConfig(type),
            CreateClientConfig(type));

        await AssertThrows<TransportException>(async () => await client.ConnectAsync());
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientProvidesAuthenticationToken(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, _, client, _) = CreateServerClient(
            CreateServerConfig(type),
            clientConfig);

        await server.StartAsync().Timeout(1);

        clientConfig.Authenticate = () => new byte[] { 123 };
        FireOnSend(clientConfig, (_, bytes) =>
        {
            var message = MemoryPackSerializer.Deserialize<ClientGreetingMessage>(new ReadOnlySpan<byte>(bytes).Slice(3));

            if (message!.AuthenticationToken!.Span[0] == 123)
            {
                tcs.SetResult();
                return;
            }
            tcs.SetException(new Exception("Client didn't send token"));
        });
        await client.ConnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }


    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientSendsPing(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        clientConfig.PingInterval = 20;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (server, _, client, _) = CreateServerClient(
            CreateServerConfig(type),
            clientConfig);

        await server.StartAsync().Timeout(1);

        FireOnSend(clientConfig, (_, bytes) =>
        {
            if (bytes.Length == 1 && bytes[0] == (int)MessageType.Ping)
                tcs.SetResult();
        });

        await client.ConnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ReconnectsOnDisconnect(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        var (server, _, client, clientNexus) = CreateServerClient(serverConfig, clientConfig);

        clientConfig.ReconnectionPolicy = new DefaultReconnectionPolicy(new[] { TimeSpan.FromMilliseconds(20) });

        clientNexus.OnConnectedEvent = (_, isReconnected) =>
        {
            if (isReconnected)
                tcs.TrySetResult();
            return ValueTask.CompletedTask;
        };

        serverConfig.InternalNoLingerOnShutdown = true;
        serverConfig.InternalForceDisableSendingDisconnectSignal = true;

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        await server.StopAsync();
        
        // Wait for the client to process the disconnect.
        await Task.Delay(100);
        await server.StartAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }
    
    /*
     * Figure out a way to test a ASP server that suddenly shuts down.  This has been tested to work.
    
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ReconnectsOnDisconnectAsp(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientConfig = CreateClientConfig(type, BasePipeTests.LogMode.Always);
        clientConfig.Timeout = 1000;
        var serverConfig = CreateServerConfig(type, BasePipeTests.LogMode.Always);
        var (server, _, client, clientNexus, startAspServer, stopAspServer) = CreateServerClientWithStoppedServer(serverConfig, clientConfig);

        clientConfig.ReconnectionPolicy = new DefaultReconnectionPolicy(new[] { TimeSpan.FromMilliseconds(20) });

        clientNexus.OnConnectedEvent = (_, isReconnected) =>
        {
            if (isReconnected)
                tcs.TrySetResult();
            return ValueTask.CompletedTask;
        };

        serverConfig.InternalNoLingerOnShutdown = true;
        serverConfig.InternalForceDisableSendingDisconnectSignal = true;
        startAspServer.Invoke();
        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        stopAspServer.Invoke();

        // Wait for the client to process the disconnect.
        await Task.Delay(2000);
        
        (server, _, _, _, startAspServer, _) = CreateServerClientWithStoppedServer(serverConfig, clientConfig);
        
        startAspServer.Invoke();
        await server.StartAsync().Timeout(1);
        
        
        await tcs.Task.Timeout(5);
    }*/
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ReconnectsOnTimeout(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        var (server, _, client, clientNexus) = CreateServerClient(serverConfig, clientConfig);

        clientConfig.ReconnectionPolicy = new DefaultReconnectionPolicy(new[] { TimeSpan.FromMilliseconds(20) });

        clientNexus.OnConnectedEvent = (_, isReconnected) =>
        {
            if (isReconnected)
                tcs.TrySetResult();
            return ValueTask.CompletedTask;
        };

        serverConfig.InternalNoLingerOnShutdown = true;
        serverConfig.InternalForceDisableSendingDisconnectSignal = true;
        clientConfig.PingInterval = 75;
        clientConfig.Timeout = 50;

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ReconnectsNotifiesReconnecting(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        clientConfig.ReconnectionPolicy = new DefaultReconnectionPolicy();
        var (server, _, client, clientNexus) = CreateServerClient(serverConfig, clientConfig);

        clientNexus.OnReconnectingEvent = _ =>
        {
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        };

        serverConfig.InternalNoLingerOnShutdown = true;
        serverConfig.InternalForceDisableSendingDisconnectSignal = true;
        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        await server.StopAsync();
        await tcs.Task.Timeout(1000);
    }
    
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ReconnectsNotifiesReconnecting_Hosted(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        clientConfig.ReconnectionPolicy = new DefaultReconnectionPolicy();
        var (server, _, client, clientNexus, startAspServer, stopAspServer) = CreateServerClientWithStoppedServer(serverConfig, clientConfig);

        clientNexus.OnReconnectingEvent = _ =>
        {
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        };

        serverConfig.InternalNoLingerOnShutdown = true;
        serverConfig.InternalForceDisableSendingDisconnectSignal = true;
        startAspServer.Invoke();
        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        stopAspServer.Invoke();
        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ReconnectsStopsAfterSpecifiedTimes(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        var (server, _, client, clientNexus) = CreateServerClient(serverConfig, clientConfig);

        clientConfig.ConnectionTimeout = 100;

        clientConfig.ReconnectionPolicy = new DefaultReconnectionPolicy(new[]
        {
            TimeSpan.FromMilliseconds(20)
        }, false);


        clientNexus.OnDisconnectedEvent = _ =>
        {
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        };

        serverConfig.InternalNoLingerOnShutdown = true;
        serverConfig.InternalForceDisableSendingDisconnectSignal = true;

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await Task.Delay(100);
        await server.StopAsync();

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientProxyInvocationCancelsOnDisconnect(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (server, serverNexus, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        serverNexus.ServerTaskValueEvent = async _ =>
        {
            await Task.Delay(100000);
            return 12345;
        };

        clientNexus.OnConnectedEvent = async (_, _) =>
        {
            try
            {
                await client.Proxy.ServerTaskValue();
            }
            catch (TaskCanceledException)
            {
                tcs.TrySetResult();
            }
            catch (Exception)
            {
                // ignored
            }
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await Task.Delay(100);
        await server.StopAsync();
        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ReadyTaskCompletesUponConnection(Type type)
    {
        var (server, serverHub, client, clientHub) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        await server.StartAsync().Timeout(1);

        await client.ConnectAsync().Timeout(1);
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ReadyTaskCompletesUponAuthentication(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.Authenticate = true;
        
        bool authCompleted = false;
        var (server, serverHub, client, clientHub) = CreateServerClient(
            serverConfig,
            CreateClientConfig(type));

        serverHub.OnAuthenticateEvent = hub =>
        {
            authCompleted = true;
            return ValueTask.FromResult<IIdentity?>(new DefaultIdentity());
        };

        await server.StartAsync().Timeout(1);

        await client.ConnectAsync().Timeout(1);
        Assert.That(authCompleted, Is.True);
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ReadyTaskCompletesUponAuthFailure(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.Authenticate = true;

        var (server, serverHub, client, clientHub) = CreateServerClient(
            serverConfig,
            CreateClientConfig(type));

        serverHub.OnAuthenticateEvent = hub => ValueTask.FromResult<IIdentity?>(null);

        await server.StartAsync().Timeout(1);

        await client.TryConnectAsync().Timeout(1);
        Assert.That(client.State, Is.EqualTo(ConnectionState.Disconnected));
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task DisconnectTaskCompletesUponDisconnection(Type type)
    {
        var (server, serverHub, client, clientHub) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        serverHub.OnAuthenticateEvent = hub => ValueTask.FromResult<IIdentity?>(null);

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        var disconnectTask = client.DisconnectedTask;

        await server.StopAsync();

        await disconnectTask.Timeout(1);

        Assert.That(client.State, Is.EqualTo(ConnectionState.Disconnected));
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task DisconnectTaskCompletesUponAuthFailure(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.Authenticate = true;

        var (server, serverHub, client, clientHub) = CreateServerClient(
            serverConfig,
            CreateClientConfig(type));

        serverHub.OnAuthenticateEvent = hub => ValueTask.FromResult<IIdentity?>(null);

        await server.StartAsync().Timeout(1);
        await client.TryConnectAsync().Timeout(1);
        await client.DisconnectedTask.Timeout(1);

        Assert.That(client.State, Is.EqualTo(ConnectionState.Disconnected));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task DisconnectTaskCompletesAfterServerStops(Type type)
    {
        // Arrange
        var serverConfig = CreateServerConfig(type);
        var clientConfig = CreateClientConfig(type);
        var (server, serverHub, client, _) = CreateServerClient(serverConfig, clientConfig);

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        // Act
        await server.StopAsync().Timeout(1);

        // Assert: client.DisconnectedTask will complete promptly
        await client.DisconnectedTask.Timeout(1);
        Assert.That(client.State, Is.EqualTo(ConnectionState.Disconnected));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientSendsDisconnectSignal(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientConfig = CreateClientConfig(type);
        clientConfig.InternalOnSend = (_, bytes) =>
        {
            // first byte is the message type
            if (bytes.Length == 1 && bytes[0] == (int)MessageType.DisconnectGraceful)
                tcs.SetResult();
        };

        var (server, _, client, _) = CreateServerClient(
            CreateServerConfig(type),
            clientConfig);

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        await client.DisconnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task FiresOnDisconnectedEvent(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, _, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        clientNexus.OnDisconnectedEvent = _ =>
        {
            tcs.SetResult();
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        await client.DisconnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ProxyInvocationPropagatesServerException(Type type)
    {
        var (server, serverNexus, client, _) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        // Server throws
        serverNexus.ServerTaskValueEvent = _ => throw new InvalidOperationException("boom");

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        Assert.ThrowsAsync<ProxyRemoteInvocationException>(
            () => client.Proxy.ServerTaskValue().Timeout(1));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public void DisconnectWithoutConnectDoesNotThrow(Type type)
    {
        var (_, _, client, _) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));
        
        // Should complete without throwing
        Assert.DoesNotThrowAsync(async () => await client.DisconnectAsync().Timeout(1));
        Assert.That(client.State, Is.EqualTo(ConnectionState.Disconnected));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task DoubleConnectDoesNotThrow(Type type)
    {
        var (server, _, client, _) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        // Second ConnectAsync should throw
        await client.ConnectAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task DoubleDisconnectDoesNotThrow(Type type)
    {
        var (server, _, client, _) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        
        Assert.That(client.State, Is.EqualTo(ConnectionState.Connected));
        // First disconnect
        await client.DisconnectAsync().Timeout(1);
        
        // Second disconnect should be a no-op
        Assert.DoesNotThrowAsync(async () => await client.DisconnectAsync().Timeout(1));
        Assert.That(client.State, Is.EqualTo(ConnectionState.Disconnected));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ConcurrentProxyInvocations(Type type)
    {
        var (server, serverNexus, client, _) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        // Server always returns 42
        serverNexus.ServerTaskValueEvent = _ => ValueTask.FromResult(42);

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        // Fire off 20 simultaneous calls
        var calls = Enumerable.Range(0, 20)
            .Select(_ => client.Proxy.ServerTaskValue().AsTask())
            .ToArray();

        var results = await Task.WhenAll(calls).Timeout(1);
        Assert.That(results, Has.Exactly(20).EqualTo(42));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ServerRejectsAuth_SendsDisconnect(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.Authenticate = true;
        // Capture what the server sends
        byte? sentMessageType = null;
        serverConfig.InternalOnSend = (_, bytes) =>
        {
            // First byte is the message type
            sentMessageType = bytes.Length > 0 ? (byte?)bytes[0] : null;
        };

        var clientConfig = CreateClientConfig(type);
        var (server, serverHub, client, clientHub) = CreateServerClient(serverConfig, clientConfig);

        // Force authentication to fail
        serverHub.OnAuthenticateEvent = _ => ValueTask.FromResult<IIdentity?>(null);

        await server.StartAsync().Timeout(1);
        await client.TryConnectAsync().Timeout(1);

        // Give it a moment to send the disconnect
        await Task.Delay(50);

        Assert.That(sentMessageType, Is.EqualTo((byte)MessageType.DisconnectAuthentication),
            "Server should send a Disconnect message when auth is rejected");
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ProxyInvocationAfterDisconnectThrows(Type type)
    {
        var (server, serverNexus, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        // Server will respond, but we will disconnect before calling
        serverNexus.ServerTaskValueEvent = _ => ValueTask.FromResult(1);

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        await client.DisconnectAsync().Timeout(1);

        // Any RPC after clean disconnect should throw
        await AssertThrows<InvalidOperationException>(() => client.Proxy.ServerTaskValue().Timeout(1));
    }
    
    /// <summary>
    /// If the reconnection policy is null, OnReconnectingEvent must never fire after a disconnect.
    /// </summary>
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task OnReconnectingEventNotFiredWithNoRetries(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        // Zero‐retry policy
        clientConfig.ReconnectionPolicy = null;

        var serverConfig = CreateServerConfig(type);
        // Ensure no clean‐disconnect signal is sent
        serverConfig.InternalNoLingerOnShutdown = true;
        serverConfig.InternalForceDisableSendingDisconnectSignal = true;

        var (server, _, client, clientNexus) = CreateServerClient(serverConfig, clientConfig);

        var reconnectingFired = false;
        clientNexus.OnReconnectingEvent = _ =>
        {
            reconnectingFired = true;
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        await server.StopAsync().Timeout(1);

        // Give it a moment to (not) trigger
        await Task.Delay(50);
        Assert.That(reconnectingFired, Is.False, "With a disabled reconnection policy, OnReconnectingEvent must not fire.");
    }
    
    /// <summary>
    /// Calling StartAsync twice on the same server instance should throw on the second call.
    /// </summary>
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task StartAsyncTwiceThrows(Type type)
    {
        var server = CreateServer(CreateServerConfig(type), /*listenerFactory*/ null);

        // First start should succeed
        await server.StartAsync().Timeout(1);

        // Second concurrent start should throw InvalidOperationException
        Assert.ThrowsAsync<InvalidOperationException>(
            () => server.StartAsync().Timeout(1));
    }
    
    
    private void FireOnSend(ConfigBase config, Action<INexusSession, byte[]> action, bool skipProtocolHeader = true)
    {
        var receiveCount = 0;
        config.InternalOnSend = (session, bytes) =>
        {
            if (receiveCount++ == 0)
                return;

            action(session, bytes);
        };
    }

}
