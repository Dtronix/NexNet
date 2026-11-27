using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.Tests.Capture;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.App.Views.Compare;

namespace NexNet.Inspector.App.Tests.Shell;

/// <summary>
/// The workspace splitters (I40): the sizes they set are clamped, saved to settings.json and restored by the next
/// window; a double-click resets one; the inspector's dragged width combines with the OQ10 collapse rules; a real
/// pointer drag over the 7 px hit area resizes; Compare's sidebar and detail panel follow their splitters.
/// </summary>
[NotInParallel("avalonia")]
public sealed class PaneSplitterTests
{
    [Test]
    public async Task Pane_sizes_round_trip_through_the_settings_file()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.File("settings.json"));
        var sizes = new PaneSizesViewModel(store);
        sizes.Update(s => s with { TimelineHeight = 222, LeftShare = 0.3, InspectorWidth = 400, CompareSidebarWidth = 300, CompareDetailHeight = 260 }, save: true);

        var reloaded = new SettingsStore(store.Path);
        await Assert.That(reloaded.Load()).IsEqualTo(SettingsLoadOutcome.Loaded);
        var restored = new PaneSizesViewModel(reloaded).Sizes;
        await Assert.That(restored.TimelineHeight).IsEqualTo(222);
        await Assert.That(restored.LeftShare).IsEqualTo(0.3);
        await Assert.That(restored.InspectorWidth).IsEqualTo(400);
        await Assert.That(restored.CompareSidebarWidth).IsEqualTo(300);
        await Assert.That(restored.CompareDetailHeight).IsEqualTo(260);
        await Assert.That(File.ReadAllText(store.Path)).Contains("\"panes\"");

        // A double-click's reset is saved too.
        sizes.ResetInspector();
        sizes.ResetLeftShare();
        var again = new SettingsStore(store.Path);
        again.Load();
        await Assert.That(again.Current.Panes.InspectorWidth).IsNull();
        await Assert.That(again.Current.Panes.LeftShare).IsEqualTo(PaneLayoutPolicy.DefaultLeftShare);
    }

    [Test]
    public async Task A_hand_edited_file_is_clamped_and_a_missing_one_keeps_the_defaults()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        File.WriteAllText(path, "{ \"panes\": { \"leftShare\": 7, \"inspectorWidth\": 5, \"compareSidebarWidth\": -3, \"timelineHeight\": 1 } }");
        var store = new SettingsStore(path);
        await Assert.That(store.Load()).IsEqualTo(SettingsLoadOutcome.Loaded);
        var panes = store.Current.Panes;
        await Assert.That(panes.LeftShare).IsEqualTo(PaneLayoutPolicy.MaxLeftShare);
        await Assert.That(panes.InspectorWidth).IsEqualTo(PaneLayoutPolicy.MinInspectorWidth);
        await Assert.That(panes.CompareSidebarWidth).IsEqualTo(PaneLayoutPolicy.MinCompareSidebarWidth);
        await Assert.That(panes.TimelineHeight).IsEqualTo(PaneLayoutPolicy.MinTimelineHeight);
        await Assert.That(panes.CompareDetailHeight).IsEqualTo(PaneLayoutPolicy.DefaultCompareDetailHeight);

        var fresh = new SettingsStore(temp.File("none.json"));
        fresh.Load();
        await Assert.That(fresh.Current.Panes).IsEqualTo(new PaneSizes());
    }

    [Test]
    public async Task A_dragged_inspector_width_combines_with_the_collapse_rules()
    {
        await Assert.That(PaneLayoutPolicy.InspectorWidthFor(ShellLayoutState.Full, null)).IsEqualTo(324);
        await Assert.That(PaneLayoutPolicy.InspectorWidthFor(ShellLayoutState.Full, 420)).IsEqualTo(420);
        await Assert.That(PaneLayoutPolicy.InspectorWidthFor(ShellLayoutState.RightPaneClosed, 420)).IsEqualTo(420);
        await Assert.That(PaneLayoutPolicy.InspectorWidthFor(ShellLayoutState.InspectorNarrow, 420)).IsEqualTo(280);
        await Assert.That(PaneLayoutPolicy.InspectorWidthFor(ShellLayoutState.InspectorNarrow, 240)).IsEqualTo(240);
        await Assert.That(PaneLayoutPolicy.ClampTimeline(5000, 800)).IsEqualTo(800 - PaneLayoutPolicy.MinPanesHeight);
        await Assert.That(PaneLayoutPolicy.ClampInspector(1000, 1100)).IsEqualTo(PaneLayoutPolicy.MaxInspectorWidth);
        await Assert.That(PaneLayoutPolicy.ShareFor(50, 1000)).IsEqualTo(0.22);
    }

    [Test]
    public async Task Dragging_the_inspect_splitters_resizes_saves_and_a_new_window_restores_them()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.File("settings.json"));
        var first = await HeadlessSession.Run(() =>
        {
            var (window, model, themes) = Open(store);
            try
            {
                var view = window.Inspect;
                double inspector = view.InspectorArea.Bounds.Width;
                view.InspectorSplitter.BeginDrag(new Point(500, 10));
                view.InspectorSplitter.MoveDrag(new Point(400, 10));
                UiPump.Frames();
                double during = view.InspectorArea.Bounds.Width;
                bool savedDuring = store.Current.Panes.InspectorWidth is not null;
                view.InspectorSplitter.EndDrag();

                double timeline = view.TimelineArea.Bounds.Height;
                view.TimelineSplitter.BeginDrag(new Point(10, 100));
                view.TimelineSplitter.MoveDrag(new Point(10, 160));
                view.TimelineSplitter.EndDrag();

                double panes = view.Panes.Bounds.Width;
                view.PanesSplitter.BeginDrag(new Point(100, 10));
                view.PanesSplitter.MoveDrag(new Point(100 - (panes / 4), 10));
                view.PanesSplitter.EndDrag();
                UiPump.Frames();
                return (inspector, during, savedDuring, After: view.InspectorArea.Bounds.Width, timeline, TimelineAfter: view.TimelineArea.Bounds.Height,
                    panes, Left: view.LeftPane.Bounds.Width, Bound: model.Inspect.InspectorWidth);
            }
            finally
            {
                window.Close();
                themes.Dispose();
            }
        });

        await Assert.That(first.inspector).IsEqualTo(324);
        await Assert.That(first.during).IsEqualTo(424);
        await Assert.That(first.savedDuring).IsFalse().Because("a drag saves once, when it ends");
        await Assert.That(first.After).IsEqualTo(424);
        await Assert.That(first.TimelineAfter).IsEqualTo(first.timeline + 60);
        await Assert.That(Math.Abs(first.Left - (first.panes / 4))).IsLessThan(2);
        await Assert.That(store.Current.Panes.InspectorWidth).IsEqualTo(424);
        await Assert.That(store.Current.Panes.TimelineHeight).IsEqualTo(first.timeline + 60);
        await Assert.That(store.Current.Panes.LeftShare).IsEqualTo(0.25);

        // The next window starts with the saved sizes; a double-click gives the inspector its default back.
        var reloaded = new SettingsStore(store.Path);
        reloaded.Load();
        var second = await HeadlessSession.Run(() =>
        {
            var (window, model, themes) = Open(reloaded);
            try
            {
                var view = window.Inspect;
                double restored = view.InspectorArea.Bounds.Width;
                double timeline = view.TimelineArea.Bounds.Height;
                double left = view.LeftPane.Bounds.Width / view.Panes.Bounds.Width;

                // Narrower than 1150 px: the inspector narrows to 280 whatever was dragged, and gets it back after.
                model.Chrome.UpdateWidth(1120);
                UiPump.Frames();
                double narrow = model.Inspect.InspectorWidth;
                model.Chrome.UpdateWidth(1440);
                UiPump.Frames();
                double wide = model.Inspect.InspectorWidth;

                view.InspectorSplitter.RequestReset();
                UiPump.Frames();
                return (restored, timeline, left, narrow, wide, Reset: view.InspectorArea.Bounds.Width);
            }
            finally
            {
                window.Close();
                themes.Dispose();
            }
        });

        await Assert.That(second.restored).IsEqualTo(424);
        await Assert.That(second.timeline).IsEqualTo(first.TimelineAfter);
        await Assert.That(Math.Abs(second.left - 0.25)).IsLessThan(0.01);
        await Assert.That(second.narrow).IsEqualTo(280);
        await Assert.That(second.wide).IsEqualTo(424);
        await Assert.That(second.Reset).IsEqualTo(324);
        await Assert.That(reloaded.Current.Panes.InspectorWidth).IsNull();
    }

    [Test]
    public async Task A_pointer_drag_over_the_hit_area_moves_the_boundary_and_a_double_click_resets_it()
    {
        var result = await HeadlessSession.Run(() =>
        {
            var (window, _, themes) = Open(null);
            try
            {
                var view = window.Inspect;
                var splitter = view.InspectorSplitter;
                // 2 px left of the line: inside the 7 px hit area, outside the 1 px border.
                Point at = splitter.TranslatePoint(new Point(1, 200), window)!.Value;
                window.MouseMove(at, RawInputModifiers.None);
                UiPump.Frames();
                bool hover = splitter.ShowsLine;
                window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
                window.MouseMove(at + new Point(-60, 0), RawInputModifiers.LeftMouseButton);
                UiPump.Frames();
                bool dragging = splitter.IsDragging;
                window.MouseUp(at + new Point(-60, 0), MouseButton.Left, RawInputModifiers.None);
                UiPump.Frames();
                double dragged = view.InspectorArea.Bounds.Width;

                Point line = splitter.TranslatePoint(new Point(3, 200), window)!.Value;
                window.MouseDown(line, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(line, MouseButton.Left, RawInputModifiers.None);
                window.MouseDown(line, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(line, MouseButton.Left, RawInputModifiers.None);
                UiPump.Frames();
                return (hover, dragging, dragged, Reset: view.InspectorArea.Bounds.Width, Width: splitter.Bounds.Width);
            }
            finally
            {
                window.Close();
                themes.Dispose();
            }
        });

        await Assert.That(result.Width).IsEqualTo(PaneLayoutPolicy.SplitterHitSize);
        await Assert.That(result.hover).IsTrue();
        await Assert.That(result.dragging).IsTrue();
        await Assert.That(result.dragged).IsEqualTo(384);
        await Assert.That(result.Reset).IsEqualTo(324);
    }

    [Test]
    public async Task Compare_sidebar_and_detail_panel_follow_their_splitters()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.File("settings.json"));
        var result = await HeadlessSession.Run(() =>
        {
            using ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(Avalonia.Platform.PlatformThemeVariant.Light), AppTheme.Light);
            MainWindowViewModel model = new(themes, store, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, store));
            CompareView view = new() { DataContext = model.Compare };
            Avalonia.Controls.Window host = new() { Content = view, Width = 1440, Height = 900 };
            host.Show();
            try
            {
                UiPump.Frames();
                view.SetSidebarWidth(300, save: true);
                view.SetDetailHeight(260, save: true);
                UiPump.Frames();
                double sidebar = view.Layout.ColumnDefinitions[0].Width.Value;
                double detail = view.Aligned.RowDefinitions[3].Height.Value;
                view.SidebarSplitter.RequestReset();
                view.DetailSplitter.RequestReset();
                UiPump.Frames();
                return (sidebar, detail, Saved: (store.Current.Panes.CompareSidebarWidth, store.Current.Panes.CompareDetailHeight),
                    ResetSidebar: view.Layout.ColumnDefinitions[0].Width.Value, ResetDetail: view.Aligned.RowDefinitions[3].Height.Value);
            }
            finally
            {
                host.Close();
            }
        });

        await Assert.That(result.sidebar).IsEqualTo(300);
        await Assert.That(result.detail).IsEqualTo(260);
        await Assert.That(result.ResetSidebar).IsEqualTo(PaneLayoutPolicy.DefaultCompareSidebarWidth);
        await Assert.That(result.ResetDetail).IsEqualTo(PaneLayoutPolicy.DefaultCompareDetailHeight);
        await Assert.That(result.Saved).IsEqualTo((PaneLayoutPolicy.DefaultCompareSidebarWidth, PaneLayoutPolicy.DefaultCompareDetailHeight));
    }

    private static (MainWindow Window, MainWindowViewModel Model, ThemeService Themes) Open(SettingsStore? store)
    {
        ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(Avalonia.Platform.PlatformThemeVariant.Light), AppTheme.Light);
        MainWindowViewModel model = new(themes, store, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, store));
        InspectLiveScreen.Open(model);
        MainWindow window = new() { DataContext = model, Width = 1440, Height = 900 };
        window.Inspect.Timeline.Layout.Animator.Enabled = false;
        window.Show();
        UiPump.Frames();
        return (window, model, themes);
    }
}
