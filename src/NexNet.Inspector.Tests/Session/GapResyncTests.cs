using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Tests.Assembly;
using NexNet.Messages;
using NexNet.Serialization;

namespace NexNet.Inspector.Tests.Session;

/// <summary>Parsing and assembly carry on after ops are lost (Drop-mode gaps) and after an attach mid-stream.</summary>
public class GapResyncTests
{
    // Ten 8-byte Invocation frames with IDs 0..9: [type][len 5][94 id method flags 90].
    private static byte[] TenInvocations()
    {
        var script = new WireScript();
        for (ushort i = 0; i < 10; i++)
            script.Invocation(TapDirection.Inbound, i, 3, InvocationFlags.None, static (ref MsgPackWriter w) => w.WriteArrayHeader(0));
        return script.Frames.SelectMany(f => f.Bytes).ToArray();
    }

    [Test]
    public async Task Reads_resume_at_the_first_op_whose_frames_chain_to_its_end()
    {
        var bytes = TenInvocations();
        await Assert.That(bytes.Length).IsEqualTo(80);
        var frames = new List<FrameRecord>();
        var parser = new FrameParser(TapDirection.Inbound, expectPreamble: false);

        parser.Push(0, 100, bytes.AsSpan(0, 12), frames);
        parser.Push(1, 101, bytes.AsSpan(12, 18), frames);

        // Op [30, 45) is lost; the frame cut by it (24..32) is reported incomplete.
        await Assert.That(parser.Gap(15, frames)).IsEqualTo(1);
        await Assert.That(parser.IsResyncing).IsTrue();
        await Assert.That(parser.StreamOffset).IsEqualTo(45);

        // [45, 60) starts mid-frame and ends mid-frame: one whole frame is not enough evidence, so it is passed over.
        parser.Push(3, 103, bytes.AsSpan(45, 15), frames);
        await Assert.That(parser.IsResyncing).IsTrue();

        // [60, 80) holds the tail of frame 7, then frames 8 and 9 ending exactly with the op.
        parser.Push(4, 104, bytes.AsSpan(60, 20), frames);
        await Assert.That(parser.IsResyncing).IsFalse();
        await Assert.That(parser.IsFaulted).IsFalse();

        await Assert.That(frames.Select(f => f.Seq).ToArray()).IsEquivalentTo(new long[] { 0, 1, 2, 3, 4, 5 });
        await Assert.That(frames.Select(f => f.Offset).ToArray()).IsEquivalentTo(new long[] { 0, 8, 16, 24, 64, 72 });
        await Assert.That(frames[3].Flags.HasFlag(FrameFlags.Incomplete)).IsTrue();
        await Assert.That(frames.Where((_, i) => i != 3).All(f => f.Flags == FrameFlags.None || f.Flags == FrameFlags.TransportSplit)).IsTrue();
        await Assert.That(parser.GapBytes).IsEqualTo(15);
        await Assert.That(parser.SkippedBytes).IsEqualTo(15 + 4);
    }

    [Test]
    public async Task Writes_resume_at_the_next_op_because_each_is_one_flush_of_whole_frames()
    {
        var bytes = TenInvocations();
        var frames = new List<FrameRecord>();
        var parser = new FrameParser(TapDirection.Outbound, expectPreamble: false);
        parser.Push(0, 100, bytes.AsSpan(0, 16), frames);
        parser.Gap(16, frames);

        // One frame and the start of the next: an outbound op's first byte is trusted on one plausible header.
        parser.Push(2, 102, bytes.AsSpan(32, 12), frames);
        await Assert.That(parser.IsResyncing).IsFalse();
        parser.Push(3, 103, bytes.AsSpan(44, 36), frames);
        await Assert.That(frames.Select(f => f.Offset).ToArray()).IsEquivalentTo(new long[] { 0, 8, 32, 40, 48, 56, 64, 72 });
    }

    [Test]
    public async Task Resync_refuses_bytes_that_only_look_like_one_header()
    {
        var parser = new FrameParser(TapDirection.Inbound, FrameParserStart.Resync);
        var frames = new List<FrameRecord>();

        // A disconnect byte followed by more bytes, and type bytes with impossible bodies, are not frame starts.
        parser.Push(0, 1, [21, 1, 1, (byte)MessageType.Invocation, 2, 0, 0x01, 0x02], frames);
        await Assert.That(frames).IsEmpty();
        await Assert.That(parser.IsResyncing).IsTrue();
        await Assert.That(parser.SkippedBytes).IsEqualTo(8);
    }

