using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Compare;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Controls.Hex;

/// <summary>One line of the A ⇄ B hex view: a 16-byte row of one document's frame.</summary>
/// <param name="Side">The document.</param>
/// <param name="Row">The row within the frame (offset = row × 16).</param>
public readonly record struct CompareHexLine(CompareSide Side, int Row);

/// <summary>
/// The bytes of the A ⇄ B hex view (PKG compare, "Hex · A ⇄ B"): for one compared value of an entity, the frame that
/// carries the selected field (or the value's first byte) in each document, laid out 16 bytes per row from the frame's
/// first header byte, A's and B's rows interleaved (A 0000, B 0000, A 0010…). Cells keep the hex view's header fills
/// (type, length, post-header; <see cref="HexRowBuilder.RoleOf"/>), bodies stay unfilled so the comparison reads, the
/// selected field's bytes are ringed on both sides, and a byte that differs from the other document's byte at the same
/// position is marked <see cref="HexCellMarks.Differs"/>.
/// </summary>
/// <remarks>
/// Both captures carry the same wire bytes for an entity they agree on (a client and a server view of one connection
/// included), so frame positions line up byte for byte and a position-wise comparison shows exactly what changed.
/// Each frame is copied out once per <see cref="Load"/> (at most a frame's 64 KB per side); rows are built on demand
/// into the caller's <see cref="HexRow"/>. UI thread only.
/// </remarks>
public sealed class CompareHexModel
{
    /// <summary>Fewest offset digits (PKG compare: "0000").</summary>
    public const int MinDigits = 4;

    /// <summary>Frames longer than this show only their start at first: the pane header then says "body start".</summary>
    public const int PreviewBytes = 4 * HexLayout.BytesPerRow;

    private readonly Side[] _sides = [new(), new()];
    private readonly List<TraceFragment> _fragments = [];
    private CompareHexLine[] _lines = [];
    private int _lineCount;

    /// <summary>Lines (A's and B's rows).</summary>
    public int LineCount => _lineCount;

    /// <summary>True when either document has bytes to show.</summary>
    public bool HasBytes => _lineCount > 0;

    /// <summary>The offset column's digit count.</summary>
    public int Digits { get; private set; } = MinDigits;

    /// <summary>The field diff shown; null when none.</summary>
    public EntityFieldDiff? Diff { get; private set; }

    /// <summary>The compared value shown (index into <see cref="EntityFieldDiff.Parts"/>); -1 when none.</summary>
    public int Part { get; private set; } = -1;

    /// <summary>The field ringed (index into <see cref="EntityFieldDiff.Rows"/>); -1 when none.</summary>
    public int Row { get; private set; } = -1;

    /// <summary>Positions where the two frames differ (a byte only one side has counts).</summary>
    public int DifferingBytes { get; private set; }

    /// <summary>Increments whenever the content changes.</summary>
    public long Version { get; private set; }

    /// <summary>Raised after <see cref="Load"/> or <see cref="Clear"/>.</summary>
    public event Action? Changed;

    /// <summary>True when the document holds a frame for the value.</summary>
    public bool HasSide(CompareSide side) => _sides[(int)side].Length > 0;

    /// <summary>The frame shown for a document; false when it has none.</summary>
    public bool TryGetFrame(CompareSide side, out FrameRecord frame)
    {
        Side s = _sides[(int)side];
        frame = s.Frame;
        return s.Length > 0;
    }

    /// <summary>The bytes shown for a document (the frame, header first).</summary>
    public ReadOnlySpan<byte> GetBytes(CompareSide side) => _sides[(int)side].Bytes.AsSpan(0, _sides[(int)side].Length);

    /// <summary>The ringed range of a document, frame positions [start, end); empty when nothing is ringed there.</summary>
    public (int Start, int End) GetSelected(CompareSide side) => (_sides[(int)side].SelectedStart, _sides[(int)side].SelectedEnd);

