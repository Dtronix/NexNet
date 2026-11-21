using System.Buffers;
using System.Text;
using static NexNet.Serialization.Tests.TestHelpers;

namespace NexNet.Serialization.Tests;

/// <summary>
/// Covers the reader's span cursor: segment boundaries, empty segments, positions and string decoding paths.
/// </summary>
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

    /// <summary>
    /// The values <see cref="Sample"/> holds, read synchronously so the ref struct reader is not live across an await.
    /// </summary>
    private readonly record struct SampleValues(
        int Header, int Int32, string? String, double Double, byte[]? Binary, ulong UInt64, bool End, long Remaining);

    private static SampleValues ReadSample(ref MsgPackReader reader) => new(
        reader.ReadArrayHeader(),
        reader.ReadInt32(),
        reader.ReadString(),
        reader.ReadDouble(),
        reader.ReadBinary()?.ToArray(),
        reader.ReadUInt64(),
        reader.End,
        reader.Remaining);

    private static async Task AssertSample(SampleValues values)
    {
        await Assert.That(values.Header).IsEqualTo(5);
        await Assert.That(values.Int32).IsEqualTo(int.MinValue);
        await Assert.That(values.String).IsEqualTo("héllo wörld");
        await Assert.That(values.Double).IsEqualTo(2.5d);
        await Assert.That(values.Binary).IsEquivalentTo(new byte[] { 1, 2, 3, 4, 5 }, CollectionOrdering.Matching);
        await Assert.That(values.UInt64).IsEqualTo(ulong.MaxValue);
        await Assert.That(values.End).IsTrue();
        await Assert.That(values.Remaining).IsZero();
    }

    private static SampleValues ReadSample(ReadOnlySequence<byte> sequence)
    {
        var reader = new MsgPackReader(sequence);
        return ReadSample(ref reader);
    }

    [Test]
    public async Task ReadsWithEmptySegmentsInterleaved()
    {
        var data = Sample();
        await AssertSample(ReadSample(Chunked(data, 0, 1, 0, 0, 3, 0, 2, 0, 5, 0, 1)));
    }

    [Test]
    public async Task ReadsWithEmptyFirstAndLastSegments()
    {
        var data = Sample();
        await AssertSample(ReadSample(Chunked(data, 0, data.Length, 0)));
    }

    [Test]
    public async Task ReadsEverySplitPoint()
    {
        var data = Sample();
        for (var split = 0; split <= data.Length; split++)
            await AssertSample(ReadSample(Chunked(data, split, data.Length - split)));
    }

    [Test]
    public async Task ConsumedAndPositionTrackAcrossSegments()
    {
        var data = Sample();
        var sequence = Segmented(data, 3);
        long consumedAfterInt, slicedAfterInt, slicedAfterRaw, consumedAfterRaw, remainingAfterRaw;
        byte[] raw;
        {
            var reader = new MsgPackReader(sequence);
            reader.ReadArrayHeader();
            reader.ReadInt32();
            consumedAfterInt = reader.Consumed;
            slicedAfterInt = sequence.Slice(0, reader.Position).Length;

            raw = reader.ReadRawValue().ToArray(); // the string
            slicedAfterRaw = sequence.Slice(0, reader.Position).Length;
            consumedAfterRaw = reader.Consumed;
            remainingAfterRaw = reader.Remaining;
        }

        await Assert.That(consumedAfterInt).IsEqualTo(6);
        await Assert.That(slicedAfterInt).IsEqualTo(6);
        await Assert.That(raw).IsEquivalentTo(data.AsSpan(6, raw.Length).ToArray(), CollectionOrdering.Matching);
        await Assert.That(slicedAfterRaw).IsEqualTo(consumedAfterRaw);
        await Assert.That(remainingAfterRaw).IsEqualTo(data.Length - consumedAfterRaw);
    }

    [Test]
    public async Task TrySkipSnapshotLeavesOriginalUntouched()
    {
        var data = Sample();
        bool skipped, probeEnd;
        long consumed;
        SampleValues values;
        {
            var reader = new MsgPackReader(Segmented(data, 2));
            var probe = reader;
            skipped = probe.TrySkip();
            probeEnd = probe.End;
            consumed = reader.Consumed;
            values = ReadSample(ref reader);
        }

        await Assert.That(skipped).IsTrue();
        await Assert.That(probeEnd).IsTrue();
        await Assert.That(consumed).IsZero();
        await AssertSample(values);
    }

    [Test]
    [MatrixDataSource]
    public async Task TrySkipFailsAtEverySplitOfTruncatedData([Matrix(1, 2, 5)] int segmentSize)
    {
        var data = Sample();
        for (var length = 0; length < data.Length; length++)
        {
            bool skipped;
            long consumed;
            {
                var reader = new MsgPackReader(Segmented(data.AsSpan(0, length).ToArray(), segmentSize));
                skipped = reader.TrySkip();
                consumed = reader.Consumed;
            }

            await Assert.That(skipped).IsFalse().Because($"length {length}");
            await Assert.That(consumed).IsZero();
        }
    }

    [Test]
    public async Task TruncatedReadsThrowAcrossSegments()
    {
        var data = Write((ref MsgPackWriter w) => w.Write(long.MinValue));
        var truncated = Segmented(data.AsSpan(0, data.Length - 1).ToArray(), 2);
        await Assert.That(() =>
        {
            var reader = new MsgPackReader(truncated);
            reader.ReadInt64();
        }).ThrowsExactly<NexusSerializationException>();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(255)]
    [Arguments(256)]
    [Arguments(257)]
    [Arguments(5000)]
    public async Task DecodesAsciiAndNonAsciiOfEveryBufferSize(int length)
    {
        var ascii = new string('a', length);
        var mixed = string.Concat(Enumerable.Range(0, length).Select(i => i % 3 == 0 ? "é" : i % 3 == 1 ? "字" : "z"));
        foreach (var s in new[] { ascii, mixed, ascii + "😀", "😀" + ascii })
        {
            var data = Write((ref MsgPackWriter w) => w.Write(s));
            await Assert.That(Read(data, (ref MsgPackReader r) => r.ReadString())).IsEqualTo(s);
            await Assert.That(Read(data, (ref MsgPackReader r) => r.ReadString(), NexusSerializerOptions.Trusted)).IsEqualTo(s);

            string? segmentedValue;
            {
                var segmented = new MsgPackReader(Segmented(data, 7));
                segmentedValue = segmented.ReadString();
            }

            await Assert.That(segmentedValue).IsEqualTo(s);
        }
    }

    [Test]
    [Arguments(10)]
    [Arguments(400)]
    public async Task InvalidUtf8IsRejectedWhenStrictAndReplacedWhenTrusted(int prefixLength)
    {
        // A non-ASCII string whose last sequence is invalid (lone continuation byte), long enough to use the
        // pooled buffer when prefixLength is large.
        var bytes = Encoding.UTF8.GetBytes(new string('é', prefixLength / 2)).Concat(new byte[] { 0x80 }).ToArray();
        var data = Write((ref MsgPackWriter w) =>
        {
            w.WriteStringHeader(bytes.Length);
            w.WriteRaw(bytes);
        });

        await Assert.That(() => Read(data, (ref MsgPackReader r) => r.ReadString())).ThrowsExactly<NexusSerializationException>();
        var replaced = Read(data, (ref MsgPackReader r) => r.ReadString(), NexusSerializerOptions.Trusted);
        await Assert.That(replaced).IsEqualTo(Encoding.UTF8.GetString(bytes));
    }

    [Test]
    public async Task TruncatedMultiByteSequenceIsRejectedWhenStrict()
    {
        // "字" is e5 ad 97; drop the last byte so the string ends mid-sequence.
        var data = new byte[] { 0xa2, 0xe5, 0xad };
        await Assert.That(() => Read(data, (ref MsgPackReader r) => r.ReadString())).ThrowsExactly<NexusSerializationException>();
        await Assert.That(Read(data, (ref MsgPackReader r) => r.ReadString(), NexusSerializerOptions.Trusted)).IsEqualTo("�");
    }

    [Test]
    public async Task ReadsFromMemoryWithOffset()
    {
        var data = Sample();
        var padded = new byte[data.Length + 10];
        data.CopyTo(padded, 5);
        SampleValues values;
        {
            var reader = new MsgPackReader(padded.AsMemory(5, data.Length));
            values = ReadSample(ref reader);
        }

        await AssertSample(values);
    }
}
