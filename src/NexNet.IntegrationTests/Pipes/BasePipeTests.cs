using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Logging;

namespace NexNet.IntegrationTests.Pipes;

internal class BasePipeTests : BaseTests
{

    public enum LogMode
    {
        None,
        OnTestFail,
        Always
    }
    protected byte[] Data = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };

    protected async Task<(
        NexusServerFactory<ServerNexus, ServerNexus.ClientProxy> server,
        NexusClient<ClientNexus, ClientNexus.ServerProxy> client,
        ClientNexus clientNexus,
        TaskCompletionSource tcs
        )> Setup(Type type, LogMode log = LogMode.OnTestFail)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (server, client, cNexus) = CreateServerClient(
            CreateServerConfig(type, log),
            CreateClientConfig(type, log));
        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        return (server, client, cNexus, tcs);
    }
    
    protected async Task<(
        NexusServerFactory<ServerNexus, ServerNexus.ClientProxy> server,
        NexusClient<ClientNexus, ClientNexus.ServerProxy> client,
        ClientNexus clientNexus,
        TaskCompletionSource tcs
        )> Setup(Type type, INexusLogger logger)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        INexusLogger clientLogger;
        INexusLogger serverLogger;
        if (logger is CoreLogger coreLogger)
        {
            clientLogger = coreLogger.CreatePrefixedLogger(null,"CL");
            serverLogger = coreLogger.CreatePrefixedLogger(null,"SV");
        }
        else
        {
            clientLogger = logger.CreateLogger(null);
            serverLogger = logger.CreateLogger(null);
        }
        
        var (server, client, cNexus) = CreateServerClient(
            CreateServerConfigWithLog(type, serverLogger),
            CreateClientConfigWithLog(type, clientLogger));
        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        return (server, client, cNexus, tcs);
    }
}
