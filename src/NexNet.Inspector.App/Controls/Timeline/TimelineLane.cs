using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Controls.Timeline;

/// <summary>When a lane takes room on the timeline.</summary>
public enum LaneVisibility : byte
{
    /// <summary>Whenever its group shows (reads, writes, frames, control, the handshake's session lane).</summary>
    Always = 0,

    /// <summary>
    /// While its entity's lifetime touches the window: a pipe, channel or collection lane is added when it opens and
    /// removed once it has closed and left the window (timeline-lane README, Lane allocation).
    /// </summary>
    Lifetime = 1,

    /// <summary>While a mark of it is in the window: invocation rows collapse when empty in view.</summary>
    Marks = 2,
}

/// <summary>
/// One lane: its key, group, glyph and labels, and its marks kept sorted by start tick, so drawing a window starts at a
/// binary search instead of the first mark (large captures draw only what is visible).
/// </summary>
public sealed class TimelineLane
{
    private static readonly Comparison<TimelineMark> ByStart = static (a, b) =>
    {
        int c = a.Start.CompareTo(b.Start);
        return c != 0 ? c : a.Seq.CompareTo(b.Seq);
    };

    private TimelineMark[] _marks = [];
    private int _count;
    private bool _sorted = true;

    internal TimelineLane(LaneKey key, LaneGroupKind group, LaneGlyph glyph, string label, LaneVisibility visibility)
    {
        Key = key;
        Group = group;
        Glyph = glyph;
        Label = label;
        Visibility = visibility;
    }

    /// <summary>The lane's key.</summary>
    public LaneKey Key { get; }

    /// <summary>The group it is drawn in.</summary>
    public LaneGroupKind Group { get; set; }

    /// <summary>The glyph before its label.</summary>
    public LaneGlyph Glyph { get; set; }

    /// <summary>The label ("reads", "Pipe 3·1"); at most 72 px, ellipsised.</summary>
    public string Label { get; set; }

    /// <summary>The tertiary sub label ("FileUpload · raw"); ellipsised, in full in the tooltip.</summary>
    public string? SubLabel { get; set; }

    /// <summary>When the lane takes room.</summary>
    public LaneVisibility Visibility { get; set; }

    /// <summary>For <see cref="LaneVisibility.Lifetime"/>: the first tick of the entity's life.</summary>
    public long LifeStart { get; set; }

    /// <summary>For <see cref="LaneVisibility.Lifetime"/>: the last tick, <see cref="long.MaxValue"/> while it lives.</summary>
    public long LifeEnd { get; set; } = long.MaxValue;

    /// <summary>The longest mark, which bounds how far before the window a mark that reaches into it can start.</summary>
    public long MaxDuration { get; private set; }

    /// <summary>The number of marks.</summary>
    public int Count => _count;

    /// <summary>The marks, by start tick.</summary>
    public ReadOnlySpan<TimelineMark> Marks
    {
        get
        {
            EnsureSorted();
            return _marks.AsSpan(0, _count);
        }
    }

    /// <summary>Adds a mark.</summary>
    public void Add(in TimelineMark mark)
    {
        if (_count == _marks.Length)
            Array.Resize(ref _marks, Math.Max(16, _marks.Length * 2));
        if (_count > 0 && ByStart(_marks[_count - 1], mark) > 0)
            _sorted = false;
        _marks[_count++] = mark;
        long duration = mark.End - mark.Start;
        if (duration > MaxDuration)
            MaxDuration = duration;
    }

    /// <summary>Removes every mark (the storage is kept for the next fill).</summary>
    public void Clear()
    {
        _count = 0;
        _sorted = true;
        MaxDuration = 0;
    }

    /// <summary>The first mark that can reach into a window starting at <paramref name="windowStart"/>.</summary>
    public int FirstCandidate(long windowStart)
    {
        ReadOnlySpan<TimelineMark> marks = Marks;
        long from = windowStart - MaxDuration;
        int lo = 0, hi = marks.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (marks[mid].Start < from) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    /// <summary>The marks touching <paramref name="window"/>.</summary>
    public int CountIn(TimeWindow window)
    {
        ReadOnlySpan<TimelineMark> marks = Marks;
        int count = 0;
        for (int i = FirstCandidate(window.Start); i < marks.Length && marks[i].Start < window.End; i++)
        {
            if (marks[i].End >= window.Start) count++;
        }

        return count;
    }

    /// <summary>True when a mark touches <paramref name="window"/>.</summary>
    public bool HasMarkIn(TimeWindow window)
    {
        ReadOnlySpan<TimelineMark> marks = Marks;
        for (int i = FirstCandidate(window.Start); i < marks.Length && marks[i].Start < window.End; i++)
        {
            if (marks[i].End >= window.Start) return true;
        }

        return false;
    }

    /// <summary>The number of marks starting in [<paramref name="from"/>, <paramref name="to"/>): the overview's density.</summary>
    public int CountStarting(long from, long to) => LowerBound(to) - LowerBound(from);

    /// <summary>The index of frame <paramref name="seq"/> in a lane whose marks are frames in sequence order; -1 if absent.</summary>
    public int IndexOfSeq(long seq)
    {
        ReadOnlySpan<TimelineMark> marks = Marks;
        int lo = 0, hi = marks.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            long s = marks[mid].Seq;
            if (s == seq) return mid;
            if (s < seq) lo = mid + 1;
            else hi = mid - 1;
        }

        return -1;
    }

    /// <summary>The first mark starting at or after <paramref name="ticks"/>; <see cref="Count"/> when none does.</summary>
    public int LowerBound(long ticks)
    {
        ReadOnlySpan<TimelineMark> marks = Marks;
        int lo = 0, hi = marks.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (marks[mid].Start < ticks) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    /// <summary>True when the lane takes room in <paramref name="window"/> (pins aside).</summary>
    public bool IsVisibleIn(TimeWindow window) => Visibility switch
    {
        LaneVisibility.Lifetime => LifeStart < window.End && LifeEnd >= window.Start,
        LaneVisibility.Marks => HasMarkIn(window),
        _ => true,
    };

    private void EnsureSorted()
    {
        if (_sorted) return;
        _marks.AsSpan(0, _count).Sort(ByStart);
        _sorted = true;
    }
}
