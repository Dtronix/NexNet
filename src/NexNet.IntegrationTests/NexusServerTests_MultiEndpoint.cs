using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using NexNet.Asp;
using NexNet.Asp.HttpSocket;
using NexNet.Asp.WebSocket;
using NexNet.Diagnostics;
using NexNet.IntegrationTests.Metrics;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NexNet.Quic;
using NexNet.RateLimiting;
using NexNet.Transports;
using NexNet.Transports.Uds;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// One server accepting sessions on several endpoints at once (TCP, UDS, WebSocket, HttpSocket, ...).
/// </summary>
internal class NexusServerTests_MultiEndpoint : BaseAspTests
{
    private static readonly Type[] AllTransports = [Type.Tcp, Type.Uds, Type.WebSocket, Type.HttpSocket];

    /// <summary>
    /// Stops this test's servers and ASP.NET apps before the next test starts, so their shutdown work does not
    /// overlap it (the base TearDown only starts stopping them).
    /// </summary>
    [After(HookType.Test)]
    public async Task StopServersAndApps()
    {
        foreach (var server in TrackedServers)
        {
            if (server.State == NexusServerState.Running)
                await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        foreach (var app in TrackedAspServers)
        {
            try
            {
                await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        while (!condition())
            await Task.Delay(10);
    }

    private async Task<Dictionary<Type, (NexusClient<ClientNexus, ClientNexus.ServerProxy> Client, ClientNexus Nexus)>>
        ConnectAll(MultiEndpointServer multi)
    {
        var clients = new Dictionary<Type, (NexusClient<ClientNexus, ClientNexus.ServerProxy>, ClientNexus)>();
        foreach (var type in multi.Transports)
        {
            var (client, nexus) = CreateClient(multi.CreateClientConfig(type));
            await client.ConnectAsync().Timeout(2);
            await Assert.That(client.State).IsEqualTo(ConnectionState.Connected).Because($"{type} client did not connect.");
            clients[type] = (client, nexus);
        }

        return clients;
    }

    [Test]
    public async Task Endpoints_ListedInOrder()
    {
        var multi = CreateMultiEndpointServer(AllTransports);

        await Assert.That(multi.Server.Endpoints.Select(e => e.Name)).IsEquivalentTo(new[] { "tcp", "uds", "websocket", "httpsocket" }, CollectionOrdering.Matching);
        await Assert.That(multi.Server.Endpoints[2]).IsEqualTo(new NexusEndpointInfo("websocket", "websocket", false, "/websocket-test"));

        await multi.Server.StartAsync().Timeout(TestTimeouts.Connect);
        await ConnectAll(multi);

        await WaitFor(() => multi.Server.GetSnapshot().Sessions.Count == AllTransports.Length).Timeout(2);
        var snapshot = multi.Server.GetSnapshot();
        await Assert.That(snapshot.Endpoints.Select(e => e.Name)).IsEquivalentTo(new[] { "tcp", "uds", "websocket", "httpsocket" }, CollectionOrdering.Matching);
        await Assert.That(snapshot.Endpoints.All(e => e.State == NexusEndpointState.Running && e.Sessions == 1)).IsTrue();
        await Assert.That(snapshot.Endpoints[0].IsListener).IsTrue();
        await Assert.That(snapshot.Endpoints[3].IsListener).IsFalse();
        await Assert.That(snapshot.Endpoints[3].Address).IsEqualTo("/httpsocket-test");
        await Assert.That(snapshot.Sessions.Select(s => s.Endpoint!)).IsEquivalentTo(new[] { "tcp", "uds", "websocket", "httpsocket" });

        await multi.Server.StopAsync().Timeout(2);
        await Assert.That(multi.Server.GetSnapshot().Endpoints.All(e => e.State == NexusEndpointState.Stopped)).IsTrue();
    }

    [Test]
    public async Task BoundEndPoint_PortZero_ReportsPickedPortClientsConnectTo()
    {
        var types = new[] { Type.Tcp, Type.TcpTls, Type.Uds };
        var multi = CreateMultiEndpointServer(null,
            (_, config) =>
            {
                if (config is TcpServerConfig tcp)
                    tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            },
            types);
        var server = multi.Server;
        await Assert.That(server.Endpoints.All(e => e.BoundEndPoint == null)).IsTrue().Because("Nothing is bound before start.");

        for (var run = 0; run < 2; run++)
        {
            await server.StartAsync().Timeout(TestTimeouts.Connect);

            var snapshot = server.GetSnapshot();
            foreach (var type in new[] { Type.Tcp, Type.TcpTls })
            {
                var name = multi.Configs[type].TransportName;
                var bound = server.Endpoints.Single(e => e.Name == name).BoundEndPoint as IPEndPoint;
                await Assert.That(bound).IsNotNull().Because($"{name}: no bound end point.");
                await Assert.That(bound!.Port).IsNotZero().Because($"{name}: port 0 was not resolved.");
                await Assert.That(bound.Address).IsEqualTo(IPAddress.Loopback);
                await Assert.That(snapshot.Endpoints.Single(e => e.Name == name).Address).IsEqualTo(bound.ToString());

                var clientConfig = (TcpClientConfig)multi.CreateClientConfig(type);
                clientConfig.EndPoint = bound;
                var (client, _) = CreateClient(clientConfig);
                await client.ConnectAsync().Timeout(2);
                await Assert.That(client.State).IsEqualTo(ConnectionState.Connected).Because($"{name}: client did not connect.");
            }

            var udsBound = server.Endpoints.Single(e => e.Name == "uds").BoundEndPoint;
            await Assert.That(udsBound?.ToString()).IsEqualTo(((UdsServerConfig)multi.Configs[Type.Uds]).EndPoint.ToString());

            await server.StopAsync().Timeout(2);
            await Assert.That(server.Endpoints.All(e => e.BoundEndPoint == null)).IsTrue().Because("Stop clears the bound end points.");
            await Assert.That(server.GetSnapshot().Endpoints[0].Address).IsEqualTo(new IPEndPoint(IPAddress.Loopback, 0).ToString()).Because("A stopped endpoint reports its configured address.");
        }
    }

    [Test]
    public async Task BoundEndPoint_QuicPortZero_ReportsPickedPortClientsConnectTo()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Skip.Test("QUIC (msquic) is not available on this platform.");

        var multi = CreateMultiEndpointServer(null,
            (_, config) =>
            {
                if (config is QuicServerConfig quic)
                    quic.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            },
            Type.Tcp, Type.Quic);
        var server = multi.Server;
        var name = multi.Configs[Type.Quic].TransportName;
        await Assert.That(server.Endpoints.Single(e => e.Name == name).BoundEndPoint).IsNull().Because("Nothing is bound before start.");

        await server.StartAsync().Timeout(TestTimeouts.Connect);

        var bound = server.Endpoints.Single(e => e.Name == name).BoundEndPoint as IPEndPoint;
        await Assert.That(bound).IsNotNull().Because("QUIC: no bound end point.");
        await Assert.That(bound!.Port).IsNotZero().Because("QUIC: port 0 was not resolved.");
        await Assert.That(bound.Address).IsEqualTo(IPAddress.Loopback);
        await Assert.That(server.GetSnapshot().Endpoints.Single(e => e.Name == name).Address).IsEqualTo(bound.ToString());

        var clientConfig = (QuicClientConfig)multi.CreateClientConfig(Type.Quic);
        clientConfig.EndPoint = bound;
        var (client, _) = CreateClient(clientConfig);
        await client.ConnectAsync().Timeout(5);
        await Assert.That(client.State).IsEqualTo(ConnectionState.Connected).Because("QUIC client did not connect to the picked port.");

        await server.StopAsync().Timeout(2);
        await Assert.That(server.Endpoints.Single(e => e.Name == name).BoundEndPoint).IsNull().Because("Stop clears the bound end point.");
        await Assert.That(server.GetSnapshot().Endpoints.Single(e => e.Name == name).Address).IsEqualTo(new IPEndPoint(IPAddress.Loopback, 0).ToString()).Because("A stopped endpoint reports its configured address.");
    }

    [Test]
    public async Task Broadcasts_ReachClientsOnEveryTransport()
    {
        var serverVoidCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var multi = CreateMultiEndpointServer(
            nexus =>
            {
                nexus.OnConnectedEvent = async n => await n.Context.Groups.AddAsync("everyone");
                // A client on one transport invoking Others reaches the clients on every other transport.
                nexus.ServerVoidEvent = n =>
                {
                    n.Context.Clients.Others.ClientVoid();
                    serverVoidCalled.TrySetResult();
                };
            },
            null,
            AllTransports);
        await multi.Server.StartAsync().Timeout(TestTimeouts.Connect);
        var clients = await ConnectAll(multi);

        var all = new Dictionary<Type, TaskCompletionSource>();
        var group = new Dictionary<Type, TaskCompletionSource<int>>();
        foreach (var (type, (_, nexus)) in clients)
        {
            var allTcs = all[type] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var groupTcs = group[type] = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            nexus.ClientVoidEvent = _ => allTcs.TrySetResult();
            nexus.ClientVoidWithParamEvent = (_, value) => groupTcs.TrySetResult(value);
        }

        // Wait for every session to be registered and in its group.
        await WaitFor(() => multi.Server.GetSnapshot().Sessions.Count == AllTransports.Length).Timeout(2);

        using (var context = multi.Server.ContextProvider.Rent())
        {
            context.Proxy.All.ClientVoid();
            context.Proxy.Group("everyone").ClientVoidWithParam(42);
        }

        await Task.WhenAll(all.Values.Select(t => t.Task)).Timeout(2);
        foreach (var (type, tcs) in group)
            await Assert.That(await tcs.Task.Timeout(2)).IsEqualTo(42).Because($"{type} client missed the group broadcast.");

        // ServerVoid on the UDS client's session broadcasts to the clients on the other transports.
        foreach (var type in all.Keys.ToList())
        {
            var allTcs = all[type] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            clients[type].Nexus.ClientVoidEvent = _ => allTcs.TrySetResult();
        }

        clients[Type.Uds].Client.Proxy.ServerVoid();
        await serverVoidCalled.Task.Timeout(2);
        await Task.WhenAll(all.Where(p => p.Key != Type.Uds).Select(p => p.Value.Task)).Timeout(2);
        await Task.Delay(50);
        await Assert.That(all[Type.Uds].Task.IsCompleted).IsFalse().Because("Others must not include the caller.");
    }

    [Test]
    public async Task Collections_SyncAcrossTransports()
    {
        var multi = CreateMultiEndpointServer(AllTransports);
        await multi.Server.StartAsync().Timeout(TestTimeouts.Connect);
        var clients = await ConnectAll(multi);

        foreach (var (client, _) in clients.Values)
            await Assert.That(await client.Proxy.IntListBi.EnableAsync().Timeout(2)).IsTrue();

        // A client on one transport adds; the clients on the others see it.
        await clients[Type.WebSocket].Client.Proxy.IntListBi.AddAsync(7).Timeout(2);

        using (var context = multi.Server.ContextProvider.Rent())
            await context.Collections.IntListBi.AddAsync(8).Timeout(2);

        foreach (var (type, (client, _)) in clients)
        {
            await WaitFor(() => client.Proxy.IntListBi.Count == 2).Timeout(2);
            await Assert.That(client.Proxy.IntListBi).IsEquivalentTo(new[] { 7, 8 }, CollectionOrdering.Matching).Because($"{type} list differs.");
        }
    }

    [Test]
    public async Task ContextEndpoint_CorrectOnEveryTransport()
    {
        var seen = new Dictionary<string, (NexusEndpointInfo? AtAuthenticate, NexusEndpointInfo? AtConnect)>();
        var seenLock = new Lock();
        var types = new[] { Type.TcpTls, Type.Tcp, Type.Uds, Type.WebSocket, Type.HttpSocket };
        var multi = CreateMultiEndpointServer(nexus =>
            {
                NexusEndpointInfo? atAuthenticate = null;
                nexus.OnAuthenticateEvent = n =>
                {
                    atAuthenticate = n.Context.Endpoint;
                    return ValueTask.FromResult<IIdentity?>(new DefaultIdentity());
                };
                nexus.OnConnectedEvent = n =>
                {
                    lock (seenLock)
                        seen[n.Context.Endpoint!.Name] = (atAuthenticate, n.Context.Endpoint);
                    return ValueTask.CompletedTask;
                };
            },
            (_, config) => config.Authenticate = true,
            types);
        await multi.Server.StartAsync().Timeout(TestTimeouts.Connect);
        await ConnectAll(multi);
        await WaitFor(() => { lock (seenLock) return seen.Count == types.Length; }).Timeout(2);

        // Listener endpoints carry the end point they bound.
        EndPoint? Bound(string name) => multi.Server.Endpoints.Single(e => e.Name == name).BoundEndPoint;

        using (Assert.Multiple())
        {
            await Assert.That(seen["tls"].AtConnect).IsEqualTo(new NexusEndpointInfo("tls", "tls", true, null) { BoundEndPoint = Bound("tls") });
            await Assert.That(seen["tcp"].AtConnect).IsEqualTo(new NexusEndpointInfo("tcp", "tcp", false, null) { BoundEndPoint = Bound("tcp") });
            await Assert.That(seen["uds"].AtConnect).IsEqualTo(new NexusEndpointInfo("uds", "uds", false, null) { BoundEndPoint = Bound("uds") });
            await Assert.That(Bound("tls")).IsNotNull();
            await Assert.That(Bound("tcp")).IsNotNull();
            await Assert.That(seen["websocket"].AtConnect).IsEqualTo(new NexusEndpointInfo("websocket", "websocket", false, "/websocket-test"));
            await Assert.That(seen["httpsocket"].AtConnect).IsEqualTo(new NexusEndpointInfo("httpsocket", "httpsocket", false, "/httpsocket-test"));
            foreach (var (name, (atAuthenticate, atConnect)) in seen)
                await Assert.That(atAuthenticate).IsEqualTo(atConnect).Because($"{name}: Endpoint differs in OnAuthenticate.");

        }
    }

    [Test]
    public async Task ContextEndpoint_AspOverHttps_IsSecure()
    {
        AspListenOptions = listen => listen.UseHttps(
            System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(Path.Combine("certs", "server.pfx"), "certPass"));

        NexusEndpointInfo? endpoint = null;
        var multi = CreateMultiEndpointServer(
            nexus => nexus.OnConnectedEvent = n =>
            {
                endpoint = n.Context.Endpoint;
                return ValueTask.CompletedTask;
            },
            null,
            Type.Tcp, Type.WebSocket);
        await multi.Server.StartAsync().Timeout(TestTimeouts.Connect);

        var clientConfig = (HttpClientConfig)multi.CreateClientConfig(Type.WebSocket);
        clientConfig.Url = new UriBuilder(clientConfig.Url) { Scheme = "wss" }.Uri;
        clientConfig.SslClientAuthenticationOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
        };
        var (client, _) = CreateClient(clientConfig);
        await client.ConnectAsync().Timeout(2);

        await WaitFor(() => endpoint != null).Timeout(2);
        await Assert.That(endpoint).IsEqualTo(new NexusEndpointInfo("websocket", "websocket", true, "/websocket-test"));
        await Assert.That(multi.Server.Endpoints[1].IsSecure).IsFalse().Because("The endpoint itself does not know the scheme.");
    }

    [Test]
    public async Task PerEndpoint_MaxMessageSize_Honored()
    {
        // The WebSocket endpoint is mapped with a non-primary configuration: its session settings apply.
        var multi = CreateMultiEndpointServer(null,
            (type, config) =>
            {
                if (type == Type.WebSocket)
                    config.MaxMessageSize = 64 * 1024;
            },
            Type.Tcp, Type.WebSocket);
        await multi.Server.StartAsync().Timeout(TestTimeouts.Connect);
        var clients = await ConnectAll(multi);

        var data = new byte[100 * 1024];
        await clients[Type.Tcp].Client.Proxy.ServerData(data).Timeout(2);
        await AssertThrows<NexusMessageTooLargeException>(
            () => clients[Type.WebSocket].Client.Proxy.ServerData(data).AsTask().Timeout(2));
    }

    [Test]
    public async Task PerEndpoint_Authenticate_Honored()
    {
        var authenticated = new List<string>();
        var multi = CreateMultiEndpointServer(nexus =>
                nexus.OnAuthenticateEvent = n =>
                {
                    lock (authenticated)
                        authenticated.Add(n.Context.Endpoint!.Name);
                    // Refuse everyone: only the UDS endpoint asks.
                    return ValueTask.FromResult<IIdentity?>(null);
                },
            (type, config) => config.Authenticate = type == Type.Uds,
            Type.Tcp, Type.Uds);
        await multi.Server.StartAsync().Timeout(TestTimeouts.Connect);

        var (tcpClient, _) = CreateClient(multi.CreateClientConfig(Type.Tcp));
        await tcpClient.ConnectAsync().Timeout(2);
        await Assert.That(tcpClient.State).IsEqualTo(ConnectionState.Connected);

        var (udsClient, _) = CreateClient(multi.CreateClientConfig(Type.Uds));
        var result = await udsClient.TryConnectAsync().Timeout(2);
        await Assert.That(result.Success).IsFalse().Because("The UDS endpoint requires authentication.");

        string[] seen;
        lock (authenticated)
            seen = authenticated.ToArray();
        await Assert.That(seen).IsEquivalentTo(new[] { "uds" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task PrimaryAuthenticate_RequiresEveryEndpointToAuthenticate()
    {
        var multi = CreateMultiEndpointServer(null,
            (type, config) => config.Authenticate = type != Type.Uds,
            Type.Tcp, Type.Uds);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => multi.Server.StartAsync());
        await Assert.That(exception!.Message).Contains("'uds'");
        await Assert.That(multi.Server.State).IsEqualTo(NexusServerState.Stopped);

        multi.Configs[Type.Uds].Authenticate = true;
        await multi.Server.StartAsync().Timeout(TestTimeouts.Connect);
        await Assert.That(multi.Server.State).IsEqualTo(NexusServerState.Running);
    }

    [Test]
    public async Task PerEndpoint_Timeout_Honored()
    {
        var multi = CreateMultiEndpointServer(null,
            (type, config) =>
            {
                if (type == Type.Uds)
                    config.Timeout = 300;
            },
            Type.Tcp, Type.Uds);
        await multi.Server.StartAsync().Timeout(TestTimeouts.Connect);

        // Clients that do not ping within the UDS endpoint's timeout.
        var tcpConfig = multi.CreateClientConfig(Type.Tcp);
        var udsConfig = multi.CreateClientConfig(Type.Uds);
        tcpConfig.PingInterval = 5_000;
        udsConfig.PingInterval = 5_000;
        var (tcpClient, _) = CreateClient(tcpConfig);
        var (udsClient, _) = CreateClient(udsConfig);
        await tcpClient.ConnectAsync().Timeout(2);
        await udsClient.ConnectAsync().Timeout(2);

        var reason = await ((Task<DisconnectReason>)udsClient.DisconnectedTask).Timeout(3);
        await Assert.That(reason).IsEqualTo(DisconnectReason.Timeout);
        await Assert.That(tcpClient.State).IsEqualTo(ConnectionState.Connected);
    }

    [Test]
    public async Task Metrics_TaggedPerEndpoint()
    {
        using var recorder = new MetricsRecorder();
        var multi = CreateMultiEndpointServer(null,
            (type, config) =>
            {
                // Only the primary configuration's meter factory is used.
                if (type == Type.Tcp)
                    config.MeterFactory = recorder;
            },
            Type.Tcp, Type.Uds, Type.WebSocket);
        await multi.Server.StartAsync().Timeout(TestTimeouts.Connect);
        await ConnectAll(multi);

        foreach (var (endpoint, transport) in new[] { ("tcp", "tcp"), ("uds", "uds"), ("websocket", "websocket") })
        {
            await WaitFor(() => recorder.ObserveSum("nexnet.session.active",
                ("nexnet.role", "server"), ("nexnet.endpoint", endpoint), ("nexnet.transport", transport)) == 1).Timeout(2);
        }

        await Assert.That(recorder.ObserveSum("nexnet.session.active", ("nexnet.role", "server"))).IsEqualTo(3);
    }

    [Test]
    public async Task BindFailureOnSecondEndpoint_RollsBackEverything()
    {
        var multi = CreateMultiEndpointServer(Type.Uds, Type.Tcp);
        var tcpPort = ((IPEndPoint)((TcpServerConfig)multi.Configs[Type.Tcp]).EndPoint).Port;

        var occupier = new TcpListener(IPAddress.Loopback, tcpPort);
        occupier.Start();
        try
        {
            var exception = await Assert.ThrowsExactlyAsync<TransportException>(() => multi.Server.StartAsync().Timeout(2));
            await Assert.That(exception!.Message).Contains("'tcp'");
            await Assert.That(multi.Server.State).IsEqualTo(NexusServerState.Stopped);

            // The UDS endpoint, bound first, was closed again.
            var (udsClient, _) = CreateClient(multi.CreateClientConfig(Type.Uds));
            var result = await udsClient.TryConnectAsync().Timeout(2);
            await Assert.That(result.Success).IsFalse();
        }
        finally
        {
            occupier.Stop();
        }

        await multi.Server.StartAsync().Timeout(2);
        await ConnectAll(multi);
    }

    [Test]
    public async Task RateLimitPerIp_SpansTransports()
    {
        var multi = CreateMultiEndpointServer(null,
            (type, config) =>
            {
                if (type == Type.Tcp)
                    config.RateLimiting = new ConnectionRateLimitConfig { MaxConnectionsPerIp = 1 };
            },
            Type.Tcp, Type.WebSocket);
        await multi.Server.StartAsync().Timeout(TestTimeouts.Connect);

        var (tcpClient, _) = CreateClient(multi.CreateClientConfig(Type.Tcp));
        await tcpClient.ConnectAsync().Timeout(2);
        await Assert.That(tcpClient.State).IsEqualTo(ConnectionState.Connected);

        // The same address on another transport counts against the same limit.
        var (webSocketClient, _) = CreateClient(multi.CreateClientConfig(Type.WebSocket));
        var result = await webSocketClient.TryConnectAsync().Timeout(3);
        await Assert.That(result.Success).IsFalse();

        await tcpClient.DisconnectAsync().Timeout(2);
        await WaitFor(() => multi.Server.GetSnapshot().RateLimiter!.Value.CurrentConnections == 0).Timeout(2);

        var (webSocketClient2, _) = CreateClient(multi.CreateClientConfig(Type.WebSocket));
        await webSocketClient2.ConnectAsync().Timeout(2);
        await Assert.That(webSocketClient2.State).IsEqualTo(ConnectionState.Connected);
    }

    [Test]
    public async Task AddEndpoint_Validation()
    {
        var multi = CreateMultiEndpointServer(Type.Tcp, Type.Uds);
        var server = multi.Server;
        var tcp = (TcpServerConfig)multi.Configs[Type.Tcp];

        using (Assert.Multiple())
        {
            // Duplicate name.
            Assert.ThrowsExactly<InvalidOperationException>(() => server.AddListener(
                new TcpServerConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, FreeTcpPort()) }, "uds"));
            // Same port.
            Assert.ThrowsExactly<InvalidOperationException>(() => server.AddListener(
                new TcpServerConfig { EndPoint = new IPEndPoint(IPAddress.Any, ((IPEndPoint)tcp.EndPoint).Port) }, "tcp2"));
            // Same UDS path.
            Assert.ThrowsExactly<InvalidOperationException>(() => server.AddListener(
                new UdsServerConfig { EndPoint = ((UdsServerConfig)multi.Configs[Type.Uds]).EndPoint }, "uds2"));
            // Same configuration instance.
            Assert.ThrowsExactly<InvalidOperationException>(() => server.AddListener(tcp, "tcp3"));
            // Not a listener configuration.
            Assert.ThrowsExactly<InvalidOperationException>(() => server.AddListener(
                new NexNet.Asp.WebSocket.WebSocketServerConfig { Path = "/ws" }, "ws"));
            // Server-wide settings.
            Assert.ThrowsExactly<InvalidOperationException>(() => server.AddListener(
                new TcpServerConfig
                {
                    EndPoint = new IPEndPoint(IPAddress.Loopback, FreeTcpPort()),
                    RateLimiting = new ConnectionRateLimitConfig { MaxConcurrentConnections = 1 }
                }, "tcp4"));
            Assert.ThrowsExactly<InvalidOperationException>(() => server.AddListener(
                new TcpServerConfig
                {
                    EndPoint = new IPEndPoint(IPAddress.Loopback, FreeTcpPort()),
                    Time = new FakeTime()
                }, "tcp5"));
            // Same ASP.NET path.
            server.AddExternalEndpoint(new NexNet.Asp.WebSocket.WebSocketServerConfig { Path = "/ws" }, "ws");
            Assert.ThrowsExactly<InvalidOperationException>(() => server.AddExternalEndpoint(
                new NexNet.Asp.HttpSocket.HttpSocketServerConfig { Path = "/WS" }, "hs"));

        }

        await Assert.That(server.Endpoints.Select(e => e.Name)).IsEquivalentTo(new[] { "tcp", "uds", "ws" }, CollectionOrdering.Matching);

        await server.StartAsync().Timeout(TestTimeouts.Connect);
        Assert.ThrowsExactly<InvalidOperationException>(() => server.AddListener(
            new TcpServerConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, FreeTcpPort()) }, "late"));

