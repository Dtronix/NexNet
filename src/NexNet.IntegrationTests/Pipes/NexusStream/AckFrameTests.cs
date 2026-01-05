using NexNet.Pipes.NexusStream.Frames;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class AckFrameTests
{
    [Test]
    public void Size_Is8()
    {
        Assert.That(AckFrame.Size, Is.EqualTo(8));
    }

    [Test]
    public void Constructor_SetsProperties()
    {
        var frame = new AckFrame(100, 65536);

        Assert.That(frame.AckedSequence, Is.EqualTo(100u));
        Assert.That(frame.WindowSize, Is.EqualTo(65536u));
    }

    [Test]
    public void GetPayloadSize_Returns8()
    {
        var frame = new AckFrame(0, 0);
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(8));
    }

    [Test]
    public void Roundtrip_ZeroValues()
    {
        var original = new AckFrame(0, 0);
        var buffer = new byte[AckFrame.Size];
        original.Write(buffer);

        var parsed = AckFrame.Read(buffer);

        Assert.That(parsed.AckedSequence, Is.EqualTo(0u));
        Assert.That(parsed.WindowSize, Is.EqualTo(0u));
    }

    [Test]
    public void Roundtrip_TypicalValues()
    {
        var original = new AckFrame(12345, 131072);
        var buffer = new byte[AckFrame.Size];
        original.Write(buffer);

        var parsed = AckFrame.Read(buffer);

        Assert.That(parsed.AckedSequence, Is.EqualTo(12345u));
        Assert.That(parsed.WindowSize, Is.EqualTo(131072u));
    }

    [Test]
    public void Roundtrip_MaxValues()
    {
        var original = new AckFrame(uint.MaxValue, uint.MaxValue);
        var buffer = new byte[AckFrame.Size];
        original.Write(buffer);

        var parsed = AckFrame.Read(buffer);

        Assert.That(parsed.AckedSequence, Is.EqualTo(uint.MaxValue));
        Assert.That(parsed.WindowSize, Is.EqualTo(uint.MaxValue));
    }

    [Test]
    public void ToString_ContainsSequenceAndWindow()
    {
        var frame = new AckFrame(100, 65536);
        var str = frame.ToString();

        Assert.That(str, Does.Contain("100"));
        Assert.That(str, Does.Contain("65536"));
    }

    [Test]
    public void Init_SetsProperties()
    {
        var frame = new AckFrame
        {
            AckedSequence = 500,
            WindowSize = 32768
        };

        Assert.That(frame.AckedSequence, Is.EqualTo(500u));
        Assert.That(frame.WindowSize, Is.EqualTo(32768u));
    }
}
