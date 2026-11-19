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
    /// reconnected.
    /// </summary>
    /// <remarks>
    /// The call's &lt;c&gt;IsReconnected&lt;/c&gt; is not asserted yet: the in-process driver derives it from a
    /// &lt;c&gt;Reconnecting&lt;/c&gt; state change the C# client does not raise, so it reports false on the csharp row (follow-up
    /// of P01a). The reconnection itself is what these tests check.
    /// </remarks>
    private static async Task WaitForReconnected(IClientUnderTest client, double seconds)
    {
        try
        {
            await Script(client).OnConnected.NextCallAsync().AsTask().Timeout(seconds);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"{client.Target}: not reconnected within {seconds} s; the state is {await client.GetStateAsync()}.");
        }

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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
    public async Task NexusFiresOnConnected(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await Connect(target, server);

        var connected = await Script(client).OnConnected.NextCallAsync().AsTask().Timeout(T(1));
        await Assert.That(connected.IsReconnected).IsFalse();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
    public async Task ConnectsToServer(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await Connect(target, server);

        // Observed on the server: it holds an established session for this client.
        await WaitUntil(() => server.Server.GetSnapshot().Sessions.Count == 1);
        await Assert.That(server.Server.GetSnapshot().Sessions.Count).IsEqualTo(1);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
    public async Task ClientFailsGracefullyWithNoServer(ClientTarget target)
    {
        await using var client = await CreateClientWithNoServer(target, spec => spec.ConnectionTimeoutMs = 100);

        await AssertThrows<TransportException>(() => client.ConnectAsync().Timeout(T(10)));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
    public async Task ClientTimesOutWithNoServer(ClientTarget target)
    {
        await using var client = await CreateClientWithNoServer(target, spec => spec.ConnectionTimeoutMs = 50);

        await AssertThrows<TransportException>(() => client.ConnectAsync().Timeout(T(10)));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
    public async Task ConnectTimesOutWithNoServer(ClientTarget target)
    {
        await using var client = await CreateClientWithNoServer(target);

        await AssertThrows<TransportException>(() => client.ConnectAsync().Timeout(T(TestTimeouts.Connect)));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
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

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
    public async Task ReconnectsStopsAfterSpecifiedTimes(ClientTarget target)
    {
        await using var server = await StartDedicatedServer(AbruptStop());
        await using var client = await Connect(target, server, spec =>
        {
            spec.ConnectionTimeoutMs = 100;
            spec.ReconnectDelaysMs = [20];
            spec.ReconnectContinuously = false;
        });

        await server.StopAsync();

        // One reconnect attempt fails, then the client gives up.
        await Script(client).OnDisconnected.NextCallAsync().AsTask().Timeout(T(5));
        await client.WaitForStateAsync(ConnectionState.Disconnected, T(1));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
    public async Task ReadyTaskCompletesUponConnection(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await Connect(target, server);

        await client.WhenReadyAsync(T(1));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
    public async Task DisconnectTaskCompletesUponDisconnection(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await Connect(target, server);

        await server.StopAsync();

        await client.WhenDisconnectedAsync(T(1));
        await client.WaitForStateAsync(ConnectionState.Disconnected, T(1));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
    public async Task FiresOnDisconnectedEvent(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await Connect(target, server);

        await client.DisconnectAsync().Timeout(T(1));

        await Script(client).OnDisconnected.NextCallAsync().AsTask().Timeout(T(1));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
    public async Task ProxyInvocationPropagatesServerException(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await Connect(target, server);

        // The server method throws.
        await Assert.ThrowsExactlyAsync<ProxyRemoteInvocationException>(
            async () => await client.Proxy<IInteropServerNexus>().ThrowWithResult("boom").Timeout(T(1)));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
    public async Task DisconnectWithoutConnectDoesNotThrow(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await CreateClient(target, server);

        // Should complete without throwing.
        await Assert.That(async () => await client.DisconnectAsync().Timeout(T(1))).ThrowsNothing();
        await client.WaitForStateAsync(ConnectionState.Disconnected, T(1));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
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
/// C#-only companions of the client-matrix tests above, on the C# transports the client matrix has no row for yet
/// (QUIC: the interop server has no QUIC endpoint). They are the pre-port tests, unchanged but for the transport list,
/// so the C# client keeps its QUIC lifecycle coverage until the matrix gets C#-only rows (H16).
/// </summary>
internal partial class NexusClientTests
{
    [Test]
    [Transports(Type.Quic)]
    public async Task NexusFiresOnConnected_CSharpTransports(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        clientNexus.OnConnectedEvent = (_, _) =>
        {
            tcs.SetResult();
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        await tcs.Task.Timeout(T(1));
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ConnectsToServer_CSharpTransports(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, _) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        // Observed on the server: its nexus for this client connected.
        server.OnNexusCreated = nexus => nexus.OnConnectedEvent = _ =>
        {
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        await tcs.Task.Timeout(T(1));
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ClientFailsGracefullyWithNoServer_CSharpTransports(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var (_, client, _, _, _) = CreateServerClientWithStoppedServer(
            CreateServerConfig(type),
            clientConfig);

        clientConfig.ConnectionTimeout = 100;

        await AssertThrows<TransportException>(() => client.ConnectAsync().Timeout(T(10)));
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ClientTimesOutWithNoServer_CSharpTransports(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var (_, client, _, _, _) = CreateServerClientWithStoppedServer(
            CreateServerConfig(type),
            clientConfig);

        clientConfig.ConnectionTimeout = 50;

        await AssertThrows<TransportException>(() => client.ConnectAsync().Timeout(T(100)));
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ConnectsAndDisconnectsMultipleTimesFromServer_CSharpTransports(Type type)
    {
        var (server, client, _) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        for (int i = 0; i < 5; i++)
        {
            await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
            var disconnected = client.DisconnectedTask;
            await client.DisconnectAsync().Timeout(T(1));
            await disconnected.Timeout(T(1));
        }
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ConnectTimesOutWithNoServer_CSharpTransports(Type type)
    {
        var (_, client, _, _, _) = CreateServerClientWithStoppedServer(
            CreateServerConfig(type),
            CreateClientConfig(type));

        await AssertThrows<TransportException>(async () => await client.ConnectAsync());
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ClientProvidesAuthenticationToken_CSharpTransports(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, _) = CreateServerClient(
            serverConfig,
            clientConfig);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        clientConfig.Authenticate = () => new byte[] { 123 };
        ServerTaps.OnReceive(serverConfig, (_, frame) =>
        {
            if (frame.Type != MessageType.ClientGreeting)
                return;

            var message = TestSerialization.DeserializeMessage<ClientGreetingMessage>(frame.Body);

            if (message!.AuthenticationToken!.Span[0] == 123)
            {
                tcs.SetResult();
                return;
            }
            tcs.SetException(new Exception("Client didn't send token"));
        });
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        await tcs.Task.Timeout(T(1));
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ClientSendsPing_CSharpTransports(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        clientConfig.PingInterval = 20;
        var serverConfig = CreateServerConfig(type);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (server, client, _) = CreateServerClient(
            serverConfig,
            clientConfig);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        ServerTaps.OnReceive(serverConfig, (_, frame) =>
        {
            if (frame.Type == MessageType.Ping)
                tcs.TrySetResult();
        });

        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        await tcs.Task.Timeout(T(1));
    }

    [Test]
    [Transports(Except = new[] { Type.WebSocket, Type.HttpSocket })]
    public async Task ReconnectsOnDisconnect_CSharpTransports(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        var (server, client, clientNexus) = CreateServerClient(serverConfig, clientConfig);
        var reconnecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        clientConfig.ReconnectionPolicy = new DefaultReconnectionPolicy(new[] { TimeSpan.FromMilliseconds(20) });

        clientNexus.OnConnectedEvent = (_, isReconnected) =>
        {
            if (isReconnected)
                tcs.TrySetResult();
            return ValueTask.CompletedTask;
        };
        clientNexus.OnReconnectingEvent = _ =>
        {
            reconnecting.TrySetResult();
            return ValueTask.CompletedTask;
        };

        serverConfig.InternalNoLingerOnShutdown = true;
        serverConfig.InternalForceDisableSendingDisconnectSignal = true;

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await server.StopAsync();
        
        // Wait for the client to process the disconnect.
        await reconnecting.Task.Timeout(T(1));
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        // The client's first reconnect attempt starts about 20 ms after the disconnect, while the server is still
        // stopped, and the transport retransmits on its own schedule rather than failing fast. QUIC drops the
        // Initial packet silently and resends it after the first probe timeout (about 1 s: RFC 9002 initial RTT
        // 333 ms x 3), so the reconnect lands about 900 ms after the restart; on Windows a TCP SYN to a closed
        // loopback port is retried after about 500 ms. A 1 s wait sat right at QUIC's retransmit and failed under
        // load, so the wait covers a second retransmit too.
        await tcs.Task.Timeout(T(TestTimeouts.Connect));
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ReconnectsOnTimeout_CSharpTransports(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        var (server, client, clientNexus) = CreateServerClient(serverConfig, clientConfig);

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

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        await tcs.Task.Timeout(T(1));
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ReconnectsNotifiesReconnecting_CSharpTransports(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        clientConfig.ReconnectionPolicy = new DefaultReconnectionPolicy();
        var (server, client, clientNexus) = CreateServerClient(serverConfig, clientConfig);

        clientNexus.OnReconnectingEvent = _ =>
        {
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        };

        serverConfig.InternalNoLingerOnShutdown = true;
        serverConfig.InternalForceDisableSendingDisconnectSignal = true;
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await server.StopAsync();
        await tcs.Task.Timeout(T(1000));
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ReconnectsStopsAfterSpecifiedTimes_CSharpTransports(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        var (server, client, clientNexus) = CreateServerClient(serverConfig, clientConfig);

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

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        // Only the reconnect attempt must time out fast: a first QUIC handshake under load can take over 100 ms.
        clientConfig.ConnectionTimeout = 100;

        await server.StopAsync();

        await tcs.Task.Timeout(T(5));
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ClientProxyInvocationCancelsOnDisconnect_CSharpTransports(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (server, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));
        
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OnNexusCreated = nexus => nexus.ServerTaskValueEvent = async _ =>
        {
            invoked.TrySetResult();
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

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        // The call reached the server before it stops.
        await invoked.Task.Timeout(T(1));
        await server.StopAsync();
        await tcs.Task.Timeout(T(1));
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ReadyTaskCompletesUponConnection_CSharpTransports(Type type)
    {
        var (server, client, _) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ReadyTaskCompletesUponAuthentication_CSharpTransports(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.Authenticate = true;
        
        bool authCompleted = false;
        var (server, client, _) = CreateServerClient(
            serverConfig,
            CreateClientConfig(type));
        
        server.OnNexusCreated = nexus => nexus.OnAuthenticateEvent = hub =>
        {
            authCompleted = true;
            return ValueTask.FromResult<IIdentity?>(new DefaultIdentity());
        };

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        
        await Assert.That(authCompleted).IsTrue();
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ReadyTaskCompletesUponAuthFailure_CSharpTransports(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.Authenticate = true;

        var (server, client, _) = CreateServerClient(
            serverConfig,
            CreateClientConfig(type));

        server.OnNexusCreated = nexus => nexus.OnAuthenticateEvent = hub => ValueTask.FromResult<IIdentity?>(null);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        await client.TryConnectAsync().Timeout(T(TestTimeouts.Connect));
        await WaitForState(client, ConnectionState.Disconnected);
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task DisconnectTaskCompletesUponDisconnection_CSharpTransports(Type type)
    {
        var (server, client, _) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        server.OnNexusCreated = nexus => nexus.OnAuthenticateEvent = hub => ValueTask.FromResult<IIdentity?>(null);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        var disconnectTask = client.DisconnectedTask;

        await server.StopAsync();

        await disconnectTask.Timeout(T(1));

        await WaitForState(client, ConnectionState.Disconnected);
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task DisconnectTaskCompletesUponAuthFailure_CSharpTransports(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.Authenticate = true;

        var (server, client, _) = CreateServerClient(
            serverConfig,
            CreateClientConfig(type));

        server.OnNexusCreated = nexus => nexus.OnAuthenticateEvent = hub => ValueTask.FromResult<IIdentity?>(null);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.TryConnectAsync().Timeout(T(TestTimeouts.Connect));
        await client.DisconnectedTask.Timeout(T(1));

        await WaitForState(client, ConnectionState.Disconnected);
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task DisconnectTaskCompletesAfterServerStops_CSharpTransports(Type type)
    {
        // Arrange
        var serverConfig = CreateServerConfig(type);
        var clientConfig = CreateClientConfig(type);
        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        // Act
        await server.StopAsync().Timeout(T(TestTimeouts.Connect));

        // Assert: client.DisconnectedTask will complete promptly
        await client.DisconnectedTask.Timeout(T(1));
        await WaitForState(client, ConnectionState.Disconnected);
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ClientSendsDisconnectSignal_CSharpTransports(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverConfig = CreateServerConfig(type);
        ServerTaps.OnReceive(serverConfig, (_, frame) =>
        {
            if (frame.Type == MessageType.DisconnectGraceful)
                tcs.TrySetResult();
        });

        var (server, client, _) = CreateServerClient(
            serverConfig,
            CreateClientConfig(type));

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await client.DisconnectAsync().Timeout(T(1));

        await tcs.Task.Timeout(T(1));
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task FiresOnDisconnectedEvent_CSharpTransports(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        clientNexus.OnDisconnectedEvent = _ =>
        {
            tcs.SetResult();
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await client.DisconnectAsync().Timeout(T(1));

        await tcs.Task.Timeout(T(1));
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ProxyInvocationPropagatesServerException_CSharpTransports(Type type)
    {
        var (server, client, _) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        // Server throws
        server.OnNexusCreated = nexus => nexus.ServerTaskValueEvent = _ => throw new InvalidOperationException("boom");

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        await Assert.ThrowsExactlyAsync<ProxyRemoteInvocationException>(() => client.Proxy.ServerTaskValue().Timeout(T(1)));
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task DisconnectWithoutConnectDoesNotThrow_CSharpTransports(Type type)
    {
        var (_, client, _) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));
        
        // Should complete without throwing
        await Assert.That(async () => await client.DisconnectAsync().Timeout(T(1))).ThrowsNothing();
        await WaitForState(client, ConnectionState.Disconnected);
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task DoubleConnectDoesNotThrow_CSharpTransports(Type type)
    {
        var (server, client, _) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        // Second ConnectAsync should throw
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task DoubleDisconnectDoesNotThrow_CSharpTransports(Type type)
    {
        var (server, client, _) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        
        await WaitForState(client, ConnectionState.Connected);
        // First disconnect
        await client.DisconnectAsync().Timeout(T(1));
        
        // Second disconnect should be a no-op
        await Assert.That(async () => await client.DisconnectAsync().Timeout(T(1))).ThrowsNothing();
        await WaitForState(client, ConnectionState.Disconnected);
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ConcurrentProxyInvocations_CSharpTransports(Type type)
    {
        var (server, client, _) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        // Server always returns 42
        server.OnNexusCreated = nexus => nexus.ServerTaskValueEvent = _ => ValueTask.FromResult(42);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        // Fire off 20 simultaneous calls
        var calls = Enumerable.Range(0, 20)
            .Select(_ => client.Proxy.ServerTaskValue().AsTask())
            .ToArray();

        var results = await Task.WhenAll(calls).Timeout(T(1));
        await Assert.That(results.Count(r => r == 42)).IsEqualTo(20);
    }

    [Test]
    [Transports(Except = new[] { Type.WebSocket, Type.HttpSocket })]
    public async Task ServerRejectsAuth_SendsDisconnect_CSharpTransports(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.Authenticate = true;
        // Signalled when any frame the server sends is the authentication disconnect.
        var disconnectSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ServerTaps.OnSend(serverConfig, (_, frame) =>
        {
            if (frame.Type == MessageType.DisconnectAuthentication)
                disconnectSent.TrySetResult();
        });

        var clientConfig = CreateClientConfig(type);
        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);

        // Force authentication to fail
        server.OnNexusCreated = nexus => nexus.OnAuthenticateEvent = _ => ValueTask.FromResult<IIdentity?>(null);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        var result = await client.TryConnectAsync().Timeout(T(TestTimeouts.Connect));

        await Task.WhenAny(disconnectSent.Task, Task.Delay(TimeSpan.FromSeconds(T(1))));

        using (Assert.Multiple())
        {
            await Assert.That(disconnectSent.Task.IsCompleted).IsTrue().Because("Server should send a Disconnect message when auth is rejected");
            await Assert.That(result.DisconnectReason).IsEqualTo(DisconnectReason.Authentication).Because("The client should observe the authentication rejection, not a bare socket close.");

        }
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ProxyInvocationAfterDisconnectThrows_CSharpTransports(Type type)
    {
        var (server, client, _) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        // Server will respond, but we will disconnect before calling
        server.OnNexusCreated = nexus => nexus.ServerTaskValueEvent = _ => ValueTask.FromResult(1);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await client.DisconnectAsync().Timeout(T(1));

        // Any RPC after clean disconnect should throw
        await AssertThrows<InvalidOperationException>(() => client.Proxy.ServerTaskValue().Timeout(T(1)));
    }

    /// <summary>
    /// If the reconnection policy is null, OnReconnectingEvent must never fire after a disconnect.
    /// </summary>
    [Test]
    [Transports(Type.Quic)]
    public async Task OnReconnectingEventNotFiredWithNoRetries_CSharpTransports(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        // Zero‐retry policy
        clientConfig.ReconnectionPolicy = null;

        var serverConfig = CreateServerConfig(type);
        // Ensure no clean‐disconnect signal is sent
        serverConfig.InternalNoLingerOnShutdown = true;
        serverConfig.InternalForceDisableSendingDisconnectSignal = true;

        var (server, client, clientNexus) = CreateServerClient(serverConfig, clientConfig);

        var reconnectingFired = false;
        clientNexus.OnReconnectingEvent = _ =>
        {
            reconnectingFired = true;
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await server.StopAsync().Timeout(T(TestTimeouts.Connect));
        await client.DisconnectedTask.Timeout(T(1));

        // The reconnect decision runs right after the disconnect, and no event marks that it is over: give it a
        // moment to (not) trigger.
        await Task.Delay(50);
        await Assert.That(reconnectingFired).IsFalse().Because("With a disabled reconnection policy, OnReconnectingEvent must not fire.");
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
