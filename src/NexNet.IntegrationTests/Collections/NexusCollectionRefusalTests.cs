using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.Invocation;

namespace NexNet.IntegrationTests.Collections;

/// <summary>
/// A refused collection connect releases everything it took: the pipe ID, the pipe slot and its window budget. On every
/// row of the client matrix.
/// </summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class NexusCollectionRefusalTests(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    /// <summary>
    /// The default <c>MaxPipes</c> of every client (C# <c>ConfigBase.MaxPipes</c>, Java <c>PipeOptions.DEFAULT_MAX_PIPES</c>,
    /// JS <c>maxPipes</c>): more refusals or cycles than this leak no slot only if each one gives its slot back.
    /// </summary>
    private const int DefaultMaxPipes = 32;

    /// <summary>
    /// Starts an <see cref="AuthServerNexus"/> server whose <c>ProtectedList</c> is refused until the returned action
    /// authorizes it, then connects the client of <paramref name="target"/>.
    /// </summary>
    private async Task<(IClientUnderTest Client, Action Authorize)> ConnectAuthAsync(ClientTarget target)
    {
        var authorized = false;
        var type = ToType(target.Transport);
        var server = CreateServer<AuthServerNexus, AuthServerNexus.ClientProxy>(CreateServerConfig(type), nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(
                Volatile.Read(ref authorized) ? AuthorizeResult.Allowed : AuthorizeResult.Unauthorized);
        });
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        var client = await ConnectAsync<AuthClientNexus, AuthClientNexus.ServerProxy>(
            target, Spec(CreateClientConfig(type), target.Transport, Logger));
        return (client, () => Volatile.Write(ref authorized, true));
    }

    private static Remote_IAuthServerNexus AuthProxy(IClientUnderTest client) => client.Proxy<Remote_IAuthServerNexus>();

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task RefusedConnects_DoNotLeakPipes(ClientTarget target)
    {
        var (client, _) = await ConnectAuthAsync(target);
        await using var owned = client;
        var proxy = AuthProxy(client);

        // More refusals than the client has pipe slots: each one must give its slot back.
        for (var i = 0; i < DefaultMaxPipes * 2; i++)
            await AssertThrows<NexusBusyException>(() => proxy.ProtectedList.EnableAsync().AsTask().Timeout(T(1)));

        // An authorized collection still opens.
        await Assert.That(await proxy.UnprotectedList.EnableAsync().AsTask().Timeout(T(1))).IsTrue();
        await Assert.That(await client.GetStateAsync()).IsEqualTo(ConnectionState.Connected);
        await proxy.UnprotectedList.DisableAsync().AsTask().Timeout(T(1));
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task RefusedConnect_RetrySucceedsOnceAuthorized(ClientTarget target)
    {
        var (client, authorize) = await ConnectAuthAsync(target);
        await using var owned = client;
        var proxy = AuthProxy(client);

        await AssertThrows<NexusBusyException>(() => proxy.ProtectedList.EnableAsync().AsTask().Timeout(T(1)));

        // A retry is a fresh attempt: refused again while unauthorized.
        await AssertThrows<NexusBusyException>(() => proxy.ProtectedList.EnableAsync().AsTask().Timeout(T(1)));

        authorize();
        await Assert.That(await proxy.ProtectedList.EnableAsync().AsTask().Timeout(T(1))).IsTrue();

        await proxy.ProtectedList.DisableAsync().AsTask().Timeout(T(1));
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task EnableDisableCycles_DoNotLeakPipes(ClientTarget target)
    {
        var (client, _) = await ConnectAuthAsync(target);
        await using var owned = client;
        var list = AuthProxy(client).UnprotectedList;

        // More cycles than the client has pipe slots: a disabled collection gives its slot back.
        for (var i = 0; i < DefaultMaxPipes * 2; i++)
        {
            await Assert.That(await list.EnableAsync().AsTask().Timeout(T(1))).IsTrue();
            await list.DisableAsync().AsTask().Timeout(T(1));
        }

        await Assert.That(await list.EnableAsync().AsTask().Timeout(T(1))).IsTrue();
        await list.DisableAsync().AsTask().Timeout(T(1));
        await EndAsync(client);
    }
}
