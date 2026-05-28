using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Pipes.NexusStream;
using NexNet.Pipes.NexusStream.Frames;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class NexusStreamFrameReaderTests
{
    private Pipe _pipe = null!;
    private NexusStreamFrameReader _reader = null!;
    private NexusStreamFrameWriter _writer = null!;

    [SetUp]
    public void SetUp()
    {
        _pipe = new Pipe();
        _reader = new NexusStreamFrameReader(_pipe.Reader);
        _writer = new NexusStreamFrameWriter(_pipe.Writer);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _pipe.Writer.CompleteAsync();
        await _pipe.Reader.CompleteAsync();
    }

    [Test]
    public void Constructor_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new NexusStreamFrameReader(null!));
    }

    [Test]
    public async Task ReadFrameAsync_EmptyPipeCompleted_ReturnsNull()
    {
        await _pipe.Writer.CompleteAsync();

        var result = await _reader.ReadFrameAsync();

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task ReadFrameAsync_ReadsOpenFrame()
    {
        var frame = new OpenFrame("/test/file.txt", StreamAccessMode.ReadWrite, StreamShareMode.Read, 1000);
        await _writer.WriteOpenAsync(frame);

        var result = await _reader.ReadFrameAsync();

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Header.Type, Is.EqualTo(FrameType.Open));

        // Parse immediately while payload is valid
        var parsed = NexusStreamFrameReader.ParseOpen(result.Value.Payload);
        Assert.That(parsed.ResourceId, Is.EqualTo(frame.ResourceId));
        Assert.That(parsed.Access, Is.EqualTo(frame.Access));
        Assert.That(parsed.Share, Is.EqualTo(frame.Share));
        Assert.That(parsed.ResumePosition, Is.EqualTo(frame.ResumePosition));
    }

    [Test]
    public async Task ReadFrameAsync_ReadsCloseFrame()
    {
        var frame = new CloseFrame(false);
        await _writer.WriteCloseAsync(frame);

        var result = await _reader.ReadFrameAsync();

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Header.Type, Is.EqualTo(FrameType.Close));

        // Parse immediately while payload is valid
        var parsed = NexusStreamFrameReader.ParseClose(result.Value.Payload);
        Assert.That(parsed.Graceful, Is.EqualTo(frame.Graceful));
    }

    [Test]
    public async Task ReadFrameAsync_ReadsErrorFrame()
    {
        var frame = new ErrorFrame(StreamErrorCode.FileNotFound, 0, "Not found");
        await _writer.WriteErrorAsync(frame);

        var result = await _reader.ReadFrameAsync();

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Header.Type, Is.EqualTo(FrameType.Error));

        // Parse immediately while payload is valid
        var parsed = NexusStreamFrameReader.ParseError(result.Value.Payload);
        Assert.That(parsed.ErrorCode, Is.EqualTo(frame.ErrorCode));
        Assert.That(parsed.Position, Is.EqualTo(frame.Position));
        Assert.That(parsed.Message, Is.EqualTo(frame.Message));
    }

    [Test]
    public async Task ReadFrameAsync_ReadsEmptyFrame()
    {
        await _writer.WriteEmptyFrameAsync(FrameType.Flush);

        var result = await _reader.ReadFrameAsync();

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Header.Type, Is.EqualTo(FrameType.Flush));
        Assert.That(result.Value.Header.PayloadLength, Is.EqualTo(0));
        Assert.That(result.Value.Payload.Length, Is.EqualTo(0));
    }

    [Test]
    public async Task ReadFrameAsync_ReadsMultipleFrames()
    {
        await _writer.WriteOpenAsync(new OpenFrame("/test", StreamAccessMode.Read));
        await _writer.WriteCloseAsync(new CloseFrame(true));

        var result1 = await _reader.ReadFrameAsync();
        Assert.That(result1!.Value.Header.Type, Is.EqualTo(FrameType.Open));
        // Parse before next read
        var open = NexusStreamFrameReader.ParseOpen(result1.Value.Payload);
        Assert.That(open.ResourceId, Is.EqualTo("/test"));

        var result2 = await _reader.ReadFrameAsync();
        Assert.That(result2!.Value.Header.Type, Is.EqualTo(FrameType.Close));
        var close = NexusStreamFrameReader.ParseClose(result2.Value.Payload);
        Assert.That(close.Graceful, Is.True);
    }

    [Test]
    public async Task ReadFrameAsync_IncompletePipeCompleted_ThrowsInvalidDataException()
    {
        // Write incomplete frame (just header, no payload)
        var buffer = _pipe.Writer.GetMemory(FrameHeader.Size);
        new FrameHeader(FrameType.Open, 100).Write(buffer.Span);
        _pipe.Writer.Advance(FrameHeader.Size);
        await _pipe.Writer.FlushAsync();
        await _pipe.Writer.CompleteAsync();

        Assert.ThrowsAsync<InvalidDataException>(async () => await _reader.ReadFrameAsync());
    }

    [Test]
    public void ReadFrameAsync_CancellationToken_Respected()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // TaskCanceledException is a subclass of OperationCanceledException
        var ex = Assert.CatchAsync<OperationCanceledException>(async () =>
            await _reader.ReadFrameAsync(cts.Token));
        Assert.That(ex, Is.Not.Null);
    }

    [Test]
    public async Task ReadFrameAsync_WaitsForCompleteFrame()
    {
        // Write header only
        var header = new FrameHeader(FrameType.Close, 1);
        var headerBuffer = _pipe.Writer.GetMemory(FrameHeader.Size);
        header.Write(headerBuffer.Span);
        _pipe.Writer.Advance(FrameHeader.Size);
        await _pipe.Writer.FlushAsync();

        // Start reading (will wait for payload)
        var readTask = _reader.ReadFrameAsync();
        await Task.Delay(50); // Give time for read to start

        Assert.That(readTask.IsCompleted, Is.False);

        // Write payload
        var payloadBuffer = _pipe.Writer.GetMemory(1);
        payloadBuffer.Span[0] = 1; // Graceful = true
        _pipe.Writer.Advance(1);
        await _pipe.Writer.FlushAsync();

        // Now read should complete
        var result = await readTask;
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Header.Type, Is.EqualTo(FrameType.Close));
    }

    [Test]
    public void ParseOpen_FromSingleSegmentSequence()
    {
        var buffer = new byte[100];
        var frame = new OpenFrame("/test/path", StreamAccessMode.ReadWrite);
        frame.Write(buffer);

        var sequence = new ReadOnlySequence<byte>(buffer, 0, frame.GetPayloadSize());
        var parsed = NexusStreamFrameReader.ParseOpen(sequence);

        Assert.That(parsed.ResourceId, Is.EqualTo(frame.ResourceId));
        Assert.That(parsed.Access, Is.EqualTo(frame.Access));
    }

    [Test]
    public void ParseClose_FromSingleSegmentSequence()
    {
        var buffer = new byte[1];
        var frame = new CloseFrame(true);
        frame.Write(buffer);

        var sequence = new ReadOnlySequence<byte>(buffer);
        var parsed = NexusStreamFrameReader.ParseClose(sequence);

        Assert.That(parsed.Graceful, Is.True);
    }

    [Test]
    public void ParseError_FromSingleSegmentSequence()
    {
        var frame = new ErrorFrame(StreamErrorCode.IoError, 999, "Test error");
        var buffer = new byte[frame.GetPayloadSize()];
        frame.Write(buffer);

        var sequence = new ReadOnlySequence<byte>(buffer);
        var parsed = NexusStreamFrameReader.ParseError(sequence);

        Assert.That(parsed.ErrorCode, Is.EqualTo(frame.ErrorCode));
        Assert.That(parsed.Position, Is.EqualTo(frame.Position));
        Assert.That(parsed.Message, Is.EqualTo(frame.Message));
    }

    [Test]
    public void TryReadFrame_NoData_ReturnsFalse()
    {
        var success = _reader.TryReadFrame(out var header, out var payload);

        Assert.That(success, Is.False);
    }

    [Test]
    public async Task TryReadFrame_WithCompleteFrame_ReturnsTrue()
    {
        await _writer.WriteCloseAsync(new CloseFrame(true));

        var success = _reader.TryReadFrame(out var header, out var payload);

        Assert.That(success, Is.True);
        Assert.That(header.Type, Is.EqualTo(FrameType.Close));

        // Parse while payload is valid
        var parsed = NexusStreamFrameReader.ParseClose(payload);
        Assert.That(parsed.Graceful, Is.True);
    }

    [Test]
    public async Task DeferredAdvance_PayloadValidUntilNextRead()
    {
        var frame1 = new OpenFrame("/test1", StreamAccessMode.Read);
        var frame2 = new OpenFrame("/test2", StreamAccessMode.Write);
        await _writer.WriteOpenAsync(frame1);
        await _writer.WriteOpenAsync(frame2);

        // Read first frame
        var result1 = await _reader.ReadFrameAsync();
        Assert.That(result1, Is.Not.Null);

        // Payload should still be valid - parse it
        var parsed1 = NexusStreamFrameReader.ParseOpen(result1!.Value.Payload);
        Assert.That(parsed1.ResourceId, Is.EqualTo("/test1"));

        // Read second frame (this advances past the first)
        var result2 = await _reader.ReadFrameAsync();
        Assert.That(result2, Is.Not.Null);

        var parsed2 = NexusStreamFrameReader.ParseOpen(result2!.Value.Payload);
        Assert.That(parsed2.ResourceId, Is.EqualTo("/test2"));
    }
}
