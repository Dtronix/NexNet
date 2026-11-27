using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using NexNet.Inspector.App.Controls.Timeline;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.Core.Model;
using static NexNet.Inspector.App.Tests.Timeline.TimelineDataBuilder;

namespace NexNet.Inspector.App.Tests.Timeline;

/// <summary>
/// Range drag (D1) with real headless pointer and key input: the overview's box has edge handles (6 px zones) that move
/// the window's start or end, its middle pans it, double-click fits all; a drag across the ruler selects a range and the
/// release zooms to it while a click still moves the playhead; Esc cancels any drag; the window keeps a minimum span and
/// stays inside the timeline's range bounds; hover and the selection band never invalidate a measure, and a drag
/// allocates nothing.
/// </summary>
[NotInParallel("avalonia")]
public sealed class RangeDragTests
{
    private const double OverviewY = TimelineLayout.OverviewHeight / 2;
    private const double RulerY = TimelineLayout.OverviewHeight + (TimelineLayout.RulerHeight / 2);

    // Demo() spans 0-100 ms on a 760 px track: 1 px is about 131.6 ticks (µs).
    private static readonly long Px = Ticks(0.2);

    private static double OverviewX(TimelineControl timeline, double ms) =>
        TimelineControl.GutterWidth + timeline.RangeBounds.ToPixel(Ticks(ms), timeline.Bounds.Width - TimelineControl.GutterWidth);

    private static void Drag(Avalonia.Controls.Window window, Point from, Point to, bool release = true)
    {
        window.MouseMove(from, RawInputModifiers.None);
        window.MouseDown(from, MouseButton.Left, RawInputModifiers.None);
        for (int i = 1; i <= 4; i++)
            window.MouseMove(from + ((to - from) * (i / 4.0)), RawInputModifiers.LeftMouseButton);
        if (release) window.MouseUp(to, MouseButton.Left, RawInputModifiers.None);
        UiPump.Frames();
    }

    [Test]
    public async Task Handle_zones_are_six_pixels_on_each_edge_and_a_narrow_box_keeps_a_middle()
    {
        using (Assert.Multiple())
        {
            await Assert.That(TimelineControl.OverviewPartAt(97, 100, 200)).IsEqualTo(OverviewPart.StartHandle);
            await Assert.That(TimelineControl.OverviewPartAt(103, 100, 200)).IsEqualTo(OverviewPart.StartHandle);
            await Assert.That(TimelineControl.OverviewPartAt(96.5, 100, 200)).IsEqualTo(OverviewPart.Outside);
            await Assert.That(TimelineControl.OverviewPartAt(103.5, 100, 200)).IsEqualTo(OverviewPart.Window);
            await Assert.That(TimelineControl.OverviewPartAt(150, 100, 200)).IsEqualTo(OverviewPart.Window);
            await Assert.That(TimelineControl.OverviewPartAt(197, 100, 200)).IsEqualTo(OverviewPart.EndHandle);
            await Assert.That(TimelineControl.OverviewPartAt(203, 100, 200)).IsEqualTo(OverviewPart.EndHandle);
            await Assert.That(TimelineControl.OverviewPartAt(204, 100, 200)).IsEqualTo(OverviewPart.Outside);

            // An 8 px box: 2 px of each handle inside, 4 px in the middle to pan by.
            await Assert.That(TimelineControl.OverviewPartAt(101.9, 100, 108)).IsEqualTo(OverviewPart.StartHandle);
            await Assert.That(TimelineControl.OverviewPartAt(104, 100, 108)).IsEqualTo(OverviewPart.Window);
            await Assert.That(TimelineControl.OverviewPartAt(106.1, 100, 108)).IsEqualTo(OverviewPart.EndHandle);
        }

        (OverviewPart start, OverviewPart middle, OverviewPart end, OverviewPart outside) = await TimelineControlTests.With(Demo(), Window(20, 60), p =>
        {
            TimelineControl t = p.Timeline;
            return (t.OverviewPartAt(OverviewX(t, 20) + 2), t.OverviewPartAt(OverviewX(t, 40)), t.OverviewPartAt(OverviewX(t, 60) - 2),
                t.OverviewPartAt(OverviewX(t, 80)));
        });

        await Assert.That(start).IsEqualTo(OverviewPart.StartHandle);
        await Assert.That(middle).IsEqualTo(OverviewPart.Window);
        await Assert.That(end).IsEqualTo(OverviewPart.EndHandle);
        await Assert.That(outside).IsEqualTo(OverviewPart.Outside);
    }

