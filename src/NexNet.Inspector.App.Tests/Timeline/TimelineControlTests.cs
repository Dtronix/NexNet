using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using NexNet.Inspector.App.Controls.Timeline;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;
using static NexNet.Inspector.App.Tests.Timeline.TimelineDataBuilder;

namespace NexNet.Inspector.App.Tests.Timeline;

/// <summary>
/// The drawn timeline on the App's headless session: Ctrl+Wheel zooms around the pointer, Wheel pans time, Shift+Wheel
/// scrolls the lanes (SYSTEM.md UX rules), a click on a span traces its invocation and moves the playhead through the
/// sync bus, hovering a lane sets its tooltip without moving any lane, P pins the hovered lane.
/// </summary>
[NotInParallel("avalonia")]
public sealed class TimelineControlTests
{
    private const double Width = TimelineControl.GutterWidth + 760;

    internal sealed record Probe(Window Window, TimelineControl Timeline, SyncBus Sync, TimelineModel Model);

    internal static Task<T> With<T>(TimelineModel model, TimeWindow window, Func<Probe, T> body, double height = 0,
        ThemeVariant? theme = null, Func<IUiDispatcher>? ui = null) =>
        HeadlessSession.Run(() => UiPump.InTheme(theme ?? ThemeVariant.Light, () =>
        {
            SyncBus sync = new(ui?.Invoke() ?? new TestUi());
            sync.SetBounds(model.Bounds);
            sync.SetWindow(window, byUser: false);
            TimelineControl timeline = new()
            {
                Model = model,
                Sync = sync,
                VerticalAlignment = VerticalAlignment.Top,
                Clock = static () => 0,
            };
            timeline.Layout.Animator.Enabled = false;
            if (height > 0) timeline.Height = height;
            Window host = new() { Width = Width, Height = 600, Content = timeline };
            host.Show();
            UiPump.Frames();
            try
            {
                return body(new Probe(host, timeline, sync, model));
            }
            finally
            {
                host.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }));

    private static Point LanePoint(TimelineControl timeline, LaneKey lane, double x)
    {
        LayoutRow row = timeline.Layout.Rows.ToArray().Single(r => r.Lane?.Key == lane);
        return new Point(x, timeline.LanesTop + row.Top + (row.Height / 2) - timeline.LaneScroll);
    }

    [Test]
    public async Task Ctrl_wheel_zooms_around_the_pointer()
    {
        (TimeWindow before, TimeWindow after, long anchor) = await With(Demo(), Window(0, 100), p =>
        {
            Point point = new(TimelineControl.GutterWidth + 190, 60);
            long anchorTicks = p.Timeline.TicksAt(point.X);
            TimeWindow start = p.Sync.Window;
            p.Window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.Control);
            UiPump.Frames();
            return (start, p.Sync.Window, anchorTicks);
        });

        await Assert.That(after.Duration).IsEqualTo((long)Math.Round(before.Duration / 1.25)).Within(1);
        double fractionBefore = (double)(anchor - before.Start) / before.Duration;
        double fractionAfter = (double)(anchor - after.Start) / after.Duration;
        await Assert.That(fractionAfter).IsEqualTo(fractionBefore).Within(1e-4);
        await Assert.That(anchor).IsEqualTo(Ticks(25)).Within(Ticks(0.2));
    }

    [Test]
    public async Task Wheel_pans_time_and_shift_wheel_scrolls_the_lanes()
    {
        (TimeWindow panned, double scroll, TimeWindow afterShift) = await With(Busy(256), Window(100, 200), p =>
        {
            Point point = new(TimelineControl.GutterWidth + 100, 40);
            p.Window.MouseWheel(point, new Vector(0, -1), RawInputModifiers.None);
            UiPump.Frames();
            TimeWindow moved = p.Sync.Window;
            p.Window.MouseWheel(point, new Vector(0, -1), RawInputModifiers.Shift);
            UiPump.Frames();
            return (moved, p.Timeline.LaneScroll, p.Sync.Window);
        }, height: 120);

        // A wheel notch pans a tenth of the window forward in time.
        await Assert.That(panned).IsEqualTo(Window(110, 210));
        await Assert.That(scroll).IsEqualTo(36);
        await Assert.That(afterShift).IsEqualTo(panned);
    }

