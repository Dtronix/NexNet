using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MemoryPack;
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
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task RawTcpConnection_SkipClientGreeting_SendInvocation_ShouldDisconnectWithProtocolError(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();
        
        using var client = CreateRawTcpClient(serverConfig, type == Type.TcpTls);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        
        // Attempt to send invocation message without ClientGreeting
        var invocationMessage = CreateMaliciousInvocationMessage();
        await client.SendMessageAsync(MessageType.Invocation, invocationMessage);
        
        // Server should disconnect with ProtocolError
        var disconnectReason = await client.WaitForDisconnectAsync();
        Assert.That(disconnectReason, Is.EqualTo(DisconnectReason.ProtocolError));
        
        // Verify connection is actually closed
        Assert.That(client.IsConnected, Is.False);
    }
    /*
    /// <summary>
    /// Tests that sending multiple ClientGreeting messages results in ProtocolError.
    /// </summary>
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task RawTcpConnection_MultipleClientGreetings_ShouldDisconnectWithProtocolError(Type type)
    {
        var (server, _, _, _, configs) = await Setup(type);
        
        using var client = CreateRawTcpClient(configs, type == Type.TcpTls);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        
        // Send first ClientGreeting (valid)
        var greeting = CreateValidClientGreeting();
        await client.SendMessageAsync(MessageType.ClientGreeting, greeting);
        
        // Wait for ServerGreeting response
        var serverGreeting = await client.WaitForServerGreetingAsync();
        Assert.That(serverGreeting, Is.Not.Null);
        
        // Send second ClientGreeting (should be rejected)
        await client.SendMessageAsync(MessageType.ClientGreeting, greeting);
        
        // Server should disconnect with ProtocolError
        var disconnectReason = await client.WaitForDisconnectAsync();
        Assert.That(disconnectReason, Is.EqualTo(DisconnectReason.ProtocolError));
    }
    
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
        return new RawTcpClient(configs, useTls, base.CurrentTcpPort!.Value);
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
    
    private byte[] CreateMaliciousInvocationMessage()
    {
        // Create a malicious invocation message attempting to call server methods without proper handshake
        var invocation = new InvocationMessage
        {
            InvocationId = 1,
            MethodId = 123,
            Arguments = Memory<byte>.Empty
        };
        
        return MemoryPackSerializer.Serialize(invocation);
    }
    
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
    private Stream? _stream;
    private readonly List<byte> _receivedData = new();
    private readonly CancellationTokenSource _cts = new();
    
    public bool IsConnected => _tcpClient.Connected;
    
    public RawTcpClient(ServerConfig configs, bool useTls, int port)
    {
        _tcpClient = new TcpClient();
        _configs = configs;
        _useTls = useTls;
        _port = port;
    }
    
    public async Task ConnectAsync()
    {
        await _tcpClient.ConnectAsync(IPAddress.Loopback, _port);
        _stream = _tcpClient.GetStream();
        
        if (_useTls)
        {
            var sslStream = new System.Net.Security.SslStream(_stream, false, (sender, certificate, chain, errors) => true);
            await sslStream.AuthenticateAsClientAsync("localhost");
            _stream = sslStream;
        }
        
        // Start reading background task
        _ = Task.Run(ReadLoop);
    }
    
    public async Task SendProtocolHeaderAsync()
    {
        const uint protocolTag = 0x4E4E5014;
        const byte protocolVersion = 1;
        var header = CreateProtocolHeader(protocolVersion, 0, 0, 0, protocolTag);
        await SendRawAsync(BitConverter.GetBytes(header));
    }
    
    public async Task SendRawAsync(byte[] data)
    {
        if (_stream == null) throw new InvalidOperationException("Not connected");
        await _stream.WriteAsync(data);
        await _stream.FlushAsync();
    }
    
    public async Task SendMessageAsync(MessageType messageType, byte[] messageBody)
    {
        if (_stream == null) throw new InvalidOperationException("Not connected");
        
        // Send message type
        await _stream.WriteAsync(new[] { (byte)messageType });
        
        // Send body length (using int32 for simplicity)
        var bodyLength = BitConverter.GetBytes((uint)messageBody.Length);
        await _stream.WriteAsync(bodyLength);
        
        // Send body
        await _stream.WriteAsync(messageBody);
        await _stream.FlushAsync();
    }
    
    public async Task<DisconnectReason> WaitForDisconnectAsync(TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(10);
        using var timeoutCts = new CancellationTokenSource(timeout.Value);
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeoutCts.Token);
        
        try
        {
            while (IsConnected && !combinedCts.Token.IsCancellationRequested)
            {
                await Task.Delay(100, combinedCts.Token);
                
                // Check if we received a disconnect message
                lock (_receivedData)
                {
                    if (_receivedData.Count > 0)
                    {
                        var lastByte = _receivedData[^1];
                        if (lastByte >= 20 && lastByte <= 39) // Disconnect message range
                        {
                            return (DisconnectReason)lastByte;
                        }
                    }
                }
            }
            
            // If connection closed without explicit disconnect message
            return IsConnected ? DisconnectReason.Timeout : DisconnectReason.SocketError;
        }
        catch (OperationCanceledException)
        {
            return DisconnectReason.Timeout;
        }
    }
    
    public async Task<ServerGreetingMessage?> WaitForServerGreetingAsync(TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(5);
        using var timeoutCts = new CancellationTokenSource(timeout.Value);
        
        try
        {
            while (!timeoutCts.Token.IsCancellationRequested)
            {
                lock (_receivedData)
                {
                    if (_receivedData.Count >= 5) // At least message type + length
                    {
                        if (_receivedData[0] == (byte)MessageType.ServerGreeting)
                        {
                            var bodyLength = BitConverter.ToUInt32(_receivedData.ToArray(), 1);
                            if (_receivedData.Count >= 5 + bodyLength)
                            {
                                var bodyData = _receivedData.Skip(5).Take((int)bodyLength).ToArray();
                                return MemoryPackSerializer.Deserialize<ServerGreetingMessage>(bodyData);
                            }
                        }
                    }
                }
                
                await Task.Delay(50, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout
        }
        
        return null;
    }
    
    private async Task ReadLoop()
    {
        if (_stream == null) return;
        
        var buffer = new byte[4096];
        try
        {
            while (IsConnected && !_cts.Token.IsCancellationRequested)
            {
                var bytesRead = await _stream.ReadAsync(buffer, _cts.Token);
                if (bytesRead == 0) break;
                
                lock (_receivedData)
                {
                    _receivedData.AddRange(buffer.Take(bytesRead));
                }
            }
        }
        catch (Exception)
        {
            // Connection closed or error
        }
    }
    
    private static ulong CreateProtocolHeader(byte protocolVersion, byte reserved1, byte reserved2, byte reserved3, uint protocolTag)
    {
        int high = (protocolVersion << 24) | (reserved1 << 16) | (reserved2 << 8) | reserved3;
        var combined = ((ulong)high << 32) | ((ulong)protocolTag & 0xFFFFFFFF);
        return combined;
    }
    
    public void Dispose()
    {
        _cts.Cancel();
        _stream?.Dispose();
        _tcpClient.Dispose();
        _cts.Dispose();
    }
}