    [Test]
    public async Task Dragging_an_edge_handle_moves_that_edge_and_keeps_the_other()
    {
        (TimeWindow left, TimeWindow right) = await TimelineControlTests.With(Demo(), Window(20, 60), p =>
        {
            TimelineControl t = p.Timeline;
            Drag(p.Window, new Point(OverviewX(t, 20) + 1, OverviewY), new Point(OverviewX(t, 10) + 1, OverviewY));
            TimeWindow afterLeft = p.Sync.Window;
            Drag(p.Window, new Point(OverviewX(t, 60) - 1, OverviewY), new Point(OverviewX(t, 80) - 1, OverviewY));
            return (afterLeft, p.Sync.Window);
        });

        await Assert.That(left.Start).IsEqualTo(Ticks(10)).Within(Px);
        await Assert.That(left.End).IsEqualTo(Ticks(60));
        await Assert.That(right.Start).IsEqualTo(left.Start);
        await Assert.That(right.End).IsEqualTo(Ticks(80)).Within(Px);
    }

    [Test]
    public async Task A_handle_stops_at_the_minimum_span_and_at_the_range_bounds()
    {
        (TimeWindow crossed, long minSpan, TimeWindow pastEnd, TimeWindow pastStart) = await TimelineControlTests.With(Demo(), Window(20, 60), p =>
        {
            TimelineControl t = p.Timeline;
            double width = t.Bounds.Width - TimelineControl.GutterWidth;
            long span = Math.Max(TimeWindow.MinSpan(Frequency), t.RangeBounds.PixelsToTicks(TimelineControl.MinHandleGap, width));

            // The start handle dragged past the end stops a minimum span before it.
            Drag(p.Window, new Point(OverviewX(t, 20), OverviewY), new Point(OverviewX(t, 90), OverviewY));
            TimeWindow a = p.Sync.Window;
            p.Sync.SetWindow(Window(20, 60));
            UiPump.Frames();

            // The end handle dragged off the control stops at the capture's end; the start handle at its start.
            Drag(p.Window, new Point(OverviewX(t, 60), OverviewY), new Point(t.Bounds.Width + 300, OverviewY));
            TimeWindow b = p.Sync.Window;
            Drag(p.Window, new Point(OverviewX(t, 20), OverviewY), new Point(-200, OverviewY));
            return (a, span, b, p.Sync.Window);
        });

        await Assert.That(crossed.End).IsEqualTo(Ticks(60));
        await Assert.That(crossed.Duration).IsEqualTo(minSpan);
        await Assert.That(minSpan).IsGreaterThanOrEqualTo(TimeWindow.MinSpan(Frequency));
        await Assert.That(pastEnd).IsEqualTo(Window(20, 100));
        await Assert.That(pastStart).IsEqualTo(Window(0, 100));
    }

    [Test]
    public async Task Dragging_the_middle_pans_at_the_same_span_and_stops_at_the_bounds()
    {
        (TimeWindow panned, TimeWindow clamped, TimeWindow centred) = await TimelineControlTests.With(Demo(), Window(20, 60), p =>
        {
            TimelineControl t = p.Timeline;
            Drag(p.Window, new Point(OverviewX(t, 40), OverviewY), new Point(OverviewX(t, 50), OverviewY));
            TimeWindow a = p.Sync.Window;
            Drag(p.Window, new Point(OverviewX(t, 50), OverviewY), new Point(-400, OverviewY));
            TimeWindow b = p.Sync.Window;

            // A press outside the box centres the window there.
            Drag(p.Window, new Point(OverviewX(t, 70), OverviewY), new Point(OverviewX(t, 70), OverviewY));
            return (a, b, p.Sync.Window);
        });

        await Assert.That(panned.Duration).IsEqualTo(Ticks(40));
        await Assert.That(panned.Start).IsEqualTo(Ticks(30)).Within(Px);
        await Assert.That(clamped).IsEqualTo(Window(0, 40));
        await Assert.That(centred.Duration).IsEqualTo(Ticks(40));
        await Assert.That(centred.Center).IsEqualTo(Ticks(70)).Within(Px);
    }

