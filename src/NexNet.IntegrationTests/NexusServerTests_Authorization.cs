using System.Net;
using NexNet.Asp;
using NexNet.Asp.HttpSocket;
using NexNet.Asp.WebSocket;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Invocation;
using NexNet.Transports;
using NexNet.Transports.HttpSocket;
using NexNet.Transports.WebSocket;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal class NexusServerTests_Authorization : BaseTests
{
    private readonly List<WebApplication> _aspApps = new();
    private readonly List<INexusServerFactory> _authServerFactories = new();
    private readonly List<INexusClient> _authClients = new();

    public override void TearDown()
    {
        foreach (var client in _authClients)
        {
            if (client.State == ConnectionState.Connected)
                _ = client.DisconnectAsync();
        }
        _authClients.Clear();

        foreach (var sf in _authServerFactories)
        {
            if (sf.ServerBase.State == NexusServerState.Running)
                _ = sf.ServerBase.StopAsync();
        }
        _authServerFactories.Clear();

        foreach (var app in _aspApps)
        {
            try { app.Lifetime.StopApplication(); } catch { }
        }
        _aspApps.Clear();

        base.TearDown();
    }

    private (NexusServerFactory<AuthServerNexus, AuthServerNexus.ClientProxy> server,
        NexusClient<AuthClientNexus, AuthClientNexus.ServerProxy> client,
        AuthClientNexus clientNexus)
        CreateAuthServerClient(Type type)
    {
        var sConfig = CreateServerConfig(type);
        var cConfig = CreateClientConfig(type);
        var serverFactory = new NexusServerFactory<AuthServerNexus, AuthServerNexus.ClientProxy>(sConfig);
        var clientNexus = new AuthClientNexus();
        var client = AuthClientNexus.CreateClient(cConfig, clientNexus);
        _authServerFactories.Add(serverFactory);
        _authClients.Add(client);

        if (sConfig is WebSocketServerConfig or HttpSocketServerConfig)
        {
            CurrentTcpPort ??= FreeTcpPort();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel((_, serverOptions) =>
                serverOptions.Listen(IPAddress.Loopback, CurrentTcpPort.Value));
            builder.Services.AddNexusServer<AuthServerNexus, AuthServerNexus.ClientProxy>();
            var app = builder.Build();

            if (sConfig is WebSocketServerConfig wsConfig)
            {
                app.UseWebSockets();
                app.MapWebSocketNexus(wsConfig, serverFactory.Server);
            }
            else if (sConfig is HttpSocketServerConfig hsConfig)
            {
                app.UseHttpSockets();
                app.MapHttpSocketNexus(hsConfig, serverFactory.Server);
            }

            _ = app.RunAsync();
            _aspApps.Add(app);
        }

        return (serverFactory, client, clientNexus);
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task AuthorizedMethod_Allowed_InvokesMethod(Type type)
    {
        var methodInvoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, _) = CreateAuthServerClient(type);

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            nexus.ProtectedMethodHandler = (_, _) =>
            {
                methodInvoked.SetResult();
                return ValueTask.CompletedTask;
            };
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await client.Proxy.ProtectedMethod("test").Timeout(1);
        await methodInvoked.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task AuthorizedMethod_Unauthorized_ThrowsOnClient(Type type)
    {
        var (server, client, _) = CreateAuthServerClient(type);

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Unauthorized);
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        Assert.ThrowsAsync<ProxyUnauthorizedException>(async () =>
            await client.Proxy.ProtectedMethod("test").Timeout(1));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task AuthorizedMethod_Disconnect_DisconnectsSession(Type type)
    {
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, clientNexus) = CreateAuthServerClient(type);

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Disconnect);
        };

        _ = client.DisconnectedTask.ContinueWith(_ => disconnected.TrySetResult());

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        // The invocation will fail since the session gets disconnected
        try
        {
            await client.Proxy.ProtectedMethod("test").Timeout(1);
        }
        catch
        {
            // Expected - session disconnected
        }

        await disconnected.Task.Timeout(2);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task UnprotectedMethod_NoAuthCheck(Type type)
    {
        var authCalled = false;
        var methodInvoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, _) = CreateAuthServerClient(type);

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) =>
            {
                authCalled = true;
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Unauthorized);
            };
            nexus.UnprotectedMethodHandler = _ =>
            {
                methodInvoked.SetResult();
                return ValueTask.CompletedTask;
            };
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await client.Proxy.UnprotectedMethod().Timeout(1);
        await methodInvoked.Task.Timeout(1);
        Assert.That(authCalled, Is.False);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task MarkerOnly_CallsOnAuthorizeWithEmptyPermissions(Type type)
    {
        ReadOnlyMemory<int> capturedPermissions = default;
        var authCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, _) = CreateAuthServerClient(type);

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, perms) =>
            {
                capturedPermissions = perms;
                authCalled.SetResult();
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            };
            nexus.MarkerOnlyMethodHandler = _ => ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await client.Proxy.MarkerOnlyMethod().Timeout(1);
        await authCalled.Task.Timeout(1);
        Assert.That(capturedPermissions.Length, Is.EqualTo(0));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task OnAuthorize_ReceivesCorrectMethodName(Type type)
    {
        string? capturedName = null;
        var authCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, _) = CreateAuthServerClient(type);

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, name, _) =>
            {
                capturedName = name;
                authCalled.SetResult();
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            };
            nexus.ProtectedMethodHandler = (_, _) => ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await client.Proxy.ProtectedMethod("test").Timeout(1);
        await authCalled.Task.Timeout(1);
        Assert.That(capturedName, Is.EqualTo("ProtectedMethod"));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task OnAuthorize_ReceivesCorrectPermissions(Type type)
    {
        ReadOnlyMemory<int> capturedPermissions = default;
        var authCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, _) = CreateAuthServerClient(type);

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, perms) =>
            {
                capturedPermissions = perms;
                authCalled.SetResult();
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            };
            nexus.ProtectedMethodHandler = (_, _) => ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await client.Proxy.ProtectedMethod("test").Timeout(1);
        await authCalled.Task.Timeout(1);
        Assert.That(capturedPermissions.Length, Is.EqualTo(1));
        Assert.That(capturedPermissions.Span[0], Is.EqualTo((int)TestPermission.Write));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task Authorized_WithReturnValue_ReturnsResult(Type type)
    {
        var (server, client, _) = CreateAuthServerClient(type);

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            nexus.ProtectedWithReturnHandler = (_, v) => new ValueTask<int>(v * 2);
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var result = await client.Proxy.ProtectedWithReturn(21).Timeout(1);
        Assert.That(result, Is.EqualTo(42));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task Unauthorized_WithReturnValue_ThrowsNotReturns(Type type)
    {
        var (server, client, _) = CreateAuthServerClient(type);

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Unauthorized);
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        Assert.ThrowsAsync<ProxyUnauthorizedException>(async () =>
            await client.Proxy.ProtectedWithReturn(21).Timeout(1));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task Unauthorized_MethodBodyNeverExecutes(Type type)
    {
        var bodyExecuted = false;
        var (server, client, _) = CreateAuthServerClient(type);

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Unauthorized);
            nexus.ProtectedMethodHandler = (_, _) =>
            {
                bodyExecuted = true;
                return ValueTask.CompletedTask;
            };
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        try { await client.Proxy.ProtectedMethod("test").Timeout(1); } catch { }

        // Small delay to ensure any async work settles
        await Task.Delay(100);
        Assert.That(bodyExecuted, Is.False);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task OnAuthorize_ThrowsException_TreatedAsDisconnect(Type type)
    {
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, _) = CreateAuthServerClient(type);

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) => throw new InvalidOperationException("Auth error");
        };

        _ = client.DisconnectedTask.ContinueWith(_ => disconnected.TrySetResult());

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        try { await client.Proxy.ProtectedMethod("test").Timeout(1); } catch { }

        await disconnected.Task.Timeout(2);
    }
}
