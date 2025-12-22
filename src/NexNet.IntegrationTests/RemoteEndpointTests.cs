using System.Net;
using NexNet.Asp.HttpSocket;
using NexNet.Asp.WebSocket;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Transports;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Tests for RemoteAddress and RemotePort properties on sessions and transports.
/// </summary>
internal class RemoteEndpointTests : BaseTests
{
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task Server_SeesClientRemoteAddress(Type type)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverConfig = CreateServerConfig(type);
        var (server, client, _) = CreateServerClient(serverConfig, CreateClientConfig(type));

        server.OnNexusCreated = nexus => nexus.OnConnectedEvent = n =>
        {
            tcs.SetResult(n.Context.RemoteAddress);
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var remoteAddress = await tcs.Task.Timeout(1);

        Assert.That(remoteAddress, Is.Not.Null);
        Assert.That(remoteAddress, Is.Not.Empty);
        // For loopback connections, should be 127.0.0.1 or ::1
        Assert.That(remoteAddress, Does.Match(@"^(127\.0\.0\.1|::1|localhost)$"));
    }

    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task Server_SeesClientRemotePort(Type type)
    {
        var tcs = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverConfig = CreateServerConfig(type);
        var (server, client, _) = CreateServerClient(serverConfig, CreateClientConfig(type));

        server.OnNexusCreated = nexus => nexus.OnConnectedEvent = n =>
        {
            tcs.SetResult(n.Context.RemotePort);
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var remotePort = await tcs.Task.Timeout(1);

        Assert.That(remotePort, Is.Not.Null);
        Assert.That(remotePort, Is.GreaterThan(0));
        Assert.That(remotePort, Is.LessThanOrEqualTo(65535));
    }

    [TestCase(Type.Uds)]
    public async Task Server_SeesUdsClientRemoteAddress_AsSocketPath(Type type)
    {
        var tcs = new TaskCompletionSource<(string?, int?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverConfig = CreateServerConfig(type);
        var (server, client, _) = CreateServerClient(serverConfig, CreateClientConfig(type));

        server.OnNexusCreated = nexus => nexus.OnConnectedEvent = n =>
        {
            tcs.SetResult((n.Context.RemoteAddress, n.Context.RemotePort));
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var (remoteAddress, remotePort) = await tcs.Task.Timeout(1);

        // UDS connections don't have traditional IP addresses or ports
        // RemoteAddress might be empty string or null, RemotePort should be null
        Assert.That(remotePort, Is.Null);
    }

    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task Server_MultipleClients_HaveDifferentRemotePorts(Type type)
    {
        var ports = new List<int?>();
        var connectCount = 0;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverConfig = CreateServerConfig(type);
        var (server, client1, _) = CreateServerClient(serverConfig, CreateClientConfig(type));
        var (client2, _) = CreateClient(CreateClientConfig(type));

        server.OnNexusCreated = nexus => nexus.OnConnectedEvent = n =>
        {
            lock (ports)
            {
                ports.Add(n.Context.RemotePort);
                connectCount++;
                if (connectCount >= 2)
                    tcs.TrySetResult();
            }
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client1.ConnectAsync().Timeout(1);
        await client2.ConnectAsync().Timeout(1);

        await tcs.Task.Timeout(2);

        Assert.That(ports, Has.Count.EqualTo(2));
        Assert.That(ports[0], Is.Not.Null);
        Assert.That(ports[1], Is.Not.Null);
        Assert.That(ports[0], Is.Not.EqualTo(ports[1]), "Each client should have a different ephemeral port");
    }

    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Client_SeesServerRemoteAddress(Type type)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverConfig = CreateServerConfig(type);
        var (server, client, clientNexus) = CreateServerClient(serverConfig, CreateClientConfig(type));

        clientNexus.OnConnectedEvent = (nexus, _) =>
        {
            tcs.SetResult(nexus.Context.RemoteAddress);
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var remoteAddress = await tcs.Task.Timeout(1);

        Assert.That(remoteAddress, Is.Not.Null);
        Assert.That(remoteAddress, Is.Not.Empty);
        // Client connects to loopback, so should see 127.0.0.1 or ::1
        Assert.That(remoteAddress, Does.Match(@"^(127\.0\.0\.1|::1|localhost)$"));
    }

    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task Client_SeesServerRemotePort(Type type)
    {
        var tcs = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverConfig = CreateServerConfig(type);
        var clientConfig = CreateClientConfig(type);
        var (server, client, clientNexus) = CreateServerClient(serverConfig, clientConfig);

        // Get the expected server port from config
        int? expectedPort = null;
        if (serverConfig is TcpServerConfig tcpConfig)
            expectedPort = ((IPEndPoint)tcpConfig.EndPoint).Port;
        else if (serverConfig is TcpTlsServerConfig tlsConfig)
            expectedPort = ((IPEndPoint)tlsConfig.EndPoint).Port;
        else if (serverConfig is Quic.QuicServerConfig quicConfig)
            expectedPort = ((IPEndPoint)quicConfig.EndPoint).Port;

        clientNexus.OnConnectedEvent = (nexus, _) =>
        {
            tcs.SetResult(nexus.Context.RemotePort);
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var remotePort = await tcs.Task.Timeout(1);

        Assert.That(remotePort, Is.Not.Null);
        Assert.That(remotePort, Is.EqualTo(expectedPort), "Client should see the server's listening port");
    }

    // WebSocket and HttpSocket client-side tests
    // Note: WebSocket/HttpSocket clients may not have RemoteAddress/RemotePort set
    // because the underlying transport doesn't expose this easily
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task Client_WebSocketHttpSocket_RemoteEndpointMayBeNull(Type type)
    {
        var tcs = new TaskCompletionSource<(string?, int?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverConfig = CreateServerConfig(type);
        var (server, client, clientNexus) = CreateServerClient(serverConfig, CreateClientConfig(type));

        clientNexus.OnConnectedEvent = (nexus, _) =>
        {
            tcs.SetResult((nexus.Context.RemoteAddress, nexus.Context.RemotePort));
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var (remoteAddress, remotePort) = await tcs.Task.Timeout(1);

        // WebSocket/HttpSocket client transports don't provide remote endpoint
        Assert.That(remoteAddress, Is.Null);
        Assert.That(remotePort, Is.Null);
    }

    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task Server_WithTrustProxyHeaders_SeesXForwardedForAddress(Type type)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverConfig = CreateServerConfig(type);

        // Enable proxy header trust
        if (serverConfig is AspServerConfig aspConfig)
            aspConfig.TrustProxyHeaders = true;

        const string expectedClientIp = "203.0.113.195";

        var server = CreateServer(
            serverConfig,
            nexus => nexus.OnConnectedEvent = n =>
            {
                tcs.SetResult(n.Context.RemoteAddress);
                return ValueTask.CompletedTask;
            },
            onAspAppConfigure: app =>
            {
                // Add middleware to inject X-Forwarded-For header (simulating a proxy)
                app.Use(async (context, next) =>
                {
                    context.Request.Headers["X-Forwarded-For"] = expectedClientIp;
                    await next();
                });
            });

        var (client, _) = CreateClient(CreateClientConfig(type));

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var remoteAddress = await tcs.Task.Timeout(1);

        Assert.That(remoteAddress, Is.EqualTo(expectedClientIp));
    }

    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task Server_WithTrustProxyHeaders_SeesXRealIPAddress(Type type)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverConfig = CreateServerConfig(type);

        if (serverConfig is AspServerConfig aspConfig)
            aspConfig.TrustProxyHeaders = true;

        const string expectedClientIp = "198.51.100.178";

        var server = CreateServer(
            serverConfig,
            nexus => nexus.OnConnectedEvent = n =>
            {
                tcs.SetResult(n.Context.RemoteAddress);
                return ValueTask.CompletedTask;
            },
            onAspAppConfigure: app =>
            {
                // Add middleware to inject X-Real-IP header
                app.Use(async (context, next) =>
                {
                    context.Request.Headers["X-Real-IP"] = expectedClientIp;
                    await next();
                });
            });

        var (client, _) = CreateClient(CreateClientConfig(type));

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var remoteAddress = await tcs.Task.Timeout(1);

        Assert.That(remoteAddress, Is.EqualTo(expectedClientIp));
    }

    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task Server_WithTrustProxyHeaders_XForwardedForTakesPriorityOverXRealIP(Type type)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverConfig = CreateServerConfig(type);

        if (serverConfig is AspServerConfig aspConfig)
            aspConfig.TrustProxyHeaders = true;

        const string xForwardedForIp = "203.0.113.195";
        const string xRealIp = "198.51.100.178";

        var server = CreateServer(
            serverConfig,
            nexus => nexus.OnConnectedEvent = n =>
            {
                tcs.SetResult(n.Context.RemoteAddress);
                return ValueTask.CompletedTask;
            },
            onAspAppConfigure: app =>
            {
                app.Use(async (context, next) =>
                {
                    // Both headers present - X-Forwarded-For should take priority
                    context.Request.Headers["X-Forwarded-For"] = xForwardedForIp;
                    context.Request.Headers["X-Real-IP"] = xRealIp;
                    await next();
                });
            });

        var (client, _) = CreateClient(CreateClientConfig(type));

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var remoteAddress = await tcs.Task.Timeout(1);

        Assert.That(remoteAddress, Is.EqualTo(xForwardedForIp), "X-Forwarded-For should take priority over X-Real-IP");
    }

    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task Server_WithTrustProxyHeaders_ParsesFirstIpFromXForwardedForChain(Type type)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverConfig = CreateServerConfig(type);

        if (serverConfig is AspServerConfig aspConfig)
            aspConfig.TrustProxyHeaders = true;

        // X-Forwarded-For can contain multiple IPs: "client, proxy1, proxy2"
        const string originalClientIp = "203.0.113.195";
        const string xForwardedForChain = "203.0.113.195, 70.41.3.18, 150.172.238.178";

        var server = CreateServer(
            serverConfig,
            nexus => nexus.OnConnectedEvent = n =>
            {
                tcs.SetResult(n.Context.RemoteAddress);
                return ValueTask.CompletedTask;
            },
            onAspAppConfigure: app =>
            {
                app.Use(async (context, next) =>
                {
                    context.Request.Headers["X-Forwarded-For"] = xForwardedForChain;
                    await next();
                });
            });

        var (client, _) = CreateClient(CreateClientConfig(type));

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var remoteAddress = await tcs.Task.Timeout(1);

        Assert.That(remoteAddress, Is.EqualTo(originalClientIp), "Should extract first IP from X-Forwarded-For chain");
    }

    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task Server_WithTrustProxyHeaders_ParsesXForwardedPort(Type type)
    {
        var tcs = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverConfig = CreateServerConfig(type);

        if (serverConfig is AspServerConfig aspConfig)
            aspConfig.TrustProxyHeaders = true;

        const int expectedPort = 54321;

        var server = CreateServer(
            serverConfig,
            nexus => nexus.OnConnectedEvent = n =>
            {
                tcs.SetResult(n.Context.RemotePort);
                return ValueTask.CompletedTask;
            },
            onAspAppConfigure: app =>
            {
                app.Use(async (context, next) =>
                {
                    context.Request.Headers["X-Forwarded-For"] = "203.0.113.195";
                    context.Request.Headers["X-Forwarded-Port"] = expectedPort.ToString();
                    await next();
                });
            });

        var (client, _) = CreateClient(CreateClientConfig(type));

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var remotePort = await tcs.Task.Timeout(1);

        Assert.That(remotePort, Is.EqualTo(expectedPort));
    }

    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task Server_WithTrustProxyHeadersFalse_IgnoresProxyHeaders(Type type)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverConfig = CreateServerConfig(type);

        // Explicitly disable proxy header trust (default)
        if (serverConfig is AspServerConfig aspConfig)
            aspConfig.TrustProxyHeaders = false;

        const string fakeClientIp = "203.0.113.195";

        var server = CreateServer(
            serverConfig,
            nexus => nexus.OnConnectedEvent = n =>
            {
                tcs.SetResult(n.Context.RemoteAddress);
                return ValueTask.CompletedTask;
            },
            onAspAppConfigure: app =>
            {
                app.Use(async (context, next) =>
                {
                    // Inject fake X-Forwarded-For header
                    context.Request.Headers["X-Forwarded-For"] = fakeClientIp;
                    await next();
                });
            });

        var (client, _) = CreateClient(CreateClientConfig(type));

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var remoteAddress = await tcs.Task.Timeout(1);

        // Should NOT be the fake IP since TrustProxyHeaders is false
        Assert.That(remoteAddress, Is.Not.EqualTo(fakeClientIp), "Should ignore X-Forwarded-For when TrustProxyHeaders is false");
        // Should be the actual connection IP (loopback)
        Assert.That(remoteAddress, Does.Match(@"^(127\.0\.0\.1|::1|localhost)$"));
    }

    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task Server_WithTrustProxyHeaders_ParsesIPv6Address(Type type)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverConfig = CreateServerConfig(type);

        if (serverConfig is AspServerConfig aspConfig)
            aspConfig.TrustProxyHeaders = true;

        const string expectedClientIp = "2001:db8:85a3::8a2e:370:7334";

        var server = CreateServer(
            serverConfig,
            nexus => nexus.OnConnectedEvent = n =>
            {
                tcs.SetResult(n.Context.RemoteAddress);
                return ValueTask.CompletedTask;
            },
            onAspAppConfigure: app =>
            {
                app.Use(async (context, next) =>
                {
                    context.Request.Headers["X-Forwarded-For"] = expectedClientIp;
                    await next();
                });
            });

        var (client, _) = CreateClient(CreateClientConfig(type));

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var remoteAddress = await tcs.Task.Timeout(1);

        Assert.That(remoteAddress, Is.EqualTo(expectedClientIp));
    }

    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task Server_WithTrustProxyHeaders_ParsesCFConnectingIP(Type type)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverConfig = CreateServerConfig(type);

        if (serverConfig is AspServerConfig aspConfig)
            aspConfig.TrustProxyHeaders = true;

        // Cloudflare uses CF-Connecting-IP header
        const string expectedClientIp = "192.0.2.1";

        var server = CreateServer(
            serverConfig,
            nexus => nexus.OnConnectedEvent = n =>
            {
                tcs.SetResult(n.Context.RemoteAddress);
                return ValueTask.CompletedTask;
            },
            onAspAppConfigure: app =>
            {
                app.Use(async (context, next) =>
                {
                    context.Request.Headers["CF-Connecting-IP"] = expectedClientIp;
                    await next();
                });
            });

        var (client, _) = CreateClient(CreateClientConfig(type));

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var remoteAddress = await tcs.Task.Timeout(1);

        Assert.That(remoteAddress, Is.EqualTo(expectedClientIp));
    }

    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task Server_RemoteEndpointRemainsConsistentDuringSession(Type type)
    {
        var tcs = new TaskCompletionSource<(string?, int?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? initialAddress = null;
        int? initialPort = null;
        var serverConfig = CreateServerConfig(type);
        var (server, client, _) = CreateServerClient(serverConfig, CreateClientConfig(type));

        server.OnNexusCreated = nexus =>
        {
            nexus.OnConnectedEvent = n =>
            {
                initialAddress = n.Context.RemoteAddress;
                initialPort = n.Context.RemotePort;
                return ValueTask.CompletedTask;
            };

            // Check again after some operations
            nexus.ServerVoidWithParamEvent = (n, val) =>
            {
                var currentAddress = n.Context.RemoteAddress;
                var currentPort = n.Context.RemotePort;
                tcs.SetResult((currentAddress, currentPort));
            };
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        await Task.Delay(100); // Let connection stabilize

        // Trigger an invocation (void method, so no await needed on the call itself)
        client.Proxy.ServerVoidWithParam(42);

        var (laterAddress, laterPort) = await tcs.Task.Timeout(1);

        Assert.That(laterAddress, Is.EqualTo(initialAddress), "RemoteAddress should remain consistent during session");
        Assert.That(laterPort, Is.EqualTo(initialPort), "RemotePort should remain consistent during session");
    }
}