    [Test]
    public async Task Hovering_a_handle_recolours_it_without_a_measure_and_double_click_fits_all()
    {
        (OverviewPart hot, bool valid, OverviewPart cold, TimeWindow fitted) = await TimelineControlTests.With(Demo(), Window(20, 60), p =>
        {
            TimelineControl t = p.Timeline;
            p.Window.MouseMove(new Point(OverviewX(t, 60), OverviewY), RawInputModifiers.None);
            OverviewPart h = t.HotHandle;
            bool v = t.IsMeasureValid;
            p.Window.MouseMove(new Point(OverviewX(t, 40), OverviewY), RawInputModifiers.None);
            OverviewPart c = t.HotHandle;

            Point at = new(OverviewX(t, 40), OverviewY);
            p.Window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
            p.Window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
            p.Window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
            p.Window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
            UiPump.Frames();
            return (h, v, c, p.Sync.Window);
        });

        await Assert.That(hot).IsEqualTo(OverviewPart.EndHandle);
        await Assert.That(valid).IsTrue();
        await Assert.That(cold).IsEqualTo(OverviewPart.None);
        await Assert.That(fitted).IsEqualTo(Window(0, 100));
    }

    [Test]
    public async Task A_drag_across_the_ruler_selects_a_band_and_the_release_zooms_to_it()
    {
        (bool selecting, long playheadDuring, TimeWindow zoomed, bool bandGone) = await TimelineControlTests.With(Demo(), Window(0, 100), p =>
        {
            TimelineControl t = p.Timeline;
            p.Sync.SetPlayhead(Ticks(5));
            Drag(p.Window, new Point(t.XOf(Ticks(50)), RulerY), new Point(t.XOf(Ticks(30)), RulerY), release: false);
            bool s = t.RulerSelection.IsSelecting;
            long playhead = p.Sync.Playhead;
            p.Window.MouseUp(new Point(t.XOf(Ticks(30)), RulerY), MouseButton.Left, RawInputModifiers.None);
            UiPump.Frames();
            return (s, playhead, p.Sync.Window, !t.RulerSelection.IsSelecting);
        });

        await Assert.That(selecting).IsTrue();
        // A drag is not a click: the playhead stays where it was.
        await Assert.That(playheadDuring).IsEqualTo(Ticks(5));
        await Assert.That(zoomed.Start).IsEqualTo(Ticks(30)).Within(Px);
        await Assert.That(zoomed.End).IsEqualTo(Ticks(50)).Within(Px);
        await Assert.That(bandGone).IsTrue();
    }

    [Test]
    public async Task A_click_on_the_ruler_moves_the_playhead_and_keeps_the_window()
    {
        (long playhead, TimeWindow window) = await TimelineControlTests.With(Demo(), Window(0, 100), p =>
        {
            TimelineControl t = p.Timeline;
            Point at = new(t.XOf(Ticks(42)), RulerY);
            p.Window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
            p.Window.MouseMove(at + new Point(2, 0), RawInputModifiers.LeftMouseButton);
            p.Window.MouseUp(at + new Point(2, 0), MouseButton.Left, RawInputModifiers.None);
            UiPump.Frames();
            return (p.Sync.Playhead, p.Sync.Window);
        });

        await Assert.That(playhead).IsEqualTo(Ticks(42)).Within(Px);
        await Assert.That(window).IsEqualTo(Window(0, 100));
    }

    [Test]
    public async Task Esc_cancels_a_handle_drag_and_a_ruler_selection()
    {
        (TimeWindow during, TimeWindow afterHandle, TimeWindow afterRuler, long playhead, bool band, bool dragging) =
            await TimelineControlTests.With(Demo(), Window(20, 60), p =>
            {
                TimelineControl t = p.Timeline;
                Drag(p.Window, new Point(OverviewX(t, 20), OverviewY), new Point(OverviewX(t, 5), OverviewY), release: false);
                TimeWindow d = p.Sync.Window;
                p.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                // Moves until the release do nothing more.
                p.Window.MouseMove(new Point(OverviewX(t, 2), OverviewY), RawInputModifiers.LeftMouseButton);
                p.Window.MouseUp(new Point(OverviewX(t, 2), OverviewY), MouseButton.Left, RawInputModifiers.None);
                UiPump.Frames();
                TimeWindow h = p.Sync.Window;

                p.Sync.SetPlayhead(Ticks(33));
                Drag(p.Window, new Point(t.XOf(Ticks(25)), RulerY), new Point(t.XOf(Ticks(45)), RulerY), release: false);
                bool shown = t.RulerSelection.IsSelecting;
                p.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                bool still = t.IsDragging;
                p.Window.MouseUp(new Point(t.XOf(Ticks(45)), RulerY), MouseButton.Left, RawInputModifiers.None);
                UiPump.Frames();
                return (d, h, p.Sync.Window, p.Sync.Playhead, shown && !t.RulerSelection.IsSelecting, still);
            });

        await Assert.That(during.Start).IsEqualTo(Ticks(5)).Within(Px);
        await Assert.That(afterHandle).IsEqualTo(Window(20, 60));
        await Assert.That(afterRuler).IsEqualTo(Window(20, 60));
        await Assert.That(playhead).IsEqualTo(Ticks(33));
        await Assert.That(band).IsTrue();
        await Assert.That(dragging).IsFalse();
    }

