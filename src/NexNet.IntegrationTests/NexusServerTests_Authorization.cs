using System.Net;
using NexNet.Asp;
using NexNet.Collections;
using NexNet.IntegrationTests.Collections;
using NexNet.Asp.HttpSocket;
using NexNet.Asp.WebSocket;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Invocation;
using NexNet.Pipes;
using NexNet.Transports;
using NexNet.Transports.HttpSocket;
using NexNet.Transports.WebSocket;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal class NexusServerTests_Authorization : BaseTests
{
    private readonly List<WebApplication> _aspApps = new();
    private readonly List<INexusServerFactory> _authServerFactories = new();
    private readonly List<INexusClient> _authClients = new();

    protected override async Task TearDown()
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

        await base.TearDown();
    }

    private (NexusServerFactory<AuthServerNexus, AuthServerNexus.ClientProxy> server,
        NexusClient<AuthClientNexus, AuthClientNexus.ServerProxy> client,
        AuthClientNexus clientNexus)
        CreateAuthServerClient(Type type, TimeSpan? authCacheDuration = null)
    {
        var sConfig = CreateServerConfig(type);
        if (authCacheDuration.HasValue)
            sConfig.AuthorizationCacheDuration = authCacheDuration.Value;
        var cConfig = CreateClientConfig(type);
        var serverFactory = new NexusServerFactory<AuthServerNexus, AuthServerNexus.ClientProxy>(sConfig);
        var clientNexus = new AuthClientNexus();
        var client = AuthClientNexus.CreateClient(cConfig, clientNexus);
        _authServerFactories.Add(serverFactory);
        _authClients.Add(client);

        StartAspIfNeeded(sConfig, serverFactory);

        return (serverFactory, client, clientNexus);
    }

    private (NexusServerFactory<AuthPipeServerNexus, AuthPipeServerNexus.ClientProxy> server,
        NexusClient<AuthPipeClientNexus, AuthPipeClientNexus.ServerProxy> client)
        CreateAuthPipeServerClient(Type type)
    {
        var sConfig = CreateServerConfig(type);
        var cConfig = CreateClientConfig(type);
        var serverFactory = new NexusServerFactory<AuthPipeServerNexus, AuthPipeServerNexus.ClientProxy>(sConfig);
        var client = AuthPipeClientNexus.CreateClient(cConfig, new AuthPipeClientNexus());
        _authServerFactories.Add(serverFactory);
        _authClients.Add(client);

        StartAspIfNeeded(sConfig, serverFactory);

        return (serverFactory, client);
    }

    private void StartAspIfNeeded<TServerNexus, TClientProxy>(
        ServerConfig sConfig,
        NexusServerFactory<TServerNexus, TClientProxy> serverFactory)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer, new()
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        if (sConfig is not (WebSocketServerConfig or HttpSocketServerConfig))
            return;

        CurrentTcpPort ??= FreeTcpPort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel((_, serverOptions) =>
            serverOptions.Listen(IPAddress.Loopback, CurrentTcpPort.Value));
        builder.Services.AddNexusServer<TServerNexus, TClientProxy>();
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


    [Test]
    [Transports]
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

    [Test]
    [Transports]
    public async Task AuthorizedMethod_Unauthorized_ThrowsOnClient(Type type)
    {
        var (server, client, _) = CreateAuthServerClient(type);

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Unauthorized);
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await Assert.ThrowsExactlyAsync<ProxyUnauthorizedException>(async () =>
            await client.Proxy.ProtectedMethod("test").Timeout(1));
    }

    [Test]
    [Transports]
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

    [Test]
    [Transports]
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
        await Assert.That(authCalled).IsFalse();
    }

    [Test]
    [Transports]
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
        await Assert.That(capturedPermissions.Length).IsEqualTo(0);
    }

    [Test]
    [Transports]
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
        await Assert.That(capturedName).IsEqualTo("ProtectedMethod");
    }

    [Test]
    [Transports]
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
        await Assert.That(capturedPermissions.Length).IsEqualTo(1);
        await Assert.That(capturedPermissions.Span[0]).IsEqualTo((int)TestPermission.Write);
    }

    [Test]
    [Transports]
    public async Task OnAuthorize_ReceivesMultiplePermissions(Type type)
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
            nexus.MultiPermissionMethodHandler = (_, _) => ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await client.Proxy.MultiPermissionMethod("test").Timeout(1);
        await authCalled.Task.Timeout(1);
        await Assert.That(capturedPermissions.Length).IsEqualTo(2);
        await Assert.That(capturedPermissions.Span[0]).IsEqualTo((int)TestPermission.Read);
        await Assert.That(capturedPermissions.Span[1]).IsEqualTo((int)TestPermission.Admin);
    }

    [Test]
    [Transports]
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
        await Assert.That(result).IsEqualTo(42);
    }

    [Test]
    [Transports]
    public async Task Unauthorized_WithReturnValue_ThrowsNotReturns(Type type)
    {
        var (server, client, _) = CreateAuthServerClient(type);

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Unauthorized);
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await Assert.ThrowsExactlyAsync<ProxyUnauthorizedException>(async () =>
            await client.Proxy.ProtectedWithReturn(21).Timeout(1));
    }

    [Test]
    [Arguments(Type.Uds, false)]
    [Arguments(Type.Tcp, false)]
    [Arguments(Type.TcpTls, false)]
    [Arguments(Type.Quic, false)]
    [Arguments(Type.WebSocket, false)]
    [Arguments(Type.HttpSocket, false)]
    [Arguments(Type.Uds, true)]
    [Arguments(Type.Tcp, true)]
    [Arguments(Type.TcpTls, true)]
    [Arguments(Type.Quic, true)]
    [Arguments(Type.WebSocket, true)]
    [Arguments(Type.HttpSocket, true)]
    public async Task Unauthorized_SessionRemainsUsable(Type type, bool cacheEnabled)
    {
        var unprotectedInvoked = 0;
        var (server, client, _) = CreateAuthServerClient(type,
            cacheEnabled ? TimeSpan.FromSeconds(30) : null);

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Unauthorized);
            nexus.UnprotectedMethodHandler = _ =>
            {
                Interlocked.Increment(ref unprotectedInvoked);
                return ValueTask.CompletedTask;
            };
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        // Twice each, so that with the cache enabled the second refusal comes from the cache.
        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsExactlyAsync<ProxyUnauthorizedException>(async () =>
                await client.Proxy.ProtectedMethod("test").Timeout(1));
            await Assert.ThrowsExactlyAsync<ProxyUnauthorizedException>(async () =>
                await client.Proxy.ProtectedWithReturn(21).Timeout(1));

            // Results arrive in order, so any extra result for a refused call has been read by the time this
            // call completes.
            await client.Proxy.UnprotectedMethod().Timeout(1);
        }

        await Assert.That(unprotectedInvoked).IsEqualTo(2);
        await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
    }

    [Test]
    [Transports]
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
        await Assert.That(bodyExecuted).IsFalse();
    }

    [Test]
    [Transports]
    public async Task AuthorizedCollection_Allowed_ClientReceivesData(Type type)
    {
        var (server, client, _) = CreateAuthServerClient(type);

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var enabled = await client.Proxy.ProtectedList.EnableAsync().Timeout(2);
        await Assert.That(enabled).IsTrue();

        var serverNexus = server.NexusCreatedQueue.First();
        var completeTask = client.Proxy.ProtectedList.WaitForEvent(
            NexNet.Collections.NexusCollectionChangedAction.Add, 1);

        await serverNexus.ProtectedList.AddAsync("hello");
        await completeTask.Wait(2);

        await Assert.That(client.Proxy.ProtectedList.Count).IsEqualTo(1);
    }

    [Test]
    [Transports]
    public async Task AuthorizedCollection_Unauthorized_ClientDisconnected(Type type)
    {
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, _) = CreateAuthServerClient(type);

        server.OnNexusCreated = nexus =>
        {
            // Return Disconnect for collection auth failures since Unauthorized without
            // a return channel would just leave the client hanging
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Disconnect);
        };

        _ = client.DisconnectedTask.ContinueWith(_ => disconnected.TrySetResult());

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        try
        {
            await client.Proxy.ProtectedList.EnableAsync().AsTask().Timeout(2);
        }
        catch
        {
            // Expected - session disconnected
        }

        await disconnected.Task.Timeout(2);
    }

    [Test]
    [Transports]
    public async Task AuthorizedCollection_Unauthorized_EnableFailsFastAndSessionStaysUp(Type type)
    {
        var unprotectedInvoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, _) = CreateAuthServerClient(type);

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Unauthorized);
            nexus.UnprotectedMethodHandler = _ =>
            {
                unprotectedInvoked.TrySetResult();
                return ValueTask.CompletedTask;
            };
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        // The server refuses the collection's pipe: the connect fails at once instead of hanging.
        await AssertThrows<NexusBusyException>(client.Proxy.ProtectedList.EnableAsync().AsTask().Timeout(1));

        // The session stays up and usable.
        await client.Proxy.UnprotectedMethod().Timeout(1);
        await unprotectedInvoked.Task.Timeout(1);
        await Assert.That(await client.Proxy.UnprotectedList.EnableAsync().Timeout(1)).IsTrue();
        await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
    }

    [Test]
    [Transports]
    public async Task UnprotectedCollection_NoAuthCheck(Type type)
    {
        var authCalled = false;
        var (server, client, _) = CreateAuthServerClient(type);

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) =>
            {
                authCalled = true;
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Unauthorized);
            };
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var enabled = await client.Proxy.UnprotectedList.EnableAsync().Timeout(2);
        await Assert.That(enabled).IsTrue();
        await Assert.That(authCalled).IsFalse();
    }

    [Test]
    [Transports]
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

    // --- Authorization cache tests ---
    // Use Uds only since caching is transport-independent

    [Test]
    [Transports(Type.Uds)]
    public async Task AuthCache_AttributeTtl_SecondCallUsesCachedResult(Type type)
    {
        var authCallCount = 0;
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var (server, client, _) = CreateAuthServerClient(type);
        server.Server.Config.Time = fakeTime;

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) =>
            {
                Interlocked.Increment(ref authCallCount);
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            };
            nexus.CachedMethodHandler = _ => ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        // No time advance — every call falls within the cache TTL deterministically.
        await client.Proxy.CachedMethod().Timeout(1);
        await client.Proxy.CachedMethod().Timeout(1);
        await client.Proxy.CachedMethod().Timeout(1);

        await Assert.That(authCallCount).IsEqualTo(1);
    }

    [Test]
    [Transports(Type.Uds)]
    public async Task AuthCache_AttributeTtl_ExpiredEntry_ReChecks(Type type)
    {
        var authCallCount = 0;
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var (server, client, _) = CreateAuthServerClient(type);
        server.Server.Config.Time = fakeTime;

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) =>
            {
                Interlocked.Increment(ref authCallCount);
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            };
            nexus.CachedMethodHandler = _ => ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await client.Proxy.CachedMethod().Timeout(1);
        await Assert.That(authCallCount).IsEqualTo(1);

        // Still cached
        await client.Proxy.CachedMethod().Timeout(1);
        await Assert.That(authCallCount).IsEqualTo(1);

        // Advance past the 2s TTL
        fakeTime.Advance(TimeSpan.FromMilliseconds(2001));

        await client.Proxy.CachedMethod().Timeout(1);
        await Assert.That(authCallCount).IsEqualTo(2);
    }

    [Test]
    [Transports(Type.Uds)]
    public async Task AuthCache_FakeTimeProvider_AdvanceJustBeforeTtl_StaysCached(Type type)
    {
        var authCallCount = 0;
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var (server, client, _) = CreateAuthServerClient(type);
        server.Server.Config.Time = fakeTime;

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) =>
            {
                Interlocked.Increment(ref authCallCount);
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            };
            nexus.CachedMethodHandler = _ => ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await client.Proxy.CachedMethod().Timeout(1);
        await Assert.That(authCallCount).IsEqualTo(1);

        // Advance to just before the 2s TTL would expire
        fakeTime.Advance(TimeSpan.FromMilliseconds(1999));

        await client.Proxy.CachedMethod().Timeout(1);
        await Assert.That(authCallCount).IsEqualTo(1).Because("Cache should remain hot until the TTL elapses");
    }

    [Test]
    [Transports(Type.Uds)]
    public async Task AuthCache_FakeTimeProvider_MultipleExpiryCycles_ReChecksEachTime(Type type)
    {
        var authCallCount = 0;
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var (server, client, _) = CreateAuthServerClient(type);
        server.Server.Config.Time = fakeTime;

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) =>
            {
                Interlocked.Increment(ref authCallCount);
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            };
            nexus.CachedMethodHandler = _ => ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        // Three full TTL cycles - each should re-invoke OnAuthorize once.
        for (var cycle = 1; cycle <= 3; cycle++)
        {
            await client.Proxy.CachedMethod().Timeout(1);
            await client.Proxy.CachedMethod().Timeout(1);
            await Assert.That(authCallCount).IsEqualTo(cycle).Because($"Cycle {cycle}: cache hits within TTL should not re-invoke OnAuthorize");

            fakeTime.Advance(TimeSpan.FromMilliseconds(2001));
        }
    }

    [Test]
    [Transports(Type.Uds)]
    public async Task AuthCache_NeverCached_AlwaysCallsOnAuthorize(Type type)
    {
        var authCallCount = 0;
        // Set server-level cache to prove CacheDurationSeconds=0 overrides it
        var (server, client, _) = CreateAuthServerClient(type, TimeSpan.FromSeconds(60));

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) =>
            {
                Interlocked.Increment(ref authCallCount);
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            };
            nexus.NeverCachedMethodHandler = _ => ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await client.Proxy.NeverCachedMethod().Timeout(1);
        await client.Proxy.NeverCachedMethod().Timeout(1);
        await client.Proxy.NeverCachedMethod().Timeout(1);

        await Assert.That(authCallCount).IsEqualTo(3);
    }

    [Test]
    [Transports(Type.Uds)]
    public async Task AuthCache_ServerConfigDefault_CachesWhenAttributeIsDefault(Type type)
    {
        var authCallCount = 0;
        // ProtectedMethod has no CacheDurationSeconds (defaults to -1), so server config applies
        var (server, client, _) = CreateAuthServerClient(type, TimeSpan.FromSeconds(30));

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) =>
            {
                Interlocked.Increment(ref authCallCount);
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            };
            nexus.ProtectedMethodHandler = (_, _) => ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await client.Proxy.ProtectedMethod("a").Timeout(1);
        await client.Proxy.ProtectedMethod("b").Timeout(1);
        await client.Proxy.ProtectedMethod("c").Timeout(1);

        await Assert.That(authCallCount).IsEqualTo(1);
    }

    [Test]
    [Transports(Type.Uds)]
    public async Task AuthCache_NoServerConfig_NoCaching(Type type)
    {
        var authCallCount = 0;
        // No server config cache, ProtectedMethod has no CacheDurationSeconds override
        var (server, client, _) = CreateAuthServerClient(type);

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) =>
            {
                Interlocked.Increment(ref authCallCount);
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            };
            nexus.ProtectedMethodHandler = (_, _) => ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await client.Proxy.ProtectedMethod("a").Timeout(1);
        await client.Proxy.ProtectedMethod("b").Timeout(1);

        await Assert.That(authCallCount).IsEqualTo(2);
    }

    [Test]
    [Transports(Type.Uds)]
    public async Task AuthCache_CachesUnauthorizedResult(Type type)
    {
        var authCallCount = 0;
        var (server, client, _) = CreateAuthServerClient(type);

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) =>
            {
                Interlocked.Increment(ref authCallCount);
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Unauthorized);
            };
            nexus.CachedMethodHandler = _ => ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        // CachedMethod has return type ValueTask (void), so Unauthorized with no return channel
        // means silent drop. Just verify OnAuthorize is called once.
        try { await client.Proxy.CachedMethod().Timeout(1); } catch { }
        try { await client.Proxy.CachedMethod().Timeout(1); } catch { }

        await Assert.That(authCallCount).IsEqualTo(1);
    }

    [Test]
    [Transports(Type.Uds)]
    public async Task AuthCache_DisconnectNeverCached(Type type)
    {
        var authCallCount = 0;
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, _) = CreateAuthServerClient(type);

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) =>
            {
                Interlocked.Increment(ref authCallCount);
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Disconnect);
            };
            nexus.CachedMethodHandler = _ => ValueTask.CompletedTask;
        };

        _ = client.DisconnectedTask.ContinueWith(_ => disconnected.TrySetResult());

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        try { await client.Proxy.CachedMethod().Timeout(1); } catch { }

        await disconnected.Task.Timeout(2);
        // Disconnect result should not be cached (session dies anyway)
        await Assert.That(authCallCount).IsEqualTo(1);
    }

    [Test]
    [Transports(Type.Uds)]
    public async Task AuthCache_DifferentMethodsHaveIndependentEntries(Type type)
    {
        var protectedCallCount = 0;
        var cachedCallCount = 0;
        var (server, client, _) = CreateAuthServerClient(type, TimeSpan.FromSeconds(30));

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, methodId, _, _) =>
            {
                // Track per-method auth calls by checking which method was invoked
                // ProtectedMethod and CachedMethod will have different method IDs
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            };
            nexus.ProtectedMethodHandler = (_, _) =>
            {
                Interlocked.Increment(ref protectedCallCount);
                return ValueTask.CompletedTask;
            };
            nexus.CachedMethodHandler = _ =>
            {
                Interlocked.Increment(ref cachedCallCount);
                return ValueTask.CompletedTask;
            };
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await client.Proxy.ProtectedMethod("a").Timeout(1);
        await client.Proxy.CachedMethod().Timeout(1);
        await client.Proxy.ProtectedMethod("b").Timeout(1);
        await client.Proxy.CachedMethod().Timeout(1);

        // Both methods should have been invoked twice (cache is per-method)
        await Assert.That(protectedCallCount).IsEqualTo(2);
        await Assert.That(cachedCallCount).IsEqualTo(2);
    }

    [Test]
    [Transports(Type.Uds)]
    public async Task AuthCache_InvalidateAll_ClearsCache(Type type)
    {
        var authCallCount = 0;
        var (server, client, _) = CreateAuthServerClient(type);

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) =>
            {
                Interlocked.Increment(ref authCallCount);
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            };
            nexus.CachedMethodHandler = _ => ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await client.Proxy.CachedMethod().Timeout(1);
        await Assert.That(authCallCount).IsEqualTo(1);

        // Invalidate cache
        var serverNexus = server.NexusCreatedQueue.First();
        serverNexus.CallInvalidateAll();

        await client.Proxy.CachedMethod().Timeout(1);
        await Assert.That(authCallCount).IsEqualTo(2);
    }

    [Test]
    [Transports(Type.Uds)]
    public async Task AuthCache_InvalidateByMethodId_ClearsSingleEntry(Type type)
    {
        var authCallCount = 0;
        int cachedMethodId = -1;
        var (server, client, _) = CreateAuthServerClient(type, TimeSpan.FromSeconds(30));

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, methodId, name, _) =>
            {
                Interlocked.Increment(ref authCallCount);
                if (name == "CachedMethod")
                    cachedMethodId = methodId;
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            };
            nexus.CachedMethodHandler = _ => ValueTask.CompletedTask;
            nexus.ProtectedMethodHandler = (_, _) => ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        // Prime both caches
        await client.Proxy.CachedMethod().Timeout(1);
        await client.Proxy.ProtectedMethod("a").Timeout(1);
        await Assert.That(authCallCount).IsEqualTo(2);

        // Invalidate only CachedMethod
        var serverNexus = server.NexusCreatedQueue.First();
        serverNexus.CallInvalidateMethod(cachedMethodId);

        // CachedMethod should re-check, ProtectedMethod should still be cached
        await client.Proxy.CachedMethod().Timeout(1);
        await client.Proxy.ProtectedMethod("b").Timeout(1);
        await Assert.That(authCallCount).IsEqualTo(3); // Only CachedMethod re-checked
    }

    [Test]
    [Transports(Type.Uds)]
    public async Task AuthCache_ExceptionNotCached_NextCallReInvokes(Type type)
    {
        var authCallCount = 0;
        var shouldThrow = true;
        var (server, client, _) = CreateAuthServerClient(type);

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) =>
            {
                Interlocked.Increment(ref authCallCount);
                if (shouldThrow)
                    throw new InvalidOperationException("Auth error");
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            };
            nexus.CachedMethodHandler = _ => ValueTask.CompletedTask;
        };

        _ = client.DisconnectedTask.ContinueWith(_ => { });

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        try { await client.Proxy.CachedMethod().Timeout(1); } catch { }

        // Session disconnected due to exception — auth was called once
        await Assert.That(authCallCount).IsEqualTo(1);
    }

    [Test]
    [Transports(Type.Uds)]
    public async Task AuthCache_ConcurrentAccess_NoExceptions(Type type)
    {
        var authCallCount = 0;
        var allThreadsReady = new ManualResetEventSlim(false);
        var (server, client, _) = CreateAuthServerClient(type);

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) =>
            {
                Interlocked.Increment(ref authCallCount);
                // Force all threads to pile up inside OnAuthorize simultaneously
                allThreadsReady.Wait(TimeSpan.FromSeconds(2));
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            };
            nexus.CachedMethodHandler = _ => ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        const int concurrentCalls = 10;
        var tasks = new Task[concurrentCalls];
        for (int i = 0; i < concurrentCalls; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                try { await client.Proxy.CachedMethod().Timeout(5); } catch { }
            });
        }

        // Let all threads proceed once they've piled up
        await Task.Delay(200);
        allThreadsReady.Set();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));

        // All calls should have completed without exceptions.
        // Auth may be called multiple times (cache misses race) but never throws.
        await Assert.That(authCallCount).IsGreaterThan(0);
    }
    [Test]
    [Transports]
    public async Task AuthorizedPipeMethod_Unauthorized_RefusesPipe(Type type)
    {
        var (server, client) = CreateAuthPipeServerClient(type);
        var methodInvoked = false;

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Unauthorized);
            nexus.ProtectedPipeHandler = (_, _) =>
            {
                methodInvoked = true;
                return ValueTask.CompletedTask;
            };
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        // Twice: the refused pipe's ID is freed and the second open fails the same way.
        for (var i = 0; i < 2; i++)
        {
            await using var pipe = client.CreatePipe();
            await client.Proxy.ProtectedPipe("test", pipe).Timeout(1);
            await AssertThrows<NexusBusyException>(pipe.ReadyTask.Timeout(1));
        }

        // The session stays up and usable.
        await Assert.That(await client.Proxy.Ping(5).Timeout(1)).IsEqualTo(5);
        await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
        await Assert.That(methodInvoked).IsFalse();
    }

    [Test]
    [Transports]
    public async Task AuthorizedChannelMethod_Unauthorized_RefusesChannel(Type type)
    {
        var (server, client) = CreateAuthPipeServerClient(type);
        var methodInvoked = false;

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Unauthorized);
            nexus.ProtectedChannelHandler = (_, _, _) =>
            {
                methodInvoked = true;
                return ValueTask.CompletedTask;
            };
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await using var channel = client.CreateChannel<int>();
        await client.Proxy.ProtectedChannel(42, "test", channel).Timeout(1);
        await AssertThrows<NexusBusyException>(channel.BasePipe!.ReadyTask.Timeout(1));

        await Assert.That(await client.Proxy.Ping(7).Timeout(1)).IsEqualTo(7);
        await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
        await Assert.That(methodInvoked).IsFalse();
    }

    [Test]
    [Transports]
    public async Task AuthorizedPipeMethod_Allowed_PipeBecomesReady(Type type)
    {
        var (server, client) = CreateAuthPipeServerClient(type);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            nexus.ProtectedPipeHandler = async (input, pipe) =>
            {
                received.TrySetResult(input);
                await pipe.CompleteTask;
            };
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await using var pipe = client.CreatePipe();
        await client.Proxy.ProtectedPipe("test", pipe).Timeout(1);
        await pipe.ReadyTask.Timeout(1);
        await Assert.That(await received.Task.Timeout(1)).IsEqualTo("test");
    }

    [Test]
    [Transports]
    public async Task AuthorizedPipeMethod_Disconnect_DisconnectsSession(Type type)
    {
        var (server, client) = CreateAuthPipeServerClient(type);

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Disconnect);
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await using var pipe = client.CreatePipe();
        try
        {
            await client.Proxy.ProtectedPipe("test", pipe).Timeout(1);
        }
        catch
        {
            // The session may already be closing.
        }

        await client.DisconnectedTask.Timeout(2);
        await Assert.That(client.State).IsEqualTo(ConnectionState.Disconnected);
    }

    private static async Task AssertThrows<TException>(Task task)
        where TException : Exception
    {
        try
        {
            await task;
        }
        catch (TException)
        {
            return;
        }

        Fail.Test($"Expected {typeof(TException).Name}.");
    }
}

