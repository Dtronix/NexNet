using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Transports;

// The C# test scaffolding of every versioned server nexus (one remote proxy per version), for the client matrix.
[assembly: NexNet.Interop.NexusTestScaffolding<NexNet.IntegrationTests.TestInterfaces.IVersionedServerNexusV1, NexNet.IntegrationTests.TestInterfaces.ISimpleClientNexus>]
[assembly: NexNet.Interop.NexusTestScaffolding<NexNet.IntegrationTests.TestInterfaces.IVersionedServerNexusV1_1, NexNet.IntegrationTests.TestInterfaces.ISimpleClientNexus>]
[assembly: NexNet.Interop.NexusTestScaffolding<NexNet.IntegrationTests.TestInterfaces.IVersionedServerNexusV2, NexNet.IntegrationTests.TestInterfaces.ISimpleClientNexus>]

namespace NexNet.IntegrationTests;

/// <summary>
/// Versioned nexuses (<c>[NexusVersion]</c>) on the client matrix (P01d): a client of each server interface version
/// (V1, V1.1, V2) against servers of each version. The client export is chosen by the client nexus type passed to
/// <see cref="Harness.CreateClientAsync{TClientNexus, TServerProxy}"/>.
/// </summary>
/// <remarks>
/// The QUIC rows of the C# suite are C#-only rows (<see cref="ClientMatrixAttribute.CSharpTransports"/>, H16).
/// </remarks>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class NexusServerTests_Versioned(LanguageHostPool hosts) : BaseTests
{
    /// <summary>The language hosts of the session; initializing them registers their drivers with <see cref="Harness"/>.</summary>
    protected LanguageHostPool Hosts { get; } = hosts;

    /// <summary>
    /// Starts a server of <typeparamref name="TServerNexus"/> for <paramref name="target"/>'s transport and creates the
    /// client under test of <typeparamref name="TClientNexus"/>; the client is not connected.
    /// </summary>
    private async Task<(ServerConfig ServerConfig, IClientUnderTest Client)> CreateAsync<TServerNexus, TClientProxy, TClientNexus, TServerProxy>(
        ClientTarget target,
        Action<TServerNexus>? nexusCreated = null)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, NexNet.Collections.ICollectionConfigurer, new()
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
        where TClientNexus : ClientNexusBase<TServerProxy>, IInvocationMethodHash, NexNet.Collections.ICollectionConfigurer, new()
        where TServerProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        var type = AuthTlsMatrix.ToType(target.Transport);
        var serverConfig = CreateServerConfig(type);
        var server = CreateServer<TServerNexus, TClientProxy>(serverConfig, nexusCreated);
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        var client = await Harness.CreateClientAsync<TClientNexus, TServerProxy>(
            target, AuthTlsMatrix.Spec(CreateClientConfig(type), target.Transport, Logger));
        return (serverConfig, client);
    }

    /// <summary>Connects <paramref name="client"/> and waits until the server has the session.</summary>
    private static async Task ConnectAndAssertServerSessionAsync(ServerConfig serverConfig, IClientUnderTest client)
    {
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        serverConfig.InternalOnConnect = () => connected.TrySetResult();

        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await connected.Task.Timeout(T(1));
    }

    /// <summary>Asserts that connecting <paramref name="client"/> fails with <see cref="DisconnectReason.ServerMismatch"/>.</summary>
    private static async Task AssertServerMismatchAsync(IClientUnderTest client)
    {
        await Assert.That(await client.TryConnectAsync().Timeout(T(TestTimeouts.Connect))).IsFalse();
        await Assert.That(await client.WhenDisconnectedAsync(T(1))).IsEqualTo(DisconnectReason.ServerMismatch);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task CurrentServerProxy_ConnectsToServerSuccessfully(ClientTarget target)
    {
        var (serverConfig, created) = await CreateAsync<VersionedServerNexusV2, VersionedServerNexusV2.ClientProxy, VersionedClientNexusV2, VersionedClientNexusV2.ServerProxy>(target);
        await using var client = created;

        await ConnectAndAssertServerSessionAsync(serverConfig, client);
        await AuthTlsMatrix.EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task CurrentServerProxy_InvokesCurrentAndOlderMethodVersions(ClientTarget target)
    {
        var runTaskNewV2 = Signal();
        var runTaskV1_1 = Signal();
        var runTaskWithResultV1_1 = Signal();
        var verifyVersionV1 = Signal();

        var (_, created) = await CreateAsync<VersionedServerNexusV2, VersionedServerNexusV2.ClientProxy, VersionedClientNexusV2, VersionedClientNexusV2.ServerProxy>(target, n =>
        {
            n.RunTaskNewV2Action = () =>
            {
                runTaskNewV2.TrySetResult();
                return ValueTask.CompletedTask;
            };
            n.RunTaskV1_1Action = () => runTaskV1_1.TrySetResult();
            n.RunTaskWithResultV1_1Action = () =>
            {
                runTaskWithResultV1_1.TrySetResult();
                return default;
            };
            n.VerifyVersionV1Action = _ =>
            {
                verifyVersionV1.TrySetResult();
                return new ValueTask<bool>(true);
            };
        });
        await using var client = created;
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        var proxy = client.Proxy<Remote_IVersionedServerNexusV2>();

        await proxy.RunTaskNewV2().Timeout(T(1));
        proxy.RunTaskV1_1();
        await proxy.RunTaskWithResultV1_1().Timeout(T(1));
        await proxy.VerifyVersionV1("").Timeout(T(1));

        await runTaskNewV2.Task.Timeout(T(1));
        await runTaskV1_1.Task.Timeout(T(1));
        await runTaskWithResultV1_1.Task.Timeout(T(1));
        await verifyVersionV1.Task.Timeout(T(1));
        await AuthTlsMatrix.EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task CurrentServerProxy_ConnectToOlderServerFails(ClientTarget target)
    {
        var (_, created) = await CreateAsync<VersionedServerNexusV1_1, VersionedServerNexusV1_1.ClientProxy, VersionedClientNexusV2, VersionedClientNexusV2.ServerProxy>(target);
        await using var client = created;

        await AssertServerMismatchAsync(client);
        await AuthTlsMatrix.EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task OlderServerProxy_ConnectsToServerSuccessfully(ClientTarget target)
    {
        var (serverConfig, created) = await CreateAsync<VersionedServerNexusV2, VersionedServerNexusV2.ClientProxy, VersionedClientNexusV1_1, VersionedClientNexusV1_1.ServerProxy>(target);
        await using var client = created;

        await ConnectAndAssertServerSessionAsync(serverConfig, client);
        await AuthTlsMatrix.EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task OlderServerProxy_InvokesCurrentAndOlderMethodVersions(ClientTarget target)
    {
        var runTaskV1_1 = Signal();
        var runTaskWithResultV1_1 = Signal();
        var verifyVersionV1 = Signal();

        var (_, created) = await CreateAsync<VersionedServerNexusV2, VersionedServerNexusV2.ClientProxy, VersionedClientNexusV1_1, VersionedClientNexusV1_1.ServerProxy>(target, n =>
        {
            n.RunTaskV1_1Action = () => runTaskV1_1.TrySetResult();
            n.RunTaskWithResultV1_1Action = () =>
            {
                runTaskWithResultV1_1.TrySetResult();
                return default;
            };
            n.VerifyVersionV1Action = _ =>
            {
                verifyVersionV1.TrySetResult();
                return new ValueTask<bool>(true);
            };
        });
        await using var client = created;
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        var proxy = client.Proxy<Remote_IVersionedServerNexusV1_1>();

        proxy.RunTaskV1_1();
        await proxy.RunTaskWithResultV1_1().Timeout(T(1));
        await proxy.VerifyVersionV1("").Timeout(T(1));

        await runTaskV1_1.Task.Timeout(T(1));
        await runTaskWithResultV1_1.Task.Timeout(T(1));
        await verifyVersionV1.Task.Timeout(T(1));
        await AuthTlsMatrix.EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task OlderServerProxy_ConnectToOlderServerSucceeds(ClientTarget target)
    {
        var (_, created) = await CreateAsync<VersionedServerNexusV1_1, VersionedServerNexusV1_1.ClientProxy, VersionedClientNexusV1_1, VersionedClientNexusV1_1.ServerProxy>(target);
        await using var client = created;

        await Assert.That(await client.TryConnectAsync().Timeout(T(TestTimeouts.Connect))).IsTrue();
        await AuthTlsMatrix.EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task OlderServerProxy_ConnectToOldestServerFails(ClientTarget target)
    {
        var (_, created) = await CreateAsync<VersionedServerNexusV1, VersionedServerNexusV1.ClientProxy, VersionedClientNexusV1_1, VersionedClientNexusV1_1.ServerProxy>(target);
        await using var client = created;

        await AssertServerMismatchAsync(client);
        await AuthTlsMatrix.EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task OldestServerProxy_ConnectsToServerSuccessfully(ClientTarget target)
    {
        var (serverConfig, created) = await CreateAsync<VersionedServerNexusV2, VersionedServerNexusV2.ClientProxy, VersionedClientNexusV1, VersionedClientNexusV1.ServerProxy>(target);
        await using var client = created;

        await ConnectAndAssertServerSessionAsync(serverConfig, client);
        await AuthTlsMatrix.EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task OldestServerProxy_InvokesCurrentAndOlderMethodVersions(ClientTarget target)
    {
        var verifyVersionV1 = Signal();

        var (_, created) = await CreateAsync<VersionedServerNexusV2, VersionedServerNexusV2.ClientProxy, VersionedClientNexusV1, VersionedClientNexusV1.ServerProxy>(target, n =>
            n.VerifyVersionV1Action = _ =>
            {
                verifyVersionV1.TrySetResult();
                return new ValueTask<bool>(true);
            });
        await using var client = created;
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        await client.Proxy<Remote_IVersionedServerNexusV1>().VerifyVersionV1("").Timeout(T(1));

        await verifyVersionV1.Task.Timeout(T(1));
        await AuthTlsMatrix.EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task CurrentServerProxy_ConnectToOldestServerFails(ClientTarget target)
    {
        var (_, created) = await CreateAsync<VersionedServerNexusV1, VersionedServerNexusV1.ClientProxy, VersionedClientNexusV2, VersionedClientNexusV2.ServerProxy>(target);
        await using var client = created;

        await AssertServerMismatchAsync(client);
        await AuthTlsMatrix.EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task OldestServerProxy_ConnectToOldestServerFails(ClientTarget target)
    {
        // Despite its name, the oldest client connects to the oldest server.
        var (_, created) = await CreateAsync<VersionedServerNexusV1, VersionedServerNexusV1.ClientProxy, VersionedClientNexusV1, VersionedClientNexusV1.ServerProxy>(target);
        await using var client = created;

        await Assert.That(await client.TryConnectAsync().Timeout(T(TestTimeouts.Connect))).IsTrue();
        await AuthTlsMatrix.EndAsync(client);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
