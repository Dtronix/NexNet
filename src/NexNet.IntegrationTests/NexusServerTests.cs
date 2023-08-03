using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexusServerTests : BaseTests
{

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task AcceptsClientConnection(Type type)
    {
        var tcs = new TaskCompletionSource();
        var serverConfig = CreateServerConfig(type, false);
        var (server, serverNexus, client, clientNexus) = CreateServerClient(
            serverConfig,
            CreateClientConfig(type, false));

        serverConfig.InternalOnConnect = () => tcs.SetResult();

        server.Start();
        await client.ConnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task NexusFiresOnConnected(Type type)
    {
        var tcs = new TaskCompletionSource();
        var (server, serverNexus, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type, false),
            CreateClientConfig(type, false));

        serverNexus.OnConnectedEvent = nexus =>
        {
            tcs.SetResult();
            return ValueTask.CompletedTask;
        };

        server.Start();

        await client.ConnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }



    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task StartsAndStopsMultipleTimes(Type type)
    {

        var clientConfig = CreateClientConfig(type, false);
        var (server, _, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type, false),
            clientConfig);


        for (int i = 0; i < 3; i++)
        {
            server.Start();
            await client.ConnectAsync().Timeout(1);

            await client.ReadyTask.Timeout(1);

            server.Stop();

            await client.DisconnectedTask.Timeout(1);

            // Wait for the client to process the disconnect.

        }
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task StopsAndReleasesStoppedTcs(Type type)
    {
        var (server, _, client, clientHub) = CreateServerClient(
            CreateServerConfig(type, false),
            CreateClientConfig(type, false));

        
        Assert.IsNull(server.StoppedTask);
        server.Start();
        Assert.IsFalse(server.StoppedTask!.IsCompleted);

        server.Stop();

        await server.StoppedTask!.Timeout(1);
    }
}
