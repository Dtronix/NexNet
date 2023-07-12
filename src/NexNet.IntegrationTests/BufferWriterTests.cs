using System.Diagnostics;
using System.Net.Sockets;
using MemoryPack;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NexNet.Transports;
using NUnit.Framework;
using Pipelines.Sockets.Unofficial.Buffers;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexusClientTests
{
    [Test]
    public void BufferWriterTests()
    {
        var bufferWriter = BufferWriter<byte>.Create(128);
        /*var span = bufferWriter.GetSpan(128);
        bufferWriter.Advance(120);
        bufferWriter.Advance(120);
        bufferWriter.Flush().Dispose();
        

        var span = bufferWriter.GetSpan(120);
        bufferWriter.Advance(120);
        bufferWriter.Flush().Dispose();*/

        FillSpan(bufferWriter.GetSpan(64).Slice(0, 64));
        bufferWriter.Advance(64);
        FillSpan(bufferWriter.GetSpan(64).Slice(0, 64));
        bufferWriter.Advance(64);


        //var buffer = bufferWriter.GetBuffer();
        //Assert.AreEqual(64 * 3, buffer.Length);

        bufferWriter.ReleaseTo(127);

        var buffer2 = bufferWriter.GetBuffer();

    }

    [Test]
    public void FuzzBufferTests()
    {
        var bufferWriter = BufferWriter<byte>.Create(128);
        var random = new Random();
        var bufferSize = 0;

        var increaseSize = 64;
        var decreaseSize = 64;

        for (int i = 0; i < 100000; i++)
        {
            var size = random.Next(1, increaseSize);
            bufferSize += size;
            var span = bufferWriter.GetSpan(size).Slice(0, size);
            FillSpan(span);
            bufferWriter.Advance(size);

            var addBufferLen = bufferWriter.GetBuffer().Length;
            Assert.AreEqual(bufferSize, addBufferLen);

            var deallocateAmount = random.Next(1, Math.Min(bufferSize, decreaseSize));
            bufferWriter.ReleaseTo(deallocateAmount);

            bufferSize -= deallocateAmount;

            var bufferLen = bufferWriter.GetBuffer().Length;
            Assert.AreEqual(bufferSize, bufferLen);

            if (bufferLen > 1024)
                decreaseSize = 128;
            else if (bufferLen < 128)
                decreaseSize = 64;
        }

        /*var span = bufferWriter.GetSpan(128);
        bufferWriter.Advance(120);
        bufferWriter.Advance(120);
        bufferWriter.Flush().Dispose();
        

        var span = bufferWriter.GetSpan(120);
        bufferWriter.Advance(120);
        bufferWriter.Flush().Dispose();*/

        FillSpan(bufferWriter.GetSpan(64).Slice(0, 64));
        bufferWriter.Advance(64);
        FillSpan(bufferWriter.GetSpan(64).Slice(0, 64));
        bufferWriter.Advance(64);


        //var buffer = bufferWriter.GetBuffer();
        //Assert.AreEqual(64 * 3, buffer.Length);

        bufferWriter.ReleaseTo(127);

        var buffer2 = bufferWriter.GetBuffer();

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
