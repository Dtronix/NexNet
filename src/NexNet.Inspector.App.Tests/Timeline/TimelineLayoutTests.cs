using NexNet.Inspector.App.Controls.Timeline;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using static NexNet.Inspector.App.Tests.Timeline.TimelineDataBuilder;

namespace NexNet.Inspector.App.Tests.Timeline;

/// <summary>
/// Hybrid packing and lane allocation (timeline-lane README): invocations Gantt-packed into the fewest rows, rows empty
/// in view collapsed, entity lanes added on open and removed once closed and out of view, pins that stay and expand,
/// the empty-group hint, the group badges, and what a selection traces.
/// </summary>
public class TimelineLayoutTests
{
    private static TimelineLayout Layout(TimelineModel model, TimeWindow window)
    {
        TimelineLayout layout = new(new LaneAnimator(enabled: false));
        layout.Update(model, window, now: 0);
        return layout;
    }

    private static LayoutRow[] Lanes(TimelineLayout layout, LaneGroupKind group) =>
        layout.Rows.ToArray().Where(r => r.Kind == LayoutRowKind.Lane && r.Group == group).ToArray();

    [Test]
    public async Task Packing_gives_each_interval_the_first_free_row()
    {
        long[] starts = [0, 11, 17, 36, 38, 68, 74];
        long[] ends = [9, 22, 20, 66, 46, 75, 78];
        int[] rows = new int[starts.Length];
        int count = LanePacker.Pack(starts, ends, rows, new long[starts.Length]);

        await Assert.That(count).IsEqualTo(2);
        await Assert.That(rows).IsEquivalentTo(new[] { 0, 0, 1, 0, 1, 0, 1 });

        // Touching intervals share no row: an interval starting where another ends needs the next one.
        int[] touching = new int[2];
        await Assert.That(LanePacker.Pack([0, 10], [10, 20], touching, new long[2])).IsEqualTo(2);
    }

    [Test]
    public async Task The_demo_packs_into_two_rows_with_no_overlap_in_a_row()
    {
        TimelineModel model = Demo();
        await Assert.That(model.InvocationRows).IsEqualTo(2);
        foreach (TimelineLane lane in model.Lanes.Where(l => l.Key.Kind == LaneKind.InvocationRow))
        {
            TimelineMark[] marks = lane.Marks.ToArray();
            for (int i = 1; i < marks.Length; i++)
                await Assert.That(marks[i].Start).IsGreaterThan(marks[i - 1].End);
        }

        TimelineLane row1 = model.FindLane(new LaneKey(LaneKind.InvocationRow, 0))!;
        await Assert.That(row1.Marks.ToArray().Select(m => m.EntityIndex)).IsEquivalentTo(new[] { 809, 810, 812, 813 });
    }

    [Test]
    public async Task Invocation_rows_empty_in_view_collapse_and_the_badge_counts_what_is_in_view()
    {
        TimelineModel model = Demo();
        TimelineLayout all = Layout(model, Window(0, 100));
        await Assert.That(Lanes(all, LaneGroupKind.Invocations).Length).IsEqualTo(2);
        await Assert.That(all.Badge(LaneGroupKind.Invocations)).IsEqualTo("2 rows · 7");

        // Only #809 Join (row 1) lies in 0-10 ms: row 2 takes no room.
        TimelineLayout early = Layout(model, Window(0, 10));
        LayoutRow[] rows = Lanes(early, LaneGroupKind.Invocations);
        await Assert.That(rows.Length).IsEqualTo(1);
        await Assert.That(rows[0].Ordinal).IsEqualTo(1);
        await Assert.That(early.Badge(LaneGroupKind.Invocations)).IsEqualTo("1 row · 1");

        // In 17.9-20 ms #810 (row 1) and #811 (row 2) are both in view.
        TimelineLayout late = Layout(model, Window(17.9, 20));
        rows = Lanes(late, LaneGroupKind.Invocations);
        await Assert.That(rows.Length).IsEqualTo(2);
        await Assert.That(rows.Select(r => r.Lane!.Key.Index)).IsEquivalentTo(new[] { 0, 1 });
    }

    [Test]
    public async Task Entity_lanes_are_added_on_open_and_removed_once_closed_and_out_of_view()
    {
        TimelineModel model = Demo(pinned: false);
        LaneKey pipe2 = new(LaneKind.Pipe, 2), pipe3 = new(LaneKind.Pipe, 3), pipe5 = new(LaneKind.Pipe, 5);

        LayoutRow[] middle = Lanes(Layout(model, Window(30, 60)), LaneGroupKind.Pipes);
        await Assert.That(middle.Select(r => r.Lane!.Key)).IsEquivalentTo(new[] { pipe3 });
        await Assert.That(Layout(model, Window(30, 60)).Badge(LaneGroupKind.Pipes)).IsEqualTo("1");

        LayoutRow[] start = Lanes(Layout(model, Window(0, 30)), LaneGroupKind.Pipes);
        await Assert.That(start.Select(r => r.Lane!.Key)).IsEquivalentTo(new[] { pipe2, pipe3 });

        LayoutRow[] end = Lanes(Layout(model, Window(70, 100)), LaneGroupKind.Pipes);
        await Assert.That(end.Select(r => r.Lane!.Key)).IsEquivalentTo(new[] { pipe3, pipe5 });
    }

