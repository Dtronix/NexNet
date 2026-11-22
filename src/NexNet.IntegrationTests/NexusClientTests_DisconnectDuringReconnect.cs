using System.Security.Principal;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Messages;
using NexNet.Transports;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// DisconnectAsync stops the C# client's reconnection loop wherever it is: waiting between attempts, during an attempt's
/// handshake, or right after the attempt connected (from the nexus's OnConnected). The client then ends Disconnected
/// and does not reconnect.
/// </summary>
internal partial class NexusClientTests
{
    /// <summary>Real time given to a stopped loop to (wrongly) retry; its retries are immediate (delay 0).</summary>
    private const int NoRetryWindowMs = 500;

    /// <summary>
    /// A DisconnectAsync while a reconnection attempt's handshake is running (the server holds the attempt in its
    /// authentication) ends the loop: the attempt is not retried and the client ends Disconnected.
    /// </summary>
    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task DisconnectAsync_DuringReconnectionHandshake_StopsReconnecting(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        serverConfig.Authenticate = true;
        clientConfig.Authenticate = () => new byte[] { 1 };
        var policy = new DefaultReconnectionPolicy([TimeSpan.Zero]);
        clientConfig.ReconnectionPolicy = policy;
        var attempts = 0;
        policy.Reconnection += (_, _) => Interlocked.Increment(ref attempts);
        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);
        var states = RecordStates(client);

        var authentications = 0;
        var handshakeHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OnNexusCreated = nexus => nexus.OnAuthenticateEvent = async _ =>
        {
            // The first reconnection attempt's handshake waits here until the test releases it.
            if (Interlocked.Increment(ref authentications) == 2)
            {
                handshakeHeld.TrySetResult();
                await releaseHandshake.Task.ConfigureAwait(false);
            }

            return new DefaultIdentity();
        };

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        // The connection is lost; the loop's first attempt connects and its handshake is held by the server.
        await client.Session!.DisconnectAsync(DisconnectReason.SocketError).Timeout(T(1));
        await handshakeHeld.Task.Timeout(T(TestTimeouts.Connect));

        await client.DisconnectAsync().Timeout(T(TestTimeouts.Connect));
        releaseHandshake.TrySetResult();

