﻿using NexNet.Pipes;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes;

internal class NexusClientTests_ChanneReaderIAsyncEnumerable : BasePipeTests
{
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task IAsyncEnumerable(Type type)
    {
        var (server, _, cNexus, _) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();
        var tcs = new TaskCompletionSource();
        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var writer = await pipe.GetUnmanagedChannelWriter<int>();
            int counter = 0;
            await foreach (var _ in await pipe.GetChannelReader<ComplexMessage>())
            {
                await writer.WriteAsync(counter++);
            }
        };

        var pipe = cNexus.Context.CreatePipe();
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);
        await pipe.ReadyTask.Timeout(1);
        int counter = 0;

        _ = Task.Run(async () =>
        {
            await foreach (var _ in await pipe.GetUnmanagedChannelReader<int>())
            {
                Interlocked.Increment(ref counter);
                if(counter == 10)
                    tcs.SetResult();
                
            }
        });
        
        var writer = await pipe.GetChannelWriter<ComplexMessage>();
        for (int i = 0; i < 10; i++)
        {
            await writer.WriteAsync(ComplexMessage.Random());
            await Task.Delay(1);
        }
        await writer.CompleteAsync().ConfigureAwait(false);
        await tcs.Task.Timeout(1);
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task StopsOnCompletion(Type type)
    {
        var (server, _, cNexus, _) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();
        sNexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            var writer = await pipe.GetUnmanagedChannelWriter<int>();
            await writer.WriteAsync(1);
        };

        var pipe = cNexus.Context.CreatePipe();
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);
        await pipe.ReadyTask.Timeout(1);
        var messageReceivedCount = 0;
        
        var cts = new CancellationTokenSource(1000);
        await foreach (var _ in (await pipe.GetUnmanagedChannelReader<int>()).WithCancellation(cts.Token))
        {
            messageReceivedCount++;
        }
        await pipe.CompleteTask.Timeout(1);
        
        Assert.That(messageReceivedCount, Is.EqualTo(1));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task CancelsRead(Type type)
    {
        var (server, _, cNexus, _) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();
        sNexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            var writer = await pipe.GetUnmanagedChannelWriter<int>();
            await Task.Delay(200);
            await writer.WriteAsync(1);
        };

        var pipe = cNexus.Context.CreatePipe();
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);
        await pipe.ReadyTask.Timeout(1);
        var messageReceivedCount = 0;
        
        var cts = new CancellationTokenSource(50);
        await foreach (var _ in (await pipe.GetUnmanagedChannelReader<int>()).WithCancellation(cts.Token))
        {
            messageReceivedCount++;
        }
        Assert.That(messageReceivedCount, Is.EqualTo(0));
        await pipe.CompleteTask.Timeout(1);
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task CancelsAndResumesRead(Type type)
    {
        var (server, _, cNexus, _) = await Setup(type);
        var sNexus = server.NexusCreatedQueue.First();
        sNexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            var writer = await pipe.GetUnmanagedChannelWriter<int>();
            await Task.Delay(200);
            await writer.WriteAsync(1);
        };

        var pipe = cNexus.Context.CreatePipe();
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);
        await pipe.ReadyTask.Timeout(1);
        var messageReceivedCount = 0;
        
        var cts = new CancellationTokenSource(50);
        await foreach (var _ in (await pipe.GetUnmanagedChannelReader<int>()).WithCancellation(cts.Token))
        {
            messageReceivedCount++;
        }
        
        Assert.That(messageReceivedCount, Is.EqualTo(0));
        
        await foreach (var _ in await pipe.GetUnmanagedChannelReader<int>())
        {
            messageReceivedCount++;
        }
        
        Assert.That(messageReceivedCount, Is.EqualTo(1));
        await pipe.CompleteTask.Timeout(1);
    }
}
