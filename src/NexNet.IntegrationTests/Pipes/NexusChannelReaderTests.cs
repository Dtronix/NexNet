using System.Buffers;
using MemoryPack;
using NexNet.Internals.Pipelines.Buffers;
using NexNet.Pipes;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes;

internal class NexusChannelReaderTests : NexusChannelTestBase
{
    [Test]
    public async Task ReadsData()
    {
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager(), null, true, 0, 0, 0);
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
            await pipeReader.BufferData(buffer).Timeout(1);
        }


        var result = await reader.ReadAsync(CancellationToken.None).Timeout(1);

        Assert.That(result.Single(), Is.EqualTo(baseObject));

        bufferWriter.Write(new ReadOnlySpan<byte>(bytes).Slice(1, bytes.Length - 1));

        using (var buffer = bufferWriter.Flush())
        {
            await pipeReader.BufferData(buffer).Timeout(1);
        }

        var result2 = await reader.ReadAsync(CancellationToken.None).Timeout(1);

        Assert.That(result.Single(), Is.EqualTo(baseObject));
    }

    [Test]
    public async Task ReadsPartialData()
    {
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager(), null, true, 0, 0, 0);
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
            await pipeReader.BufferData(buffer).Timeout(1);
        }

        var result = await reader.ReadAsync(CancellationToken.None).Timeout(1);

        Assert.That(result.Single(), Is.EqualTo(baseObject));

        //Write the rest of the data
        bufferWriter.Write(new ReadOnlySpan<byte>(bytes).Slice(1, bytes.Length - 1));

        using (var buffer = bufferWriter.Flush())
        {
            await pipeReader.BufferData(buffer).Timeout(1);
        }

        var result2 = await reader.ReadAsync(CancellationToken.None).Timeout(1);

        Assert.That(result.Single(), Is.EqualTo(baseObject));
    }

    [Test]
    public async Task CancelsReadDelayed()
    {
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager(), null, true, 0, 0, 0);
        var reader = new NexusChannelReader<ComplexMessage>(pipeReader);
        var cts = new CancellationTokenSource(100);
        var result = await reader.ReadAsync(cts.Token).Timeout(1);

        Assert.IsTrue(cts.IsCancellationRequested);
        Assert.NotNull(result);
        Assert.IsEmpty(result);
    }

    [Test]
    public async Task CancelsReadImmediate()
    {
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager(), null, true, 0, 0, 0);
        var reader = new NexusChannelReader<ComplexMessage>(pipeReader);
        var cts = new CancellationTokenSource(100);
        cts.Cancel();
        var result = await reader.ReadAsync(cts.Token).Timeout(1);

        Assert.IsTrue(cts.IsCancellationRequested);
        Assert.NotNull(result);
        Assert.IsEmpty(result);
    }

    [Test]
    public async Task Completes()
    {
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager(), null, true, 0, 0, 0);
        var reader = new NexusChannelReader<ComplexMessage>(pipeReader);
        // ReSharper disable once MethodHasAsyncOverload
        await pipeReader.CompleteAsync();
        var result = await reader.ReadAsync().Timeout(1);

        Assert.IsTrue(reader.IsComplete);
        Assert.NotNull(result);
        Assert.IsEmpty(result);
    }


    [Test]
    public async Task WaitsForFullData()
    {
        var tcs = new TaskCompletionSource();
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager(), null, true, 0, 0, 0);
        var reader = new NexusChannelReader<ComplexMessage>(pipeReader);
        var baseObject = ComplexMessage.Random();
        var bytes = new ReadOnlySequence<byte>(MemoryPackSerializer.Serialize(baseObject));
        _ = Task.Run(async () =>
        {
            await tcs.Task.Timeout(1);
            for (var i = 0; i < bytes.Length; i++)
            {
                await pipeReader.BufferData(bytes.Slice(i, 1)).Timeout(1);
            }
        });

        tcs.SetResult();
        var result = await reader.ReadAsync(CancellationToken.None).Timeout(1);

        Assert.That(result.Single(), Is.EqualTo(baseObject));
    }

    [Test]
    public async Task ReadsMultiple()
    {
        const int iterations = 1000;
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager(), null, true, 0, 0, 0);
        var reader = new NexusChannelReader<ComplexMessage>(pipeReader);
        var baseObject = ComplexMessage.Random();
        var bytes = new ReadOnlySequence<byte>(MemoryPackSerializer.Serialize(baseObject));

        for (var i = 0; i < iterations; i++)
        {
            await pipeReader.BufferData(bytes).Timeout(1);
        }
        var result = await reader.ReadAsync(CancellationToken.None).Timeout(1);

        foreach (var complexMessage in result)
        {
            Assert.That(complexMessage, Is.EqualTo(baseObject));
        }

        Assert.That(result.Count(), Is.EqualTo(iterations));
    }
}
