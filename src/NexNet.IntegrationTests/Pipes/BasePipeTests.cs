﻿using NexNet.IntegrationTests.TestInterfaces;

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
        NexusServer<ServerNexus,
            ServerNexus.ClientProxy> server,
        ServerNexus serverNexus,
        NexusClient<ClientNexus, ClientNexus.ServerProxy> client,
        ClientNexus clientNexus,
        TaskCompletionSource tcs
        )> Setup(Type type, LogMode log = LogMode.None)
    {
        var tcs = new TaskCompletionSource();

        var (server, sNexus, client, cNexus) = CreateServerClient(
            CreateServerConfig(type, log),
            CreateClientConfig(type, log));
        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        return (server, sNexus, client, cNexus, tcs);
    }
}
