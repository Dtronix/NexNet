using NexNet.Collections.Lists;
using NexNet.IntegrationTests.Pipes;
using NexNet.IntegrationTests.TestInterfaces;

namespace NexNet.IntegrationTests.Collections;

internal class NexusCollectionBaseTests : BaseTests
{
    protected async Task<(NexusServerFactory<ServerNexus, ServerNexus.ClientProxy> server,
        NexusClient<ClientNexus, ClientNexus.ServerProxy> client,
        ClientNexus clientNexus)> ConnectServerAndClient(Type type, BasePipeTests.LogMode logMode = BasePipeTests.LogMode.OnTestFail)
    {
        var (server, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type, logMode),
            CreateClientConfig(type, logMode));
        
        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        return (server, client, clientNexus);
    }

    protected record RelayedCollectionServerClient(
        NexusServer<ServerNexus, ServerNexus.ClientProxy> Server1,
        NexusClient<ClientNexus, ClientNexus.ServerProxy> Client1,
        NexusServer<ServerNexus, ServerNexus.ClientProxy> Server2,
        NexusClient<ClientNexus, ClientNexus.ServerProxy> Client2);
    
    protected async ValueTask<RelayedCollectionServerClient> CreateRelayCollectionClientServers(bool startServers = false)
    {
        CurrentUdsPath = null;
        var serverConfig1 = CreateServerConfig(Type.Uds);
        var server1 = CreateServer(serverConfig1, nexus => { });
        var clientConfig1 = CreateClientConfig(Type.Uds);
        var client1 = CreateClient(clientConfig1);
        
        var clientPool =
            new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(new NexusClientPoolConfig(clientConfig1));
        
        // Reset the port to get a new port.
        CurrentUdsPath = null;
        var serverConfig2 = CreateServerConfig(Type.Uds);
        var server2 = CreateServer(serverConfig2, nexus => { }, configureCollections: nexus =>
            nexus.IntListRelay.ConfigureRelay(clientPool.GetCollectionConnector(n => n.IntListBi)));
        var clientConfig2 = CreateClientConfig(Type.Uds);
        var client2 = CreateClient(clientConfig2);

        if (startServers)
        {
            await server1.StartAsync().Timeout(1);
            await server2.StartAsync().Timeout(1);
        }
        return new RelayedCollectionServerClient(server1, client1.client, server2, client2.client);
    }
    
    protected record RelayCollectionClientServers(
        NexusServer<ServerNexus, ServerNexus.ClientProxy> Parent, 
        NexusServer<ServerNexus, ServerNexus.ClientProxy> Child1, 
        NexusServer<ServerNexus, ServerNexus.ClientProxy> Child2, 
        INexusList<int> SourceList, 
        INexusList<int> Child1Relay,
        INexusList<int> Child2Relay);
    
    protected async ValueTask<RelayCollectionClientServers> CreateRelayCollectionServers()
    {
        var parentConfig = CreateServerConfig(Type.Uds);
        var parent = CreateServer(parentConfig, nexus => { });
        var parentClientConfig = CreateClientConfig(Type.Uds);
        
        var clientPool =
            new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(new NexusClientPoolConfig(parentClientConfig));
        
        CurrentUdsPath = null;
        var serverConfig2 = CreateServerConfig(Type.Uds);
        var child1 = CreateServer(serverConfig2, nexus => { }, configureCollections: nexus =>
            nexus.IntListRelay.ConfigureRelay(clientPool.GetCollectionConnector(n => n.IntListBi)));
        
        CurrentUdsPath = null;
        var serverConfig3 = CreateServerConfig(Type.Uds);
        var child2 = CreateServer(serverConfig3, nexus => { }, configureCollections: nexus =>
            nexus.IntListRelay.ConfigureRelay(clientPool.GetCollectionConnector(n => n.IntListBi)));
        
        await parent.StartAsync().Timeout(1);
        await child1.StartAsync().Timeout(1);
        await child2.StartAsync().Timeout(1);
        
        var parentRelay = parent.ContextProvider.Rent().Collections.IntListBi;
        var child1Relay = child1.ContextProvider.Rent().Collections.IntListRelay;
        var child2Relay = child2.ContextProvider.Rent().Collections.IntListRelay;
        return new (parent, child1, child2, parentRelay, child1Relay, child2Relay);
    }

}
