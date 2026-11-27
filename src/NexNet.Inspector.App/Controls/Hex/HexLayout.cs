using System.Runtime.InteropServices;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Controls.Hex;

/// <summary>One line of the hex view.</summary>
/// <param name="Kind">Bytes, read rule or frame annotation.</param>
/// <param name="Entry">The shown op whose rows hold the line (index into <see cref="HexLayout"/>'s ops).</param>
/// <param name="Direction">The stream of the row.</param>
/// <param name="Row">Stream offset of the row's first cell (a multiple of 16); the row the annotation stands above.</param>
public readonly record struct HexLine(HexLineKind Kind, int Entry, TapDirection Direction, long Row);

/// <summary>One shown transport op.</summary>
/// <param name="Direction">Read (inbound) or write (outbound).</param>
/// <param name="Offset">Stream offset of its first byte.</param>
/// <param name="Length">Bytes it carried.</param>
/// <param name="OpId">The tap's op id.</param>
/// <param name="Ticks">Stopwatch ticks of the op.</param>
/// <param name="RunStart">True when it does not continue the previous shown op's bytes (first op, direction switch, gap).</param>
public readonly record struct HexOp(TapDirection Direction, long Offset, int Length, long OpId, long Ticks, bool RunStart)
{
    /// <summary>Offset one past its last byte.</summary>
    public long End => Offset + Length;
}

/// <summary>
/// The lines of the hex view over one <see cref="CaptureSession"/>: the session's transport ops (both directions
/// interleaved in op order, or one of them), laid out 16 bytes per row at their stream offsets, with a read rule above
/// each row a new op starts in and a frame annotation above each row a frame starts in. Never materialises text or a
/// line list: it keeps one small entry per op (rows it owns, rule and annotation counts, first line) and generates any
/// window of lines on demand, so a 100 MB recording costs a few bytes per op and a line lookup is a binary search.
/// </summary>
/// <remarks>
/// <para>
/// Ops that follow each other in one direction with contiguous offsets form a run whose rows are shared: a read that
/// ends mid-row and the next one that continues it share that row (the new read's first cell gets the transport
/// border). A row belongs to the first op of the run that touches it. A direction switch or a gap starts a new run on a
/// new row, so a row never mixes directions.
/// </para>
/// <para>
/// <see cref="Update"/> is incremental: only ops and frames that arrived since the last call are added, and only the
/// line numbers after the first op whose count changed are recomputed (the tail, for a growing capture). Call it and
/// every reader under the session's <see cref="CaptureSession.Gate"/>. Not thread-safe; UI thread.
/// </para>
/// </remarks>
public sealed class HexLayout
{
    /// <summary>Bytes per row (PKG: "16 B/row").</summary>
    public const int BytesPerRow = 16;

    private const long RowMask = ~(long)(BytesPerRow - 1);

    private readonly List<int>[] _byOffset = [[], []];
    private readonly long[] _merged = new long[2];
    private readonly int[] _framesSeen = new int[2];
    private readonly (int Owner, long Row)[] _lastFrameRow = [(-1, -1), (-1, -1)];
    private Entry[] _entries = new Entry[256];
    private int _count;
    private int _runOwner = -1;
    private int _dirty = int.MaxValue;
    private long _lineCount;

    /// <summary>The session laid out; null when none.</summary>
    public CaptureSession? Session { get; private set; }

    /// <summary>The directions shown.</summary>
    public HexDirections Directions { get; private set; } = HexDirections.Both;

    /// <summary>The document's scope (D2): only ops whose ticks fall in it are laid out; empty for all.</summary>
    public TimeWindow Scope { get; private set; }

    /// <summary>Lines of every kind.</summary>
    public long LineCount => _lineCount;

    /// <summary>Ops shown.</summary>
    public int OpCount => _count;

    /// <summary>Increments whenever the lines change (data, filter, session).</summary>
    public long Version { get; private set; }

    /// <summary>Raised after <see cref="Update"/> or <see cref="Reset"/> changed the lines.</summary>
    public event Action? Changed;

    /// <summary>The widest stream offset shown, for the offset column's digit count.</summary>
    public long MaxOffset { get; private set; }

