using NexNet.IntegrationTests.Pipes;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Security;

internal class ServerVersionInvocationValidationTests : BaseTests
{
    [Test]
    public async Task NonVersionedServer_NullClientVersion_ShouldConnect()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        
        await client.SendMessageAsync(new ClientGreetingMessage()
        {
            ServerNexusHash = Invocation.IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = Invocation.IInvocationMethodHash.GetMethodHash<ClientNexus>(),
            Version = null // Null version for non-versioned server
        }).Timeout(1);

        await client.AssertReceiveMessageAsync<ServerGreetingMessage>();
    }

    [Test]
    public async Task VersionedServer_ValidClientVersionCurrent_ShouldConnect()
    {
        // Similar to above - demonstrates the pattern for when we have versioned servers
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer<VersionedServerNexusV2, VersionedServerNexusV2.ClientProxy>(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        var version = "v1.2";
        await client.SendMessageAsync(new ClientGreetingMessage()
        {
            ServerNexusHash = Invocation.IInvocationMethodHash.GetVersionHashTable<VersionedServerNexusV2>()[version],
            ClientNexusHash = Invocation.IInvocationMethodHash.GetMethodHash<VersionedClientNexusV2>(),
            Version = version // Would be a valid version in versioned server
        }).Timeout(1);
        
        await client.AssertReceiveMessageAsync<ServerGreetingMessage>();
    }
    
}
