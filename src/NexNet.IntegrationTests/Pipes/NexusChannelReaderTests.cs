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
        var pipeReader = TestPipes.Reader(new DummyPipeStateManager(), null, true);
        var reader = new NexusChannelReader<ComplexMessage>(pipeReader);

        var baseObject = ComplexMessage.Random();
        var bufferWriter = BufferWriter<byte>.Create();

        var bytes = TestSerialization.SerializeChannelItem(baseObject);
        var header = BitConverter.GetBytes((ushort)bytes.Length);
        //bufferWriter.Write(header);
        bufferWriter.Write(bytes);
        bufferWriter.Write(new ReadOnlySpan<byte>(bytes).Slice(0, 1));

        using (var buffer = bufferWriter.Flush())
        {
            pipeReader.BufferData(buffer);
        }


        var result = await reader.ReadAsync(CancellationToken.None).Timeout(1);

        Assert.That(result.Single(), Is.EqualTo(baseObject));

        bufferWriter.Write(new ReadOnlySpan<byte>(bytes).Slice(1, bytes.Length - 1));

        using (var buffer = bufferWriter.Flush())
        {
            pipeReader.BufferData(buffer);
        }

        var result2 = await reader.ReadAsync(CancellationToken.None).Timeout(1);

        Assert.That(result.Single(), Is.EqualTo(baseObject));
    }

    [Test]
    public async Task ReadsPartialData()
    {
        var pipeReader = TestPipes.Reader(new DummyPipeStateManager(), null, true);
        var reader = new NexusChannelReader<ComplexMessage>(pipeReader);

        var baseObject = ComplexMessage.Random();
        var bufferWriter = BufferWriter<byte>.Create();

        var bytes = TestSerialization.SerializeChannelItem(baseObject);
        var header = BitConverter.GetBytes((ushort)bytes.Length);
        //bufferWriter.Write(header);
        bufferWriter.Write(bytes);

        // Perform a partial write.
        bufferWriter.Write(new ReadOnlySpan<byte>(bytes).Slice(0, 1));

        using (var buffer = bufferWriter.Flush())
        {
            pipeReader.BufferData(buffer);
        }

        var result = await reader.ReadAsync(CancellationToken.None).Timeout(1);

        Assert.That(result.Single(), Is.EqualTo(baseObject));

        //Write the rest of the data
        bufferWriter.Write(new ReadOnlySpan<byte>(bytes).Slice(1, bytes.Length - 1));

        using (var buffer = bufferWriter.Flush())
        {
            pipeReader.BufferData(buffer);
        }

        var result2 = await reader.ReadAsync(CancellationToken.None).Timeout(1);

        Assert.That(result.Single(), Is.EqualTo(baseObject));
    }

    private static async Task Buffer(NexusPipeReader pipeReader, byte[] data)
    {
        var bufferWriter = BufferWriter<byte>.Create();
        bufferWriter.Write(data.AsSpan());
        using var buffer = bufferWriter.Flush();
        pipeReader.BufferData(buffer);
    }

    [Test]
    public async Task ReadsLargerItemSplitAfterSmallItems()
    {
        // Small items first, then an item far larger than any before it, delivered in two parts. The first part is
        // big enough that the reader decodes it without probing, fails, and must wait for the rest.
        var pipeReader = TestPipes.Reader(new DummyPipeStateManager(), null, true);
        var reader = new NexusChannelReader<string>(pipeReader);
        var small = Enumerable.Range(0, 20).Select(i => "s" + i).ToArray();
        var large = new string('L', 2000);
        var bytes = small.SelectMany(TestSerialization.SerializeChannelItem).Concat(TestSerialization.SerializeChannelItem(large)).ToArray();
        var split = bytes.Length - 1000;

        await Buffer(pipeReader, bytes[..split]);
        var first = await reader.ReadAsync().Timeout(1);
        Assert.That(first, Is.EqualTo(small));

        await Buffer(pipeReader, bytes[split..]);
        var second = await reader.ReadAsync().Timeout(1);
        Assert.That(second, Is.EqualTo(new[] { large }));
    }

    [Test]
    public async Task ReadAvailableDecodesOnlyCompleteItemsWithoutWaiting()
    {
        var pipeReader = TestPipes.Reader(new DummyPipeStateManager(), null, true);
        var reader = new NexusChannelReader<ComplexMessage>(pipeReader);
        var item = ComplexMessage.Random();
        var bytes = TestSerialization.SerializeChannelItem(item);
        var half = bytes.Length / 2;
        var list = new List<ComplexMessage>();

        // Partial data: nothing decoded, nothing consumed.
        await Buffer(pipeReader, bytes[..half]);
        Assert.That(reader.ReadAvailable(list, null), Is.EqualTo(0));
        Assert.That(list, Is.Empty);

        // The rest of the data completes the buffered half into one item.
        await Buffer(pipeReader, bytes[half..]);
        Assert.That(reader.ReadAvailable(list, null), Is.EqualTo(bytes.Length));
        Assert.That(list.Single(), Is.EqualTo(item));

        // No new data: nothing more to read.
        list.Clear();
        Assert.That(reader.ReadAvailable(list, null), Is.EqualTo(0));
        Assert.That(list, Is.Empty);
    }

    [Test]
    public async Task ReadsManyItemsAcrossEverySplitPoint()
    {
        var items = Enumerable.Range(0, 40).Select(i => new string((char)('a' + i % 26), i * 7)).ToArray();
        var bytes = items.SelectMany(TestSerialization.SerializeChannelItem).ToArray();
        for (var split = 1; split < bytes.Length; split += 13)
        {
            var pipeReader = TestPipes.Reader(new DummyPipeStateManager(), null, true);
            var reader = new NexusChannelReader<string>(pipeReader);
            var read = new List<string>();

            // The first chunk may hold no complete item (only a prefix), so the first read may wait for the second.
            await Buffer(pipeReader, bytes[..split]);
            var first = reader.ReadAsync().AsTask();
            await Buffer(pipeReader, bytes[split..]);
            read.AddRange(await first.Timeout(1));
            while (read.Count < items.Length)
                read.AddRange(await reader.ReadAsync().Timeout(1));

            Assert.That(read, Is.EqualTo(items), $"split {split}");
        }
    }

    [Test]
    public async Task CompleteMalformedItemAfterSmallItemsThrows()
    {
        // Small ints, then a complete item holding a string where an int is expected: the error must surface.
        var pipeReader = TestPipes.Reader(new DummyPipeStateManager(), null, true);
        var reader = new NexusChannelReader<int>(pipeReader);
        var data = Enumerable.Range(0, 10).SelectMany(i => TestSerialization.SerializeChannelItem(i))
            .Concat(TestSerialization.FrameChannelItem([0xa5, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o']))
            .ToArray();

        await Buffer(pipeReader, data);
        await Assert.ThatAsync(async () => await reader.ReadAsync().Timeout(1), Throws.InstanceOf<NexNet.Serialization.NexusSerializationException>());
    }

    [Test]
    public async Task NeverUsedCodeAfterSmallItemsThrows()
    {
        var pipeReader = TestPipes.Reader(new DummyPipeStateManager(), null, true);
        var reader = new NexusChannelReader<int>(pipeReader);
        var data = Enumerable.Range(0, 10).SelectMany(i => TestSerialization.SerializeChannelItem(i))
            .Concat(TestSerialization.FrameChannelItem([0xc1]))
            .ToArray();

        await Buffer(pipeReader, data);
        await Assert.ThatAsync(async () => await reader.ReadAsync().Timeout(1), Throws.InstanceOf<NexNet.Serialization.NexusSerializationException>());
    }

    [Test]
    public async Task ItemOverTheLimit_ResetsThePipe()
    {
        var stateManager = new ResetCountingStateManager();
        var pipeReader = TestPipes.Reader(stateManager, null, true);
        var reader = new NexusChannelReader<byte[]>(pipeReader, maxItemSize: 100);

        // The prefix alone is enough: the reader does not wait for the item.
        await Buffer(pipeReader, TestSerialization.FrameChannelItem(new byte[101])[..2]);

        await Assert.ThatAsync(async () => await reader.ReadAsync().Timeout(1), Throws.InstanceOf<NexusMessageTooLargeException>());
        Assert.That(stateManager.Resets, Is.EqualTo(1));
    }

    [Test]
    public async Task ItemWithBytesAfterItsValue_ResetsThePipe()
    {
        var stateManager = new ResetCountingStateManager();
        var pipeReader = TestPipes.Reader(stateManager, null, true);
        var reader = new NexusChannelReader<int>(pipeReader);

        // The prefix says 2 bytes; the value (fixint 5) is 1.
        await Buffer(pipeReader, [0x02, 0x05, 0x05]);

        await Assert.ThatAsync(async () => await reader.ReadAsync().Timeout(1), Throws.InstanceOf<NexNet.Serialization.NexusSerializationException>());
        Assert.That(stateManager.Resets, Is.EqualTo(1));
    }

    [Test]
    public async Task ItemLargerThanTheWindow_GetsAnExtraGrant()
    {
        var stateManager = new ResetCountingStateManager();
        var pipeReader = TestPipes.Reader(stateManager, null, true, window: 64);
        var reader = new NexusChannelReader<byte[]>(pipeReader);
        var item = TestSerialization.SerializeChannelItem(new byte[100]);

        // The window holds only part of the item; the reader can't consume it, so the writer would stall.
        Assert.That(pipeReader.BufferData(new ReadOnlySequence<byte>(item, 0, 64)), Is.EqualTo(NexusPipeBufferResult.Success));
        var read = reader.ReadAsync().AsTask();
        await Task.Delay(50);

        // The difference was granted as extra credit, so the rest fits.
        Assert.That(stateManager.Grants, Is.EqualTo(new[] { item.Length - 64 }));
        Assert.That(pipeReader.BufferData(new ReadOnlySequence<byte>(item, 64, item.Length - 64)), Is.EqualTo(NexusPipeBufferResult.Success));
        Assert.That((await read.Timeout(1)).Single(), Has.Length.EqualTo(100));

        // The extra is paid off by consuming the item, not granted back.
        Assert.That(stateManager.ExtraReleased, Is.EqualTo(item.Length - 64));
    }

    private sealed class ResetCountingStateManager : IPipeStateManager
    {
        public int Resets;
        public List<int> Grants { get; } = [];
        public int ExtraReleased;
        public ushort Id => 1;
        public NexusDuplexPipe.State CurrentState { get; private set; } = NexusDuplexPipe.State.Ready;
        public ValueTask NotifyState() => default;

        public bool UpdateState(NexusDuplexPipe.State updatedState, bool remove = false)
        {
            CurrentState |= updatedState;
            return true;
        }

        public void GrantCredit(int bytes) => Grants.Add(bytes);
        public void ReleaseExtra(int bytes) => ExtraReleased += bytes;
        public void Reset(PipeResetReason reason) => Resets++;
    }

    [Test]
    public async Task CancelsReadDelayed()
    {
        var pipeReader = TestPipes.Reader(new DummyPipeStateManager(), null, true);
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
        var pipeReader = TestPipes.Reader(new DummyPipeStateManager(), null, true);
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
        var pipeReader = TestPipes.Reader(new DummyPipeStateManager(), null, true);
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
        var pipeReader = TestPipes.Reader(new DummyPipeStateManager(), null, true);
        var reader = new NexusChannelReader<ComplexMessage>(pipeReader);
        var baseObject = ComplexMessage.Random();
        var bytes = new ReadOnlySequence<byte>(TestSerialization.SerializeChannelItem(baseObject));
        _ = Task.Run(async () =>
        {
            await tcs.Task.Timeout(1);
            for (var i = 0; i < bytes.Length; i++)
            {
                pipeReader.BufferData(bytes.Slice(i, 1));
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
        var pipeReader = TestPipes.Reader(new DummyPipeStateManager(), null, true);
        var reader = new NexusChannelReader<ComplexMessage>(pipeReader);
        var baseObject = ComplexMessage.Random();
        var bytes = new ReadOnlySequence<byte>(TestSerialization.SerializeChannelItem(baseObject));

        for (var i = 0; i < iterations; i++)
        {
            pipeReader.BufferData(bytes);
        }
        var result = await reader.ReadAsync(CancellationToken.None).Timeout(1);

        foreach (var complexMessage in result)
        {
            Assert.That(complexMessage, Is.EqualTo(baseObject));
        }

        Assert.That(result.Count(), Is.EqualTo(iterations));
    }
}
