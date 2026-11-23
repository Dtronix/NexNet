using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Controls.Timeline;

/// <summary>
/// What the timeline draws for one document: its lanes (grouped, each with its marks), gaps, the capture's bounds and
/// clock, and the user's pins and collapsed groups. Filled by <see cref="TimelineProjection"/> from a capture session,
/// or directly (tests, the gallery). UI thread only; <see cref="NotifyChanged"/> tells the control to redraw.
/// </summary>
public sealed class TimelineModel
{
    /// <summary>The number of <see cref="LaneGroupKind"/> values.</summary>
    public const int GroupCount = (int)LaneGroupKind.Control + 1;

    private static readonly string[] Titles =
        ["TRANSPORT", "FRAMES", "HANDSHAKE", "INVOCATIONS", "PIPES · CHANNELS", "COLLECTIONS", "SESSION CONTROL"];

    private static readonly string[] Nouns =
        ["transport ops", "frames", "handshake", "invocations", "pipes or channels", "collections", "session control"];

    private readonly List<TimelineLane> _lanes = [];
    private readonly Dictionary<LaneKey, TimelineLane> _byKey = [];
    private readonly HashSet<LaneKey> _pinned = [];
    private readonly bool[] _collapsed = new bool[GroupCount];
    private readonly bool[] _always = new bool[GroupCount];
    private readonly List<TimelineGap> _gaps = [];
    private readonly List<string> _labels = [];
    private readonly Dictionary<int, int> _byteLabels = [];
    private readonly List<InvocationSpan> _spans = [];
    private readonly Dictionary<int, int> _spanByIndex = [];
    private InvocationSpan[] _sorted = [];
    private long[] _starts = [];
    private long[] _ends = [];
    private long[] _rowEnds = [];
    private int[] _rows = [];
    private int _rowCount;

    /// <summary>Raised by <see cref="NotifyChanged"/>.</summary>
    public event Action? Changed;

    /// <summary>Stopwatch ticks per second of the capture's clock.</summary>
    public long Frequency { get; set; } = Stopwatch.Frequency;

    /// <summary>The capture's time range: the overview's extent and the ruler's origin.</summary>
    public TimeWindow Bounds { get; set; }

    /// <summary>The transport's name, the TRANSPORT group's badge ("TCP"); null for none.</summary>
    public string? TransportName { get; set; }

    /// <summary>Increments on every <see cref="NotifyChanged"/>.</summary>
    public int Version { get; private set; }

    /// <summary>Every lane, in the order added (each group draws its own in that order).</summary>
    public IReadOnlyList<TimelineLane> Lanes => _lanes;

    /// <summary>The gaps, in capture order.</summary>
    public ReadOnlySpan<TimelineGap> Gaps => CollectionsMarshal.AsSpan(_gaps);

    /// <summary>Invocation rows the last <see cref="PackInvocations"/> used.</summary>
    public int InvocationRows => _rowCount;

    /// <summary>The invocations last packed.</summary>
    public ReadOnlySpan<InvocationSpan> Invocations => CollectionsMarshal.AsSpan(_spans);

    /// <summary>A group's header title, uppercase as drawn.</summary>
    public static string Title(LaneGroupKind group) => Titles[(int)group];

    /// <summary>The empty-group hint ("No invocations in this window · zoom out (Ctrl+Wheel) or scrub").</summary>
    public static string Noun(LaneGroupKind group) => Nouns[(int)group];

