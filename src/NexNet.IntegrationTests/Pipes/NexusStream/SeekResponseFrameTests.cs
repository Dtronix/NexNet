using System;
using NexNet.Pipes.NexusStream;
using NexNet.Pipes.NexusStream.Frames;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class SeekResponseFrameTests
{
    [Test]
    public void Constructor_Success_SetsProperties()
    {
        var frame = new SeekResponseFrame(1024);

        Assert.That(frame.Success, Is.True);
        Assert.That(frame.Position, Is.EqualTo(1024));
        Assert.That(frame.ErrorCode, Is.EqualTo(StreamErrorCode.Success));
    }

    [Test]
    public void Constructor_Failure_SetsProperties()
    {
        var frame = new SeekResponseFrame(StreamErrorCode.InvalidPosition, 500);

        Assert.That(frame.Success, Is.False);
        Assert.That(frame.Position, Is.EqualTo(500));
        Assert.That(frame.ErrorCode, Is.EqualTo(StreamErrorCode.InvalidPosition));
    }

    [Test]
    public void GetPayloadSize_ReturnsCorrectSize()
    {
        var frame = new SeekResponseFrame(0);
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(SeekResponseFrame.Size));
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(13)); // 1 + 8 + 4
    }

    [Test]
    public void Roundtrip_Success()
    {
        Span<byte> buffer = stackalloc byte[SeekResponseFrame.Size];
        var original = new SeekResponseFrame(5000);

        original.Write(buffer);
        var result = SeekResponseFrame.Read(buffer);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Position, Is.EqualTo(5000));
        Assert.That(result.ErrorCode, Is.EqualTo(StreamErrorCode.Success));
    }

    [Test]
    public void Roundtrip_Failure()
    {
        Span<byte> buffer = stackalloc byte[SeekResponseFrame.Size];
        var original = new SeekResponseFrame(StreamErrorCode.InvalidPosition, 1000);

        original.Write(buffer);
        var result = SeekResponseFrame.Read(buffer);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Position, Is.EqualTo(1000));
        Assert.That(result.ErrorCode, Is.EqualTo(StreamErrorCode.InvalidPosition));
    }

    [Test]
    public void Roundtrip_MaxPosition()
    {
        Span<byte> buffer = stackalloc byte[SeekResponseFrame.Size];
        var original = new SeekResponseFrame(long.MaxValue);

        original.Write(buffer);
        var result = SeekResponseFrame.Read(buffer);

        Assert.That(result.Position, Is.EqualTo(long.MaxValue));
    }

    [Test]
    public void WireFormat_LittleEndian()
    {
        var frame = new SeekResponseFrame(StreamErrorCode.InvalidPosition, 0x0102030405060708);
        var buffer = new byte[SeekResponseFrame.Size];

        frame.Write(buffer);

        // Success flag
        Assert.That(buffer[0], Is.EqualTo(0), "Success flag");

        // Position (little-endian)
        Assert.That(buffer[1], Is.EqualTo(0x08), "Position byte 0");
        Assert.That(buffer[8], Is.EqualTo(0x01), "Position byte 7");

        // ErrorCode (little-endian)
        var errorCodeOffset = 9;
        Assert.That(buffer[errorCodeOffset], Is.EqualTo((int)StreamErrorCode.InvalidPosition & 0xFF), "ErrorCode byte 0");
    }

    [Test]
    public void ToString_Success_ContainsKeyInfo()
    {
        var frame = new SeekResponseFrame(2048);
        var str = frame.ToString();

        Assert.That(str, Does.Contain("Success"));
        Assert.That(str, Does.Contain("2048"));
        Assert.That(str, Does.Contain("SeekResponseFrame"));
    }

    [Test]
    public void ToString_Failure_ContainsKeyInfo()
    {
        var frame = new SeekResponseFrame(StreamErrorCode.InvalidPosition, 1000);
        var str = frame.ToString();

        Assert.That(str, Does.Contain("Failed"));
        Assert.That(str, Does.Contain("InvalidPosition"));
        Assert.That(str, Does.Contain("1000"));
    }
}