    [Test]
    public async Task A_pinned_lane_stays_out_of_its_lifetime_and_expands_while_others_keep_their_height()
    {
        TimelineModel model = Demo(pinned: false);
        LaneKey pipe2 = new(LaneKind.Pipe, 2);
        model.SetPinned(pipe2, true);
        LayoutRow[] rows = Lanes(Layout(model, Window(30, 60)), LaneGroupKind.Pipes);
        LayoutRow pinned = rows.Single(r => r.Lane!.Key == pipe2);
        await Assert.That(pinned.Height).IsEqualTo(TimelineLayout.ExpandedHeight);
        await Assert.That(pinned.Pinned).IsTrue();

        LayoutRow rest = Lanes(Layout(model, Window(30, 60)), LaneGroupKind.Transport).Single();
        await Assert.That(rest.Height).IsEqualTo(TimelineLayout.LaneHeight);
    }

    [Test]
    public async Task A_group_with_nothing_in_view_shows_its_hint()
    {
        TimelineLayout empty = Layout(EmptyInvocations(), Window(0, 100));
        await Assert.That(empty.Rows.ToArray().Select(r => r.Kind)).IsEquivalentTo(new[] { LayoutRowKind.Group, LayoutRowKind.Hint });
        await Assert.That(empty.Badge(LaneGroupKind.Invocations)).IsEqualTo("0");
        await Assert.That(empty.Height).IsEqualTo(TimelineLayout.GroupHeight + TimelineLayout.HintHeight);
        await Assert.That(TimelineControl.HintText(LaneGroupKind.Invocations))
            .IsEqualTo("No invocations in this window · zoom out (Ctrl+Wheel) or scrub");

        // The demo has no invocation after 78.8 ms.
        TimelineLayout late = Layout(Demo(), Window(85, 95));
        await Assert.That(late.Rows.ToArray().Any(r => r.Kind == LayoutRowKind.Hint && r.Group == LaneGroupKind.Invocations)).IsTrue();
    }

    [Test]
    public async Task A_collapsed_group_shows_its_header_only()
    {
        TimelineModel model = Demo();
        model.SetCollapsed(LaneGroupKind.Invocations, true);
        TimelineLayout layout = Layout(model, Window(0, 100));
        await Assert.That(Lanes(layout, LaneGroupKind.Invocations).Length).IsEqualTo(0);
        await Assert.That(layout.Rows.ToArray().Count(r => r.Group == LaneGroupKind.Invocations)).IsEqualTo(1);
    }

    [Test]
    public async Task Groups_draw_in_order_and_the_transport_badge_names_the_transport()
    {
        TimelineLayout layout = Layout(Busy(64), Window(0, 1000));
        LaneGroupKind[] groups = layout.Rows.ToArray().Where(r => r.Kind == LayoutRowKind.Group).Select(r => r.Group).ToArray();
        await Assert.That(groups).IsEquivalentTo(new[]
        {
            LaneGroupKind.Transport, LaneGroupKind.Frames, LaneGroupKind.Invocations, LaneGroupKind.Pipes,
            LaneGroupKind.Collections, LaneGroupKind.Control,
        });
        await Assert.That(layout.Badge(LaneGroupKind.Transport)).IsEqualTo("TCP");
    }

    [Test]
    public async Task Selecting_an_invocation_or_one_of_its_frames_traces_it()
    {
        TimelineModel model = Busy(64);
        TimelineTrace bySpan = model.GetTrace(Selection.OfEntity(EntityKind.Invocation, 2));
        await Assert.That(bySpan.IsActive).IsTrue();
        await Assert.That(bySpan.Entity).IsEqualTo(EntityKind.Invocation);
        await Assert.That(bySpan.Start).IsEqualTo(model.Invocations.ToArray().Single(s => s.Index == 2).Start);

        // Frame 17 inbound belongs to invocation 2 (eight frames each): tracing it traces the invocation.
        TimelineTrace byFrame = model.GetTrace(Selection.OfFrame(new FrameKey(TapDirection.Inbound, 17)));
        await Assert.That(byFrame).IsEqualTo(bySpan);

        TimelineLane inbound = model.FindLane(new LaneKey(LaneKind.InboundFrames, 0))!;
        await Assert.That(byFrame.Lights(inbound.Marks[17])).IsTrue();
        await Assert.That(byFrame.Lights(inbound.Marks[40])).IsFalse();

        await Assert.That(model.GetTrace(Selection.None).IsActive).IsFalse();
        await Assert.That(TimelineTrace.None.Lights(inbound.Marks[40])).IsTrue();
    }
}