    /// <summary>A line.</summary>
    public CompareHexLine GetLine(int index) =>
        (uint)index < (uint)_lineCount ? _lines[index] : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>The line of a document's row; -1 when it has no such row.</summary>
    public int LineOf(CompareSide side, int row)
    {
        int rowsA = Rows(CompareSide.A);
        int rowsB = Rows(CompareSide.B);
        int rows = side == CompareSide.A ? rowsA : rowsB;
        if ((uint)row >= (uint)rows)
            return -1;

        // Rows below both counts alternate A, B; past the shorter side only the longer one's rows remain.
        int shared = Math.Min(rowsA, rowsB);
        if (row < shared)
            return (2 * row) + (side == CompareSide.A ? 0 : 1);
        return (2 * shared) + (row - shared);
    }

    /// <summary>The header's subtitle: "header + body" for a frame shown whole at first, "header + body start" otherwise.</summary>
    public string Subtitle => Math.Max(_sides[0].Length, _sides[1].Length) > PreviewBytes ? "header + body start" : "header + body";

    /// <summary>Clears the view.</summary>
    public void Clear()
    {
        Diff = null;
        Part = -1;
        Row = -1;
        _sides[0].Reset();
        _sides[1].Reset();
        _lineCount = 0;
        DifferingBytes = 0;
        Digits = MinDigits;
        Version++;
        Changed?.Invoke();
    }

    /// <summary>
    /// Shows one compared value of an entity: the part of <paramref name="row"/> when one is given, else
    /// <paramref name="part"/>, else the first part that differs (or the first part).
    /// </summary>
    /// <param name="compare">The compare the diff came from.</param>
    /// <param name="diff">The entity's field diff; null clears.</param>
    /// <param name="part">The part to show when no row is given; -1 picks one.</param>
    /// <param name="row">The field to ring (index into <see cref="EntityFieldDiff.Rows"/>); -1 for none.</param>
    public void Load(CompareSession compare, EntityFieldDiff? diff, int part = -1, int row = -1)
    {
        ArgumentNullException.ThrowIfNull(compare);
        if (diff is null || diff.Parts.IsEmpty)
        {
            Clear();
            return;
        }

        if ((uint)row < (uint)diff.Rows.Length)
            part = diff.Rows[row].Part;
        else
            row = -1;
        if ((uint)part >= (uint)diff.Parts.Length)
            part = FirstDiffering(diff);

        Diff = diff;
        Part = part;
        Row = row;
        FieldDiffPart value = diff.Parts[part];
        Core.Compare.FieldDiff? field = row >= 0 ? diff.Rows[row] : null;
        Fill(_sides[0], compare.A, value.A, field?.RangeA ?? PayloadRange.None, field?.RangeB ?? PayloadRange.None, value.B);
        Fill(_sides[1], compare.B, value.B, field?.RangeB ?? PayloadRange.None, field?.RangeA ?? PayloadRange.None, value.A);
        Compare();
        Lay();
        Version++;
        Changed?.Invoke();
    }

    /// <summary>Fills <paramref name="row"/> for a line.</summary>
    public void BuildRow(int line, ref HexRow row)
    {
        row = default;
        if ((uint)line >= (uint)_lineCount)
            return;

        CompareHexLine at = _lines[line];
        Side side = _sides[(int)at.Side];
        Side other = _sides[at.Side == CompareSide.A ? 1 : 0];
        int start = at.Row * HexLayout.BytesPerRow;
        row.Direction = side.Frame.Direction;
        row.Row = start;
        row.First = 0;
        row.Count = Math.Min(HexLayout.BytesPerRow, side.Length - start);
        Span<byte> bytes = row.Bytes;
        Span<HexCellRole> roles = row.Roles;
        Span<HexCellMarks> marks = row.Marks;
        side.Bytes.AsSpan(start, row.Count).CopyTo(bytes);
        for (int cell = 0; cell < row.Count; cell++)
        {
            int position = start + cell;
            HexCellRole role = HexRowBuilder.RoleOf(side.Frame, position);
            // Bodies stay unfilled: the comparison, not the framing, is what the pane is read for.
            roles[cell] = role == HexCellRole.Body ? HexCellRole.None : role;
            HexCellMarks mark = side.Available[position] ? HexCellMarks.None : HexCellMarks.Unavailable;
            if (position >= side.SelectedStart && position < side.SelectedEnd)
                mark |= HexCellMarks.Selected;
            if (other.Length > 0 && (position >= other.Length || other.Bytes[position] != side.Bytes[position]))
                mark |= HexCellMarks.Differs;
            marks[cell] = mark;
        }
    }