        await AssertStaysDisconnected(client, states, () => Volatile.Read(ref attempts), 1);
        await Assert.That(Volatile.Read(ref authentications)).IsEqualTo(2).Because("The stopped loop opened no other session.");
    }

    /// <summary>
    /// A DisconnectAsync while the client is Reconnecting between attempts (the server is down) stops the loop: the
    /// client is Disconnected when DisconnectAsync returns and does not reconnect once the server is back.
    /// </summary>
    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task DisconnectAsync_BetweenReconnectionAttempts_StopsReconnecting(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        var policy = new DefaultReconnectionPolicy([TimeSpan.FromMilliseconds(20)]);
        clientConfig.ReconnectionPolicy = policy;
        serverConfig.InternalNoLingerOnShutdown = true;
        serverConfig.InternalForceDisableSendingDisconnectSignal = true;
        var attempts = 0;
        policy.Reconnection += (_, _) => Interlocked.Increment(ref attempts);
        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);
        var states = RecordStates(client);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await server.StopAsync().Timeout(T(TestTimeouts.Connect));

        // At least one attempt failed against the stopped server: the loop is between attempts or in one.
        await WaitUntil(() => Volatile.Read(ref attempts) >= 1, T(TestTimeouts.Connect));
        await Assert.That(client.State).IsEqualTo(ConnectionState.Reconnecting);

        await client.DisconnectAsync().Timeout(T(TestTimeouts.Connect));
        await Assert.That(client.State).IsEqualTo(ConnectionState.Disconnected).Because("DisconnectAsync stopped the loop.");
        var attemptsAtDisconnect = Volatile.Read(ref attempts);

        // A loop that still ran would connect now.
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await AssertStaysDisconnected(client, states, () => Volatile.Read(ref attempts), attemptsAtDisconnect);
        ConnectionState[] recorded;
        lock (states)
            recorded = states.ToArray();
        await Assert.That(recorded).IsEquivalentTo(
            [ConnectionState.Connected, ConnectionState.Disconnecting, ConnectionState.Disconnected, ConnectionState.Reconnecting, ConnectionState.Disconnected],
            CollectionOrdering.Matching);
    }

    /// <summary>
    /// A DisconnectAsync from OnConnected of a reconnection, made before the attempt that opened the session has
    /// returned to the loop (held by a test hook), ends the loop: the disconnected session is not retried.
    /// </summary>
    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task DisconnectAsync_FromOnConnectedOfAReconnection_StopsReconnecting(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        var policy = new DefaultReconnectionPolicy([TimeSpan.Zero]);
        clientConfig.ReconnectionPolicy = policy;
        var attempts = 0;
        policy.Reconnection += (_, _) => Interlocked.Increment(ref attempts);
        var (server, client, clientNexus) = CreateServerClient(serverConfig, clientConfig);
        var states = RecordStates(client);

        var disconnectedFromOnConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readySessions = 0;
        client.InternalOnSessionReady = async () =>
        {
            // The reconnection attempt returns to the loop only after OnConnected disconnected it.
            if (Interlocked.Increment(ref readySessions) == 2)
                await disconnectedFromOnConnected.Task.ConfigureAwait(false);
        };
        clientNexus.OnConnectedEvent = async (_, isReconnected) =>
        {
            if (!isReconnected)
                return;

            await client.DisconnectAsync().ConfigureAwait(false);
            disconnectedFromOnConnected.TrySetResult();
        };

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await client.Session!.DisconnectAsync(DisconnectReason.SocketError).Timeout(T(1));

        await disconnectedFromOnConnected.Task.Timeout(T(TestTimeouts.Connect));
        await AssertStaysDisconnected(client, states, () => Volatile.Read(ref attempts), 1);
    }

    /// <summary>
    /// A DisconnectAsync while the client is Reconnecting between attempts stops the loop on every language: the
    /// client ends Disconnected and does not reconnect once the server is back.
    /// </summary>
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task DisconnectWhileReconnecting_StopsReconnecting(ClientTarget target)
    {
        await using var server = await StartDedicatedServer(AbruptStop());
        await using var client = await Connect(target, server, spec =>
        {
            spec.ReconnectDelaysMs = [0, 200];
            spec.ReconnectContinuously = true;
        });
        await WaitForFirstConnected(client);

        var disconnected = client.WhenDisconnectedAsync(T(TestTimeouts.Connect));
        await server.StopAsync();
        await disconnected;
        await client.WaitForStateAsync(ConnectionState.Reconnecting, T(TestTimeouts.Connect));

        await client.DisconnectAsync().Timeout(T(TestTimeouts.Connect));
        await client.WaitForStateAsync(ConnectionState.Disconnected, T(1));

        // A loop that still ran would reconnect now: it retries every 200 ms.
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await AssertNotReconnectedWithin(client, 1000, "DisconnectAsync stopped the reconnection loop.");
        await Assert.That(await client.GetStateAsync()).IsEqualTo(ConnectionState.Disconnected);
    }

    /// <summary>
    /// OnDisconnected runs once for the lost session: not again when the reconnection policy then gives up (the client
    /// goes from Reconnecting to Disconnected without a session of its own).
    /// </summary>
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task OnDisconnected_RunsOnceWhenReconnectionGivesUp(ClientTarget target)
    {
        var connectionTimeoutSeconds = T(TestTimeouts.Connect);
        await using var server = await StartDedicatedServer(AbruptStop());
        await using var client = await Connect(target, server, spec =>
        {
            spec.ConnectionTimeoutMs = (int)(connectionTimeoutSeconds * 1000);
            spec.ReconnectDelaysMs = [20];
            spec.ReconnectContinuously = false;
        });
        await WaitForFirstConnected(client);

        await server.StopAsync();
        await Script(client).OnDisconnected.NextCallAsync().AsTask().Timeout(T(5));
        await client.WaitForStateAsync(ConnectionState.Reconnecting, T(TestTimeouts.Connect));
        await client.WaitForStateAsync(ConnectionState.Disconnected, connectionTimeoutSeconds + T(TestTimeouts.Connect));

        using var window = new CancellationTokenSource(500);
        var again = false;
        try
        {
            await Script(client).OnDisconnected.NextCallAsync(window.Token);
            again = true;
        }
        catch (OperationCanceledException) when (window.IsCancellationRequested)
        {
            // No second call.
        }

        await Assert.That(again).IsFalse().Because("OnDisconnected runs once per session; the policy giving up ends no session.");
    }

    /// <summary>
    /// OnConnected runs once the handshake is complete, on every language: when it is delivered the server has
    /// already registered the session (it does so before it sends the ServerGreeting that completes the handshake).
    /// </summary>
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task OnConnected_IsDeliveredAfterTheHandshake(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await CreateClient(target, server);
        var sessionsAtOnConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Script(client).OnConnected.Forward(_ =>
        {
            sessionsAtOnConnected.TrySetResult(server.Server.GetSnapshot().Sessions.Count);
            return ValueTask.FromResult(NoResult.Value);
        });

        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        await Assert.That(await sessionsAtOnConnected.Task.Timeout(T(1))).IsEqualTo(1)
            .Because("The server registers the session before the greeting that completes the client's handshake.");
    }

    /// <summary>Records the client's state changes.</summary>
    private static List<ConnectionState> RecordStates(INexusClient client)
    {
        var states = new List<ConnectionState>();
        client.StateChanged += (_, state) =>
        {
            lock (states)
                states.Add(state);
        };
        return states;
    }

    /// <summary>
    /// Asserts that the client stays Disconnected through <see cref="NoRetryWindowMs"/>: no further reconnection
    /// attempt, no session, and Disconnected as the last state change.
    /// </summary>
    private static async Task AssertStaysDisconnected(
        NexusClient<ClientNexus, ClientNexus.ServerProxy> client,
        List<ConnectionState> states,
        Func<int> attempts,
        int expectedAttempts)
    {
        await Task.Delay(NoRetryWindowMs);

        await Assert.That(attempts()).IsEqualTo(expectedAttempts).Because("DisconnectAsync stopped the reconnection loop.");
        await Assert.That(client.State).IsEqualTo(ConnectionState.Disconnected);
        await Assert.That(client.Session).IsNull();
        ConnectionState[] recorded;
        lock (states)
            recorded = states.ToArray();
        await Assert.That(recorded[^1]).IsEqualTo(ConnectionState.Disconnected)
            .Because($"The client ends Disconnected; it raised {string.Join(", ", recorded)}.");
    }
}
