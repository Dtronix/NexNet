using System;
using NexNet.Pipes.NexusStream;
using NexNet.Pipes.NexusStream.Frames;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class FlushResponseFrameTests
{
    [Test]
    public void Constructor_Success_SetsProperties()
    {
        var frame = new FlushResponseFrame(1024);

        Assert.That(frame.Success, Is.True);
        Assert.That(frame.Position, Is.EqualTo(1024));
        Assert.That(frame.ErrorCode, Is.EqualTo(StreamErrorCode.Success));
    }

    [Test]
    public void Constructor_Failure_SetsProperties()
    {
        var frame = new FlushResponseFrame(StreamErrorCode.IoError, 500);

        Assert.That(frame.Success, Is.False);
        Assert.That(frame.Position, Is.EqualTo(500));
        Assert.That(frame.ErrorCode, Is.EqualTo(StreamErrorCode.IoError));
    }

    [Test]
    public void GetPayloadSize_ReturnsCorrectSize()
    {
        var frame = new FlushResponseFrame(0);
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(FlushResponseFrame.Size));
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(13)); // 1 + 8 + 4
    }

    [Test]
    public void Roundtrip_Success()
    {
        Span<byte> buffer = stackalloc byte[FlushResponseFrame.Size];
        var original = new FlushResponseFrame(5000);

        original.Write(buffer);
        var result = FlushResponseFrame.Read(buffer);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Position, Is.EqualTo(5000));
        Assert.That(result.ErrorCode, Is.EqualTo(StreamErrorCode.Success));
    }

    [Test]
    public void Roundtrip_Failure()
    {
        Span<byte> buffer = stackalloc byte[FlushResponseFrame.Size];
        var original = new FlushResponseFrame(StreamErrorCode.IoError, 1000);

        original.Write(buffer);
        var result = FlushResponseFrame.Read(buffer);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Position, Is.EqualTo(1000));
        Assert.That(result.ErrorCode, Is.EqualTo(StreamErrorCode.IoError));
    }

    [Test]
    public void Roundtrip_MaxPosition()
    {
        Span<byte> buffer = stackalloc byte[FlushResponseFrame.Size];
        var original = new FlushResponseFrame(long.MaxValue);

        original.Write(buffer);
        var result = FlushResponseFrame.Read(buffer);

        Assert.That(result.Position, Is.EqualTo(long.MaxValue));
    }

    [Test]
    public void WireFormat_LittleEndian()
    {
        var frame = new FlushResponseFrame(StreamErrorCode.IoError, 0x0102030405060708);
        var buffer = new byte[FlushResponseFrame.Size];

        frame.Write(buffer);

        // Success flag
        Assert.That(buffer[0], Is.EqualTo(0), "Success flag");

        // Position (little-endian)
        Assert.That(buffer[1], Is.EqualTo(0x08), "Position byte 0");
        Assert.That(buffer[8], Is.EqualTo(0x01), "Position byte 7");
    }

    [Test]
    public void ToString_Success_ContainsKeyInfo()
    {
        var frame = new FlushResponseFrame(2048);
        var str = frame.ToString();

        Assert.That(str, Does.Contain("Success"));
        Assert.That(str, Does.Contain("2048"));
        Assert.That(str, Does.Contain("FlushResponseFrame"));
    }

    [Test]
    public void ToString_Failure_ContainsKeyInfo()
    {
        var frame = new FlushResponseFrame(StreamErrorCode.IoError, 1000);
        var str = frame.ToString();

        Assert.That(str, Does.Contain("Failed"));
        Assert.That(str, Does.Contain("IoError"));
        Assert.That(str, Does.Contain("1000"));
    }
}
