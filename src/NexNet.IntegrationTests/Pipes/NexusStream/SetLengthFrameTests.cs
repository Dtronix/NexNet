using System;
using NexNet.Pipes.NexusStream.Frames;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class SetLengthFrameTests
{
    [Test]
    public void Constructor_SetsLength()
    {
        var frame = new SetLengthFrame(1024);

        Assert.That(frame.Length, Is.EqualTo(1024));
    }

    [Test]
    public void GetPayloadSize_ReturnsCorrectSize()
    {
        var frame = new SetLengthFrame(0);
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(SetLengthFrame.Size));
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(8));
    }

    [TestCase(0L)]
    [TestCase(100L)]
    [TestCase(1024L)]
    [TestCase(long.MaxValue)]
    public void Roundtrip_VariousValues(long length)
    {
        Span<byte> buffer = stackalloc byte[SetLengthFrame.Size];
        var original = new SetLengthFrame(length);

        original.Write(buffer);
        var result = SetLengthFrame.Read(buffer);

        Assert.That(result.Length, Is.EqualTo(length));
    }

    [Test]
    public void WireFormat_LittleEndian()
    {
        var frame = new SetLengthFrame(0x0102030405060708);
        var buffer = new byte[SetLengthFrame.Size];

        frame.Write(buffer);

        // Length (little-endian)
        Assert.That(buffer[0], Is.EqualTo(0x08), "Length byte 0");
        Assert.That(buffer[1], Is.EqualTo(0x07), "Length byte 1");
        Assert.That(buffer[2], Is.EqualTo(0x06), "Length byte 2");
        Assert.That(buffer[3], Is.EqualTo(0x05), "Length byte 3");
        Assert.That(buffer[4], Is.EqualTo(0x04), "Length byte 4");
        Assert.That(buffer[5], Is.EqualTo(0x03), "Length byte 5");
        Assert.That(buffer[6], Is.EqualTo(0x02), "Length byte 6");
        Assert.That(buffer[7], Is.EqualTo(0x01), "Length byte 7");
    }

    [Test]
    public void ToString_ContainsKeyInfo()
    {
        var frame = new SetLengthFrame(2048);
        var str = frame.ToString();

        Assert.That(str, Does.Contain("2048"));
        Assert.That(str, Does.Contain("SetLengthFrame"));
    }
}
