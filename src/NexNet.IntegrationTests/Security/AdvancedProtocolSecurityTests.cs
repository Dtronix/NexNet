using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MemoryPack;
using NexNet.Messages;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Security;

/// <summary>
/// Advanced security tests covering sophisticated attack scenarios and edge cases
/// that could potentially bypass NexNet's protocol security measures.
/// </summary>
[TestFixture]
public class AdvancedProtocolSecurityTests : BaseTests
{
    /// <summary>
    /// Tests that rapid connection attempts cannot overwhelm the server's handshake validation.
    /// </summary>
    [TestCase(Type.Tcp)]
    public async Task RapidConnectionFlood_ShouldNotOverwhelm_Server(Type type)
    {
        var (server, _, _, _, configs) = await Setup(type);
        
        const int connectionCount = 50;
        var connectionTasks = new List<Task>();
        var results = new List<DisconnectReason>();
        var resultsLock = new object();
        
        // Create many concurrent malicious connections
        for (int i = 0; i < connectionCount; i++)
        {
            connectionTasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var client = new RawTcpClient(configs, false);
                    await client.ConnectAsync();
                    await client.SendProtocolHeaderAsync();
                    
                    // Send invalid message immediately
                    await client.SendRawAsync(new byte[] { 255 }); // Invalid message type
                    
                    var reason = await client.WaitForDisconnectAsync(TimeSpan.FromSeconds(5));
                    lock (resultsLock)
                    {
                        results.Add(reason);
                    }
                }
                catch (Exception)
                {
                    // Connection might fail, which is expected behavior
                    lock (resultsLock)
                    {
                        results.Add(DisconnectReason.SocketError);
                    }
                }
            }));
        }
        
        await Task.WhenAll(connectionTasks);
        
        // Verify all connections were properly rejected
        Assert.That(results.Count, Is.EqualTo(connectionCount));
        Assert.That(results.All(r => r == DisconnectReason.ProtocolError || r == DisconnectReason.SocketError), Is.True);
        
        // Verify server is still responsive
        Assert.That(server.IsListening, Is.True);
    }
    
    /// <summary>
    /// Tests that partial protocol headers don't cause the server to hang or crash.
    /// </summary>
    [TestCase(Type.Tcp)]
    public async Task PartialProtocolHeader_ShouldTimeoutGracefully(Type type)
    {
        var (server, _, _, _, configs) = await Setup(type);
        
        using var client = new RawTcpClient(configs, false);
        await client.ConnectAsync();
        
        // Send only part of protocol header (4 bytes instead of 8)
        var partialHeader = new byte[] { 0x14, 0x50, 0x4E, 0x4E };
        await client.SendRawAsync(partialHeader);
        
        // Server should timeout and disconnect
        var disconnectReason = await client.WaitForDisconnectAsync(TimeSpan.FromSeconds(30));
        Assert.That(disconnectReason, Is.EqualTo(DisconnectReason.Timeout));
    }
    
    /// <summary>
    /// Tests that fragmented messages don't bypass protocol validation.
    /// </summary>
    [TestCase(Type.Tcp)]
    public async Task FragmentedMaliciousMessage_ShouldBeRejected(Type type)
    {
        var (server, _, _, _, configs) = await Setup(type);
        
        using var client = new RawTcpClient(configs, false);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        
        // Send invocation message in fragments to try to bypass validation
        var invocationMessage = CreateMaliciousInvocationMessage();
        
        // Send message type
        await client.SendRawAsync(new[] { (byte)MessageType.Invocation });
        await Task.Delay(10); // Small delay
        
        // Send body length
        var bodyLength = BitConverter.GetBytes((uint)invocationMessage.Length);
        await client.SendRawAsync(bodyLength);
        await Task.Delay(10); // Small delay
        
        // Send body in small fragments
        for (int i = 0; i < invocationMessage.Length; i += 8)
        {
            var fragmentSize = Math.Min(8, invocationMessage.Length - i);
            var fragment = new byte[fragmentSize];
            Array.Copy(invocationMessage, i, fragment, 0, fragmentSize);
            await client.SendRawAsync(fragment);
            await Task.Delay(5); // Small delay between fragments
        }
        
        // Server should still reject with ProtocolError
        var disconnectReason = await client.WaitForDisconnectAsync();
        Assert.That(disconnectReason, Is.EqualTo(DisconnectReason.ProtocolError));
    }
    
    /// <summary>
    /// Tests that sending valid protocol header followed by garbage data results in disconnection.
    /// </summary>
    [TestCase(Type.Tcp)]
    public async Task ValidHeaderFollowedByGarbage_ShouldDisconnect(Type type)
    {
        var (server, _, _, _, configs) = await Setup(type);
        
        using var client = new RawTcpClient(configs, false);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        
        // Send random garbage data
        var garbage = new byte[1024];
        new Random().NextBytes(garbage);
        await client.SendRawAsync(garbage);
        
        // Server should disconnect with ProtocolError
        var disconnectReason = await client.WaitForDisconnectAsync();
        Assert.That(disconnectReason, Is.EqualTo(DisconnectReason.ProtocolError));
    }
    
    /// <summary>
    /// Tests that attempting to exploit message length fields doesn't cause crashes.
    /// </summary>
    [TestCase(Type.Tcp)]
    public async Task MaliciousMessageLengths_ShouldBeHandledSafely(Type type)
    {
        var (server, _, _, _, configs) = await Setup(type);
        
        var testCases = new[]
        {
            uint.MaxValue,     // Maximum value
            0x7FFFFFFF,        // Large positive value
            0,                 // Zero length
            1                  // Minimal length
        };
        
        foreach (var maliciousLength in testCases)
        {
            using var client = new RawTcpClient(configs, false);
            await client.ConnectAsync();
            
            // Send valid protocol header
            await client.SendProtocolHeaderAsync();
            
            // Send message with malicious length
            await client.SendRawAsync(new[] { (byte)MessageType.ClientGreeting });
            await client.SendRawAsync(BitConverter.GetBytes(maliciousLength));
            
            // Send some data (but not the full amount claimed)
            if (maliciousLength > 0 && maliciousLength < 1024)
            {
                var data = new byte[Math.Min(maliciousLength, 1024)];
                await client.SendRawAsync(data);
            }
            
            // Server should handle this gracefully
            var disconnectReason = await client.WaitForDisconnectAsync();
            Assert.That(disconnectReason, Is.Not.EqualTo(DisconnectReason.None));
        }
    }
    
    /// <summary>
    /// Tests that attempting to send messages before protocol confirmation is rejected.
    /// </summary>
    [TestCase(Type.Tcp)]
    public async Task MessagesBeforeProtocolConfirmation_ShouldBeRejected(Type type)
    {
        var (server, _, _, _, configs) = await Setup(type);
        
        using var client = new RawTcpClient(configs, false);
        await client.ConnectAsync();
        
        // DON'T send protocol header, try to send messages directly
        var greeting = CreateValidClientGreeting();
        await client.SendMessageAsync(MessageType.ClientGreeting, greeting);
        
        // Server should disconnect due to missing protocol header
        var disconnectReason = await client.WaitForDisconnectAsync();
        Assert.That(disconnectReason, Is.EqualTo(DisconnectReason.ProtocolError));
    }
    
    /// <summary>
    /// Tests that the server properly validates message ordering.
    /// </summary>
    [TestCase(Type.Tcp)]
    public async Task OutOfOrderMessages_ShouldBeRejected(Type type)
    {
        var (server, _, _, _, configs) = await Setup(type);
        
        using var client = new RawTcpClient(configs, false);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        
        // Send ServerGreeting instead of ClientGreeting (wrong order/role)
        var serverGreeting = new ServerGreetingMessage { Version = 0, ClientId = 12345 };
        var serialized = MemoryPackSerializer.Serialize(serverGreeting);
        await client.SendMessageAsync(MessageType.ServerGreeting, serialized);
        
        // Server should reject this as it's expecting ClientGreeting
        var disconnectReason = await client.WaitForDisconnectAsync();
        Assert.That(disconnectReason, Is.EqualTo(DisconnectReason.ProtocolError));
    }
    
    /// <summary>
    /// Tests that connection state is properly managed during attacks.
    /// </summary>
    [TestCase(Type.Tcp)]
    public async Task ConnectionStateConsistency_DuringAttack(Type type)
    {
        var (server, _, _, _, configs) = await Setup(type);
        
        // Establish legitimate connection first
        using var legitimateClient = new RawTcpClient(configs, false);
        await legitimateClient.ConnectAsync();
        await legitimateClient.SendProtocolHeaderAsync();
        
        var greeting = CreateValidClientGreeting();
        await legitimateClient.SendMessageAsync(MessageType.ClientGreeting, greeting);
        
        var serverGreeting = await legitimateClient.WaitForServerGreetingAsync();
        Assert.That(serverGreeting, Is.Not.Null, "Legitimate connection should succeed");
        
        // Now launch attack
        using var attackClient = new RawTcpClient(configs, false);
        await attackClient.ConnectAsync();
        await attackClient.SendRawAsync(new byte[] { 255 }); // Invalid data
        
        var attackResult = await attackClient.WaitForDisconnectAsync();
        Assert.That(attackResult, Is.EqualTo(DisconnectReason.ProtocolError));
        
        // Verify legitimate connection is unaffected
        Assert.That(legitimateClient.IsConnected, Is.True);
        
        // Verify server is still responsive
        Assert.That(server.IsListening, Is.True);
    }
    
    /// <summary>
    /// Tests that malformed MemoryPack data doesn't cause exceptions.
    /// </summary>
    [TestCase(Type.Tcp)]
    public async Task MalformedMemoryPackData_ShouldBeHandledSafely(Type type)
    {
        var (server, _, _, _, configs) = await Setup(type);
        
        using var client = new RawTcpClient(configs, false);
        await client.ConnectAsync();
        
        // Send valid protocol header
        await client.SendProtocolHeaderAsync();
        
        // Send ClientGreeting message type with malformed MemoryPack data
        await client.SendRawAsync(new[] { (byte)MessageType.ClientGreeting });
        
        var malformedData = new byte[] { 0xFF, 0xFE, 0xFD, 0xFC, 0xFB, 0xFA, 0xF9, 0xF8 };
        var bodyLength = BitConverter.GetBytes((uint)malformedData.Length);
        
        await client.SendRawAsync(bodyLength);
        await client.SendRawAsync(malformedData);
        
        // Server should handle deserialization failure gracefully
        var disconnectReason = await client.WaitForDisconnectAsync();
        Assert.That(disconnectReason, Is.EqualTo(DisconnectReason.ProtocolError));
    }
    
    private byte[] CreateValidClientGreeting()
    {
        var greeting = new ClientGreetingMessage
        {
            Version = 0,
            ServerNexusMethodHash = typeof(BasicTestsServerNexus).GetHashCode(),
            ClientNexusMethodHash = typeof(BasicTestsClientNexus).GetHashCode(),
            AuthenticationToken = Memory<byte>.Empty
        };
        
        return MemoryPackSerializer.Serialize(greeting);
    }
    
    private byte[] CreateMaliciousInvocationMessage()
    {
        var invocation = new InvocationMessage
        {
            InvocationId = 1,
            MethodId = 999, // Non-existent method
            Arguments = Memory<byte>.Empty
        };
        
        return MemoryPackSerializer.Serialize(invocation);
    }
}

/// <summary>
/// Extension of RawTcpClient with additional testing capabilities for advanced scenarios.
/// </summary>
public static class RawTcpClientExtensions
{
    /// <summary>
    /// Sends data in very small fragments to test fragmentation handling.
    /// </summary>
    public static async Task SendFragmentedAsync(this RawTcpClient client, byte[] data, int fragmentSize = 1)
    {
        for (int i = 0; i < data.Length; i += fragmentSize)
        {
            var size = Math.Min(fragmentSize, data.Length - i);
            var fragment = new byte[size];
            Array.Copy(data, i, fragment, 0, size);
            await client.SendRawAsync(fragment);
            await Task.Delay(1); // Small delay between fragments
        }
    }
    
    /// <summary>
    /// Sends data with random delays to test timing-based vulnerabilities.
    /// </summary>
    public static async Task SendWithRandomDelaysAsync(this RawTcpClient client, byte[] data)
    {
        var random = new Random();
        for (int i = 0; i < data.Length; i++)
        {
            await client.SendRawAsync(new[] { data[i] });
            if (random.Next(10) == 0) // 10% chance of delay
            {
                await Task.Delay(random.Next(1, 10));
            }
        }
    }
}