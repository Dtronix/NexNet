using System.Buffers;
using System.Text;
using NexNet.IntegrationTests.Pipes;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Internals;
using NexNet.Messages;
using NexNet.Transports;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Security;

internal class ProtocolSecurityTests : BaseTests
{
    /// <summary>
    /// Tests that a raw TCP connection attempting to send invocation messages
    /// without proper ClientGreeting handshake is rejected with ProtocolError.
    /// </summary>
    [Test]
    public async Task SkipClientGreeting_SendInvocation_ShouldDisconnectWithProtocolError()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        
        // Attempt to send invocation message without ClientGreeting/Authentication
        await client.SendMessageAsync(new InvocationMessage
        {
            InvocationId = 1,
            MethodId = 123,
            Arguments = Memory<byte>.Empty
        }).Timeout(1);
        
        await client.AssertDisconnectReason(DisconnectReason.ProtocolError).Timeout(1);
    }
    
    /// <summary>
    /// Tests that sending multiple ClientGreeting messages results in ProtocolError.
    /// </summary>
    [Test]
    public async Task MultipleClientGreetings_ShouldDisconnectWithProtocolError()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();

        var clientGreeting = new ClientGreetingMessage()
        {
            ServerNexusHash = Invocation.IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = Invocation.IInvocationMethodHash.GetMethodHash<ClientNexus>(),
            Version = null
        };

        await client.SendMessageAsync(clientGreeting).Timeout(1);
        await client.SendMessageAsync(clientGreeting).Timeout(1);

        // Ignore the message
        await client.AssertReceiveMessageAsync<ServerGreetingMessage>();

        await client.AssertDisconnectReason(DisconnectReason.ProtocolError).Timeout(1);
    }
    
    [Test]
    public async Task HandshakeTimeout_PostProtocolHeader_ShouldDisconnectWithTimeout()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        serverConfig.HandshakeTimeout = 50;
        var tcs =  new TaskCompletionSource();
        var server = CreateServer(serverConfig, nx =>
        {
            nx.OnDisconnectedEvent = _ =>
            {
                tcs.TrySetResult();
                return default;
            };
        });
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        
        await tcs.Task.Timeout(1);
    }
    
    [Test]
    public async Task HandshakeTimeout_PreProtocolHeader_ShouldDisconnectWithTimeout()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        serverConfig.HandshakeTimeout = 50;
        var tcs =  new TaskCompletionSource();
        var server = CreateServer(serverConfig, nx =>
        {
            nx.OnDisconnectedEvent = _ =>
            {
                tcs.TrySetResult();
                return default;
            };
        });
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        await tcs.Task.Timeout(1);
    }
    
    
    [Test]
    public async Task InvalidProtocolHeader_ShouldDisconnectWithProtocolError()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync(true);
        await client.ReadProtocolHeaderAsync();

        // Should disconnect with ServerMismatch
        await client.AssertDisconnectReason(DisconnectReason.ProtocolError).Timeout(1);
    }
    
    [Test]
    public async Task InvalidVersionHeader_ShouldDisconnectWithProtocolError()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync(false, true);
        await client.ReadProtocolHeaderAsync();

        // Should disconnect with ServerMismatch
        await client.AssertDisconnectReason(DisconnectReason.ProtocolError).Timeout(1);
    }
    
    [Test]
    public async Task UnsupportedProtocolVersion_ShouldDisconnectWithProtocolError()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();

        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();

        // Valid magic and reserved bytes, but protocol version 2.
        await client.AssertWrite(RawTcpClient.ProtocolHeader,
            [(byte)'N', (byte)'n', (byte)'P', (byte)0x14, (byte)0, (byte)0, (byte)0, (byte)2]);
        await client.ReadProtocolHeaderAsync();

        await client.AssertDisconnectReason(DisconnectReason.ProtocolError).Timeout(1);
    }

    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    public async Task NonZeroReservedByte_ShouldDisconnectWithProtocolError(int reservedIndex)
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();

        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();

        object[] header = [(byte)'N', (byte)'n', (byte)'P', (byte)0x14, (byte)0, (byte)0, (byte)0, RawTcpClient.ProtocolVersion];
        header[reservedIndex] = (byte)1;
        await client.AssertWrite(RawTcpClient.ProtocolHeader, header);
        await client.ReadProtocolHeaderAsync();

        await client.AssertDisconnectReason(DisconnectReason.ProtocolError).Timeout(1);
    }

    /// <summary>
    /// A peer from an older release passes the preamble (the version byte is unchanged) but sends a greeting body
    /// in a different encoding. The server must disconnect with ProtocolError instead of throwing.
    /// </summary>
    [Test]
    public async Task MalformedClientGreetingBody_ShouldDisconnectWithProtocolError()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();

        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();

        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();

        // A pre-0.17 style body: member count followed by raw little-endian fields. Not a greeting array.
        await client.SendMessageWithBodyAsync(MessageType.ClientGreeting,
            [0x04, 0xFF, 0xFF, 0xFF, 0xFF, 0x2A, 0x00, 0x00, 0x00, 0x15, 0x00, 0x00, 0x00]);

        await client.AssertDisconnectReason(DisconnectReason.ProtocolError).Timeout(1);
    }

    [Test]
    [Repeat(10)]
    public async Task MalformedMessageHeader_PreClientGreeting_ShouldDisconnectWithProtocolError()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();

        byte messageType;

        // Ensure we don't get the message type required.
        while (true)
        {
            messageType = (byte)Random.Shared.Next(0, 256);
            if (messageType != (byte)MessageType.ClientGreetingReconnection 
                && messageType != (byte)MessageType.ClientGreeting
                && messageType is < 20 or > 39)
                break;
        }
        
        client.Write(messageType);
        
        // Should disconnect with ServerMismatch
        await client.AssertDisconnectReason(DisconnectReason.ProtocolError).Timeout(1);
    }
    
    [Test]
    [Repeat(10)]
    public async Task MalformedMessageHeader_AfterConnection_ShouldDisconnectWithProtocolError()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();

        byte messageType;

        // Ensure we don't get the message type required.
        while (true)
        {
            messageType = (byte)Random.Shared.Next(0, 256);
            if (messageType != (byte)MessageType.ClientGreetingReconnection 
                && messageType != (byte)MessageType.ClientGreeting
                && messageType is < 20 or > 39)
                break;
        }
        
        client.Write(messageType);
        
        // Should disconnect with ServerMismatch
        await client.AssertDisconnectReason(DisconnectReason.ProtocolError).Timeout(1);
    }
    
    /// <summary>
    /// Tests that attempting to send server-only messages from client results in ProtocolError.
    /// </summary>
    [Test]
    public async Task ClientSendsServerGreeting_ShouldDisconnectWithProtocolError()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer<VersionedServerNexusV2, VersionedServerNexusV2.ClientProxy>(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();

        await client.SendMessageAsync(new ServerGreetingMessage()
        {
            ClientId = -1,
            Version = 1
        }).Timeout(1);
        
        // For our non-versioned server, this should fail as per non-versioned + non-null version test
        await client.AssertDisconnectReason(DisconnectReason.ProtocolError).Timeout(1);
    }
}
