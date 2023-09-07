using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MemoryPack;
using NexNet.Pipes;
using NUnit.Framework;
using Pipelines.Sockets.Unofficial.Buffers;

namespace NexNet.IntegrationTests;

internal class NexusChannelReaderTests
{
    [Test]
    public async Task ReadsData()
    {
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager());
        var reader = new NexusChannelReader<ComplexMessage>(pipeReader);

        var baseObject = ComplexMessage.Random();
        var bufferWriter = BufferWriter<byte>.Create();

        var bytes = MemoryPackSerializer.Serialize(baseObject);
        var header = BitConverter.GetBytes((ushort)bytes.Length);
        //bufferWriter.Write(header);
        bufferWriter.Write(bytes);
        bufferWriter.Write(new ReadOnlySpan<byte>(bytes).Slice(0, 1));

        using (var buffer = bufferWriter.Flush())
        {
            await pipeReader.BufferData(buffer);
        }


        var result = await reader.ReadAsync(CancellationToken.None).AsTask().Timeout(10000);

        Assert.AreEqual(baseObject, result.Single());

        bufferWriter.Write(new ReadOnlySpan<byte>(bytes).Slice(1, bytes.Length - 1));

        using (var buffer = bufferWriter.Flush())
        {
            await pipeReader.BufferData(buffer);
        }

        var result2 = await reader.ReadAsync(CancellationToken.None).AsTask().Timeout(10000);

        Assert.AreEqual(baseObject, result.Single());
    }

    [Test]
    public async Task ReadsPartialData()
    {
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager());
        var reader = new NexusChannelReader<ComplexMessage>(pipeReader);

        var baseObject = ComplexMessage.Random();
        var bufferWriter = BufferWriter<byte>.Create();

        var bytes = MemoryPackSerializer.Serialize(baseObject);
        var header = BitConverter.GetBytes((ushort)bytes.Length);
        //bufferWriter.Write(header);
        bufferWriter.Write(bytes);

        // Perform a partial write.
        bufferWriter.Write(new ReadOnlySpan<byte>(bytes).Slice(0, 1));

        using (var buffer = bufferWriter.Flush())
        {
            await pipeReader.BufferData(buffer);
        }


        var result = await reader.ReadAsync(CancellationToken.None).AsTask().Timeout(10000);

        Assert.AreEqual(baseObject, result.Single());

        //Write the rest of the data
        bufferWriter.Write(new ReadOnlySpan<byte>(bytes).Slice(1, bytes.Length - 1));

        using (var buffer = bufferWriter.Flush())
        {
            await pipeReader.BufferData(buffer);
        }

        var result2 = await reader.ReadAsync(CancellationToken.None).AsTask().Timeout(10000);

        Assert.AreEqual(baseObject, result.Single());
    }

    [Test]
    public async Task CancelsReadDelayed()
    {
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager());
        var reader = new NexusChannelReader<ComplexMessage>(pipeReader);
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
        var reader = new NexusChannelReader<ComplexMessage>(pipeReader);
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
        var reader = new NexusChannelReader<ComplexMessage>(pipeReader);
        // ReSharper disable once MethodHasAsyncOverload
        pipeReader.Complete();
        var result = await reader.ReadAsync().AsTask().Timeout(1);

        Assert.IsTrue(reader.IsComplete);
        Assert.NotNull(result);
        Assert.IsEmpty(result);
    }


    [Test]
    public async Task WaitsForFullData()
    {
        var tcs = new TaskCompletionSource();
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager());
        var reader = new NexusChannelReader<ComplexMessage>(pipeReader);
        var baseObject = ComplexMessage.Random();
        var bytes = new ReadOnlySequence<byte>(MemoryPackSerializer.Serialize(baseObject));
        _ = Task.Run(async () =>
        {
            await tcs.Task;
            for (int i = 0; i < bytes.Length; i++)
            {
                await pipeReader.BufferData(bytes.Slice(i, 1));
            }
        });

        tcs.SetResult();
        var result = await reader.ReadAsync(CancellationToken.None).AsTask().Timeout(1);

        Assert.AreEqual(baseObject, result.Single());
    }

    [Test]
    public async Task ReadsMultiple()
    {
        const int iterations = 1000;
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager());
        var reader = new NexusChannelReader<ComplexMessage>(pipeReader);
        var baseObject = ComplexMessage.Random();
        var bytes = new ReadOnlySequence<byte>(MemoryPackSerializer.Serialize(baseObject));

        for (int i = 0; i < iterations; i++)
        {
            await pipeReader.BufferData(bytes);
        }
        var result = await reader.ReadAsync(CancellationToken.None).AsTask().Timeout(1);
        
        foreach (var complexMessage in result)
        {
            Assert.AreEqual(baseObject, complexMessage);
        }

        Assert.AreEqual(iterations, result.Count());
    }
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
}
