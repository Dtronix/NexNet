using System.Diagnostics;
using System.Runtime.InteropServices;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Controls.Ladder;

/// <summary>
/// What the ladder draws for one document: a row per frame in the order the parsers completed them, the endpoint names
/// of its two columns, the clock, and the few strings rows share (method names). Filled by <see cref="LadderProjection"/>
/// from a capture session, or directly (tests, the gallery). UI thread only; <see cref="NotifyChanged"/> tells the view.
/// </summary>
public sealed class LadderModel
{
    private readonly List<LadderRow> _rows = [];
    private readonly List<int>[] _byDirection = [[], []];
    private readonly List<string> _labels = [];
    private readonly Dictionary<string, int> _labelIds = new(StringComparer.Ordinal);
    private readonly List<int> _chunkTotals = [];
    private readonly List<MessageRef> _messages = [];

    /// <summary>Raised by <see cref="NotifyChanged"/>.</summary>
    public event Action? Changed;

    /// <summary>Stopwatch ticks per second of the capture's clock.</summary>
    public long Frequency { get; set; } = Stopwatch.Frequency;

    /// <summary>The tick the Time column counts milliseconds from (the capture's start).</summary>
    public long Origin { get; set; }

    /// <summary>The left column: the remote peer ("Client · 192.168.1.40").</summary>
    public string LeftEndpoint { get; set; } = "Remote";

    /// <summary>The right column: the tapped endpoint ("Server").</summary>
    public string RightEndpoint { get; set; } = "Local";

    /// <summary>Increments on every <see cref="NotifyChanged"/>.</summary>
    public int Version { get; private set; }

    /// <summary>Increments on every <see cref="Clear"/>: rows laid out before it are gone.</summary>
    public int Generation { get; private set; }

    /// <summary>The rows, in display order.</summary>
    public ReadOnlySpan<LadderRow> Rows => CollectionsMarshal.AsSpan(_rows);

    /// <summary>The number of rows.</summary>
    public int Count => _rows.Count;

    /// <summary>The row at <paramref name="index"/>.</summary>
    public ref readonly LadderRow this[int index] => ref CollectionsMarshal.AsSpan(_rows)[index];

    /// <summary>Appends a row; frames of one direction must come in sequence order. Returns its index.</summary>
    public int Add(in LadderRow row)
    {
        List<int> own = _byDirection[(int)row.Direction];
        if (own.Count > 0 && _rows[own[^1]].Frame.Seq >= row.Frame.Seq)
            throw new ArgumentException("frames of one direction must be added in sequence order", nameof(row));

        int index = _rows.Count;
        _rows.Add(row);
        own.Add(index);
        NoteChunk(row);
        return index;
    }

    /// <summary>Replaces a row's values (assembly added flags to a frame after it was projected); its frame stays.</summary>
    public void Replace(int index, in LadderRow row)
    {
        if (_rows[index].Frame != row.Frame)
            throw new ArgumentException("a replaced row keeps its frame", nameof(row));
        _rows[index] = row;
        NoteChunk(row);
    }

