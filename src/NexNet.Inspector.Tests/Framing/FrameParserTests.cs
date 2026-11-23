using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Messages;

namespace NexNet.Inspector.Tests.Framing;

public class FrameParserTests
{
    [Test]
    public async Task Every_golden_frame_parses_alone()
    {
        var vectors = new List<GoldenFrame> { GoldenFrames.Preamble };
        vectors.AddRange(GoldenFrames.Frames);
        vectors.AddRange(GoldenFrames.Messages);
        await Assert.That(GoldenFrames.Frames.Count).IsEqualTo(18);
        await Assert.That(GoldenFrames.Messages.Count).IsEqualTo(29);

        foreach (var vector in vectors)
        {
            var parser = new FrameParser(TapDirection.Outbound, expectPreamble: vector.Kind == FrameKind.Preamble);
            var records = new List<FrameRecord>();
            var emitted = parser.Push(7, 70, vector.Bytes, records);

            await Assert.That(emitted).IsEqualTo(1).Because(vector.Id);
            await Assert.That(records.Count).IsEqualTo(1).Because(vector.Id);
            await Assert.That(records[0]).IsEqualTo(Expected(vector, seq: 0, offset: 0, opId: 7, ticks: 70)).Because(vector.Id);
            await Assert.That(parser.HasPending).IsFalse().Because(vector.Id);
            await Assert.That(parser.Complete(records)).IsFalse().Because(vector.Id);
        }
    }

    [Test]
    public async Task Golden_stream_in_one_op_gives_every_frame_in_order()
    {
        var (stream, frames) = GoldenFrames.Stream();
        var records = ParseInOps(stream, [stream.Length]);

        await Assert.That(records.Count).IsEqualTo(frames.Count);
        long offset = 0;
        for (var i = 0; i < frames.Count; i++)
        {
            await Assert.That(records[i]).IsEqualTo(Expected(frames[i], i, offset, opId: 0, ticks: 1000)).Because(frames[i].Id);
            offset += frames[i].Bytes.Length;
        }
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(42)]
    [Arguments(20261122)]
    public async Task Random_rechunking_gives_the_same_frames_and_marks_transport_splits(int seed)
    {
        var (stream, _) = GoldenFrames.Stream();
        var baseline = ParseInOps(stream, [stream.Length]);
        var random = new Random(seed);

        // One-byte ops split every multi-byte frame; small and large random ops mix splits with whole frames per op.
        int[][] splits =
        [
            Enumerable.Repeat(1, stream.Length).ToArray(),
            RandomSizes(random, stream.Length, 1, 8),
            RandomSizes(random, stream.Length, 1, 40),
            RandomSizes(random, stream.Length, 1, 300),
        ];

        foreach (var sizes in splits)
        {
            var records = ParseInOps(stream, sizes);
            await Assert.That(records.Count).IsEqualTo(baseline.Count);

            var opStarts = new long[sizes.Length];
            for (var i = 1; i < sizes.Length; i++)
                opStarts[i] = opStarts[i - 1] + sizes[i - 1];

            var splitCount = 0;
            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i];
                await Assert.That(Normalize(record)).IsEqualTo(Normalize(baseline[i]));

                // The op that holds a byte is the last op starting at or before it.
                var firstOp = OpOf(opStarts, record.Offset);
                var lastOp = OpOf(opStarts, record.Offset + record.Length - 1);
                await Assert.That(record.FirstOpId).IsEqualTo(firstOp);
                await Assert.That(record.LastOpId).IsEqualTo(lastOp);
                await Assert.That(record.StartTicks).IsEqualTo(1000 + firstOp);
                await Assert.That(record.EndTicks).IsEqualTo(1000 + lastOp);
                var expectedFlags = firstOp != lastOp ? FrameFlags.TransportSplit : FrameFlags.None;
                await Assert.That(record.Flags).IsEqualTo(expectedFlags);
                if (firstOp != lastOp)
                    splitCount++;
            }

