using NexNet.Pipes.NexusStream.Frames;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class GetMetadataFrameTests
{
    [Test]
    public void Size_Is0()
    {
        Assert.That(GetMetadataFrame.Size, Is.EqualTo(0));
    }

    [Test]
    public void GetPayloadSize_Returns0()
    {
        var frame = new GetMetadataFrame();
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(0));
    }

    [Test]
    public void Write_Returns0()
    {
        var frame = new GetMetadataFrame();
        var buffer = new byte[10];
        var written = frame.Write(buffer);
        Assert.That(written, Is.EqualTo(0));
    }

    [Test]
    public void ToString_ReturnsExpected()
    {
        var frame = new GetMetadataFrame();
        Assert.That(frame.ToString(), Is.EqualTo("GetMetadataFrame()"));
    }
}
