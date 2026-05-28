using System;
using NexNet.Pipes.NexusStream.Frames;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class WriteFrameTests
{
    [Test]
    public void Constructor_SetsCount()
    {
        var frame = new WriteFrame(2048);
        Assert.That(frame.Count, Is.EqualTo(2048));
    }

    [Test]
    public void Constructor_ZeroCount_Succeeds()
    {
        var frame = new WriteFrame(0);
        Assert.That(frame.Count, Is.EqualTo(0));
    }

    [Test]
    public void Constructor_NegativeCount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WriteFrame(-1));
    }

    [Test]
    public void GetPayloadSize_ReturnsCorrectSize()
    {
        var frame = new WriteFrame(2048);
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(WriteFrame.Size));
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(4));
    }

    [Test]
    public void Roundtrip_SmallCount()
    {
        Span<byte> buffer = stackalloc byte[WriteFrame.Size];
        var original = new WriteFrame(256);

        original.Write(buffer);
        var result = WriteFrame.Read(buffer);

        Assert.That(result.Count, Is.EqualTo(256));
    }

    [Test]
    public void Roundtrip_LargeCount()
    {
        Span<byte> buffer = stackalloc byte[WriteFrame.Size];
        var original = new WriteFrame(int.MaxValue);

        original.Write(buffer);
        var result = WriteFrame.Read(buffer);

        Assert.That(result.Count, Is.EqualTo(int.MaxValue));
    }

    [Test]
    public void Roundtrip_ZeroCount()
    {
        Span<byte> buffer = stackalloc byte[WriteFrame.Size];
        var original = new WriteFrame(0);

        original.Write(buffer);
        var result = WriteFrame.Read(buffer);

        Assert.That(result.Count, Is.EqualTo(0));
    }

    [Test]
    public void WireFormat_LittleEndian()
    {
        var frame = new WriteFrame(0x04030201);
        var buffer = new byte[WriteFrame.Size];

        frame.Write(buffer);

        // Little-endian: LSB first
        Assert.That(buffer[0], Is.EqualTo(0x01));
        Assert.That(buffer[1], Is.EqualTo(0x02));
        Assert.That(buffer[2], Is.EqualTo(0x03));
        Assert.That(buffer[3], Is.EqualTo(0x04));
    }

    [Test]
    public void ToString_ContainsCount()
    {
        var frame = new WriteFrame(2048);
        var str = frame.ToString();

        Assert.That(str, Does.Contain("2048"));
        Assert.That(str, Does.Contain("WriteFrame"));
    }
}
