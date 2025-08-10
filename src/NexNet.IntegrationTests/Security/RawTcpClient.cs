using System.Net;
using System.Net.Sockets;
using MemoryPack;
using NexNet.Invocation;
using NexNet.Logging;
using NexNet.Messages;
using NexNet.Transports;
using NUnit.Framework;
using StreamStruct;
using LogLevel = StreamStruct.LogLevel;

namespace NexNet.IntegrationTests.Security;

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

    public StreamFieldProcessor Processor => _streamProcessor!;

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
    
    public void Write(byte data)
    {
        _logger.LogTrace($"Sending: {data}");
        Assert.DoesNotThrow(() => _stream!.WriteByte(data));
    }
    
    public void Write(byte[] data)
    {
        _logger.LogTrace($"Sending: [{string.Join(",", data)}]");
        Assert.DoesNotThrowAsync(async () => await _stream!.WriteAsync(data));
    }

    public async Task AssertVerify(string definition, object?[]? expectedValues)
    {
        var result = await _streamProcessor!.VerifyAsync(definition, expectedValues);
        Assert.That(result.ValidationErrors, Is.Empty);
    }
    
    public async Task AssertReadSuccess(string definition)
    {
        var result = await _streamProcessor!.ReadAsync(definition);
        Assert.That(result.ErrorCode, Is.EqualTo(ParseError.Success));
    }
    
    public async Task AssertDisconnectReason(DisconnectReason reason)
    {
        await AssertVerify("[type:byte]", [(byte)reason]);
    }

    public readonly string ProtocolMessageDefinition = "[type:byte][body_length:ushort][body:body_length]";
    
    public static readonly string ProtocolHeader =
        "[magByt1:byte][magByt2:byte][magByt3:byte][magByt4:byte][reserved1:byte][reserved2:byte][reserved3:byte][version:byte]";

    public static byte ProtocolVersion = 1;
    private static readonly object[] _protocolHeaderValues =
        [(byte)'N', (byte)'n', (byte)'P', (byte)'\u0014', 0, 0, 0, ProtocolVersion];
    public async Task SendProtocolHeaderAsync(bool badHeader = false, bool badVersion = false)
    {
        object[] values = [(byte)'N', (byte)'n', (byte)'P', (byte)'\u0014', 0, 0, 0, ProtocolVersion];

        if (badHeader)
            values[Random.Shared.NextInt64(0, 7)] = 255;
        
        if(badVersion)
            values[7] = 255;
        
        var result = await _streamProcessor!.WriteAsync(ProtocolHeader, values).Timeout(1);
        
        
        Assert.That(result, Is.True);
    }

    public Task SendClientGreetingMessage<TServer, TClient, TClientProxy>(string? version = null) 
        where TServer : IInvocationMethodHash 
        where TClient : IInvocationMethodHash
        where TClientProxy : IInvocationMethodHash
    {
        var clientHash = IInvocationMethodHash.GetMethodHash<TClient>();
        
        // If no version specified, determine from the client's type name what version it represents
        version ??= IInvocationMethodHash.GetLatestVersionString<TClientProxy>();

        var serverHash = version != null
            ? IInvocationMethodHash.GetVersionHashTable<TServer>()[version]
            : IInvocationMethodHash.GetMethodHash<TServer>();

        return SendClientGreetingMessage(version, clientHash, serverHash);
    }
    
    public async Task SendClientGreetingMessage(string? version, int clientHash, int serverHash) 
    {
        var message = new ClientGreetingMessage()
        {
            ServerNexusHash = serverHash,
            ClientNexusHash = clientHash,
            Version = version // Would be a valid version in versioned server
        };
        await SendMessageAsync(message).Timeout(1);
    }
    
    public async Task ReadProtocolHeaderAsync()
    {
        if (Stream == null) 
            throw new InvalidOperationException("Not connected");
        await AssertVerify(ProtocolHeader, _protocolHeaderValues);
    }
    
    public async Task AssertWrite(string definition, object?[] data)
    {
        if (Stream == null) 
            throw new InvalidOperationException("Not connected");
        
        var result = await _streamProcessor!.WriteAsync(definition, data).Timeout(1);
        Assert.That(result, Is.True);
    } 
    
    public async Task<TMessage?> AssertReceiveMessageAsync<TMessage>()
        where TMessage : class, IMessageBase
    {
        
        var typeResult = await _streamProcessor!.ReadAsync("[type:byte]");
        Assert.That(typeResult.ErrorCode, Is.EqualTo(ParseError.Success));
        Assert.That(typeResult.TryRead<byte>("type", out var typeValue), Is.True);
        Assert.That(typeValue, Is.EqualTo((byte)TMessage.Type));
        
        var bodyResult = await _streamProcessor!.ReadAsync("[body_length:ushort][body:body_length]");
        Assert.That(bodyResult.ErrorCode, Is.EqualTo(ParseError.Success));
        Assert.That(bodyResult.TryRead<byte[]>("body", out var bodyValue), Is.True);
        TMessage? message = null;
        Assert.DoesNotThrow(() => message = MemoryPackSerializer.Deserialize<TMessage>(bodyValue));

        return message;
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
    
    public async Task SendMessageAsync<TMessage>(TMessage message)
        where TMessage : IMessageBase
    {
        
        if (Stream == null) 
            throw new InvalidOperationException("Not connected");

        var messageBody = MemoryPackSerializer.Serialize(message);
        
        await AssertWrite(ProtocolMessageDefinition, [
            (byte)TMessage.Type,
            (ushort)messageBody.Length,
            messageBody
        ]).Timeout(1);
    }

    /// <summary>
    /// Forces an immediate disconnect by closing the underlying TCP connection.
    /// This simulates network failures or abrupt disconnections for testing purposes.
    /// </summary>
    public void ForceDisconnect()
    {
        try
        {
            _cts.Cancel();
            
            // Close the stream first
            _stream?.Close();
            
            // Then close the TCP client connection
            _tcpClient.Close();
        }
        catch
        {
            // Ignore exceptions during forced disconnect
            // as this is meant to simulate abrupt network failures
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        Stream?.Dispose();
        _tcpClient.Dispose();
        _cts.Dispose();
    }
}
