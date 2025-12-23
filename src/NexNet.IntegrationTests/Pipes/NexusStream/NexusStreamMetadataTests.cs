using System;
using NexNet.Pipes.NexusStream;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class NexusStreamMetadataTests
{
    [Test]
    public void Size_Is9()
    {
        Assert.That(NexusStreamMetadata.Size, Is.EqualTo(9));
    }

    [Test]
    public void Roundtrip_AllFlagsTrue()
    {
        var original = new NexusStreamMetadata
        {
            Length = 1024,
            HasKnownLength = true,
            CanSeek = true,
            CanRead = true,
            CanWrite = true
        };

        Span<byte> buffer = stackalloc byte[NexusStreamMetadata.Size];
        original.Write(buffer);
        var result = NexusStreamMetadata.Read(buffer);

        Assert.That(result.Length, Is.EqualTo(original.Length));
        Assert.That(result.HasKnownLength, Is.True);
        Assert.That(result.CanSeek, Is.True);
        Assert.That(result.CanRead, Is.True);
        Assert.That(result.CanWrite, Is.True);
    }

    [Test]
    public void Roundtrip_AllFlagsFalse()
    {
        var original = new NexusStreamMetadata
        {
            Length = -1,
            HasKnownLength = false,
            CanSeek = false,
            CanRead = false,
            CanWrite = false
        };

        Span<byte> buffer = stackalloc byte[NexusStreamMetadata.Size];
        original.Write(buffer);
        var result = NexusStreamMetadata.Read(buffer);

        Assert.That(result.Length, Is.EqualTo(-1));
        Assert.That(result.HasKnownLength, Is.False);
        Assert.That(result.CanSeek, Is.False);
        Assert.That(result.CanRead, Is.False);
        Assert.That(result.CanWrite, Is.False);
    }

    [Test]
    public void Roundtrip_MixedFlags()
    {
        var original = new NexusStreamMetadata
        {
            Length = 5000,
            HasKnownLength = true,
            CanSeek = false,
            CanRead = true,
            CanWrite = false
        };

        Span<byte> buffer = stackalloc byte[NexusStreamMetadata.Size];
        original.Write(buffer);
        var result = NexusStreamMetadata.Read(buffer);

        Assert.That(result.Length, Is.EqualTo(original.Length));
        Assert.That(result.HasKnownLength, Is.True);
        Assert.That(result.CanSeek, Is.False);
        Assert.That(result.CanRead, Is.True);
        Assert.That(result.CanWrite, Is.False);
    }

    [Test]
    [TestCase(0L)]
    [TestCase(1L)]
    [TestCase(1000L)]
    [TestCase(long.MaxValue)]
    [TestCase(-1L)]
    [TestCase(long.MinValue)]
    public void Roundtrip_VariousLengths(long length)
    {
        var original = new NexusStreamMetadata
        {
            Length = length,
            HasKnownLength = length >= 0,
            CanSeek = true,
            CanRead = true,
            CanWrite = true
        };

        Span<byte> buffer = stackalloc byte[NexusStreamMetadata.Size];
        original.Write(buffer);
        var result = NexusStreamMetadata.Read(buffer);

        Assert.That(result.Length, Is.EqualTo(length));
    }

    [Test]
    public void GetFlags_EncodesCorrectly()
    {
        var metadata = new NexusStreamMetadata
        {
            HasKnownLength = true,  // Bit 0 = 0x01
            CanSeek = true,         // Bit 1 = 0x02
            CanRead = true,         // Bit 2 = 0x04
            CanWrite = true         // Bit 3 = 0x08
        };

        var flags = metadata.GetFlags();
        Assert.That(flags, Is.EqualTo(0x0F)); // All bits set
    }

    [Test]
    public void GetFlags_OnlyReadAndWrite()
    {
        var metadata = new NexusStreamMetadata
        {
            HasKnownLength = false,
            CanSeek = false,
            CanRead = true,         // Bit 2 = 0x04
            CanWrite = true         // Bit 3 = 0x08
        };

        var flags = metadata.GetFlags();
        Assert.That(flags, Is.EqualTo(0x0C)); // 0x04 | 0x08
    }

    [Test]
    public void WireFormat_MatchesExpected()
    {
        var metadata = new NexusStreamMetadata
        {
            Length = 0x123456789ABCDEF0,
            HasKnownLength = true,
            CanSeek = true,
            CanRead = true,
            CanWrite = false
        };

        var buffer = new byte[NexusStreamMetadata.Size];
        metadata.Write(buffer);

        // Flags: HasKnownLength | CanSeek | CanRead = 0x01 | 0x02 | 0x04 = 0x07
        Assert.That(buffer[0], Is.EqualTo(0x07), "Flags byte");

        // Length in little-endian
        Assert.That(buffer[1], Is.EqualTo(0xF0), "Length byte 0");
        Assert.That(buffer[2], Is.EqualTo(0xDE), "Length byte 1");
        Assert.That(buffer[3], Is.EqualTo(0xBC), "Length byte 2");
        Assert.That(buffer[4], Is.EqualTo(0x9A), "Length byte 3");
        Assert.That(buffer[5], Is.EqualTo(0x78), "Length byte 4");
        Assert.That(buffer[6], Is.EqualTo(0x56), "Length byte 5");
        Assert.That(buffer[7], Is.EqualTo(0x34), "Length byte 6");
        Assert.That(buffer[8], Is.EqualTo(0x12), "Length byte 7");
    }

    [Test]
    public void ToString_ContainsKeyInfo()
    {
        var metadata = new NexusStreamMetadata
        {
            Length = 1024,
            HasKnownLength = true,
            CanSeek = true,
            CanRead = true,
            CanWrite = false
        };

        var str = metadata.ToString();

        Assert.That(str, Does.Contain("1024"));
        Assert.That(str, Does.Contain("HasKnownLength=True"));
        Assert.That(str, Does.Contain("CanSeek=True"));
        Assert.That(str, Does.Contain("CanRead=True"));
        Assert.That(str, Does.Contain("CanWrite=False"));
    }
}
