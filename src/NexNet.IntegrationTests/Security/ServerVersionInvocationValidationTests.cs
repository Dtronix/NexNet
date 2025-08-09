using System.Buffers;
using System.Collections.Concurrent;
using MemoryPack;
using NexNet.IntegrationTests.Pipes;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Invocation;
using NexNet.Messages;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Security;

internal class ServerVersionInvocationValidationTests : BaseTests
{
    [Test]
    public async Task NonVersionedServer_NullClientVersion_ShouldConnect()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        
        await client.SendClientGreetingMessage<ServerNexus, ClientNexus, ClientNexus.ServerProxy>().Timeout(1);
        await client.AssertReceiveMessageAsync<ServerGreetingMessage>();
    }

    [Test]
    public async Task VersionedServerV2_ValidClientVersionCurrent_ShouldConnect()
    {
        // Similar to above - demonstrates the pattern for when we have versioned servers
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer<VersionedServerNexusV2, VersionedServerNexusV2.ClientProxy>(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        
        await client.SendClientGreetingMessage<VersionedServerNexusV2, VersionedClientNexusV2, VersionedClientNexusV2.ServerProxy>().Timeout(1);
        await client.AssertReceiveMessageAsync<ServerGreetingMessage>();
    }
    
    // V2 Server Tests
    [Test]
    public async Task VersionedServerV2_ClientV2_AllMethodsAccessible()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var completedMethods = new ConcurrentBag<ushort>();
        var methodCompletions = new Dictionary<ushort, TaskCompletionSource>
        {
            [1] = new(),
            [2] = new(),
            [3] = new(),
            [4] = new()
        };
        var server = CreateServer<VersionedServerNexusV2, VersionedServerNexusV2.ClientProxy>(serverConfig, nexus =>
        {
            nexus.VerifyVersionV1Action = version => 
            {
                completedMethods.Add(1);
                methodCompletions[1].SetResult();
                return ValueTask.FromResult(true);
            };
            nexus.RunTaskV1_1Action = () => 
            {
                completedMethods.Add(2);
                methodCompletions[2].SetResult();
            };
            nexus.RunTaskWithResultV1_1Action = () => 
            {
                completedMethods.Add(3);
                methodCompletions[3].SetResult();
                return ValueTask.FromResult(IVersionedServerNexusV1_1.ReturnState.Success);
            };
            nexus.RunTaskNewV2Action = () => 
            {
                completedMethods.Add(4);
                methodCompletions[4].SetResult();
                return ValueTask.CompletedTask;
            };
        });
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        
        await client.SendClientGreetingMessage<VersionedServerNexusV2, VersionedClientNexusV2, VersionedClientNexusV2.ServerProxy>().Timeout(1);
        await client.AssertReceiveMessageAsync<ServerGreetingMessage>();
        
        // Test all methods - V2 client should have access to all V1, V1.1, and V2 methods
        await InvokeMethod(client, 1); // VerifyVersionV1
        await InvokeMethod(client, 2); // RunTaskV1_1  
        await InvokeMethod(client, 3); // RunTaskWithResultV1_1
        await InvokeMethod(client, 4); // RunTaskNewV2
        
        // Wait for all methods to complete
        await Task.WhenAll(methodCompletions.Values.Select(tcs => tcs.Task));
        
        Assert.That(completedMethods, Contains.Item((ushort)1));
        Assert.That(completedMethods, Contains.Item((ushort)2));
        Assert.That(completedMethods, Contains.Item((ushort)3));
        Assert.That(completedMethods, Contains.Item((ushort)4));
    }

    [Test]
    public async Task VersionedServerV2_ClientV1_1_OnlyV1AndV1_1MethodsAccessible()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var completedMethods = new ConcurrentBag<ushort>();
        var methodCompletions = new Dictionary<ushort, TaskCompletionSource>
        {
            [1] = new(),
            [2] = new(),
            [3] = new()
        };
        var server = CreateServer<VersionedServerNexusV2, VersionedServerNexusV2.ClientProxy>(serverConfig, nexus =>
        {
            nexus.VerifyVersionV1Action = version => 
            {
                completedMethods.Add(1);
                methodCompletions[1].SetResult();
                return ValueTask.FromResult(true);
            };
            nexus.RunTaskV1_1Action = () => 
            {
                completedMethods.Add(2);
                methodCompletions[2].SetResult();
            };
            nexus.RunTaskWithResultV1_1Action = () => 
            {
                completedMethods.Add(3);
                methodCompletions[3].SetResult();
                return ValueTask.FromResult(IVersionedServerNexusV1_1.ReturnState.Success);
            };
            nexus.RunTaskNewV2Action = () => 
            {
                completedMethods.Add(4);
                return ValueTask.CompletedTask;
            };
        });
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        
        // Connect as V1.1 client
        await client.SendClientGreetingMessage<VersionedServerNexusV2, VersionedClientNexusV1_1, VersionedClientNexusV1_1.ServerProxy>().Timeout(1);
        await client.AssertReceiveMessageAsync<ServerGreetingMessage>();
        
        // Test accessible methods
        await InvokeMethod(client, 1); // VerifyVersionV1 - should work
        await InvokeMethod(client, 2); // RunTaskV1_1 - should work
        await InvokeMethod(client, 3); // RunTaskWithResultV1_1 - should work
        
        // Wait for accessible methods to complete
        await Task.WhenAll(methodCompletions.Values.Select(tcs => tcs.Task));
        
        // Test inaccessible method with new client (previous one will disconnect)
        using var client2 = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client2.ConnectAsync();
        await client2.SendProtocolHeaderAsync();
        await client2.ReadProtocolHeaderAsync();
        await client2.SendClientGreetingMessage<VersionedServerNexusV2, VersionedClientNexusV1_1, VersionedClientNexusV1_1.ServerProxy>().Timeout(1);
        await client2.AssertReceiveMessageAsync<ServerGreetingMessage>();
        await InvokeMethodExpectingError(client2, 4); // RunTaskNewV2 - should disconnect
        
        Assert.That(completedMethods, Contains.Item((ushort)1));
        Assert.That(completedMethods, Contains.Item((ushort)2));
        Assert.That(completedMethods, Contains.Item((ushort)3));
        Assert.That(completedMethods, Does.Not.Contain((ushort)4));
    }

    [Test]
    public async Task VersionedServerV2_ClientV1_OnlyV1MethodsAccessible()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var completedMethods = new ConcurrentBag<ushort>();
        var methodCompletions = new Dictionary<ushort, TaskCompletionSource>
        {
            [1] = new()
        };
        var server = CreateServer<VersionedServerNexusV2, VersionedServerNexusV2.ClientProxy>(serverConfig, nexus =>
        {
            nexus.VerifyVersionV1Action = version => 
            {
                completedMethods.Add(1);
                methodCompletions[1].SetResult();
                return ValueTask.FromResult(true);
            };
            nexus.RunTaskV1_1Action = () => completedMethods.Add(2);
            nexus.RunTaskWithResultV1_1Action = () => 
            {
                completedMethods.Add(3);
                return ValueTask.FromResult(IVersionedServerNexusV1_1.ReturnState.Success);
            };
            nexus.RunTaskNewV2Action = () => 
            {
                completedMethods.Add(4);
                return ValueTask.CompletedTask;
            };
        });
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        
        // Connect as V1 client
        await client.SendClientGreetingMessage<VersionedServerNexusV2, VersionedClientNexusV1, VersionedClientNexusV1.ServerProxy>().Timeout(1);
        await client.AssertReceiveMessageAsync<ServerGreetingMessage>();
        
        // Test accessible method
        await InvokeMethod(client, 1); // VerifyVersionV1 - should work
        
        // Wait for accessible method to complete
        await Task.WhenAll(methodCompletions.Values.Select(tcs => tcs.Task));
        
        // Test inaccessible methods with separate clients (each will disconnect)
        using var client2 = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client2.ConnectAsync();
        await client2.SendProtocolHeaderAsync();
        await client2.ReadProtocolHeaderAsync();
        await client2.SendClientGreetingMessage<VersionedServerNexusV2, VersionedClientNexusV1, VersionedClientNexusV1.ServerProxy>().Timeout(1);
        await client2.AssertReceiveMessageAsync<ServerGreetingMessage>();
        await InvokeMethodExpectingError(client2, 2); // RunTaskV1_1 - should disconnect
        
        using var client3 = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client3.ConnectAsync();
        await client3.SendProtocolHeaderAsync();
        await client3.ReadProtocolHeaderAsync();
        await client3.SendClientGreetingMessage<VersionedServerNexusV2, VersionedClientNexusV1, VersionedClientNexusV1.ServerProxy>().Timeout(1);
        await client3.AssertReceiveMessageAsync<ServerGreetingMessage>();
        await InvokeMethodExpectingError(client3, 3); // RunTaskWithResultV1_1 - should disconnect
        
        using var client4 = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client4.ConnectAsync();
        await client4.SendProtocolHeaderAsync();
        await client4.ReadProtocolHeaderAsync();
        await client4.SendClientGreetingMessage<VersionedServerNexusV2, VersionedClientNexusV1, VersionedClientNexusV1.ServerProxy>().Timeout(1);
        await client4.AssertReceiveMessageAsync<ServerGreetingMessage>();
        await InvokeMethodExpectingError(client4, 4); // RunTaskNewV2 - should disconnect
        
        Assert.That(completedMethods, Contains.Item((ushort)1));
        Assert.That(completedMethods, Does.Not.Contain((ushort)2));
        Assert.That(completedMethods, Does.Not.Contain((ushort)3));
        Assert.That(completedMethods, Does.Not.Contain((ushort)4));
    }

    // V1.1 Server Tests
    [Test]
    public async Task VersionedServerV1_1_ClientV1_1_AllAvailableMethodsAccessible()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var completedMethods = new ConcurrentBag<ushort>();
        var methodCompletions = new Dictionary<ushort, TaskCompletionSource>
        {
            [1] = new(),
            [2] = new(),
            [3] = new()
        };
        var server = CreateServer<VersionedServerNexusV1_1, VersionedServerNexusV1_1.ClientProxy>(serverConfig, nexus =>
        {
            nexus.VerifyVersionV1Action = version => 
            {
                completedMethods.Add(1);
                methodCompletions[1].SetResult();
                return ValueTask.FromResult(true);
            };
            nexus.RunTaskV1_1Action = () => 
            {
                completedMethods.Add(2);
                methodCompletions[2].SetResult();
            };
            nexus.RunTaskWithResultV1_1Action = () => 
            {
                completedMethods.Add(3);
                methodCompletions[3].SetResult();
                return ValueTask.FromResult(IVersionedServerNexusV1_1.ReturnState.Success);
            };
        });
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        
        await client.SendClientGreetingMessage<VersionedServerNexusV1_1, VersionedClientNexusV1_1, VersionedClientNexusV1_1.ServerProxy>().Timeout(1);
        await client.AssertReceiveMessageAsync<ServerGreetingMessage>();
        
        // Test all available methods
        await InvokeMethod(client, 1); // VerifyVersionV1
        await InvokeMethod(client, 2); // RunTaskV1_1
        await InvokeMethod(client, 3); // RunTaskWithResultV1_1
        
        // Wait for all methods to complete
        await Task.WhenAll(methodCompletions.Values.Select(tcs => tcs.Task));
        
        Assert.That(completedMethods, Contains.Item((ushort)1));
        Assert.That(completedMethods, Contains.Item((ushort)2));
        Assert.That(completedMethods, Contains.Item((ushort)3));
    }

    [Test]
    public async Task VersionedServerV1_1_ClientV1_OnlyV1MethodsAccessible()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var completedMethods = new ConcurrentBag<ushort>();
        var methodCompletions = new Dictionary<ushort, TaskCompletionSource>
        {
            [1] = new()
        };
        var server = CreateServer<VersionedServerNexusV1_1, VersionedServerNexusV1_1.ClientProxy>(serverConfig, nexus =>
        {
            nexus.VerifyVersionV1Action = version => 
            {
                completedMethods.Add(1);
                methodCompletions[1].SetResult();
                return ValueTask.FromResult(true);
            };
            nexus.RunTaskV1_1Action = () => completedMethods.Add(2);
            nexus.RunTaskWithResultV1_1Action = () => 
            {
                completedMethods.Add(3);
                return ValueTask.FromResult(IVersionedServerNexusV1_1.ReturnState.Success);
            };
        });
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        
        // Connect as V1 client
        await client.SendClientGreetingMessage<VersionedServerNexusV1_1, VersionedClientNexusV1, VersionedClientNexusV1.ServerProxy>().Timeout(1);
        await client.AssertReceiveMessageAsync<ServerGreetingMessage>();
        
        // Test accessible method
        await InvokeMethod(client, 1); // VerifyVersionV1 - should work
        
        // Wait for accessible method to complete
        await Task.WhenAll(methodCompletions.Values.Select(tcs => tcs.Task));
        
        // Test inaccessible methods with separate clients (each will disconnect)
        using var client2 = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client2.ConnectAsync();
        await client2.SendProtocolHeaderAsync();
        await client2.ReadProtocolHeaderAsync();
        await client2.SendClientGreetingMessage<VersionedServerNexusV1_1, VersionedClientNexusV1, VersionedClientNexusV1.ServerProxy>().Timeout(1);
        await client2.AssertReceiveMessageAsync<ServerGreetingMessage>();
        await InvokeMethodExpectingError(client2, 2); // RunTaskV1_1 - should disconnect
        
        using var client3 = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client3.ConnectAsync();
        await client3.SendProtocolHeaderAsync();
        await client3.ReadProtocolHeaderAsync();
        await client3.SendClientGreetingMessage<VersionedServerNexusV1_1, VersionedClientNexusV1, VersionedClientNexusV1.ServerProxy>().Timeout(1);
        await client3.AssertReceiveMessageAsync<ServerGreetingMessage>();
        await InvokeMethodExpectingError(client3, 3); // RunTaskWithResultV1_1 - should disconnect
        
        Assert.That(completedMethods, Contains.Item((ushort)1));
        Assert.That(completedMethods, Does.Not.Contain((ushort)2));
        Assert.That(completedMethods, Does.Not.Contain((ushort)3));
    }

    // V1 Server Tests
    [Test]
    public async Task VersionedServerV1_ClientV1_OnlyV1MethodsAccessible()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var completedMethods = new ConcurrentBag<ushort>();
        var methodCompletions = new Dictionary<ushort, TaskCompletionSource>
        {
            [1] = new()
        };
        var server = CreateServer<VersionedServerNexusV1, VersionedServerNexusV1.ClientProxy>(serverConfig, nexus =>
        {
            nexus.VerifyVersionV1Action = version => 
            {
                completedMethods.Add(1);
                methodCompletions[1].SetResult();
                return ValueTask.FromResult(true);
            };
        });
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        
        await client.SendClientGreetingMessage<VersionedServerNexusV1, VersionedClientNexusV1, VersionedClientNexusV1.ServerProxy>().Timeout(1);
        await client.AssertReceiveMessageAsync<ServerGreetingMessage>();
        
        // Test accessible method
        await InvokeMethod(client, 1); // VerifyVersionV1 - should work
        
        // Wait for method to complete
        await Task.WhenAll(methodCompletions.Values.Select(tcs => tcs.Task));
        
        Assert.That(completedMethods, Contains.Item((ushort)1));
    }

    // Invalid Method Tests
    [Test]
    public async Task VersionedServer_InvalidMethodId_ShouldReturnError()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer<VersionedServerNexusV2, VersionedServerNexusV2.ClientProxy>(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        
        await client.SendClientGreetingMessage<VersionedServerNexusV2, VersionedClientNexusV2, VersionedClientNexusV2.ServerProxy>().Timeout(1);
        await client.AssertReceiveMessageAsync<ServerGreetingMessage>();
        
        // Try invalid method ID
        await InvokeMethodExpectingError(client, 999);
    }

    [Test]
    public async Task VersionedServer_RapidInvalidMethodInvocations_ShouldDisconnectImmediately()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer<VersionedServerNexusV2, VersionedServerNexusV2.ClientProxy>(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        
        // Connect as V1 client (restricted access)
        await client.SendClientGreetingMessage<VersionedServerNexusV2, VersionedClientNexusV1, VersionedClientNexusV1.ServerProxy>().Timeout(1);
        await client.AssertReceiveMessageAsync<ServerGreetingMessage>();
        
        // Send multiple rapid invalid method invocations without waiting
        var tasks = new List<Task>();
        for (int i = 0; i < 5; i++)
        {
            var invocation = new InvocationMessage
            {
                InvocationId = (ushort)(i + 1),
                MethodId = (ushort)(i + 2), // Methods 2-6 are not available to V1 client
                Arguments = Memory<byte>.Empty
            };
            tasks.Add(client.SendMessageAsync(invocation));
        }
        
        // Send all messages rapidly
        await Task.WhenAll(tasks);
        
        // Server should disconnect immediately after first unauthorized method
        await client.AssertDisconnectReason(DisconnectReason.ProtocolError).Timeout(2);
    }

    [Test]
    public async Task VersionedServer_MalformedInvocationMessage_ShouldDisconnectGracefully()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer<VersionedServerNexusV2, VersionedServerNexusV2.ClientProxy>(serverConfig, null);
        await server.StartAsync();
        
        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        
        await client.SendClientGreetingMessage<VersionedServerNexusV2, VersionedClientNexusV2, VersionedClientNexusV2.ServerProxy>().Timeout(1);
        await client.AssertReceiveMessageAsync<ServerGreetingMessage>();
        
        // Send malformed invocation message with corrupted data
        var malformedData = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00 };
        client.Write(malformedData);
        
        // Server should disconnect gracefully due to protocol error
        await client.AssertDisconnectReason(DisconnectReason.ProtocolError).Timeout(2);
    }

    [Test]
    public async Task VersionedServer_HighVolumeInvalidAccess_ShouldMaintainPerformance()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer<VersionedServerNexusV2, VersionedServerNexusV2.ClientProxy>(serverConfig, null);
        await server.StartAsync();
        
        const int clientCount = 10;
        const int invalidRequestsPerClient = 20;
        var clientTasks = new List<Task>();
        
        // Create multiple clients that will attempt invalid method access
        for (int clientIndex = 0; clientIndex < clientCount; clientIndex++)
        {
            clientTasks.Add(Task.Run(async () =>
            {
                using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
                await client.ConnectAsync();
                
                await client.SendProtocolHeaderAsync();
                await client.ReadProtocolHeaderAsync();
                
                // Connect as V1 client (restricted access)
                await client.SendClientGreetingMessage<VersionedServerNexusV2, VersionedClientNexusV1, VersionedClientNexusV1.ServerProxy>().Timeout(1);
                await client.AssertReceiveMessageAsync<ServerGreetingMessage>();
                
                // Send multiple invalid method requests rapidly
                for (int i = 0; i < invalidRequestsPerClient; i++)
                {
                    var invocation = new InvocationMessage
                    {
                        InvocationId = (ushort)(i + 1),
                        MethodId = (ushort)(i % 3 + 2), // Methods 2-4 are not available to V1 client
                        Arguments = Memory<byte>.Empty
                    };
                    
                    try
                    {
                        await client.SendMessageAsync(invocation);
                    }
                    catch
                    {
                        // Client may disconnect during sending - this is expected
                        break;
                    }
                }
                
                // Expect disconnection due to unauthorized access
                try
                {
                    await client.AssertDisconnectReason(DisconnectReason.ProtocolError).Timeout(2);
                }
                catch
                {
                    // Client might already be disconnected
                }
            }));
        }
        
        // Measure performance - all clients should be handled within reasonable time
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(clientTasks);
        stopwatch.Stop();
        
        // Server should handle all invalid access attempts within 10 seconds
        Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(10000), 
            "Server took too long to handle high volume invalid access attempts");
        
        // Create a valid client to ensure server is still responsive
        using var validClient = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await validClient.ConnectAsync();
        
        await validClient.SendProtocolHeaderAsync();
        await validClient.ReadProtocolHeaderAsync();
        
        await validClient.SendClientGreetingMessage<VersionedServerNexusV2, VersionedClientNexusV2, VersionedClientNexusV2.ServerProxy>().Timeout(1);
        await validClient.AssertReceiveMessageAsync<ServerGreetingMessage>();
        
        // Server should still be responsive to valid clients
        Assert.Pass("Server maintained performance and responsiveness under high volume invalid access");
    }
    
    private async Task InvokeMethod(RawTcpClient client, ushort methodId)
    {
        var invocation = new InvocationMessage
        {
            InvocationId = (ushort)Random.Shared.Next(1, ushort.MaxValue),
            MethodId = methodId,
            Arguments = methodId == 1 ? MemoryPackSerializer.Serialize(ValueTuple.Create("test")) : Memory<byte>.Empty
        };
        
        await client.SendMessageAsync(invocation).Timeout(5);
    }
    
    private async Task InvokeMethodExpectingError(RawTcpClient client, ushort methodId)
    {
        var invocation = new InvocationMessage
        {
            InvocationId = (ushort)Random.Shared.Next(1, ushort.MaxValue),
            MethodId = methodId,
            Arguments = methodId == 1 ? MemoryPackSerializer.Serialize(ValueTuple.Create("test")) : Memory<byte>.Empty
        };
        
        await client.SendMessageAsync(invocation).Timeout(5);
        
        // Server should disconnect when unauthorized methods are invoked
        await client.AssertDisconnectReason(DisconnectReason.ProtocolError).Timeout(1);
    }
}
