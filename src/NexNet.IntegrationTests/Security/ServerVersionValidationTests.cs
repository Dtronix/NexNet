using NexNet.IntegrationTests.Pipes;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Security;

internal class ServerVersionValidationTests : BaseTests
{
    /// <summary>
    /// Tests non-versioned server with null client version (success path).
    /// Path: TNexus.VersionHashTable.Count == 0 && requestedVersion == null
    /// Expected: Connection succeeds
    /// </summary>
    [Test]
    public async Task NonVersionedServer_NullClientVersion_ShouldConnect()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        
        // Send ClientGreeting with null version for non-versioned server
        await client.SendMessageAsync(new ClientGreetingMessage()
        {
            ServerNexusHash = Invocation.IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = Invocation.IInvocationMethodHash.GetMethodHash<ClientNexus>(),
            Version = null // Null version for non-versioned server
        }).Timeout(1);

        await client.AssertReceiveMessageAsync<ServerGreetingMessage>();
    }

    /// <summary>
    /// Tests non-versioned server with non-null client version (failure path).
    /// Path: TNexus.VersionHashTable.Count == 0 && requestedVersion != null
    /// Expected: DisconnectReason.ServerMismatch
    /// </summary>
    [Test]
    public async Task NonVersionedServer_NonNullClientVersion_ShouldDisconnectWithServerMismatch()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        
        // Send ClientGreeting with non-null version for non-versioned server
        await client.SendMessageAsync(new ClientGreetingMessage()
        {
            ServerNexusHash = Invocation.IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = Invocation.IInvocationMethodHash.GetMethodHash<ClientNexus>(),
            Version = "v1.0" // Non-null version for non-versioned server
        }).Timeout(1);
        
        // Should disconnect with ServerMismatch
        await client.AssertDisconnectReason(DisconnectReason.ServerMismatch).Timeout(1);
    }

    /// <summary>
    /// Tests versioned server with null client version (failure path).
    /// Path: TNexus.VersionHashTable.Count > 0 && requestedVersion == null
    /// Expected: DisconnectReason.ServerMismatch
    /// </summary>
    [Test]
    public async Task VersionedServer_NullClientVersion_ShouldDisconnectWithServerMismatch()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer<VersionedServerNexus, VersionedServerNexus.ClientProxy>(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        
        // If the server had versions configured, sending null version would fail
        await client.SendMessageAsync(new ClientGreetingMessage()
        {
            ServerNexusHash = Invocation.IInvocationMethodHash.GetMethodHash<VersionedServerNexus>(),
            ClientNexusHash = Invocation.IInvocationMethodHash.GetMethodHash<VersionedClientNexus>(),
            Version = null // Null version for versioned server
        }).Timeout(1);

        await client.AssertDisconnectReason(DisconnectReason.ServerMismatch);
    }

    /// <summary>
    /// Tests versioned server with valid client version (success path).
    /// Path: TNexus.VersionHashTable.Count > 0 && TNexus.VersionHashTable.TryGetValue(requestedVersion, out verificationHash) == true
    /// Expected: Connection succeeds if hash matches
    /// </summary>
    [Test]
    public async Task VersionedServer_ValidClientVersionCurrent_ShouldConnect()
    {
        // Similar to above - demonstrates the pattern for when we have versioned servers
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer<VersionedServerNexus, VersionedServerNexus.ClientProxy>(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        var version = "v1.1";
        await client.SendMessageAsync(new ClientGreetingMessage()
        {
            ServerNexusHash = Invocation.IInvocationMethodHash.GetVersionHashTable<VersionedServerNexus>()[version],
            ClientNexusHash = Invocation.IInvocationMethodHash.GetMethodHash<VersionedClientNexus>(),
            Version = version // Would be a valid version in versioned server
        }).Timeout(1);
        
        await client.AssertReceiveMessageAsync<ServerGreetingMessage>();
    }
    
    /// <summary>
    /// Tests versioned server with valid client version (success path).
    /// Path: TNexus.VersionHashTable.Count > 0 && TNexus.VersionHashTable.TryGetValue(requestedVersion, out verificationHash) == true
    /// Expected: Connection succeeds if hash matches
    /// </summary>
    [Test]
    public async Task VersionedServer_ValidClientVersionOlder_ShouldConnect()
    {
        // Similar to above - demonstrates the pattern for when we have versioned servers
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer<VersionedServerNexus, VersionedServerNexus.ClientProxy>(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        var version = "v1.0";
        await client.SendMessageAsync(new ClientGreetingMessage()
        {
            ServerNexusHash = Invocation.IInvocationMethodHash.GetVersionHashTable<VersionedServerNexus>()[version],
            ClientNexusHash = Invocation.IInvocationMethodHash.GetMethodHash<VersionedClientNexus>(),
            Version = version // Would be a valid version in versioned server
        }).Timeout(1);
        
        await client.AssertReceiveMessageAsync<ServerGreetingMessage>();
    }

    /// <summary>
    /// Tests versioned server with invalid client version (failure path).
    /// Path: TNexus.VersionHashTable.Count > 0 && TNexus.VersionHashTable.TryGetValue(requestedVersion, out verificationHash) == false
    /// Expected: DisconnectReason.ServerMismatch
    /// </summary>
    [Test]
    public async Task VersionedServer_InvalidClientVersion_ShouldDisconnectWithServerMismatch()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer<VersionedServerNexus, VersionedServerNexus.ClientProxy>(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();

        await client.SendMessageAsync(new ClientGreetingMessage()
        {
            ServerNexusHash = Invocation.IInvocationMethodHash.GetLatestVersionHash<VersionedServerNexus>(),
            ClientNexusHash = Invocation.IInvocationMethodHash.GetMethodHash<VersionedClientNexus>(),
            Version = "999.999.999" // Invalid version
        }).Timeout(1);
        
        // For our non-versioned server, this should fail as per non-versioned + non-null version test
        await client.AssertDisconnectReason(DisconnectReason.ServerMismatch).Timeout(1);
    }

    /// <summary>
    /// Tests hash mismatch scenario (failure path).
    /// Path: verificationHash != cGreeting.ServerNexusHash
    /// Expected: DisconnectReason.ServerMismatch
    /// </summary>
    [Test]
    public async Task HashMismatch_ShouldDisconnectWithServerMismatch()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        
        await client.SendMessageAsync(new ClientGreetingMessage()
        {
            ServerNexusHash = 999999, // Wrong hash
            ClientNexusHash = Invocation.IInvocationMethodHash.GetMethodHash<ClientNexus>(),
            Version = null
        }).Timeout(1);
        
        // Should disconnect with ServerMismatch due to hash mismatch
        await client.AssertDisconnectReason(DisconnectReason.ServerMismatch).Timeout(1);
    }

    /// <summary>
    /// Tests client hash mismatch scenario (failure path).
    /// This tests the path just before the version validation: cGreeting.ClientNexusHash != TProxy.MethodHash
    /// Expected: DisconnectReason.ClientMismatch
    /// </summary>
    [Test]
    public async Task ClientHashMismatch_ShouldDisconnectWithClientMismatch()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        
        // Send ClientGreeting with incorrect client hash
        await client.SendMessageAsync(new ClientGreetingMessage()
        {
            ServerNexusHash = Invocation.IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = 999999, // Wrong client hash
            Version = null
        }).Timeout(1);
        
        // Should disconnect with ClientMismatch due to client hash mismatch
        await client.AssertDisconnectReason(DisconnectReason.ClientMismatch).Timeout(1);
    }

    /// <summary>
    /// Tests successful connection with correct hashes and null version.
    /// This verifies the happy path through the version validation logic.
    /// </summary>
    [Test]
    public async Task CorrectHashesNullVersion_ShouldConnectSuccessfully()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        
        // Send ClientGreeting with correct hashes and null version
        await client.SendMessageAsync(new ClientGreetingMessage()
        {
            ServerNexusHash = Invocation.IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = Invocation.IInvocationMethodHash.GetMethodHash<ClientNexus>(),
            Version = null
        }).Timeout(1);

        await client.AssertReceiveMessageAsync<ServerGreetingMessage>();
    }

    /// <summary>
    /// Tests versioned server with empty string client version (failure path).
    /// Path: TNexus.VersionHashTable.Count > 0 && requestedVersion == ""
    /// Expected: DisconnectReason.ServerMismatch
    /// </summary>
    [Test]
    public async Task VersionedServer_EmptyStringClientVersion_ShouldDisconnectWithServerMismatch()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer<VersionedServerNexus, VersionedServerNexus.ClientProxy>(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        
        await client.SendMessageAsync(new ClientGreetingMessage()
        {
            ServerNexusHash = Invocation.IInvocationMethodHash.GetLatestVersionHash<VersionedServerNexus>(),
            ClientNexusHash = Invocation.IInvocationMethodHash.GetMethodHash<VersionedClientNexus>(),
            Version = "" // Empty string version for versioned server
        }).Timeout(1);
        
        await client.AssertDisconnectReason(DisconnectReason.ServerMismatch).Timeout(1);
    }

    /// <summary>
    /// Tests versioned server with valid version but wrong hash (failure path).
    /// Path: TNexus.VersionHashTable.TryGetValue succeeds but verificationHash != cGreeting.ServerNexusHash
    /// Expected: DisconnectReason.ServerMismatch
    /// </summary>
    [Test]
    public async Task VersionedServer_ValidVersionWithWrongHash_ShouldDisconnectWithServerMismatch()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer<VersionedServerNexus, VersionedServerNexus.ClientProxy>(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        
        var version = "v1.1";
        await client.SendMessageAsync(new ClientGreetingMessage()
        {
            ServerNexusHash = 999999, // Wrong hash for valid version
            ClientNexusHash = Invocation.IInvocationMethodHash.GetMethodHash<VersionedClientNexus>(),
            Version = version
        }).Timeout(1);
        
        await client.AssertDisconnectReason(DisconnectReason.ServerMismatch).Timeout(1);
    }

    /// <summary>
    /// Tests zero hashes scenario (boundary condition).
    /// Expected: DisconnectReason.ServerMismatch
    /// </summary>
    [Test]
    public async Task ZeroHashes_ShouldDisconnectWithClientMismatch()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        
        await client.SendMessageAsync(new ClientGreetingMessage()
        {
            ServerNexusHash = 0, // Zero hash
            ClientNexusHash = 0, // Zero hash
            Version = null
        }).Timeout(1);
        
        // Should disconnect with ServerMismatch due to hash mismatch
        await client.AssertDisconnectReason(DisconnectReason.ClientMismatch).Timeout(1);
    }

    /// <summary>
    /// Tests network timeout during version validation.
    /// Expected: Graceful handling without hanging
    /// </summary>
    [Test]
    public async Task NetworkTimeoutDuringVersionValidation_ShouldHandleGracefully()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        ServerNexus? nexus = null;
        var server = CreateServer(serverConfig, (nx) => nexus = nx);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        
        var tcs =  new TaskCompletionSource();
        nexus!.OnDisconnectedEvent = serverNexus =>
        {
            tcs.TrySetResult();
            return default;
        }; 
        
        // Immediately disconnect to simulate network timeout
        client.ForceDisconnect();
        
        await tcs.Task.Timeout(1);
    }
}