    [Test]
    public async Task Clicking_a_span_traces_its_invocation_and_moves_the_playhead()
    {
        (Selection selection, long playhead, TimelineTrace trace) = await With(Demo(), Window(0, 100), p =>
        {
            Point point = LanePoint(p.Timeline, new LaneKey(LaneKind.InvocationRow, 0), p.Timeline.XOf(Ticks(50)));
            p.Window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            p.Window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            UiPump.Frames();
            return (p.Sync.Selection, p.Sync.Playhead, p.Timeline.Trace);
        });

        await Assert.That(selection).IsEqualTo(Selection.OfEntity(EntityKind.Invocation, 812));
        await Assert.That(playhead).IsEqualTo(Ticks(36.2));
        await Assert.That(trace.IsActive).IsTrue();
        await Assert.That(trace.Start).IsEqualTo(Ticks(36.2));
        await Assert.That(trace.End).IsEqualTo(Ticks(66.8));
    }

    [Test]
    public async Task Clicking_a_frame_selects_it_and_left_right_step_through_its_lane()
    {
        (Selection clicked, Selection next) = await With(Busy(64), Window(0, 1000), p =>
        {
            TimelineLane inbound = p.Model.FindLane(new LaneKey(LaneKind.InboundFrames, 0))!;
            TimelineMark frame = inbound.Marks[10];
            Point point = LanePoint(p.Timeline, inbound.Key, p.Timeline.XOf(frame.Start) + 1);
            p.Window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            p.Window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            UiPump.Frames();
            Selection first = p.Sync.Selection;
            p.Window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            UiPump.Frames();
            return (first, p.Sync.Selection);
        });

        await Assert.That(clicked).IsEqualTo(Selection.OfFrame(new Core.Session.FrameKey(TapDirection.Inbound, 10)));
        await Assert.That(next).IsEqualTo(Selection.OfFrame(new Core.Session.FrameKey(TapDirection.Inbound, 11)));
    }

    [Test]
    public async Task Hovering_a_lane_keeps_its_height_shows_its_tooltip_and_P_pins_it()
    {
        LaneKey pipe = new(LaneKind.Pipe, 2);
        (double height, string? tip, bool pinned) = await With(Demo(pinned: false), Window(0, 100), p =>
        {
            Point point = LanePoint(p.Timeline, pipe, 40);
            p.Window.MouseMove(point, RawInputModifiers.None);
            UiPump.Frames();
            double expanded = p.Timeline.Layout.Rows.ToArray().Single(r => r.Lane?.Key == pipe).Height;
            string? text = ToolTip.GetTip(p.Timeline) as string;
            p.Timeline.Focus();
            p.Window.KeyPress(Key.P, RawInputModifiers.None, PhysicalKey.P, "p");
            UiPump.Frames();
            return (expanded, text, p.Model.IsPinned(pipe));
        });

        await Assert.That(height).IsEqualTo(TimelineLayout.LaneHeight);
        await Assert.That(tip).IsEqualTo("Pipe 2·1 Sync · closed");
        await Assert.That(pinned).IsTrue();
    }

    [Test]
    public async Task Moving_the_pointer_across_every_lane_moves_no_lane_and_invalidates_no_measure()
    {
        (bool sameRows, bool measureValid, bool hoveredSome, double height, double after) = await With(Demo(pinned: false), Window(0, 100), p =>
        {
            (LaneKey?, double, double)[] before = Snapshot(p.Timeline);
            double height0 = p.Timeline.Bounds.Height;
            bool valid = true, hovered = false;
            double top = p.Timeline.LanesTop;
            for (double y = top; y < top + p.Timeline.Layout.Height; y += 3)
            {
                foreach (double x in new[] { 30.0, TimelineControl.GutterWidth + 200 })
                {
                    p.Window.MouseMove(new Point(x, y), RawInputModifiers.None);
                    valid &= p.Timeline.IsMeasureValid;
                    hovered |= p.Timeline.HoveredLane is not null;
                }
            }

            UiPump.Frames();
            return (before.SequenceEqual(Snapshot(p.Timeline)), valid, hovered, height0, p.Timeline.Bounds.Height);
        });

        await Assert.That(hoveredSome).IsTrue();
        await Assert.That(measureValid).IsTrue();
        await Assert.That(sameRows).IsTrue();
        await Assert.That(after).IsEqualTo(height);
    }

    private static (LaneKey?, double, double)[] Snapshot(TimelineControl timeline) =>
        timeline.Layout.Rows.ToArray().Select(r => (r.Lane?.Key, r.Top, r.Height)).ToArray();

    [Test]
    public async Task Clicking_a_group_header_collapses_it()
    {
        bool collapsed = await With(Demo(), Window(0, 100), p =>
        {
            LayoutRow header = p.Timeline.Layout.Rows.ToArray().First(r => r.Kind == LayoutRowKind.Group && r.Group == LaneGroupKind.Invocations);
            Point point = new(20, p.Timeline.LanesTop + header.Top + 5);
            p.Window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            p.Window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            UiPump.Frames();
            return p.Model.IsCollapsed(LaneGroupKind.Invocations);
        });

        await Assert.That(collapsed).IsTrue();
    }
}
