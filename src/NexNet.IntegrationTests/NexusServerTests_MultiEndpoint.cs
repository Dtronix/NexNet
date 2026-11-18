using System.Net;
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
using NUnit.Framework;

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
    [TearDown]
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
            Assert.That(client.State, Is.EqualTo(ConnectionState.Connected), $"{type} client did not connect.");
            clients[type] = (client, nexus);
        }

        return clients;
    }

    [Test]
    public async Task Endpoints_ListedInOrder()
    {
        var multi = CreateMultiEndpointServer(AllTransports);

        Assert.That(multi.Server.Endpoints.Select(e => e.Name),
            Is.EqualTo(new[] { "tcp", "uds", "websocket", "httpsocket" }));
        Assert.That(multi.Server.Endpoints[2],
            Is.EqualTo(new NexusEndpointInfo("websocket", "websocket", false, "/websocket-test")));

        await multi.Server.StartAsync().Timeout(1);
        await ConnectAll(multi);

        await WaitFor(() => multi.Server.GetSnapshot().Sessions.Count == AllTransports.Length).Timeout(2);
        var snapshot = multi.Server.GetSnapshot();
        Assert.That(snapshot.Endpoints.Select(e => e.Name), Is.EqualTo(new[] { "tcp", "uds", "websocket", "httpsocket" }));
        Assert.That(snapshot.Endpoints.All(e => e.State == NexusEndpointState.Running && e.Sessions == 1), Is.True);
        Assert.That(snapshot.Endpoints[0].IsListener, Is.True);
        Assert.That(snapshot.Endpoints[3].IsListener, Is.False);
        Assert.That(snapshot.Endpoints[3].Address, Is.EqualTo("/httpsocket-test"));
        Assert.That(snapshot.Sessions.Select(s => s.Endpoint), Is.EquivalentTo(new[] { "tcp", "uds", "websocket", "httpsocket" }));

        await multi.Server.StopAsync().Timeout(2);
        Assert.That(multi.Server.GetSnapshot().Endpoints.All(e => e.State == NexusEndpointState.Stopped), Is.True);
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
        await multi.Server.StartAsync().Timeout(1);
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
            Assert.That(await tcs.Task.Timeout(2), Is.EqualTo(42), $"{type} client missed the group broadcast.");

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
        Assert.That(all[Type.Uds].Task.IsCompleted, Is.False, "Others must not include the caller.");
    }

    [Test]
    public async Task Collections_SyncAcrossTransports()
    {
        var multi = CreateMultiEndpointServer(AllTransports);
        await multi.Server.StartAsync().Timeout(1);
        var clients = await ConnectAll(multi);

        foreach (var (client, _) in clients.Values)
            Assert.That(await client.Proxy.IntListBi.EnableAsync().Timeout(2), Is.True);

        // A client on one transport adds; the clients on the others see it.
        await clients[Type.WebSocket].Client.Proxy.IntListBi.AddAsync(7).Timeout(2);

        using (var context = multi.Server.ContextProvider.Rent())
            await context.Collections.IntListBi.AddAsync(8).Timeout(2);

        foreach (var (type, (client, _)) in clients)
        {
            await WaitFor(() => client.Proxy.IntListBi.Count == 2).Timeout(2);
            Assert.That(client.Proxy.IntListBi, Is.EqualTo(new[] { 7, 8 }), $"{type} list differs.");
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
        await multi.Server.StartAsync().Timeout(1);
        await ConnectAll(multi);
        await WaitFor(() => { lock (seenLock) return seen.Count == types.Length; }).Timeout(2);

        Assert.Multiple(() =>
        {
            Assert.That(seen["tls"].AtConnect, Is.EqualTo(new NexusEndpointInfo("tls", "tls", true, null)));
            Assert.That(seen["tcp"].AtConnect, Is.EqualTo(new NexusEndpointInfo("tcp", "tcp", false, null)));
            Assert.That(seen["uds"].AtConnect, Is.EqualTo(new NexusEndpointInfo("uds", "uds", false, null)));
            Assert.That(seen["websocket"].AtConnect,
                Is.EqualTo(new NexusEndpointInfo("websocket", "websocket", false, "/websocket-test")));
            Assert.That(seen["httpsocket"].AtConnect,
                Is.EqualTo(new NexusEndpointInfo("httpsocket", "httpsocket", false, "/httpsocket-test")));
            foreach (var (name, (atAuthenticate, atConnect)) in seen)
                Assert.That(atAuthenticate, Is.EqualTo(atConnect), $"{name}: Endpoint differs in OnAuthenticate.");
        });
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
        await multi.Server.StartAsync().Timeout(1);

        var clientConfig = (HttpClientConfig)multi.CreateClientConfig(Type.WebSocket);
        clientConfig.Url = new UriBuilder(clientConfig.Url) { Scheme = "wss" }.Uri;
        clientConfig.SslClientAuthenticationOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
        };
        var (client, _) = CreateClient(clientConfig);
        await client.ConnectAsync().Timeout(2);

        await WaitFor(() => endpoint != null).Timeout(2);
        Assert.That(endpoint, Is.EqualTo(new NexusEndpointInfo("websocket", "websocket", true, "/websocket-test")));
        Assert.That(multi.Server.Endpoints[1].IsSecure, Is.False, "The endpoint itself does not know the scheme.");
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
        await multi.Server.StartAsync().Timeout(1);
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
        await multi.Server.StartAsync().Timeout(1);

        var (tcpClient, _) = CreateClient(multi.CreateClientConfig(Type.Tcp));
        await tcpClient.ConnectAsync().Timeout(2);
        Assert.That(tcpClient.State, Is.EqualTo(ConnectionState.Connected));

        var (udsClient, _) = CreateClient(multi.CreateClientConfig(Type.Uds));
        var result = await udsClient.TryConnectAsync().Timeout(2);
        Assert.That(result.Success, Is.False, "The UDS endpoint requires authentication.");

        lock (authenticated)
            Assert.That(authenticated, Is.EqualTo(new[] { "uds" }));
    }

    [Test]
    public async Task PrimaryAuthenticate_RequiresEveryEndpointToAuthenticate()
    {
        var multi = CreateMultiEndpointServer(null,
            (type, config) => config.Authenticate = type != Type.Uds,
            Type.Tcp, Type.Uds);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => multi.Server.StartAsync());
        Assert.That(exception!.Message, Does.Contain("'uds'"));
        Assert.That(multi.Server.State, Is.EqualTo(NexusServerState.Stopped));

        multi.Configs[Type.Uds].Authenticate = true;
        await multi.Server.StartAsync().Timeout(1);
        Assert.That(multi.Server.State, Is.EqualTo(NexusServerState.Running));
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
        await multi.Server.StartAsync().Timeout(1);

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
        Assert.That(reason, Is.EqualTo(DisconnectReason.Timeout));
        Assert.That(tcpClient.State, Is.EqualTo(ConnectionState.Connected));
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
        await multi.Server.StartAsync().Timeout(1);
        await ConnectAll(multi);

        foreach (var (endpoint, transport) in new[] { ("tcp", "tcp"), ("uds", "uds"), ("websocket", "websocket") })
        {
            await WaitFor(() => recorder.ObserveSum("nexnet.session.active",
                ("nexnet.role", "server"), ("nexnet.endpoint", endpoint), ("nexnet.transport", transport)) == 1).Timeout(2);
        }

        Assert.That(recorder.ObserveSum("nexnet.session.active", ("nexnet.role", "server")), Is.EqualTo(3));
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
            var exception = await Assert.ThrowsAsync<TransportException>(() => multi.Server.StartAsync().Timeout(2));
            Assert.That(exception!.Message, Does.Contain("'tcp'"));
            Assert.That(multi.Server.State, Is.EqualTo(NexusServerState.Stopped));

            // The UDS endpoint, bound first, was closed again.
            var (udsClient, _) = CreateClient(multi.CreateClientConfig(Type.Uds));
            var result = await udsClient.TryConnectAsync().Timeout(2);
            Assert.That(result.Success, Is.False);
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
        await multi.Server.StartAsync().Timeout(1);

        var (tcpClient, _) = CreateClient(multi.CreateClientConfig(Type.Tcp));
        await tcpClient.ConnectAsync().Timeout(2);
        Assert.That(tcpClient.State, Is.EqualTo(ConnectionState.Connected));

        // The same address on another transport counts against the same limit.
        var (webSocketClient, _) = CreateClient(multi.CreateClientConfig(Type.WebSocket));
        var result = await webSocketClient.TryConnectAsync().Timeout(3);
        Assert.That(result.Success, Is.False);

        await tcpClient.DisconnectAsync().Timeout(2);
        await WaitFor(() => multi.Server.GetSnapshot().RateLimiter!.Value.CurrentConnections == 0).Timeout(2);

        var (webSocketClient2, _) = CreateClient(multi.CreateClientConfig(Type.WebSocket));
        await webSocketClient2.ConnectAsync().Timeout(2);
        Assert.That(webSocketClient2.State, Is.EqualTo(ConnectionState.Connected));
    }

    [Test]
    public async Task AddEndpoint_Validation()
    {
        var multi = CreateMultiEndpointServer(Type.Tcp, Type.Uds);
        var server = multi.Server;
        var tcp = (TcpServerConfig)multi.Configs[Type.Tcp];

        Assert.Multiple(() =>
        {
            // Duplicate name.
            Assert.Throws<InvalidOperationException>(() => server.AddListener(
                new TcpServerConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, FreeTcpPort()) }, "uds"));
            // Same port.
            Assert.Throws<InvalidOperationException>(() => server.AddListener(
                new TcpServerConfig { EndPoint = new IPEndPoint(IPAddress.Any, ((IPEndPoint)tcp.EndPoint).Port) }, "tcp2"));
            // Same UDS path.
            Assert.Throws<InvalidOperationException>(() => server.AddListener(
                new UdsServerConfig { EndPoint = ((UdsServerConfig)multi.Configs[Type.Uds]).EndPoint }, "uds2"));
            // Same configuration instance.
            Assert.Throws<InvalidOperationException>(() => server.AddListener(tcp, "tcp3"));
            // Not a listener configuration.
            Assert.Throws<InvalidOperationException>(() => server.AddListener(
                new NexNet.Asp.WebSocket.WebSocketServerConfig { Path = "/ws" }, "ws"));
            // Server-wide settings.
            Assert.Throws<InvalidOperationException>(() => server.AddListener(
                new TcpServerConfig
                {
                    EndPoint = new IPEndPoint(IPAddress.Loopback, FreeTcpPort()),
                    RateLimiting = new ConnectionRateLimitConfig { MaxConcurrentConnections = 1 }
                }, "tcp4"));
            Assert.Throws<InvalidOperationException>(() => server.AddListener(
                new TcpServerConfig
                {
                    EndPoint = new IPEndPoint(IPAddress.Loopback, FreeTcpPort()),
                    Time = new FakeTime()
                }, "tcp5"));
            // Same ASP.NET path.
            server.AddExternalEndpoint(new NexNet.Asp.WebSocket.WebSocketServerConfig { Path = "/ws" }, "ws");
            Assert.Throws<InvalidOperationException>(() => server.AddExternalEndpoint(
                new NexNet.Asp.HttpSocket.HttpSocketServerConfig { Path = "/WS" }, "hs"));
        });

        Assert.That(server.Endpoints.Select(e => e.Name), Is.EqualTo(new[] { "tcp", "uds", "ws" }));

        await server.StartAsync().Timeout(1);
        Assert.Throws<InvalidOperationException>(() => server.AddListener(
            new TcpServerConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, FreeTcpPort()) }, "late"));

        var unconfigured = new NexusServer<ServerNexus, ServerNexus.ClientProxy>();
        Assert.Throws<InvalidOperationException>(() => unconfigured.AddListener(
            new TcpServerConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, FreeTcpPort()) }, "tcp"));
    }

    [Test]
    public void AddEndpoint_QuicValidationAndAddress()
    {
        var multi = CreateMultiEndpointServer(Type.Tcp);
        var server = multi.Server;
        var udpPort = FreeUdpPort();
        var quic = new QuicServerConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, udpPort) };
        server.AddListener(quic, "quic");

        Assert.Multiple(() =>
        {
            // Same UDP port, on a wildcard address.
            var conflict = Assert.Throws<InvalidOperationException>(() => server.AddListener(
                new QuicServerConfig { EndPoint = new IPEndPoint(IPAddress.IPv6Any, udpPort) }, "quic2"));
            Assert.That(conflict!.Message, Does.Contain("'quic'").And.Contain("UDP"));
            // Same UDP port and address.
            Assert.Throws<InvalidOperationException>(() => server.AddListener(
                new QuicServerConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, udpPort) }, "quic3"));
        });

        // A TCP listener on the same port number is a different socket; another UDP port is free.
        server.AddListener(new TcpServerConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, udpPort) }, "tcp-same-number");
        var otherPort = FreeUdpPort();
        server.AddListener(new QuicServerConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, otherPort) }, "quic-other");
        // Port 0 binds an ephemeral port and never conflicts.
        server.AddListener(new QuicServerConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) }, "quic-any1");
        server.AddListener(new QuicServerConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) }, "quic-any2");

        var endpoints = server.GetSnapshot().Endpoints;
        Assert.That(endpoints.Select(e => e.Name),
            Is.EqualTo(new[] { "tcp", "quic", "tcp-same-number", "quic-other", "quic-any1", "quic-any2" }));
        Assert.That(endpoints[1].Address, Is.EqualTo($"127.0.0.1:{udpPort}"));
        Assert.That(endpoints[3].Address, Is.EqualTo($"127.0.0.1:{otherPort}"));
        Assert.That(server.Endpoints[1], Is.EqualTo(new NexusEndpointInfo("quic", "quic", true, null)));
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
        await multi.Server.StartAsync().Timeout(1);

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
        Assert.That(endpoint!.Name, Is.EqualTo("external"));
        Assert.That(endpoint.Transport, Is.EqualTo("tcp"));

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
        Assert.That(server.State, Is.EqualTo(NexusServerState.Stopped));
        Assert.That(server.Endpoints.Select(e => e.Name), Is.EqualTo(new[] { "tcp", "uds" }));

        await app.StartAsync().Timeout(5);
        await WaitFor(() => server.State == NexusServerState.Running).Timeout(2);

        var (tcpClient, _) = CreateClient(CreateClientConfig(Type.Tcp));
        var (udsClient, _) = CreateClient(CreateClientConfig(Type.Uds));
        await tcpClient.ConnectAsync().Timeout(2);
        await udsClient.ConnectAsync().Timeout(2);

        await app.StopAsync().Timeout(5);
        Assert.That(server.State, Is.EqualTo(NexusServerState.Stopped));
        Assert.That(await ((Task<DisconnectReason>)tcpClient.DisconnectedTask).Timeout(2), Is.EqualTo(DisconnectReason.ServerShutdown));
        Assert.That(await ((Task<DisconnectReason>)udsClient.DisconnectedTask).Timeout(2), Is.EqualTo(DisconnectReason.ServerShutdown));
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
        Assert.That(server.Endpoints.Select(e => e.Name), Is.EqualTo(new[] { "tcp", "websocket" }));

        await app.StartAsync().Timeout(5);
        await WaitFor(() => server.State == NexusServerState.Running).Timeout(2);

        var (tcpClient, _) = CreateClient(CreateClientConfig(Type.Tcp));
        await tcpClient.ConnectAsync().Timeout(2);

        CurrentTcpPort = aspPort;
        var (webSocketClient, _) = CreateClient(CreateClientConfig(Type.WebSocket));
        await webSocketClient.ConnectAsync().Timeout(2);
        Assert.That(webSocketClient.State, Is.EqualTo(ConnectionState.Connected));

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
        Assert.That(multi.Server.Endpoints[1].Name, Is.EqualTo("websocket"), "The WebSocket is an added endpoint.");
        await multi.Server.StartAsync().Timeout(1);
        var url = ((HttpClientConfig)multi.CreateClientConfig(Type.WebSocket)).Url;

        using (var blocked = new ClientWebSocket())
        {
            blocked.Options.CollectHttpResponseDetails = true;
            blocked.Options.SetRequestHeader("Origin", "https://evil.example");
            await Assert.ThrowsAsync<WebSocketException>(() => blocked.ConnectAsync(url, CancellationToken.None).Timeout(1));
            Assert.That(blocked.HttpStatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        }

        using (var permitted = new ClientWebSocket())
        {
            permitted.Options.SetRequestHeader("Origin", allowed);
            await permitted.ConnectAsync(url, CancellationToken.None).Timeout(1);
            Assert.That(permitted.State, Is.EqualTo(WebSocketState.Open));
            permitted.Abort();
        }

        // The TCP endpoint is unaffected.
        var (tcpClient, _) = CreateClient(multi.CreateClientConfig(Type.Tcp));
        await tcpClient.ConnectAsync().Timeout(2);
    }

    [TestCase(Type.WebSocket, true)]
    [TestCase(Type.WebSocket, false)]
    [TestCase(Type.HttpSocket, true)]
    [TestCase(Type.HttpSocket, false)]
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
        await multi.Server.StartAsync().Timeout(1);

        var clientConfig = (HttpClientConfig)multi.CreateClientConfig(type);
        clientConfig.Url = new Uri(clientConfig.Url + "?access_token=" + (validToken ? "Token123" : "Bad"));
        var (client, _) = CreateClient(clientConfig);

        if (validToken)
        {
            await client.ConnectAsync().Timeout(2);
            Assert.That(client.State, Is.EqualTo(ConnectionState.Connected));
        }
        else
        {
            var actual = await Assert.ThrowsAsync<TransportException>(() => client.ConnectAsync().Timeout(2));
            Assert.That(actual!.Error, Is.EqualTo(TransportError.AuthenticationError));
        }
    }

    private sealed class FakeTime : TimeProvider;

    private sealed class AsyncDisposableAction(Func<ValueTask> action) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => action();
    }
}
