using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using NexNet.Inspector.App.Controls.Ladder;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Assembly;

namespace NexNet.Inspector.App.Tests.Ladder;

/// <summary>
/// A screenful of a long, traced, time-scaled ladder redrawn without change: the rows allocate nothing of their own —
/// text is shaped when a row is bound and kept, chips come from their caches, arrowheads are shared geometry, the dotted
/// bracket is plain rectangles. The only bytes are Skia's per glyph run (measured on the same context), and a rebind to
/// the same frames shapes nothing.
/// </summary>
[NotInParallel("avalonia")]
public sealed class LadderAllocationTests
{
    [Test]
    public async Task Redrawing_the_rows_in_view_allocates_nothing_of_their_own()
    {
        (long allocated, long perText, int texts, int rows, int shapes, int reshaped) = await LadderViewTests.With(LadderDataBuilder.Busy(50_000), p =>
        {
            p.View.Mode = LadderMode.TimeScaled;
            p.Sync.Select(Selection.OfEntity(EntityKind.Invocation, 2_000));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            p.View.ScrollIntoView(p.Model.GetTrace(p.Sync.Selection).First);
            UiPump.Frames();
            p.View.HoveredIndex = p.View.FirstRealized + 3;
            UiPump.Frames();

            Size size = p.View.Bounds.Size;
            using RenderTargetBitmap bitmap = new(new PixelSize((int)size.Width, (int)size.Height));
            using DrawingContext context = bitmap.CreateDrawingContext();
            IReadOnlyList<LadderRowControl> realized = p.View.RealizedRows;
            int textRuns = 0;
            foreach (LadderRowControl row in realized)
            {
                // Time, title and first op always; target, bytes and last op when the row has them; a letter per chip.
                textRuns += 3 + (LadderText.Detail(row.Row, null).Length > 0 ? 1 : 0) + (row.Row.Bytes >= 0 ? 1 : 0)
                    + (row.Row.LastOpId != row.Row.FirstOpId ? 1 : 0) + System.Numerics.BitOperations.PopCount((uint)row.Row.Flags);
            }

            // Twice to fill every cache (shaped text, chip letters, pens) and the renderer's pools.
            for (int pass = 0; pass < 2; pass++)
                foreach (LadderRowControl row in realized) row.Render(context);
            int shapedBefore = realized.Sum(r => r.Shapes);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < realized.Count; i++) realized[i].Render(context);
            long after = GC.GetAllocatedBytesForCurrentThread();

            // Binding the same frames again (a selection change elsewhere) reshapes nothing.
            p.View.HoveredIndex = -1;
            foreach (LadderRowControl row in realized) row.Render(context);
            int shapedAfter = realized.Sum(r => r.Shapes);

            ThemePalette palette = ThemeBrushes.For(p.View);
            using TextLayout label = new("4,096 B", new Typeface(palette.Mono), 10.5, palette[ThemeBrush.TextSecondary]);
            label.Draw(context, default);
            long calibrate = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) label.Draw(context, default);
            long text = (GC.GetAllocatedBytesForCurrentThread() - calibrate) / 100;
            return (after - before, text, textRuns, realized.Count, shapedBefore, shapedAfter - shapedBefore);
        }, height: 700);

        await Assert.That(rows).IsGreaterThan(10);
        await Assert.That(shapes).IsGreaterThan(0);
        await Assert.That(reshaped).IsEqualTo(0);
        await Assert.That(allocated).IsLessThanOrEqualTo(texts * perText).Because($"{allocated} B for {texts} glyph runs at {perText} B");
    }
}