public partial interface IAuthPipeServerNexus
{
    ValueTask ProtectedPipe(string input, INexusDuplexPipe pipe);
    ValueTask ProtectedChannel(int value, string input, INexusDuplexChannel<int> channel);
    ValueTask<int> Ping(int value);
}

public partial interface IAuthPipeClientNexus { }

[Nexus<IAuthPipeServerNexus, IAuthPipeClientNexus>(NexusType = NexusType.Server)]
public partial class AuthPipeServerNexus
{
    public Func<ServerSessionContext<ClientProxy>, int, string, ReadOnlyMemory<int>, ValueTask<AuthorizeResult>>? OnAuthorizeHandler;
    public Func<string, INexusDuplexPipe, ValueTask>? ProtectedPipeHandler;
    public Func<int, string, INexusDuplexChannel<int>, ValueTask>? ProtectedChannelHandler;

    [NexusAuthorize<TestPermission>(TestPermission.Write)]
    public ValueTask ProtectedPipe(string input, INexusDuplexPipe pipe)
    {
        return ProtectedPipeHandler?.Invoke(input, pipe) ?? ValueTask.CompletedTask;
    }

    [NexusAuthorize<TestPermission>(TestPermission.Write)]
    public ValueTask ProtectedChannel(int value, string input, INexusDuplexChannel<int> channel)
    {
        return ProtectedChannelHandler?.Invoke(value, input, channel) ?? ValueTask.CompletedTask;
    }

    public ValueTask<int> Ping(int value) => new(value);

    protected override ValueTask<AuthorizeResult> OnAuthorize(
        ServerSessionContext<ClientProxy> context,
        int methodId,
        string methodName,
        ReadOnlyMemory<int> requiredPermissions)
    {
        return OnAuthorizeHandler?.Invoke(context, methodId, methodName, requiredPermissions)
               ?? new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
    }
}

[Nexus<IAuthPipeClientNexus, IAuthPipeServerNexus>(NexusType = NexusType.Client)]
public partial class AuthPipeClientNexus { }