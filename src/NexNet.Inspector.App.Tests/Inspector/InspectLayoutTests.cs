using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Controls;
using NexNet.Inspector.App.Controls.PropertyTree;
using NexNet.Inspector.App.Controls.Timeline;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.Tests.Capture;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.App.Views.Inspect;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Tests.Inspector;

/// <summary>
/// The Inspect workspace: split / single panes, the OQ10 collapse (right pane first, then the inspector narrows), the
/// views wired to the active document, and the property tree's list (virtualized, hover, section toggles).
/// </summary>
[NotInParallel("avalonia")]
public sealed class InspectLayoutTests
{
    [Test]
    public async Task Single_and_split_toggle_the_right_pane_and_the_buttons_follow()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        ShellChromeViewModel chrome = new(documents);
        using InspectWorkspaceViewModel inspect = new(documents, chrome);

        await Assert.That(inspect.ShowsRightPane).IsTrue();
        await Assert.That(inspect.Left.SplitAction).IsEqualTo(PaneSplitAction.Single);
        await Assert.That(inspect.Right.SplitAction).IsEqualTo(PaneSplitAction.Close);
        await Assert.That(inspect.Left.View.Value).IsEqualTo(StreamPaneKind.Ladder);
        await Assert.That(inspect.Right.View.Value).IsEqualTo(StreamPaneKind.Hex);

        inspect.Left.SplitCommand!.Execute(null);
        await Assert.That(inspect.ShowsRightPane).IsFalse();
        await Assert.That(inspect.Left.SplitAction).IsEqualTo(PaneSplitAction.Split);

        inspect.Left.SplitCommand!.Execute(null);
        await Assert.That(inspect.ShowsRightPane).IsTrue();

