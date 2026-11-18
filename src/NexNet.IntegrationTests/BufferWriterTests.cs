using System.Buffers;
using NexNet.Internals.Pipelines.Buffers;
using System.Threading.Tasks;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal class BufferWriterTests
{

    [Test]
    public void FuzzBufferTests()
    {
        var bufferWriter = BufferWriter.Create(128);
        var bufferSize = 0;

        var increaseSize = 201;
        var decreaseSize = 67;

        for (int i = 0; i < 50000; i++)
        {
            bufferSize += increaseSize;
            var span = bufferWriter.GetSpan(increaseSize).Slice(0, increaseSize);
            bufferWriter.Advance(increaseSize);

            var addBufferLen = bufferWriter.GetBuffer().Length;
            if (bufferSize != addBufferLen)
                Fail.Test($"bufferSize: {bufferSize} != addBufferLen: {addBufferLen}");

            if (decreaseSize < bufferWriter.Length)
                decreaseSize = (int)bufferWriter.Length;
            
            bufferWriter.ReleaseTo(decreaseSize);

            bufferSize -= decreaseSize;
            var buffer = bufferWriter.GetBuffer();
            var bufferLen = buffer.Length;

            if (bufferSize != bufferLen)
                Fail.Test($"bufferSize: {bufferSize} != bufferLen: {bufferLen}");

            if (bufferLen > 1024 * 32)
                decreaseSize = 529;
            else if (bufferLen < 1024 * 16)
                decreaseSize = 67;
        }
    }

    [Test]
    public void FuzzBufferTestsSequencePosition()
    {
        var bufferWriter = BufferWriter.Create(128);
        var bufferSize = 0;

        var increaseSize = 201;
        var decreaseSize = 67;

        for (int i = 0; i < 50000; i++)
        {
            bufferSize += increaseSize;
            var span = bufferWriter.GetSpan(increaseSize).Slice(0, increaseSize);
            bufferWriter.Advance(increaseSize);

            var addBuffer = bufferWriter.GetBuffer();
            var addBufferLen = addBuffer.Length;
            if (bufferSize != addBufferLen)
                Fail.Test($"bufferSize: {bufferSize} != addBufferLen: {addBufferLen}");

            bufferWriter.ReleaseTo(addBuffer.GetPosition(decreaseSize));

            bufferSize -= decreaseSize;
            var buffer = bufferWriter.GetBuffer();
            var bufferLen = buffer.Length;

            if (bufferSize != bufferLen)
                Fail.Test($"bufferSize: {bufferSize} != bufferLen: {bufferLen}");

            if (bufferLen > 1024 * 32)
                decreaseSize = 529;
            else if (bufferLen < 1024 * 16)
                decreaseSize = 67;
        }
    }

    [Test]
    public async Task ReleasesLargeDataSpan()
    {
        var bufferWriter = BufferWriter.Create(8 * 1024);
        var loops = 9000;
        var dataLength = 10;
        Span<byte> data = new byte[dataLength];

        FillSpan(data);

        for (int i = 0; i < loops; i++)
        {
            data.CopyTo(bufferWriter.GetSpan(dataLength));
            bufferWriter.Advance(dataLength);
        }

        var sequence = bufferWriter.GetBuffer();
        await Assert.That(sequence.Length).IsEqualTo(loops * dataLength);

        bufferWriter.ReleaseTo(3000 * 16);

        sequence = bufferWriter.GetBuffer();
        await Assert.That(sequence.Length).IsEqualTo(loops * dataLength - 3000 * 16);
    }    
    
    [Test]
    public async Task AllowsUseAfterDisposal()
    {
        var bufferWriter = BufferWriter.Create(8 * 1024);
 
        Span<byte> data = new byte[50];

        FillSpan(data);

        data.CopyTo(bufferWriter.GetSpan(data.Length));
        bufferWriter.Advance(data.Length);

        bufferWriter.Reset();

        data.CopyTo(bufferWriter.GetSpan(data.Length));
        bufferWriter.Advance(data.Length);

        var sequence = bufferWriter.GetBuffer();
        await Assert.That(sequence.Length).IsEqualTo(data.Length);


    }
    
    [Test]
    public async Task GetBuffer_Empty_ReturnsZeroLength()
    {
        var writer = BufferWriter.Create(128);
        await Assert.That(writer.GetBuffer().Length).IsEqualTo(0);
    }

    [Test]
    public async Task Advance_Negative_ThrowsArgumentOutOfRangeException()
    {
        var writer = BufferWriter.Create(128);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => writer.Advance(-1));
    }

    [Test]
    public void Deallocate_KeepsDataWrittenAfterGetBuffer()
    {
        var writer = BufferWriter.Create(4);
        var data = Enumerable.Range(0, 10).Select(i => (byte)i).ToArray();
        Write(writer, data.AsSpan(0, 6));

        var first = writer.GetBuffer();
        Write(writer, data.AsSpan(6));
        // TODO: TUnit migration - Complex NUnit constraint. Manual conversion required.

        Assert.That(first.ToArray(), Is.EqualTo(data.Take(6).ToArray()).AsCollection);
        writer.Deallocate(first);
        // TODO: TUnit migration - Complex NUnit constraint. Manual conversion required.

        Assert.That(writer.GetBuffer().ToArray(), Is.EqualTo(data.Skip(6).ToArray()).AsCollection);
    }

    [Test]
    public async Task Deallocate_EndingAtSegmentBoundary_ContinuesInNextSegment()
    {
        // Blocks are rented with at least 16 bytes; fill the first block exactly, then start a second one.
        var writer = BufferWriter.Create(16);
        var firstBlock = writer.GetMemory(16).Length;
        Write(writer, new byte[firstBlock]);
        var buffer = writer.GetBuffer();
        Write(writer, new byte[] { 1, 2, 3 });

        writer.Deallocate(buffer);
        // TODO: TUnit migration - Complex NUnit constraint. Manual conversion required.

        Assert.That(writer.GetBuffer().ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }).AsCollection);
        await Assert.That(writer.Length).IsEqualTo(3);
    }

    [Test]
    public async Task Deallocate_MultipleSegments_ReleasesThemAll()
    {
        var writer = BufferWriter.Create(16);
        for (var i = 0; i < 10; i++)
            Write(writer, new byte[16]);

        var buffer = writer.GetBuffer();
        await Assert.That(buffer.IsSingleSegment).IsFalse();

        writer.Deallocate(buffer);
        await Assert.That(writer.Length).IsZero();

        // The writer keeps working on the remaining (or new) segments.
        Write(writer, new byte[] { 7 });
        // TODO: TUnit migration - Complex NUnit constraint. Manual conversion required.
        Assert.That(writer.GetBuffer().ToArray(), Is.EqualTo(new byte[] { 7 }).AsCollection);
    }

    // Pool return: each test writes 16-byte blocks into 16-byte segments, so segment i holds block i and the
    // arrays rented are known in order.

    [Test]
    public async Task Deallocate_ReturnsReleasedArraysToThePool()
    {
        var pool = new RecordingArrayPool();
        var writer = BufferWriter.Create(pool, 16);
        for (var i = 0; i < 3; i++)
            Write(writer, new byte[16]);
        await Assert.That(pool.Rented).Count().IsEqualTo(3);

        writer.Deallocate(writer.GetBuffer());
        // TODO: TUnit migration - Complex NUnit constraint. Manual conversion required.

        // The last segment stays as the head (the writer continues in it); the others go back to the pool.
        Assert.That(pool.Returned, Is.EqualTo(pool.Rented.Take(2)).AsCollection);

        writer.Dispose();
        // TODO: TUnit migration - Complex NUnit constraint. Manual conversion required.
        Assert.That(pool.Returned, Is.EquivalentTo(pool.Rented));
    }

    [Test]
    public async Task ReleaseTo_ReturnsOnlyFullyConsumedSegments()
    {
        var pool = new RecordingArrayPool();
        var writer = BufferWriter.Create(pool, 16);
        for (var i = 0; i < 3; i++)
            Write(writer, new byte[16]);

        // All of the first segment and part of the second.
        writer.ReleaseTo(20);
        // TODO: TUnit migration - Complex NUnit constraint. Manual conversion required.
        Assert.That(pool.Returned, Is.EqualTo(pool.Rented.Take(1)).AsCollection);

        writer.ReleaseTo(writer.GetBuffer().GetPosition(12));
        // TODO: TUnit migration - Complex NUnit constraint. Manual conversion required.
        Assert.That(pool.Returned, Is.EqualTo(pool.Rented.Take(2)).AsCollection);
        await Assert.That(writer.Length).IsEqualTo(16);
    }

    [Test]
    public async Task Dispose_ReturnsEveryRentedArray()
    {
        var pool = new RecordingArrayPool();
        var writer = BufferWriter.Create(pool, 16);
        for (var i = 0; i < 5; i++)
            Write(writer, new byte[16]);
        writer.ReleaseTo(24);

        writer.Dispose();
        // TODO: TUnit migration - Complex NUnit constraint. Manual conversion required.

        Assert.That(pool.Returned, Is.EquivalentTo(pool.Rented));
        await Assert.That(pool.Returned.Distinct().Count()).IsEqualTo(pool.Returned.Count).Because("an array was returned twice");
    }

    private static void Write(BufferWriter writer, ReadOnlySpan<byte> data)
    {
        data.CopyTo(writer.GetSpan(data.Length));
        writer.Advance(data.Length);
    }

    /// <summary>
    /// Array pool that rents new arrays of exactly the requested length and records what is rented and returned.
    /// </summary>
    private sealed class RecordingArrayPool : ArrayPool<byte>
    {
        public List<byte[]> Rented { get; } = new();
        public List<byte[]> Returned { get; } = new();

        public override byte[] Rent(int minimumLength)
        {
            var array = new byte[minimumLength];
            Rented.Add(array);
            return array;
        }

        public override void Return(byte[] array, bool clearArray = false) => Returned.Add(array);
    }

    [Test]
    public async Task Deallocate_FullSequence_ClearsBuffer()
    {
        var writer = BufferWriter.Create(128);
        var data = Enumerable.Range(0, 5).Select(i => (byte)i).ToArray();
        data.CopyTo(writer.GetSpan(data.Length));
        writer.Advance(data.Length);

        var seq = writer.GetBuffer();
        writer.Deallocate(seq);
        await Assert.That(writer.GetBuffer().Length).IsEqualTo(0);
    }

    [Test]
    public async Task ReleaseTo_CountGreaterThanBuffered_Throws()
    {
        var writer = BufferWriter.Create(128);
        var data = new byte[20];
        var span = writer.GetSpan(data.Length);
        data.CopyTo(span);
        writer.Advance(data.Length);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => writer.ReleaseTo(data.Length + 10));
    }

    [Test]
    public async Task Dispose_ClearsBuffer_And_IsIdempotent()
    {
        var writer = BufferWriter.Create(64);
        writer.GetSpan(10);
        writer.Advance(10);

        writer.Dispose();
        await Assert.That(writer.GetBuffer().Length).IsZero();

        // second dispose must not throw
        await Assert.That(() => writer.Dispose()).ThrowsNothing();
    }
    
    [Test]
    public async Task GetSpan_DoesNotCommitUntilAdvance()
    {
        var writer = BufferWriter.Create(32);
        var span = writer.GetSpan(10);
        for (int i = 0; i < 10; i++) span[i] = (byte)i;

        // not yet committed
        await Assert.That(writer.GetBuffer().Length).IsZero();

        writer.Advance(10);
        await Assert.That(writer.GetBuffer().Length).IsEqualTo(10);
    }


    private void FillSpan(Span<byte> span)
    {
        var number = 0;
        for (int i = 0; i < span.Length; i++)
        {
            if (number > 256)
                number = 0;

            span[i] = (byte)number++;
        }
    }
    
}