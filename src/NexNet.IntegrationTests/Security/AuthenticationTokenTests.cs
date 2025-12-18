using MemoryPack;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Transports;
using NUnit.Framework;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Security;

/// <summary>
/// Tests for Fix 2.3: Authentication token size limit (8KB max)
/// Verifies that oversized authentication tokens are rejected with ProtocolError.
/// </summary>
internal class AuthenticationTokenTests : BaseTests
{
    private const int MaxAuthenticationTokenSize = 8192; // 8KB max - must match NexusSession.MaxAuthenticationTokenSize

    [Test]
    public async Task AuthenticationToken_AtLimit_Succeeds()
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

        // Create a token at the exact limit (8KB)
        var token = new byte[MaxAuthenticationTokenSize];
        Random.Shared.NextBytes(token);

        var greeting = new ClientGreetingMessage()
        {
            ServerNexusHash = IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = IInvocationMethodHash.GetMethodHash<ClientNexus>(),
            Version = null,
            AuthenticationToken = token
        };

        await client.SendMessageAsync(greeting).Timeout(1);

        // Should receive server greeting (connection accepted)
        await client.AssertReceiveMessageAsync<ServerGreetingMessage>().Timeout(1);

        // Connection should succeed
        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task AuthenticationToken_OverLimit_DisconnectsWithProtocolError()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();

        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();

        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();

        // Create a token that exceeds the limit (8KB + 1 byte)
        var token = new byte[MaxAuthenticationTokenSize + 1];
        Random.Shared.NextBytes(token);

        var greeting = new ClientGreetingMessage()
        {
            ServerNexusHash = IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = IInvocationMethodHash.GetMethodHash<ClientNexus>(),
            Version = null,
            AuthenticationToken = token
        };

        await client.SendMessageAsync(greeting).Timeout(1);

        // Should disconnect with ProtocolError
        await client.AssertDisconnectReason(DisconnectReason.ProtocolError).Timeout(1);
    }

    [Test]
    public async Task AuthenticationToken_WayOverLimit_DisconnectsWithProtocolError()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();

        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();

        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();

        // Create a very large token (16KB)
        var token = new byte[MaxAuthenticationTokenSize * 2];
        Random.Shared.NextBytes(token);

        var greeting = new ClientGreetingMessage()
        {
            ServerNexusHash = IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = IInvocationMethodHash.GetMethodHash<ClientNexus>(),
            Version = null,
            AuthenticationToken = token
        };

        await client.SendMessageAsync(greeting).Timeout(1);

        // Should disconnect with ProtocolError
        await client.AssertDisconnectReason(DisconnectReason.ProtocolError).Timeout(1);
    }

    [Test]
    public async Task AuthenticationToken_Empty_Succeeds()
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

        // Send greeting with empty token
        var greeting = new ClientGreetingMessage()
        {
            ServerNexusHash = IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = IInvocationMethodHash.GetMethodHash<ClientNexus>(),
            Version = null,
            AuthenticationToken = Memory<byte>.Empty
        };

        await client.SendMessageAsync(greeting).Timeout(1);

        // Should receive server greeting (connection accepted)
        await client.AssertReceiveMessageAsync<ServerGreetingMessage>().Timeout(1);

        // Connection should succeed
        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task AuthenticationToken_SmallValid_Succeeds()
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

        // Send greeting with small token
        var token = new byte[64];
        Random.Shared.NextBytes(token);

        var greeting = new ClientGreetingMessage()
        {
            ServerNexusHash = IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = IInvocationMethodHash.GetMethodHash<ClientNexus>(),
            Version = null,
            AuthenticationToken = token
        };

        await client.SendMessageAsync(greeting).Timeout(1);

        // Should receive server greeting (connection accepted)
        await client.AssertReceiveMessageAsync<ServerGreetingMessage>().Timeout(1);

        // Connection should succeed
        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task AuthenticationToken_JustUnderLimit_Succeeds()
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

        // Create a token just under the limit
        var token = new byte[MaxAuthenticationTokenSize - 1];
        Random.Shared.NextBytes(token);

        var greeting = new ClientGreetingMessage()
        {
            ServerNexusHash = IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = IInvocationMethodHash.GetMethodHash<ClientNexus>(),
            Version = null,
            AuthenticationToken = token
        };

        await client.SendMessageAsync(greeting).Timeout(1);

        // Should receive server greeting (connection accepted)
        await client.AssertReceiveMessageAsync<ServerGreetingMessage>().Timeout(1);

        // Connection should succeed
        await tcs.Task.Timeout(1);
    }
}
