using NexNet.Pipes.NexusStream.Frames;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class FlushFrameTests
{
    [Test]
    public void GetPayloadSize_ReturnsZero()
    {
        var frame = new FlushFrame();
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(0));
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(FlushFrame.Size));
    }

    [Test]
    public void ToString_ReturnsExpectedFormat()
    {
        var frame = new FlushFrame();
        var str = frame.ToString();

        Assert.That(str, Does.Contain("FlushFrame"));
    }
}
