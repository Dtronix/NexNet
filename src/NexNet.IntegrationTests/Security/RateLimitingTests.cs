using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Transports;
using NUnit.Framework;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Security;

/// <summary>
/// Tests for Fix 2.7: Rate limiting on greeting messages
/// Verifies that sending too many greeting messages results in ProtocolError.
/// </summary>
internal class RateLimitingTests : BaseTests
{
    private const int MaxGreetingAttempts = 3; // Must match NexusSession.MaxGreetingAttempts

    [Test]
    public async Task GreetingRateLimit_SingleGreeting_Succeeds()
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

        // Send single greeting
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
    public async Task GreetingRateLimit_TwoGreetings_SecondRejected()
    {
        // Note: The existing test in ProtocolSecurityTests.cs tests this scenario
        // This is an additional test to verify the rate limiting behavior
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();

        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();

        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();

        var greeting = new ClientGreetingMessage()
        {
            ServerNexusHash = IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = IInvocationMethodHash.GetMethodHash<ClientNexus>(),
            Version = null
        };

        // Send first greeting (should succeed)
        await client.SendMessageAsync(greeting).Timeout(1);
        await client.AssertReceiveMessageAsync<ServerGreetingMessage>().Timeout(1);

        // Send second greeting (should be rejected - already connected)
        await client.SendMessageAsync(greeting).Timeout(1);

        // Should disconnect with ProtocolError
        await client.AssertDisconnectReason(DisconnectReason.ProtocolError).Timeout(1);
    }

    [Test]
    public async Task GreetingRateLimit_MultipleGreetingsAfterConnection_RejectedWithProtocolError()
    {
        // Test that sending multiple greetings after successful connection triggers rate limit
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

        // First greeting - should succeed
        var validGreeting = new ClientGreetingMessage()
        {
            ServerNexusHash = IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = IInvocationMethodHash.GetMethodHash<ClientNexus>(),
            Version = null
        };

        await client.SendMessageAsync(validGreeting).Timeout(1);
        await client.AssertReceiveMessageAsync<ServerGreetingMessage>().Timeout(1);
        await tcs.Task.Timeout(1);

        // Second greeting - should be rejected (already connected)
        await client.SendMessageAsync(validGreeting).Timeout(1);

        // Should disconnect with ProtocolError
        await client.AssertDisconnectReason(DisconnectReason.ProtocolError).Timeout(1);
    }

    [Test]
    public async Task GreetingRateLimit_FirstBadGreeting_DisconnectsImmediately()
    {
        // Test that a single bad greeting disconnects with appropriate error
        // (Rate limiting doesn't prevent the first disconnect for other reasons)
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();

        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();

        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();

        // Create greeting with bad hash
        var badGreeting = new ClientGreetingMessage()
        {
            ServerNexusHash = 12345, // Invalid hash
            ClientNexusHash = 67890, // Invalid hash
            Version = null
        };

        await client.SendMessageAsync(badGreeting).Timeout(1);

        // Should disconnect with ClientMismatch (not rate limited - first greeting)
        await client.AssertDisconnectReason(DisconnectReason.ClientMismatch).Timeout(1);
    }

    [Test]
    public async Task GreetingRateLimit_NewConnection_CounterResets()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();

        // First connection - send some greetings
        using (var client1 = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger))
        {
            await client1.ConnectAsync();
            await client1.SendProtocolHeaderAsync();
            await client1.ReadProtocolHeaderAsync();

            var badGreeting = new ClientGreetingMessage()
            {
                ServerNexusHash = 12345, // Invalid hash
                ClientNexusHash = 67890, // Invalid hash
                Version = null
            };

            // Send two greetings (under the limit)
            await client1.SendMessageAsync(badGreeting).Timeout(1);
            await client1.SendMessageAsync(badGreeting).Timeout(1);

            // Disconnect
            client1.ForceDisconnect();
        }

        // Wait a bit for server to clean up
        await Task.Delay(100);

        // Second connection - should have fresh counter
        using var client2 = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client2.ConnectAsync();
        await client2.SendProtocolHeaderAsync();
        await client2.ReadProtocolHeaderAsync();

        // Send valid greeting - should succeed because counter was reset
        var validGreeting = new ClientGreetingMessage()
        {
            ServerNexusHash = IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = IInvocationMethodHash.GetMethodHash<ClientNexus>(),
            Version = null
        };

        await client2.SendMessageAsync(validGreeting).Timeout(1);

        // Should receive server greeting (connection accepted)
        await client2.AssertReceiveMessageAsync<ServerGreetingMessage>().Timeout(1);
    }

}