    /// <summary>The lane for <paramref name="key"/>, created in <paramref name="group"/> when missing.</summary>
    public TimelineLane GetOrAddLane(LaneKey key, LaneGroupKind group, LaneGlyph glyph, string label,
        LaneVisibility visibility = LaneVisibility.Always)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (_byKey.TryGetValue(key, out TimelineLane? lane)) return lane;
        lane = new TimelineLane(key, group, glyph, label, visibility);
        _byKey.Add(key, lane);
        _lanes.Add(lane);
        return lane;
    }

    /// <summary>The lane for <paramref name="key"/>; null when there is none.</summary>
    public TimelineLane? FindLane(LaneKey key) => _byKey.GetValueOrDefault(key);

    /// <summary>Removes a lane (its pin goes with it only if <paramref name="unpin"/>).</summary>
    public void RemoveLane(LaneKey key, bool unpin = false)
    {
        if (!_byKey.Remove(key, out TimelineLane? lane)) return;
        _lanes.Remove(lane);
        if (unpin) _pinned.Remove(key);
    }

    /// <summary>True when the group has a lane at all (it may have none in view).</summary>
    public bool HasLanes(LaneGroupKind group)
    {
        foreach (TimelineLane lane in _lanes)
        {
            if (lane.Group == group) return true;
        }

        return false;
    }

    /// <summary>Stores a label, returning its id for <see cref="TimelineMark.Label"/>.</summary>
    public int AddLabel(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _labels.Add(text);
        return _labels.Count - 1;
    }

    /// <summary>Replaces a stored label (an invocation's method became known).</summary>
    public void SetLabel(int id, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _labels[id] = text;
    }

    /// <summary>A stored label; null for -1.</summary>
    public string? GetLabel(int id) => id >= 0 && id < _labels.Count ? _labels[id] : null;

    /// <summary>The label of a byte count ("16,384"), stored once per distinct count.</summary>
    public int BytesLabel(int bytes)
    {
        if (_byteLabels.TryGetValue(bytes, out int id)) return id;
        id = AddLabel(bytes.ToString("N0", CultureInfo.InvariantCulture));
        _byteLabels.Add(bytes, id);
        return id;
    }

    /// <summary>Shows a group even with no lanes (TRANSPORT, FRAMES, INVOCATIONS, SESSION CONTROL of a session).</summary>
    public void AlwaysShow(LaneGroupKind group, bool show = true) => _always[(int)group] = show;

    /// <summary>True when the group shows even with no lanes.</summary>
    public bool IsAlwaysShown(LaneGroupKind group) => _always[(int)group];

    /// <summary>True when the lane is pinned: it stays and expands.</summary>
    public bool IsPinned(LaneKey key) => _pinned.Contains(key);

    /// <summary>The pinned lanes.</summary>
    public IReadOnlyCollection<LaneKey> Pinned => _pinned;

    /// <summary>Pins or unpins a lane.</summary>
    public void SetPinned(LaneKey key, bool pinned)
    {
        if (pinned ? _pinned.Add(key) : _pinned.Remove(key)) NotifyChanged();
    }

    /// <summary>Flips a lane's pin.</summary>
    public void TogglePinned(LaneKey key) => SetPinned(key, !IsPinned(key));

    /// <summary>True when the group shows its header only.</summary>
    public bool IsCollapsed(LaneGroupKind group) => _collapsed[(int)group];

    /// <summary>Collapses or expands a group.</summary>
    public void SetCollapsed(LaneGroupKind group, bool collapsed)
    {
        if (_collapsed[(int)group] == collapsed) return;
        _collapsed[(int)group] = collapsed;
        NotifyChanged();
    }

    /// <summary>Flips a group's collapse.</summary>
    public void ToggleCollapsed(LaneGroupKind group) => SetCollapsed(group, !IsCollapsed(group));

    /// <summary>Adds a gap.</summary>
    public void AddGap(in TimelineGap gap) => _gaps.Add(gap);

    /// <summary>Removes every gap.</summary>
    public void ClearGaps() => _gaps.Clear();

    /// <summary>Removes the invocations (before adding them again and packing).</summary>
    public void ClearInvocations() => _spans.Clear();

    /// <summary>Adds an invocation; <see cref="PackInvocations"/> places it.</summary>
    public void AddInvocation(in InvocationSpan span) => _spans.Add(span);

    /// <summary>
    /// Packs the invocations into the fewest rows (<see cref="LanePacker"/>) and refills the INVOCATIONS row lanes,
    /// adding or removing rows as the count changes. Pins of rows that remain are kept.
    /// </summary>
    public void PackInvocations()
    {
        int n = _spans.Count;
        if (_sorted.Length < n)
        {
            int size = Math.Max(n, _sorted.Length * 2);
            _sorted = new InvocationSpan[size];
            _starts = new long[size];
            _ends = new long[size];
            _rowEnds = new long[size];
            _rows = new int[size];
        }

        Span<InvocationSpan> sorted = _sorted.AsSpan(0, n);
        CollectionsMarshal.AsSpan(_spans).CopyTo(sorted);
        sorted.Sort(static (a, b) =>
        {
            int c = a.Start.CompareTo(b.Start);
            return c != 0 ? c : a.Index.CompareTo(b.Index);
        });
        _spanByIndex.Clear();
        for (int i = 0; i < n; i++)
        {
            _starts[i] = sorted[i].Start;
            _ends[i] = sorted[i].End;
            _spanByIndex[sorted[i].Index] = i;
        }

        int rows = LanePacker.Pack(_starts.AsSpan(0, n), _ends.AsSpan(0, n), _rows.AsSpan(0, n), _rowEnds.AsSpan(0, n));
        for (int r = rows; r < _rowCount; r++)
            RemoveLane(new LaneKey(LaneKind.InvocationRow, r));
        for (int r = 0; r < rows; r++)
        {
            GetOrAddLane(new LaneKey(LaneKind.InvocationRow, r), LaneGroupKind.Invocations, LaneGlyph.None,
                RowLabel(r), LaneVisibility.Marks).Clear();
        }

        _rowCount = rows;
        for (int i = 0; i < n; i++)
        {
            ref readonly InvocationSpan s = ref sorted[i];
            FindLane(new LaneKey(LaneKind.InvocationRow, _rows[i]))!.Add(new TimelineMark(s.Start, Math.Max(s.Start, s.End),
                MarkKind.Span, s.Direction, s.Flags, EntityKind.Invocation, s.Index, -1, 0, s.Label));
        }
    }

    /// <summary>"row 1", "row 2", … (cached: the gutter draws them every frame).</summary>
    public static string RowLabel(int row)
    {
        while (RowLabels.Count <= row)
            RowLabels.Add("row " + (RowLabels.Count + 1).ToString(CultureInfo.InvariantCulture));
        return RowLabels[row];
    }

    private static readonly List<string> RowLabels = [];

    /// <summary>
    /// What <paramref name="selection"/> traces: an invocation (from its span, or from any frame of it), a pipe or
    /// greeting, or a lone frame; nothing for no selection or a byte range.
    /// </summary>
    public TimelineTrace GetTrace(in Selection selection)
    {
        switch (selection.Kind)
        {
            case SelectionKind.Entity:
                return TraceEntity(selection.Entity, selection.EntityIndex, TimelineTrace.None);
            case SelectionKind.Frame:
            case SelectionKind.Message:
            {
                TapDirection direction = selection.Frame.Direction;
                TimelineLane? frames = FindLane(new LaneKey(direction == TapDirection.Inbound ? LaneKind.InboundFrames : LaneKind.OutboundFrames, 0));
                int at = frames?.IndexOfSeq(selection.Frame.Seq) ?? -1;
                if (at < 0) return TimelineTrace.None;
                TimelineMark frame = frames!.Marks[at];
                TimelineTrace lone = new(true, EntityKind.None, -1, direction, frame.Seq, frame.Start, frame.End);
                return frame.Entity == EntityKind.Invocation ? TraceEntity(frame.Entity, frame.EntityIndex, lone) : lone;
            }

            default:
                return TimelineTrace.None;
        }
    }

    /// <summary>
    /// The band and lit marks of a trace resolved on the sync bus: an invocation's band spans its span (request to
    /// result) and its span is drawn selected; any other trace (a pipe run, a channel item, the handshake, a pipe) spans
    /// exactly its member frames, which are the marks lit.
    /// </summary>
    public TimelineTrace GetTrace(MessageTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        if (!trace.IsActive) return TimelineTrace.None;
        if (trace.Entity == EntityKind.Invocation && _spanByIndex.TryGetValue(trace.EntityIndex, out int at))
        {
            InvocationSpan span = _sorted[at];
            return new TimelineTrace(true, EntityKind.Invocation, trace.EntityIndex, span.Direction, -1, Math.Min(span.Start, trace.Start),
                Math.Max(span.End, trace.End)) { Members = trace };
        }

        EntityKind entity = trace.Entity is EntityKind.Invocation or EntityKind.Pipe ? trace.Entity : EntityKind.None;
        return new TimelineTrace(true, entity, entity == EntityKind.None ? -1 : trace.EntityIndex, trace.Message.Direction, -1, trace.Start,
            trace.End) { Members = trace };
    }

    /// <summary>Removes everything but pins and collapsed groups (a new document, a detach).</summary>
    public void Clear()
    {
        _lanes.Clear();
        _byKey.Clear();
        _gaps.Clear();
        _labels.Clear();
        _byteLabels.Clear();
        _spans.Clear();
        _spanByIndex.Clear();
        _rowCount = 0;
        Bounds = default;
        TransportName = null;
        Array.Clear(_always);
    }

    /// <summary>Tells the views the model changed.</summary>
    public void NotifyChanged()
    {
        Version++;
        Changed?.Invoke();
    }

    private TimelineTrace TraceEntity(EntityKind entity, int index, TimelineTrace fallback)
    {
        if (entity == EntityKind.Invocation && _spanByIndex.TryGetValue(index, out int at))
        {
            InvocationSpan span = _sorted[at];
            return new TimelineTrace(true, entity, index, span.Direction, -1, span.Start, span.End);
        }

        // A pipe or a greeting: the band covers its marks.
        long start = long.MaxValue, end = long.MinValue;
        foreach (TimelineLane lane in _lanes)
        {
            if (lane.Group is LaneGroupKind.Transport or LaneGroupKind.Frames) continue;
            foreach (ref readonly TimelineMark mark in lane.Marks)
            {
                if (mark.Entity != entity || mark.EntityIndex != index) continue;
                start = Math.Min(start, mark.Start);
                end = Math.Max(end, mark.End);
            }
        }

        return start <= end ? new TimelineTrace(true, entity, index, TapDirection.Inbound, -1, start, end) : fallback;
    }
}
