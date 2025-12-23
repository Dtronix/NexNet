using System;
using System.IO;
using NexNet.Pipes.NexusStream.Frames;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class SeekFrameTests
{
    [Test]
    public void Constructor_SetsProperties()
    {
        var frame = new SeekFrame(1024, SeekOrigin.Begin);

        Assert.That(frame.Offset, Is.EqualTo(1024));
        Assert.That(frame.Origin, Is.EqualTo(SeekOrigin.Begin));
    }

    [Test]
    public void GetPayloadSize_ReturnsCorrectSize()
    {
        var frame = new SeekFrame(0, SeekOrigin.Begin);
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(SeekFrame.Size));
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(9)); // 8 (offset) + 1 (origin)
    }

    [TestCase(0L, SeekOrigin.Begin)]
    [TestCase(100L, SeekOrigin.Current)]
    [TestCase(-50L, SeekOrigin.Current)]
    [TestCase(-100L, SeekOrigin.End)]
    [TestCase(long.MaxValue, SeekOrigin.Begin)]
    [TestCase(long.MinValue, SeekOrigin.End)]
    public void Roundtrip_VariousValues(long offset, SeekOrigin origin)
    {
        Span<byte> buffer = stackalloc byte[SeekFrame.Size];
        var original = new SeekFrame(offset, origin);

        original.Write(buffer);
        var result = SeekFrame.Read(buffer);

        Assert.That(result.Offset, Is.EqualTo(offset));
        Assert.That(result.Origin, Is.EqualTo(origin));
    }

    [Test]
    public void WireFormat_LittleEndian()
    {
        var frame = new SeekFrame(0x0102030405060708, SeekOrigin.Current);
        var buffer = new byte[SeekFrame.Size];

        frame.Write(buffer);

        // Offset (little-endian)
        Assert.That(buffer[0], Is.EqualTo(0x08), "Offset byte 0");
        Assert.That(buffer[1], Is.EqualTo(0x07), "Offset byte 1");
        Assert.That(buffer[2], Is.EqualTo(0x06), "Offset byte 2");
        Assert.That(buffer[3], Is.EqualTo(0x05), "Offset byte 3");
        Assert.That(buffer[4], Is.EqualTo(0x04), "Offset byte 4");
        Assert.That(buffer[5], Is.EqualTo(0x03), "Offset byte 5");
        Assert.That(buffer[6], Is.EqualTo(0x02), "Offset byte 6");
        Assert.That(buffer[7], Is.EqualTo(0x01), "Offset byte 7");

        // Origin
        Assert.That(buffer[8], Is.EqualTo((byte)SeekOrigin.Current), "Origin");
    }

    [Test]
    public void ToString_ContainsKeyInfo()
    {
        var frame = new SeekFrame(500, SeekOrigin.End);
        var str = frame.ToString();

        Assert.That(str, Does.Contain("500"));
        Assert.That(str, Does.Contain("End"));
        Assert.That(str, Does.Contain("SeekFrame"));
    }
}
