using System;
using NexNet.Pipes.NexusStream;
using NexNet.Pipes.NexusStream.Frames;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class SetLengthResponseFrameTests
{
    [Test]
    public void Constructor_Success_SetsProperties()
    {
        var frame = new SetLengthResponseFrame(1024, 500);

        Assert.That(frame.Success, Is.True);
        Assert.That(frame.NewLength, Is.EqualTo(1024));
        Assert.That(frame.Position, Is.EqualTo(500));
        Assert.That(frame.ErrorCode, Is.EqualTo(StreamErrorCode.Success));
    }

    [Test]
    public void Constructor_Failure_SetsProperties()
    {
        var frame = new SetLengthResponseFrame(StreamErrorCode.DiskFull, 2048, 500);

        Assert.That(frame.Success, Is.False);
        Assert.That(frame.NewLength, Is.EqualTo(2048));
        Assert.That(frame.Position, Is.EqualTo(500));
        Assert.That(frame.ErrorCode, Is.EqualTo(StreamErrorCode.DiskFull));
    }

    [Test]
    public void GetPayloadSize_ReturnsCorrectSize()
    {
        var frame = new SetLengthResponseFrame(0, 0);
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(SetLengthResponseFrame.Size));
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(21)); // 1 + 8 + 8 + 4
    }

    [Test]
    public void Roundtrip_Success()
    {
        Span<byte> buffer = stackalloc byte[SetLengthResponseFrame.Size];
        var original = new SetLengthResponseFrame(5000, 4000);

        original.Write(buffer);
        var result = SetLengthResponseFrame.Read(buffer);

        Assert.That(result.Success, Is.True);
        Assert.That(result.NewLength, Is.EqualTo(5000));
        Assert.That(result.Position, Is.EqualTo(4000));
        Assert.That(result.ErrorCode, Is.EqualTo(StreamErrorCode.Success));
    }

    [Test]
    public void Roundtrip_Failure()
    {
        Span<byte> buffer = stackalloc byte[SetLengthResponseFrame.Size];
        var original = new SetLengthResponseFrame(StreamErrorCode.DiskFull, 2048, 1000);

        original.Write(buffer);
        var result = SetLengthResponseFrame.Read(buffer);

        Assert.That(result.Success, Is.False);
        Assert.That(result.NewLength, Is.EqualTo(2048));
        Assert.That(result.Position, Is.EqualTo(1000));
        Assert.That(result.ErrorCode, Is.EqualTo(StreamErrorCode.DiskFull));
    }

    [Test]
    public void Roundtrip_PositionAdjusted()
    {
        // When SetLength truncates, position may be adjusted to not exceed new length
        Span<byte> buffer = stackalloc byte[SetLengthResponseFrame.Size];
        var original = new SetLengthResponseFrame(100, 100); // Position adjusted to new length

        original.Write(buffer);
        var result = SetLengthResponseFrame.Read(buffer);

        Assert.That(result.Success, Is.True);
        Assert.That(result.NewLength, Is.EqualTo(100));
        Assert.That(result.Position, Is.EqualTo(100));
    }

    [Test]
    public void Roundtrip_MaxValues()
    {
        Span<byte> buffer = stackalloc byte[SetLengthResponseFrame.Size];
        var original = new SetLengthResponseFrame(long.MaxValue, long.MaxValue);

        original.Write(buffer);
        var result = SetLengthResponseFrame.Read(buffer);

        Assert.That(result.NewLength, Is.EqualTo(long.MaxValue));
        Assert.That(result.Position, Is.EqualTo(long.MaxValue));
    }

    [Test]
    public void WireFormat_LittleEndian()
    {
        var frame = new SetLengthResponseFrame(StreamErrorCode.DiskFull, 0x0102030405060708, 0x1112131415161718);
        var buffer = new byte[SetLengthResponseFrame.Size];

        frame.Write(buffer);

        // Success flag
        Assert.That(buffer[0], Is.EqualTo(0), "Success flag");

        // NewLength (little-endian)
        Assert.That(buffer[1], Is.EqualTo(0x08), "NewLength byte 0");
        Assert.That(buffer[8], Is.EqualTo(0x01), "NewLength byte 7");

        // Position (little-endian)
        Assert.That(buffer[9], Is.EqualTo(0x18), "Position byte 0");
        Assert.That(buffer[16], Is.EqualTo(0x11), "Position byte 7");
    }

    [Test]
    public void ToString_Success_ContainsKeyInfo()
    {
        var frame = new SetLengthResponseFrame(2048, 1000);
        var str = frame.ToString();

        Assert.That(str, Does.Contain("Success"));
        Assert.That(str, Does.Contain("2048"));
        Assert.That(str, Does.Contain("1000"));
        Assert.That(str, Does.Contain("SetLengthResponseFrame"));
    }

    [Test]
    public void ToString_Failure_ContainsKeyInfo()
    {
        var frame = new SetLengthResponseFrame(StreamErrorCode.DiskFull, 1024, 500);
        var str = frame.ToString();

        Assert.That(str, Does.Contain("Failed"));
        Assert.That(str, Does.Contain("DiskFull"));
        Assert.That(str, Does.Contain("1024"));
        Assert.That(str, Does.Contain("500"));
    }
}
