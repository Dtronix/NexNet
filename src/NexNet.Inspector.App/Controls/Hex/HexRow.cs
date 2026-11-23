using System.Runtime.CompilerServices;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Messages;

namespace NexNet.Inspector.App.Controls.Hex;

/// <summary>A byte range of one direction's stream: [<see cref="Start"/>, <see cref="End"/>).</summary>
/// <param name="Direction">The stream.</param>
/// <param name="Start">First byte.</param>
/// <param name="End">One past the last byte.</param>
public readonly record struct HexRange(TapDirection Direction, long Start, long End)
{
    /// <summary>True when the range holds <paramref name="offset"/> of <paramref name="direction"/>.</summary>
    public bool Contains(TapDirection direction, long offset) => direction == Direction && offset >= Start && offset < End;
}

/// <summary>Sixteen bytes, inline.</summary>
[InlineArray(HexLayout.BytesPerRow)]
public struct HexBytes16
{
    private byte _element;
}

/// <summary>Sixteen cell roles, inline.</summary>
[InlineArray(HexLayout.BytesPerRow)]
public struct HexRoles16
{
    private HexCellRole _element;
}

/// <summary>Sixteen cell marks, inline.</summary>
[InlineArray(HexLayout.BytesPerRow)]
public struct HexMarks16
{
    private HexCellMarks _element;
}

/// <summary>
/// One byte row as drawn: its bytes, the role each cell is filled by and the marks over it. A value type with inline
/// arrays, so a window of rows is one array that is reused across frames and nothing is allocated per byte.
/// </summary>
public struct HexRow
{
    /// <summary>The stream.</summary>
    public TapDirection Direction;

    /// <summary>Stream offset of cell 0 (a multiple of 16).</summary>
    public long Row;

    /// <summary>The first shown cell; cells before it are blank (another op's or another run's bytes).</summary>
    public int First;

    /// <summary>Shown cells from <see cref="First"/>.</summary>
    public int Count;

    /// <summary>The bytes of the shown cells.</summary>
    public HexBytes16 Bytes;

    /// <summary>The fill of each cell.</summary>
    public HexRoles16 Roles;

    /// <summary>The marks of each cell.</summary>
    public HexMarks16 Marks;

    /// <summary>True when cell <paramref name="cell"/> shows a byte.</summary>
    public readonly bool Shows(int cell) => cell >= First && cell < First + Count;
}

/// <summary>
/// What the selection and the hovered field light up in the hex view: the selected frames (their headers keep their
/// field fills, their bodies take body-selected, every other frame dims to other-frame), the selected bytes (accent
/// ring) and the hovered bytes (inbound ring). Built from the <see cref="Services.SyncBus"/> by <see cref="HexLinker"/>.
/// </summary>
public sealed class HexHighlight
{
    /// <summary>Frames of the selection.</summary>
    public List<FrameKey> Frames { get; } = [];

    /// <summary>Bytes ringed as selected.</summary>
    public List<HexRange> Selected { get; } = [];

    /// <summary>Bytes ringed as hovered.</summary>
    public List<HexRange> Hovered { get; } = [];

    /// <summary>True when something is selected: unselected frames then dim.</summary>
    public bool HasSelection => Frames.Count > 0;

    /// <summary>Clears every part.</summary>
    public void Clear()
    {
        Frames.Clear();
        Selected.Clear();
        Hovered.Clear();
    }

    /// <summary>True when the frame is part of the selection.</summary>
    public bool IsSelected(TapDirection direction, long seq)
    {
        foreach (FrameKey frame in Frames)
        {
            if (frame.Seq == seq && frame.Direction == direction)
                return true;
        }

        return false;
    }
}

