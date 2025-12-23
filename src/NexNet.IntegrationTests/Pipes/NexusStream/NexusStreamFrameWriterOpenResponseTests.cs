using System.IO.Pipelines;
using System.Threading.Tasks;
using NexNet.Pipes.NexusStream;
using NexNet.Pipes.NexusStream.Frames;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class NexusStreamFrameWriterOpenResponseTests
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
    public async Task WriteOpenResponseAsync_Success_WritesCorrectFrame()
    {
        var metadata = new NexusStreamMetadata
        {
            Length = 1024,
            HasKnownLength = true,
            CanSeek = true,
            CanRead = true,
            CanWrite = false
        };
        var frame = new OpenResponseFrame(metadata);

        await _writer.WriteOpenResponseAsync(frame);

        var result = await _reader.ReadFrameAsync();
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Header.Type, Is.EqualTo(FrameType.OpenResponse));

        var parsed = NexusStreamFrameReader.ParseOpenResponse(result.Value.Payload);
        Assert.That(parsed.Success, Is.True);
        Assert.That(parsed.Metadata.Length, Is.EqualTo(1024));
        Assert.That(parsed.Metadata.CanSeek, Is.True);
        Assert.That(parsed.Metadata.CanRead, Is.True);
        Assert.That(parsed.Metadata.CanWrite, Is.False);
    }

    [Test]
    public async Task WriteOpenResponseAsync_Failure_WritesCorrectFrame()
    {
        var frame = new OpenResponseFrame(StreamErrorCode.FileNotFound, "File not found");

        await _writer.WriteOpenResponseAsync(frame);

        var result = await _reader.ReadFrameAsync();
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Header.Type, Is.EqualTo(FrameType.OpenResponse));

        var parsed = NexusStreamFrameReader.ParseOpenResponse(result.Value.Payload);
        Assert.That(parsed.Success, Is.False);
        Assert.That(parsed.ErrorCode, Is.EqualTo(StreamErrorCode.FileNotFound));
        Assert.That(parsed.ErrorMessage, Is.EqualTo("File not found"));
    }

    [Test]
    public async Task WriteOpenResponseAsync_PayloadLength_MatchesExpected()
    {
        var metadata = new NexusStreamMetadata
        {
            Length = 5000,
            HasKnownLength = true,
            CanSeek = true,
            CanRead = true,
            CanWrite = true
        };
        var frame = new OpenResponseFrame(metadata);
        var expectedPayloadSize = frame.GetPayloadSize();

        await _writer.WriteOpenResponseAsync(frame);

        var result = await _reader.ReadFrameAsync();
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Header.PayloadLength, Is.EqualTo(expectedPayloadSize));
    }
}
