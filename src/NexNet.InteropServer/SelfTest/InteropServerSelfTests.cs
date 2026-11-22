using System.Net.WebSockets;
using System.Text;
using NexNet.InteropServer.Probe;
using NexNet.Messages;
using NexNet.Transports;
using NexNet.Transports.WebSocket;

namespace NexNet.InteropServer.SelfTest;

public class InteropServerSelfTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // The endpoints a server of this process has: QUIC only where msquic is available.
    private static readonly InteropEndpointKind[] Available =
        InteropEndpointKinds.All.Where(k => k.GetUnsupportedReason() == null).ToArray();

    private static Task<InteropServer<InteropProbeServerNexus, InteropProbeServerNexus.ClientProxy>> StartDedicated(
        InteropServerOptions? options = null)
        => InteropServerHost.StartAsync<InteropProbeServerNexus, InteropProbeServerNexus.ClientProxy>(
            () => new InteropProbeServerNexus(), options);

    private static async Task<NexusClient<InteropProbeClientNexus, InteropProbeClientNexus.ServerProxy>> Connect(
        ClientConfig config)
    {
        var client = InteropProbeClientNexus.CreateClient(config, new InteropProbeClientNexus());
        await client.ConnectAsync().WaitAsync(Wait);
        return client;
    }

    private static string Expected(InteropEndpointKind kind, string pathPrefix)
    {
        var path = kind switch
        {
            InteropEndpointKind.HttpSocket => "/hs",
            InteropEndpointKind.HttpSocketTls => "/hss",
            InteropEndpointKind.WebSocket => "/ws",
            InteropEndpointKind.WebSocketTls => "/wss",
            _ => null,
        };
        return InteropProbeServerNexus.Describe(new NexusEndpointInfo(
            kind.GetEndpointName(), kind.GetTransportName(), kind.IsSecure(), path == null ? null : pathPrefix + path));
    }

    [Test]
    public async Task EveryEndpoint_ConnectsAndReportsContextEndpoint()
    {
        await using var server = await StartDedicated();

        await Assert.That(server.Server.Endpoints.Select(e => e.Name).Order(StringComparer.Ordinal))
            .IsEquivalentTo(Available.Select(k => k.GetEndpointName()).Order(StringComparer.Ordinal),
                TUnit.Assertions.Enums.CollectionOrdering.Matching);

        foreach (var kind in Available)
        {
            var client = await Connect(server.CreateClientConfig(kind));
            try
            {
                await Assert.That(await client.Proxy.GetEndpoint().AsTask().WaitAsync(Wait))
                    .IsEqualTo(Expected(kind, "")).Because(kind.ToString());
                await Assert.That(await client.Proxy.Echo("hi " + kind).AsTask().WaitAsync(Wait))
                    .IsEqualTo("hi " + kind);
            }
            finally
            {
                await client.DisposeAsync();
            }
        }
    }

    [Test]
    public async Task Endpoints_ReportAddresses()
    {
        await using var server = await StartDedicated();
        var endpoints = server.Endpoints;

        await Assert.That(endpoints.Tcp.Port).IsNotEqualTo(0);
        await Assert.That(endpoints.TcpTls.Port).IsNotEqualTo(endpoints.Tcp.Port);
        await Assert.That(endpoints.WebSocket.ToString()).IsEqualTo($"ws://127.0.0.1:{server.Host.HttpBaseUri.Port}/ws");
        await Assert.That(endpoints.WebSocketTls.ToString()).IsEqualTo($"wss://127.0.0.1:{server.Host.HttpsBaseUri.Port}/wss");
        await Assert.That(endpoints.HttpSocket.ToString()).IsEqualTo($"http://127.0.0.1:{server.Host.HttpBaseUri.Port}/hs");
        await Assert.That(endpoints.HttpSocketTls.ToString()).IsEqualTo($"https://127.0.0.1:{server.Host.HttpsBaseUri.Port}/hss");
        await Assert.That(endpoints.GetAddress(InteropEndpointKind.Uds)).IsEqualTo("uds:" + endpoints.UdsPath);
        await Assert.That(endpoints.ToDictionary().Count).IsEqualTo(Available.Length);
        if (InteropEndpointKinds.IsQuicSupported)
        {
            await Assert.That(endpoints.Quic).IsNotNull();
            await Assert.That(endpoints.Quic!.Port).IsNotEqualTo(0);
            await Assert.That(endpoints.GetAddress(InteropEndpointKind.Quic)).IsEqualTo($"quic://127.0.0.1:{endpoints.Quic.Port}");
        }
        else
        {
            await Assert.That(endpoints.Quic).IsNull();
        }

        await Assert.That(File.Exists(InteropCertificates.TestCaPemPath)).IsTrue();
    }

    [Test]
    public async Task Endpoints_WithoutQuic_LeaveItOut()
    {
        var any = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1);
        var uri = new Uri("ws://127.0.0.1:1/ws");
        var endpoints = new InteropEndpoints(any, any, "/tmp/s", uri, uri, uri, uri);

        await Assert.That(endpoints.Has(InteropEndpointKind.Quic)).IsFalse();
        await Assert.That(endpoints.Has(InteropEndpointKind.Tcp)).IsTrue();
        await Assert.That(endpoints.ToDictionary().ContainsKey(InteropEndpointKind.Quic)).IsFalse();
        await Assert.That(endpoints.ToDictionary().Count).IsEqualTo(InteropEndpointKinds.All.Count - 1);
        await Assert.That(() => endpoints.GetAddress(InteropEndpointKind.Quic)).ThrowsExactly<NotSupportedException>();
    }

    [Test]
    public async Task SharedHost_RoutesByTestId()
    {
        await using var host = await InteropServerHost.StartSharedAsync();
        var a = await host.AddServerAsync<InteropProbeServerNexus, InteropProbeServerNexus.ClientProxy>(
            () => new InteropProbeServerNexus(), new InteropServerOptions { TestId = "test-a" });
        var b = await host.AddServerAsync<InteropProbeServerNexus, InteropProbeServerNexus.ClientProxy>(
            () => new InteropProbeServerNexus(), new InteropServerOptions { TestId = "test-b" });

        await Assert.That(a.Endpoints.WebSocket.AbsolutePath).IsEqualTo("/t/test-a/ws");
        await Assert.That(a.Endpoints.WebSocket.Port).IsEqualTo(b.Endpoints.WebSocket.Port);

        foreach (var (server, prefix) in new[] { (a, "/t/test-a"), (b, "/t/test-b") })
        {
            foreach (var kind in Available)
            {
                var client = await Connect(server.CreateClientConfig(kind));
                await Assert.That(await client.Proxy.GetEndpoint().AsTask().WaitAsync(Wait))
                    .IsEqualTo(Expected(kind, prefix)).Because($"{prefix} {kind}");
                await client.DisposeAsync();
            }
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.AddServerAsync<InteropProbeServerNexus, InteropProbeServerNexus.ClientProxy>(
                () => new InteropProbeServerNexus(), new InteropServerOptions { TestId = "test-a" }));

        // A removed server's routes are gone; the other server still answers.
        await a.DisposeAsync();
        var gone = InteropProbeClientNexus.CreateClient(
            new WebSocketClientConfig { Url = a.Endpoints.WebSocket }, new InteropProbeClientNexus());
        var result = await gone.TryConnectAsync().WaitAsync(Wait);
        await Assert.That(result.Success).IsFalse();

        var stillThere = await Connect(b.CreateClientConfig(InteropEndpointKind.WebSocket));
        await Assert.That(await stillThere.Proxy.Echo("b").AsTask().WaitAsync(Wait)).IsEqualTo("b");
        await stillThere.DisposeAsync();
    }

    [Test]
    public async Task TokenValidator_AppliesOnEveryEndpoint()
    {
        var seen = new List<string>();
        await using var server = await StartDedicated(new InteropServerOptions
        {
            TokenValidator = (token, endpoint) =>
            {
                lock (seen)
                    seen.Add(endpoint?.Name ?? "?");
                var ok = token is { } t && Encoding.UTF8.GetString(t.Span) == "good";
                return ValueTask.FromResult<IIdentity?>(ok ? new DefaultIdentity { DisplayName = "user" } : null);
            },
        });

        foreach (var kind in Available)
        {
            var good = server.CreateClientConfig(kind);
            good.Authenticate = () => Encoding.UTF8.GetBytes("good");
            var client = await Connect(good);
            await Assert.That(await client.Proxy.GetIdentity().AsTask().WaitAsync(Wait)).IsEqualTo("user");
            await client.DisposeAsync();

            var bad = server.CreateClientConfig(kind);
            bad.Authenticate = () => Encoding.UTF8.GetBytes("bad");
            var refused = InteropProbeClientNexus.CreateClient(bad, new InteropProbeClientNexus());
            var result = await refused.TryConnectAsync().WaitAsync(Wait);
            await Assert.That(result.Success).IsFalse().Because(kind.ToString());
            await refused.DisposeAsync();
        }

        string[] neverAuthenticated;
        lock (seen)
            neverAuthenticated = Available.Select(k => k.GetEndpointName()).Where(n => !seen.Contains(n)).ToArray();
        await Assert.That(neverAuthenticated).IsEmpty();
    }

    [Test]
    public async Task HoldAuthentication_HoldsTheChosenAuthenticationUntilReleased()
    {
        var options = new InteropServerOptions
        {
            TokenValidator = (_, _) => ValueTask.FromResult<IIdentity?>(new DefaultIdentity { DisplayName = "user" }),
        };
        var hold = options.HoldAuthentication(2);
        await using var server = await StartDedicated(options);
        try
        {
            // Authentication 1 is not held.
            var first = await Connect(server.CreateClientConfig(InteropEndpointKind.Tcp));
            await first.DisposeAsync();
            await Assert.That(hold.Held.IsCompleted).IsFalse();

            // Authentication 2 waits for the release; the validator set before the hold still decides it.
            var second = InteropProbeClientNexus.CreateClient(server.CreateClientConfig(InteropEndpointKind.Tcp), new InteropProbeClientNexus());
            var connect = second.ConnectAsync();
            await hold.Held.WaitAsync(Wait);
            await Task.Delay(100);
            await Assert.That(connect.IsCompleted).IsFalse().Because("The server holds the handshake.");

            hold.Release();
            await connect.WaitAsync(Wait);
            await Assert.That(await second.Proxy.GetIdentity().AsTask().WaitAsync(Wait)).IsEqualTo("user");
            await second.DisposeAsync();
            await Assert.That(hold.Count).IsEqualTo(2);
        }
        finally
        {
            hold.Release();
        }
    }

    [Test]
    public async Task PerEndpointTimeout_Honored()
    {
        var options = new InteropServerOptions();
        options.EndpointTimeouts[InteropEndpointKind.Uds] = 300;
        await using var server = await StartDedicated(options);

        var tcpConfig = server.CreateClientConfig(InteropEndpointKind.Tcp);
        var udsConfig = server.CreateClientConfig(InteropEndpointKind.Uds);
        tcpConfig.PingInterval = 5_000;
        udsConfig.PingInterval = 5_000;
        var tcp = await Connect(tcpConfig);
        var uds = await Connect(udsConfig);

        var reason = await ((Task<DisconnectReason>)uds.DisconnectedTask).WaitAsync(Wait);
        await Assert.That(reason).IsEqualTo(DisconnectReason.Timeout);
        await Assert.That(tcp.State).IsEqualTo(ConnectionState.Connected);
        await tcp.DisposeAsync();
        await uds.DisposeAsync();
    }

    [Test]
    public async Task AllowedOrigins_RejectsOtherOrigins()
    {
        var options = new InteropServerOptions();
        options.AllowedOrigins.Add("https://allowed.example");
        await using var server = await StartDedicated(options);

        async Task<bool> Opens(string origin)
        {
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Origin", origin);
            try
            {
                await socket.ConnectAsync(server.Endpoints.WebSocket, CancellationToken.None).WaitAsync(Wait);
                return true;
            }
            catch (WebSocketException)
            {
                return false;
            }
        }

        await Assert.That(await Opens("https://allowed.example")).IsTrue();
        await Assert.That(await Opens("https://evil.example")).IsFalse();
        await Assert.That(server.RequestOrigins).IsEquivalentTo(new[] { "https://allowed.example", "https://evil.example" });
    }

    [Test]
    public async Task RequestOrigins_RecordsOnlyRequestsWithAnOrigin()
    {
        await using var server = await StartDedicated();

        using (var plain = new ClientWebSocket())
            await plain.ConnectAsync(server.Endpoints.WebSocket, CancellationToken.None).WaitAsync(Wait);
        await Assert.That(server.RequestOrigins).IsEmpty();

        using (var page = new ClientWebSocket())
        {
            page.Options.SetRequestHeader("Origin", "http://127.0.0.1:1234");
            await page.ConnectAsync(server.Endpoints.WebSocket, CancellationToken.None).WaitAsync(Wait);
        }

        await Assert.That(server.RequestOrigins).IsEquivalentTo(new[] { "http://127.0.0.1:1234" });
    }

    [Test]
    public async Task DedicatedServer_RestartsOnSameEndpoints()
    {
        await using var server = await StartDedicated();
        var endpoints = server.Endpoints;

        await server.StopAsync();
        var refused = InteropProbeClientNexus.CreateClient(
            server.CreateClientConfig(InteropEndpointKind.Tcp), new InteropProbeClientNexus());
        await Assert.That((await refused.TryConnectAsync().WaitAsync(Wait)).Success).IsFalse();
        await refused.DisposeAsync();

        await server.StartAsync();
        await Assert.That(server.Endpoints).IsEqualTo(endpoints);
        foreach (var kind in Available)
        {
            var client = await Connect(server.CreateClientConfig(kind));
            await Assert.That(await client.Proxy.Echo("again").AsTask().WaitAsync(Wait)).IsEqualTo("again");
            await client.DisposeAsync();
        }
    }
}
