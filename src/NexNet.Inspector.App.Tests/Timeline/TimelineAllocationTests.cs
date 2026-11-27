using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.App.Controls.Timeline;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Assembly;
using static NexNet.Inspector.App.Tests.Timeline.TimelineDataBuilder;

namespace NexNet.Inspector.App.Tests.Timeline;

/// <summary>
/// A busy window (over a hundred thousand marks, a trace, a gap, a hovered lane, labels) redrawn without change: the
/// timeline itself allocates nothing — marks come from binary search, labels from the shaped-text cache, pens from the
/// palette, badges and ruler labels from their caches, the gap hatch from plain strokes. The only bytes are Skia's own
/// per glyph run (measured here on the same context, whoever draws text pays them), and the marks drawn are those in
/// view, not those captured.
/// </summary>
[NotInParallel("avalonia")]
public sealed class TimelineAllocationTests
{
    [Test]
    public async Task Redrawing_a_busy_window_allocates_nothing_of_its_own_and_draws_only_what_is_in_view()
    {
        (long allocated, long perText, int texts, int drawn, int marks) = await TimelineControlTests.With(
            Busy(40_000, durationMs: 10_000), Window(7_900, 8_350), p =>
            {
                p.Sync.Select(Selection.OfEntity(EntityKind.Invocation, 3_990), Ticks(7_980));
                p.Sync.SetPlayhead(Ticks(8_150));
                p.Timeline.HoveredLane = new LaneKey(LaneKind.InboundFrames, 0);
                p.Timeline.InvalidateMeasure();
                UiPump.Frames();
                Size size = p.Timeline.Bounds.Size;
                using RenderTargetBitmap bitmap = new(new PixelSize((int)size.Width, (int)size.Height));
                using DrawingContext context = bitmap.CreateDrawingContext();

                // Twice to fill every cache (shaped labels, pens, the overview's buckets) and the renderer's pools.
                p.Timeline.Render(context);
                p.Timeline.Render(context);

                // The best of a few renders. A render costs the same bytes every time (1,504 B in each of 5 renders of 62
                // runs under load), yet one render in the full suite counted 3,296 B: the same transient, not the
                // timeline's, as in FuzzyMatcherTests (I48b). A render that allocated per mark or label would do it
                // every time.
                long allocated = long.MaxValue;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    p.Timeline.Render(context);
                    long one = GC.GetAllocatedBytesForCurrentThread() - before;
                    allocated = Math.Min(allocated, one);
                }

                // Skia's cost of one cached label, drawn the same way on the same context.
                ThemePalette palette = ThemeBrushes.For(p.Timeline);
                using TextLayout label = new("16,384", new Typeface(palette.Ui), 8.5, palette[ThemeBrush.TextOnAccent]);
                label.Draw(context, default);
                long calibrate = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 100; i++) label.Draw(context, default);
                long text = (GC.GetAllocatedBytesForCurrentThread() - calibrate) / 100;
                return (allocated, text, p.Timeline.TextDraws, p.Timeline.MarksDrawn, p.Model.Lanes.Sum(l => l.Count));
            }, height: 700);

        await Assert.That(marks).IsGreaterThan(100_000);
        await Assert.That(texts).IsGreaterThan(0);
        await Assert.That(allocated).IsLessThanOrEqualTo(texts * perText).Because($"{allocated} B for {texts} glyph runs at {perText} B");

        // A 4.5 % window of the capture, and blocks closer than a pixel are skipped.
        await Assert.That(drawn).IsGreaterThan(0).And.IsLessThan(marks / 20);
    }
}