    [Test]
    public async Task Random_cuts_and_lost_ops_never_misframe_after_resync()
    {
        // Frames of varied sizes; whatever ops are lost, every frame emitted after a resync is a real frame.
        var script = new WireScript();
        var random = new Random(11);
        for (ushort i = 0; i < 400; i++)
        {
            var size = random.Next(0, 300);
            if (i % 7 == 0)
                script.Ping(TapDirection.Inbound);
            else
                script.Invocation(TapDirection.Inbound, i, 9, InvocationFlags.None, (ref MsgPackWriter w) =>
                {
                    w.WriteArrayHeader(1);
                    w.WriteBinary(new byte[size]);
                });
        }

        var bytes = script.Frames.SelectMany(f => f.Bytes).ToArray();
        var starts = new HashSet<long>();
        long at = 0;
        foreach (var (_, frame) in script.Frames)
        {
            starts.Add(at);
            at += frame.Length;
        }

        for (var run = 0; run < 100; run++)
        {
            var frames = new List<FrameRecord>();
            var parser = new FrameParser(TapDirection.Inbound, expectPreamble: false);
            long op = 0;
            for (var position = 0; position < bytes.Length;)
            {
                var size = Math.Min(bytes.Length - position, random.Next(1, 900));
                if (random.Next(6) == 0)
                    parser.Gap(size, frames);
                else
                    parser.Push(op, op, bytes.AsSpan(position, size), frames);
                op++;
                position += size;
            }

            await Assert.That(parser.IsFaulted).IsFalse();
            foreach (var frame in frames)
                await Assert.That(starts.Contains(frame.Offset)).IsTrue().Because($"run {run}: frame at {frame.Offset}");
        }
    }

    [Test]
    public async Task Assembly_marks_what_a_gap_cut_and_starts_fresh_after_it()
    {
        var inbound = new LiveOpStore();
        var outbound = new LiveOpStore();
        var assembler = new MessageAssembler(inbound, outbound);
        var parser = new FrameParser(TapDirection.Inbound, expectPreamble: false);

        var script = new WireScript()
            .MessageStart(TapDirection.Inbound, 7, LargeMessageKind.Argument, 10)
            .Chunk(TapDirection.Inbound, 7, LargeMessageKind.Argument, new byte[4])
            .PipeWrite(TapDirection.Inbound, 0x0201, new byte[ushort.MaxValue]);
        long op = 0;
        foreach (var (_, frame) in script.Frames)
        {
            inbound.Append(Op(frame, op), 0);
            parser.Push(op, op++, frame, assembler);
        }

        // The rest of the large message and of the pipe write were lost.
        parser.Gap(6 + 3 + 3, assembler);
        assembler.Gap(TapDirection.Inbound);
        inbound.SkipGap(12);

        var after = new WireScript()
            .Chunk(TapDirection.Inbound, 7, LargeMessageKind.Argument, new byte[2])
            .PipeWrite(TapDirection.Inbound, 0x0201, new byte[10]);
        var tail = after.Frames.SelectMany(f => f.Bytes).ToArray();
        inbound.Append(Op(tail, op), 0);
        parser.Push(op, op, tail, assembler);

        await Assert.That(assembler.GapCount).IsEqualTo(1);
        var messages = assembler.Messages.ToArray();
        await Assert.That(messages[0].State).IsEqualTo(AssemblyState.Truncated);
        await Assert.That(messages[1].Kind).IsEqualTo(AssembledMessageKind.PipeRun);
        await Assert.That(messages[1].State).IsEqualTo(AssemblyState.Truncated);
        await Assert.That(messages[2].State).IsEqualTo(AssemblyState.Orphan);
        await Assert.That(messages[2].InvocationId).IsEqualTo((ushort)7);
        await Assert.That(messages[3].Kind).IsEqualTo(AssembledMessageKind.PipeRun);
        await Assert.That(messages[3].State).IsEqualTo(AssemblyState.Complete);
        await Assert.That(assembler.Pipes.Length).IsEqualTo(1);
    }

    internal static NntlTransportOp Op(byte[] bytes, long opId, TapDirection direction = TapDirection.Inbound)
        => new(1, direction, opId, 1000 + opId, NntlTransportOpFlags.None, ReadOnlyMemory<NntlByteRange>.Empty, bytes);
}