    [Test]
    public async Task Pointer_moves_over_the_axis_and_during_drags_keep_the_lanes_and_the_measure()
    {
        (bool sameRows, bool valid, bool hotSome) = await TimelineControlTests.With(Demo(pinned: false), Window(0, 100), p =>
        {
            TimelineControl t = p.Timeline;
            (LaneKey?, double, double)[] before = t.Layout.Rows.ToArray().Select(r => (r.Lane?.Key, r.Top, r.Height)).ToArray();
            bool v = true, hot = false;

            // Hover across the overview and the ruler.
            for (double x = 0; x < t.Bounds.Width; x += 5)
            {
                foreach (double y in new[] { OverviewY, RulerY })
                {
                    p.Window.MouseMove(new Point(x, y), RawInputModifiers.None);
                    v &= t.IsMeasureValid;
                    hot |= t.HotHandle != OverviewPart.None;
                }
            }

            // A ruler selection sweeping the whole track: the band is pixels only.
            p.Window.MouseDown(new Point(t.XOf(Ticks(10)), RulerY), MouseButton.Left, RawInputModifiers.None);
            for (double x = t.XOf(Ticks(10)); x < t.Bounds.Width; x += 7)
            {
                p.Window.MouseMove(new Point(x, RulerY + 40), RawInputModifiers.LeftMouseButton);
                v &= t.IsMeasureValid;
            }

            p.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            p.Window.MouseUp(new Point(t.Bounds.Width - 1, RulerY), MouseButton.Left, RawInputModifiers.None);
            UiPump.Frames();
            return (before.SequenceEqual(t.Layout.Rows.ToArray().Select(r => (r.Lane?.Key, r.Top, r.Height))), v, hot);
        });

        await Assert.That(hotSome).IsTrue();
        await Assert.That(valid).IsTrue();
        await Assert.That(sameRows).IsTrue();
    }

    [Test]
    public async Task Handle_pan_and_ruler_drags_allocate_nothing_per_move()
    {
        long allocated = await TimelineControlTests.With(Busy(256), Window(200, 400), p =>
        {
            TimelineControl t = p.Timeline;
            double x0 = TimelineControl.GutterWidth + t.RangeBounds.ToPixel(Ticks(200), t.Bounds.Width - TimelineControl.GutterWidth);
            double x1 = TimelineControl.GutterWidth + t.RangeBounds.ToPixel(Ticks(400), t.Bounds.Width - TimelineControl.GutterWidth);
            long total = 0;
            foreach ((Point press, double from) in new[]
                     {
                         (new Point(x0, OverviewY), x0), (new Point(x1, OverviewY), x1), (new Point((x0 + x1) / 2, OverviewY), (x0 + x1) / 2),
                         (new Point(t.XOf(Ticks(250)), RulerY), t.XOf(Ticks(250))),
                     })
            {
                p.Window.MouseDown(press, MouseButton.Left, RawInputModifiers.None);
                // Warm up (first band draw, first window change), then measure the control's own handling.
                for (int i = 0; i < 4; i++) t.DragTo(from + 10 + i);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200; i++) t.DragTo(from + 20 + (i % 40));
                total += GC.GetAllocatedBytesForCurrentThread() - before;
                t.CancelDrag();
                p.Window.MouseUp(press, MouseButton.Left, RawInputModifiers.None);
                UiPump.Frames();
            }

            return total;
        }, ui: static () => new QueuedUi());

        await Assert.That(allocated).IsEqualTo(0);
    }
}