    /// <summary>A shown op.</summary>
    /// <param name="entry">Its index, 0 to <see cref="OpCount"/> - 1.</param>
    public HexOp GetOp(int entry)
    {
        ref readonly Entry e = ref _entries[entry];
        return new HexOp(e.Direction, e.Offset, e.Length, e.OpId, e.Ticks, e.RunStart);
    }

    /// <summary>The first line of a shown op's rows (for an op that owns no row, the line after the row it ends in).</summary>
    public long FirstLineOf(int entry) => _entries[entry].FirstLine;

    /// <summary>Clears the layout.</summary>
    public void Reset()
    {
        Session = null;
        Clear();
        Version++;
        Changed?.Invoke();
    }

    /// <summary>
    /// Brings the layout up to date with <paramref name="session"/>: a different session or filter lays out again, the
    /// same one adds what arrived since the last call. Call under the session's gate.
    /// </summary>
    /// <param name="session">The session; null clears.</param>
    /// <param name="directions">The directions to show; <see cref="HexDirections.None"/> is read as both.</param>
    /// <param name="scope">The document's scope: only the lines of ops in it show; empty for every op.</param>
    /// <returns>True when the lines changed.</returns>
    public bool Update(CaptureSession? session, HexDirections directions, TimeWindow scope = default)
    {
        if (directions == HexDirections.None)
            directions = HexDirections.Both;
        if (scope.IsEmpty)
            scope = default;
        bool reset = false;
        if (!ReferenceEquals(session, Session) || directions != Directions || scope != Scope)
        {
            Session = session;
            Directions = directions;
            Scope = scope;
            Clear();
            Version++;
            reset = true;
            if (session is null)
            {
                Changed?.Invoke();
                return true;
            }
        }
        else if (session is null)
        {
            return false;
        }

        long before = _lineCount;
        int opsBefore = _count;
        long versionBefore = Version;
        MergeOps(session);
        CountFrames(session, TapDirection.Inbound);
        CountFrames(session, TapDirection.Outbound);
        bool renumbered = Renumber();
        if (_count != opsBefore || _lineCount != before || renumbered)
            Version++;
        bool result = reset || Version != versionBefore;
        if (result)
            Changed?.Invoke();
        return result;
    }

    /// <summary>Fills <paramref name="destination"/> with consecutive lines from <paramref name="first"/>.</summary>
    /// <returns>Lines written: fewer at the end of the layout.</returns>
    public int Read(long first, Span<HexLine> destination)
    {
        if (first < 0 || first >= _lineCount || destination.IsEmpty)
            return 0;

        int entry = EntryAtLine(first);
        long skip = first - _entries[entry].FirstLine;
        int written = 0;
        for (; entry < _count && written < destination.Length; entry++)
        {
            written += Emit(entry, ref skip, destination.Slice(written));
            skip = 0;
        }

        return written;
    }

    /// <summary>The line of the byte row that shows a stream byte.</summary>
    /// <param name="direction">The stream.</param>
    /// <param name="offset">The byte's stream offset.</param>
    /// <returns>The line, or -1 when the byte is not shown (filtered out, not yet arrived, in a gap).</returns>
    public long FindLine(TapDirection direction, long offset)
    {
        int owner = OwnerOf(direction, offset);
        if (owner < 0)
            return -1;

        long row = offset & RowMask;
        long line = _entries[owner].FirstLine;
        // Walk the owner's lines up to the byte row; an op owns at most a few thousand rows.
        ref readonly Entry e = ref _entries[owner];
        for (int r = 0; r < e.Rows; r++)
        {
            long at = e.FirstRow + ((long)r * BytesPerRow);
            line += LinesBeforeBytes(owner, r, at);
            if (at == row)
                return line;
            line++;
        }

        return -1;
    }

