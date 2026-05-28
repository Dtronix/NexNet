using System;
using NexNet.Pipes.NexusStream;
using NexNet.Pipes.NexusStream.Frames;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class ErrorFrameTests
{
    [Test]
    public void Constructor_SetsProperties()
    {
        var frame = new ErrorFrame(StreamErrorCode.AccessDenied, 4096, "Access denied");

        Assert.That(frame.ErrorCode, Is.EqualTo(StreamErrorCode.AccessDenied));
        Assert.That(frame.Position, Is.EqualTo(4096));
        Assert.That(frame.Message, Is.EqualTo("Access denied"));
    }

    [Test]
    public void Constructor_NullMessage_ConvertsToEmpty()
    {
        var frame = new ErrorFrame(StreamErrorCode.IoError, 0, null!);

        Assert.That(frame.Message, Is.EqualTo(string.Empty));
    }

    [Test]
    public void GetPayloadSize_CalculatesCorrectly()
    {
        var frame = new ErrorFrame(StreamErrorCode.FileNotFound, 0, "Not found");

        // ErrorCode (4) + Position (8) + Message (2 + 9) = 23
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(4 + 8 + 2 + 9));
    }

    [Test]
    public void GetPayloadSize_EmptyMessage()
    {
        var frame = new ErrorFrame(StreamErrorCode.FileNotFound, 0, "");

        // ErrorCode (4) + Position (8) + Message (2 + 0) = 14
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(14));
    }

    [Test]
    public void Roundtrip_AllErrorCodes()
    {
        Span<byte> buffer = stackalloc byte[100];

        foreach (StreamErrorCode code in Enum.GetValues(typeof(StreamErrorCode)))
        {
            var original = new ErrorFrame(code, 12345, "Test message");

            original.Write(buffer);
            var result = ErrorFrame.Read(buffer);

            Assert.That(result.ErrorCode, Is.EqualTo(code), $"ErrorCode mismatch for {code}");
        }
    }

    [Test]
    public void Roundtrip_VariousPositions()
    {
        var positions = new long[] { 0, 1, 1000, long.MaxValue };
        Span<byte> buffer = stackalloc byte[100];

        foreach (var position in positions)
        {
            var original = new ErrorFrame(StreamErrorCode.IoError, position, "Test");

            original.Write(buffer);
            var result = ErrorFrame.Read(buffer);

            Assert.That(result.Position, Is.EqualTo(position));
        }
    }

    [Test]
    public void Roundtrip_EmptyMessage()
    {
        var original = new ErrorFrame(StreamErrorCode.IoError, 0, "");
        var size = original.GetPayloadSize();
        Span<byte> buffer = stackalloc byte[size];

        original.Write(buffer);
        var result = ErrorFrame.Read(buffer);

        Assert.That(result.Message, Is.EqualTo(""));
    }

    [Test]
    public void Roundtrip_UnicodeMessage()
    {
        var original = new ErrorFrame(StreamErrorCode.FileNotFound, 0, "Файл не найден");
        var size = original.GetPayloadSize();
        Span<byte> buffer = stackalloc byte[size];

        original.Write(buffer);
        var result = ErrorFrame.Read(buffer);

        Assert.That(result.Message, Is.EqualTo(original.Message));
    }

    [Test]
    public void IsProtocolError_StreamErrors_ReturnsFalse()
    {
        var codes = new[]
        {
            StreamErrorCode.Success,
            StreamErrorCode.FileNotFound,
            StreamErrorCode.AccessDenied,
            StreamErrorCode.IoError,
            StreamErrorCode.SeekError
        };

        foreach (var code in codes)
        {
            var frame = new ErrorFrame(code, 0, "");
            Assert.That(frame.IsProtocolError, Is.False, $"{code} should not be a protocol error");
        }
    }

    [Test]
    public void IsProtocolError_ProtocolErrors_ReturnsTrue()
    {
        var codes = new[]
        {
            StreamErrorCode.InvalidFrameType,
            StreamErrorCode.InvalidFrameSequence,
            StreamErrorCode.MalformedFrame,
            StreamErrorCode.SequenceGap,
            StreamErrorCode.UnexpectedFrame
        };

        foreach (var code in codes)
        {
            var frame = new ErrorFrame(code, 0, "");
            Assert.That(frame.IsProtocolError, Is.True, $"{code} should be a protocol error");
        }
    }

    /// <summary>
    /// Validates wire format matches the specification example from Appendix A.3.
    /// </summary>
    [Test]
    public void WireFormat_MatchesSpecExample()
    {
        // From spec Appendix A.3:
        // ErrorCode: 2 (AccessDenied)
        // Position: 4096 (0x1000)
        // Message: "Access denied" (13 chars)
        var frame = new ErrorFrame(StreamErrorCode.AccessDenied, 4096, "Access denied");
        var size = frame.GetPayloadSize();
        var buffer = new byte[size];

        frame.Write(buffer);

        // ErrorCode: 2 in little-endian (4 bytes)
        Assert.That(buffer[0], Is.EqualTo(0x02), "ErrorCode byte 0");
        Assert.That(buffer[1], Is.EqualTo(0x00), "ErrorCode byte 1");
        Assert.That(buffer[2], Is.EqualTo(0x00), "ErrorCode byte 2");
        Assert.That(buffer[3], Is.EqualTo(0x00), "ErrorCode byte 3");

        // Position: 4096 = 0x1000 in little-endian (8 bytes)
        Assert.That(buffer[4], Is.EqualTo(0x00), "Position byte 0");
        Assert.That(buffer[5], Is.EqualTo(0x10), "Position byte 1");
        Assert.That(buffer[6], Is.EqualTo(0x00), "Position byte 2");
        Assert.That(buffer[11], Is.EqualTo(0x00), "Position byte 7");

        // Message length: 13 in little-endian (2 bytes)
        Assert.That(buffer[12], Is.EqualTo(0x0D), "Message length low byte");
        Assert.That(buffer[13], Is.EqualTo(0x00), "Message length high byte");

        // Message content: "Access denied"
        Assert.That(buffer[14], Is.EqualTo((byte)'A'), "Message first char");
    }

    [Test]
    public void ToString_ContainsKeyInfo()
    {
        var frame = new ErrorFrame(StreamErrorCode.AccessDenied, 4096, "Access denied");
        var str = frame.ToString();

        Assert.That(str, Does.Contain("AccessDenied"));
        Assert.That(str, Does.Contain("4096"));
        Assert.That(str, Does.Contain("Access denied"));
    }
}
