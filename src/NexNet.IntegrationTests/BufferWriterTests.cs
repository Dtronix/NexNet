using NUnit.Framework;
using Pipelines.Sockets.Unofficial.Buffers;

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
        Assert.AreEqual(loops * dataLength, sequence.Length);

        bufferWriter.ReleaseTo(3000 * 16);

        sequence = bufferWriter.GetBuffer().AsReadOnly();
        Assert.AreEqual(loops * dataLength - 3000 * 16, sequence.Length);


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