    /// <summary>The first line of the first shown op at or after <paramref name="ticks"/>; -1 when none.</summary>
    public long FindLineAtTicks(long ticks)
    {
        int lo = 0;
        int hi = _count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_entries[mid].Ticks < ticks)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo < _count ? _entries[lo].FirstLine : -1;
    }

    /// <summary>The ticks of the op whose rows hold <paramref name="line"/>.</summary>
    public long TicksAtLine(long line) => _count == 0 ? 0 : _entries[EntryAtLine(Math.Clamp(line, 0, Math.Max(0, _lineCount - 1)))].Ticks;

    /// <summary>The bytes a row shows: from its owner's first byte in it to the end of the run's bytes in it.</summary>
    /// <param name="line">A line of the row (any kind).</param>
    /// <param name="start">First shown offset.</param>
    /// <param name="end">One past the last shown offset.</param>
    public void GetRowSpan(in HexLine line, out long start, out long end)
    {
        ref readonly Entry e = ref _entries[line.Entry];
        start = Math.Max(line.Row, e.Offset);
        end = Math.Min(line.Row + BytesPerRow, CoverageEnd(line.Entry, line.Row));
    }

    /// <summary>
    /// The shown ops after the row's owner that start inside the row (or the owner itself when its own start is ruled):
    /// the ops whose first cell gets the read border, and whose ids the rule above the row names.
    /// </summary>
    /// <param name="line">A line of the row.</param>
    /// <param name="first">The first such op's index; -1 when none.</param>
    /// <param name="count">How many follow it consecutively.</param>
    public void GetReadStarts(in HexLine line, out int first, out int count)
    {
        first = -1;
        count = 0;
        int owner = line.Entry;
        ref readonly Entry e = ref _entries[owner];
        long rowEnd = line.Row + BytesPerRow;
        if (e.StartRule && (e.Offset & RowMask) == line.Row)
        {
            first = owner;
            count = 1;
        }

        for (int j = owner + 1; j < _count && !_entries[j].RunStart && _entries[j].Offset < rowEnd; j++)
        {
            if (_entries[j].Offset < line.Row)
                continue;
            if (first < 0)
                first = j;
            count++;
        }
    }

    /// <summary>The frames (of the row's direction) that start in the bytes a row shows.</summary>
    /// <param name="line">A line of the row.</param>
    /// <param name="first">Index of the first such frame in the assembler's frames of that direction.</param>
    /// <returns>How many.</returns>
    public int GetFrameStarts(in HexLine line, out int first)
    {
        first = -1;
        if (Session is not { } session)
            return 0;

        GetRowSpan(line, out long start, out long end);
        ReadOnlySpan<FrameRecord> frames = session.Assembler.GetFrames(line.Direction);
        int seen = Math.Min(frames.Length, _framesSeen[(int)line.Direction]);
        int at = LowerBound(frames.Slice(0, seen), start);
        int n = 0;
        while (at + n < seen && frames[at + n].Offset < end)
            n++;
        if (n > 0)
            first = at;
        return n;
    }

    /// <summary>The index of the first frame (of the given direction's frames) whose offset is at or after <paramref name="offset"/>.</summary>
    public static int LowerBound(ReadOnlySpan<FrameRecord> frames, long offset)
    {
        int lo = 0;
        int hi = frames.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (frames[mid].Offset < offset)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    /// <summary>The frame that covers a stream byte, by binary search.</summary>
    /// <returns>Its index in <paramref name="frames"/>, or -1.</returns>
    public static int FrameAt(ReadOnlySpan<FrameRecord> frames, long offset)
    {
        int at = LowerBound(frames, offset + 1) - 1;
        if (at < 0)
            return -1;
        ref readonly FrameRecord frame = ref frames[at];
        return offset < frame.Offset + Math.Max(1, frame.Length) ? at : -1;
    }

    private void Clear()
    {
        _count = 0;
        _runOwner = -1;
        _dirty = int.MaxValue;
        _lineCount = 0;
        MaxOffset = 0;
        _merged[0] = _merged[1] = 0;
        _framesSeen[0] = _framesSeen[1] = 0;
        _lastFrameRow[0] = _lastFrameRow[1] = (-1, -1);
        _byOffset[0].Clear();
        _byOffset[1].Clear();
    }

    private bool Shows(TapDirection direction) =>
        (Directions & (direction == TapDirection.Inbound ? HexDirections.Inbound : HexDirections.Outbound)) != 0;

    // New ops of the shown directions, merged by time (ties: inbound first, as the peer's bytes are read before the
    // answer is written).
    private void MergeOps(CaptureSession session)
    {
        ReadOnlySpan<StreamOp> inbound = Shows(TapDirection.Inbound) ? Pending(session.Inbound.Ops, 0) : default;
        ReadOnlySpan<StreamOp> outbound = Shows(TapDirection.Outbound) ? Pending(session.Outbound.Ops, 1) : default;
        int i = 0;
        int o = 0;
        while (i < inbound.Length || o < outbound.Length)
        {
            bool takeInbound = o >= outbound.Length || (i < inbound.Length && inbound[i].Ticks <= outbound[o].Ticks);
            if (takeInbound)
                Append(TapDirection.Inbound, inbound[i++]);
            else
                Append(TapDirection.Outbound, outbound[o++]);
        }
    }

    private ReadOnlySpan<StreamOp> Pending(ReadOnlySpan<StreamOp> ops, int direction)
    {
        long from = _merged[direction];
        int lo = 0;
        int hi = ops.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (ops[mid].Offset < from)
                lo = mid + 1;
            else
                hi = mid;
        }

        return ops.Slice(lo);
    }

    private void Append(TapDirection direction, in StreamOp op)
    {
        if (op.Length <= 0)
            return;

        _merged[(int)direction] = op.End;
        // Outside the scope: not shown, and the next shown op of this direction starts a new run (its offset no longer
        // continues the last shown one's).
        if (!Scope.IsEmpty && !Scope.Contains(op.Ticks))
            return;
        if (_count == _entries.Length)
            Array.Resize(ref _entries, _count * 2);

        int index = _count;
        long lastRow = (op.End - 1) & RowMask;
        bool continues = index > 0 && _entries[index - 1].Direction == direction && _entries[index - 1].End == op.Offset
                         && _runOwner >= 0;
        Entry entry = new()
        {
            Direction = direction,
            Offset = op.Offset,
            Length = op.Length,
            OpId = op.OpId,
            Ticks = op.Ticks,
            RunStart = !continues,
        };

        if (continues && (op.Offset & (BytesPerRow - 1)) != 0)
        {
            // The op starts inside the run owner's last row: that row gains the rule, and this op owns only the rows
            // after it.
            ref Entry owner = ref _entries[_runOwner];
            if (!owner.TailRule)
            {
                owner.TailRule = true;
                Touch(_runOwner);
            }

            entry.FirstRow = (op.Offset & RowMask) + BytesPerRow;
            entry.Rows = lastRow >= entry.FirstRow ? (int)(((lastRow - entry.FirstRow) / BytesPerRow) + 1) : 0;
        }
        else
        {
            entry.FirstRow = op.Offset & RowMask;
            entry.Rows = (int)(((lastRow - entry.FirstRow) / BytesPerRow) + 1);
            entry.StartRule = index > 0;
        }

        _entries[index] = entry;
        _count++;
        _byOffset[(int)direction].Add(index);
        if (entry.Rows > 0)
            _runOwner = index;
        if (op.End > MaxOffset)
            MaxOffset = op.End;
        Touch(index);
    }

    private void CountFrames(CaptureSession session, TapDirection direction)
    {
        int d = (int)direction;
        if (!Shows(direction))
            return;

        ReadOnlySpan<FrameRecord> frames = session.Assembler.GetFrames(direction);
        for (int f = _framesSeen[d]; f < frames.Length; f++)
        {
            long offset = frames[f].Offset;
            int owner = OwnerOf(direction, offset);
            if (owner < 0)
                continue;

            (int Owner, long Row) key = (owner, offset & RowMask);
            if (key == _lastFrameRow[d])
                continue;
            _lastFrameRow[d] = key;
            _entries[owner].FrameRows++;
            Touch(owner);
        }

        _framesSeen[d] = frames.Length;
    }

    // The op whose rows show the byte: the op holding it, or the run's earlier op that owns the row it starts in.
    private int OwnerOf(TapDirection direction, long offset)
    {
        List<int> list = _byOffset[(int)direction];
        int lo = 0;
        int hi = list.Count - 1;
        if (hi < 0)
            return -1;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo + 1) >> 1);
            if (_entries[list[mid]].Offset <= offset)
                lo = mid;
            else
                hi = mid - 1;
        }

        int k = list[lo];
        if (offset < _entries[k].Offset || offset >= _entries[k].End)
            return -1;

        long row = offset & RowMask;
        while (k > 0 && (_entries[k].Rows == 0 || row < _entries[k].FirstRow) && !_entries[k].RunStart)
            k--;
        return row >= _entries[k].FirstRow && _entries[k].Rows > 0 ? k : -1;
    }

    private void Touch(int index)
    {
        if (index < _dirty)
            _dirty = index;
    }

    private bool Renumber()
    {
        if (_dirty == int.MaxValue)
            return false;

        long line = _dirty == 0 ? 0 : _entries[_dirty - 1].FirstLine + _entries[_dirty - 1].LineCount;
        for (int i = _dirty; i < _count; i++)
        {
            _entries[i].FirstLine = line;
            line += _entries[i].LineCount;
        }

        _lineCount = line;
        _dirty = int.MaxValue;
        return true;
    }

    private int EntryAtLine(long line)
    {
        int lo = 0;
        int hi = _count - 1;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo + 1) >> 1);
            if (_entries[mid].FirstLine <= line)
                lo = mid;
            else
                hi = mid - 1;
        }

        // Ops that own no row share the next op's first line; the line belongs to the earlier op that has lines.
        while (lo > 0 && _entries[lo].LineCount == 0)
            lo--;
        return lo;
    }

    private long CoverageEnd(int owner, long row)
    {
        long end = _entries[owner].End;
        long rowEnd = row + BytesPerRow;
        for (int j = owner + 1; j < _count && !_entries[j].RunStart && _entries[j].Offset < rowEnd; j++)
            end = _entries[j].End;
        return end;
    }

    // Lines a row has before its byte line: its rule and its frame annotation.
    private int LinesBeforeBytes(int owner, int r, long row)
    {
        ref readonly Entry e = ref _entries[owner];
        int lines = 0;
        if ((r == 0 && e.StartRule) || (r == e.Rows - 1 && e.TailRule))
            lines++;
        if (GetFrameStarts(new HexLine(HexLineKind.Bytes, owner, e.Direction, row), out _) > 0)
            lines++;
        return lines;
    }

    private int Emit(int owner, ref long skip, Span<HexLine> destination)
    {
        ref readonly Entry e = ref _entries[owner];
        int written = 0;
        for (int r = 0; r < e.Rows && written < destination.Length; r++)
        {
            long row = e.FirstRow + ((long)r * BytesPerRow);
            int before = LinesBeforeBytes(owner, r, row);
            if (skip > before)
            {
                skip -= before + 1;
                continue;
            }

            bool rule = (r == 0 && e.StartRule) || (r == e.Rows - 1 && e.TailRule);
            bool frames = before - (rule ? 1 : 0) > 0;
            if (rule)
                Put(HexLineKind.ReadRule, owner, row, ref skip, destination, ref written);
            if (frames)
                Put(HexLineKind.Frames, owner, row, ref skip, destination, ref written);
            Put(HexLineKind.Bytes, owner, row, ref skip, destination, ref written);
        }

        return written;
    }

    private void Put(HexLineKind kind, int owner, long row, ref long skip, Span<HexLine> destination, ref int written)
    {
        if (skip > 0)
        {
            skip--;
            return;
        }

        if (written < destination.Length)
            destination[written++] = new HexLine(kind, owner, _entries[owner].Direction, row);
    }

    private struct Entry
    {
        public TapDirection Direction;
        public bool RunStart;
        public bool StartRule;
        public bool TailRule;
        public long Offset;
        public int Length;
        public long OpId;
        public long Ticks;
        public long FirstRow;
        public int Rows;
        public int FrameRows;
        public long FirstLine;

        public readonly long End => Offset + Length;

        public readonly int LineCount =>
            Rows == 0 ? 0 : Rows + FrameRows + (StartRule ? 1 : 0) + (TailRule && !(StartRule && Rows == 1) ? 1 : 0);
    }
}