/// <summary>Builds <see cref="HexRow"/>s from a layout's byte lines (PKG hex-view cell fills, redaction, read borders).</summary>
public static class HexRowBuilder
{
    /// <summary>Fills <paramref name="row"/> for a byte line. Call under the session's gate.</summary>
    /// <param name="layout">The layout the line came from.</param>
    /// <param name="line">A <see cref="HexLineKind.Bytes"/> line.</param>
    /// <param name="highlight">The selection and hover; null for none.</param>
    /// <param name="row">Receives the row.</param>
    public static void Build(HexLayout layout, in HexLine line, HexHighlight? highlight, ref HexRow row)
    {
        row = default;
        row.Direction = line.Direction;
        row.Row = line.Row;
        if (layout.Session is not { } session)
            return;

        layout.GetRowSpan(line, out long start, out long end);
        row.First = (int)(start - line.Row);
        row.Count = (int)Math.Max(0, end - start);
        if (row.Count == 0)
            return;

        TransportOpStore store = session.GetStore(line.Direction);
        Span<byte> bytes = row.Bytes;
        Span<HexCellMarks> marks = row.Marks;
        if (!store.TryCopyTo(start, bytes.Slice(row.First, row.Count)))
        {
            // Part of the row is gone (trimmed, or the recording no longer reads): copy what is there.
            for (int cell = row.First; cell < row.First + row.Count; cell++)
            {
                if (store.TryGetSegment(line.Row + cell, out ReadOnlyMemory<byte> segment) && !segment.IsEmpty)
                    bytes[cell] = segment.Span[0];
                else
                    marks[cell] |= HexCellMarks.Unavailable;
            }
        }

        FillRoles(session, line.Direction, start, end, highlight, ref row);
        MarkRedactions(store.Redactions, start, end, ref row);

        layout.GetReadStarts(line, out int first, out int count);
        for (int j = first; j >= 0 && j < first + count; j++)
        {
            long at = layout.GetOp(j).Offset - line.Row;
            if (at >= 0 && at < HexLayout.BytesPerRow)
                marks[(int)at] |= HexCellMarks.ReadStart;
        }

        if (highlight is not null)
        {
            Ring(highlight.Selected, HexCellMarks.Selected, ref row);
            Ring(highlight.Hovered, HexCellMarks.Hovered, ref row);
        }
    }

    /// <summary>The role of the byte at <paramref name="position"/> within a frame, before selection dims it.</summary>
    public static HexCellRole RoleOf(in FrameRecord frame, long position)
    {
        if (frame.Kind == FrameKind.Preamble || frame.Type == MessageType.Ping)
            return HexCellRole.Preamble;
        if (frame.Kind == FrameKind.Malformed)
            return HexCellRole.OtherFrame;
        if (position == 0)
            return HexCellRole.Type;
        if (position < frame.HeaderLength)
            return HexCellRole.Length;
        if (position < frame.HeaderLength + frame.PostHeaderLength)
            return HexCellRole.PostHeader;
        return HexCellRole.Body;
    }

    private static void FillRoles(CaptureSession session, TapDirection direction, long start, long end, HexHighlight? highlight,
        ref HexRow row)
    {
        ReadOnlySpan<FrameRecord> frames = session.Assembler.GetFrames(direction);
        int f = HexLayout.LowerBound(frames, start + 1) - 1;
        if (f < 0)
            f = 0;
        bool dims = highlight is { HasSelection: true };
        Span<HexCellRole> roles = row.Roles;
        for (; f < frames.Length && frames[f].Offset < end; f++)
        {
            ref readonly FrameRecord frame = ref frames[f];
            long frameEnd = frame.Offset + frame.Length;
            if (frameEnd <= start)
                continue;

            bool selected = dims && highlight!.IsSelected(direction, frame.Seq);
            long from = Math.Max(start, frame.Offset);
            long to = Math.Min(end, frameEnd);
            for (long at = from; at < to; at++)
            {
                HexCellRole role = RoleOf(frame, at - frame.Offset);
                if (dims && role != HexCellRole.Preamble)
                {
                    if (!selected)
                        role = HexCellRole.OtherFrame;
                    else if (role == HexCellRole.Body)
                        role = HexCellRole.BodySelected;
                }

                roles[(int)(at - row.Row)] = role;
            }
        }
    }

    private static void MarkRedactions(ReadOnlySpan<StreamRange> redactions, long start, long end, ref HexRow row)
    {
        if (redactions.IsEmpty)
            return;

        // Ranges are in stream order; find the first that ends after the row starts.
        int lo = 0;
        int hi = redactions.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (redactions[mid].End <= start)
                lo = mid + 1;
            else
                hi = mid;
        }

        Span<HexCellMarks> marks = row.Marks;
        for (int r = lo; r < redactions.Length && redactions[r].Offset < end; r++)
        {
            long from = Math.Max(start, redactions[r].Offset);
            long to = Math.Min(end, redactions[r].End);
            for (long at = from; at < to; at++)
                marks[(int)(at - row.Row)] |= HexCellMarks.Redacted;
        }
    }

    private static void Ring(List<HexRange> ranges, HexCellMarks mark, ref HexRow row)
    {
        Span<HexCellMarks> marks = row.Marks;
        long start = row.Row + row.First;
        long end = start + row.Count;
        foreach (HexRange range in ranges)
        {
            if (range.Direction != row.Direction || range.End <= start || range.Start >= end)
                continue;
            long from = Math.Max(start, range.Start);
            long to = Math.Min(end, range.End);
            for (long at = from; at < to; at++)
                marks[(int)(at - row.Row)] |= mark;
        }
    }
}
