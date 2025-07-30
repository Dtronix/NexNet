using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MemoryPack;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Internals;
using NexNet.Logging;
using NexNet.Messages;
using NexNet.Transports;
using NUnit.Framework;
using StreamStruct;
using LogLevel = StreamStruct.LogLevel;

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
        var invocationData = MemoryPackSerializer.Serialize(new InvocationMessage
        {
            InvocationId = 1,
            MethodId = 123,
            Arguments = Memory<byte>.Empty
        });
        
        await client.SendMessageWithBodyAsync(MessageType.Invocation, invocationData).Timeout(1);
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
        
        using var client = CreateRawTcpClient(serverConfig, false);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        
        var clientGreeting = MemoryPackSerializer.Serialize(new ClientGreetingMessage()
        {
            ServerNexusMethodHash = Invocation.IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusMethodHash = Invocation.IInvocationMethodHash.GetMethodHash<ClientNexus>(),
            Version = 1
        });

        await client.SendMessageWithBodyAsync(MessageType.ClientGreeting, clientGreeting).Timeout(1);
        await client.SendMessageWithBodyAsync(MessageType.ClientGreeting, clientGreeting).Timeout(1);

        // Ignore the message
        await client.Processor.ReadAsync(client.ProtocolMessageDefinition);

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
    /*
    private byte[] CreateValidClientGreeting()
    {
        var greeting = new ClientGreetingMessage
        {
            Version = 0,
            ServerNexusMethodHash = typeof(BasicTestsServerNexus).GetHashCode(), // Use actual hash
            ClientNexusMethodHash = typeof(BasicTestsClientNexus).GetHashCode(), // Use actual hash
            AuthenticationToken = Memory<byte>.Empty
        };
        
        return MemoryPackSerializer.Serialize(greeting);
    }
    
    private byte[] CreateClientGreetingWithInvalidHashes()
    {
        var greeting = new ClientGreetingMessage
        {
            Version = 0,
            ServerNexusMethodHash = 0xDEADBEEF, // Invalid hash
            ClientNexusMethodHash = 0xCAFEBABE, // Invalid hash
            AuthenticationToken = Memory<byte>.Empty
        };
        
        return MemoryPackSerializer.Serialize(greeting);
    }*/

    private static ulong CreateProtocolHeader(byte protocolVersion, byte reserved1, byte reserved2, byte reserved3, uint protocolTag)
    {
        int high = (protocolVersion << 24) | (reserved1 << 16) | (reserved2 << 8) | reserved3;
        var combined = ((ulong)high << 32) | ((ulong)protocolTag & 0xFFFFFFFF);
        return combined;
    }
}

/// <summary>
/// Raw TCP client for testing protocol security without using NexNet client libraries.
/// </summary>
internal class RawTcpClient : IDisposable
{
    private readonly TcpClient _tcpClient;
    private readonly ServerConfig _configs;
    private readonly bool _useTls;
    private readonly int _port;
    private readonly RollingLogger _logger;
    private Stream? _stream;
    private readonly CancellationTokenSource _cts = new();
    private StreamFieldProcessor? _streamProcessor;

    public bool IsConnected => _tcpClient.Connected;

    public Stream? Stream => _stream;

    public StreamFieldProcessor Processor => _streamProcessor;

    public RawTcpClient(ServerConfig configs, bool useTls, int port, RollingLogger logger)
    {
        _tcpClient = new TcpClient();
        _configs = configs;
        _useTls = useTls;
        _port = port;
        _logger = logger;
    }
    
    public async Task ConnectAsync()
    {
        await _tcpClient.ConnectAsync(IPAddress.Loopback, _port);
        _stream = _tcpClient.GetStream();
        _streamProcessor = new StreamFieldProcessor(_stream)
        {
            Logger = new RollingStreamLogger(_logger, false, LogLevel.Info)
        };
        
        if (_useTls)
        {
            var sslStream = new System.Net.Security.SslStream(_stream, false, (sender, certificate, chain, errors) => true);
            await sslStream.AuthenticateAsClientAsync("localhost");
            _stream = sslStream;
        }
    }

    public async Task AssertVerify(string definition, object?[]? expectedValues)
    {
        var result = await _streamProcessor!.VerifyAsync(definition, expectedValues);
        Assert.That(result.ValidationErrors, Is.Empty);
    }
    
    public async Task AssertDisconnectReason(DisconnectReason reason)
    {
        await AssertVerify("[type:byte]", [(byte)reason]);
    }

    public readonly string ProtocolMessageDefinition = "[type:byte][body_length:ushort][body:body_length]";
    
    private static readonly string _protocolHeader =
        "[magByt1:byte][magByt2:byte][magByt3:byte][magByt4:byte][reserved1:byte][reserved2:byte][reserved3:byte][version:byte]";

    private static byte _protocolVersion = 1;
    private static readonly object[] _protocolHeaderValues =
        [(byte)'N', (byte)'n', (byte)'P', (byte)'\u0014', 0, 0, 0, _protocolVersion];
    public async Task SendProtocolHeaderAsync()
    {
        var result = await _streamProcessor!.WriteAsync(_protocolHeader, _protocolHeaderValues).Timeout(1);
        Assert.That(result, Is.True);
    }
    
    public async Task ReadProtocolHeaderAsync()
    {
        if (Stream == null) 
            throw new InvalidOperationException("Not connected");
        await AssertVerify(_protocolHeader, _protocolHeaderValues);
    }
    
    public async Task AssertWrite(string definition, object?[] data)
    {
        if (Stream == null) 
            throw new InvalidOperationException("Not connected");
        
        var result = await _streamProcessor!.WriteAsync(definition, data).Timeout(1);
        Assert.That(result, Is.True);
    }
    
    public async Task SendMessageWithBodyAsync(MessageType messageType, byte[] messageBody)
    {
        if (Stream == null) 
            throw new InvalidOperationException("Not connected");
        
        await AssertWrite(ProtocolMessageDefinition, [
            (byte)messageType,
            (ushort)messageBody.Length,
            messageBody
        ]).Timeout(1);
    }
    public void Dispose()
    {
        _cts.Cancel();
        Stream?.Dispose();
        _tcpClient.Dispose();
        _cts.Dispose();
    }
}
