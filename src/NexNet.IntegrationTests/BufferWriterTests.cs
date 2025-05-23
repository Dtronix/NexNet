using System.Buffers;
using NexNet.Internals.Pipelines.Arenas;
using NexNet.Internals.Pipelines.Buffers;
using NUnit.Framework;
using NUnit.Framework.Legacy;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal class BufferWriterTests
{

    [Test]
    public void FuzzBufferTests()
    {
        var bufferWriter = BufferWriter<byte>.Create(128);
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
                Assert.Fail($"bufferSize: {bufferSize} != addBufferLen: {addBufferLen}");

            if (decreaseSize < bufferWriter.Length)
                decreaseSize = (int)bufferWriter.Length;
            
            bufferWriter.ReleaseTo(decreaseSize);

            bufferSize -= decreaseSize;
            var buffer = bufferWriter.GetBuffer();
            var bufferLen = buffer.Length;

            if (bufferSize != bufferLen)
                Assert.Fail($"bufferSize: {bufferSize} != bufferLen: {bufferLen}");

            if (bufferLen > 1024 * 32)
                decreaseSize = 529;
            else if (bufferLen < 1024 * 16)
                decreaseSize = 67;
        }
    }

    [Test]
    public void FuzzBufferTestsSequencePosition()
    {
        var bufferWriter = BufferWriter<byte>.Create(128);
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
                Assert.Fail($"bufferSize: {bufferSize} != addBufferLen: {addBufferLen}");

            bufferWriter.ReleaseTo(addBuffer.GetPosition(decreaseSize));

            bufferSize -= decreaseSize;
            var buffer = bufferWriter.GetBuffer();
            var bufferLen = buffer.Length;

            if (bufferSize != bufferLen)
                Assert.Fail($"bufferSize: {bufferSize} != bufferLen: {bufferLen}");

            if (bufferLen > 1024 * 32)
                decreaseSize = 529;
            else if (bufferLen < 1024 * 16)
                decreaseSize = 67;
        }
    }

    [Test]
    public void ReleasesLargeDataSpan()
    {
        var bufferWriter = BufferWriter<byte>.Create(8 * 1024);
        var loops = 9000;
        var dataLength = 10;
        Span<byte> data = new byte[dataLength];

        FillSpan(data);

        for (int i = 0; i < loops; i++)
        {
            data.CopyTo(bufferWriter.GetSpan(dataLength));
            bufferWriter.Advance(dataLength);
        }

        var sequence = bufferWriter.GetBuffer().AsReadOnly();
        Assert.That(sequence.Length, Is.EqualTo(loops * dataLength));

        bufferWriter.ReleaseTo(3000 * 16);

        sequence = bufferWriter.GetBuffer().AsReadOnly();
        Assert.That(sequence.Length, Is.EqualTo(loops * dataLength - 3000 * 16));
    }    
    
    [Test]
    public void AllowsUseAfterDisposal()
    {
        var bufferWriter = BufferWriter<byte>.Create(8 * 1024);
 
        Span<byte> data = new byte[50];

        FillSpan(data);

        data.CopyTo(bufferWriter.GetSpan(data.Length));
        bufferWriter.Advance(data.Length);

        bufferWriter.Reset();

        data.CopyTo(bufferWriter.GetSpan(data.Length));
        bufferWriter.Advance(data.Length);

        var sequence = bufferWriter.GetBuffer().AsReadOnly();
        Assert.That(sequence.Length, Is.EqualTo(data.Length));


    }
    
    [Test]
    public void GetBuffer_Empty_ReturnsZeroLength()
    {
        var writer = BufferWriter<byte>.Create(128);
        Assert.That(writer.GetBuffer().Length, Is.EqualTo(0));
    }

    [Test]
    public void Advance_Negative_ThrowsArgumentOutOfRangeException()
    {
        var writer = BufferWriter<byte>.Create(128);
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Advance(-1));
    }

    [Test]
    public void Flush_LongCount_Negative_ThrowsArgumentOutOfRangeException()
    {
        var writer = BufferWriter<byte>.Create(128);
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Flush(-5));
    }
    
    
    [Test]
    public void Flush_CountGreaterThanBuffered_ThrowsArgumentOutOfRangeException()
    {
        var writer = BufferWriter<byte>.Create(128);
        var data = Enumerable.Range(0, 5).Select(i => (byte)i).ToArray();
        data.CopyTo(writer.GetSpan(data.Length));
        writer.Advance(data.Length);

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Flush(data.Length + 1));
    }

    [Test]
    public void Flush_ZeroCount_ReturnsEmptySequence()
    {
        var writer = BufferWriter<byte>.Create(128);
        var owned = writer.Flush(0);
        // Owned.Value is a ReadOnlySequence<byte>
        Assert.That(owned.Value.Length, Is.EqualTo(0));
    }
    
    [Test]
    public void Flush_PartialAndFull_WorksAsExpected()
    {
        var writer = BufferWriter<byte>.Create(4);
        var data = Enumerable.Range(0, 10).Select(i => (byte)i).ToArray();
        // write all 10 bytes
        data.CopyTo(writer.GetSpan(data.Length));
        writer.Advance(data.Length);

        // flush 6
        var ownedPartial = writer.Flush(6);
        var seqPartial = ownedPartial.Value;
        Assert.That(seqPartial.Length, Is.EqualTo(6));
        Assert.That(seqPartial.ToArray(), Is.EqualTo(data.Take(6).ToArray()).AsCollection);

        // remaining should be 4
        Assert.That(writer.GetBuffer().Length, Is.EqualTo(4));
        Assert.That(writer.GetBuffer().ToArray(),
            Is.EqualTo(data.Skip(6).ToArray()).AsCollection);

        // flush all remaining
        var ownedAll = writer.Flush();
        Assert.That(ownedAll.Value.Length, Is.EqualTo(4));
        Assert.That(writer.GetBuffer().Length, Is.EqualTo(0));
    }
    
    [Test]
    public void Deallocate_FullSequence_ClearsBuffer()
    {
        var writer = BufferWriter<byte>.Create(128);
        var data = Enumerable.Range(0, 5).Select(i => (byte)i).ToArray();
        data.CopyTo(writer.GetSpan(data.Length));
        writer.Advance(data.Length);

        var seq = writer.GetBuffer();
        writer.Deallocate(seq);
        Assert.That(writer.GetBuffer().Length, Is.EqualTo(0));
    }

    [Test]
    [Ignore("Under investigation.")]
    public void ReleaseTo_CountGreaterThanBuffered_Throws()
    {
        var writer = BufferWriter<byte>.Create(128);
        var data = new byte[20];
        var span = writer.GetSpan(data.Length);
        data.CopyTo(span);
        writer.Advance(data.Length);

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.ReleaseTo(data.Length + 10));
    }

    [Test]
    public void Dispose_ClearsBuffer_And_IsIdempotent()
    {
        var writer = BufferWriter<byte>.Create(64);
        writer.GetSpan(10);
        writer.Advance(10);

        writer.Dispose();
        Assert.That(writer.GetBuffer().Length, Is.Zero);

        // second dispose must not throw
        Assert.DoesNotThrow(() => writer.Dispose());
    }
    
    [Test]
    public void GetSpan_DoesNotCommitUntilAdvance()
    {
        var writer = BufferWriter<byte>.Create(32);
        var span = writer.GetSpan(10);
        for (int i = 0; i < 10; i++) span[i] = (byte)i;

        // not yet committed
        Assert.That(writer.GetBuffer().Length, Is.Zero);

        writer.Advance(10);
        Assert.That(writer.GetBuffer().Length, Is.EqualTo(10));
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
