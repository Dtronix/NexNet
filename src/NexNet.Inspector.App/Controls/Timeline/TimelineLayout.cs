using System.Globalization;
using System.Runtime.InteropServices;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Controls.Timeline;

/// <summary>What a <see cref="LayoutRow"/> draws.</summary>
public enum LayoutRowKind : byte
{
    /// <summary>A 14 px group header.</summary>
    Group = 0,

    /// <summary>A lane (12 px, 24 px expanded, anything between while it animates).</summary>
    Lane = 1,

    /// <summary>The hint of a group with nothing in the window.</summary>
    Hint = 2,
}

/// <summary>One row below the axis, top to bottom.</summary>
/// <param name="Kind">What it draws.</param>
/// <param name="Group">Its group.</param>
/// <param name="Lane">The lane, for <see cref="LayoutRowKind.Lane"/>.</param>
/// <param name="Top">Its top, from the top of the lane area.</param>
/// <param name="Height">Its height now.</param>
/// <param name="Ordinal">For a lane in view, its 1-based place among its group's lanes in view (invocation rows are labelled by it).</param>
/// <param name="Expanded">True when the lane is (heading to) 24 px: hovered, pinned or expanded by hand.</param>
/// <param name="Pinned">True for a pinned lane.</param>
public readonly record struct LayoutRow(
    LayoutRowKind Kind,
    LaneGroupKind Group,
    TimelineLane? Lane,
    double Top,
    double Height,
    int Ordinal,
    bool Expanded,
    bool Pinned)
{
    /// <summary>Its bottom.</summary>
    public double Bottom => Top + Height;
}

/// <summary>
/// The vertical layout of the lanes for a window: which groups and lanes take room (hybrid packing collapses invocation
/// rows that are empty in view, entity lanes come and go with their lifetime, pinned lanes stay), each lane's animated
/// height (<see cref="LaneAnimator"/>), and each group's badge. Reused frame to frame: rows live in one list that is
/// cleared, and badges are formatted only when their numbers change, so a steady window allocates nothing.
/// </summary>
public sealed class TimelineLayout
{
    /// <summary>The overview strip (whole session) above the ruler.</summary>
    public const double OverviewHeight = 16;

    /// <summary>The ruler.</summary>
    public const double RulerHeight = 16;

    /// <summary>A group header (lane-group.height).</summary>
    public const double GroupHeight = 14;

    /// <summary>A lane (lane.height).</summary>
    public const double LaneHeight = 12;

    /// <summary>A hovered or pinned lane (lane.expanded-height).</summary>
    public const double ExpandedHeight = 24;

    /// <summary>The empty-group hint: 12 px text with 12 px padding (timeline-lane.html .e).</summary>
    public const double HintHeight = 40;

    private readonly List<LayoutRow> _rows = [];
    private readonly long[] _badgeA = new long[TimelineModel.GroupCount];
    private readonly long[] _badgeB = new long[TimelineModel.GroupCount];
    private readonly string?[] _badges = new string?[TimelineModel.GroupCount];

    /// <param name="animator">Lane heights over time; a default one follows the OS's motion preference.</param>
    public TimelineLayout(LaneAnimator? animator = null) => Animator = animator ?? new LaneAnimator();

    /// <summary>The lane height motion.</summary>
    public LaneAnimator Animator { get; }

    /// <summary>The rows, top to bottom.</summary>
    public ReadOnlySpan<LayoutRow> Rows => CollectionsMarshal.AsSpan(_rows);

    /// <summary>The rows' total height.</summary>
    public double Height { get; private set; }

    /// <summary>True when the last update left a lane mid-motion (draw again next frame).</summary>
    public bool IsAnimating { get; private set; }

    /// <summary>A group's badge ("TCP", "3 rows · 9", "4", "3.1 ms"); null for none.</summary>
    public string? Badge(LaneGroupKind group) => _badges[(int)group];

