using System;
using NexNet.Pipes.NexusStream.Frames;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class FrameHeaderTests
{
    [Test]
    public void Size_Is5()
    {
        Assert.That(FrameHeader.Size, Is.EqualTo(5));
    }

    [Test]
    public void Constructor_SetsProperties()
    {
        var header = new FrameHeader(FrameType.Open, 100);

        Assert.That(header.Type, Is.EqualTo(FrameType.Open));
        Assert.That(header.PayloadLength, Is.EqualTo(100));
    }

    [Test]
    public void Write_WritesTypeAndLengthInLittleEndian()
    {
        var header = new FrameHeader(FrameType.Open, 0x12345678);
        Span<byte> buffer = stackalloc byte[5];

        header.Write(buffer);

        // Type byte
        Assert.That(buffer[0], Is.EqualTo((byte)FrameType.Open));
        // Length in little-endian
        Assert.That(buffer[1], Is.EqualTo(0x78));
        Assert.That(buffer[2], Is.EqualTo(0x56));
        Assert.That(buffer[3], Is.EqualTo(0x34));
        Assert.That(buffer[4], Is.EqualTo(0x12));
    }

    [Test]
    public void Read_ReadsTypeAndLengthCorrectly()
    {
        ReadOnlySpan<byte> buffer = new byte[] { 0x01, 0x78, 0x56, 0x34, 0x12 };

        var header = FrameHeader.Read(buffer);

        Assert.That(header.Type, Is.EqualTo(FrameType.Open));
        Assert.That(header.PayloadLength, Is.EqualTo(0x12345678));
    }

    [Test]
    public void Roundtrip_AllFrameTypes()
    {
        Span<byte> buffer = stackalloc byte[5];

        foreach (FrameType type in Enum.GetValues(typeof(FrameType)))
        {
            var original = new FrameHeader(type, 12345);

            original.Write(buffer);
            var result = FrameHeader.Read(buffer);

            Assert.That(result.Type, Is.EqualTo(original.Type), $"Type mismatch for {type}");
            Assert.That(result.PayloadLength, Is.EqualTo(original.PayloadLength), $"PayloadLength mismatch for {type}");
        }
    }

    [Test]
    public void TryRead_BufferTooSmall_ReturnsFalse()
    {
        ReadOnlySpan<byte> buffer = new byte[] { 0x01, 0x02, 0x03 }; // Only 3 bytes

        var success = FrameHeader.TryRead(buffer, out var header);

        Assert.That(success, Is.False);
        Assert.That(header, Is.EqualTo(default(FrameHeader)));
    }

    [Test]
    public void TryRead_BufferExactSize_ReturnsTrue()
    {
        ReadOnlySpan<byte> buffer = new byte[] { 0x01, 0x0A, 0x00, 0x00, 0x00 };

        var success = FrameHeader.TryRead(buffer, out var header);

        Assert.That(success, Is.True);
        Assert.That(header.Type, Is.EqualTo(FrameType.Open));
        Assert.That(header.PayloadLength, Is.EqualTo(10));
    }

    [Test]
    public void TryRead_BufferLargerThanHeader_ReturnsTrue()
    {
        ReadOnlySpan<byte> buffer = new byte[] { 0x01, 0x0A, 0x00, 0x00, 0x00, 0xFF, 0xFF };

        var success = FrameHeader.TryRead(buffer, out var header);

        Assert.That(success, Is.True);
        Assert.That(header.Type, Is.EqualTo(FrameType.Open));
        Assert.That(header.PayloadLength, Is.EqualTo(10));
    }

    [Test]
    public void TotalFrameSize_ReturnsHeaderPlusPayload()
    {
        var header = new FrameHeader(FrameType.Data, 1000);

        Assert.That(header.TotalFrameSize, Is.EqualTo(1005)); // 5 + 1000
    }

    [Test]
    public void TotalFrameSize_ZeroPayload_ReturnsHeaderSize()
    {
        var header = new FrameHeader(FrameType.Flush, 0);

        Assert.That(header.TotalFrameSize, Is.EqualTo(5));
    }

    [Test]
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(65535)]
    [TestCase(int.MaxValue)]
    public void PayloadLength_BoundaryValues(int length)
    {
        var original = new FrameHeader(FrameType.Data, length);
        Span<byte> buffer = stackalloc byte[5];

        original.Write(buffer);
        var result = FrameHeader.Read(buffer);

        Assert.That(result.PayloadLength, Is.EqualTo(length));
    }

    [Test]
    public void ToString_ReturnsReadableFormat()
    {
        var header = new FrameHeader(FrameType.Open, 100);
        var str = header.ToString();

        Assert.That(str, Does.Contain("Open"));
        Assert.That(str, Does.Contain("100"));
    }
}
