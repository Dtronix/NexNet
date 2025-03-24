using System.Net.Quic;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Transports;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexusServerTests : BaseTests
{

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task AcceptsClientConnection(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverConfig = CreateServerConfig(type);
        var (server, serverNexus, client, clientNexus) = CreateServerClient(
            serverConfig,
            CreateClientConfig(type));

        serverConfig.InternalOnConnect = () => tcs.SetResult();

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task NexusFiresOnConnected(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, serverNexus, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        serverNexus.OnConnectedEvent = nexus =>
        {
            tcs.SetResult();
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);

        await client.ConnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }



    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    //[TestCase(Type.WebSocket)] Can't start and stop Asp.
    //[TestCase(Type.HttpSocket)] Can't start and stop Asp.
    public async Task StartsAndStopsMultipleTimes(Type type)
    {

        var clientConfig = CreateClientConfig(type);
        var (server, _, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            clientConfig);


        for (int i = 0; i < 5; i++)
        {
            await server.StartAsync().Timeout(1);

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
    //[TestCase(Type.WebSocket)] Can't start and stop Asp.
    //[TestCase(Type.HttpSocket)] Can't start and stop Asp.
    public async Task StopsAndReleasesStoppedTcs(Type type)
    {
        var (server, _, client, clientHub) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));


        Assert.That(server.StoppedTask, Is.Null);
        await server.StartAsync().Timeout(1);
        Assert.That(server.StoppedTask!.IsCompleted, Is.False);

        await server.StopAsync();

        await server.StoppedTask!.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    //[TestCase(Type.WebSocket)] Can't start and stop Asp.
    //[TestCase(Type.HttpSocket)] Can't start and stop Asp.
    public async Task ThrowsWhenServerIsAlreadyOpenOnSameTransport(Type type)
    {
        var config = CreateServerConfig(type);

        var server1 = this.CreateServer(config, null);
        var server2 = this.CreateServer(config, null);

        await server1.StartAsync();

        try
        {
            await server2.StartAsync();
        }
        catch (TransportException e)
        {
            // Quic does not return information if a UDP port is already in use or not.
            Assert.That(e.Error, Is.EqualTo(TransportError.AddressInUse));
        }
        catch (Exception e)
        {
            Assert.Fail($"Expected {nameof(TransportException)} but got {e.GetType().Name}");
        }

    }
}
