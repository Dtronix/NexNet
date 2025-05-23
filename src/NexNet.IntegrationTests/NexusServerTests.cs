using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NexNet.Transports;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexusServerTests : BaseTests
{

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task AcceptsClientConnection(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverConfig = CreateServerConfig(type);
        var (server, serverNexus, client, clientNexus) = CreateServerClient(
            serverConfig,
            CreateClientConfig(type));

        serverConfig.InternalOnConnect = () => tcs.SetResult();

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
    public async Task NexusFiresOnConnected(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, serverNexus, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        serverNexus.OnConnectedEvent = nexus =>
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
    //[TestCase(Type.WebSocket)] Can't start and stop Asp.
    //[TestCase(Type.HttpSocket)] Can't start and stop Asp.
    public async Task StartsAndStopsMultipleTimes(Type type)
    {

        var clientConfig = CreateClientConfig(type);
        var (server, _, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            clientConfig);


        for (int i = 0; i < 5; i++)
        {
            await server.StartAsync().Timeout(1);

            await client.ConnectAsync().Timeout(1);

            await server.StopAsync();

            await client.DisconnectedTask.Timeout(2);

            // Wait for the client to process the disconnect.

        }
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    //[TestCase(Type.WebSocket)] Can't start and stop Asp.
    //[TestCase(Type.HttpSocket)] Can't start and stop Asp.
    public async Task StopsAndReleasesStoppedTcs(Type type)
    {
        var (server, _, client, clientHub) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));


        Assert.That(server.StoppedTask, Is.Null);
        await server.StartAsync().Timeout(1);
        Assert.That(server.StoppedTask!.IsCompleted, Is.False);

        await server.StopAsync();

        await server.StoppedTask!.Timeout(1);
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    //[TestCase(Type.WebSocket)] Can't start and stop Asp.
    //[TestCase(Type.HttpSocket)] Can't start and stop Asp.
    public async Task ThrowsWhenServerIsAlreadyOpenOnSameTransport(Type type)
    {
        var config = CreateServerConfig(type);

        var server1 = this.CreateServer(config, null);
        var server2 = this.CreateServer(config, null);

        await server1.StartAsync();

        try
        {
            await server2.StartAsync();
        }
        catch (TransportException e)
        {
            // Quic does not return information if a UDP port is already in use or not.
            Assert.That(e.Error, Is.EqualTo(TransportError.AddressInUse));
        }
        catch (Exception e)
        {
            Assert.Fail($"Expected {nameof(TransportException)} but got {e.GetType().Name}");
        }
    }
    
    [Test]
    public void ServerThrowsWhenTransportConfigReturnsNullListenerOnWrongMode()
    {
        var server = ServerNexus.CreateServer(new CustomServerConfig(ServerConnectionMode.Listener), () => null!);
        Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync());
    }
    
    [Test]
    public void ServerDoesntThrowWhenTransportConfigReturnsNullListenerOnCorrectMode()
    {
        var server = ServerNexus.CreateServer(new CustomServerConfig(ServerConnectionMode.Receiver), () => null!);
        Assert.DoesNotThrowAsync(() => server.StartAsync());
    }
    
    [Test]
    public void ServerThrowsWhenStartingTwiceWhileAlreadyRunning()
    {
        var server = ServerNexus.CreateServer(new CustomServerConfig(ServerConnectionMode.Receiver), () => null!);
        Assert.DoesNotThrowAsync(() => server.StartAsync());
        Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync());
    }
    
    /// <summary>
    /// The server should fire its OnDisconnectedEvent when a connected client cleanly disconnects.
    /// </summary>
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ServerFiresOnDisconnectedEvent(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, serverNexus, client, _) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        serverNexus.OnDisconnectedEvent = nexus =>
        {
            tcs.SetResult();
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        await client.DisconnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }
    
    /// <summary>
    /// If authentication fails (OnAuthenticateEvent returns null), the server should immediately send a Disconnect frame.
    /// </summary>
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ServerRejectsAuthAndSendsDisconnect(Type type)
    {
        var disconnectSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverConfig = CreateServerConfig(type);
        serverConfig.Authenticate = true;
        serverConfig.InternalOnSend = (_, bytes) =>
        {
            if (bytes is [(byte)MessageType.DisconnectAuthentication])
                disconnectSent.TrySetResult();
        };

        var clientConfig = CreateClientConfig(type);
        var (server, serverHub, client, clientHub) = CreateServerClient(serverConfig, clientConfig);

        // Force authentication failure
        serverHub.OnAuthenticateEvent = _ => ValueTask.FromResult<IIdentity?>(null);

        await server.StartAsync().Timeout(1);
        await client.TryConnectAsync().Timeout(1);

        // give the server a moment to send its disconnect
        await disconnectSent.Task.Timeout(1);
    }
    
    /// <summary>
    /// Calling StopAsync on a server that was never started should throw.
    /// </summary>
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public void StopWithoutStartThrows(Type type)
    {
        var server = CreateServer(CreateServerConfig(type), /*listenerFactory*/ null);
        Assert.ThrowsAsync<InvalidOperationException>(() => server.StopAsync());
    }
    
    /// <summary>
    /// When authentication is enabled and OnAuthenticateEvent returns a non‐null identity,
    /// the server’s OnConnectedEvent should fire only after auth succeeds.
    /// </summary>
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task OnConnectedEventFiresAfterAuthentication(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.Authenticate = true;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientConfig = CreateClientConfig(type);

        var (server, serverNexus, client, _) = CreateServerClient(serverConfig, clientConfig);

        // Force authentication to succeed
        serverNexus.OnAuthenticateEvent = hub => ValueTask.FromResult<IIdentity?>(new DefaultIdentity());

        // Only fire once auth is done
        serverNexus.OnConnectedEvent = nexus =>
        {
            tcs.SetResult();
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        // Should complete only after auth succeeded
        await tcs.Task.Timeout(1);
    }
    
    [TestCase(Type.Quic, true)]
    [TestCase(Type.Uds, true)]
    [TestCase(Type.Tcp, true)]
    [TestCase(Type.TcpTls, true)]
    [TestCase(Type.WebSocket, true)]
    [TestCase(Type.HttpSocket, true)]
    [TestCase(Type.Quic, false)]
    [TestCase(Type.Uds, false)]
    [TestCase(Type.Tcp, false)]
    [TestCase(Type.TcpTls, false)]
    [TestCase(Type.WebSocket, false)]
    [TestCase(Type.HttpSocket, false)]
    public async Task ConnectAsyncThrowsAndOnConnectedNeverFiresWhenAuthHandlerThrows(Type type, bool authenticateClient)
    {
        // Arrange: force server to require auth, and make its auth handler throw
        var serverConfig = CreateServerConfig(type);
        serverConfig.Authenticate = true;
        var clientConfig = CreateClientConfig(type);
        
        if(authenticateClient)
            clientConfig.Authenticate = () => Memory<byte>.Empty;

        var (server, serverHub, client, clientNexus) =
            CreateServerClient(serverConfig, clientConfig);

        serverHub.OnAuthenticateEvent = _ => throw new InvalidOperationException("boom!");

        // Track whether the client ever thinks it's connected
        var connectedFired = false;
        clientNexus.OnConnectedEvent = (_, _) =>
        {
            connectedFired = true;
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);

        // Act & Assert: ConnectAsync should throw, and OnConnectedEvent must not fire
        await AssertThrows<TransportException>(async () =>
            await client.ConnectAsync().Timeout(1));

        Assert.That(connectedFired, Is.False, "OnConnectedEvent should not fire when authentication handler throws");
    }
    
    /// <summary>
    /// If your OnDisconnectedEvent handler throws, the server should swallow that
    /// and remain able to accept new clients afterwards.
    /// </summary>
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task OnDisconnectedEventExceptionDoesNotBreakServer(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        var clientConfig = CreateClientConfig(type);
        var (server, serverNexus, client, clientNexus) =
            CreateServerClient(serverConfig, clientConfig);

        bool first = true;
        serverNexus.OnDisconnectedEvent = nexus =>
        {
            if (first)
            {
                first = false;
                throw new InvalidOperationException("disconnect handler boom");
            }
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);

        // First cycle: handler throws, but server should stay up
        await client.ConnectAsync().Timeout(1);
        await client.DisconnectAsync().Timeout(1);
        await client.DisconnectedTask.Timeout(1);
        Assert.That(client.State, Is.EqualTo(ConnectionState.Disconnected));

        // Second cycle: should still accept and connect cleanly
        await client.ConnectAsync().Timeout(1);
        Assert.That(client.State, Is.EqualTo(ConnectionState.Connected));
    }
    
}
