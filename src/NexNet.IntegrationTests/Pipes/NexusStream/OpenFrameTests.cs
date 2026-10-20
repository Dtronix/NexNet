using System;
using NexNet.Pipes.NexusStream;
using NexNet.Pipes.NexusStream.Frames;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class OpenFrameTests
{
    [Test]
    public void Constructor_SetsProperties()
    {
        var frame = new OpenFrame("/test/file.txt", StreamAccessMode.Read, StreamShareMode.Read, 1000);

        Assert.That(frame.ResourceId, Is.EqualTo("/test/file.txt"));
        Assert.That(frame.Access, Is.EqualTo(StreamAccessMode.Read));
        Assert.That(frame.Share, Is.EqualTo(StreamShareMode.Read));
        Assert.That(frame.ResumePosition, Is.EqualTo(1000));
    }

    [Test]
    public void Constructor_DefaultValues()
    {
        var frame = new OpenFrame("/test/file.txt", StreamAccessMode.ReadWrite);

        Assert.That(frame.Share, Is.EqualTo(StreamShareMode.None));
        Assert.That(frame.ResumePosition, Is.EqualTo(-1));
    }

    [Test]
    public void Constructor_NullResourceId_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new OpenFrame(null!, StreamAccessMode.Read));
    }

    [Test]
    public void Constructor_TooLongResourceId_Throws()
    {
        var longId = new string('a', StreamBinaryHelpers.MaxResourceIdLength + 1);
        Assert.Throws<ArgumentException>(() => new OpenFrame(longId, StreamAccessMode.Read));
    }

    [Test]
    public void GetPayloadSize_CalculatesCorrectly()
    {
        var frame = new OpenFrame("/test", StreamAccessMode.Read);

        // 2 (string length) + 5 ("/test") + 1 (access) + 1 (share) + 8 (resumePosition) = 17
        var expectedSize = 2 + 5 + 1 + 1 + 8;
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(expectedSize));
    }

    [Test]
    public void Roundtrip_AllAccessModes()
    {
        Span<byte> buffer = stackalloc byte[100];

        foreach (StreamAccessMode access in Enum.GetValues(typeof(StreamAccessMode)))
        {
            var original = new OpenFrame("/test", access);

            original.Write(buffer);
            var result = OpenFrame.Read(buffer);

            Assert.That(result.Access, Is.EqualTo(access));
        }
    }

    [Test]
    public void Roundtrip_AllShareModes()
    {
        Span<byte> buffer = stackalloc byte[100];

        foreach (StreamShareMode share in Enum.GetValues(typeof(StreamShareMode)))
        {
            var original = new OpenFrame("/test", StreamAccessMode.Read, share);

            original.Write(buffer);
            var result = OpenFrame.Read(buffer);

            Assert.That(result.Share, Is.EqualTo(share));
        }
    }

    [Test]
    public void Roundtrip_VariousResumePositions()
    {
        var positions = new long[] { -1, 0, 1, 1000, long.MaxValue };
        Span<byte> buffer = stackalloc byte[100];

        foreach (var position in positions)
        {
            var original = new OpenFrame("/test", StreamAccessMode.Read, StreamShareMode.None, position);

            original.Write(buffer);
            var result = OpenFrame.Read(buffer);

            Assert.That(result.ResumePosition, Is.EqualTo(position), $"Position mismatch for {position}");
        }
    }

    [Test]
    public void Roundtrip_UnicodeResourceId()
    {
        var original = new OpenFrame("/файлы/документ.pdf", StreamAccessMode.ReadWrite);
        var size = original.GetPayloadSize();
        Span<byte> buffer = stackalloc byte[size];

        original.Write(buffer);
        var result = OpenFrame.Read(buffer);

        Assert.That(result.ResourceId, Is.EqualTo(original.ResourceId));
    }

    /// <summary>
    /// Validates wire format matches the specification example from Appendix A.1.
    /// </summary>
    [Test]
    public void WireFormat_MatchesSpecExample()
    {
        // From spec Appendix A.1:
        // ResourceId: "/files/test.txt" (15 chars)
        // Access: 0x01 (Read)
        // Share: 0x00 (None)
        // ResumePosition: -1 (fresh start)
        var frame = new OpenFrame("/files/test.txt", StreamAccessMode.Read, StreamShareMode.None, -1);
        var size = frame.GetPayloadSize();
        var buffer = new byte[size];

        frame.Write(buffer);

        // Expected payload (without frame header):
        // [0-1] Length prefix: 15 = 0x000F (little-endian: 0F 00)
        // [2-16] ResourceId: "/files/test.txt" in UTF-8
        // [17] Access: 0x01
        // [18] Share: 0x00
        // [19-26] ResumePosition: -1 = 0xFFFFFFFFFFFFFFFF (little-endian)

        Assert.That(buffer[0], Is.EqualTo(0x0F), "String length low byte");
        Assert.That(buffer[1], Is.EqualTo(0x00), "String length high byte");
        Assert.That(buffer[2], Is.EqualTo((byte)'/'), "First char of path");
        Assert.That(buffer[16], Is.EqualTo((byte)'t'), "Last char of path");

        var accessOffset = 2 + 15; // After string
        Assert.That(buffer[accessOffset], Is.EqualTo(0x01), "Access mode");
        Assert.That(buffer[accessOffset + 1], Is.EqualTo(0x00), "Share mode");

        // Resume position -1 in little-endian
        Assert.That(buffer[accessOffset + 2], Is.EqualTo(0xFF), "ResumePosition byte 0");
        Assert.That(buffer[accessOffset + 9], Is.EqualTo(0xFF), "ResumePosition byte 7");
    }

    [Test]
    public void ToString_ContainsKeyInfo()
    {
        var frame = new OpenFrame("/test/file.txt", StreamAccessMode.Read);
        var str = frame.ToString();

        Assert.That(str, Does.Contain("/test/file.txt"));
        Assert.That(str, Does.Contain("Read"));
    }
}
