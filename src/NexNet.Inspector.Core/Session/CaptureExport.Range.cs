using System.Runtime.InteropServices;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Messages;

namespace NexNet.Inspector.Core.Session;

/// <summary>What <see cref="CaptureExport.WriteRange"/> exports.</summary>
/// <param name="FromTicks">The first Stopwatch tick of the section: frames that start at or after it are exported.</param>
/// <param name="ToTicks">The last tick: frames that start after it are left out.</param>
/// <param name="IncludeHandshake">
/// Also write the preamble and greetings, so the file frames and decodes on its own from its first byte.
/// </param>
/// <param name="IncludeSchema">Embed the schema the session names, so the file decodes without a workspace schema.</param>
public readonly record struct CaptureExportRange(long FromTicks, long ToTicks, bool IncludeHandshake = true, bool IncludeSchema = true);

/// <summary>What <see cref="CaptureExport.WriteRange"/> wrote.</summary>
/// <param name="Ops">Transport op blocks (an op the section cuts counts once per piece written).</param>
/// <param name="Gaps">Gap blocks inside the section.</param>
/// <param name="Bytes">Bytes of the file (without its footer when the output cannot seek).</param>
/// <param name="Frames">Frames of the session inside the section (the handshake not counted).</param>
/// <param name="Handshake">True when the handshake was written.</param>
/// <param name="Snapshot">True when a snapshot block describes the state at the section's start.</param>
public readonly record struct CaptureRangeResult(long Ops, long Gaps, long Bytes, int Frames, bool Handshake, bool Snapshot);

public static partial class CaptureExport
{
    /// <summary>
    /// Writes a section of <paramref name="session"/> (Export section as .nntl…) as a one-session recording another
    /// person can open on its own: the header and session-open block it was captured under, the embedded schema, a
    /// snapshot block describing what was open when the section starts, the handshake, the frames that start inside
    /// <see cref="CaptureExportRange.FromTicks"/>–<see cref="CaptureExportRange.ToTicks"/> with the gaps between them, and a
    /// session-close block marking the section's end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ops are cut at frame boundaries, so the section holds whole frames only, and its stream continues straight after
    /// the handshake: what lies between is left out rather than written as a gap, which would make the reader resynchronise
    /// (and possibly lose the section's first frames). Stream offsets in the exported file therefore count from the
    /// handshake, not from the original session's start; ticks, op ids and redaction ranges are kept. Redacted bytes stay
    /// redacted: the export copies the captured bytes, which are redacted already unless the source included secrets.
    /// </para>
    /// <para>
    /// The snapshot is synthesised from the assembled session: pipes that were open at the section's start (and the
    /// collections they carry), and large messages in transfer, so frames of what was already running resolve from the
    /// first one. A section that starts at the session's first frame instead keeps the session's own attach snapshot.
    /// </para>
    /// </remarks>
    /// <param name="session">The session.</param>
    /// <param name="output">Writable destination (left open).</param>
    /// <param name="range">The section and its options.</param>
    /// <param name="cancellationToken">Stops between batches; the output then holds a partial file without its footer.</param>
    /// <exception cref="IOException">The output failed.</exception>
    /// <exception cref="OperationCanceledException">Canceled.</exception>
    public static CaptureRangeResult WriteRange(CaptureSession session, Stream output, in CaptureExportRange range,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(output);
        var from = Math.Min(range.FromTicks, range.ToTicks);
        var to = Math.Max(range.FromTicks, range.ToTicks);

        RangePlan plan;
        lock (session.Gate)
            plan = RangePlan.Of(session, from, to, range.IncludeHandshake);

        var header = session.Header;
        if (string.IsNullOrEmpty(header.Producer))
            header = header with { Producer = DefaultProducer };

        var start = output.CanSeek ? output.Position : 0;
        using var writer = new NntlWriter(output, header, leaveOpen: true);
        var open = session.Open;
        if (range.IncludeSchema && session.SchemaBlock is { } schema && !open.SchemaHash.IsEmpty)
            writer.WriteSchema(schema.Hash.Span, schema.Schema.Span);
        else
            open = open with { SchemaHash = ReadOnlyMemory<byte>.Empty };
        writer.WriteSessionOpen(open);

        // Before any op: the assembler is seeded only before its first frame.
        var snapshot = false;
        if (plan.Snapshot is { } sections)
        {
            writer.WriteSnapshot(session.SessionId, Math.Max(from, open.Ticks), sections);
            snapshot = true;
        }
        else if (plan.KeepsOwnSnapshot && session.SnapshotBlock is { } own)
        {
            writer.WriteSnapshot(own.SessionId, own.Ticks, own.Sections.Span);
            snapshot = true;
        }

        long ops = 0;
        long gaps = 0;
        long lastTicks = Math.Max(from, open.Ticks);
        var ranges = new List<NntlByteRange>();
        var pieces = plan.Pieces;
        for (var i = 0; i < pieces.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (session.Gate)
            {
                for (var n = 0; n < BatchBlocks && i < pieces.Length; n++, i++)
                {
                    ref readonly var piece = ref pieces[i];
                    if (piece.Gap >= 0)
                    {
                        writer.WriteGap(session.Gaps[piece.Gap]);
                        gaps++;
                    }
                    else if (WritePiece(session, writer, piece, ranges))
                    {
                        ops++;
                    }
                    else
                    {
                        // Bytes no longer held (a live store trimmed them meanwhile): a gap keeps later offsets honest.
                        writer.WriteGap(new NntlGap(session.SessionId, piece.Direction, piece.Ticks, piece.Ticks, 1, piece.End - piece.Start));
                        gaps++;
                    }

                    lastTicks = Math.Max(lastTicks, piece.Ticks);
                }
            }
        }

        // The session's own close when the section reaches it; otherwise a close marking where the section ends.
        if (plan.Close is { } close && close.Ticks <= to)
            writer.WriteSessionClose(close);
        else
            writer.WriteSessionClose(new NntlSessionClose(session.SessionId, Math.Max(lastTicks, Math.Min(to, Math.Max(plan.EndTicks, lastTicks))),
                NntlCloseKind.RecordingStopped, default));

        writer.Close();
        var bytes = output.CanSeek ? output.Position - start : writer.Position;
        return new CaptureRangeResult(ops, gaps, bytes, plan.Frames, plan.Handshake, snapshot);
    }