        inspect.Right.SplitCommand!.Execute(null);
        await Assert.That(inspect.IsSplit).IsFalse();
        await Assert.That(inspect.Left.SplitAction).IsEqualTo(PaneSplitAction.Split);
    }

    [Test]
    public async Task Narrow_windows_close_the_right_pane_first_then_narrow_the_inspector_and_widening_restores_the_split()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        ShellChromeViewModel chrome = new(documents);
        using InspectWorkspaceViewModel inspect = new(documents, chrome);

        chrome.UpdateWidth(1180);
        await Assert.That(inspect.ShowsRightPane).IsFalse();
        await Assert.That(inspect.CanSplit).IsFalse();
        await Assert.That(inspect.Left.CanSplit).IsFalse();
        await Assert.That(inspect.InspectorWidth).IsEqualTo(324);
        await Assert.That(inspect.IsSplit).IsTrue(); // the user's choice is kept

        chrome.UpdateWidth(1120);
        await Assert.That(inspect.InspectorWidth).IsEqualTo(280);

        chrome.UpdateWidth(1440);
        await Assert.That(inspect.ShowsRightPane).IsTrue();
        await Assert.That(inspect.InspectorWidth).IsEqualTo(324);
        await Assert.That(inspect.Left.CanSplit).IsTrue();
    }

    [Test]
    public async Task Panes_keep_one_model_per_view_and_the_workspace_shows_only_in_Inspect_with_a_document()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        ShellChromeViewModel chrome = new(documents);
        using InspectWorkspaceViewModel inspect = new(documents, chrome);

        object ladder = inspect.Left.Content;
        inspect.Left.View.Value = StreamPaneKind.Events;
        await Assert.That(inspect.Left.Content).IsTypeOf<EventsViewModel>();
        await Assert.That(inspect.Right.Content).IsTypeOf<HexViewModel>();
        await Assert.That(ladder).IsTypeOf<LadderViewModel>();
        inspect.Left.View.Value = StreamPaneKind.Ladder;
        await Assert.That(inspect.Left.Content).IsSameReferenceAs(ladder);
        await Assert.That(inspect.Left.SyncState).IsEqualTo(PaneSyncState.Synced);
        inspect.Left.IsSynced = false;
        await Assert.That(inspect.Left.SyncState).IsEqualTo(PaneSyncState.Unsynced);
        await Assert.That(((LadderViewModel)ladder).IsSynced).IsFalse(); // the badge and the ladder agree
        inspect.Left.IsSynced = true;

        await Assert.That(inspect.IsShown).IsFalse();
        var (document, _) = await ChatFixture.OpenAsync(documents);
        await Assert.That(inspect.IsShown).IsTrue();
        await Assert.That(inspect.Timeline.Document).IsSameReferenceAs(document);
        await Assert.That(inspect.Inspector.Document).IsSameReferenceAs(document);
        chrome.Workspace.Value = Workspace.Compare;
        await Assert.That(inspect.IsShown).IsFalse();
    }

    [Test]
    public async Task The_window_lays_the_panes_out_and_collapses_them_by_width()
    {
        (MainWindow window, MainWindowViewModel model) = await HeadlessSession.Run(() =>
        {
            using ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(Avalonia.Platform.PlatformThemeVariant.Light), AppTheme.Light);
            MainWindowViewModel model = new(themes, store: null, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, null));
            InspectLiveScreen.Open(model);
            MainWindow window = new() { DataContext = model, Width = 1440, Height = 900 };
            window.Show();
            UiPump.Frames();
            return (window, model);
        });
        try
        {
            (bool Right, double Left, double Inspector, double TimelineBottom) Measure(double width) 
            {
                window.Width = width;
                UiPump.Frames();
                InspectView view = window.Inspect;
                return (view.RightPaneArea.IsEffectivelyVisible, view.LeftPane.Bounds.Width, view.InspectorArea.Bounds.Width,
                    view.TimelineArea.Bounds.Bottom);
            }

            var wide = await HeadlessSession.Run(() => Measure(1440));
            var closed = await HeadlessSession.Run(() => Measure(1180));
            var narrow = await HeadlessSession.Run(() => Measure(1120));
            var single = await HeadlessSession.Run(() =>
            {
                Measure(1440);
                model.Inspect.ClosePaneCommand.Execute(null);
                return Measure(1440);
            });
            bool shown = await HeadlessSession.Run(() => window.Inspect.IsEffectivelyVisible && !window.Home.IsEffectivelyVisible);

            await Assert.That(shown).IsTrue();
            await Assert.That(wide.Right).IsTrue();
            await Assert.That(wide.Inspector).IsEqualTo(324);
            await Assert.That(Math.Abs(wide.Left - ((1440 - 324 - 1) / 2.0))).IsLessThan(2);
            await Assert.That(wide.TimelineBottom).IsLessThanOrEqualTo((900 - 36) * InspectView.TimelineShare + 1);
            await Assert.That(closed.Right).IsFalse();
            await Assert.That(closed.Inspector).IsEqualTo(324);
            await Assert.That(closed.Left).IsEqualTo(1180 - 324);
            await Assert.That(narrow.Inspector).IsEqualTo(280);
            await Assert.That(narrow.Left).IsEqualTo(1120 - 280);
            await Assert.That(single.Right).IsFalse();
            await Assert.That(single.Left).IsEqualTo(1440 - 324);
        }
        finally
        {
            await HeadlessSession.Run(() =>
            {
                window.Close();
                return model.Documents.DisposeAsync().AsTask();
            });
        }
    }

    [Test]
    public async Task The_inspect_live_scenario_traces_SendAttachment_and_decodes_chunk_2()
    {
        var result = await HeadlessSession.Run(() =>
        {
            using ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(Avalonia.Platform.PlatformThemeVariant.Light), AppTheme.Light);
            MainWindowViewModel model = new(themes, store: null, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, null));
            InspectLiveScreen.Picks picks = InspectLiveScreen.Open(model);
            TimeWindow window = model.Documents.Sync.Window;
            bool gapInView;
            lock (picks.Document.Session!.Gate) gapInView = picks.Document.Session.Gaps.ToArray().Any(g => g.FromTicks >= window.Start && g.ToTicks <= window.End);
            InspectorContent? content = model.Inspect.Inspector.Content;
            bool pinned = model.Inspect.Timeline.Model.IsPinned(new LaneKey(LaneKind.Pipe, picks.Pipe));
            MessageTrace trace = model.Documents.Sync.Trace;
            bool traceInView = trace.Start > window.Start && trace.End < window.End;
            double ms = (window.End - window.Start) * 1000.0 / picks.Document.Frequency;
            model.Documents.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return (picks, content, gapInView, pinned, traceInView, ms);
        });

        await Assert.That(result.traceInView).IsTrue();
        await Assert.That(result.ms).IsGreaterThanOrEqualTo(50).And.IsLessThan(100);

        await Assert.That(result.content!.Frame).IsEqualTo(result.picks.Chunk);
        await Assert.That(result.content.Assembly!.Segments.Single(s => s.IsCurrent).Number).IsEqualTo(2);
        await Assert.That(result.gapInView).IsTrue();
        await Assert.That(result.pinned).IsTrue();
        await Assert.That(result.picks.Pipe).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task The_inspect_session_start_scenario_shows_the_first_50_ms_with_the_handshake_traced()
    {
        var result = await HeadlessSession.Run(() =>
        {
            using ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(Avalonia.Platform.PlatformThemeVariant.Light), AppTheme.Light);
            MainWindowViewModel model = new(themes, store: null, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, null));
            InspectSessionStartScreen.Picks picks = InspectSessionStartScreen.Open(model);
            TimeWindow window = model.Documents.Sync.Window;
            long start;
            lock (picks.Document.Session!.Gate) start = picks.Document.Session.StartTicks;
            double ms = (window.End - window.Start) * 1000.0 / picks.Document.Frequency;
            InspectorContent? content = model.Inspect.Inspector.Content;
            MessageTrace trace = model.Documents.Sync.Trace;
            (StreamPaneKind left, StreamPaneKind right) = (model.Inspect.Left.View.Value, model.Inspect.Right.View.Value);
            bool pinned = model.Inspect.Timeline.Model.IsPinned(new LaneKey(LaneKind.Session, 0));
            model.Documents.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return (picks, content, trace.Kind, window.Start - start, ms, left, right, pinned);
        });

        await Assert.That(result.content!.Frame).IsEqualTo(result.picks.Greeting);
        await Assert.That(result.content.IsHandshake).IsTrue();
        await Assert.That(result.content.Assembly!.Title).IsEqualTo("Handshake");
        await Assert.That(result.Kind).IsEqualTo(TraceKind.Handshake);
        await Assert.That(result.Item4).IsEqualTo(0L);
        await Assert.That(Math.Abs(result.ms - InspectSessionStartScreen.WindowMilliseconds)).IsLessThan(0.01);
        await Assert.That((result.left, result.right)).IsEqualTo((StreamPaneKind.Events, StreamPaneKind.Hex));
        await Assert.That(result.pinned).IsTrue();
    }

    [Test]
    public async Task The_tree_list_realizes_only_visible_rows_tracks_the_hovered_row_and_toggles_sections()
    {
        var result = await HeadlessSession.Run(() =>
        {
            PropertyTreeModel tree = new();
            tree.Section("Big", "10,000 fields");
            for (int i = 0; i < 10_000; i++) tree.Field("field " + i, "value " + i);
            tree.Complete();
            PropertyTreeView view = new() { ItemsSource = tree.Rows };
            Window window = new() { Content = view, Width = 324, Height = 220 };
            window.Show();
            UiPump.Frames();
            int realized = view.GetVisualDescendants().OfType<ListBoxItem>().Count();
            int made = tree.RowsMade;

            window.MouseMove(new Point(100, 40), RawInputModifiers.None);
            UiPump.Frames();
            string? hovered = view.HoveredRow?.Name;

            window.MouseDown(new Point(100, 12), MouseButton.Left);
            window.MouseUp(new Point(100, 12), MouseButton.Left);
            UiPump.Frames();
            int afterToggle = tree.Rows.Count;
            object? selected = view.SelectedItem;
            window.Close();
            return (realized, made, hovered, afterToggle, selected);
        });

        await Assert.That(result.realized).IsLessThan(30);
        await Assert.That(result.made).IsLessThan(60);
        await Assert.That(result.hovered).IsEqualTo("field 0");
        // The section row folds its fields and does not take the selection.
        await Assert.That(result.afterToggle).IsEqualTo(1);
        await Assert.That(result.selected).IsNull();
    }
}
