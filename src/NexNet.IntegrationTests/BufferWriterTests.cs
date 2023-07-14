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
                Console.WriteLine($"bufferSize: {bufferSize} != addBufferLen: {addBufferLen}");

            bufferWriter.ReleaseTo(decreaseSize);

            bufferSize -= decreaseSize;
            var buffer = bufferWriter.GetBuffer();
            var bufferLen = buffer.Length;

            if (bufferSize != bufferLen)
                Console.WriteLine($"bufferSize: {bufferSize} != bufferLen: {bufferLen}");

            if (bufferLen > 1024 * 32)
                decreaseSize = 529;
            else if (bufferLen < 1024 * 16)
                decreaseSize = 67;
        }

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
