using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using NexNet.Inspector.App.Controls.Compare;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Services.Commands;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views.Compare;
using NexNet.Inspector.Core.Compare;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Tests.Compare;

/// <summary>
/// The drawn aligned view and the workspace view on the App's headless session: a click on a category's gutter folds
/// it, a click on a span picks its entity, a double-click opens it in Inspect, Ctrl+Wheel zooms both files around the
/// pointer and Wheel scrolls the rows; a redraw of the same view shapes nothing new; the window binds "Open in Compare"
/// and the open-recording dialog's comparer.
/// </summary>
[NotInParallel("avalonia")]
public sealed class CompareRowsControlTests
{
    private sealed record Probe(Window Window, CompareRowsControl Rows, CompareFixture Fixture);

    private static async Task<T> With<T>(Func<Probe, T> body, double height = 600)
    {
        CompareFixture fixture = await CompareFixture.OpenAsync();
        try
        {
            return await HeadlessSession.Run(() => UiPump.InTheme(ThemeVariant.Light, () =>
            {
                CompareRowsControl rows = new() { Model = fixture.Compare.Rows };
                Window host = new() { Width = 1200, Height = height, Content = rows };
                host.Show();
                UiPump.Frames();
                try
                {
                    return body(new Probe(host, rows, fixture));
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

    private static Point RowPoint(CompareRowsControl control, Func<CompareRow, bool> which, double x)
    {
        CompareRow row = control.Model!.Rows.ToArray().First(which);
        return new Point(x, control.YOf(row) + (row.Height / 2));
    }

    [Test]
    public async Task A_click_on_a_category_gutter_folds_it_and_a_click_on_a_span_picks_its_entity()
    {
        (bool folded, int picked, int expected, long playhead, long clicked) = await With(p =>
        {
            CompareRowsModel model = p.Rows.Model!;
            p.Window.MouseDown(RowPoint(p.Rows, r => r is { Kind: CompareRowKind.Expanded, Category: CompareCategory.Pipes }, 60), MouseButton.Left);
            p.Window.MouseUp(RowPoint(p.Rows, r => r is { Kind: CompareRowKind.Collapsed, Category: CompareCategory.Pipes }, 60), MouseButton.Left);
            UiPump.Frames();
            bool isFolded = !model.IsExpanded(CompareCategory.Pipes);

            // Expand Invocations and click the middle of a group's first span in file B.
            model.SetExpanded(CompareCategory.Invocations, true);
            CompareFixture.ShowGroups(model, CompareCategory.Invocations);
            UiPump.Frames();
            CompareSession session = model.Session!;
            CompareRow group = model.Rows.ToArray().First(r => r is { Kind: CompareRowKind.Group, Side: CompareSide.B, Category: CompareCategory.Invocations }
                && session.Matches[session.Groups[r.Group].FirstMatch].B.IsPresent);
            int match = session.Groups[group.Group].FirstMatch;
            EntityMatch entity = session.Matches[match];
            long middle = (session.GetStart(entity, CompareSide.B) + session.GetEnd(entity, CompareSide.B)) / 2;
            Point at = new(p.Rows.XOf(middle), p.Rows.YOf(group) + (group.Height / 2));
            CompareHit hit = p.Rows.HitTest(at);
            p.Rows.Click(model, hit);
            return (isFolded, model.Selected, match, model.Playhead, hit.Time);
        });

        using (Assert.Multiple())
        {
            await Assert.That(folded).IsTrue();
            await Assert.That(picked).IsEqualTo(expected);
            await Assert.That(playhead).IsEqualTo(clicked);
        }
    }

    [Test]
    public async Task A_double_click_on_the_pinned_row_asks_to_open_it()
    {
        (CompareOpenRequest? request, int selected) = await With(p =>
        {
            CompareRowsModel model = p.Rows.Model!;
            CompareSession session = model.Session!;
            int attachment = Capture.CompareScreen.FindInvocation(session, "SendAttachment");
            p.Fixture.Compare.Select(attachment);
            UiPump.Frames();
            CompareOpenRequest? asked = null;
            // The view-model would switch to Inspect: catch the request first.
            model.OpenRequested += r => asked = r;
            EntityMatch entity = session.Matches[attachment];
            long middle = (session.GetStart(entity, CompareSide.A) + session.GetEnd(entity, CompareSide.A)) / 2;
            Point at = RowPoint(p.Rows, r => r is { Kind: CompareRowKind.Pinned, Side: CompareSide.A }, p.Rows.XOf(middle));
            p.Window.MouseDown(at, MouseButton.Left);
            p.Window.MouseUp(at, MouseButton.Left);
            p.Window.MouseDown(at, MouseButton.Left);
            p.Window.MouseUp(at, MouseButton.Left);
            UiPump.Frames();
            return (asked, attachment);
        });

        await Assert.That(request).IsNotNull();
        await Assert.That(request!.Value.Side).IsEqualTo(CompareSide.A);
        await Assert.That(request.Value.Match).IsEqualTo(selected);
    }

    [Test]
    public async Task Ctrl_wheel_zooms_both_files_around_the_pointer_and_wheel_scrolls_the_rows()
    {
        (TimeWindow before, TimeWindow after, long anchor, long anchorAfter, double scroll, double maximum) = await With(p =>
        {
            CompareRowsModel model = p.Rows.Model!;
            model.SetExpanded(CompareCategory.Invocations, true);
            CompareFixture.ShowGroups(model, CompareCategory.Invocations);
            UiPump.Frames();
            Point point = new(CompareRowsControl.GutterWidth + 300, CompareRowsControl.RowsTop + 40);
            long at = p.Rows.TimeAt(point.X);
            TimeWindow start = model.Window;
            p.Window.MouseWheel(point, new Vector(0, 2), RawInputModifiers.Control);
            UiPump.Frames();
            long atAfter = p.Rows.TimeAt(point.X);
            p.Window.MouseWheel(point, new Vector(0, -1), RawInputModifiers.None);
            UiPump.Frames();
            return (start, model.Window, at, atAfter, p.Rows.ScrollOffset, p.Rows.ScrollMaximum);
        }, height: 300);

        using (Assert.Multiple())
        {
            await Assert.That(after.Duration).IsEqualTo((long)Math.Round(before.Duration / 1.5625)).Within(2);
            await Assert.That(anchorAfter).IsEqualTo(anchor).Within(before.Duration / 1000);
            await Assert.That(maximum).IsGreaterThan(0);
            await Assert.That(scroll).IsEqualTo(Math.Min(36, maximum));
        }
    }

    [Test]
    public async Task Redrawing_the_same_view_shapes_nothing_new_and_allocates_only_skia_glyph_runs()
    {
        (int cachedBefore, int cachedAfter, long allocated, long perText, int texts) = await With(p =>
        {
            CompareRowsModel model = p.Rows.Model!;
            p.Fixture.Compare.Select(Capture.CompareScreen.FindInvocation(model.Session!, "SendAttachment"));
            model.SetExpanded(CompareCategory.Invocations, true);
            model.SetPlayhead(model.Session!.Clock.AnchorTime);
            UiPump.Frames();
            Size size = p.Rows.Bounds.Size;
            using RenderTargetBitmap bitmap = new(new PixelSize((int)size.Width, (int)size.Height));
            using DrawingContext context = bitmap.CreateDrawingContext();
            p.Rows.Render(context);
            p.Rows.Render(context);
            int cached = p.Rows.CachedText;
            long before = GC.GetAllocatedBytesForCurrentThread();
            p.Rows.Render(context);
            long after = GC.GetAllocatedBytesForCurrentThread();

            // Skia's cost of this view's labels, each drawn once on the same context: the average per glyph run.
            p.Rows.DrawCachedText(context);
            long beforeText = GC.GetAllocatedBytesForCurrentThread();
            p.Rows.DrawCachedText(context);
            long text = (GC.GetAllocatedBytesForCurrentThread() - beforeText) / Math.Max(1, p.Rows.CachedText);
            return (cached, p.Rows.CachedText, after - before, text, p.Rows.TextDraws);
        });

        using (Assert.Multiple())
        {
            await Assert.That(cachedAfter).IsEqualTo(cachedBefore);
            await Assert.That(texts).IsGreaterThan(20);
            // The labels' glyph runs (some are drawn more often than the average one), the band's two dashed lines; nothing per row or span.
            await Assert.That(allocated).IsLessThanOrEqualTo((long)(perText * texts * 1.5) + 1024).Because($"{allocated} B for {texts} labels at {perText} B");
        }
    }

    [Test]
    public async Task The_window_binds_open_in_compare_and_the_session_comparer_and_shows_the_workspace()
    {
        // Indexed inline; the compare then lands on the UI thread the window's bindings live on.
        SwitchableUi ui = new(new TestUi());
        await using DocumentsViewModel documents = new(ui, null);
        await CompareFixture.OpenIndexedAsync(documents, "chat-client.nntl");
        FileDocumentViewModel second = await CompareFixture.OpenIndexedAsync(documents, "chat-server.nntl");

        (bool comparer, bool canBefore, bool isCompare, int ticked, bool secondTicked, bool hasSession, bool viewShown, int entityRows) =
            await HeadlessSession.Run(() =>
            {
                ui.Target = AvaloniaUiDispatcher.Instance;
                ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(Avalonia.Platform.PlatformThemeVariant.Light), AppTheme.Light);
                MainWindowViewModel model = new(themes, null, documents);
                model.Commands.Refresh();
                bool can = model.Commands.CanExecute(AppCommand.OpenInCompare);
                Views.MainWindow window = new() { DataContext = model, Width = 1440, Height = 900 };
                window.Show();
                model.Commands.CommandFor(AppCommand.OpenInCompare).Execute(null);
                Task computed = model.Compare.WhenComputedAsync();
                Capture.InspectLiveScreen.Pump(() => computed.IsCompleted);
                UiPump.Settle(200);
                CompareView view = window.Compare;
                int listed = view.EntityTree.GetRealizedContainers().Count();
                (bool, bool, bool, int, bool, bool, bool, int) result = (documents.SessionComparer is not null, can, model.Chrome.IsCompare,
                    documents.ComparedCount, second.IsIncludedInCompare, model.Compare.Session is not null, view.IsEffectivelyVisible, listed);
                window.Close();
                model.Compare.Dispose();
                Dispatcher.UIThread.RunJobs();
                return result;
            });

        using (Assert.Multiple())
        {
            await Assert.That(comparer).IsTrue();
            await Assert.That(canBefore).IsTrue();
            await Assert.That(isCompare).IsTrue();
            await Assert.That(ticked).IsEqualTo(2);
            await Assert.That(secondTicked).IsTrue();
            await Assert.That(hasSession).IsTrue();
            await Assert.That(viewShown).IsTrue();
            await Assert.That(entityRows).IsGreaterThan(5);
        }
    }
}
