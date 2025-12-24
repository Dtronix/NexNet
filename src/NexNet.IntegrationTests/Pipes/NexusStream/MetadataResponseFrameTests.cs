using System;
using NexNet.Pipes.NexusStream;
using NexNet.Pipes.NexusStream.Frames;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class MetadataResponseFrameTests
{
    [Test]
    public void Size_MatchesMetadataSize()
    {
        Assert.That(MetadataResponseFrame.Size, Is.EqualTo(NexusStreamMetadata.Size));
    }

    [Test]
    public void Constructor_SetsMetadata()
    {
        var metadata = new NexusStreamMetadata
        {
            Length = 1024,
            HasKnownLength = true,
            CanSeek = true,
            CanRead = true,
            CanWrite = false
        };

        var frame = new MetadataResponseFrame(metadata);

        Assert.That(frame.Metadata.Length, Is.EqualTo(1024));
        Assert.That(frame.Metadata.HasKnownLength, Is.True);
        Assert.That(frame.Metadata.CanSeek, Is.True);
        Assert.That(frame.Metadata.CanRead, Is.True);
        Assert.That(frame.Metadata.CanWrite, Is.False);
    }

    [Test]
    public void GetPayloadSize_ReturnsMetadataSize()
    {
        var frame = new MetadataResponseFrame(new NexusStreamMetadata());
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(NexusStreamMetadata.Size));
    }

    [Test]
    public void Roundtrip_PreservesMetadata()
    {
        var metadata = new NexusStreamMetadata
        {
            Length = 5000,
            HasKnownLength = true,
            CanSeek = false,
            CanRead = true,
            CanWrite = true,
            Created = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
            Modified = new DateTimeOffset(2024, 12, 20, 14, 30, 0, TimeSpan.Zero)
        };

        var original = new MetadataResponseFrame(metadata);
        var buffer = new byte[MetadataResponseFrame.Size];
        original.Write(buffer);

        var parsed = MetadataResponseFrame.Read(buffer);

        Assert.That(parsed.Metadata.Length, Is.EqualTo(5000));
        Assert.That(parsed.Metadata.HasKnownLength, Is.True);
        Assert.That(parsed.Metadata.CanSeek, Is.False);
        Assert.That(parsed.Metadata.CanRead, Is.True);
        Assert.That(parsed.Metadata.CanWrite, Is.True);
        Assert.That(parsed.Metadata.Created, Is.Not.Null);
        Assert.That(parsed.Metadata.Modified, Is.Not.Null);
    }

    [Test]
    public void Roundtrip_WithNullDateTimes()
    {
        var metadata = new NexusStreamMetadata
        {
            Length = 100,
            HasKnownLength = true,
            CanSeek = true,
            CanRead = true,
            CanWrite = true,
            Created = null,
            Modified = null
        };

        var original = new MetadataResponseFrame(metadata);
        var buffer = new byte[MetadataResponseFrame.Size];
        original.Write(buffer);

        var parsed = MetadataResponseFrame.Read(buffer);

        Assert.That(parsed.Metadata.Created, Is.Null);
        Assert.That(parsed.Metadata.Modified, Is.Null);
    }

    [Test]
    public void ToString_ContainsMetadata()
    {
        var metadata = new NexusStreamMetadata { Length = 1234 };
        var frame = new MetadataResponseFrame(metadata);
        var str = frame.ToString();

        Assert.That(str, Does.Contain("MetadataResponseFrame"));
        Assert.That(str, Does.Contain("1234"));
    }
}
