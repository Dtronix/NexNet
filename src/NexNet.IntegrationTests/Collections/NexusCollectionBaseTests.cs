using NexNet.IntegrationTests.Pipes;
using NexNet.IntegrationTests.TestInterfaces;

namespace NexNet.IntegrationTests.Collections;

internal class NexusCollectionBaseTests : BaseTests
{
    protected async Task<(NexusServer<ServerNexus, ServerNexus.ClientProxy> server,
        ServerNexus serverNexus,
        NexusClient<ClientNexus, ClientNexus.ServerProxy> client,
        ClientNexus clientNexus)> ConnectServerAndClient(Type type, BasePipeTests.LogMode logMode = BasePipeTests.LogMode.OnTestFail)
    {
        var (server, serverNexus, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type, logMode),
            CreateClientConfig(type, logMode));
        
        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        return (server, serverNexus, client, clientNexus);
    }

}
