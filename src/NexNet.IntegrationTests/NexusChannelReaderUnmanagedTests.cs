using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NexNet.Internals.Pipes;
using NUnit.Framework;
using Pipelines.Sockets.Unofficial.Buffers;

namespace NexNet.IntegrationTests;

internal class NexusChannelReaderUnmanagedTests
{
    private class DummyPipeStateManager : IPipeStateManager
    {
        public ushort Id { get; } = 0;
        public ValueTask NotifyState()
        {
            return default;
        }

        public bool UpdateState(NexusDuplexPipe.State updatedState, bool remove = false)
        {
            CurrentState |= updatedState;
            return true;
        }

        public NexusDuplexPipe.State CurrentState { get; private set; } = NexusDuplexPipe.State.Ready;
    }

    [Test]
    public async Task ReadsData()
    {
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager());
        var data = new ReadOnlySequence<byte>(BitConverter.GetBytes(1234567890L));

        var reader = new NexusChannelReaderUnmanaged<long>(pipeReader);
        await pipeReader.BufferData(data);

        var result = await reader.ReadAsync(CancellationToken.None).AsTask().Timeout(1);

        Assert.AreEqual(1234567890L, result.Single());
    }

    [Test]
    public async Task CancelsReadDelayed()
    {
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager());
        var reader = new NexusChannelReaderUnmanaged<long>(pipeReader);
        var cts = new CancellationTokenSource(100);
        var result = await reader.ReadAsync(cts.Token).AsTask().Timeout(1);

        Assert.IsTrue(cts.IsCancellationRequested);
        Assert.NotNull(result);
        Assert.IsEmpty(result);
    }

    [Test]
    public async Task CancelsReadImmediate()
    {
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager());
        var reader = new NexusChannelReaderUnmanaged<long>(pipeReader);
        var cts = new CancellationTokenSource(100);
        cts.Cancel();
        var result = await reader.ReadAsync(cts.Token).AsTask().Timeout(1);

        Assert.IsTrue(cts.IsCancellationRequested);
        Assert.NotNull(result);
        Assert.IsEmpty(result);
    }

    [Test]
    public async Task Completes()
    {
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager());
        var reader = new NexusChannelReaderUnmanaged<long>(pipeReader);
        var cts = new CancellationTokenSource(100);
        cts.Cancel();
        var result = await reader.ReadAsync(cts.Token).AsTask().Timeout(1);

        Assert.IsTrue(cts.IsCancellationRequested);
        Assert.NotNull(result);
        Assert.IsEmpty(result);
    }


    [Test]
    public async Task WaitsForFullData()
    {
        var tcs = new TaskCompletionSource();
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager());
        var data = new ReadOnlySequence<byte>(BitConverter.GetBytes(1234567890L));
        var reader = new NexusChannelReaderUnmanaged<long>(pipeReader);

        _ = Task.Run(async () =>
        {
            await tcs.Task;
            for (int i = 0; i < 8; i++)
            {
                await pipeReader.BufferData(data.Slice(i, 1));
            }
        });

        tcs.SetResult();
        var result = await reader.ReadAsync(CancellationToken.None).AsTask().Timeout(1);

        Assert.AreEqual(1234567890L, result.Single());
    }

    [Test]
    public async Task ReadsMultiple()
    {
        const int iterations = 1000;
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager());
        var reader = new NexusChannelReaderUnmanaged<long>(pipeReader);
        var longValue = new byte[8];
        long count = 0;

        for (int i = 0; i < iterations; i++)
        {
            BitConverter.TryWriteBytes(longValue, (long)i);
            await pipeReader.BufferData(new ReadOnlySequence<byte>(longValue));
        }
        var result = await reader.ReadAsync(CancellationToken.None).AsTask().Timeout(1);
        
        foreach (var l in result)
        {
            Assert.AreEqual(count++, l);
        }

        Assert.AreEqual(iterations, count);
    }
}
