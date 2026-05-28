using System;
using NexNet.Pipes.NexusStream.Frames;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class ReadFrameTests
{
    [Test]
    public void Constructor_SetsCount()
    {
        var frame = new ReadFrame(1024);
        Assert.That(frame.Count, Is.EqualTo(1024));
    }

    [Test]
    public void Constructor_ZeroCount_Succeeds()
    {
        var frame = new ReadFrame(0);
        Assert.That(frame.Count, Is.EqualTo(0));
    }

    [Test]
    public void Constructor_NegativeCount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReadFrame(-1));
    }

    [Test]
    public void GetPayloadSize_ReturnsCorrectSize()
    {
        var frame = new ReadFrame(1024);
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(ReadFrame.Size));
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(4));
    }

    [Test]
    public void Roundtrip_SmallCount()
    {
        Span<byte> buffer = stackalloc byte[ReadFrame.Size];
        var original = new ReadFrame(100);

        original.Write(buffer);
        var result = ReadFrame.Read(buffer);

        Assert.That(result.Count, Is.EqualTo(100));
    }

    [Test]
    public void Roundtrip_LargeCount()
    {
        Span<byte> buffer = stackalloc byte[ReadFrame.Size];
        var original = new ReadFrame(int.MaxValue);

        original.Write(buffer);
        var result = ReadFrame.Read(buffer);

        Assert.That(result.Count, Is.EqualTo(int.MaxValue));
    }

    [Test]
    public void Roundtrip_ZeroCount()
    {
        Span<byte> buffer = stackalloc byte[ReadFrame.Size];
        var original = new ReadFrame(0);

        original.Write(buffer);
        var result = ReadFrame.Read(buffer);

        Assert.That(result.Count, Is.EqualTo(0));
    }

    [Test]
    public void WireFormat_LittleEndian()
    {
        var frame = new ReadFrame(0x01020304);
        var buffer = new byte[ReadFrame.Size];

        frame.Write(buffer);

        // Little-endian: LSB first
        Assert.That(buffer[0], Is.EqualTo(0x04));
        Assert.That(buffer[1], Is.EqualTo(0x03));
        Assert.That(buffer[2], Is.EqualTo(0x02));
        Assert.That(buffer[3], Is.EqualTo(0x01));
    }

    [Test]
    public void ToString_ContainsCount()
    {
        var frame = new ReadFrame(1024);
        var str = frame.ToString();

        Assert.That(str, Does.Contain("1024"));
        Assert.That(str, Does.Contain("ReadFrame"));
    }
}
