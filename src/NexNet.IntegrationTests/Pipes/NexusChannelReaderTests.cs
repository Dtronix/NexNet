using System.Buffers;
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

        var bytes = TestSerialization.SerializePayload(baseObject);
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

        var bytes = TestSerialization.SerializePayload(baseObject);
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

    private static async Task Buffer(NexusPipeReader pipeReader, byte[] data)
    {
        var bufferWriter = BufferWriter<byte>.Create();
        bufferWriter.Write(data.AsSpan());
        using var buffer = bufferWriter.Flush();
        await pipeReader.BufferData(buffer).Timeout(1);
    }

    [Test]
    public async Task ReadsLargerItemSplitAfterSmallItems()
    {
        // Small items first, then an item far larger than any before it, delivered in two parts. The first part is
        // big enough that the reader decodes it without probing, fails, and must wait for the rest.
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager(), null, true, 0, 0, 0);
        var reader = new NexusChannelReader<string>(pipeReader);
        var small = Enumerable.Range(0, 20).Select(i => "s" + i).ToArray();
        var large = new string('L', 2000);
        var bytes = small.SelectMany(TestSerialization.SerializePayload).Concat(TestSerialization.SerializePayload(large)).ToArray();
        var split = bytes.Length - 1000;

        await Buffer(pipeReader, bytes[..split]);
        var first = await reader.ReadAsync().Timeout(1);
        Assert.That(first, Is.EqualTo(small));

        await Buffer(pipeReader, bytes[split..]);
        var second = await reader.ReadAsync().Timeout(1);
        Assert.That(second, Is.EqualTo(new[] { large }));
    }

#if !NEXNET_MEMORYPACK
    // MessagePack only: the legacy MemoryPack read path spins on repeated partial reads (impl-notes deviation 13)
    // and is removed in Phase 8.
    [Test]
    public async Task ReadsManyItemsAcrossEverySplitPoint()
    {
        var items = Enumerable.Range(0, 40).Select(i => new string((char)('a' + i % 26), i * 7)).ToArray();
        var bytes = items.SelectMany(TestSerialization.SerializePayload).ToArray();
        for (var split = 1; split < bytes.Length; split += 13)
        {
            var pipeReader = new NexusPipeReader(new DummyPipeStateManager(), null, true, 0, 0, 0);
            var reader = new NexusChannelReader<string>(pipeReader);
            var read = new List<string>();

            await Buffer(pipeReader, bytes[..split]);
            read.AddRange(await reader.ReadAsync().Timeout(1));
            await Buffer(pipeReader, bytes[split..]);
            while (read.Count < items.Length)
                read.AddRange(await reader.ReadAsync().Timeout(1));

            Assert.That(read, Is.EqualTo(items), $"split {split}");
        }
    }

    [Test]
    public async Task CompleteMalformedItemAfterSmallItemsThrows()
    {
        // Small ints, then a complete string where an int is expected, followed by enough padding that the string
        // is decoded without probing. The error must surface, not be mistaken for an incomplete item.
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager(), null, true, 0, 0, 0);
        var reader = new NexusChannelReader<int>(pipeReader);
        var data = Enumerable.Range(0, 10).Select(i => (byte)i)
            .Concat(new byte[] { 0xa5, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' })
            .Concat(Enumerable.Repeat((byte)1, 64))
            .ToArray();

        await Buffer(pipeReader, data);
        await Assert.ThatAsync(async () => await reader.ReadAsync().Timeout(1), Throws.InstanceOf<NexNet.Serialization.NexusSerializationException>());
    }

    [Test]
    public async Task NeverUsedCodeAfterSmallItemsThrows()
    {
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager(), null, true, 0, 0, 0);
        var reader = new NexusChannelReader<int>(pipeReader);
        var data = Enumerable.Range(0, 10).Select(i => (byte)i)
            .Concat(new byte[] { 0xc1 })
            .Concat(Enumerable.Repeat((byte)1, 64))
            .ToArray();

        await Buffer(pipeReader, data);
        await Assert.ThatAsync(async () => await reader.ReadAsync().Timeout(1), Throws.InstanceOf<NexNet.Serialization.NexusSerializationException>());
    }
#endif

    [Test]
    public async Task CancelsReadDelayed()
    {
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager(), null, true, 0, 0, 0);
        var reader = new NexusChannelReader<ComplexMessage>(pipeReader);
        var cts = new CancellationTokenSource(100);
        var result = await reader.ReadAsync(cts.Token).Timeout(1);

        Assert.That(cts.IsCancellationRequested, Is.True);
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task CancelsReadImmediate()
    {
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager(), null, true, 0, 0, 0);
        var reader = new NexusChannelReader<ComplexMessage>(pipeReader);
        var cts = new CancellationTokenSource(100);
        cts.Cancel();
        var result = await reader.ReadAsync(cts.Token).Timeout(1);

        Assert.That(cts.IsCancellationRequested, Is.True);
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task Completes()
    {
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager(), null, true, 0, 0, 0);
        var reader = new NexusChannelReader<ComplexMessage>(pipeReader);
        // ReSharper disable once MethodHasAsyncOverload
        await pipeReader.CompleteAsync();
        var result = await reader.ReadAsync().Timeout(1);

        Assert.That(reader.IsComplete, Is.True);
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }


    [Test]
    public async Task WaitsForFullData()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeReader = new NexusPipeReader(new DummyPipeStateManager(), null, true, 0, 0, 0);
        var reader = new NexusChannelReader<ComplexMessage>(pipeReader);
        var baseObject = ComplexMessage.Random();
        var bytes = new ReadOnlySequence<byte>(TestSerialization.SerializePayload(baseObject));
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
        var bytes = new ReadOnlySequence<byte>(TestSerialization.SerializePayload(baseObject));

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
