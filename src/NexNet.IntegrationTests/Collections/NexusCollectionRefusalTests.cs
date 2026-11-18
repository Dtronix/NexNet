using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Invocation;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Collections;

/// <summary>
/// A refused collection connect releases everything it took: the pipe ID, the pipe slot and its window budget.
/// </summary>
internal class NexusCollectionRefusalTests : BaseTests
{
    private const int ClientMaxPipes = 2;

    private async Task<(NexusClient<AuthClientNexus, AuthClientNexus.ServerProxy> client, Func<bool> setAuthorized)>
        StartAuthServerClient(Type type)
    {
        var authorized = false;
        var server = CreateServer<AuthServerNexus, AuthServerNexus.ClientProxy>(CreateServerConfig(type), nexus =>
        {
            nexus.OnAuthorizeHandler = (_, _, _, _) => new ValueTask<AuthorizeResult>(
                Volatile.Read(ref authorized) ? AuthorizeResult.Allowed : AuthorizeResult.Unauthorized);
        });

        var clientConfig = CreateClientConfig(type);
        clientConfig.MaxPipes = ClientMaxPipes;
        var (client, _) = CreateClient<AuthClientNexus, AuthClientNexus.ServerProxy>(clientConfig);

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        return (client, () =>
        {
            Volatile.Write(ref authorized, true);
            return true;
        });
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task RefusedConnects_DoNotLeakPipes(Type type)
    {
        var (client, _) = await StartAuthServerClient(type);

        // More refusals than the client has pipe slots: each one must give its slot back.
        for (var i = 0; i < ClientMaxPipes * 3; i++)
        {
            await AssertThrows<NexusBusyException>(() => client.Proxy.ProtectedList.EnableAsync().AsTask().Timeout(1));
        }

        // An authorized collection still opens.
        Assert.That(await client.Proxy.UnprotectedList.EnableAsync().Timeout(1), Is.True);
        await client.Proxy.UnprotectedList.ReadyTask.Timeout(1);
        Assert.That(client.State, Is.EqualTo(ConnectionState.Connected));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.WebSocket)]
    public async Task RefusedConnect_RetrySucceedsOnceAuthorized(Type type)
    {
        var (client, setAuthorized) = await StartAuthServerClient(type);

        await AssertThrows<NexusBusyException>(() => client.Proxy.ProtectedList.EnableAsync().AsTask().Timeout(1));

        // A retry is a fresh attempt: refused again while unauthorized.
        await AssertThrows<NexusBusyException>(() => client.Proxy.ProtectedList.EnableAsync().AsTask().Timeout(1));

        setAuthorized();
        Assert.That(await client.Proxy.ProtectedList.EnableAsync().Timeout(1), Is.True);
        await client.Proxy.ProtectedList.ReadyTask.Timeout(1);

        await client.Proxy.ProtectedList.DisableAsync().Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.WebSocket)]
    public async Task EnableDisableCycles_DoNotLeakPipes(Type type)
    {
        var (client, _) = await StartAuthServerClient(type);

        // More cycles than the client has pipe slots: a disabled collection gives its slot back.
        for (var i = 0; i < ClientMaxPipes * 3; i++)
        {
            Assert.That(await client.Proxy.UnprotectedList.EnableAsync().Timeout(1), Is.True);
            await client.Proxy.UnprotectedList.DisableAsync().Timeout(1);
        }

        Assert.That(await client.Proxy.UnprotectedList.EnableAsync().Timeout(1), Is.True);
    }
}