    /// <summary>
    /// The field at a byte of a document's frame: the deepest field of the shown value whose bytes hold it, as a row of
    /// <see cref="EntityFieldDiff.Rows"/> (clicking a byte selects its field in the table).
    /// </summary>
    /// <returns>The row, or -1 over a header byte or a byte of no field.</returns>
    public int FindRow(CompareSide side, int position)
    {
        Side s = _sides[(int)side];
        if (Diff is not { } diff || (uint)position >= (uint)s.Length || s.Value is not { } value)
            return -1;

        long offset = s.MessageOffsetAt(position);
        if (offset < 0)
            return -1;
        int field = value.FindField((int)(offset - value.Range.Offset));
        if (field < 0)
            return -1;

        ReadOnlySpan<Core.Compare.FieldDiff> rows = diff.GetRows(Part);
        int first = diff.Parts[Part].FirstRow;
        for (int i = 0; i < rows.Length; i++)
        {
            if ((side == CompareSide.A ? rows[i].FieldA : rows[i].FieldB) == field)
                return first + i;
        }

        return -1;
    }

    private static int FirstDiffering(EntityFieldDiff diff)
    {
        ReadOnlySpan<FieldDiffPart> parts = diff.Parts;
        for (int p = 0; p < parts.Length; p++)
        {
            if (parts[p].Differing > 0)
                return p;
        }

        return 0;
    }

    private int Rows(CompareSide side) => (_sides[(int)side].Length + HexLayout.BytesPerRow - 1) / HexLayout.BytesPerRow;

    // Picks the frame of a document that carries the field (or, for a field only the other document has, the same
    // message position; else the value's first byte) and copies it out.
    private void Fill(Side side, CaptureSession session, DecodedValue? value, PayloadRange own, PayloadRange other, DecodedValue? otherValue)
    {
        side.Reset();
        side.Value = value;
        if (value is null || value.Range.IsNone)
            return;

        long target = value.Range.Offset;
        if (!own.IsNone)
            target = own.Offset;
        else if (!other.IsNone && otherValue is not null)
            target = value.Range.Offset + Math.Max(0, other.Offset - otherValue.Range.Offset);

        lock (session.Gate)
        {
            MessageAssembler assembler = session.Assembler;
            _fragments.Clear();
            assembler.GetFragments(value.Range.Message, _fragments);
            int pick = -1;
            for (int i = 0; i < _fragments.Count; i++)
            {
                TraceFragment f = _fragments[i];
                if (f.Length <= 0)
                    continue;
                if (pick < 0)
                    pick = i;
                if (target >= f.MessageOffset && target < f.MessageOffset + f.Length)
                {
                    pick = i;
                    break;
                }
            }

            if (pick < 0 || !assembler.TryGetFrame(_fragments[pick].Direction, _fragments[pick].Seq, out FrameRecord frame) || frame.Length <= 0)
                return;

            side.Frame = frame;
            side.Length = frame.Length;
            if (side.Bytes.Length < frame.Length)
            {
                side.Bytes = new byte[frame.Length];
                side.Available = new bool[frame.Length];
            }

            TransportOpStore store = session.GetStore(frame.Direction);
            Span<byte> bytes = side.Bytes.AsSpan(0, frame.Length);
            if (store.TryCopyTo(frame.Offset, bytes))
            {
                side.Available.AsSpan(0, frame.Length).Fill(true);
            }
            else
            {
                // Part of the frame is gone (a live capture trimmed it): show what is still there.
                for (int i = 0; i < frame.Length; i++)
                {
                    bool held = store.TryGetSegment(frame.Offset + i, out ReadOnlyMemory<byte> segment) && !segment.IsEmpty;
                    bytes[i] = held ? segment.Span[0] : (byte)0;
                    side.Available[i] = held;
                }
            }

            // The fragments inside this frame map message offsets to frame positions (a frame carries one fragment of a
            // message, but a pipe run's frame may carry none of a neighbour's).
            for (int i = 0; i < _fragments.Count; i++)
            {
                TraceFragment f = _fragments[i];
                if (f.Direction == frame.Direction && f.Seq == frame.Seq && f.Length > 0)
                    side.Fragments.Add(f);
            }
        }

        if (!own.IsNone)
        {
            foreach (TraceFragment f in side.Fragments)
            {
                long from = Math.Max(own.Offset, f.MessageOffset);
                long to = Math.Min(own.Offset + Math.Max(0, own.Length), f.MessageOffset + f.Length);
                if (to <= from)
                    continue;
                long position = f.StreamOffset - side.Frame.Offset + (from - f.MessageOffset);
                side.SelectedStart = (int)Math.Clamp(position, 0, side.Length);
                side.SelectedEnd = (int)Math.Clamp(position + (to - from), 0, side.Length);
                break;
            }
        }
    }

