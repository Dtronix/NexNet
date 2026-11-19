using System.Text;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Invocation;
using NexNet.Logging;
using NexNet.Pipes;
using NexNet.Quic;
using NexNet.TestNexus.Interop;
using NexNet.Transports;
using NexNet.Transports.Uds;
using NexNet.InteropServer;
#pragma warning disable VSTHRD200

// The C# test scaffolding (remote proxies, script facades) of the authorization nexuses, for the client matrix.
[assembly: NexNet.Interop.NexusTestScaffolding<NexNet.IntegrationTests.TestInterfaces.IAuthServerNexus, NexNet.IntegrationTests.TestInterfaces.IAuthClientNexus>]
[assembly: NexNet.Interop.NexusTestScaffolding<NexNet.IntegrationTests.TestInterfaces.IAuthPipeServerNexus, NexNet.IntegrationTests.TestInterfaces.IAuthPipeClientNexus>]

namespace NexNet.IntegrationTests;

/// <summary>
/// Method, collection, pipe and channel authorization (<c>[NexusAuthorize]</c>, <c>OnAuthorize</c>, the authorization
/// cache) and the NexNet greeting token, on the client matrix (P01d). The server is the C# server of the test; the
/// client under test comes from <see cref="Harness"/> for each <see cref="ClientTarget"/> row.
/// </summary>
/// <remarks>
/// The QUIC rows of the C# suite are C#-only rows (<see cref="ClientMatrixAttribute.CSharpTransports"/>, H16).
/// </remarks>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class NexusServerTests_Authorization(LanguageHostPool hosts) : MatrixTestBase(hosts)
{

    /// <summary>
    /// Starts an <see cref="AuthServerNexus"/> server for <paramref name="target"/>'s transport, then creates and connects the
    /// client under test. <paramref name="nexusCreated"/> sets up each session's nexus; <paramref name="configure"/> the
    /// server configuration.
    /// </summary>
    private async Task<IClientUnderTest> ConnectAuthAsync(
        ClientTarget target,
        Action<AuthServerNexus> nexusCreated,
        Action<ServerConfig>? configure = null)
    {
        var type = ToType(target.Transport);
        var serverConfig = CreateServerConfig(type);
        configure?.Invoke(serverConfig);
        var server = CreateServer<AuthServerNexus, AuthServerNexus.ClientProxy>(serverConfig, nexusCreated);
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        return await ConnectAsync<AuthClientNexus, AuthClientNexus.ServerProxy>(
            target, Spec(CreateClientConfig(type), target.Transport, Logger));
    }

    /// <summary>As <see cref="ConnectAuthAsync"/>, with an <see cref="AuthPipeServerNexus"/> server.</summary>
    private async Task<IClientUnderTest> ConnectAuthPipeAsync(ClientTarget target, Action<AuthPipeServerNexus> nexusCreated)
    {
        var type = ToType(target.Transport);
        var serverConfig = CreateServerConfig(type);
        var server = CreateServer<AuthPipeServerNexus, AuthPipeServerNexus.ClientProxy>(serverConfig, nexusCreated);
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        return await ConnectAsync<AuthPipeClientNexus, AuthPipeClientNexus.ServerProxy>(
            target, Spec(CreateClientConfig(type), target.Transport, Logger));
    }

    private static Remote_IAuthServerNexus AuthProxy(IClientUnderTest client) => client.Proxy<Remote_IAuthServerNexus>();

    private static Remote_IAuthPipeServerNexus PipeProxy(IClientUnderTest client) => client.Proxy<Remote_IAuthPipeServerNexus>();

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task AuthorizedMethod_Allowed_InvokesMethod(ClientTarget target)
    {
        var methodInvoked = Signal();
        await using var client = await ConnectAuthAsync(target, nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            nexus.ProtectedMethodHandler = (_, _) =>
            {
                methodInvoked.SetResult();
                return ValueTask.CompletedTask;
            };
        });

        await AuthProxy(client).ProtectedMethod("test").Timeout(T(1));
        await methodInvoked.Task.Timeout(T(1));
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task AuthorizedMethod_Unauthorized_ThrowsOnClient(ClientTarget target)
    {
        await using var client = await ConnectAuthAsync(target, nexus =>
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Unauthorized));

        await Assert.ThrowsExactlyAsync<ProxyUnauthorizedException>(async () =>
            await AuthProxy(client).ProtectedMethod("test").Timeout(T(1)));
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task AuthorizedMethod_Disconnect_DisconnectsSession(ClientTarget target)
    {
        await using var client = await ConnectAuthAsync(target, nexus =>
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Disconnect));

        // The invocation fails: the session gets disconnected.
        try
        {
            await AuthProxy(client).ProtectedMethod("test").Timeout(T(1));
        }
        catch
        {
            // Expected - session disconnected
        }

        await client.WhenDisconnectedAsync(T(2));
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task UnprotectedMethod_NoAuthCheck(ClientTarget target)
    {
        var authCalled = false;
        var methodInvoked = Signal();
        await using var client = await ConnectAuthAsync(target, nexus =>
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
        });

        await AuthProxy(client).UnprotectedMethod().Timeout(T(1));
        await methodInvoked.Task.Timeout(T(1));
        await Assert.That(authCalled).IsFalse();
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task MarkerOnly_CallsOnAuthorizeWithEmptyPermissions(ClientTarget target)
    {
        ReadOnlyMemory<int> capturedPermissions = default;
        var authCalled = Signal();
        await using var client = await ConnectAuthAsync(target, nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, perms) =>
            {
                capturedPermissions = perms;
                authCalled.SetResult();
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            };
            nexus.MarkerOnlyMethodHandler = _ => ValueTask.CompletedTask;
        });

        await AuthProxy(client).MarkerOnlyMethod().Timeout(T(1));
        await authCalled.Task.Timeout(T(1));
        await Assert.That(capturedPermissions.Length).IsEqualTo(0);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task OnAuthorize_ReceivesCorrectMethodName(ClientTarget target)
    {
        string? capturedName = null;
        var authCalled = Signal();
        await using var client = await ConnectAuthAsync(target, nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, name, _) =>
            {
                capturedName = name;
                authCalled.SetResult();
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            };
            nexus.ProtectedMethodHandler = (_, _) => ValueTask.CompletedTask;
        });

        await AuthProxy(client).ProtectedMethod("test").Timeout(T(1));
        await authCalled.Task.Timeout(T(1));
        await Assert.That(capturedName).IsEqualTo("ProtectedMethod");
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task OnAuthorize_ReceivesCorrectPermissions(ClientTarget target)
    {
        ReadOnlyMemory<int> capturedPermissions = default;
        var authCalled = Signal();
        await using var client = await ConnectAuthAsync(target, nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, perms) =>
            {
                capturedPermissions = perms;
                authCalled.SetResult();
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            };
            nexus.ProtectedMethodHandler = (_, _) => ValueTask.CompletedTask;
        });

        await AuthProxy(client).ProtectedMethod("test").Timeout(T(1));
        await authCalled.Task.Timeout(T(1));
        await Assert.That(capturedPermissions.Length).IsEqualTo(1);
        await Assert.That(capturedPermissions.Span[0]).IsEqualTo((int)TestPermission.Write);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task OnAuthorize_ReceivesMultiplePermissions(ClientTarget target)
    {
        ReadOnlyMemory<int> capturedPermissions = default;
        var authCalled = Signal();
        await using var client = await ConnectAuthAsync(target, nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, perms) =>
            {
                capturedPermissions = perms;
                authCalled.SetResult();
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            };
            nexus.MultiPermissionMethodHandler = (_, _) => ValueTask.CompletedTask;
        });

        await AuthProxy(client).MultiPermissionMethod("test").Timeout(T(1));
        await authCalled.Task.Timeout(T(1));
        await Assert.That(capturedPermissions.Length).IsEqualTo(2);
        await Assert.That(capturedPermissions.Span[0]).IsEqualTo((int)TestPermission.Read);
        await Assert.That(capturedPermissions.Span[1]).IsEqualTo((int)TestPermission.Admin);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Authorized_WithReturnValue_ReturnsResult(ClientTarget target)
    {
        await using var client = await ConnectAuthAsync(target, nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            nexus.ProtectedWithReturnHandler = (_, v) => new ValueTask<int>(v * 2);
        });

        var result = await AuthProxy(client).ProtectedWithReturn(21).Timeout(T(1));
        await Assert.That(result).IsEqualTo(42);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Unauthorized_WithReturnValue_ThrowsNotReturns(ClientTarget target)
    {
        await using var client = await ConnectAuthAsync(target, nexus =>
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Unauthorized));

        await Assert.ThrowsExactlyAsync<ProxyUnauthorizedException>(async () =>
            await AuthProxy(client).ProtectedWithReturn(21).Timeout(T(1)));
        await EndAsync(client);
    }

    // One test per cache setting: a ClientMatrix row cannot be combined with [Arguments].
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public Task Unauthorized_SessionRemainsUsable(ClientTarget target)
        => UnauthorizedSessionRemainsUsable(target, cacheEnabled: false);

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public Task Unauthorized_SessionRemainsUsable_CacheEnabled(ClientTarget target)
        => UnauthorizedSessionRemainsUsable(target, cacheEnabled: true);

    private async Task UnauthorizedSessionRemainsUsable(ClientTarget target, bool cacheEnabled)
    {
        var unprotectedInvoked = 0;
        await using var client = await ConnectAuthAsync(
            target,
            nexus =>
            {
                nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Unauthorized);
                nexus.UnprotectedMethodHandler = _ =>
                {
                    Interlocked.Increment(ref unprotectedInvoked);
                    return ValueTask.CompletedTask;
                };
            },
            config =>
            {
                if (cacheEnabled)
                    config.AuthorizationCacheDuration = TimeSpan.FromSeconds(30);
            });
        var proxy = AuthProxy(client);

        // Twice each, so that with the cache enabled the second refusal comes from the cache.
        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsExactlyAsync<ProxyUnauthorizedException>(async () =>
                await proxy.ProtectedMethod("test").Timeout(T(1)));
            await Assert.ThrowsExactlyAsync<ProxyUnauthorizedException>(async () =>
                await proxy.ProtectedWithReturn(21).Timeout(T(1)));

            // Results arrive in order, so any extra result for a refused call has been read by the time this
            // call completes.
            await proxy.UnprotectedMethod().Timeout(T(1));
        }

        await Assert.That(unprotectedInvoked).IsEqualTo(2);
        await client.WaitForStateAsync(ConnectionState.Connected, T(1));
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Unauthorized_MethodBodyNeverExecutes(ClientTarget target)
    {
        var bodyExecuted = false;
        await using var client = await ConnectAuthAsync(target, nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Unauthorized);
            nexus.ProtectedMethodHandler = (_, _) =>
            {
                bodyExecuted = true;
                return ValueTask.CompletedTask;
            };
            nexus.UnprotectedMethodHandler = _ => ValueTask.CompletedTask;
        });
        var proxy = AuthProxy(client);

        try { await proxy.ProtectedMethod("test").Timeout(T(1)); } catch { }

        // Barrier: a later call on the same session has completed.
        await proxy.UnprotectedMethod().Timeout(T(1));
        await Assert.That(bodyExecuted).IsFalse();
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task AuthorizedCollection_Allowed_ClientReceivesData(ClientTarget target)
    {
        AuthServerNexus? serverNexus = null;
        await using var client = await ConnectAuthAsync(target, nexus =>
        {
            serverNexus ??= nexus;
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
        });
        var list = AuthProxy(client).ProtectedList;

        var enabled = await list.EnableAsync().AsTask().Timeout(T(2));
        await Assert.That(enabled).IsTrue();

        await serverNexus!.ProtectedList.AddAsync("hello");
        var items = await list.WaitForAsync(l => l.Count == 1, TimeSpan.FromSeconds(T(2)));

        await Assert.That(items).IsEquivalentTo(new[] { "hello" });
        await list.DisableAsync().AsTask().Timeout(T(1));
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task AuthorizedCollection_Unauthorized_ClientDisconnected(ClientTarget target)
    {
        // Disconnect for collection auth failures: the session ends instead of the connect failing.
        await using var client = await ConnectAuthAsync(target, nexus =>
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Disconnect));

        try
        {
            await AuthProxy(client).ProtectedList.EnableAsync().AsTask().Timeout(T(2));
        }
        catch
        {
            // Expected - session disconnected
        }

        await client.WhenDisconnectedAsync(T(2));
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task AuthorizedCollection_Unauthorized_EnableFailsFastAndSessionStaysUp(ClientTarget target)
    {
        var unprotectedInvoked = Signal();
        await using var client = await ConnectAuthAsync(target, nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Unauthorized);
            nexus.UnprotectedMethodHandler = _ =>
            {
                unprotectedInvoked.TrySetResult();
                return ValueTask.CompletedTask;
            };
        });
        var proxy = AuthProxy(client);

        // The server refuses the collection's pipe: the connect fails at once instead of hanging.
        await AssertThrows<NexusBusyException>(proxy.ProtectedList.EnableAsync().AsTask().Timeout(T(1)));

        // The session stays up and usable.
        await proxy.UnprotectedMethod().Timeout(T(1));
        await unprotectedInvoked.Task.Timeout(T(1));
        await Assert.That(await proxy.UnprotectedList.EnableAsync().AsTask().Timeout(T(1))).IsTrue();
        await client.WaitForStateAsync(ConnectionState.Connected, T(1));
        await proxy.UnprotectedList.DisableAsync().AsTask().Timeout(T(1));
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task UnprotectedCollection_NoAuthCheck(ClientTarget target)
    {
        var authCalled = false;
        await using var client = await ConnectAuthAsync(target, nexus =>
            nexus.OnAuthorizeHandler = (_, _, _, _) =>
            {
                authCalled = true;
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Unauthorized);
            });
        var list = AuthProxy(client).UnprotectedList;

        var enabled = await list.EnableAsync().AsTask().Timeout(T(2));
        await Assert.That(enabled).IsTrue();
        await Assert.That(authCalled).IsFalse();
        await list.DisableAsync().AsTask().Timeout(T(1));
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task OnAuthorize_ThrowsException_TreatedAsDisconnect(ClientTarget target)
    {
        await using var client = await ConnectAuthAsync(target, nexus =>
            nexus.OnAuthorizeHandler = (_, _, _, _) => throw new InvalidOperationException("Auth error"));

        try { await AuthProxy(client).ProtectedMethod("test").Timeout(T(1)); } catch { }

        await client.WhenDisconnectedAsync(T(2));
        await EndAsync(client);
    }

    // --- Authorization cache tests ---
    // Caching is transport-independent: UDS as before, plus WebSocket so that every client language has a row.

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket])]
    public async Task AuthCache_AttributeTtl_SecondCallUsesCachedResult(ClientTarget target)
    {
        var authCallCount = 0;
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var client = await ConnectAuthAsync(
            target,
            nexus =>
            {
                nexus.OnAuthorizeHandler = (_, _, _, _) =>
                {
                    Interlocked.Increment(ref authCallCount);
                    return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
                };
                nexus.CachedMethodHandler = _ => ValueTask.CompletedTask;
            },
            config => config.Time = fakeTime);
        var proxy = AuthProxy(client);

        // No time advance — every call falls within the cache TTL deterministically.
        await proxy.CachedMethod().Timeout(T(1));
        await proxy.CachedMethod().Timeout(T(1));
        await proxy.CachedMethod().Timeout(T(1));

        await Assert.That(authCallCount).IsEqualTo(1);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket])]
    public async Task AuthCache_AttributeTtl_ExpiredEntry_ReChecks(ClientTarget target)
    {
        var authCallCount = 0;
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var client = await ConnectAuthAsync(
            target,
            nexus =>
            {
                nexus.OnAuthorizeHandler = (_, _, _, _) =>
                {
                    Interlocked.Increment(ref authCallCount);
                    return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
                };
                nexus.CachedMethodHandler = _ => ValueTask.CompletedTask;
            },
            config => config.Time = fakeTime);
        var proxy = AuthProxy(client);

        await proxy.CachedMethod().Timeout(T(1));
        await Assert.That(authCallCount).IsEqualTo(1);

        // Still cached
        await proxy.CachedMethod().Timeout(T(1));
        await Assert.That(authCallCount).IsEqualTo(1);

        // Advance past the 2s TTL
        fakeTime.Advance(TimeSpan.FromMilliseconds(2001));

        await proxy.CachedMethod().Timeout(T(1));
        await Assert.That(authCallCount).IsEqualTo(2);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket])]
    public async Task AuthCache_FakeTimeProvider_AdvanceJustBeforeTtl_StaysCached(ClientTarget target)
    {
        var authCallCount = 0;
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var client = await ConnectAuthAsync(
            target,
            nexus =>
            {
                nexus.OnAuthorizeHandler = (_, _, _, _) =>
                {
                    Interlocked.Increment(ref authCallCount);
                    return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
                };
                nexus.CachedMethodHandler = _ => ValueTask.CompletedTask;
            },
            config => config.Time = fakeTime);
        var proxy = AuthProxy(client);

        await proxy.CachedMethod().Timeout(T(1));
        await Assert.That(authCallCount).IsEqualTo(1);

        // Advance to just before the 2s TTL would expire
        fakeTime.Advance(TimeSpan.FromMilliseconds(1999));

        await proxy.CachedMethod().Timeout(T(1));
        await Assert.That(authCallCount).IsEqualTo(1).Because("Cache should remain hot until the TTL elapses");
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket])]
    public async Task AuthCache_FakeTimeProvider_MultipleExpiryCycles_ReChecksEachTime(ClientTarget target)
    {
        var authCallCount = 0;
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var client = await ConnectAuthAsync(
            target,
            nexus =>
            {
                nexus.OnAuthorizeHandler = (_, _, _, _) =>
                {
                    Interlocked.Increment(ref authCallCount);
                    return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
                };
                nexus.CachedMethodHandler = _ => ValueTask.CompletedTask;
            },
            config => config.Time = fakeTime);
        var proxy = AuthProxy(client);

        // Three full TTL cycles - each should re-invoke OnAuthorize once.
        for (var cycle = 1; cycle <= 3; cycle++)
        {
            await proxy.CachedMethod().Timeout(T(1));
            await proxy.CachedMethod().Timeout(T(1));
            await Assert.That(authCallCount).IsEqualTo(cycle).Because($"Cycle {cycle}: cache hits within TTL should not re-invoke OnAuthorize");

            fakeTime.Advance(TimeSpan.FromMilliseconds(2001));
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket])]
    public async Task AuthCache_NeverCached_AlwaysCallsOnAuthorize(ClientTarget target)
    {
        var authCallCount = 0;
        // Set server-level cache to prove CacheDurationSeconds=0 overrides it
        await using var client = await ConnectAuthAsync(
            target,
            nexus =>
            {
                nexus.OnAuthorizeHandler = (_, _, _, _) =>
                {
                    Interlocked.Increment(ref authCallCount);
                    return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
                };
                nexus.NeverCachedMethodHandler = _ => ValueTask.CompletedTask;
            },
            config => config.AuthorizationCacheDuration = TimeSpan.FromSeconds(60));
        var proxy = AuthProxy(client);

        await proxy.NeverCachedMethod().Timeout(T(1));
        await proxy.NeverCachedMethod().Timeout(T(1));
        await proxy.NeverCachedMethod().Timeout(T(1));

        await Assert.That(authCallCount).IsEqualTo(3);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket])]
    public async Task AuthCache_ServerConfigDefault_CachesWhenAttributeIsDefault(ClientTarget target)
    {
        var authCallCount = 0;
        // ProtectedMethod has no CacheDurationSeconds (defaults to -1), so server config applies
        await using var client = await ConnectAuthAsync(
            target,
            nexus =>
            {
                nexus.OnAuthorizeHandler = (_, _, _, _) =>
                {
                    Interlocked.Increment(ref authCallCount);
                    return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
                };
                nexus.ProtectedMethodHandler = (_, _) => ValueTask.CompletedTask;
            },
            config => config.AuthorizationCacheDuration = TimeSpan.FromSeconds(30));
        var proxy = AuthProxy(client);

        await proxy.ProtectedMethod("a").Timeout(T(1));
        await proxy.ProtectedMethod("b").Timeout(T(1));
        await proxy.ProtectedMethod("c").Timeout(T(1));

        await Assert.That(authCallCount).IsEqualTo(1);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket])]
    public async Task AuthCache_NoServerConfig_NoCaching(ClientTarget target)
    {
        var authCallCount = 0;
        // No server config cache, ProtectedMethod has no CacheDurationSeconds override
        await using var client = await ConnectAuthAsync(target, nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) =>
            {
                Interlocked.Increment(ref authCallCount);
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            };
            nexus.ProtectedMethodHandler = (_, _) => ValueTask.CompletedTask;
        });
        var proxy = AuthProxy(client);

        await proxy.ProtectedMethod("a").Timeout(T(1));
        await proxy.ProtectedMethod("b").Timeout(T(1));

        await Assert.That(authCallCount).IsEqualTo(2);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket])]
    public async Task AuthCache_CachesUnauthorizedResult(ClientTarget target)
    {
        var authCallCount = 0;
        await using var client = await ConnectAuthAsync(target, nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) =>
            {
                Interlocked.Increment(ref authCallCount);
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Unauthorized);
            };
            nexus.CachedMethodHandler = _ => ValueTask.CompletedTask;
        });
        var proxy = AuthProxy(client);

        // Both calls are refused (ProxyUnauthorizedException); verify OnAuthorize is called once.
        try { await proxy.CachedMethod().Timeout(T(1)); } catch { }
        try { await proxy.CachedMethod().Timeout(T(1)); } catch { }

        await Assert.That(authCallCount).IsEqualTo(1);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket])]
    public async Task AuthCache_DisconnectNeverCached(ClientTarget target)
    {
        var authCallCount = 0;
        await using var client = await ConnectAuthAsync(target, nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) =>
            {
                Interlocked.Increment(ref authCallCount);
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Disconnect);
            };
            nexus.CachedMethodHandler = _ => ValueTask.CompletedTask;
        });

        try { await AuthProxy(client).CachedMethod().Timeout(T(1)); } catch { }

        await client.WhenDisconnectedAsync(T(2));
        // Disconnect result should not be cached (session dies anyway)
        await Assert.That(authCallCount).IsEqualTo(1);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket])]
    public async Task AuthCache_DifferentMethodsHaveIndependentEntries(ClientTarget target)
    {
        var protectedCallCount = 0;
        var cachedCallCount = 0;
        await using var client = await ConnectAuthAsync(
            target,
            nexus =>
            {
                // ProtectedMethod and CachedMethod have different method IDs.
                nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
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
            },
            config => config.AuthorizationCacheDuration = TimeSpan.FromSeconds(30));
        var proxy = AuthProxy(client);

        await proxy.ProtectedMethod("a").Timeout(T(1));
        await proxy.CachedMethod().Timeout(T(1));
        await proxy.ProtectedMethod("b").Timeout(T(1));
        await proxy.CachedMethod().Timeout(T(1));

        // Both methods should have been invoked twice (cache is per-method)
        await Assert.That(protectedCallCount).IsEqualTo(2);
        await Assert.That(cachedCallCount).IsEqualTo(2);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket])]
    public async Task AuthCache_InvalidateAll_ClearsCache(ClientTarget target)
    {
        var authCallCount = 0;
        AuthServerNexus? serverNexus = null;
        await using var client = await ConnectAuthAsync(target, nexus =>
        {
            serverNexus ??= nexus;
            nexus.OnAuthorizeHandler = (_, _, _, _) =>
            {
                Interlocked.Increment(ref authCallCount);
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            };
            nexus.CachedMethodHandler = _ => ValueTask.CompletedTask;
        });
        var proxy = AuthProxy(client);

        await proxy.CachedMethod().Timeout(T(1));
        await Assert.That(authCallCount).IsEqualTo(1);

        // Invalidate cache
        serverNexus!.CallInvalidateAll();

        await proxy.CachedMethod().Timeout(T(1));
        await Assert.That(authCallCount).IsEqualTo(2);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket])]
    public async Task AuthCache_InvalidateByMethodId_ClearsSingleEntry(ClientTarget target)
    {
        var authCallCount = 0;
        var cachedMethodId = -1;
        AuthServerNexus? serverNexus = null;
        await using var client = await ConnectAuthAsync(
            target,
            nexus =>
            {
                serverNexus ??= nexus;
                nexus.OnAuthorizeHandler = (_, methodId, name, _) =>
                {
                    Interlocked.Increment(ref authCallCount);
                    if (name == "CachedMethod")
                        cachedMethodId = methodId;
                    return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
                };
                nexus.CachedMethodHandler = _ => ValueTask.CompletedTask;
                nexus.ProtectedMethodHandler = (_, _) => ValueTask.CompletedTask;
            },
            config => config.AuthorizationCacheDuration = TimeSpan.FromSeconds(30));
        var proxy = AuthProxy(client);

        // Prime both caches
        await proxy.CachedMethod().Timeout(T(1));
        await proxy.ProtectedMethod("a").Timeout(T(1));
        await Assert.That(authCallCount).IsEqualTo(2);

        // Invalidate only CachedMethod
        serverNexus!.CallInvalidateMethod(cachedMethodId);

        // CachedMethod should re-check, ProtectedMethod should still be cached
        await proxy.CachedMethod().Timeout(T(1));
        await proxy.ProtectedMethod("b").Timeout(T(1));
        await Assert.That(authCallCount).IsEqualTo(3); // Only CachedMethod re-checked
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket])]
    public async Task AuthCache_ExceptionNotCached_NextCallReInvokes(ClientTarget target)
    {
        var authCallCount = 0;
        await using var client = await ConnectAuthAsync(target, nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) =>
            {
                Interlocked.Increment(ref authCallCount);
                throw new InvalidOperationException("Auth error");
            };
            nexus.CachedMethodHandler = _ => ValueTask.CompletedTask;
        });

        try { await AuthProxy(client).CachedMethod().Timeout(T(1)); } catch { }

        // Session disconnected due to exception — auth was called once
        await client.WhenDisconnectedAsync(T(2));
        await Assert.That(authCallCount).IsEqualTo(1);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket])]
    public async Task AuthCache_ConcurrentAccess_NoExceptions(ClientTarget target)
    {
        var authCallCount = 0;
        using var allThreadsReady = new ManualResetEventSlim(false);
        await using var client = await ConnectAuthAsync(target, nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) =>
            {
                Interlocked.Increment(ref authCallCount);
                // Force all threads to pile up inside OnAuthorize simultaneously
                allThreadsReady.Wait(TimeSpan.FromSeconds(2));
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            };
            nexus.CachedMethodHandler = _ => ValueTask.CompletedTask;
        });
        var proxy = AuthProxy(client);

        const int concurrentCalls = 10;
        var tasks = new Task[concurrentCalls];
        for (var i = 0; i < concurrentCalls; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                try { await proxy.CachedMethod().Timeout(T(5)); } catch { }
            });
        }

        // Let all threads proceed once they've piled up inside OnAuthorize (bounded: the invocation pool may run
        // fewer at once).
        await WaitUntil(() => Volatile.Read(ref authCallCount) >= concurrentCalls);
        allThreadsReady.Set();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(T(5)));

        // All calls should have completed without exceptions.
        // Auth may be called multiple times (cache misses race) but never throws.
        await Assert.That(authCallCount).IsGreaterThan(0);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task AuthorizedPipeMethod_Unauthorized_RefusesPipe(ClientTarget target)
    {
        var methodInvoked = false;
        await using var client = await ConnectAuthPipeAsync(target, nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Unauthorized);
            nexus.ProtectedPipeHandler = (_, _) =>
            {
                methodInvoked = true;
                return ValueTask.CompletedTask;
            };
        });
        var proxy = PipeProxy(client);

        // Twice: the refused pipe's ID is freed and the second open fails the same way.
        for (var i = 0; i < 2; i++)
        {
            await using var pipe = await client.CreatePipeAsync();
            await proxy.ProtectedPipe("test", pipe).Timeout(T(1));
            await AssertThrows<NexusBusyException>(pipe.WhenReadyAsync().AsTask().Timeout(T(1)));
        }

        // The session stays up and usable.
        await Assert.That(await proxy.Ping(5).Timeout(T(1))).IsEqualTo(5);
        await client.WaitForStateAsync(ConnectionState.Connected, T(1));
        await Assert.That(methodInvoked).IsFalse();
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task AuthorizedChannelMethod_Unauthorized_RefusesChannel(ClientTarget target)
    {
        var methodInvoked = false;
        await using var client = await ConnectAuthPipeAsync(target, nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Unauthorized);
            nexus.ProtectedChannelHandler = (_, _, _) =>
            {
                methodInvoked = true;
                return ValueTask.CompletedTask;
            };
        });
        var proxy = PipeProxy(client);

        await using (var channel = await client.CreatePipeAsync())
        {
            await proxy.ProtectedChannel(42, "test", channel).Timeout(T(1));
            await AssertThrows<NexusBusyException>(channel.WhenReadyAsync().AsTask().Timeout(T(1)));
        }

        await Assert.That(await proxy.Ping(7).Timeout(T(1))).IsEqualTo(7);
        await client.WaitForStateAsync(ConnectionState.Connected, T(1));
        await Assert.That(methodInvoked).IsFalse();
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task AuthorizedPipeMethod_Allowed_PipeBecomesReady(ClientTarget target)
    {
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = await ConnectAuthPipeAsync(target, nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
            nexus.ProtectedPipeHandler = async (input, pipe) =>
            {
                received.TrySetResult(input);
                await pipe.CompleteTask;
            };
        });

        await using (var pipe = await client.CreatePipeAsync())
        {
            await PipeProxy(client).ProtectedPipe("test", pipe).Timeout(T(1));
            await pipe.WhenReadyAsync().AsTask().Timeout(T(1));
            await Assert.That(await received.Task.Timeout(T(1))).IsEqualTo("test");
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task AuthorizedPipeMethod_Disconnect_DisconnectsSession(ClientTarget target)
    {
        await using var client = await ConnectAuthPipeAsync(target, nexus =>
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(AuthorizeResult.Disconnect));

        await using (var pipe = await client.CreatePipeAsync())
        {
            try
            {
                await PipeProxy(client).ProtectedPipe("test", pipe).Timeout(T(1));
            }
            catch
            {
                // The session may already be closing.
            }

            await client.WhenDisconnectedAsync(T(2));
            await client.WaitForStateAsync(ConnectionState.Disconnected, T(1));
        }

        await EndAsync(client);
    }

    // --- NexNet greeting token (Authenticate): the canonical authentication of every client (D15) ---

    /// <summary>
    /// Starts an <see cref="InteropServerNexus"/> server that requires authentication and takes the greeting token as
    /// the identity name (<see cref="InteropServerNexus.ReaderIdentity"/> may read, <see cref="InteropServerNexus.AdminIdentity"/>
    /// may do everything); an empty or missing token is refused. Returns the spec of <paramref name="target"/>'s client.
    /// </summary>
    private async Task<ClientSpec> StartGreetingTokenServerAsync(ClientTarget target, string? token)
    {
        var type = ToType(target.Transport);
        var serverConfig = CreateServerConfig(type);
        serverConfig.Authenticate = true;
        var server = CreateServer<InteropServerNexus, InteropServerNexus.ClientProxy>(serverConfig, nexus =>
            nexus.TokenValidator = (received, _) =>
            {
                var name = received is { Length: > 0 } value ? Encoding.UTF8.GetString(value.Span) : null;
                return ValueTask.FromResult<IIdentity?>(name == null ? null : new DefaultIdentity { DisplayName = name });
            });
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        var spec = Spec(CreateClientConfig(type), target.Transport, Logger);
        spec.AuthToken = token;
        return spec;
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task GreetingToken_IdentityReachesServerAndAuthorizes(ClientTarget target)
    {
        var spec = await StartGreetingTokenServerAsync(target, InteropServerNexus.ReaderIdentity);
        await using var client = await ConnectAsync<InteropClientNexus, InteropClientNexus.ServerProxy>(target, spec);
        var proxy = client.Proxy<IInteropServerNexus>();

        await Assert.That(await proxy.GetIdentity().AsTask().Timeout(T(1))).IsEqualTo(InteropServerNexus.ReaderIdentity);
        await Assert.That(await proxy.ReadSecret().AsTask().Timeout(T(1))).IsEqualTo(InteropServerNexus.Secret);
        await Assert.ThrowsExactlyAsync<ProxyUnauthorizedException>(async () => await proxy.AdminEcho(1).AsTask().Timeout(T(1)));
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task GreetingToken_Missing_ConnectFailsWithAuthenticationError(ClientTarget target)
    {
        var spec = await StartGreetingTokenServerAsync(target, token: null);
        await using var client = await Harness.CreateClientAsync(target, spec);

        var exception = await Assert.ThrowsExactlyAsync<TransportException>(() => client.ConnectAsync().Timeout(T(TestTimeouts.Connect)));
        await Assert.That(exception!.Error).IsEqualTo(TransportError.AuthenticationError);
        await EndAsync(client);
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
