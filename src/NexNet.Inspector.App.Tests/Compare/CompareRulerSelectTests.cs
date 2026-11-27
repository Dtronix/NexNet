using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using NexNet.Inspector.App.Controls.Compare;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Tests.Compare;

/// <summary>
/// The ruler drag-select in Compare, as the timeline's (D1, the shared <see cref="Controls.Timeline.RulerSelection"/>):
/// a drag across the ruler shows the band and its release zooms both files to the range; a click still moves the
/// playhead; Esc cancels the band and puts the playhead back; the band never invalidates a measure.
/// </summary>
[NotInParallel("avalonia")]
public sealed class CompareRulerSelectTests
{
    private const double RulerY = CompareRowsControl.RulerHeight / 2;

    private static async Task<T> With<T>(Func<Window, CompareRowsControl, T> body)
    {
        CompareFixture fixture = await CompareFixture.OpenAsync();
        try
        {
            return await HeadlessSession.Run(() => UiPump.InTheme(ThemeVariant.Light, () =>
            {
                CompareRowsControl rows = new() { Model = fixture.Compare.Rows };
                Window host = new() { Width = 1200, Height = 600, Content = rows };
                host.Show();
                UiPump.Frames();
                try
                {
                    return body(host, rows);
                }
                finally
                {
                    host.Close();
                    Dispatcher.UIThread.RunJobs();
                }
            }));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Test]
    public async Task A_drag_across_the_compare_ruler_zooms_to_the_range_and_keeps_the_playhead()
    {
        (bool selecting, bool valid, long playheadBefore, long playheadDuring, TimeWindow expected, TimeWindow zoomed) = await With((window, rows) =>
        {
            CompareRowsModel model = rows.Model!;
            double from = CompareRowsControl.GutterWidth + 500, to = CompareRowsControl.GutterWidth + 200;
            TimeWindow want = TimeWindow.Between(rows.TimeAt(from), rows.TimeAt(to), TimeWindow.MinSpan(TimeSpan.TicksPerSecond), model.Bounds);
            long before = model.Playhead;
            window.MouseDown(new Point(from, RulerY), MouseButton.Left, RawInputModifiers.None);
            bool v = true;
            for (int i = 1; i <= 10; i++)
            {
                window.MouseMove(new Point(from + ((to - from) * i / 10), RulerY + 100), RawInputModifiers.LeftMouseButton);
                v &= rows.IsMeasureValid;
            }

            bool s = rows.RulerSelection.IsSelecting;
            long during = model.Playhead;
            window.MouseUp(new Point(to, RulerY), MouseButton.Left, RawInputModifiers.None);
            UiPump.Frames();
            return (s, v, before, during, want, model.Window);
        });

        await Assert.That(selecting).IsTrue();
        await Assert.That(valid).IsTrue();
        await Assert.That(playheadDuring).IsEqualTo(playheadBefore);
        await Assert.That(zoomed).IsEqualTo(expected);
        await Assert.That(expected.Duration).IsGreaterThan(0);
    }

    [Test]
    public async Task A_click_on_the_compare_ruler_moves_the_playhead_and_Esc_cancels_a_drag()
    {
        (long clicked, long expected, TimeWindow windowBefore, TimeWindow afterClick, TimeWindow afterEsc, long playheadAfterEsc, bool band) =
            await With((window, rows) =>
            {
                CompareRowsModel model = rows.Model!;
                TimeWindow start = model.Window;
                Point at = new(CompareRowsControl.GutterWidth + 300, RulerY);
                long want = rows.TimeAt(at.X);
                window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
                UiPump.Frames();
                long playhead = model.Playhead;
                TimeWindow click = model.Window;

                window.MouseDown(new Point(CompareRowsControl.GutterWidth + 100, RulerY), MouseButton.Left, RawInputModifiers.None);
                window.MouseMove(new Point(CompareRowsControl.GutterWidth + 400, RulerY), RawInputModifiers.LeftMouseButton);
                bool shown = rows.RulerSelection.IsSelecting;
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                window.MouseUp(new Point(CompareRowsControl.GutterWidth + 400, RulerY), MouseButton.Left, RawInputModifiers.None);
                UiPump.Frames();
                return (playhead, want, start, click, model.Window, model.Playhead, shown && !rows.RulerSelection.IsPressed);
            });

        await Assert.That(clicked).IsEqualTo(expected);
        await Assert.That(afterClick).IsEqualTo(windowBefore);
        await Assert.That(band).IsTrue();
        await Assert.That(afterEsc).IsEqualTo(windowBefore);
        await Assert.That(playheadAfterEsc).IsEqualTo(clicked);
    }
}