            // With one-byte ops every multi-byte frame is split; the random sizes may happen to cut only at boundaries.
            if (sizes.Length == stream.Length)
                await Assert.That(splitCount).IsEqualTo(baseline.Count(r => r.Length > 1));
        }
    }

    [Test]
    public async Task Stream_cut_inside_a_frame_ends_with_an_incomplete_record()
    {
        var (stream, frames) = GoldenFrames.Stream();
        var baseline = ParseInOps(stream, [stream.Length]);
        var random = new Random(5);

        for (var cut = 1; cut < stream.Length; cut++)
        {
            var frameIndex = baseline.FindIndex(r => r.Offset < cut && cut < r.Offset + r.Length);
            var prefix = stream.AsSpan(0, cut).ToArray();
            var parser = new FrameParser(TapDirection.Outbound, expectPreamble: true);
            var records = new List<FrameRecord>();
            var sizes = RandomSizes(random, cut, 1, 30);
            var pos = 0;
            for (var op = 0; op < sizes.Length; op++)
            {
                parser.Push(op, 1000 + op, prefix.AsSpan(pos, sizes[op]), records);
                pos += sizes[op];
            }

            var pendingFound = parser.TryGetPending(out var pending);
            var completed = parser.Complete(records);
            await Assert.That(completed).IsEqualTo(frameIndex >= 0);
            await Assert.That(pendingFound).IsEqualTo(frameIndex >= 0);
            if (frameIndex < 0)
            {
                await Assert.That(records.Count(r => r.Offset + r.Length <= cut)).IsEqualTo(records.Count);
                continue;
            }

            await Assert.That(records.Count).IsEqualTo(frameIndex + 1);
            for (var i = 0; i < frameIndex; i++)
                await Assert.That(Normalize(records[i])).IsEqualTo(Normalize(baseline[i]));

            var last = records[frameIndex];
            await Assert.That(last).IsEqualTo(pending);
            await Assert.That(last.Flags.HasFlag(FrameFlags.Incomplete)).IsTrue();
            await Assert.That(last.Seq).IsEqualTo(frameIndex);
            await Assert.That(last.Offset).IsEqualTo(baseline[frameIndex].Offset);
            await Assert.That(last.Length).IsEqualTo((int)(cut - last.Offset));
            await Assert.That(last.LastOpId).IsEqualTo(sizes.Length - 1);
            await Assert.That(last.Kind).IsEqualTo(frames[frameIndex].Kind);
            await Assert.That(last.Type).IsEqualTo(frames[frameIndex].Type);

            // Fields are filled in as their bytes arrive.
            var expected = frames[frameIndex];
            var arrived = last.Length;
            var bodyLength = expected.HeaderLength == 3 && arrived >= 3 ? expected.BodyLength : 0;
            await Assert.That(last.BodyLength).IsEqualTo(bodyLength);
            var targetArrived = arrived >= expected.HeaderLength + expected.PostHeaderLength;
            await Assert.That(last.Target).IsEqualTo(targetArrived ? expected.Target : FrameTarget.None);
            await Assert.That(last.TargetId).IsEqualTo(targetArrived ? expected.TargetId : (ushort)0);
        }
    }

    [Test]
    public async Task Pending_frame_completes_with_the_seq_it_was_previewed_with()
    {
        var frame = GoldenFrames.Frames.First(f => f.Type == MessageType.MessageChunk).Bytes;
        var parser = new FrameParser(TapDirection.Inbound, expectPreamble: false);
        var records = new List<FrameRecord>();

        parser.Push(0, 10, frame.AsSpan(0, 4), records);
        await Assert.That(parser.TryGetPending(out var pending)).IsTrue();
        await Assert.That(pending.BodyLength).IsEqualTo(3);
        await Assert.That(pending.Target).IsEqualTo(FrameTarget.None);

        parser.Push(1, 11, frame.AsSpan(4), records);
        await Assert.That(parser.TryGetPending(out _)).IsFalse();
        await Assert.That(records.Count).IsEqualTo(1);
        await Assert.That(records[0].Seq).IsEqualTo(pending.Seq);
        await Assert.That(records[0].Flags).IsEqualTo(FrameFlags.TransportSplit);
        await Assert.That(records[0].Direction).IsEqualTo(TapDirection.Inbound);
        await Assert.That(records[0].Target).IsEqualTo(FrameTarget.Invocation);
        await Assert.That(records[0].TargetId).IsEqualTo((ushort)5);
        await Assert.That(records[0].BodyOffset).IsEqualTo(6);
    }

    [Test]
    public async Task Every_disconnect_code_is_a_one_byte_frame()
    {
        for (byte code = 20; code <= 38; code++)
        {
            var parser = new FrameParser(TapDirection.Inbound, expectPreamble: false);
            var records = new List<FrameRecord>();
            parser.Push(0, 0, [code, code], records);
            await Assert.That(records.Count).IsEqualTo(2);
            await Assert.That(records[1].Type).IsEqualTo((MessageType)code);
            await Assert.That(records[1].Length).IsEqualTo(1);
            await Assert.That(records[1].Offset).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments((byte)0)]
    [Arguments((byte)2)]
    [Arguments((byte)39)]
    [Arguments((byte)52)]
    [Arguments((byte)121)]
    [Arguments((byte)255)]
    public async Task Unknown_type_keeps_earlier_frames_and_stops_framing(byte type)
    {
        var parser = new FrameParser(TapDirection.Inbound, expectPreamble: false);
        var records = new List<FrameRecord>();
        byte[] op = [1, 21, type, 1, 1];

        var emitted = parser.Push(0, 5, op, records);
        await Assert.That(emitted).IsEqualTo(3);
        await Assert.That(records[0].Type).IsEqualTo(MessageType.Ping);
        await Assert.That(records[1].Type).IsEqualTo(MessageType.DisconnectGraceful);
        await Assert.That(records[2].Kind).IsEqualTo(FrameKind.Malformed);
        await Assert.That(records[2].Type).IsEqualTo((MessageType)type);
        await Assert.That(records[2].Offset).IsEqualTo(2);
        await Assert.That(records[2].Length).IsEqualTo(3);
        await Assert.That(parser.IsFaulted).IsTrue();
        await Assert.That(parser.FaultOffset).IsEqualTo(2);

        await Assert.That(parser.Push(1, 6, [1, 1, 1, 1], records)).IsEqualTo(0);
        await Assert.That(records.Count).IsEqualTo(3);
        await Assert.That(parser.StreamOffset).IsEqualTo(9);
        await Assert.That(parser.UnframedBytes).IsEqualTo(7);
        await Assert.That(parser.Complete(records)).IsFalse();
    }

    [Test]
    public async Task Bad_preamble_split_across_ops_is_one_malformed_range()
    {
        var parser = new FrameParser(TapDirection.Outbound, expectPreamble: true);
        var records = new List<FrameRecord>();
        byte[] bad = [0x4E, 0x6E, 0x50, 0x14, 0, 1, 0, 1, 1, 1];

        parser.Push(0, 100, bad.AsSpan(0, 5), records);
        await Assert.That(parser.TryGetPending(out var pending)).IsTrue();
        await Assert.That(pending.Kind).IsEqualTo(FrameKind.Preamble);
        parser.Push(1, 101, bad.AsSpan(5), records);

        await Assert.That(records.Count).IsEqualTo(1);
        await Assert.That(records[0].Kind).IsEqualTo(FrameKind.Malformed);
        await Assert.That(records[0].Offset).IsEqualTo(0);
        await Assert.That(records[0].Length).IsEqualTo(bad.Length);
        await Assert.That(records[0].FirstOpId).IsEqualTo(0);
        await Assert.That(records[0].LastOpId).IsEqualTo(1);
        await Assert.That(records[0].Flags).IsEqualTo(FrameFlags.TransportSplit);
    }

    [Test]
    public async Task Offsets_start_at_the_given_stream_offset_and_empty_ops_change_nothing()
    {
        var frame = GoldenFrames.Frames.First(f => f.Type == MessageType.Invocation).Bytes;
        var parser = new FrameParser(TapDirection.Inbound, expectPreamble: false, startOffset: 1000);
        var records = new List<FrameRecord>();

        parser.Push(0, 1, frame.AsSpan(0, 2), records);
        parser.Push(1, 2, ReadOnlySpan<byte>.Empty, records);
        parser.Push(2, 3, frame.AsSpan(2), records);

        await Assert.That(records.Count).IsEqualTo(1);
        await Assert.That(records[0].Offset).IsEqualTo(1000);
        await Assert.That(records[0].LastOpId).IsEqualTo(2);
        await Assert.That(parser.StreamOffset).IsEqualTo(1000 + frame.Length);
    }

    [Test]
    public async Task Push_after_complete_throws()
    {
        var parser = new FrameParser(TapDirection.Inbound, expectPreamble: false);
        parser.Complete(new List<FrameRecord>());
        await Assert.That(() => parser.Push(0, 0, [1], new List<FrameRecord>())).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Pushing_one_byte_ops_into_a_struct_sink_allocates_nothing()
    {
        var (stream, frames) = GoldenFrames.Stream();
        var parser = new FrameParser(TapDirection.Outbound, expectPreamble: true);
        var counter = new int[1];

        // Warm up JIT paths on a separate parser so tiering does not count.
        ParseCounting(new FrameParser(TapDirection.Outbound, expectPreamble: true), stream, counter);
        counter[0] = 0;

        var before = GC.GetAllocatedBytesForCurrentThread();
        ParseCounting(parser, stream, counter);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(counter[0]).IsEqualTo(frames.Count);
        await Assert.That(allocated).IsEqualTo(0);
    }

    private static void ParseCounting(FrameParser parser, byte[] stream, int[] counter)
    {
        var sink = new CountingSink(counter);
        for (var i = 0; i < stream.Length; i++)
            parser.Push(i, i, stream.AsSpan(i, 1), sink);
    }

    private readonly struct CountingSink(int[] counter) : IFrameSink
    {
        public void Add(in FrameRecord frame) => counter[0]++;
    }

    private static List<FrameRecord> ParseInOps(byte[] stream, int[] sizes)
    {
        var parser = new FrameParser(TapDirection.Outbound, expectPreamble: true);
        var records = new List<FrameRecord>();
        var pos = 0;
        for (var op = 0; op < sizes.Length; op++)
        {
            parser.Push(op, 1000 + op, stream.AsSpan(pos, sizes[op]), records);
            pos += sizes[op];
        }

        parser.Complete(records);
        return records;
    }

    private static int[] RandomSizes(Random random, int total, int min, int max)
    {
        var sizes = new List<int>();
        while (total > 0)
        {
            var size = Math.Min(total, random.Next(min, max + 1));
            sizes.Add(size);
            total -= size;
        }

        return sizes.ToArray();
    }

    private static long OpOf(long[] opStarts, long offset)
    {
        var index = Array.BinarySearch(opStarts, offset);
        return index >= 0 ? index : ~index - 1;
    }

    private static FrameRecord Normalize(FrameRecord record)
        => record with { FirstOpId = 0, LastOpId = 0, StartTicks = 0, EndTicks = 0, Flags = FrameFlags.None };

    private static FrameRecord Expected(GoldenFrame vector, long seq, long offset, long opId, long ticks) => new()
    {
        Seq = seq,
        Direction = TapDirection.Outbound,
        Kind = vector.Kind,
        Type = vector.Type,
        Flags = FrameFlags.None,
        StartTicks = ticks,
        EndTicks = ticks,
        FirstOpId = opId,
        LastOpId = opId,
        Offset = offset,
        Length = vector.Bytes.Length,
        HeaderLength = (byte)vector.HeaderLength,
        PostHeaderLength = (byte)vector.PostHeaderLength,
        BodyLength = vector.BodyLength,
        Target = vector.Target,
        TargetId = vector.TargetId,
        ChunkKind = vector.ChunkKind,
    };
}