    /// <summary>Lays the lanes out for <paramref name="window"/>.</summary>
    /// <param name="model">The model.</param>
    /// <param name="window">The visible window.</param>
    /// <param name="hovered">The lane under the pointer, which expands.</param>
    /// <param name="now">Stopwatch ticks, for the height motion.</param>
    /// <param name="instant">True to apply heights at once (a document's first layout).</param>
    public void Update(TimelineModel model, TimeWindow window, LaneKey? hovered, long now, bool instant = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        _rows.Clear();
        double y = 0;
        IReadOnlyList<TimelineLane> lanes = model.Lanes;
        for (int g = 0; g < TimelineModel.GroupCount; g++)
        {
            LaneGroupKind group = (LaneGroupKind)g;
            bool any = model.IsAlwaysShown(group);
            for (int i = 0; i < lanes.Count && !any; i++)
                any = lanes[i].Group == group;
            if (!any)
            {
                _badges[g] = null;
                continue;
            }

            _rows.Add(new LayoutRow(LayoutRowKind.Group, group, null, y, GroupHeight, 0, false, false));
            y += GroupHeight;
            bool collapsed = model.IsCollapsed(group);
            int inView = 0;
            int marksInView = 0;
            bool drawn = false;
            for (int i = 0; i < lanes.Count; i++)
            {
                TimelineLane lane = lanes[i];
                if (lane.Group != group) continue;
                bool pinned = model.IsPinned(lane.Key);
                bool visible = !model.IsHidden(lane.Key) && (pinned || lane.IsVisibleIn(window));
                if (visible && group == LaneGroupKind.Invocations) marksInView += lane.CountIn(window);
                bool expanded = visible && (pinned || hovered == lane.Key || model.IsExpanded(lane.Key));
                double target = collapsed || !visible ? 0 : expanded ? ExpandedHeight : LaneHeight;
                double height = Animator.Height(lane.Key, target, now, instant || collapsed);
                if (visible && !collapsed) inView++;
                if (height < 0.5) continue;
                drawn = true;
                _rows.Add(new LayoutRow(LayoutRowKind.Lane, group, lane, y, height, visible ? inView : 0, expanded, pinned));
                y += height;
            }

            if (!collapsed && inView == 0 && !drawn)
            {
                _rows.Add(new LayoutRow(LayoutRowKind.Hint, group, null, y, HintHeight, 0, false, false));
                y += HintHeight;
            }

            UpdateBadge(model, group, inView, marksInView);
        }

        Height = y;
        IsAnimating = Animator.IsAnimating(now);
    }

    /// <summary>The row at <paramref name="y"/> (lane-area coordinates); -1 when none.</summary>
    public int RowAt(double y)
    {
        ReadOnlySpan<LayoutRow> rows = Rows;
        for (int i = 0; i < rows.Length; i++)
        {
            if (y >= rows[i].Top && y < rows[i].Bottom) return i;
        }

        return -1;
    }

    private void UpdateBadge(TimelineModel model, LaneGroupKind group, int lanesInView, int marksInView)
    {
        int g = (int)group;
        switch (group)
        {
            case LaneGroupKind.Transport:
                _badges[g] = model.TransportName;
                break;
            case LaneGroupKind.Invocations:
                SetBadge(g, lanesInView, marksInView, static (rows, count) => rows == 0
                    ? "0"
                    : rows.ToString(CultureInfo.InvariantCulture) + (rows == 1 ? " row · " : " rows · ") + count.ToString("N0", CultureInfo.InvariantCulture));
                break;
            case LaneGroupKind.Pipes:
            case LaneGroupKind.Collections:
                SetBadge(g, lanesInView, 0, static (count, _) => count.ToString(CultureInfo.InvariantCulture));
                break;
            case LaneGroupKind.Handshake:
            {
                long duration = HandshakeTicks(model);
                long frequency = model.Frequency;
                SetBadge(g, duration, frequency, static (ticks, freq) =>
                    (ticks * 1000.0 / Math.Max(1, freq)).ToString("0.0", CultureInfo.InvariantCulture) + " ms");
                break;
            }

            default:
                _badges[g] = null;
                break;
        }
    }

    private void SetBadge(int g, long a, long b, Func<long, long, string> format)
    {
        if (_badges[g] is not null && _badgeA[g] == a && _badgeB[g] == b) return;
        _badgeA[g] = a;
        _badgeB[g] = b;
        _badges[g] = format(a, b);
    }

    private static long HandshakeTicks(TimelineModel model)
    {
        long start = long.MaxValue, end = long.MinValue;
        foreach (TimelineLane lane in model.Lanes)
        {
            if (lane.Group != LaneGroupKind.Handshake || lane.Count == 0) continue;
            ReadOnlySpan<TimelineMark> marks = lane.Marks;
            start = Math.Min(start, marks[0].Start);
            foreach (ref readonly TimelineMark mark in marks)
                end = Math.Max(end, mark.End);
        }

        return start <= end ? end - start : 0;
    }
}
