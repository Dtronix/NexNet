using System;
using NexNet.Pipes.NexusStream;
using NexNet.Pipes.NexusStream.Frames;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class OpenResponseFrameTests
{
    [Test]
    public void Constructor_Success_SetsProperties()
    {
        var metadata = new NexusStreamMetadata
        {
            Length = 1024,
            HasKnownLength = true,
            CanSeek = true,
            CanRead = true,
            CanWrite = false
        };

        var frame = new OpenResponseFrame(metadata);

        Assert.That(frame.Success, Is.True);
        Assert.That(frame.ErrorCode, Is.EqualTo(StreamErrorCode.Success));
        Assert.That(frame.ErrorMessage, Is.Null);
        Assert.That(frame.Metadata.Length, Is.EqualTo(1024));
        Assert.That(frame.Metadata.CanRead, Is.True);
    }

    [Test]
    public void Constructor_Failure_SetsProperties()
    {
        var frame = new OpenResponseFrame(StreamErrorCode.FileNotFound, "File not found");

        Assert.That(frame.Success, Is.False);
        Assert.That(frame.ErrorCode, Is.EqualTo(StreamErrorCode.FileNotFound));
        Assert.That(frame.ErrorMessage, Is.EqualTo("File not found"));
    }

    [Test]
    public void GetPayloadSize_Success_CalculatesCorrectly()
    {
        var metadata = new NexusStreamMetadata();
        var frame = new OpenResponseFrame(metadata);

        // Success (1) + Metadata (9) = 10
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(1 + NexusStreamMetadata.Size));
    }

    [Test]
    public void GetPayloadSize_Failure_CalculatesCorrectly()
    {
        var frame = new OpenResponseFrame(StreamErrorCode.FileNotFound, "Not found");

        // Success (1) + ErrorCode (4) + Message (2 + 9) = 16
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(1 + 4 + 2 + 9));
    }

    [Test]
    public void GetPayloadSize_Failure_EmptyMessage()
    {
        var frame = new OpenResponseFrame(StreamErrorCode.IoError, "");

        // Success (1) + ErrorCode (4) + Message (2 + 0) = 7
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(7));
    }

    [Test]
    public void GetPayloadSize_Failure_NullMessage()
    {
        var frame = new OpenResponseFrame(StreamErrorCode.IoError, null);

        // Success (1) + ErrorCode (4) + NullString (2) = 7
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(7));
    }

    [Test]
    public void Roundtrip_Success()
    {
        var metadata = new NexusStreamMetadata
        {
            Length = 5000,
            HasKnownLength = true,
            CanSeek = true,
            CanRead = true,
            CanWrite = true
        };

        var original = new OpenResponseFrame(metadata);
        var buffer = new byte[original.GetPayloadSize()];

        original.Write(buffer);
        var result = OpenResponseFrame.Read(buffer);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Metadata.Length, Is.EqualTo(5000));
        Assert.That(result.Metadata.HasKnownLength, Is.True);
        Assert.That(result.Metadata.CanSeek, Is.True);
        Assert.That(result.Metadata.CanRead, Is.True);
        Assert.That(result.Metadata.CanWrite, Is.True);
    }

    [Test]
    public void Roundtrip_Failure()
    {
        var original = new OpenResponseFrame(StreamErrorCode.AccessDenied, "Access denied to resource");
        var buffer = new byte[original.GetPayloadSize()];

        original.Write(buffer);
        var result = OpenResponseFrame.Read(buffer);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCode, Is.EqualTo(StreamErrorCode.AccessDenied));
        Assert.That(result.ErrorMessage, Is.EqualTo("Access denied to resource"));
    }

    [Test]
    public void Roundtrip_AllErrorCodes()
    {
        Span<byte> buffer = stackalloc byte[100];

        foreach (StreamErrorCode code in Enum.GetValues(typeof(StreamErrorCode)))
        {
            var original = new OpenResponseFrame(code, $"Error: {code}");

            original.Write(buffer);
            var result = OpenResponseFrame.Read(buffer);

            Assert.That(result.Success, Is.False, $"Success should be false for {code}");
            Assert.That(result.ErrorCode, Is.EqualTo(code), $"ErrorCode mismatch for {code}");
        }
    }

    [Test]
    public void Roundtrip_UnicodeMessage()
    {
        var original = new OpenResponseFrame(StreamErrorCode.FileNotFound, "Файл не найден");
        var buffer = new byte[original.GetPayloadSize()];

        original.Write(buffer);
        var result = OpenResponseFrame.Read(buffer);

        Assert.That(result.ErrorMessage, Is.EqualTo("Файл не найден"));
    }

    [Test]
    public void WireFormat_Success_MatchesExpected()
    {
        var metadata = new NexusStreamMetadata
        {
            Length = 1024, // 0x400
            HasKnownLength = true,
            CanSeek = true,
            CanRead = true,
            CanWrite = false
        };

        var frame = new OpenResponseFrame(metadata);
        var buffer = new byte[frame.GetPayloadSize()];
        frame.Write(buffer);

        // Success byte
        Assert.That(buffer[0], Is.EqualTo(1), "Success flag");

        // Metadata flags: HasKnownLength | CanSeek | CanRead = 0x07
        Assert.That(buffer[1], Is.EqualTo(0x07), "Metadata flags");

        // Length: 1024 in little-endian
        Assert.That(buffer[2], Is.EqualTo(0x00), "Length byte 0");
        Assert.That(buffer[3], Is.EqualTo(0x04), "Length byte 1");
    }

    [Test]
    public void WireFormat_Failure_MatchesExpected()
    {
        var frame = new OpenResponseFrame(StreamErrorCode.FileNotFound, "NF");
        var buffer = new byte[frame.GetPayloadSize()];
        frame.Write(buffer);

        // Success byte = 0
        Assert.That(buffer[0], Is.EqualTo(0), "Success flag");

        // ErrorCode: FileNotFound = 1 in little-endian
        Assert.That(buffer[1], Is.EqualTo(0x01), "ErrorCode byte 0");
        Assert.That(buffer[2], Is.EqualTo(0x00), "ErrorCode byte 1");
        Assert.That(buffer[3], Is.EqualTo(0x00), "ErrorCode byte 2");
        Assert.That(buffer[4], Is.EqualTo(0x00), "ErrorCode byte 3");

        // Message length: 2 in little-endian
        Assert.That(buffer[5], Is.EqualTo(0x02), "Message length low byte");
        Assert.That(buffer[6], Is.EqualTo(0x00), "Message length high byte");

        // Message: "NF"
        Assert.That(buffer[7], Is.EqualTo((byte)'N'), "Message char 0");
        Assert.That(buffer[8], Is.EqualTo((byte)'F'), "Message char 1");
    }

    [Test]
    public void ToString_Success_ContainsKeyInfo()
    {
        var metadata = new NexusStreamMetadata { Length = 1024 };
        var frame = new OpenResponseFrame(metadata);
        var str = frame.ToString();

        Assert.That(str, Does.Contain("Success = true"));
        Assert.That(str, Does.Contain("Metadata"));
    }

    [Test]
    public void ToString_Failure_ContainsKeyInfo()
    {
        var frame = new OpenResponseFrame(StreamErrorCode.AccessDenied, "Access denied");
        var str = frame.ToString();

        Assert.That(str, Does.Contain("Success = false"));
        Assert.That(str, Does.Contain("AccessDenied"));
        Assert.That(str, Does.Contain("Access denied"));
    }
}
