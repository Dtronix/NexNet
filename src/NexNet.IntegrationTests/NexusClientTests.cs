using System.Text;
using NexNet.Internals;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.InteropServer;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.TestNexus.Interop;
using NexNet.Transports;
using TestServer = NexNet.InteropServer.InteropServer<NexNet.TestNexus.Interop.InteropServerNexus, NexNet.TestNexus.Interop.InteropServerNexus.ClientProxy>;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Client lifecycle: connect, disconnect, reconnection, readiness and authentication, on every row of the client matrix
/// (the C# client in process, and the language hosts of <c>NEXNET_TEST_LANGS</c>). The server is the C# interop server
/// (<see cref="InteropServerNexus"/>): a server on the session's shared host, or a dedicated one for tests that stop and
/// restart it.
/// </summary>
/// <remarks>
/// Every partial part of this class shares the <see cref="LanguageHostPool"/> declared here; the other parts must not
/// declare a constructor or another class data source for it.
/// </remarks>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal partial class NexusClientTests(LanguageHostPool hosts) : BaseTests
{
    /// <summary>The language hosts of the session; their hello decides which rows are skipped.</summary>
    public LanguageHostPool Hosts { get; } = hosts;

    /// <summary>The interop server host shared by the tests that keep their server running.</summary>
    [ClassDataSource<SharedInteropHost>(Shared = SharedType.PerTestSession)]
    public required SharedInteropHost SharedHost { get; init; }

    /// <summary>A server on the shared host.</summary>
    private Task<TestServer> StartServer(InteropServerOptions? options = null)
        => SharedHost.AddServerAsync(options ?? new InteropServerOptions());

    /// <summary>A server with its own host, for tests that stop and restart it.</summary>
    private static Task<TestServer> StartDedicatedServer(InteropServerOptions? options = null)
        => InteropServerHost.StartAsync<InteropServerNexus, InteropServerNexus.ClientProxy>(() => new InteropServerNexus(), options);

    /// <summary>Server options whose sessions close without lingering and without a disconnect signal, as on a crash.</summary>
    private static InteropServerOptions AbruptStop(Action<ServerConfig>? configure = null) => new()
    {
        ConfigureEndpoint = (_, config) =>
        {
            config.InternalNoLingerOnShutdown = true;
            config.InternalForceDisableSendingDisconnectSignal = true;
            configure?.Invoke(config);
        },
    };

    /// <summary>Server options that call <paramref name="onFrame"/> with every frame the server receives.</summary>
    private static InteropServerOptions TapReceived(Action<TappedFrame> onFrame) => new()
    {
        ConfigureEndpoint = (_, config) => ServerTaps.OnReceive(config, (_, frame) => onFrame(frame)),
    };

    /// <summary>Creates a client of <paramref name="target"/> for <paramref name="server"/>; it is not connected.</summary>
    private static Task<IClientUnderTest> CreateClient(ClientTarget target, TestServer server, Action<ClientSpec>? configure = null)
    {
        var spec = ClientSpec.ForEndpoint(server.Endpoints, target.Transport);
        configure?.Invoke(spec);
        return Harness.CreateClientAsync(target, spec);
    }

    /// <summary>Creates and connects a client of <paramref name="target"/> to <paramref name="server"/>.</summary>
    private static async Task<IClientUnderTest> Connect(ClientTarget target, TestServer server, Action<ClientSpec>? configure = null)
    {
        var client = await CreateClient(target, server, configure);
        try
        {
            await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }

        return client;
    }

    /// <summary>
    /// A client of <paramref name="target"/> whose address has nothing listening: the endpoint of a server that was
    /// started and then disposed with its host.
    /// </summary>
    private static async Task<IClientUnderTest> CreateClientWithNoServer(ClientTarget target, Action<ClientSpec>? configure = null)
    {
        target.SkipIfUnsupported();
        InteropEndpoints endpoints;
        await using (var server = await StartDedicatedServer())
            endpoints = server.Endpoints;

        var spec = ClientSpec.ForEndpoint(endpoints, target.Transport);
        configure?.Invoke(spec);
        return await Harness.CreateClientAsync(target, spec);
    }

    private static InteropClientNexusScript Script(IClientUnderTest client) => client.Callbacks<InteropClientNexusScript>();

    /// <summary>Waits for the OnConnected script call of the client's first connection.</summary>
    private static async Task WaitForFirstConnected(IClientUnderTest client)
    {
        var call = await Script(client).OnConnected.NextCallAsync().AsTask().Timeout(T(1));
        await Assert.That(call.IsReconnected).IsFalse();
    }

    /// <summary>
    /// Waits until the client's OnConnected script runs again after <see cref="WaitForFirstConnected"/>: the client
    /// reconnected, and OnConnected reports it as a reconnection.
    /// </summary>
    private static async Task WaitForReconnected(IClientUnderTest client, double seconds)
    {
        bool isReconnected;
        try
        {
            isReconnected = (await Script(client).OnConnected.NextCallAsync().AsTask().Timeout(seconds)).IsReconnected;
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"{client.Target}: not reconnected within {seconds} s; the state is {await client.GetStateAsync()}.");
        }

        await Assert.That(isReconnected).IsTrue().Because("OnConnected after an automatic reconnection reports isReconnected.");
        await client.WaitForStateAsync(ConnectionState.Connected, T(1));
    }

    /// <summary>
    /// Asserts that the client's OnConnected script does not run again within <paramref name="milliseconds"/> (real
    /// time) after <see cref="WaitForFirstConnected"/>: the client did not reconnect.
    /// </summary>
    private static async Task AssertNotReconnectedWithin(IClientUnderTest client, int milliseconds, string because)
    {
        using var window = new CancellationTokenSource(milliseconds);
        var reconnected = false;
        try
        {
            await Script(client).OnConnected.NextCallAsync(window.Token);
            reconnected = true;
        }
        catch (OperationCanceledException) when (window.IsCancellationRequested)
        {
            // The window passed without a reconnection.
        }

        await Assert.That(reconnected).IsFalse().Because(because);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task NexusFiresOnConnected(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await Connect(target, server);

        var connected = await Script(client).OnConnected.NextCallAsync().AsTask().Timeout(T(1));
        await Assert.That(connected.IsReconnected).IsFalse();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ConnectsToServer(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await Connect(target, server);

        // Observed on the server: it holds an established session for this client.
        await WaitUntil(() => server.Server.GetSnapshot().Sessions.Count == 1);
        await Assert.That(server.Server.GetSnapshot().Sessions.Count).IsEqualTo(1);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ClientFailsGracefullyWithNoServer(ClientTarget target)
    {
        await using var client = await CreateClientWithNoServer(target, spec => spec.ConnectionTimeoutMs = 100);

        await AssertThrows<TransportException>(() => client.ConnectAsync().Timeout(T(10)));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ClientTimesOutWithNoServer(ClientTarget target)
    {
        await using var client = await CreateClientWithNoServer(target, spec => spec.ConnectionTimeoutMs = 50);

        await AssertThrows<TransportException>(() => client.ConnectAsync().Timeout(T(10)));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ConnectsAndDisconnectsMultipleTimesFromServer(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await CreateClient(target, server);

        for (int i = 0; i < 5; i++)
        {
            await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
            await client.DisconnectAsync().Timeout(T(1));
            await client.WhenDisconnectedAsync(T(1));
            await client.WaitForStateAsync(ConnectionState.Disconnected, T(1));
        }
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ConnectTimesOutWithNoServer(ClientTarget target)
    {
        await using var client = await CreateClientWithNoServer(target);

        await AssertThrows<TransportException>(() => client.ConnectAsync().Timeout(T(TestTimeouts.Connect)));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ClientProvidesAuthenticationToken(ClientTarget target)
    {
        const string token = "token-123";
        var received = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartServer(new InteropServerOptions
        {
            TokenValidator = (sent, _) =>
            {
                received.TrySetResult(sent?.ToArray());
                return ValueTask.FromResult<IIdentity?>(new DefaultIdentity());
            },
        });
        await using var client = await Connect(target, server, spec => spec.AuthToken = token);

        await Assert.That(await received.Task.Timeout(T(1))).IsEquivalentTo(Encoding.UTF8.GetBytes(token));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ClientSendsPing(ClientTarget target)
    {
        var pinged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartServer(TapReceived(frame =>
        {
            if (frame.Type == MessageType.Ping)
                pinged.TrySetResult();
        }));
        await using var client = await Connect(target, server, spec => spec.PingIntervalMs = 20);

        await pinged.Task.Timeout(T(1));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.FakeTime)]
    public async Task Client_PingTimer_FiresAtConfiguredInterval_DrivenByFakeTime(ClientTarget target)
    {
        var pingCount = 0;
        var firstPing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartServer(TapReceived(frame =>
        {
            if (frame.Type != MessageType.Ping)
                return;

            var observed = Interlocked.Increment(ref pingCount);
            if (observed == 1)
                firstPing.TrySetResult();
            else if (observed == 2)
                secondPing.TrySetResult();
        }));
        await using var client = await Connect(target, server, spec =>
        {
            spec.FakeTime = true;
            spec.PingIntervalMs = 1000;
        });

        // A round trip: the server has received everything the client sent before it.
        await client.Proxy<IInteropServerNexus>().Ping().Timeout(T(1));
        await Assert.That(Volatile.Read(ref pingCount)).IsEqualTo(0).Because("No ping should be sent before the first interval elapses.");

        await client.AdvanceTimeAsync(TimeSpan.FromMilliseconds(1000));
        await firstPing.Task.Timeout(T(1));
        await Assert.That(Volatile.Read(ref pingCount)).IsEqualTo(1);

        await client.AdvanceTimeAsync(TimeSpan.FromMilliseconds(1000));
        await secondPing.Task.Timeout(T(1));
        await Assert.That(Volatile.Read(ref pingCount)).IsEqualTo(2);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket], CSharpTransports = [ClientTransport.Quic])]
    public async Task ReconnectsOnDisconnect(ClientTarget target)
    {
        await using var server = await StartDedicatedServer(AbruptStop());
        await using var client = await Connect(target, server, spec =>
        {
            spec.ReconnectDelaysMs = [20];
            spec.ReconnectContinuously = true;
        });
        await WaitForFirstConnected(client);

        var disconnected = client.WhenDisconnectedAsync(T(TestTimeouts.Connect));
        await server.StopAsync();

        // Wait for the client to process the disconnect.
        await disconnected;
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        // The client's first reconnect attempt starts about 20 ms after the disconnect, while the server is still
        // stopped, and the transport retransmits on its own schedule rather than failing fast: on Windows a TCP SYN to
        // a closed loopback port is retried after about 500 ms. The wait covers a second retransmit too.
        await WaitForReconnected(client, T(TestTimeouts.Connect));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ReconnectsOnTimeout(ClientTarget target)
    {
        await using var server = await StartDedicatedServer(AbruptStop());

        // The session times out before the first ping: the client reconnects for Timeout.
        await using var client = await Connect(target, server, spec =>
        {
            spec.ReconnectDelaysMs = [20];
            spec.ReconnectContinuously = true;
            spec.PingIntervalMs = 75;
            spec.TimeoutMs = 50;
        });
        await WaitForFirstConnected(client);

        await WaitForReconnected(client, T(1));
    }

    /// <summary>
    /// OnConnected reports isReconnected only for a connection the client's reconnection policy opened: false on the
    /// first connect, true after an automatic reconnection, and false again on a connect the application asks for
    /// after a disconnect.
    /// </summary>
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task OnConnected_ReportsIsReconnectedOnlyForAReconnection(ClientTarget target)
    {
        await using var server = await StartDedicatedServer(AbruptStop());
        await using var client = await Connect(target, server, spec =>
        {
            spec.ReconnectDelaysMs = [20];
            spec.ReconnectContinuously = true;
        });
        await WaitForFirstConnected(client);

        // A graceful disconnect is not reconnected for; the connect after it is a new connection, not a reconnection.
        // Checked before the automatic reconnection below, from a connection whose handshake has completed: a graceful
        // disconnect while a reconnection's handshake is still running leaves the C# client's reconnection loop going,
        // and it reconnects (a real reconnection) in a race with the application's connect.
        await client.DisconnectAsync().Timeout(T(1));
        await client.WaitForStateAsync(ConnectionState.Disconnected, T(1));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        var call = await Script(client).OnConnected.NextCallAsync().AsTask().Timeout(T(1));
        await Assert.That(call.IsReconnected).IsFalse().Because("A connect the application asks for is not a reconnection.");

        var disconnected = client.WhenDisconnectedAsync(T(TestTimeouts.Connect));
        await server.StopAsync();
        await disconnected;
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await WaitForReconnected(client, T(TestTimeouts.Connect));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.FakeTime)]
    public async Task Client_ReconnectDelay_DrivenByFakeTime(ClientTarget target)
    {
        await using var server = await StartDedicatedServer(AbruptStop());

        // A 30-second reconnect delay: impractical to test with a real clock.
        await using var client = await Connect(target, server, spec =>
        {
            spec.FakeTime = true;
            spec.ReconnectDelaysMs = [30_000];
            spec.ReconnectContinuously = true;
        });
        await WaitForFirstConnected(client);

        // Restart cycle: stop, then bring the server back up so the eventual reconnect can succeed.
        var disconnected = client.WhenDisconnectedAsync(T(TestTimeouts.Connect));
        await server.StopAsync();
        await disconnected; // the policy delay starts right after the disconnect
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        // Without advancing the client's clock, the 30 s reconnect delay must not have elapsed yet. This wait is about
        // time: real time passes, the client's clock does not.
        await AssertNotReconnectedWithin(client, 100, "Reconnect must not occur until the client's clock has advanced past the policy delay.");

        // Advance past the configured 30 s policy delay.
        await client.AdvanceTimeAsync(TimeSpan.FromSeconds(31));

        await WaitForReconnected(client, T(2));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ReconnectsNotifiesReconnecting(ClientTarget target)
    {
        await using var server = await StartDedicatedServer(AbruptStop());

        // The default C# policy: 0, 2, 10 and 30 s, then every 30 s.
        await using var client = await Connect(target, server, spec =>
        {
            spec.ReconnectDelaysMs = [0, 2_000, 10_000, 30_000];
            spec.ReconnectContinuously = true;
        });
        await WaitForFirstConnected(client);

        var disconnected = client.WhenDisconnectedAsync(T(TestTimeouts.Connect));
        await server.StopAsync();
        await disconnected;

        // The client entered its reconnect loop: the attempt after the restart (2 s after the disconnect) gets through.
        // The harness has no lifecycle slot for the client's Reconnecting callback yet (follow-up of P01a).
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await WaitForReconnected(client, T(TestTimeouts.Connect));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
    public async Task ReconnectsNotifiesReconnecting_Hosted(ClientTarget target)
    {
        var server = await StartDedicatedServer(AbruptStop());
        await using var client = await Connect(target, server, spec =>
        {
            spec.ReconnectDelaysMs = [0, 2_000, 10_000, 30_000];
            spec.ReconnectContinuously = true;
        });

        // Waits for this session's end: once the client reconnects, a later wait would follow the attempt's session.
        var disconnected = client.WhenDisconnectedAsync(T(TestTimeouts.Connect));

        // The whole web host goes away (the server's own host: disposing it stops Kestrel), so reconnect attempts
        // are refused rather than reaching a stopped server.
        await server.Host.DisposeAsync();

        // The client sees a reason its policy reconnects for. The harness has no lifecycle slot for the client's
        // Reconnecting callback yet (follow-up of P01a).
        var reason = await disconnected;
        await Assert.That(reason is DisconnectReason.SocketError or DisconnectReason.Timeout or DisconnectReason.ServerRestarting)
            .IsTrue().Because($"The default policy reconnects for this disconnect; the reason was {reason}.");
    }

    /// <summary>
    /// A lost session puts the client in the Reconnecting state until its reconnection loop connects again: the state
    /// goes Connected, then Reconnecting, then Connected.
    /// </summary>
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ReconnectRaisesReconnectingState(ClientTarget target)
    {
        await using var server = await StartDedicatedServer(AbruptStop());

        // Retried every 200 ms while the server is down, so a client that reports Reconnecting only during an attempt
        // reports it again and again.
        await using var client = await Connect(target, server, spec =>
        {
            spec.ReconnectDelaysMs = [0, 200];
            spec.ReconnectContinuously = true;
        });
        await WaitForFirstConnected(client);
        await client.WaitForStateAsync(ConnectionState.Connected, T(1));

        var disconnected = client.WhenDisconnectedAsync(T(TestTimeouts.Connect));
        await server.StopAsync();
        await disconnected;

        await client.WaitForStateAsync(ConnectionState.Reconnecting, T(TestTimeouts.Connect));

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await WaitForReconnected(client, T(TestTimeouts.Connect));
    }

    /// <summary>
    /// The client stays Reconnecting between failed reconnection attempts: from the loss of the session until an attempt
    /// connects it never reads Disconnected (each attempt's own disconnect is not reported).
    /// </summary>
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ReconnectingStateLastsBetweenRetries(ClientTarget target)
    {
        await using var server = await StartDedicatedServer(AbruptStop());

        // 100 ms between attempts: a client that went back to Disconnected after each failed attempt would read
        // Disconnected for most of that time.
        await using var client = await Connect(target, server, spec =>
        {
            spec.ReconnectDelaysMs = [0, 100];
            spec.ReconnectContinuously = true;
        });
        await WaitForFirstConnected(client);

        var disconnected = client.WhenDisconnectedAsync(T(TestTimeouts.Connect));
        await server.StopAsync();
        await disconnected;
        await client.WaitForStateAsync(ConnectionState.Reconnecting, T(TestTimeouts.Connect));

        // Samples the state for 1.5 s, over several failed attempts (the server is down).
        var observed = await SampleStatesAsync(client, 1.5);
        await Assert.That(observed.Distinct()).IsEquivalentTo([ConnectionState.Reconnecting])
            .Because($"the states sampled between attempts were {string.Join(", ", observed)}");

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await WaitForReconnected(client, T(TestTimeouts.Connect));
    }

    /// <summary>
    /// When the reconnection policy gives up, the client goes from Reconnecting to Disconnected once, after its last
    /// attempt, and stays there.
    /// </summary>
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ReconnectingStateEndsDisconnectedWhenThePolicyGivesUp(ClientTarget target)
    {
        // An attempt to the stopped server is usually refused quickly. A QUIC attempt gets no answer and lasts the
        // connection timeout (no longer: S47 removed a ~3 s overshoot), so the timeout bounds each attempt.
        var connectionTimeoutSeconds = T(TestTimeouts.Connect);
        await using var server = await StartDedicatedServer(AbruptStop());

        // Two attempts, 100 ms apart, then the policy gives up.
        await using var client = await Connect(target, server, spec =>
        {
            spec.ConnectionTimeoutMs = (int)(connectionTimeoutSeconds * 1000);
            spec.ReconnectDelaysMs = [0, 100];
            spec.ReconnectContinuously = false;
        });
        await WaitForFirstConnected(client);

        var disconnected = client.WhenDisconnectedAsync(T(TestTimeouts.Connect));
        await server.StopAsync();
        await disconnected;
        await client.WaitForStateAsync(ConnectionState.Reconnecting, T(TestTimeouts.Connect));

        var whileReconnecting = await SampleStatesAsync(client, 2 * connectionTimeoutSeconds + T(2), until: ConnectionState.Disconnected);
        await Assert.That(whileReconnecting[^1]).IsEqualTo(ConnectionState.Disconnected)
            .Because("the policy gives up after two attempts");
        await Assert.That(whileReconnecting[..^1].Distinct()).IsEquivalentTo([ConnectionState.Reconnecting])
            .Because($"the states sampled before the policy gave up were {string.Join(", ", whileReconnecting)}");

        // It stays Disconnected: no further attempt.
        var after = await SampleStatesAsync(client, 0.3);
        await Assert.That(after.Distinct()).IsEquivalentTo([ConnectionState.Disconnected])
            .Because($"the states sampled after the policy gave up were {string.Join(", ", after)}");
    }

    /// <summary>
    /// Reads the client's state every 10 ms until it is <paramref name="until"/> (included) or <paramref name="seconds"/>
    /// elapse; returns the states read, in order.
    /// </summary>
    private static async Task<List<ConnectionState>> SampleStatesAsync(IClientUnderTest client, double seconds, ConnectionState? until = null)
    {
        var observed = new List<ConnectionState>();
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var state = await client.GetStateAsync();
            observed.Add(state);
            if (state == until)
                break;
            await Task.Delay(10);
        }

        return observed;
    }

    /// <summary>
    /// The C# client's StateChanged events while it reconnects: it stays Reconnecting between failed attempts (it does not
    /// report each attempt's own disconnect) and raises the sequence Connected, Disconnecting, Disconnected, Reconnecting,
    /// Connected. The other languages check their sequence in their own unit tests.
    /// </summary>
    [Test]
    [Transports(Type.Uds)]
    public async Task ReconnectingStateChangedSequenceBetweenRetries(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        clientConfig.ReconnectionPolicy = new DefaultReconnectionPolicy([TimeSpan.FromMilliseconds(20)]);
        serverConfig.InternalNoLingerOnShutdown = true;
        serverConfig.InternalForceDisableSendingDisconnectSignal = true;
        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);
        var attempts = 0;
        clientConfig.ReconnectionPolicy.Reconnection += (_, _) => Interlocked.Increment(ref attempts);
        var states = new List<ConnectionState>();
        client.StateChanged += (_, state) =>
        {
            lock (states)
                states.Add(state);
        };

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await server.StopAsync().Timeout(T(TestTimeouts.Connect));
        await WaitForState(client, ConnectionState.Reconnecting, seconds: T(TestTimeouts.Connect));

        // Several attempts fail against the stopped server; the client stays Reconnecting throughout.
        var deadline = DateTime.UtcNow.AddSeconds(T(TestTimeouts.Connect));
        while (Volatile.Read(ref attempts) < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        await Assert.That(Volatile.Read(ref attempts)).IsGreaterThanOrEqualTo(3);
        await Assert.That(client.State).IsEqualTo(ConnectionState.Reconnecting);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await WaitForState(client, ConnectionState.Connected, seconds: T(TestTimeouts.Connect));

        ConnectionState[] recorded;
        lock (states)
            recorded = states.ToArray();
        await Assert.That(recorded).IsEquivalentTo(
            [ConnectionState.Connected, ConnectionState.Disconnecting, ConnectionState.Disconnected, ConnectionState.Reconnecting, ConnectionState.Connected],
            CollectionOrdering.Matching);
    }

    /// <summary>
    /// The C# client's StateChanged events when its reconnection policy gives up: Reconnecting, then Disconnected.
    /// </summary>
    [Test]
    [Transports(Type.Uds)]
    public async Task ReconnectingStateChangedSequenceWhenThePolicyGivesUp(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        clientConfig.ReconnectionPolicy = new DefaultReconnectionPolicy([TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20)], continuousRetry: false);
        serverConfig.InternalNoLingerOnShutdown = true;
        serverConfig.InternalForceDisableSendingDisconnectSignal = true;
        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);
        var states = new List<ConnectionState>();
        var gaveUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateChanged += (_, state) =>
        {
            lock (states)
            {
                states.Add(state);
                if (state == ConnectionState.Disconnected && states.Contains(ConnectionState.Reconnecting))
                    gaveUp.TrySetResult();
            }
        };

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await server.StopAsync().Timeout(T(TestTimeouts.Connect));
        await gaveUp.Task.Timeout(T(TestTimeouts.Connect));

        await Assert.That(client.State).IsEqualTo(ConnectionState.Disconnected);
        ConnectionState[] recorded;
        lock (states)
            recorded = states.ToArray();
        await Assert.That(recorded).IsEquivalentTo(
            [ConnectionState.Connected, ConnectionState.Disconnecting, ConnectionState.Disconnected, ConnectionState.Reconnecting, ConnectionState.Disconnected],
            CollectionOrdering.Matching);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ReconnectsStopsAfterSpecifiedTimes(ClientTarget target)
    {
        // The connection timeout applies to the initial connect too, so it gets the standard connect budget: a 100 ms
        // timeout failed that connect whenever a cold process or a loaded machine made the (TLS) HTTP connect slower.
        var connectionTimeoutSeconds = T(TestTimeouts.Connect);
        await using var server = await StartDedicatedServer(AbruptStop());
        await using var client = await Connect(target, server, spec =>
        {
            spec.ConnectionTimeoutMs = (int)(connectionTimeoutSeconds * 1000);
            spec.ReconnectDelaysMs = [20];
            spec.ReconnectContinuously = false;
        });

        await server.StopAsync();

        // One reconnect attempt fails, then the client gives up: the client is Reconnecting until then. The attempt is
        // usually refused quickly; a QUIC attempt to the stopped server gets no answer and lasts the connection timeout,
        // which bounds it (S47 removed a ~3 s overshoot past it).
        await Script(client).OnDisconnected.NextCallAsync().AsTask().Timeout(T(5));
        await client.WaitForStateAsync(ConnectionState.Disconnected, connectionTimeoutSeconds + T(2));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ClientProxyInvocationCancelsOnDisconnect(ClientTarget target)
    {
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartServer(TapReceived(frame =>
        {
            if (frame.Type == MessageType.Invocation)
                invoked.TrySetResult();
        }));
        await using var client = await Connect(target, server);

        // The server holds the call far longer than the test runs.
        var call = client.Proxy<IInteropServerNexus>().Delay(100_000, 12345, CancellationToken.None).AsTask();

        // The call reached the server before it stops.
        await invoked.Task.Timeout(T(1));
        await server.StopAsync();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await call.Timeout(T(1)));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ReadyTaskCompletesUponConnection(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await Connect(target, server);

        await client.WhenReadyAsync(T(1));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ReadyTaskCompletesUponAuthentication(ClientTarget target)
    {
        var authCompleted = false;
        await using var server = await StartServer(new InteropServerOptions
        {
            TokenValidator = (_, _) =>
            {
                Volatile.Write(ref authCompleted, true);
                return ValueTask.FromResult<IIdentity?>(new DefaultIdentity());
            },
        });
        await using var client = await Connect(target, server);

        await client.WhenReadyAsync(T(1));
        await Assert.That(Volatile.Read(ref authCompleted)).IsTrue();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ReadyTaskCompletesUponAuthFailure(ClientTarget target)
    {
        await using var server = await StartServer(new InteropServerOptions
        {
            TokenValidator = (_, _) => ValueTask.FromResult<IIdentity?>(null),
        });
        await using var client = await CreateClient(target, server);

        await Assert.That(await client.TryConnectAsync().Timeout(T(TestTimeouts.Connect))).IsFalse();
        await client.WaitForStateAsync(ConnectionState.Disconnected, T(1));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task DisconnectTaskCompletesUponDisconnection(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await Connect(target, server);

        await server.StopAsync();

        await client.WhenDisconnectedAsync(T(1));
        await client.WaitForStateAsync(ConnectionState.Disconnected, T(1));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task DisconnectTaskCompletesUponAuthFailure(ClientTarget target)
    {
        await using var server = await StartServer(new InteropServerOptions
        {
            TokenValidator = (_, _) => ValueTask.FromResult<IIdentity?>(null),
        });
        await using var client = await CreateClient(target, server);

        await client.TryConnectAsync().Timeout(T(TestTimeouts.Connect));
        await client.WhenDisconnectedAsync(T(1));
        await client.WaitForStateAsync(ConnectionState.Disconnected, T(1));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task DisconnectTaskCompletesAfterServerStops(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await Connect(target, server);

        await server.StopAsync().Timeout(T(TestTimeouts.Connect));

        // The client observes the disconnect promptly.
        await client.WhenDisconnectedAsync(T(1));
        await client.WaitForStateAsync(ConnectionState.Disconnected, T(1));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ClientSendsDisconnectSignal(ClientTarget target)
    {
        var signaled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartServer(TapReceived(frame =>
        {
            if (frame.Type == MessageType.DisconnectGraceful)
                signaled.TrySetResult();
        }));
        await using var client = await Connect(target, server);

        await client.DisconnectAsync().Timeout(T(1));

        await signaled.Task.Timeout(T(1));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task FiresOnDisconnectedEvent(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await Connect(target, server);

        await client.DisconnectAsync().Timeout(T(1));

        await Script(client).OnDisconnected.NextCallAsync().AsTask().Timeout(T(1));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ProxyInvocationPropagatesServerException(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await Connect(target, server);

        // The server method throws.
        await Assert.ThrowsExactlyAsync<ProxyRemoteInvocationException>(
            async () => await client.Proxy<IInteropServerNexus>().ThrowWithResult("boom").Timeout(T(1)));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task DisconnectWithoutConnectDoesNotThrow(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await CreateClient(target, server);

        // Should complete without throwing.
        await Assert.That(async () => await client.DisconnectAsync().Timeout(T(1))).ThrowsNothing();
        await client.WaitForStateAsync(ConnectionState.Disconnected, T(1));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task DoubleConnectDoesNotThrow(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await Connect(target, server);

        // Connecting a connected client does nothing.
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await client.WaitForStateAsync(ConnectionState.Connected, T(1));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task DoubleDisconnectDoesNotThrow(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await Connect(target, server);
        await client.WaitForStateAsync(ConnectionState.Connected, T(1));

        // First disconnect.
        await client.DisconnectAsync().Timeout(T(1));

        // Second disconnect should be a no-op.
        await Assert.That(async () => await client.DisconnectAsync().Timeout(T(1))).ThrowsNothing();
        await client.WaitForStateAsync(ConnectionState.Disconnected, T(1));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ConcurrentProxyInvocations(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await Connect(target, server);
        var proxy = client.Proxy<IInteropServerNexus>();

        // Fire off 20 simultaneous calls.
        var calls = Enumerable.Range(0, 20)
            .Select(_ => proxy.EchoInt32(42).AsTask())
            .ToArray();

        var results = await Task.WhenAll(calls).Timeout(T(1));
        await Assert.That(results.Count(r => r == 42)).IsEqualTo(20);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket], CSharpTransports = [ClientTransport.Quic])]
    public async Task ServerRejectsAuth_SendsDisconnect(ClientTarget target)
    {
        // Signaled when any frame the server sends is the authentication disconnect.
        var disconnectSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartServer(new InteropServerOptions
        {
            // Force authentication to fail.
            TokenValidator = (_, _) => ValueTask.FromResult<IIdentity?>(null),
            ConfigureEndpoint = (_, config) => ServerTaps.OnSend(config, (_, frame) =>
            {
                if (frame.Type == MessageType.DisconnectAuthentication)
                    disconnectSent.TrySetResult();
            }),
        });
        await using var client = await CreateClient(target, server);

        var connected = await client.TryConnectAsync().Timeout(T(TestTimeouts.Connect));
        var reason = await client.WhenDisconnectedAsync(T(1));
        await Task.WhenAny(disconnectSent.Task, Task.Delay(TimeSpan.FromSeconds(T(1))));

        using (Assert.Multiple())
        {
            await Assert.That(connected).IsFalse();
            await Assert.That(disconnectSent.Task.IsCompleted).IsTrue().Because("Server should send a Disconnect message when auth is rejected");
            await Assert.That(reason).IsEqualTo(DisconnectReason.Authentication).Because("The client should observe the authentication rejection, not a bare socket close.");
        }
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ProxyInvocationAfterDisconnectThrows(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await Connect(target, server);
        await client.DisconnectAsync().Timeout(T(1));

        // Any call after a clean disconnect throws.
        await AssertThrows<InvalidOperationException>(() => client.Proxy<IInteropServerNexus>().EchoInt32(1).Timeout(T(1)));
    }

    /// <summary>
    /// Without a reconnection policy the client never starts reconnecting after a disconnect.
    /// </summary>
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task OnReconnectingEventNotFiredWithNoRetries(ClientTarget target)
    {
        // No clean-disconnect signal: the client sees a socket error, a reason the default policy reconnects for.
        await using var server = await StartServer(AbruptStop());
        await using var client = await Connect(target, server, spec => spec.ReconnectDelaysMs = null);
        await WaitForFirstConnected(client);

        await server.StopAsync().Timeout(T(TestTimeouts.Connect));
        await client.WhenDisconnectedAsync(T(1));

        // The reconnect decision runs right after the disconnect, and no event marks that it is over: give it a
        // moment to (not) start.
        await AssertNotReconnectedWithin(client, 50, "With no reconnection policy, the client must not reconnect.");
        await Assert.That(await client.GetStateAsync()).IsEqualTo(ConnectionState.Disconnected)
            .Because("With no reconnection policy, the client must not start reconnecting.");
    }

    [Test]
    [Transports]
    public async Task StartAsyncTwiceThrows(Type type)
    {
        var server = CreateServer(CreateServerConfig(type), /*listenerFactory*/ null);

        // First start should succeed
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        // Second concurrent start should throw InvalidOperationException
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => server.StartAsync().Timeout(T(TestTimeouts.Connect)));
    }
}

/// <summary>
/// One <see cref="InteropServerHost"/> (Kestrel) for the client-matrix tests of a test session: each test adds its own
/// server to it under a unique test id. Started on first use; stopped at the end of the session.
/// </summary>
internal sealed class SharedInteropHost : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InteropServerHost? _host;
    private bool _disposed;

    /// <summary>Adds and starts a server; its <see cref="InteropServerOptions.TestId"/> is set when it has none.</summary>
    public async Task<TestServer> AddServerAsync(InteropServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.TestId ??= "t" + Guid.NewGuid().ToString("N");
        var host = await GetHostAsync().ConfigureAwait(false);
        return await host.AddServerAsync<InteropServerNexus, InteropServerNexus.ClientProxy>(() => new InteropServerNexus(), options)
            .ConfigureAwait(false);
    }

    private async Task<InteropServerHost> GetHostAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_host == null)
            {
                // Kestrel's threads must not carry the execution context (the TUnit test) of the test that starts it.
                Task<InteropServerHost> start;
                using (ExecutionContext.SuppressFlow())
                    start = Task.Run(() => InteropServerHost.StartSharedAsync());
                _host = await start.ConfigureAwait(false);
            }

            return _host;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            if (_host != null)
                await _host.DisposeAsync().ConfigureAwait(false);
            _host = null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
