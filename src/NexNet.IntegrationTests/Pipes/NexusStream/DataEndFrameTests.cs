using System;
using NexNet.Pipes.NexusStream.Frames;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class DataEndFrameTests
{
    [Test]
    public void Constructor_SetsProperties()
    {
        var frame = new DataEndFrame(1024, 10);

        Assert.That(frame.TotalBytes, Is.EqualTo(1024));
        Assert.That(frame.FinalSequence, Is.EqualTo(10));
    }

    [Test]
    public void Constructor_ZeroValues()
    {
        var frame = new DataEndFrame(0, 0);

        Assert.That(frame.TotalBytes, Is.EqualTo(0));
        Assert.That(frame.FinalSequence, Is.EqualTo(0));
    }

    [Test]
    public void GetPayloadSize_ReturnsCorrectSize()
    {
        var frame = new DataEndFrame(1024, 10);
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(DataEndFrame.Size));
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(8)); // 4 + 4
    }

    [Test]
    public void Roundtrip_NormalValues()
    {
        Span<byte> buffer = stackalloc byte[DataEndFrame.Size];
        var original = new DataEndFrame(5000, 100);

        original.Write(buffer);
        var result = DataEndFrame.Read(buffer);

        Assert.That(result.TotalBytes, Is.EqualTo(5000));
        Assert.That(result.FinalSequence, Is.EqualTo(100));
    }

    [Test]
    public void Roundtrip_MaxTotalBytes()
    {
        Span<byte> buffer = stackalloc byte[DataEndFrame.Size];
        var original = new DataEndFrame(int.MaxValue, 0);

        original.Write(buffer);
        var result = DataEndFrame.Read(buffer);

        Assert.That(result.TotalBytes, Is.EqualTo(int.MaxValue));
    }

    [Test]
    public void Roundtrip_MaxFinalSequence()
    {
        Span<byte> buffer = stackalloc byte[DataEndFrame.Size];
        var original = new DataEndFrame(0, uint.MaxValue);

        original.Write(buffer);
        var result = DataEndFrame.Read(buffer);

        Assert.That(result.FinalSequence, Is.EqualTo(uint.MaxValue));
    }

    [Test]
    public void Roundtrip_ZeroValues()
    {
        Span<byte> buffer = stackalloc byte[DataEndFrame.Size];
        var original = new DataEndFrame(0, 0);

        original.Write(buffer);
        var result = DataEndFrame.Read(buffer);

        Assert.That(result.TotalBytes, Is.EqualTo(0));
        Assert.That(result.FinalSequence, Is.EqualTo(0));
    }

    [Test]
    public void WireFormat_LittleEndian()
    {
        var frame = new DataEndFrame(0x01020304, 0x05060708);
        var buffer = new byte[DataEndFrame.Size];

        frame.Write(buffer);

        // TotalBytes (little-endian)
        Assert.That(buffer[0], Is.EqualTo(0x04), "TotalBytes byte 0");
        Assert.That(buffer[1], Is.EqualTo(0x03), "TotalBytes byte 1");
        Assert.That(buffer[2], Is.EqualTo(0x02), "TotalBytes byte 2");
        Assert.That(buffer[3], Is.EqualTo(0x01), "TotalBytes byte 3");

        // FinalSequence (little-endian)
        Assert.That(buffer[4], Is.EqualTo(0x08), "FinalSequence byte 0");
        Assert.That(buffer[5], Is.EqualTo(0x07), "FinalSequence byte 1");
        Assert.That(buffer[6], Is.EqualTo(0x06), "FinalSequence byte 2");
        Assert.That(buffer[7], Is.EqualTo(0x05), "FinalSequence byte 3");
    }

    [Test]
    public void ToString_ContainsKeyInfo()
    {
        var frame = new DataEndFrame(2048, 50);
        var str = frame.ToString();

        Assert.That(str, Does.Contain("2048"));
        Assert.That(str, Does.Contain("50"));
        Assert.That(str, Does.Contain("DataEndFrame"));
    }
}
