using System.Buffers;
using System.Text;
using MemoryPack;
using NexNet.IntegrationTests.Pipes;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Internals;
using NexNet.Messages;
using NexNet.Transports;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Security;

/// <summary>
/// Security tests to ensure that malicious or malformed TCP connections cannot bypass 
/// the server's ClientGreetingMessage connection requirement or exploit protocol vulnerabilities.
/// </summary>
[TestFixture]
internal class ProtocolSecurityTests : BaseTests
{
    // Protocol constants from NexusSession
    private const uint ProtocolTag = 0x4E4E5014;
    private const byte ProtocolVersion = 1;
    
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
        
        using var client = CreateRawTcpClient(serverConfig, false);
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
        var serverConfig = CreateServerConfig(Type.Tcp, BasePipeTests.LogMode.Always);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();
        
        using var client = CreateRawTcpClient(serverConfig, false);
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
    /*
    /// <summary>
    /// Tests that sending invalid protocol header results in disconnection.
    /// </summary>
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task RawTcpConnection_InvalidProtocolHeader_ShouldDisconnectWithProtocolError(Type type)
    {
        var (server, _, _, _, configs) = await Setup(type);

        using var client = CreateRawTcpClient(configs, type == Type.TcpTls);
        await client.ConnectAsync();

        // Send invalid protocol header (wrong tag)
        var invalidHeader = CreateProtocolHeader(ProtocolVersion, 0, 0, 0, 0xDEADBEEF);
        await client.SendRawAsync(BitConverter.GetBytes(invalidHeader));

        // Server should disconnect with ProtocolError
        var disconnectReason = await client.WaitForDisconnectAsync();
        Assert.That(disconnectReason, Is.EqualTo(DisconnectReason.ProtocolError));
    }

    /// <summary>
    /// Tests that sending invalid protocol version results in disconnection.
    /// </summary>
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task RawTcpConnection_InvalidProtocolVersion_ShouldDisconnectWithProtocolError(Type type)
    {
        var (server, _, _, _, configs) = await Setup(type);

        using var client = CreateRawTcpClient(configs, type == Type.TcpTls);
        await client.ConnectAsync();

        // Send invalid protocol header (wrong version)
        var invalidHeader = CreateProtocolHeader(99, 0, 0, 0, ProtocolTag);
        await client.SendRawAsync(BitConverter.GetBytes(invalidHeader));

        // Server should disconnect with ProtocolError
        var disconnectReason = await client.WaitForDisconnectAsync();
        Assert.That(disconnectReason, Is.EqualTo(DisconnectReason.ProtocolError));
    }

    /// <summary>
    /// Tests that sending malformed message headers results in disconnection.
    /// </summary>
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task RawTcpConnection_MalformedMessageHeader_ShouldDisconnectWithProtocolError(Type type)
    {
        var (server, _, _, _, configs) = await Setup(type);

        using var client = CreateRawTcpClient(configs, type == Type.TcpTls);
        await client.ConnectAsync();

        // Send valid protocol header
        await client.SendProtocolHeaderAsync();

        // Send invalid message type
        await client.SendRawAsync(new byte[] { 255 }); // Invalid message type

        // Server should disconnect with ProtocolError
        var disconnectReason = await client.WaitForDisconnectAsync();
        Assert.That(disconnectReason, Is.EqualTo(DisconnectReason.ProtocolError));
    }

    /// <summary>
    /// Tests that sending ClientGreetingReconnection without prior connection results in ProtocolError.
    /// </summary>
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task RawTcpConnection_ReconnectionWithoutPriorConnection_ShouldDisconnectWithProtocolError(Type type)
    {
        var (server, _, _, _, configs) = await Setup(type);

        using var client = CreateRawTcpClient(configs, type == Type.TcpTls);
        await client.ConnectAsync();

        // Send valid protocol header
        await client.SendProtocolHeaderAsync();

        // Send ClientGreetingReconnection without prior connection
        var greeting = CreateValidClientGreeting();
        await client.SendMessageAsync(MessageType.ClientGreetingReconnection, greeting);

        // Server should disconnect with ProtocolError (reconnection logic disabled in current version)
        var disconnectReason = await client.WaitForDisconnectAsync();
        Assert.That(disconnectReason, Is.EqualTo(DisconnectReason.ProtocolError));
    }

    /// <summary>
    /// Tests that sending messages with incorrect hash values results in mismatch disconnection.
    /// </summary>
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task RawTcpConnection_InvalidMethodHashes_ShouldDisconnectWithMismatch(Type type)
    {
        var (server, _, _, _, configs) = await Setup(type);

        using var client = CreateRawTcpClient(configs, type == Type.TcpTls);
        await client.ConnectAsync();

        // Send valid protocol header
        await client.SendProtocolHeaderAsync();

        // Send ClientGreeting with invalid server hash
        var greeting = CreateClientGreetingWithInvalidHashes();
        await client.SendMessageAsync(MessageType.ClientGreeting, greeting);

        // Server should disconnect with ServerMismatch or ClientMismatch
        var disconnectReason = await client.WaitForDisconnectAsync();
        Assert.That(disconnectReason, Is.EqualTo(DisconnectReason.ServerMismatch).Or.EqualTo(DisconnectReason.ClientMismatch));
    }

    /// <summary>
    /// Tests that attempting to send server-only messages from client results in ProtocolError.
    /// </summary>
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task RawTcpConnection_ClientSendsServerGreeting_ShouldDisconnectWithProtocolError(Type type)
    {
        var (server, _, _, _, configs) = await Setup(type);

        using var client = CreateRawTcpClient(configs, type == Type.TcpTls);
        await client.ConnectAsync();

        // Send valid protocol header
        await client.SendProtocolHeaderAsync();

        // Send ServerGreeting from client (should be rejected)
        var serverGreeting = new ServerGreetingMessage { Version = 0, ClientId = 12345 };
        var serialized = MemoryPackSerializer.Serialize(serverGreeting);
        await client.SendMessageAsync(MessageType.ServerGreeting, serialized);

        // Server should disconnect with ProtocolError
        var disconnectReason = await client.WaitForDisconnectAsync();
        Assert.That(disconnectReason, Is.EqualTo(DisconnectReason.ProtocolError));
    }

    /// <summary>
    /// Tests that the server properly times out connections that don't complete handshake.
    /// </summary>
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task RawTcpConnection_IncompleteHandshake_ShouldTimeoutAndDisconnect(Type type)
    {
        var (server, _, _, _, configs) = await Setup(type);

        using var client = CreateRawTcpClient(configs, type == Type.TcpTls);
        await client.ConnectAsync();

        // Send protocol header but don't send ClientGreeting
        await client.SendProtocolHeaderAsync();

        // Wait for handshake timeout (should be shorter than test timeout)
        var disconnectReason = await client.WaitForDisconnectAsync(TimeSpan.FromSeconds(30));
        Assert.That(disconnectReason, Is.EqualTo(DisconnectReason.Timeout));
    }

    /// <summary>
    /// Tests buffer overflow protection by sending oversized messages.
    /// </summary>
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task RawTcpConnection_OversizedMessage_ShouldDisconnectGracefully(Type type)
    {
        var (server, _, _, _, configs) = await Setup(type);

        using var client = CreateRawTcpClient(configs, type == Type.TcpTls);
        await client.ConnectAsync();

        // Send valid protocol header
        await client.SendProtocolHeaderAsync();

        // Send message with extremely large body length (1GB)
        var header = new byte[] { (byte)MessageType.ClientGreeting };
        var bodyLength = BitConverter.GetBytes((uint)(1024 * 1024 * 1024)); // 1GB

        await client.SendRawAsync(header);
        await client.SendRawAsync(bodyLength);

        // Server should handle this gracefully and disconnect
        var disconnectReason = await client.WaitForDisconnectAsync();
        Assert.That(disconnectReason, Is.EqualTo(DisconnectReason.ProtocolError).Or.EqualTo(DisconnectReason.SocketError));
    }*/
    
    private RawTcpClient CreateRawTcpClient(ServerConfig configs, bool useTls)
    {
        return new RawTcpClient(configs, useTls, base.CurrentTcpPort!.Value, Logger);
    }
}
