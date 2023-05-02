using System.Net.Sockets;
using MemoryPack;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NexNet.Transports;
using NUnit.Framework;

namespace NexNet.IntegrationTests;

internal partial class NexNetServerTests : BaseTests
{

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task AcceptsClientConnection(Type type)
    {
        var tcs = new TaskCompletionSource();
        var serverConfig = CreateServerConfig(type, true);
        var (server, serverHub, client, clientHub) = CreateServerClient(
            serverConfig,
            CreateClientConfig(type, true));

        serverConfig.InternalOnConnect = () => tcs.SetResult();

        server.Start();
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task HubFiresOnConnected(Type type)
    {
        var tcs = new TaskCompletionSource();
        var (server, serverHub, client, clientHub) = CreateServerClient(
            CreateServerConfig(type, true),
            CreateClientConfig(type, true));

        serverHub.OnConnectedEvent = async hub => tcs.SetResult();

        server.Start();

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }


    //[Test]
    //[TestCase(Type.Uds)]
    //[TestCase(Type.Tcp)]
    public async Task StartsAndStopsMultipleTimes(Type type)
    {
        var (server, serverHub, client, clientHub) = CreateServerClient(
            CreateServerConfig(type, true),
            CreateClientConfig(type, true));


        for (int i = 0; i < 5; i++)
        {
            server.Start();
            await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

            await clientHub.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));
            clientHub.ConnectedTCS = new TaskCompletionSource();

            server.Stop();

            await Task.Delay(500);

            // Wait for the client to process the disconnect.

        }
    }


}
