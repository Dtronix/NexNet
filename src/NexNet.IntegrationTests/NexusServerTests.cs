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
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
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

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }



    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task StartsAndStopsMultipleTimes(Type type)
    {

        var clientConfig = CreateClientConfig(type, false);
        clientConfig.ReconnectionPolicy = null;
        var (server, _, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type, false),
            clientConfig);


        for (int i = 0; i < 3; i++)
        {
            server.Start();
            await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

            await clientNexus.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));
            clientNexus.ConnectedTCS = new TaskCompletionSource();

            server.Stop();

            await clientNexus.DisconnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));
            clientNexus.DisconnectedTCS = new TaskCompletionSource();

            await Task.Delay(100);
            // Wait for the client to process the disconnect.

        }
    }
}
