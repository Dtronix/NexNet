using System;
using NexNet.Pipes.NexusStream.Frames;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class DataFrameTests
{
    [Test]
    public void Constructor_SetsProperties()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var frame = new DataFrame(42, data);

        Assert.That(frame.Sequence, Is.EqualTo(42));
        Assert.That(frame.Data.Length, Is.EqualTo(5));
        Assert.That(frame.Data.ToArray(), Is.EqualTo(data));
    }

    [Test]
    public void Constructor_EmptyData()
    {
        var frame = new DataFrame(0, Memory<byte>.Empty);

        Assert.That(frame.Sequence, Is.EqualTo(0));
        Assert.That(frame.Data.Length, Is.EqualTo(0));
    }

    [Test]
    public void GetPayloadSize_CalculatesCorrectly()
    {
        var data = new byte[100];
        var frame = new DataFrame(0, data);

        // HeaderSize (4) + data length (100)
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(DataFrame.HeaderSize + 100));
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(104));
    }

    [Test]
    public void GetPayloadSize_EmptyData()
    {
        var frame = new DataFrame(0, Memory<byte>.Empty);
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(DataFrame.HeaderSize));
    }

    [Test]
    public void Roundtrip_SmallData()
    {
        var originalData = new byte[] { 10, 20, 30, 40, 50 };
        var original = new DataFrame(123, originalData);
        var payloadSize = original.GetPayloadSize();

        var buffer = new byte[payloadSize];
        original.Write(buffer);

        var resultData = new byte[originalData.Length];
        var result = DataFrame.Read(buffer, resultData);

        Assert.That(result.Sequence, Is.EqualTo(123));
        Assert.That(result.Data.ToArray(), Is.EqualTo(originalData));
    }

    [Test]
    public void Roundtrip_LargeData()
    {
        var originalData = new byte[1000];
        for (int i = 0; i < originalData.Length; i++)
            originalData[i] = (byte)(i % 256);

        var original = new DataFrame(uint.MaxValue, originalData);
        var payloadSize = original.GetPayloadSize();

        var buffer = new byte[payloadSize];
        original.Write(buffer);

        var resultData = new byte[originalData.Length];
        var result = DataFrame.Read(buffer, resultData);

        Assert.That(result.Sequence, Is.EqualTo(uint.MaxValue));
        Assert.That(result.Data.ToArray(), Is.EqualTo(originalData));
    }

    [Test]
    public void Roundtrip_MaxSequence()
    {
        var data = new byte[] { 1 };
        var original = new DataFrame(uint.MaxValue, data);
        var buffer = new byte[original.GetPayloadSize()];

        original.Write(buffer);

        var resultData = new byte[1];
        var result = DataFrame.Read(buffer, resultData);

        Assert.That(result.Sequence, Is.EqualTo(uint.MaxValue));
    }

    [Test]
    public void ReadHeader_ExtractsCorrectly()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var frame = new DataFrame(12345, data);
        var buffer = new byte[frame.GetPayloadSize()];

        frame.Write(buffer);

        DataFrame.ReadHeader(buffer, out var sequence, out var dataLength);

        Assert.That(sequence, Is.EqualTo(12345));
        Assert.That(dataLength, Is.EqualTo(10));
    }

    [Test]
    public void WireFormat_SequenceLittleEndian()
    {
        var data = new byte[] { 0xAA };
        var frame = new DataFrame(0x01020304, data);
        var buffer = new byte[frame.GetPayloadSize()];

        frame.Write(buffer);

        // Sequence in little-endian
        Assert.That(buffer[0], Is.EqualTo(0x04), "Sequence byte 0");
        Assert.That(buffer[1], Is.EqualTo(0x03), "Sequence byte 1");
        Assert.That(buffer[2], Is.EqualTo(0x02), "Sequence byte 2");
        Assert.That(buffer[3], Is.EqualTo(0x01), "Sequence byte 3");

        // Data follows
        Assert.That(buffer[4], Is.EqualTo(0xAA), "Data byte 0");
    }

    [Test]
    public void Write_DataIsCopied()
    {
        var data = new byte[] { 1, 2, 3 };
        var frame = new DataFrame(0, data);
        var buffer = new byte[frame.GetPayloadSize()];

        frame.Write(buffer);

        // Modify original data
        data[0] = 99;

        // Buffer should still have original value
        Assert.That(buffer[DataFrame.HeaderSize], Is.EqualTo(1));
    }

    [Test]
    public void Read_DataIsCopied()
    {
        var originalData = new byte[] { 5, 6, 7 };
        var frame = new DataFrame(0, originalData);
        var buffer = new byte[frame.GetPayloadSize()];
        frame.Write(buffer);

        var resultBuffer = new byte[3];
        var result = DataFrame.Read(buffer, resultBuffer);

        // Modify the buffer
        buffer[DataFrame.HeaderSize] = 99;

        // Result data should be unchanged
        Assert.That(result.Data.Span[0], Is.EqualTo(5));
    }

    [Test]
    public void ToString_ContainsKeyInfo()
    {
        var data = new byte[100];
        var frame = new DataFrame(42, data);
        var str = frame.ToString();

        Assert.That(str, Does.Contain("42"));
        Assert.That(str, Does.Contain("100"));
        Assert.That(str, Does.Contain("DataFrame"));
    }
}
