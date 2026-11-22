using System;
using System.Net;
using System.Net.Sockets;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NexNet.Transports;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexusServerTests : BaseTests
{

    [Test]
    [Transports]
    public async Task AcceptsClientConnection(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverConfig = CreateServerConfig(type);
        var (server, client, clientNexus) = CreateServerClient(
            serverConfig,
            CreateClientConfig(type));

        serverConfig.InternalOnConnect = () => tcs.SetResult();

        await server.StartAsync().Timeout(TestTimeouts.Connect);
        await client.ConnectAsync().Timeout(TestTimeouts.Connect);

        await tcs.Task.Timeout(1);
    }


    [Test]
    [Transports]
    public async Task NexusFiresOnConnected(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, _) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        server.OnNexusCreated = nexus => nexus.OnConnectedEvent = nexus =>
        {
            tcs.SetResult();
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(TestTimeouts.Connect);

        await client.ConnectAsync().Timeout(TestTimeouts.Connect);

        await tcs.Task.Timeout(1);
    }



    [Test]
    [Transports(Except = new[] { Type.WebSocket, Type.HttpSocket })]
    //[TestCase(Type.WebSocket)] Can't start and stop Asp.
    //[TestCase(Type.HttpSocket)] Can't start and stop Asp.
    public async Task StartsAndStopsMultipleTimes(Type type)
    {

        var clientConfig = CreateClientConfig(type);
        var (server, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            clientConfig);


        for (int i = 0; i < 5; i++)
        {
            await server.StartAsync().Timeout(TestTimeouts.Connect);

            await client.ConnectAsync().Timeout(TestTimeouts.Connect);

            await server.StopAsync();

            await client.DisconnectedTask.Timeout(2);

            // Wait for the client to process the disconnect.

        }
    }

    [Test]
    [Transports(Except = new[] { Type.WebSocket, Type.HttpSocket })]
    //[TestCase(Type.WebSocket)] Can't start and stop Asp.
    //[TestCase(Type.HttpSocket)] Can't start and stop Asp.
    public async Task StopsAndReleasesStoppedTcs(Type type)
    {
        var (server, client, clientHub) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));


        await Assert.That(server.StoppedTask is null).IsTrue();
        await server.StartAsync().Timeout(TestTimeouts.Connect);
        await Assert.That(server.StoppedTask!.IsCompleted).IsFalse();

        await server.StopAsync();

        await server.StoppedTask!.Timeout(1);
    }

    [Test]
    [Transports(Except = new[] { Type.WebSocket, Type.HttpSocket })]
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
            await Assert.That(e.Error).IsEqualTo(TransportError.AddressInUse);
        }
        catch (Exception e)
        {
            Fail.Test($"Expected {nameof(TransportException)} but got {e.GetType().Name}");
        }
    }
    
    [Test]
    public async Task ServerThrowsWhenTransportConfigReturnsNullListenerOnWrongMode()
    {
        var server = ServerNexus.CreateServer(new CustomServerConfig(ServerConnectionMode.Listener), () => null!);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => server.StartAsync());
    }
    
    [Test]
    public async Task ServerDoesntThrowWhenTransportConfigReturnsNullListenerOnCorrectMode()
    {
        var server = ServerNexus.CreateServer(new CustomServerConfig(ServerConnectionMode.Receiver), () => null!);
        await Assert.That(() => server.StartAsync()).ThrowsNothing();
    }
    
    [Test]
    public async Task ServerThrowsWhenStartingTwiceWhileAlreadyRunning()
    {
        var server = ServerNexus.CreateServer(new CustomServerConfig(ServerConnectionMode.Receiver), () => null!);
        await Assert.That(() => server.StartAsync()).ThrowsNothing();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => server.StartAsync());
    }
    
    /// <summary>
    /// The server should fire its OnDisconnectedEvent when a connected client cleanly disconnects.
    /// </summary>
    [Test]
    [Transports]
    public async Task ServerFiresOnDisconnectedEvent(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, _) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));
        
        server.OnNexusCreated = nexus => nexus.OnDisconnectedEvent = _ =>
        {
            tcs.SetResult();
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(TestTimeouts.Connect);
        await client.ConnectAsync().Timeout(TestTimeouts.Connect);
        await client.DisconnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }
    
    /// <summary>
    /// If authentication fails (OnAuthenticateEvent returns null), the server should immediately send a Disconnect frame.
    /// </summary>
    [Test]
    [Transports(Except = new[] { Type.WebSocket, Type.HttpSocket })]
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
        var (server, client, clientHub) = CreateServerClient(serverConfig, clientConfig);

        // Force authentication failure
        server.OnNexusCreated = nexus => nexus.OnAuthenticateEvent = _ => ValueTask.FromResult<IIdentity?>(null);

        await server.StartAsync().Timeout(TestTimeouts.Connect);
        var result = await client.TryConnectAsync().Timeout(TestTimeouts.Connect);

        // Signalled by the send itself, not by timing.
        await disconnectSent.Task.Timeout(1);
        await Assert.That(result.DisconnectReason).IsEqualTo(DisconnectReason.Authentication).Because("The client should observe the authentication rejection, not a bare socket close.");
    }

    /// <summary>
    /// The server's read loop exits as soon as its session is disconnecting. When it exits while the disconnect is
    /// still being sent (authentication runs off the read loop), the server must not complete the output first:
    /// the disconnect frame has to reach the client. The hook holds the disconnect after its state change and before
    /// its send while the client wakes the server's read loop with a ping.
    /// </summary>
    [Test]
    [Transports(Type.Uds, Type.Tcp, Type.TcpTls)]
    public async Task ServerRejectsAuth_DisconnectIsSentWhenReadLoopExitsFirst(Type type)
    {
        var disconnectHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDisconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var serverConfig = CreateServerConfig(type);
        serverConfig.Authenticate = true;
        serverConfig.InternalOnSend = (_, bytes) =>
        {
            if (bytes is [(byte)MessageType.DisconnectAuthentication])
                disconnectSent.TrySetResult();
        };
        serverConfig.InternalOnDisconnectBeforeSend = async _ =>
        {
            disconnectHeld.TrySetResult();
            await releaseDisconnect.Task.ConfigureAwait(false);
        };

        var (server, client, _) = CreateServerClient(serverConfig, CreateClientConfig(type));
        server.OnNexusCreated = nexus => nexus.OnAuthenticateEvent = _ => ValueTask.FromResult<IIdentity?>(null);

        await server.StartAsync().Timeout(TestTimeouts.Connect);
        var connectTask = client.TryConnectAsync();

        // The server is disconnecting (state changed) and has not sent the disconnect frame yet.
        await disconnectHeld.Task.Timeout(1);

        // Wake the server's read loop: it sees the session is disconnecting and exits.
        await client.Session!.SendHeader(MessageType.Ping).Timeout(1);

        // If the server completes its output when the read loop exits, the client sees the stream end here.
        await Task.WhenAny(client.DisconnectedTask, Task.Delay(1000));

        releaseDisconnect.TrySetResult();

        var result = await connectTask.Timeout(2);
        await Task.WhenAny(disconnectSent.Task, Task.Delay(1000));

        using (Assert.Multiple())
        {
            await Assert.That(disconnectSent.Task.IsCompleted).IsTrue().Because("The server should send DisconnectAuthentication even though its read loop exited first.");
            await Assert.That(result.DisconnectReason).IsEqualTo(DisconnectReason.Authentication).Because("The client should observe the authentication rejection, not a bare socket close.");

        }
    }
    
    /// <summary>
    /// Calling StopAsync on a server that was never started should throw.
    /// </summary>
    [Test]
    [Transports]
    public async Task StopWithoutStartThrows(Type type)
    {
        var server = CreateServer(CreateServerConfig(type), /*listenerFactory*/ null);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => server.StopAsync());
    }
    
    /// <summary>
    /// When authentication is enabled and OnAuthenticateEvent returns a non‐null identity,
    /// the server’s OnConnectedEvent should fire only after auth succeeds.
    /// </summary>
    [Test]
    [Transports]
    public async Task OnConnectedEventFiresAfterAuthentication(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.Authenticate = true;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientConfig = CreateClientConfig(type);

        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);

        // Force authentication to succeed
        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthenticateEvent = _ => ValueTask.FromResult<IIdentity?>(new DefaultIdentity());
            // Only fire once auth is done
            nexus.OnConnectedEvent = _ =>
            {
                tcs.SetResult();
                return ValueTask.CompletedTask;
            };
        };

        await server.StartAsync().Timeout(TestTimeouts.Connect);
        await client.ConnectAsync().Timeout(TestTimeouts.Connect);

        // Should complete only after auth succeeded
        await tcs.Task.Timeout(1);
    }

    [Test]
    [Arguments(Type.Quic, true)]
    [Arguments(Type.Uds, true)]
    [Arguments(Type.Tcp, true)]
    [Arguments(Type.TcpTls, true)]
    [Arguments(Type.WebSocket, true)]
    [Arguments(Type.HttpSocket, true)]
    [Arguments(Type.Quic, false)]
    [Arguments(Type.Uds, false)]
    [Arguments(Type.Tcp, false)]
    [Arguments(Type.TcpTls, false)]
    [Arguments(Type.WebSocket, false)]
    [Arguments(Type.HttpSocket, false)]
    public async Task ConnectAsyncThrowsAndOnConnectedNeverFiresWhenAuthHandlerThrows(Type type, bool authenticateClient)
    {
        // Arrange: force server to require auth, and make its auth handler throw
        var serverConfig = CreateServerConfig(type);
        serverConfig.Authenticate = true;
        var clientConfig = CreateClientConfig(type);
        
        if(authenticateClient)
            clientConfig.Authenticate = () => Memory<byte>.Empty;

        var (server, client, clientNexus) =
            CreateServerClient(serverConfig, clientConfig);

        server.OnNexusCreated = nexus => nexus.OnAuthenticateEvent = _ => throw new InvalidOperationException("boom!");

        // Track whether the client ever thinks it's connected
        var connectedFired = false;
        clientNexus.OnConnectedEvent = (_, _) =>
        {
            connectedFired = true;
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(TestTimeouts.Connect);

        // Act & Assert: ConnectAsync should throw, and OnConnectedEvent must not fire
        await AssertThrows<TransportException>(async () =>
            await client.ConnectAsync().Timeout(TestTimeouts.Connect));

        await Assert.That(connectedFired).IsFalse().Because("OnConnectedEvent should not fire when authentication handler throws");
    }
    
    /// <summary>
    /// If your OnDisconnectedEvent handler throws, the server should swallow that
    /// and remain able to accept new clients afterwards.
    /// </summary>
    [Test]
    [Transports]
    public async Task OnDisconnectedEventExceptionDoesNotBreakServer(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        var clientConfig = CreateClientConfig(type);
        var (server, client, _) =
            CreateServerClient(serverConfig, clientConfig);

        bool first = true;
        server.OnNexusCreated = nexus => nexus.OnDisconnectedEvent = nexus =>
        {
            if (first)
            {
                first = false;
                throw new InvalidOperationException("disconnect handler boom");
            }
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(TestTimeouts.Connect);

        // First cycle: handler throws, but server should stay up
        await client.ConnectAsync().Timeout(TestTimeouts.Connect);
        await client.DisconnectAsync().Timeout(1);
        await client.DisconnectedTask.Timeout(1);
        await Assert.That(client.State).IsEqualTo(ConnectionState.Disconnected);

        // Second cycle: should still accept and connect cleanly
        await client.ConnectAsync().Timeout(TestTimeouts.Connect);
        await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
    }
    
    // Skip the UDS & UDP protocols
    //[TestCase(Type.Quic)]
    //[TestCase(Type.Uds)]
    [Test]
    [Transports(Type.Tcp)]
    //[TestCase(Type.TcpTls)]
    //[TestCase(Type.WebSocket)]
    //[TestCase(Type.HttpSocket)]
    public async Task ServerDisconnectsNonNexNetClient(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        var server = CreateServer(serverConfig, n => { });
        await server.StartAsync();
        
        if (serverConfig is TcpServerConfig { EndPoint: IPEndPoint tcpEndPoint } tcpConfig)
        {
            var client = new TcpClient();
            client.LingerState = new LingerOption(false, 0);
            await client.ConnectAsync(IPAddress.Loopback, tcpEndPoint.Port);
            var stream = client.GetStream();
            // Bunk data for the protocol header.
            stream.Write([1, 2, 3, 4, 5, 6, 7, 8]);

            await Utilities.WaitForConnectionClosureAsync(client).Timeout(1);
        }
    }
    
    // Skip the UDS & UDP protocols
    //[TestCase(Type.Quic)]
    //[TestCase(Type.Uds)]
    [Test]
    [Transports(Type.Tcp)]
    //[TestCase(Type.TcpTls)]
    //[TestCase(Type.WebSocket)]
    //[TestCase(Type.HttpSocket)]
    public async Task ServerDisconnectsOnNoData(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.HandshakeTimeout = 100;
        var server = CreateServer(serverConfig, n => { });
        await server.StartAsync();
        
        if (serverConfig is TcpServerConfig { EndPoint: IPEndPoint tcpEndPoint } tcpConfig)
        {
            var client = new TcpClient();
            client.LingerState = new LingerOption(false, 0);
            await client.ConnectAsync(IPAddress.Loopback, tcpEndPoint.Port);
            var stream = client.GetStream();
            // Don't send anything and wait for disconnect.
            //stream.Write([1, 2, 3, 4, 5, 6, 7, 8]);

            await Utilities.WaitForConnectionClosureAsync(client).Timeout(1);
        }
    }

    [Test]
    [Transports(Type.Uds)]
    public async Task Server_ConnectionWatchdog_DisconnectsTimedOutSessions_DrivenByFakeTime(Type type)
    {
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var serverConfig = CreateServerConfig(type);
        serverConfig.Time = fakeTime;
        serverConfig.Timeout = 4000; // Watchdog fires every Timeout/4 = 1000 ms.

        var clientConfig = CreateClientConfig(type);
        // Make the client effectively silent — its PingInterval >> the test horizon — so the server's
        // LastReceived doesn't get refreshed mid-test by real-time client pings.
        clientConfig.PingInterval = 600_000;

        var (server, client, clientNexus) = CreateServerClient(serverConfig, clientConfig);

        var disconnectTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clientNexus.OnDisconnectedEvent = _ =>
        {
            disconnectTcs.TrySetResult();
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(TestTimeouts.Connect);
        await client.ConnectAsync().Timeout(TestTimeouts.Connect);

        await Assert.That(disconnectTcs.Task.IsCompleted).IsFalse().Because("Client should still be connected at t=0.");

        // Advance past the server-side timeout. The watchdog fires under fakeTime, sees LastReceived
        // (recorded at handshake time) as expired, and closes the session — the client observes
        // the socket close.
        fakeTime.Advance(TimeSpan.FromMilliseconds(5000));

        await disconnectTcs.Task.Timeout(2);
    }

    [Test]
    public async Task ConcurrentConnections_UniqueSessionIds()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var sessionIds = new System.Collections.Concurrent.ConcurrentBag<long>();
        var expectedClients = 20;

        var server = ServerNexus.CreateServer(serverConfig, () =>
        {
            var nexus = new ServerNexus();
            nexus.OnConnectedEvent = _ =>
            {
                sessionIds.Add(nexus.SessionContext.Id);
                return default;
            };
            return nexus;
        });

        await server.StartAsync();

        var tasks = Enumerable.Range(0, expectedClients).Select(async _ =>
        {
            var clientConfig = CreateClientConfig(Type.Tcp);
            var client = ClientNexus.CreateClient(clientConfig, new ClientNexus());
            try
            {
                await client.ConnectAsync().Timeout(5);
                await Task.Delay(100);
            }
            catch
            {
                // Some connections may fail under load
            }
        });

        await Task.WhenAll(tasks);
        await Task.Delay(500);

        var uniqueIds = sessionIds.Distinct().ToList();
        await Assert.That(uniqueIds.Count).IsEqualTo(sessionIds.Count).Because($"All {sessionIds.Count} session IDs should be unique, but found {uniqueIds.Count} unique");
    }
}