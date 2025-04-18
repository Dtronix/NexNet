﻿using MemoryPack;
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
        clientConfig.InternalOnSend= (_, bytes) =>
        {
            var message = MemoryPackSerializer.Deserialize<ClientGreetingMessage>(new ReadOnlySpan<byte>(bytes).Slice(3));

            if (message!.AuthenticationToken!.Span[0] == 123)
            {
                tcs.SetResult();
                return;
            }
            tcs.SetException(new Exception("Client didn't send token"));
        };
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

        clientConfig.InternalOnSend = (_, bytes) =>
        {
            if (bytes.Length == 1 && bytes[0] == (int)MessageType.Ping)
                tcs.SetResult();
        };

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
        await Task.Delay(50);

        await server.StartAsync().Timeout(1);

        await tcs.Task.Timeout(5);
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

        await client.ConnectAsync().Timeout(1);
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
        await client.ConnectAsync().Timeout(1);
        await client.DisconnectedTask.Timeout(1);

        Assert.That(client.State, Is.EqualTo(ConnectionState.Disconnected));
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task DisconnectTaskCompletesAfterDisconnect(Type type)
    {
        var (server, serverHub, client, clientHub) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        serverHub.OnAuthenticateEvent = hub => ValueTask.FromResult<IIdentity?>(null);

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await server.StopAsync();

        await Task.Delay(1000);

        await client.DisconnectedTask.Timeout(1);

        Assert.That(client.State, Is.EqualTo(ConnectionState.Disconnected));
    }
}
