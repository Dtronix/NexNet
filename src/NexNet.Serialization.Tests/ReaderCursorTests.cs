using System.Buffers;
using System.Text;
using static NexNet.Serialization.Tests.TestHelpers;

namespace NexNet.Serialization.Tests;

/// <summary>
/// Covers the reader's span cursor: segment boundaries, empty segments, positions and string decoding paths.
/// </summary>
[TestFixture]
public class ReaderCursorTests
{
    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }

    /// <summary>
    /// Builds a sequence from the given chunk sizes; a size of 0 inserts an empty segment.
    /// </summary>
    private static ReadOnlySequence<byte> Chunked(byte[] data, params int[] sizes)
    {
        Segment? first = null;
        Segment? last = null;
        var offset = 0;
        var i = 0;
        while (offset < data.Length || i < sizes.Length)
        {
            var size = i < sizes.Length ? sizes[i++] : data.Length - offset;
            size = Math.Min(size, data.Length - offset);
            var chunk = data.AsMemory(offset, size);
            offset += size;
            if (first == null)
                first = last = new Segment(chunk, 0);
            else
                last = last!.Append(chunk);
        }

        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    private static byte[] Sample() => Write((ref MsgPackWriter w) =>
    {
        w.WriteArrayHeader(5);
        w.Write(int.MinValue);
        w.Write("héllo wörld");
        w.Write(2.5d);
        w.WriteBinary(new byte[] { 1, 2, 3, 4, 5 });
        w.Write(ulong.MaxValue);
    });

    private static void AssertSample(ref MsgPackReader reader)
    {
        Assert.That(reader.ReadArrayHeader(), Is.EqualTo(5));
        Assert.That(reader.ReadInt32(), Is.EqualTo(int.MinValue));
        Assert.That(reader.ReadString(), Is.EqualTo("héllo wörld"));
        Assert.That(reader.ReadDouble(), Is.EqualTo(2.5d));
        Assert.That(reader.ReadBinary()!.Value.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
        Assert.That(reader.ReadUInt64(), Is.EqualTo(ulong.MaxValue));
        Assert.That(reader.End, Is.True);
        Assert.That(reader.Remaining, Is.Zero);
    }

    [Test]
    public void ReadsWithEmptySegmentsInterleaved()
    {
        var data = Sample();
        var reader = new MsgPackReader(Chunked(data, 0, 1, 0, 0, 3, 0, 2, 0, 5, 0, 1));
        AssertSample(ref reader);
    }

    [Test]
    public void ReadsWithEmptyFirstAndLastSegments()
    {
        var data = Sample();
        var reader = new MsgPackReader(Chunked(data, 0, data.Length, 0));
        AssertSample(ref reader);
    }

    [Test]
    public void ReadsEverySplitPoint()
    {
        var data = Sample();
        for (var split = 0; split <= data.Length; split++)
        {
            var reader = new MsgPackReader(Chunked(data, split, data.Length - split));
            AssertSample(ref reader);
        }
    }

    [Test]
    public void ConsumedAndPositionTrackAcrossSegments()
    {
        var data = Sample();
        var sequence = Segmented(data, 3);
        var reader = new MsgPackReader(sequence);
        reader.ReadArrayHeader();
        reader.ReadInt32();
        Assert.That(reader.Consumed, Is.EqualTo(6));
        Assert.That(sequence.Slice(0, reader.Position).Length, Is.EqualTo(6));

        var raw = reader.ReadRawValue(); // the string
        Assert.That(raw.ToArray(), Is.EqualTo(data.AsSpan(6, (int)raw.Length).ToArray()));
        Assert.That(sequence.Slice(0, reader.Position).Length, Is.EqualTo(reader.Consumed));
        Assert.That(reader.Remaining, Is.EqualTo(data.Length - reader.Consumed));
    }

    [Test]
    public void TrySkipSnapshotLeavesOriginalUntouched()
    {
        var data = Sample();
        var reader = new MsgPackReader(Segmented(data, 2));
        var probe = reader;
        Assert.That(probe.TrySkip(), Is.True);
        Assert.That(probe.End, Is.True);
        Assert.That(reader.Consumed, Is.Zero);
        AssertSample(ref reader);
    }

    [Test]
    public void TrySkipFailsAtEverySplitOfTruncatedData([Values(1, 2, 5)] int segmentSize)
    {
        var data = Sample();
        for (var length = 0; length < data.Length; length++)
        {
            var reader = new MsgPackReader(Segmented(data.AsSpan(0, length).ToArray(), segmentSize));
            Assert.That(reader.TrySkip(), Is.False, $"length {length}");
            Assert.That(reader.Consumed, Is.Zero);
        }
    }

    [Test]
    public void TruncatedReadsThrowAcrossSegments()
    {
        var data = Write((ref MsgPackWriter w) => w.Write(long.MinValue));
        var truncated = Segmented(data.AsSpan(0, data.Length - 1).ToArray(), 2);
        Assert.Throws<NexusSerializationException>(() =>
        {
            var reader = new MsgPackReader(truncated);
            reader.ReadInt64();
        });
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(255)]
    [TestCase(256)]
    [TestCase(257)]
    [TestCase(5000)]
    public void DecodesAsciiAndNonAsciiOfEveryBufferSize(int length)
    {
        var ascii = new string('a', length);
        var mixed = string.Concat(Enumerable.Range(0, length).Select(i => i % 3 == 0 ? "é" : i % 3 == 1 ? "字" : "z"));
        foreach (var s in new[] { ascii, mixed, ascii + "😀", "😀" + ascii })
        {
            var data = Write((ref MsgPackWriter w) => w.Write(s));
            Assert.That(Read(data, (ref MsgPackReader r) => r.ReadString()), Is.EqualTo(s));
            Assert.That(Read(data, (ref MsgPackReader r) => r.ReadString(), NexusSerializerOptions.Trusted), Is.EqualTo(s));

            var segmented = new MsgPackReader(Segmented(data, 7));
            Assert.That(segmented.ReadString(), Is.EqualTo(s));
        }
    }

    [TestCase(10)]
    [TestCase(400)]
    public void InvalidUtf8IsRejectedWhenStrictAndReplacedWhenTrusted(int prefixLength)
    {
        // A non-ASCII string whose last sequence is invalid (lone continuation byte), long enough to use the
        // pooled buffer when prefixLength is large.
        var bytes = Encoding.UTF8.GetBytes(new string('é', prefixLength / 2)).Concat(new byte[] { 0x80 }).ToArray();
        var data = Write((ref MsgPackWriter w) =>
        {
            w.WriteStringHeader(bytes.Length);
            w.WriteRaw(bytes);
        });

        Assert.Throws<NexusSerializationException>(() => Read(data, (ref MsgPackReader r) => r.ReadString()));
        var replaced = Read(data, (ref MsgPackReader r) => r.ReadString(), NexusSerializerOptions.Trusted);
        Assert.That(replaced, Is.EqualTo(Encoding.UTF8.GetString(bytes)));
    }

    [Test]
    public void TruncatedMultiByteSequenceIsRejectedWhenStrict()
    {
        // "字" is e5 ad 97; drop the last byte so the string ends mid-sequence.
        var data = new byte[] { 0xa2, 0xe5, 0xad };
        Assert.Throws<NexusSerializationException>(() => Read(data, (ref MsgPackReader r) => r.ReadString()));
        Assert.That(Read(data, (ref MsgPackReader r) => r.ReadString(), NexusSerializerOptions.Trusted), Is.EqualTo("�"));
    }

    [Test]
    public void ReadsFromMemoryWithOffset()
    {
        var data = Sample();
        var padded = new byte[data.Length + 10];
        data.CopyTo(padded, 5);
        var reader = new MsgPackReader(padded.AsMemory(5, data.Length));
        AssertSample(ref reader);
    }
}