    // Writes one op's slice with the redaction ranges inside it. Under the gate.
    private static bool WritePiece(CaptureSession session, NntlWriter writer, in RangePiece piece, List<NntlByteRange> ranges)
    {
        var store = session.GetStore(piece.Direction);
        var length = checked((int)(piece.End - piece.Start));
        if (!store.TryGetSegment(piece.Start, out var bytes) || bytes.Length < length)
            return false;

        ranges.Clear();
        foreach (var redaction in store.Redactions)
        {
            if (redaction.End <= piece.Start)
                continue;
            if (redaction.Offset >= piece.End)
                break;
            var s = Math.Max(redaction.Offset, piece.Start);
            var e = Math.Min(redaction.End, piece.End);
            ranges.Add(new NntlByteRange((int)(s - piece.Start), (int)(e - s)));
        }

        writer.WriteTransportOp(session.SessionId, piece.Direction, piece.OpId, piece.Ticks, bytes.Span[..length],
            CollectionsMarshal.AsSpan(ranges));
        return true;
    }

    // One block of the section: an op's slice [Start, End), or one of the session's gaps (Gap >= 0).
    private readonly record struct RangePiece(TapDirection Direction, long Start, long End, long OpId, long Ticks, long Source, int Gap, int Order);

    // What the section covers, fixed when the export begins.
    private sealed class RangePlan
    {
        public RangePiece[] Pieces = [];
        public NntlSnapshotSection[]? Snapshot;
        public bool KeepsOwnSnapshot;
        public bool Handshake;
        public int Frames;
        public NntlSessionClose? Close;
        public long EndTicks;

        public static RangePlan Of(CaptureSession session, long from, long to, bool includeHandshake)
        {
            var plan = new RangePlan { Close = session.Close, EndTicks = session.EndTicks };
            var pieces = new List<RangePiece>();
            var omitted = false;
            var firstStart = long.MaxValue;
            foreach (var direction in (ReadOnlySpan<TapDirection>)[TapDirection.Inbound, TapDirection.Outbound])
            {
                var frames = session.Assembler.GetFrames(direction);
                var store = session.GetStore(direction);
                var handshakeEnd = includeHandshake ? HandshakeEnd(session, direction, frames) : 0;

                var first = LowerBound(frames, from);
                var last = LowerBound(frames, to == long.MaxValue ? to : to + 1) - 1;
                var count = 0;
                long rangeStart = 0;
                long rangeEnd = 0;
                if (first <= last)
                {
                    rangeStart = frames[first].Offset;
                    rangeEnd = frames[last].Offset + frames[last].Length;
                    for (var i = first; i <= last; i++)
                    {
                        if (frames[i].Offset + frames[i].Length > handshakeEnd)
                            count++;
                    }

                    firstStart = Math.Min(firstStart, frames[first].StartTicks);
                }

                plan.Frames += count;
                for (var i = 0; i < first && i < frames.Length; i++)
                {
                    if (frames[i].Offset >= handshakeEnd)
                    {
                        omitted = true;
                        break;
                    }
                }

                // The handshake, then the section; the two merge when the section starts inside the handshake.
                var hasRange = count > 0;
                if (handshakeEnd > 0 && (!hasRange || rangeStart > handshakeEnd))
                {
                    plan.Handshake = true;
                    AddSlices(pieces, direction, store, frames[0].Offset, handshakeEnd, frames, splitFirst: false);
                }
                else if (handshakeEnd > 0)
                {
                    plan.Handshake = true;
                    rangeStart = frames[0].Offset;
                }

                if (hasRange)
                {
                    // Without the handshake before it, the reader resynchronises on the section's first op: it ends on a
                    // frame boundary, so the first frame is found by its own length.
                    AddSlices(pieces, direction, store, rangeStart, rangeEnd, frames, splitFirst: handshakeEnd == 0);
                    AddGaps(pieces, session, direction, rangeStart, rangeEnd);
                }
            }

            // Capture order: the blocks' order keys, ticks for equal keys, then the order they were planned in.
            var array = pieces.ToArray();
            Array.Sort(array, static (a, b) =>
            {
                var c = a.Source.CompareTo(b.Source);
                if (c != 0)
                    return c;
                c = a.Ticks.CompareTo(b.Ticks);
                return c != 0 ? c : a.Order.CompareTo(b.Order);
            });
            plan.Pieces = array;

            if (omitted)
                plan.Snapshot = SnapshotAt(session, firstStart == long.MaxValue ? from : Math.Min(from, firstStart));
            else
                plan.KeepsOwnSnapshot = true;
            return plan;
        }