    private void Compare()
    {
        Side a = _sides[0];
        Side b = _sides[1];
        int count = 0;
        if (a.Length > 0 && b.Length > 0)
        {
            int shared = Math.Min(a.Length, b.Length);
            ReadOnlySpan<byte> x = a.Bytes.AsSpan(0, shared);
            ReadOnlySpan<byte> y = b.Bytes.AsSpan(0, shared);
            for (int i = 0; i < shared; i++)
            {
                if (x[i] != y[i])
                    count++;
            }

            count += Math.Abs(a.Length - b.Length);
        }

        DifferingBytes = count;
    }

    private void Lay()
    {
        int rowsA = Rows(CompareSide.A);
        int rowsB = Rows(CompareSide.B);
        _lineCount = rowsA + rowsB;
        if (_lines.Length < _lineCount)
            _lines = new CompareHexLine[Math.Max(_lineCount, _lines.Length * 2)];
        int line = 0;
        for (int r = 0; r < Math.Max(rowsA, rowsB); r++)
        {
            if (r < rowsA)
                _lines[line++] = new CompareHexLine(CompareSide.A, r);
            if (r < rowsB)
                _lines[line++] = new CompareHexLine(CompareSide.B, r);
        }

        int longest = Math.Max(_sides[0].Length, _sides[1].Length);
        int digits = 1;
        for (int v = Math.Max(0, longest - 1); v >= 16; v >>= 4)
            digits++;
        Digits = Math.Max(MinDigits, digits);
    }

    private sealed class Side
    {
        public byte[] Bytes = [];
        public bool[] Available = [];
        public int Length;
        public FrameRecord Frame;
        public DecodedValue? Value;
        public readonly List<TraceFragment> Fragments = [];
        public int SelectedStart;
        public int SelectedEnd;

        public void Reset()
        {
            Length = 0;
            Frame = default;
            Value = null;
            Fragments.Clear();
            SelectedStart = SelectedEnd = 0;
        }

        // The message offset of a frame position; -1 for a header byte or a byte of another message.
        public long MessageOffsetAt(int position)
        {
            long stream = Frame.Offset + position;
            foreach (TraceFragment f in Fragments)
            {
                if (stream >= f.StreamOffset && stream < f.StreamOffset + f.Length)
                    return f.MessageOffset + (stream - f.StreamOffset);
            }

            return -1;
        }
    }
}
