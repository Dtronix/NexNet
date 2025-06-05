using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NexNet.Transports;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace NexNet.IntegrationTests.Collections.Lists;

internal class NexusListTests : BaseTests
{
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientCanAdd(Type type)
    {
        var (server, serverNexus, client, clientNexus) = await ConnectServerAndClient(type);

        await client.Proxy.IntListBi.ConnectAsync();
        await client.Proxy.IntListBi.AddAsync(1);
        await client.Proxy.IntListBi.AddAsync(2);
        await client.Proxy.IntListBi.AddAsync(3);
        Assert.Multiple(() =>
        {
            Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(serverNexus.IntListBi));
            Assert.That(serverNexus.IntListBi, Is.EquivalentTo([1, 2, 3]));
        });
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientCanInsert(Type type)
    {
        var (server, serverNexus, client, clientNexus) = await ConnectServerAndClient(type);

        await client.Proxy.IntListBi.ConnectAsync();
        await client.Proxy.IntListBi.InsertAsync(0, 2);
        await client.Proxy.IntListBi.InsertAsync(1, 3);
        await client.Proxy.IntListBi.InsertAsync(0, 1);
        Assert.Multiple(() =>
        {
            Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(serverNexus.IntListBi));
            Assert.That(serverNexus.IntListBi, Is.EquivalentTo([1, 2, 3]));
        });
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientCanRemoveAt(Type type)
    {
        var (server, serverNexus, client, clientNexus) = await ConnectServerAndClient(type);

        await client.Proxy.IntListBi.ConnectAsync();
        await client.Proxy.IntListBi.AddAsync(0);
        await client.Proxy.IntListBi.AddAsync(111);
        await client.Proxy.IntListBi.AddAsync(2);
        await client.Proxy.IntListBi.RemoveAtAsync(1);
        Assert.Multiple(() =>
        {
            Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(serverNexus.IntListBi));
            Assert.That(serverNexus.IntListBi, Is.EquivalentTo([0, 2]));
        });
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientCanRemove(Type type)
    {
        var (server, serverNexus, client, clientNexus) = await ConnectServerAndClient(type);

        await client.Proxy.IntListBi.ConnectAsync();
        await client.Proxy.IntListBi.AddAsync(0);
        await client.Proxy.IntListBi.AddAsync(111);
        await client.Proxy.IntListBi.AddAsync(2);
        await client.Proxy.IntListBi.RemoveAsync(111);
        Assert.Multiple(() =>
        {
            Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(serverNexus.IntListBi));
            Assert.That(serverNexus.IntListBi, Is.EquivalentTo([0, 2]));
        });
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ServerSendsInitialValues(Type type)
    {
        var (server, serverNexus, client, clientNexus) = await ConnectServerAndClient(type);

        await serverNexus.IntListBi.AddAsync(1);
        await serverNexus.IntListBi.AddAsync(2);
        await serverNexus.IntListBi.AddAsync(3);

        await client.Proxy.IntListBi.ConnectAsync();
        Assert.Multiple(() =>
        {
            Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(serverNexus.IntListBi));
            Assert.That(serverNexus.IntListBi, Is.EquivalentTo([1, 2, 3]));
        });
    }

    private async Task<(NexusServer<ServerNexus, ServerNexus.ClientProxy> server,
        ServerNexus serverNexus,
        NexusClient<ClientNexus, ClientNexus.ServerProxy> client,
        ClientNexus clientNexus)> ConnectServerAndClient(Type type)
    {
        var (server, serverNexus, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));
        
        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        return (server, serverNexus, client, clientNexus);
    }
}
