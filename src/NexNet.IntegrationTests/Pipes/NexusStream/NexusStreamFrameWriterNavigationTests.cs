using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using NexNet.Pipes.NexusStream;
using NexNet.Pipes.NexusStream.Frames;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class NexusStreamFrameWriterNavigationTests
{
    private Pipe _pipe = null!;
    private NexusStreamFrameWriter _writer = null!;
    private NexusStreamFrameReader _reader = null!;

    [SetUp]
    public void SetUp()
    {
        _pipe = new Pipe();
        _writer = new NexusStreamFrameWriter(_pipe.Writer);
        _reader = new NexusStreamFrameReader(_pipe.Reader);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _pipe.Writer.CompleteAsync();
        await _pipe.Reader.CompleteAsync();
    }

    [Test]
    public async Task WriteSeekAsync_WritesCorrectFrame()
    {
        var frame = new SeekFrame(1024, SeekOrigin.Begin);

        await _writer.WriteSeekAsync(frame);

        var result = await _reader.ReadFrameAsync();
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Header.Type, Is.EqualTo(FrameType.Seek));

        var parsed = NexusStreamFrameReader.ParseSeek(result.Value.Payload);
        Assert.That(parsed.Offset, Is.EqualTo(1024));
        Assert.That(parsed.Origin, Is.EqualTo(SeekOrigin.Begin));
    }

    [Test]
    public async Task WriteSeekResponseAsync_Success_WritesCorrectFrame()
    {
        var frame = new SeekResponseFrame(5000);

        await _writer.WriteSeekResponseAsync(frame);

        var result = await _reader.ReadFrameAsync();
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Header.Type, Is.EqualTo(FrameType.SeekResponse));

        var parsed = NexusStreamFrameReader.ParseSeekResponse(result.Value.Payload);
        Assert.That(parsed.Success, Is.True);
        Assert.That(parsed.Position, Is.EqualTo(5000));
    }

    [Test]
    public async Task WriteSeekResponseAsync_Failure_WritesCorrectFrame()
    {
        var frame = new SeekResponseFrame(StreamErrorCode.InvalidPosition, 1000);

        await _writer.WriteSeekResponseAsync(frame);

        var result = await _reader.ReadFrameAsync();
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Header.Type, Is.EqualTo(FrameType.SeekResponse));

        var parsed = NexusStreamFrameReader.ParseSeekResponse(result.Value.Payload);
        Assert.That(parsed.Success, Is.False);
        Assert.That(parsed.ErrorCode, Is.EqualTo(StreamErrorCode.InvalidPosition));
        Assert.That(parsed.Position, Is.EqualTo(1000));
    }

    [Test]
    public async Task WriteFlushAsync_WritesCorrectFrame()
    {
        await _writer.WriteFlushAsync();

        var result = await _reader.ReadFrameAsync();
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Header.Type, Is.EqualTo(FrameType.Flush));
        Assert.That(result.Value.Header.PayloadLength, Is.EqualTo(0));
    }

    [Test]
    public async Task WriteFlushResponseAsync_Success_WritesCorrectFrame()
    {
        var frame = new FlushResponseFrame(5000);

        await _writer.WriteFlushResponseAsync(frame);

        var result = await _reader.ReadFrameAsync();
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Header.Type, Is.EqualTo(FrameType.FlushResponse));

        var parsed = NexusStreamFrameReader.ParseFlushResponse(result.Value.Payload);
        Assert.That(parsed.Success, Is.True);
        Assert.That(parsed.Position, Is.EqualTo(5000));
    }

    [Test]
    public async Task WriteFlushResponseAsync_Failure_WritesCorrectFrame()
    {
        var frame = new FlushResponseFrame(StreamErrorCode.IoError, 1000);

        await _writer.WriteFlushResponseAsync(frame);

        var result = await _reader.ReadFrameAsync();
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Header.Type, Is.EqualTo(FrameType.FlushResponse));

        var parsed = NexusStreamFrameReader.ParseFlushResponse(result.Value.Payload);
        Assert.That(parsed.Success, Is.False);
        Assert.That(parsed.ErrorCode, Is.EqualTo(StreamErrorCode.IoError));
        Assert.That(parsed.Position, Is.EqualTo(1000));
    }

    [Test]
    public async Task WriteSetLengthAsync_WritesCorrectFrame()
    {
        var frame = new SetLengthFrame(2048);

        await _writer.WriteSetLengthAsync(frame);

        var result = await _reader.ReadFrameAsync();
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Header.Type, Is.EqualTo(FrameType.SetLength));

        var parsed = NexusStreamFrameReader.ParseSetLength(result.Value.Payload);
        Assert.That(parsed.Length, Is.EqualTo(2048));
    }

    [Test]
    public async Task WriteSetLengthResponseAsync_Success_WritesCorrectFrame()
    {
        var frame = new SetLengthResponseFrame(2048, 1000);

        await _writer.WriteSetLengthResponseAsync(frame);

        var result = await _reader.ReadFrameAsync();
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Header.Type, Is.EqualTo(FrameType.SetLengthResponse));

        var parsed = NexusStreamFrameReader.ParseSetLengthResponse(result.Value.Payload);
        Assert.That(parsed.Success, Is.True);
        Assert.That(parsed.NewLength, Is.EqualTo(2048));
        Assert.That(parsed.Position, Is.EqualTo(1000));
    }

    [Test]
    public async Task WriteSetLengthResponseAsync_Failure_WritesCorrectFrame()
    {
        var frame = new SetLengthResponseFrame(StreamErrorCode.DiskFull, 1024, 500);

        await _writer.WriteSetLengthResponseAsync(frame);

        var result = await _reader.ReadFrameAsync();
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Header.Type, Is.EqualTo(FrameType.SetLengthResponse));

        var parsed = NexusStreamFrameReader.ParseSetLengthResponse(result.Value.Payload);
        Assert.That(parsed.Success, Is.False);
        Assert.That(parsed.ErrorCode, Is.EqualTo(StreamErrorCode.DiskFull));
        Assert.That(parsed.NewLength, Is.EqualTo(1024));
        Assert.That(parsed.Position, Is.EqualTo(500));
    }

    [Test]
    public async Task WriteSeekAsync_AllOrigins()
    {
        foreach (var origin in new[] { SeekOrigin.Begin, SeekOrigin.Current, SeekOrigin.End })
        {
            var pipe = new Pipe();
            var writer = new NexusStreamFrameWriter(pipe.Writer);
            var reader = new NexusStreamFrameReader(pipe.Reader);

            var frame = new SeekFrame(100, origin);
            await writer.WriteSeekAsync(frame);

            var result = await reader.ReadFrameAsync();
            Assert.That(result, Is.Not.Null);

            var parsed = NexusStreamFrameReader.ParseSeek(result!.Value.Payload);
            Assert.That(parsed.Origin, Is.EqualTo(origin), $"Failed for origin {origin}");

            await pipe.Writer.CompleteAsync();
            await pipe.Reader.CompleteAsync();
        }
    }

    [Test]
    public async Task WriteSeekAsync_NegativeOffset()
    {
        var frame = new SeekFrame(-500, SeekOrigin.End);

        await _writer.WriteSeekAsync(frame);

        var result = await _reader.ReadFrameAsync();
        Assert.That(result, Is.Not.Null);

        var parsed = NexusStreamFrameReader.ParseSeek(result!.Value.Payload);
        Assert.That(parsed.Offset, Is.EqualTo(-500));
        Assert.That(parsed.Origin, Is.EqualTo(SeekOrigin.End));
    }
}
