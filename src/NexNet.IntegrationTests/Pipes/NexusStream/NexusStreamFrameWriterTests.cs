using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Tasks;
using NexNet.Pipes.NexusStream;
using NexNet.Pipes.NexusStream.Frames;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class NexusStreamFrameWriterTests
{
    private Pipe _pipe = null!;
    private NexusStreamFrameWriter _writer = null!;

    [SetUp]
    public void SetUp()
    {
        _pipe = new Pipe();
        _writer = new NexusStreamFrameWriter(_pipe.Writer);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _pipe.Writer.CompleteAsync();
        await _pipe.Reader.CompleteAsync();
    }

    [Test]
    public void Constructor_NullOutput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new NexusStreamFrameWriter(null!));
    }

    [Test]
    public void MaxPayloadSize_DefaultIs512KB()
    {
        Assert.That(_writer.MaxPayloadSize, Is.EqualTo(NexusStreamFrameWriter.DefaultMaxPayloadSize));
        Assert.That(_writer.MaxPayloadSize, Is.EqualTo(524288)); // 0.5 MB
    }

    [Test]
    public void MaxPayloadSize_CustomValue()
    {
        var writer = new NexusStreamFrameWriter(_pipe.Writer, 1024);
        Assert.That(writer.MaxPayloadSize, Is.EqualTo(1024));
    }

    [Test]
    public async Task WriteFrameAsync_EmptyPayload_WritesHeaderOnly()
    {
        await _writer.WriteFrameAsync(FrameType.Flush, ReadOnlyMemory<byte>.Empty);

        var result = await _pipe.Reader.ReadAsync();
        var buffer = result.Buffer;

        Assert.That(buffer.Length, Is.EqualTo(FrameHeader.Size));

        var header = FrameHeader.Read(buffer.FirstSpan);
        Assert.That(header.Type, Is.EqualTo(FrameType.Flush));
        Assert.That(header.PayloadLength, Is.EqualTo(0));
    }

    [Test]
    public async Task WriteFrameAsync_WithPayload_WritesHeaderAndPayload()
    {
        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        await _writer.WriteFrameAsync(FrameType.Data, payload);

        var result = await _pipe.Reader.ReadAsync();
        var buffer = result.Buffer;

        Assert.That(buffer.Length, Is.EqualTo(FrameHeader.Size + payload.Length));

        var header = FrameHeader.Read(buffer.FirstSpan);
        Assert.That(header.Type, Is.EqualTo(FrameType.Data));
        Assert.That(header.PayloadLength, Is.EqualTo(4));

        // Check payload
        var payloadSpan = buffer.Slice(FrameHeader.Size).FirstSpan;
        Assert.That(payloadSpan[0], Is.EqualTo(0x01));
        Assert.That(payloadSpan[3], Is.EqualTo(0x04));
    }

    [Test]
    public async Task WriteOpenAsync_WritesCorrectFrame()
    {
        var frame = new OpenFrame("/test/file.txt", StreamAccessMode.Read, StreamShareMode.None, -1);
        await _writer.WriteOpenAsync(frame);

        var result = await _pipe.Reader.ReadAsync();
        var buffer = result.Buffer;

        var header = FrameHeader.Read(buffer.FirstSpan);
        Assert.That(header.Type, Is.EqualTo(FrameType.Open));
        Assert.That(header.PayloadLength, Is.EqualTo(frame.GetPayloadSize()));

        // Parse and verify payload
        var payloadBuffer = buffer.Slice(FrameHeader.Size, header.PayloadLength);
        var parsed = NexusStreamFrameReader.ParseOpen(payloadBuffer);

        Assert.That(parsed.ResourceId, Is.EqualTo(frame.ResourceId));
        Assert.That(parsed.Access, Is.EqualTo(frame.Access));
        Assert.That(parsed.Share, Is.EqualTo(frame.Share));
        Assert.That(parsed.ResumePosition, Is.EqualTo(frame.ResumePosition));
    }

    [Test]
    public async Task WriteCloseAsync_WritesCorrectFrame()
    {
        var frame = new CloseFrame(true);
        await _writer.WriteCloseAsync(frame);

        var result = await _pipe.Reader.ReadAsync();
        var buffer = result.Buffer;

        var header = FrameHeader.Read(buffer.FirstSpan);
        Assert.That(header.Type, Is.EqualTo(FrameType.Close));
        Assert.That(header.PayloadLength, Is.EqualTo(1));

        // Parse and verify payload
        var payloadBuffer = buffer.Slice(FrameHeader.Size, header.PayloadLength);
        var parsed = NexusStreamFrameReader.ParseClose(payloadBuffer);

        Assert.That(parsed.Graceful, Is.True);
    }

    [Test]
    public async Task WriteErrorAsync_WritesCorrectFrame()
    {
        var frame = new ErrorFrame(StreamErrorCode.AccessDenied, 4096, "Access denied");
        await _writer.WriteErrorAsync(frame);

        var result = await _pipe.Reader.ReadAsync();
        var buffer = result.Buffer;

        var header = FrameHeader.Read(buffer.FirstSpan);
        Assert.That(header.Type, Is.EqualTo(FrameType.Error));
        Assert.That(header.PayloadLength, Is.EqualTo(frame.GetPayloadSize()));

        // Parse and verify payload
        var payloadBuffer = buffer.Slice(FrameHeader.Size, header.PayloadLength);
        var parsed = NexusStreamFrameReader.ParseError(payloadBuffer);

        Assert.That(parsed.ErrorCode, Is.EqualTo(frame.ErrorCode));
        Assert.That(parsed.Position, Is.EqualTo(frame.Position));
        Assert.That(parsed.Message, Is.EqualTo(frame.Message));
    }

    [Test]
    public async Task WriteEmptyFrameAsync_WritesHeaderOnly()
    {
        await _writer.WriteEmptyFrameAsync(FrameType.GetMetadata);

        var result = await _pipe.Reader.ReadAsync();
        var buffer = result.Buffer;

        Assert.That(buffer.Length, Is.EqualTo(FrameHeader.Size));

        var header = FrameHeader.Read(buffer.FirstSpan);
        Assert.That(header.Type, Is.EqualTo(FrameType.GetMetadata));
        Assert.That(header.PayloadLength, Is.EqualTo(0));
    }

    [Test]
    public async Task MultipleWrites_ProducesMultipleFrames()
    {
        var frame1 = new OpenFrame("/test1", StreamAccessMode.Read);
        var frame2 = new CloseFrame(true);

        await _writer.WriteOpenAsync(frame1);
        await _writer.WriteCloseAsync(frame2);

        var result = await _pipe.Reader.ReadAsync();
        var buffer = result.Buffer;

        // First frame
        var header1 = FrameHeader.Read(buffer.FirstSpan);
        Assert.That(header1.Type, Is.EqualTo(FrameType.Open));

        // Advance past first frame
        buffer = buffer.Slice(header1.TotalFrameSize);

        // Second frame
        var header2 = FrameHeader.Read(buffer.FirstSpan);
        Assert.That(header2.Type, Is.EqualTo(FrameType.Close));
    }

    [Test]
    public async Task ConcurrentWrites_AreSerializedByLock()
    {
        // This test verifies that concurrent writes don't corrupt each other
        var tasks = new Task[10];

        for (int i = 0; i < 10; i++)
        {
            var index = i;
            tasks[i] = Task.Run(async () =>
            {
                var frame = new OpenFrame($"/test{index}", StreamAccessMode.Read);
                await _writer.WriteOpenAsync(frame);
            });
        }

        await Task.WhenAll(tasks);

        // Read all frames and verify they're all valid
        var framesRead = 0;
        while (framesRead < 10)
        {
            var result = await _pipe.Reader.ReadAsync();
            var buffer = result.Buffer;

            while (buffer.Length >= FrameHeader.Size)
            {
                var header = FrameHeader.Read(buffer.FirstSpan);
                if (buffer.Length < header.TotalFrameSize)
                    break;

                Assert.That(header.Type, Is.EqualTo(FrameType.Open));
                buffer = buffer.Slice(header.TotalFrameSize);
                framesRead++;
            }

            _pipe.Reader.AdvanceTo(buffer.Start, buffer.End);
        }

        Assert.That(framesRead, Is.EqualTo(10));
    }
}
