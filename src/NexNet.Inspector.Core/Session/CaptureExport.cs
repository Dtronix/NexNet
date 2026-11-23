using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.Core.Session;

/// <summary>What <see cref="CaptureExport.Write"/> wrote.</summary>
/// <param name="Ops">Transport op blocks.</param>
/// <param name="Gaps">Gap blocks (the session's own, plus one per op whose bytes could not be had).</param>
/// <param name="Bytes">Bytes of the file (without its footer when the output cannot seek).</param>
/// <param name="Closed">True when a session-close block was written.</param>
public readonly record struct CaptureExportResult(long Ops, long Gaps, long Bytes, bool Closed);

/// <summary>
/// Saves a <see cref="CaptureSession"/> as a one-session <c>.nntl</c> recording (Save .nntl…): the header it was
/// captured under, the schema it names, the session-open block, then its snapshot, transport ops (with their redaction
/// ranges) and gaps in capture order, its close block, and the index footer. Reopened, the file frames and assembles
/// exactly as the session did.
/// </summary>
/// <remarks>
/// <para>
/// Capture order comes from the blocks' order keys (<see cref="StreamOp.Source"/>, <see cref="GapMark.Source"/>,
/// <see cref="CaptureSession.SnapshotSource"/>): block offsets for a recording, the stream's block sequence for a live
/// attach. Equal keys (a session fed without them) fall back to ticks.
/// </para>
/// <para>
/// The session may still be growing (a live attach saved while it streams): what it held when the save began is
/// written, under its gate one batch at a time so the feeder and the views wait at most one batch. A session that is
/// still open is closed as <see cref="NntlCloseKind.RecordingStopped"/>; one that ended without a close block
/// (truncated) is saved without one, as it was.
/// </para>
/// <para>
/// A live store that dropped its oldest ops (past its retention bound) is saved from its first held op, behind one
/// gap block for everything before it, so stream offsets stay those of the session.
/// </para>
/// </remarks>
public static class CaptureExport
{
    // Blocks written per hold of the session's gate.
    private const int BatchBlocks = 256;

    /// <summary>The producer the header of a saved file names when the capture's own header names none.</summary>
    public const string DefaultProducer = "nexnet-inspector";

    /// <summary>Writes <paramref name="session"/> to <paramref name="output"/> (left open).</summary>
    /// <param name="session">The session.</param>
    /// <param name="output">Writable destination.</param>
    /// <param name="cancellationToken">Stops between batches; the output then holds a partial file without its footer.</param>
    /// <returns>What was written.</returns>
    /// <exception cref="IOException">The output failed.</exception>
    /// <exception cref="OperationCanceledException">Canceled.</exception>
    public static CaptureExportResult Write(CaptureSession session, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(output);

        Plan plan;
        lock (session.Gate)
            plan = Plan.Of(session);

        var header = session.Header;
        if (string.IsNullOrEmpty(header.Producer))
            header = header with { Producer = DefaultProducer };

        var start = output.CanSeek ? output.Position : 0;
        using var writer = new NntlWriter(output, header, leaveOpen: true);
        if (session.SchemaBlock is { } schema && !session.Open.SchemaHash.IsEmpty)
            writer.WriteSchema(schema.Hash.Span, schema.Schema.Span);
        writer.WriteSessionOpen(session.Open);

        var state = new Cursor(plan);
        long ops = 0;
        long gaps = 0;
        var ranges = new List<NntlByteRange>();
        foreach (var lost in plan.LostPrefix)
        {
            if (lost.DroppedBytes > 0)
            {
                writer.WriteGap(lost);
                gaps++;
            }
        }

        var done = false;
        while (!done)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (session.Gate)
            {
                for (var n = 0; n < BatchBlocks; n++)
                {
                    var next = state.Next(session);
                    if (next == Pick.None)
                    {
                        done = true;
                        break;
                    }

                    switch (next)
                    {
                        case Pick.Snapshot:
                            var snapshot = session.SnapshotBlock!.Value;
                            writer.WriteSnapshot(snapshot.SessionId, snapshot.Ticks, snapshot.Sections.Span);
                            state.SnapshotWritten = true;
                            break;
                        case Pick.Gap:
                            writer.WriteGap(session.Gaps[state.Gap]);
                            state.Gap++;
                            gaps++;
                            break;
                        default:
                            var direction = next == Pick.Inbound ? TapDirection.Inbound : TapDirection.Outbound;
                            if (WriteOp(session, writer, direction, ref state, ranges))
                                ops++;
                            else
                                gaps++;
                            break;
                    }
                }
            }
        }

        var closed = false;
        if (plan.Close is { } close)
        {
            writer.WriteSessionClose(close);
            closed = true;
        }
        else if (plan.State == CaptureState.Open)
        {
            writer.WriteSessionClose(new NntlSessionClose(session.SessionId, Math.Max(plan.EndTicks, session.Open.Ticks),
                NntlCloseKind.RecordingStopped, default));
            closed = true;
        }