        var unconfigured = new NexusServer<ServerNexus, ServerNexus.ClientProxy>();
        Assert.ThrowsExactly<InvalidOperationException>(() => unconfigured.AddListener(
            new TcpServerConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, FreeTcpPort()) }, "tcp"));
    }

    [Test]
    public async Task AddEndpoint_QuicValidationAndAddress()
    {
        var multi = CreateMultiEndpointServer(Type.Tcp);
        var server = multi.Server;
        var udpPort = FreeUdpPort();
        var quic = new QuicServerConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, udpPort) };
        server.AddListener(quic, "quic");

        Assert.ThrowsExactly<InvalidOperationException>(() => server.AddListener(
                new QuicServerConfig { EndPoint = new IPEndPoint(IPAddress.IPv6Any, udpPort) }, "quic2"));

        // A TCP listener on the same port number is a different socket; another UDP port is free.
        server.AddListener(new TcpServerConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, udpPort) }, "tcp-same-number");
        var otherPort = FreeUdpPort();
        server.AddListener(new QuicServerConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, otherPort) }, "quic-other");
        // Port 0 binds an ephemeral port and never conflicts.
        server.AddListener(new QuicServerConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) }, "quic-any1");
        server.AddListener(new QuicServerConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) }, "quic-any2");

        var endpoints = server.GetSnapshot().Endpoints;
        await Assert.That(endpoints.Select(e => e.Name)).IsEquivalentTo(new[] { "tcp", "quic", "tcp-same-number", "quic-other", "quic-any1", "quic-any2" }, CollectionOrdering.Matching);
        await Assert.That(endpoints[1].Address).IsEqualTo($"127.0.0.1:{udpPort}");
        await Assert.That(endpoints[3].Address).IsEqualTo($"127.0.0.1:{otherPort}");
        await Assert.That(server.Endpoints[1]).IsEqualTo(new NexusEndpointInfo("quic", "quic", true, null));
    }

    [Test]
    public async Task ExternalEndpoint_AcceptsTransportsWithItsSettings()
    {
        // The TCP listener feeds a second server's external endpoint, like an application-owned listener would.
        NexusEndpointInfo? endpoint = null;
        var multi = CreateMultiEndpointServer(
            nexus => nexus.OnConnectedEvent = n =>
            {
                endpoint = n.Context.Endpoint;
                return ValueTask.CompletedTask;
            },
            null,
            Type.Uds);

        var tcpConfig = (TcpServerConfig)CreateServerConfig(Type.Tcp);
        var acceptor = multi.Server.AddExternalEndpoint(tcpConfig, "external");
        await multi.Server.StartAsync().Timeout(TestTimeouts.Connect);

        var listener = await tcpConfig.CreateServerListener(CancellationToken.None);
        await using var _ = new AsyncDisposableAction(() => listener!.CloseAsync(false));
        var accept = Task.Run(async () =>
        {
            var transport = await listener!.AcceptTransportAsync(CancellationToken.None);
            await acceptor.AcceptTransport(transport!);
        });

        var (client, _) = CreateClient(CreateClientConfig(Type.Tcp));
        await client.ConnectAsync().Timeout(2);
        await WaitFor(() => endpoint != null).Timeout(2);
        await Assert.That(endpoint!.Name).IsEqualTo("external");
        await Assert.That(endpoint.Transport).IsEqualTo("tcp");

        await client.DisconnectAsync().Timeout(2);
        await accept.Timeout(2);
    }

    [Test]
    public async Task HostedService_StartsAfterAppAndStopsTcpSessions()
    {
        var tcpConfig = (TcpServerConfig)CreateServerConfig(Type.Tcp);
        var udsConfig = (UdsServerConfig)CreateServerConfig(Type.Uds);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel((_, options) => options.Listen(IPAddress.Loopback, FreeTcpPort()));
        builder.Services.AddNexusServer<ServerNexus, ServerNexus.ClientProxy>(o =>
        {
            o.Configure(tcpConfig);
            o.AddListener(udsConfig, "uds");
        });
        await using var app = builder.Build();
        var server = app.Services.GetRequiredService<NexusServer<ServerNexus, ServerNexus.ClientProxy>>();
        await Assert.That(server.State).IsEqualTo(NexusServerState.Stopped);
        await Assert.That(server.Endpoints.Select(e => e.Name)).IsEquivalentTo(new[] { "tcp", "uds" }, CollectionOrdering.Matching);

        await app.StartAsync().Timeout(5);
        await WaitFor(() => server.State == NexusServerState.Running).Timeout(2);

        var (tcpClient, _) = CreateClient(CreateClientConfig(Type.Tcp));
        var (udsClient, _) = CreateClient(CreateClientConfig(Type.Uds));
        await tcpClient.ConnectAsync().Timeout(2);
        await udsClient.ConnectAsync().Timeout(2);

        await app.StopAsync().Timeout(5);
        await Assert.That(server.State).IsEqualTo(NexusServerState.Stopped);
        await Assert.That(await ((Task<DisconnectReason>)tcpClient.DisconnectedTask).Timeout(2)).IsEqualTo(DisconnectReason.ServerShutdown);
        await Assert.That(await ((Task<DisconnectReason>)udsClient.DisconnectedTask).Timeout(2)).IsEqualTo(DisconnectReason.ServerShutdown);
    }

    [Test]
    public async Task UseWebSocketNexusServer_AttachesToConfiguredServer()
    {
        var tcpConfig = (TcpServerConfig)CreateServerConfig(Type.Tcp);
        var aspPort = FreeTcpPort();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel((_, options) => options.Listen(IPAddress.Loopback, aspPort));
        builder.Services.AddNexusServer<ServerNexus, ServerNexus.ClientProxy>(o => o.Configure(tcpConfig));
        await using var app = builder.Build();

        var server = app.UseWebSocketNexusServerAsync<ServerNexus, ServerNexus.ClientProxy>(
            c => c.NexusConfig.Path = "/websocket-test");
        await Assert.That(server.Endpoints.Select(e => e.Name)).IsEquivalentTo(new[] { "tcp", "websocket" }, CollectionOrdering.Matching);

        await app.StartAsync().Timeout(5);
        await WaitFor(() => server.State == NexusServerState.Running).Timeout(2);

        var (tcpClient, _) = CreateClient(CreateClientConfig(Type.Tcp));
        await tcpClient.ConnectAsync().Timeout(2);

        CurrentTcpPort = aspPort;
        var (webSocketClient, _) = CreateClient(CreateClientConfig(Type.WebSocket));
        await webSocketClient.ConnectAsync().Timeout(2);
        await Assert.That(webSocketClient.State).IsEqualTo(ConnectionState.Connected);

        await app.StopAsync().Timeout(5);
    }

    [Test]
    public async Task AddedWebSocketEndpoint_ChecksItsAllowedOrigins()
    {
        const string allowed = "https://allowed.example";
        var multi = CreateMultiEndpointServer(null,
            (type, config) =>
            {
                if (config is WebSocketServerConfig webSocket)
                    webSocket.AllowedOrigins.Add(allowed);
            },
            Type.Tcp, Type.WebSocket);
        await Assert.That(multi.Server.Endpoints[1].Name).IsEqualTo("websocket").Because("The WebSocket is an added endpoint.");
        await multi.Server.StartAsync().Timeout(TestTimeouts.Connect);
        var url = ((HttpClientConfig)multi.CreateClientConfig(Type.WebSocket)).Url;

        using (var blocked = new ClientWebSocket())
        {
            blocked.Options.CollectHttpResponseDetails = true;
            blocked.Options.SetRequestHeader("Origin", "https://evil.example");
            await Assert.ThrowsExactlyAsync<WebSocketException>(() => blocked.ConnectAsync(url, CancellationToken.None).Timeout(TestTimeouts.Connect));
            await Assert.That(blocked.HttpStatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }

        using (var permitted = new ClientWebSocket())
        {
            permitted.Options.SetRequestHeader("Origin", allowed);
            await permitted.ConnectAsync(url, CancellationToken.None).Timeout(TestTimeouts.Connect);
            await Assert.That(permitted.State).IsEqualTo(WebSocketState.Open);
            permitted.Abort();
        }

        // The TCP endpoint is unaffected.
        var (tcpClient, _) = CreateClient(multi.CreateClientConfig(Type.Tcp));
        await tcpClient.ConnectAsync().Timeout(2);
    }

    [Test]
    [Arguments(Type.WebSocket, true)]
    [Arguments(Type.WebSocket, false)]
    [Arguments(Type.HttpSocket, true)]
    [Arguments(Type.HttpSocket, false)]
    public async Task AddedAspEndpoint_PromotesItsQueryStringToken(Type type, bool validToken)
    {
        var multi = CreateMultiEndpointServer(null,
            (t, config) =>
            {
                if (config is not AspServerConfig asp)
                    return;
                asp.AspEnableAuthentication = true;
                asp.AspAuthenticationScheme = "BearerToken";
                if (config is WebSocketServerConfig ws)
                    ws.AccessTokenQueryParameter = "access_token";
                else if (config is HttpSocketServerConfig hs)
                    hs.AccessTokenQueryParameter = "access_token";
            },
            AspCreateAuthServices,
            AspAppAuthConfigure,
            Type.Tcp, type);
        await multi.Server.StartAsync().Timeout(TestTimeouts.Connect);

        var clientConfig = (HttpClientConfig)multi.CreateClientConfig(type);
        clientConfig.Url = new Uri(clientConfig.Url + "?access_token=" + (validToken ? "Token123" : "Bad"));
        var (client, _) = CreateClient(clientConfig);

        if (validToken)
        {
            await client.ConnectAsync().Timeout(2);
            await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
        }
        else
        {
            var actual = await Assert.ThrowsExactlyAsync<TransportException>(() => client.ConnectAsync().Timeout(2));
            await Assert.That(actual!.Error).IsEqualTo(TransportError.AuthenticationError);
        }
    }

    private sealed class FakeTime : TimeProvider;

    private sealed class AsyncDisposableAction(Func<ValueTask> action) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => action();
    }
}