        // The end of a direction's handshake: the preamble and the greeting after it, when captured from inception.
        private static long HandshakeEnd(CaptureSession session, TapDirection direction, ReadOnlySpan<FrameRecord> frames)
        {
            if (!session.IsFromInception(direction) || frames.IsEmpty)
                return 0;
            long end = 0;
            for (var i = 0; i < frames.Length && i < 3; i++)
            {
                var frame = frames[i];
                if (frame.Kind == FrameKind.Preamble)
                {
                    end = frame.Offset + frame.Length;
                    continue;
                }

                if (frame.Type is MessageType.ClientGreeting or MessageType.ClientGreetingReconnection or MessageType.ServerGreeting)
                    end = frame.Offset + frame.Length;
                break;
            }

            // Only when the store still holds it (a live store may have dropped its oldest ops).
            return end > 0 && session.GetStore(direction).TryFindOp(frames[0].Offset, out _) ? end : 0;
        }

        // The first frame that starts at or after a tick; frames of a direction start in tick order.
        private static int LowerBound(ReadOnlySpan<FrameRecord> frames, long ticks)
        {
            var lo = 0;
            var hi = frames.Length;
            while (lo < hi)
            {
                var mid = lo + ((hi - lo) >> 1);
                if (frames[mid].StartTicks < ticks)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            return lo;
        }

        private static void AddSlices(List<RangePiece> pieces, TapDirection direction, TransportOpStore store,
            long start, long end, ReadOnlySpan<FrameRecord> frames, bool splitFirst)
        {
            var ops = store.Ops;
            var index = store.TryFindOp(start, out var found) ? found : store.IndexAtOrAfter(start);
            var first = true;
            for (; index < ops.Length && ops[index].Offset < end; index++)
            {
                var op = ops[index];
                var s = Math.Max(op.Offset, start);
                var e = Math.Min(op.End, end);
                if (e <= s)
                    continue;
                if (first && splitFirst && LastFrameEnd(frames, s, e) is var cut && cut > s && cut < e)
                {
                    pieces.Add(new RangePiece(direction, s, cut, op.OpId, op.Ticks, op.Source, -1, pieces.Count));
                    s = cut;
                }

                first = false;
                pieces.Add(new RangePiece(direction, s, e, op.OpId, op.Ticks, op.Source, -1, pieces.Count));
            }
        }

        // The end of the last frame that starts at or after start and ends at or before end; start when none does.
        private static long LastFrameEnd(ReadOnlySpan<FrameRecord> frames, long start, long end)
        {
            var best = start;
            var lo = 0;
            var hi = frames.Length;
            while (lo < hi)
            {
                var mid = lo + ((hi - lo) >> 1);
                if (frames[mid].Offset < start)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            for (var i = lo; i < frames.Length; i++)
            {
                var frameEnd = frames[i].Offset + frames[i].Length;
                if (frameEnd > end)
                    break;
                best = frameEnd;
            }

            return best;
        }

        private static void AddGaps(List<RangePiece> pieces, CaptureSession session, TapDirection direction, long start, long end)
        {
            var gaps = session.Gaps;
            var marks = session.GapMarks;
            for (var i = 0; i < gaps.Length; i++)
            {
                if (gaps[i].Direction != direction || marks[i].Offset <= start || marks[i].Offset >= end)
                    continue;
                pieces.Add(new RangePiece(direction, marks[i].Offset, marks[i].Offset, 0, gaps[i].FromTicks, marks[i].Source, i, pieces.Count));
            }
        }

        // What was open when the section starts: pipes (and the collections they carry) and large messages in transfer.
        private static NntlSnapshotSection[]? SnapshotAt(CaptureSession session, long ticks)
        {
            var assembler = session.Assembler;
            var pipes = assembler.Pipes;
            var opened = new long[pipes.Length];
            var completed = new PipeStateFlags[pipes.Length];
            Array.Fill(opened, long.MaxValue);
            for (var i = 0; i < pipes.Length; i++)
            {
                if (pipes[i].FromSnapshot)
                    opened[i] = long.MinValue;
            }

            foreach (var e in assembler.PipeEvents)
            {
                if (!session.TryGetFrame(new FrameKey(e.Direction, e.Seq), out var frame) || frame.StartTicks >= ticks)
                    continue;
                if (e.Kind == PipeEventKind.Ready)
                    opened[e.Pipe] = Math.Min(opened[e.Pipe], frame.StartTicks);
                else if (e.Kind == PipeEventKind.State)
                    completed[e.Pipe] |= e.State;
            }

            const PipeStateFlags bothComplete = PipeStateFlags.ClientWriterServerReaderComplete | PipeStateFlags.ClientReaderServerWriterComplete;
            var open = new List<NntlPipeState>();
            var openIds = new HashSet<ushort>();
            for (var i = 0; i < pipes.Length; i++)
            {
                if (opened[i] >= ticks || pipes[i].Lifecycle == PipeLifecycle.Refused || (completed[i] & bothComplete) == bothComplete)
                    continue;
                if (!openIds.Add(pipes[i].PipeId))
                    continue;
                var flags = NntlPipeStateFlags.Ready | (completed[i] != PipeStateFlags.None ? NntlPipeStateFlags.Closing : 0);
                open.Add(new NntlPipeState(pipes[i].PipeId, flags, 0, 0, 0, 0));
            }

            // A collection's version as of the section's start: the last message before it that carried one.
            var collections = assembler.Collections;
            var versions = new int[collections.Length];
            Array.Fill(versions, -1);
            foreach (var message in assembler.CollectionMessages)
            {
                if (message.Version < 0 || message.Item < 0)
                    continue;
                var item = assembler.ChannelItems[message.Item];
                if (session.TryGetFrame(new FrameKey(item.Direction, item.FirstSeq), out var frame) && frame.StartTicks < ticks)
                    versions[message.Collection] = Math.Max(versions[message.Collection], message.Version);
            }

            var collectionStates = new List<NntlCollectionState>();
            foreach (var collection in collections)
            {
                if (collection.Pipe < 0 || !openIds.Contains(pipes[collection.Pipe].PipeId) || opened[collection.Pipe] >= ticks)
                    continue;
                collectionStates.Add(new NntlCollectionState(collection.CollectionId, default, default, default, 0,
                    versions[collection.Index], [pipes[collection.Pipe].PipeId]));
            }

            var large = new List<NntlLargeMessageState>();
            var fragments = new List<TraceFragment>();
            foreach (var message in assembler.Messages)
            {
                if (message.Kind != AssembledMessageKind.Large || message.FirstSeq < 0)
                    continue;
                if (!session.TryGetFrame(new FrameKey(message.Direction, message.FirstSeq), out var firstFrame) || firstFrame.StartTicks >= ticks)
                    continue;
                if (message.State != AssemblyState.Assembling && message.State != AssemblyState.Orphan
                    && session.TryGetFrame(new FrameKey(message.Direction, message.LastSeq), out var lastFrame) && lastFrame.StartTicks < ticks)
                    continue;

                fragments.Clear();
                assembler.GetFragments(message.Ref, fragments);
                long transferred = 0;
                foreach (var fragment in fragments)
                {
                    if (session.TryGetFrame(new FrameKey(fragment.Direction, fragment.Seq), out var f) && f.StartTicks < ticks)
                        transferred = Math.Max(transferred, fragment.MessageOffset + fragment.Length);
                }

                large.Add(new NntlLargeMessageState(message.Direction, message.LargeKind == LargeMessageKind.Result, message.InvocationId,
                    message.TotalLength, transferred));
            }

            if (open.Count == 0 && collectionStates.Count == 0 && large.Count == 0)
                return null;

            return
            [
                new(NntlSnapshotSectionKind.OpenPipes, NntlSnapshotSections.EncodeOpenPipes(CollectionsMarshal.AsSpan(open))),
                new(NntlSnapshotSectionKind.PendingInvocations,
                    NntlSnapshotSections.EncodeInvocations(new NntlInvocationState(0, 0, 0, 0, 0, 0, 0, 0, [.. large]))),
                new(NntlSnapshotSectionKind.CollectionState, NntlSnapshotSections.EncodeCollections(CollectionsMarshal.AsSpan(collectionStates))),
            ];
        }
    }
}
