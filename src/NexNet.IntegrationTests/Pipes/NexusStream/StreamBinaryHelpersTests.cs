using System;
using System.Text;
using NexNet.Pipes.NexusStream;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class StreamBinaryHelpersTests
{
    // =============================================
    // Integer Write/Read Tests
    // =============================================

    [Test]
    public void WriteUInt16_WritesLittleEndian()
    {
        Span<byte> buffer = stackalloc byte[2];
        StreamBinaryHelpers.WriteUInt16(buffer, 0x1234);

        Assert.That(buffer[0], Is.EqualTo(0x34)); // Low byte first
        Assert.That(buffer[1], Is.EqualTo(0x12)); // High byte second
    }

    [Test]
    public void ReadUInt16_ReadsLittleEndian()
    {
        ReadOnlySpan<byte> buffer = new byte[] { 0x34, 0x12 };
        var value = StreamBinaryHelpers.ReadUInt16(buffer);

        Assert.That(value, Is.EqualTo(0x1234));
    }

    [Test]
    public void WriteInt32_WritesLittleEndian()
    {
        Span<byte> buffer = stackalloc byte[4];
        StreamBinaryHelpers.WriteInt32(buffer, 0x12345678);

        Assert.That(buffer[0], Is.EqualTo(0x78));
        Assert.That(buffer[1], Is.EqualTo(0x56));
        Assert.That(buffer[2], Is.EqualTo(0x34));
        Assert.That(buffer[3], Is.EqualTo(0x12));
    }

    [Test]
    public void ReadInt32_ReadsLittleEndian()
    {
        ReadOnlySpan<byte> buffer = new byte[] { 0x78, 0x56, 0x34, 0x12 };
        var value = StreamBinaryHelpers.ReadInt32(buffer);

        Assert.That(value, Is.EqualTo(0x12345678));
    }

    [Test]
    public void WriteInt64_WritesLittleEndian()
    {
        Span<byte> buffer = stackalloc byte[8];
        StreamBinaryHelpers.WriteInt64(buffer, 0x123456789ABCDEF0);

        Assert.That(buffer[0], Is.EqualTo(0xF0));
        Assert.That(buffer[7], Is.EqualTo(0x12));
    }

    [Test]
    public void ReadInt64_ReadsLittleEndian()
    {
        ReadOnlySpan<byte> buffer = new byte[] { 0xF0, 0xDE, 0xBC, 0x9A, 0x78, 0x56, 0x34, 0x12 };
        var value = StreamBinaryHelpers.ReadInt64(buffer);

        Assert.That(value, Is.EqualTo(0x123456789ABCDEF0));
    }

    [Test]
    [TestCase((ushort)0)]
    [TestCase((ushort)65535)]
    [TestCase((ushort)12345)]
    public void UInt16_Roundtrip(ushort original)
    {
        Span<byte> buffer = stackalloc byte[2];
        StreamBinaryHelpers.WriteUInt16(buffer, original);
        var result = StreamBinaryHelpers.ReadUInt16(buffer);

        Assert.That(result, Is.EqualTo(original));
    }

    [Test]
    [TestCase(0)]
    [TestCase(int.MinValue)]
    [TestCase(int.MaxValue)]
    [TestCase(-12345)]
    public void Int32_Roundtrip(int original)
    {
        Span<byte> buffer = stackalloc byte[4];
        StreamBinaryHelpers.WriteInt32(buffer, original);
        var result = StreamBinaryHelpers.ReadInt32(buffer);

        Assert.That(result, Is.EqualTo(original));
    }

    [Test]
    [TestCase(0L)]
    [TestCase(long.MinValue)]
    [TestCase(long.MaxValue)]
    [TestCase(-1L)]
    public void Int64_Roundtrip(long original)
    {
        Span<byte> buffer = stackalloc byte[8];
        StreamBinaryHelpers.WriteInt64(buffer, original);
        var result = StreamBinaryHelpers.ReadInt64(buffer);

        Assert.That(result, Is.EqualTo(original));
    }

    // =============================================
    // String Serialization Tests
    // =============================================

    [Test]
    public void WriteString_NullString_WritesNullMarker()
    {
        Span<byte> buffer = stackalloc byte[2];
        var bytesWritten = StreamBinaryHelpers.WriteString(buffer, null);

        Assert.That(bytesWritten, Is.EqualTo(2));
        Assert.That(buffer[0], Is.EqualTo(0xFF));
        Assert.That(buffer[1], Is.EqualTo(0xFF));
    }

    [Test]
    public void ReadString_NullMarker_ReturnsNull()
    {
        ReadOnlySpan<byte> buffer = new byte[] { 0xFF, 0xFF };
        var result = StreamBinaryHelpers.ReadString(buffer, out var bytesRead);

        Assert.That(result, Is.Null);
        Assert.That(bytesRead, Is.EqualTo(2));
    }

    [Test]
    public void WriteString_EmptyString_WritesZeroLength()
    {
        Span<byte> buffer = stackalloc byte[2];
        var bytesWritten = StreamBinaryHelpers.WriteString(buffer, "");

        Assert.That(bytesWritten, Is.EqualTo(2));
        Assert.That(buffer[0], Is.EqualTo(0x00));
        Assert.That(buffer[1], Is.EqualTo(0x00));
    }

    [Test]
    public void ReadString_ZeroLength_ReturnsEmptyString()
    {
        ReadOnlySpan<byte> buffer = new byte[] { 0x00, 0x00 };
        var result = StreamBinaryHelpers.ReadString(buffer, out var bytesRead);

        Assert.That(result, Is.EqualTo(""));
        Assert.That(bytesRead, Is.EqualTo(2));
    }

    [Test]
    public void WriteString_SimpleAscii_WritesCorrectly()
    {
        var testString = "Hello";
        Span<byte> buffer = stackalloc byte[20];
        var bytesWritten = StreamBinaryHelpers.WriteString(buffer, testString);

        Assert.That(bytesWritten, Is.EqualTo(2 + 5)); // 2-byte length + "Hello"
        Assert.That(buffer[0], Is.EqualTo(5)); // Length low byte
        Assert.That(buffer[1], Is.EqualTo(0)); // Length high byte
        Assert.That(buffer[2], Is.EqualTo((byte)'H'));
        Assert.That(buffer[3], Is.EqualTo((byte)'e'));
        Assert.That(buffer[4], Is.EqualTo((byte)'l'));
        Assert.That(buffer[5], Is.EqualTo((byte)'l'));
        Assert.That(buffer[6], Is.EqualTo((byte)'o'));
    }

    [Test]
    public void String_Roundtrip_Simple()
    {
        var testString = "Hello, World!";
        Span<byte> buffer = stackalloc byte[100];

        var bytesWritten = StreamBinaryHelpers.WriteString(buffer, testString);
        var result = StreamBinaryHelpers.ReadString(buffer, out var bytesRead);

        Assert.That(result, Is.EqualTo(testString));
        Assert.That(bytesRead, Is.EqualTo(bytesWritten));
    }

    [Test]
    public void String_Roundtrip_Unicode()
    {
        var testString = "こんにちは世界"; // Japanese "Hello World"
        var byteCount = Encoding.UTF8.GetByteCount(testString);
        Span<byte> buffer = stackalloc byte[byteCount + 2];

        var bytesWritten = StreamBinaryHelpers.WriteString(buffer, testString);
        var result = StreamBinaryHelpers.ReadString(buffer, out var bytesRead);

        Assert.That(result, Is.EqualTo(testString));
        Assert.That(bytesRead, Is.EqualTo(bytesWritten));
    }

    [Test]
    public void String_Roundtrip_Null()
    {
        Span<byte> buffer = stackalloc byte[2];

        StreamBinaryHelpers.WriteString(buffer, null);
        var result = StreamBinaryHelpers.ReadString(buffer, out _);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void GetStringSize_NullString_Returns2()
    {
        Assert.That(StreamBinaryHelpers.GetStringSize(null), Is.EqualTo(2));
    }

    [Test]
    public void GetStringSize_EmptyString_Returns2()
    {
        Assert.That(StreamBinaryHelpers.GetStringSize(""), Is.EqualTo(2));
    }

    [Test]
    public void GetStringSize_SimpleString_ReturnsCorrectSize()
    {
        var testString = "Hello";
        Assert.That(StreamBinaryHelpers.GetStringSize(testString), Is.EqualTo(2 + 5));
    }

    [Test]
    public void GetStringSize_UnicodeString_ReturnsUtf8Size()
    {
        var testString = "こんにちは";
        var expectedByteCount = Encoding.UTF8.GetByteCount(testString);
        Assert.That(StreamBinaryHelpers.GetStringSize(testString), Is.EqualTo(2 + expectedByteCount));
    }

    // =============================================
    // Boolean Tests
    // =============================================

    [Test]
    public void WriteBool_True_Writes1()
    {
        Span<byte> buffer = stackalloc byte[1];
        StreamBinaryHelpers.WriteBool(buffer, true);

        Assert.That(buffer[0], Is.EqualTo(1));
    }

    [Test]
    public void WriteBool_False_Writes0()
    {
        Span<byte> buffer = stackalloc byte[1];
        StreamBinaryHelpers.WriteBool(buffer, false);

        Assert.That(buffer[0], Is.EqualTo(0));
    }

    [Test]
    public void ReadBool_NonZero_ReturnsTrue()
    {
        Assert.That(StreamBinaryHelpers.ReadBool(new byte[] { 1 }), Is.True);
        Assert.That(StreamBinaryHelpers.ReadBool(new byte[] { 42 }), Is.True);
        Assert.That(StreamBinaryHelpers.ReadBool(new byte[] { 255 }), Is.True);
    }

    [Test]
    public void ReadBool_Zero_ReturnsFalse()
    {
        Assert.That(StreamBinaryHelpers.ReadBool(new byte[] { 0 }), Is.False);
    }

    // =============================================
    // Constants Tests
    // =============================================

    [Test]
    public void MaxResourceIdLength_Is2000()
    {
        Assert.That(StreamBinaryHelpers.MaxResourceIdLength, Is.EqualTo(2000));
    }

    [Test]
    public void NullStringLength_Is0xFFFF()
    {
        Assert.That(StreamBinaryHelpers.NullStringLength, Is.EqualTo(0xFFFF));
    }
}
