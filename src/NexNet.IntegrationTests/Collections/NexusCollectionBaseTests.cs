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

}