        writer.Close();
        // The writer's position stops before the footer it writes on close.
        return new CaptureExportResult(ops, gaps, output.CanSeek ? output.Position - start : writer.Position, closed);
    }

    // Writes the op at the direction's cursor, or a gap for it when its bytes cannot be had (a recording that no longer
    // reads), so later offsets stay right. Under the gate.
    private static bool WriteOp(CaptureSession session, NntlWriter writer, TapDirection direction, ref Cursor state,
        List<NntlByteRange> ranges)
    {
        var store = session.GetStore(direction);
        ref var at = ref state.At(direction);
        var op = store.Ops[store.IndexAtOrAfter(at)];
        at = op.End;
        if (!store.TryGetSegment(op.Offset, out var bytes) || bytes.Length < op.Length)
        {
            writer.WriteGap(new NntlGap(session.SessionId, direction, op.Ticks, op.Ticks, 1, op.Length));
            return false;
        }

        ranges.Clear();
        var redactions = store.Redactions;
        var lo = 0;
        var hi = redactions.Length;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (redactions[mid].End <= op.Offset)
                lo = mid + 1;
            else
                hi = mid;
        }

        for (var i = lo; i < redactions.Length && redactions[i].Offset < op.End; i++)
        {
            var start = Math.Max(redactions[i].Offset, op.Offset);
            var end = Math.Min(redactions[i].End, op.End);
            ranges.Add(new NntlByteRange((int)(start - op.Offset), (int)(end - start)));
        }

        writer.WriteTransportOp(session.SessionId, direction, op.OpId, op.Ticks, bytes.Span[..op.Length],
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(ranges));
        return true;
    }

    private enum Pick : byte
    {
        None,
        Snapshot,
        Gap,
        Inbound,
        Outbound,
    }

    // What the save covers, fixed when it begins: everything the session held then.
    private sealed class Plan
    {
        public long InboundEnd;
        public long OutboundEnd;
        public long InboundStart;
        public long OutboundStart;
        public int GapCount;
        public bool HasSnapshot;
        public NntlSessionClose? Close;
        public CaptureState State;
        public long EndTicks;
        public NntlGap[] LostPrefix = [];

        public static Plan Of(CaptureSession session)
        {
            var plan = new Plan
            {
                InboundEnd = session.Inbound.EndOffset,
                OutboundEnd = session.Outbound.EndOffset,
                GapCount = session.Gaps.Length,
                HasSnapshot = session.SnapshotBlock is not null,
                Close = session.Close,
                State = session.State,
                EndTicks = session.EndTicks,
            };

            // A live store past its retention bound dropped its oldest ops: everything before its first held op becomes
            // one gap, and the session's own gaps inside that range are folded into it.
            var lost = new List<NntlGap>(2);
            plan.InboundStart = Start(session, TapDirection.Inbound, lost);
            plan.OutboundStart = Start(session, TapDirection.Outbound, lost);
            plan.LostPrefix = [.. lost];
            return plan;
        }

        private static long Start(CaptureSession session, TapDirection direction, List<NntlGap> lost)
        {
            if (session.GetStore(direction) is not LiveOpStore { TrimmedBytes: > 0 } store || store.Ops.IsEmpty)
                return 0;
            var first = store.Ops[0];
            lost.Add(new NntlGap(session.SessionId, direction, first.Ticks, first.Ticks, 0, first.Offset));
            return first.Offset;
        }
    }

    private struct Cursor(Plan plan)
    {
        public long Inbound = plan.InboundStart;
        public long Outbound = plan.OutboundStart;
        public int Gap;
        public bool SnapshotWritten = !plan.HasSnapshot;
        private readonly Plan _plan = plan;

        [System.Diagnostics.CodeAnalysis.UnscopedRef]
        public ref long At(TapDirection direction) => ref direction == TapDirection.Inbound ? ref Inbound : ref Outbound;

        // The block that comes first in capture order among the next op of each direction, the next gap and the
        // snapshot. Under the gate.
        public Pick Next(CaptureSession session)
        {
            var best = Pick.None;
            long bestSource = 0;
            long bestTicks = 0;

            if (!SnapshotWritten)
                Consider(Pick.Snapshot, session.SnapshotSource, session.SnapshotTicks, ref best, ref bestSource, ref bestTicks);

            var marks = session.GapMarks;
            var gaps = session.Gaps;
            while (Gap < _plan.GapCount && Folded(gaps[Gap].Direction, marks[Gap].Offset))
                Gap++;
            if (Gap < _plan.GapCount)
                Consider(Pick.Gap, marks[Gap].Source, gaps[Gap].FromTicks, ref best, ref bestSource, ref bestTicks);

            NextOp(session.Inbound, Inbound, _plan.InboundEnd, Pick.Inbound, ref best, ref bestSource, ref bestTicks);
            NextOp(session.Outbound, Outbound, _plan.OutboundEnd, Pick.Outbound, ref best, ref bestSource, ref bestTicks);
            return best;
        }

        // A gap before the first held op of a trimmed store is inside the lost prefix already written.
        private readonly bool Folded(TapDirection direction, long offset)
            => offset < (direction == TapDirection.Inbound ? _plan.InboundStart : _plan.OutboundStart);

        private static void NextOp(TransportOpStore store, long at, long end, Pick pick, ref Pick best, ref long bestSource,
            ref long bestTicks)
        {
            if (at >= end)
                return;
            var ops = store.Ops;
            var index = store.IndexAtOrAfter(at);
            if (index >= ops.Length || ops[index].Offset >= end)
                return;
            Consider(pick, ops[index].Source, ops[index].Ticks, ref best, ref bestSource, ref bestTicks);
        }

        private static void Consider(Pick pick, long source, long ticks, ref Pick best, ref long bestSource, ref long bestTicks)
        {
            if (best != Pick.None && (source > bestSource || (source == bestSource && ticks >= bestTicks)))
                return;
            best = pick;
            bestSource = source;
            bestTicks = ticks;
        }
    }
}
