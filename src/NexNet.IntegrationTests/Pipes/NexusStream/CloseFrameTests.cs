using System;
using NexNet.Pipes.NexusStream.Frames;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class CloseFrameTests
{
    [Test]
    public void PayloadSize_Is1()
    {
        Assert.That(CloseFrame.PayloadSize, Is.EqualTo(1));
    }

    [Test]
    public void Constructor_Graceful_SetsProperty()
    {
        var frame = new CloseFrame(true);
        Assert.That(frame.Graceful, Is.True);
    }

    [Test]
    public void Constructor_NonGraceful_SetsProperty()
    {
        var frame = new CloseFrame(false);
        Assert.That(frame.Graceful, Is.False);
    }

    [Test]
    public void GetPayloadSize_Returns1()
    {
        var frame = new CloseFrame(true);
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(1));
    }

    [Test]
    public void Write_Graceful_Writes1()
    {
        var frame = new CloseFrame(true);
        Span<byte> buffer = stackalloc byte[1];

        frame.Write(buffer);

        Assert.That(buffer[0], Is.EqualTo(1));
    }

    [Test]
    public void Write_NonGraceful_Writes0()
    {
        var frame = new CloseFrame(false);
        Span<byte> buffer = stackalloc byte[1];

        frame.Write(buffer);

        Assert.That(buffer[0], Is.EqualTo(0));
    }

    [Test]
    public void Read_GracefulByte_ReturnsGraceful()
    {
        ReadOnlySpan<byte> buffer = new byte[] { 1 };

        var frame = CloseFrame.Read(buffer);

        Assert.That(frame.Graceful, Is.True);
    }

    [Test]
    public void Read_NonGracefulByte_ReturnsNonGraceful()
    {
        ReadOnlySpan<byte> buffer = new byte[] { 0 };

        var frame = CloseFrame.Read(buffer);

        Assert.That(frame.Graceful, Is.False);
    }

    [Test]
    public void Roundtrip_Graceful()
    {
        var original = new CloseFrame(true);
        Span<byte> buffer = stackalloc byte[1];

        original.Write(buffer);
        var result = CloseFrame.Read(buffer);

        Assert.That(result.Graceful, Is.EqualTo(original.Graceful));
    }

    [Test]
    public void Roundtrip_NonGraceful()
    {
        var original = new CloseFrame(false);
        Span<byte> buffer = stackalloc byte[1];

        original.Write(buffer);
        var result = CloseFrame.Read(buffer);

        Assert.That(result.Graceful, Is.EqualTo(original.Graceful));
    }

    [Test]
    public void ToString_ContainsGracefulInfo()
    {
        var graceful = new CloseFrame(true);
        var nonGraceful = new CloseFrame(false);

        Assert.That(graceful.ToString(), Does.Contain("True"));
        Assert.That(nonGraceful.ToString(), Does.Contain("False"));
    }
}
