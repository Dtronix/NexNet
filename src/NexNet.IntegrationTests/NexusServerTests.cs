using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexusServerTests : BaseTests
{

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task AcceptsClientConnection(Type type)
    {
        var tcs = new TaskCompletionSource();
        var serverConfig = CreateServerConfig(type);
        var (server, serverNexus, client, clientNexus) = CreateServerClient(
            serverConfig,
            CreateClientConfig(type));

        serverConfig.InternalOnConnect = () => tcs.SetResult();

        await server.StartAsync();
        await client.ConnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task NexusFiresOnConnected(Type type)
    {
        var tcs = new TaskCompletionSource();
        var (server, serverNexus, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        serverNexus.OnConnectedEvent = nexus =>
        {
            tcs.SetResult();
            return ValueTask.CompletedTask;
        };

        await server.StartAsync();

        await client.ConnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }



    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task StartsAndStopsMultipleTimes(Type type)
    {

        var clientConfig = CreateClientConfig(type, false);
        var (server, _, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            clientConfig);


        for (int i = 0; i < 5; i++)
        {
            await server.StartAsync();

            await client.ConnectAsync().Timeout(1);

            await server.StopAsync();

            await client.DisconnectedTask.Timeout(2);

            // Wait for the client to process the disconnect.

        }
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task StopsAndReleasesStoppedTcs(Type type)
    {
        var (server, _, client, clientHub) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        
        Assert.IsNull(server.StoppedTask);
        await server.StartAsync();
        Assert.IsFalse(server.StoppedTask!.IsCompleted);

        await server.StopAsync();

        await server.StoppedTask!.Timeout(1);
    }
}