    /// <summary>Stores a label once, returning its id for <see cref="LadderRow.Name"/>.</summary>
    public int AddLabel(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_labelIds.TryGetValue(text, out int id)) return id;
        id = _labels.Count;
        _labels.Add(text);
        _labelIds.Add(text, id);
        return id;
    }

    /// <summary>The label stored as <paramref name="id"/>; null for -1 or an unknown id.</summary>
    public string? GetLabel(int id) => (uint)id < (uint)_labels.Count ? _labels[id] : null;

    /// <summary>The chunks seen so far of an assembled message ("MessageChunk 2/5": the 5).</summary>
    public int ChunkTotal(int message) => (uint)message < (uint)_chunkTotals.Count ? _chunkTotals[message] : 0;

    /// <summary>Records the reference of an assembled message, so a double-click can select (trace) it.</summary>
    public void SetMessage(int message, MessageRef reference)
    {
        if (message < 0) return;
        while (_messages.Count <= message) _messages.Add(MessageRef.None);
        _messages[message] = reference;
    }

    /// <summary>The reference of an assembled message; <see cref="MessageRef.None"/> when unknown.</summary>
    public MessageRef MessageOf(int message) => (uint)message < (uint)_messages.Count ? _messages[message] : MessageRef.None;

    /// <summary>The row of <paramref name="frame"/>; -1 when it has none.</summary>
    public int IndexOf(FrameKey frame)
    {
        List<int> own = _byDirection[(int)frame.Direction];
        int lo = 0, hi = own.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            long seq = _rows[own[mid]].Frame.Seq;
            if (seq == frame.Seq) return own[mid];
            if (seq < frame.Seq) lo = mid + 1;
            else hi = mid - 1;
        }

        return -1;
    }

    /// <summary>
    /// The first row at or after <paramref name="ticks"/> (<see cref="Count"/> when none). Rows are in completion order,
    /// which is time order but for a frame of one direction finishing while the other's is still arriving; the search
    /// treats the order as monotonic, which lands on the right row or one beside it.
    /// </summary>
    public int IndexAtOrAfter(long ticks)
    {
        int lo = 0, hi = _rows.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_rows[mid].Ticks < ticks) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    /// <summary>The row the selection names (a frame, or the frame a message was picked from); -1 for none.</summary>
    public int SelectedIndex(in Selection selection) =>
        selection.Kind is SelectionKind.Frame or SelectionKind.Message ? IndexOf(selection.Frame) : -1;

    /// <summary>
    /// What the selection traces: an entity's frames, an assembled message's, a lone message's frame, or — for a
    /// selected frame — its invocation, else its assembled message (the timeline traces a frame's invocation the same
    /// way). A frame that is a whole message of its own and belongs to no invocation traces nothing.
    /// </summary>
    public LadderTrace GetTrace(in Selection selection)
    {
        switch (selection.Kind)
        {
            case SelectionKind.Entity when selection.Entity != EntityKind.None && selection.EntityIndex >= 0:
                return Span(new LadderTrace(LadderTraceKind.Entity, selection.Entity, selection.EntityIndex, default, -1, -1));
            case SelectionKind.Message when selection.Message.Kind == MessageRefKind.Assembled:
                return Span(new LadderTrace(LadderTraceKind.Message, EntityKind.None, selection.Message.Index, default, -1, -1));
            case SelectionKind.Message when selection.Message.Kind == MessageRefKind.Frame:
                return Span(new LadderTrace(LadderTraceKind.Frame, EntityKind.None, -1,
                    new FrameKey(selection.Message.Direction, selection.Message.Seq), -1, -1));
            case SelectionKind.Message:
            case SelectionKind.Frame:
            {
                int at = IndexOf(selection.Frame);
                if (at < 0) return LadderTrace.None;
                ref readonly LadderRow row = ref this[at];
                if (row.Entity == EntityKind.Invocation && row.EntityIndex >= 0)
                    return Span(new LadderTrace(LadderTraceKind.Entity, EntityKind.Invocation, row.EntityIndex, default, -1, -1));
                return row.Message >= 0
                    ? Span(new LadderTrace(LadderTraceKind.Message, EntityKind.None, row.Message, default, -1, -1))
                    : LadderTrace.None;
            }

            default:
                return LadderTrace.None;
        }
    }

    /// <summary>Empties the model (another document, a detach).</summary>
    public void Clear()
    {
        _rows.Clear();
        _byDirection[0].Clear();
        _byDirection[1].Clear();
        _labels.Clear();
        _labelIds.Clear();
        _chunkTotals.Clear();
        _messages.Clear();
        Origin = 0;
        Generation++;
    }

    /// <summary>Tells the views the model changed.</summary>
    public void NotifyChanged()
    {
        Version++;
        Changed?.Invoke();
    }

    private LadderTrace Span(LadderTrace trace)
    {
        ReadOnlySpan<LadderRow> rows = Rows;
        int first = -1, last = -1;
        if (trace.Kind == LadderTraceKind.Frame)
        {
            first = last = IndexOf(trace.Frame);
        }
        else
        {
            for (int i = 0; i < rows.Length; i++)
            {
                if (!trace.Contains(rows[i])) continue;
                if (first < 0) first = i;
                last = i;
            }
        }

        return first < 0 ? LadderTrace.None : trace with { First = first, Last = last };
    }

    private void NoteChunk(in LadderRow row)
    {
        if (row.Part <= 0 || row.Message < 0) return;
        while (_chunkTotals.Count <= row.Message) _chunkTotals.Add(0);
        if (_chunkTotals[row.Message] < row.Part) _chunkTotals[row.Message] = row.Part;
    }
}
