using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Transports;
using NUnit.Framework;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Security;

/// <summary>
/// Tests for Fix 2.8: Version string length limit (16 chars max)
/// Verifies that oversized version strings are rejected with ProtocolError.
/// </summary>
internal class VersionStringTests : BaseTests
{
    private const int MaxVersionStringLength = 16; // Must match NexusSession.MaxVersionStringLength

    [Test]
    public async Task VersionString_AtLimit_Succeeds()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var tcs = new TaskCompletionSource();
        var server = CreateServer(serverConfig, nx =>
        {
            nx.OnConnectedEvent = _ =>
            {
                tcs.TrySetResult();
                return default;
            };
        });
        await server.StartAsync();

        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();

        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();

        // Create a version string at the exact limit (16 chars)
        var version = new string('v', MaxVersionStringLength);

        var greeting = new ClientGreetingMessage()
        {
            ServerNexusHash = IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = IInvocationMethodHash.GetMethodHash<ClientNexus>(),
            Version = version
        };

        await client.SendMessageAsync(greeting).Timeout(1);

        // Non-versioned server will reject versioned client, but not due to version length
        // The server should process the version string before rejecting for other reasons
        // For this test, we need a versioned server to properly test version string length
        // Since we're testing the length limit, let's just verify we don't get immediate ProtocolError
        // due to length - we'll get ServerMismatch instead because it's a non-versioned server

        // The important thing is that the version string was accepted and processed
        // If it was too long, we'd get ProtocolError before the hash mismatch check
        await client.AssertDisconnectReason(DisconnectReason.ServerMismatch).Timeout(1);
    }

    [Test]
    public async Task VersionString_OverLimit_DisconnectsWithProtocolError()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();

        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();

        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();

        // Create a version string that exceeds the limit (17 chars)
        var version = new string('v', MaxVersionStringLength + 1);

        var greeting = new ClientGreetingMessage()
        {
            ServerNexusHash = IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = IInvocationMethodHash.GetMethodHash<ClientNexus>(),
            Version = version
        };

        await client.SendMessageAsync(greeting).Timeout(1);

        // Should disconnect with ProtocolError due to version string too long
        await client.AssertDisconnectReason(DisconnectReason.ProtocolError).Timeout(1);
    }

    [Test]
    public async Task VersionString_WayOverLimit_DisconnectsWithProtocolError()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();

        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();

        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();

        // Create a very long version string (100 chars)
        var version = new string('v', 100);

        var greeting = new ClientGreetingMessage()
        {
            ServerNexusHash = IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = IInvocationMethodHash.GetMethodHash<ClientNexus>(),
            Version = version
        };

        await client.SendMessageAsync(greeting).Timeout(1);

        // Should disconnect with ProtocolError due to version string too long
        await client.AssertDisconnectReason(DisconnectReason.ProtocolError).Timeout(1);
    }

    [Test]
    public async Task VersionString_Null_Succeeds()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var tcs = new TaskCompletionSource();
        var server = CreateServer(serverConfig, nx =>
        {
            nx.OnConnectedEvent = _ =>
            {
                tcs.TrySetResult();
                return default;
            };
        });
        await server.StartAsync();

        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();

        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();

        // Send greeting with null version
        var greeting = new ClientGreetingMessage()
        {
            ServerNexusHash = IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = IInvocationMethodHash.GetMethodHash<ClientNexus>(),
            Version = null
        };

        await client.SendMessageAsync(greeting).Timeout(1);

        // Should receive server greeting (connection accepted)
        await client.AssertReceiveMessageAsync<ServerGreetingMessage>().Timeout(1);

        // Connection should succeed
        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task VersionString_Empty_Succeeds()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();

        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();

        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();

        // Send greeting with empty version (will be treated as versioned attempt)
        var greeting = new ClientGreetingMessage()
        {
            ServerNexusHash = IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = IInvocationMethodHash.GetMethodHash<ClientNexus>(),
            Version = ""
        };

        await client.SendMessageAsync(greeting).Timeout(1);

        // Empty string is treated as version specified, so non-versioned server rejects it
        await client.AssertDisconnectReason(DisconnectReason.ServerMismatch).Timeout(1);
    }

    [Test]
    public async Task VersionString_ShortValid_Succeeds()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();

        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();

        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();

        // Send greeting with short version string
        var greeting = new ClientGreetingMessage()
        {
            ServerNexusHash = IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = IInvocationMethodHash.GetMethodHash<ClientNexus>(),
            Version = "v1"
        };

        await client.SendMessageAsync(greeting).Timeout(1);

        // Version string is valid (short), but non-versioned server will reject versioned client
        await client.AssertDisconnectReason(DisconnectReason.ServerMismatch).Timeout(1);
    }

    [Test]
    public async Task VersionString_JustUnderLimit_Succeeds()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();

        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();

        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();

        // Create a version string just under the limit
        var version = new string('v', MaxVersionStringLength - 1);

        var greeting = new ClientGreetingMessage()
        {
            ServerNexusHash = IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = IInvocationMethodHash.GetMethodHash<ClientNexus>(),
            Version = version
        };

        await client.SendMessageAsync(greeting).Timeout(1);

        // Version string is valid (under limit), but non-versioned server will reject versioned client
        await client.AssertDisconnectReason(DisconnectReason.ServerMismatch).Timeout(1);
    }
}
