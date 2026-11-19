namespace NexNet.IntegrationTests.Collections;

/// <summary>
/// DisableAsync returns once the collection's pipe is freed (S23): with a single pipe slot, an enable right after a
/// disable always finds the slot available.
/// </summary>
internal class NexusCollectionDisableFreesPipeTests : BaseTests
{
    private const int Cycles = 1000;

    [Test]
    [Transports(Type.Uds, Type.Tcp, Type.WebSocket)]
    public async Task DisableAsync_FreesThePipeBeforeReturning(Type type)
    {
        var server = CreateServer(CreateServerConfig(type), null);
        await server.StartAsync().Timeout(TestTimeouts.Connect);

        var clientConfig = CreateClientConfig(type);
        clientConfig.MaxPipes = 1;
        var (client, _) = CreateClient(clientConfig);
        await client.ConnectAsync().Timeout(TestTimeouts.Connect);

        var list = client.Proxy.IntListBi;
        var pipes = client.Session!.PipeManager;

        await Assert.That(await list.EnableAsync().Timeout(5)).IsTrue();
        await list.DisableAsync().Timeout(5);

        await Assert.That(pipes.OpenPipes).IsZero();
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task EnableDisableCycles_WithOnePipeSlot_AllSucceed(Type type)
    {
        var server = CreateServer(CreateServerConfig(type), null);
        await server.StartAsync().Timeout(TestTimeouts.Connect);

        var clientConfig = CreateClientConfig(type);
        clientConfig.MaxPipes = 1;
        var (client, _) = CreateClient(clientConfig);
        await client.ConnectAsync().Timeout(TestTimeouts.Connect);

        var list = client.Proxy.IntListBi;

        for (var i = 0; i < Cycles; i++)
        {
            await Assert.That(await list.EnableAsync().Timeout(5)).IsTrue();
            await list.DisableAsync().Timeout(5);
        }

        await Assert.That(client.Session!.PipeManager.OpenPipes).IsZero();
        await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
    }

    /// <summary>
    /// S25: the server also has a single pipe slot. The client frees its pipe once the server confirmed the close,
    /// which can be before the server's collection handler released the server's side; the next enable must not be
    /// refused for that.
    /// </summary>
    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task EnableDisableCycles_WithOnePipeSlotOnBothSides_AllSucceed(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.MaxPipes = 1;
        var server = CreateServer(serverConfig, null);
        await server.StartAsync().Timeout(TestTimeouts.Connect);

        var clientConfig = CreateClientConfig(type);
        clientConfig.MaxPipes = 1;
        var (client, _) = CreateClient(clientConfig);
        await client.ConnectAsync().Timeout(TestTimeouts.Connect);

        var list = client.Proxy.IntListBi;

        for (var i = 0; i < Cycles; i++)
        {
            await Assert.That(await list.EnableAsync().Timeout(5)).IsTrue();
            await list.DisableAsync().Timeout(5);
        }

        await Assert.That(client.Session!.PipeManager.OpenPipes).IsZero();
        await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
    }
}
