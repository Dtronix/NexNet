using System;
using NexNet.Pipes.NexusStream;
using NexNet.Pipes.NexusStream.Frames;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class WriteResponseFrameTests
{
    [Test]
    public void SuccessConstructor_SetsProperties()
    {
        var frame = new WriteResponseFrame(1024, 2048);

        Assert.That(frame.Success, Is.True);
        Assert.That(frame.BytesWritten, Is.EqualTo(1024));
        Assert.That(frame.Position, Is.EqualTo(2048));
        Assert.That(frame.ErrorCode, Is.EqualTo(StreamErrorCode.Success));
    }

    [Test]
    public void FailureConstructor_SetsProperties()
    {
        var frame = new WriteResponseFrame(StreamErrorCode.DiskFull, 500);

        Assert.That(frame.Success, Is.False);
        Assert.That(frame.BytesWritten, Is.EqualTo(0));
        Assert.That(frame.Position, Is.EqualTo(500));
        Assert.That(frame.ErrorCode, Is.EqualTo(StreamErrorCode.DiskFull));
    }

    [Test]
    public void FailureConstructor_DefaultPosition()
    {
        var frame = new WriteResponseFrame(StreamErrorCode.IoError);

        Assert.That(frame.Success, Is.False);
        Assert.That(frame.Position, Is.EqualTo(0));
    }

    [Test]
    public void GetPayloadSize_ReturnsCorrectSize()
    {
        var frame = new WriteResponseFrame(1024, 2048);
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(WriteResponseFrame.Size));
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(17)); // 1 + 4 + 8 + 4
    }

    [Test]
    public void Roundtrip_Success()
    {
        Span<byte> buffer = stackalloc byte[WriteResponseFrame.Size];
        var original = new WriteResponseFrame(5000, 10000);

        original.Write(buffer);
        var result = WriteResponseFrame.Read(buffer);

        Assert.That(result.Success, Is.True);
        Assert.That(result.BytesWritten, Is.EqualTo(5000));
        Assert.That(result.Position, Is.EqualTo(10000));
        Assert.That(result.ErrorCode, Is.EqualTo(StreamErrorCode.Success));
    }

    [Test]
    public void Roundtrip_Failure()
    {
        Span<byte> buffer = stackalloc byte[WriteResponseFrame.Size];
        var original = new WriteResponseFrame(StreamErrorCode.AccessDenied, 1000);

        original.Write(buffer);
        var result = WriteResponseFrame.Read(buffer);

        Assert.That(result.Success, Is.False);
        Assert.That(result.BytesWritten, Is.EqualTo(0));
        Assert.That(result.Position, Is.EqualTo(1000));
        Assert.That(result.ErrorCode, Is.EqualTo(StreamErrorCode.AccessDenied));
    }

    [Test]
    public void Roundtrip_LargePosition()
    {
        Span<byte> buffer = stackalloc byte[WriteResponseFrame.Size];
        var original = new WriteResponseFrame(100, long.MaxValue);

        original.Write(buffer);
        var result = WriteResponseFrame.Read(buffer);

        Assert.That(result.Position, Is.EqualTo(long.MaxValue));
    }

    [Test]
    public void Roundtrip_MaxBytesWritten()
    {
        Span<byte> buffer = stackalloc byte[WriteResponseFrame.Size];
        var original = new WriteResponseFrame(int.MaxValue, 0);

        original.Write(buffer);
        var result = WriteResponseFrame.Read(buffer);

        Assert.That(result.BytesWritten, Is.EqualTo(int.MaxValue));
    }

    [TestCase(StreamErrorCode.Success)]
    [TestCase(StreamErrorCode.FileNotFound)]
    [TestCase(StreamErrorCode.AccessDenied)]
    [TestCase(StreamErrorCode.DiskFull)]
    [TestCase(StreamErrorCode.IoError)]
    [TestCase(StreamErrorCode.ProtocolError)]
    public void Roundtrip_AllErrorCodes(StreamErrorCode errorCode)
    {
        Span<byte> buffer = stackalloc byte[WriteResponseFrame.Size];
        var original = new WriteResponseFrame(errorCode);

        original.Write(buffer);
        var result = WriteResponseFrame.Read(buffer);

        Assert.That(result.ErrorCode, Is.EqualTo(errorCode));
    }

    [Test]
    public void WireFormat_Success()
    {
        var frame = new WriteResponseFrame(0x01020304, 0x0102030405060708);
        var buffer = new byte[WriteResponseFrame.Size];

        frame.Write(buffer);

        // Success flag
        Assert.That(buffer[0], Is.EqualTo(1), "Success flag");

        // BytesWritten (little-endian)
        Assert.That(buffer[1], Is.EqualTo(0x04), "BytesWritten byte 0");
        Assert.That(buffer[2], Is.EqualTo(0x03), "BytesWritten byte 1");
        Assert.That(buffer[3], Is.EqualTo(0x02), "BytesWritten byte 2");
        Assert.That(buffer[4], Is.EqualTo(0x01), "BytesWritten byte 3");

        // Position (little-endian)
        Assert.That(buffer[5], Is.EqualTo(0x08), "Position byte 0");
        Assert.That(buffer[12], Is.EqualTo(0x01), "Position byte 7");
    }

    [Test]
    public void ToString_Success_ContainsKeyInfo()
    {
        var frame = new WriteResponseFrame(1024, 2048);
        var str = frame.ToString();

        Assert.That(str, Does.Contain("Success = true"));
        Assert.That(str, Does.Contain("1024"));
        Assert.That(str, Does.Contain("2048"));
    }

    [Test]
    public void ToString_Failure_ContainsKeyInfo()
    {
        var frame = new WriteResponseFrame(StreamErrorCode.DiskFull, 500);
        var str = frame.ToString();

        Assert.That(str, Does.Contain("Success = false"));
        Assert.That(str, Does.Contain("DiskFull"));
    }
}
