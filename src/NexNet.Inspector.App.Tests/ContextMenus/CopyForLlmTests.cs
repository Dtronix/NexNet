using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Controls.DataTable;
using NexNet.Inspector.App.Controls.Timeline;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Services.Commands;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.Tests.Capture;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.App.Views.Inspect;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Tests.ContextMenus;

/// <summary>
/// Copy for LLM (I50, D6-E): the content menu's and the timeline menu's "Copy for LLM ▸" (Selection, Traced message,
/// Current view, Scope) and the app menu's command put the inspector's text format on the clipboard (header first,
/// within ~4k tokens), clicked with the pointer as a user would.
/// </summary>
[NotInParallel("avalonia")]
public sealed class CopyForLlmTests
{
    [Test]
    public async Task Each_kind_copies_its_text_and_is_offered_only_when_there_is_one()
    {
        var result = await InWindow(null, (window, model, _) =>
        {
            SyncBus sync = model.Documents.Sync;
            CaptureSession session = model.Inspect.Session!;
            string? copied = null;
            CopyForLlmViewModel llm = new(session, sync, text =>
            {
                copied = text;
                return Task.CompletedTask;
            });
            bool[] before = [.. LlmClip.Kinds.Select(llm.CanCopy)];
            string? id = LlmClip.SelectionId(session, sync.Selection);
            string selection = llm.Text(LlmClipKind.Selection)!;
            string trace = llm.Text(LlmClipKind.Trace)!;
            string view = llm.Text(LlmClipKind.View)!;
            LlmClipKind fallback = LlmClip.Default(session, sync);
            Task<bool> copying = llm.CopyDefaultAsync();
            UiPump.Frames();
            bool scoped = model.Documents.FilterToView();
            UiPump.Frames();
            string scope = new CopyForLlmViewModel(session, sync, null).Text(LlmClipKind.Scope)!;
            bool canScope = llm.CanCopy(LlmClipKind.Scope);
            sync.Select(Selection.None);
            UiPump.Frames();
            bool afterClear = llm.CanCopy(LlmClipKind.Selection);
            LlmClipKind cleared = LlmClip.Default(session, sync);
            bool noClipboard = new CopyForLlmViewModel(session, sync, null).CanCopy(LlmClipKind.View);
            return (before, id, selection, trace, view, fallback, Copied: copying.IsCompleted && copying.Result, copied, scoped, scope, canScope,
                afterClear, cleared, noClipboard);
        });

        await Assert.That(result.before).IsEquivalentTo(new[] { true, true, true, false }).Because("no scope is set yet");
        await Assert.That(result.id).StartsWith("#");
        string[] selection = result.selection.Split('\n');
        await Assert.That(selection[0]).StartsWith("@0x");
        await Assert.That(selection[1]).Contains(result.id!);
        await Assert.That(result.selection).Contains("SendAttachment");
        await Assert.That(result.trace.Split('\n')[0]).StartsWith("@0x");
        await Assert.That(result.trace).Contains("frag");
        await Assert.That(result.view.Split('\n')[1]).StartsWith("clip view +");
        await Assert.That(result.view.Split('\n', StringSplitOptions.RemoveEmptyEntries).Sum(l => Core.Text.TextBudget.Estimate(l)))
            .IsLessThanOrEqualTo(Core.Text.TextClip.DefaultMaxTokens);
        await Assert.That(result.fallback).IsEqualTo(LlmClipKind.Selection);
        await Assert.That(result.Copied).IsTrue();
        await Assert.That(result.copied).IsEqualTo(result.selection);
        await Assert.That(result.scoped).IsTrue();
        await Assert.That(result.scope.Split('\n')[1]).StartsWith("clip scope +");
        await Assert.That(result.canScope).IsTrue();
        await Assert.That(result.afterClear).IsFalse();
        await Assert.That(result.cleared).IsEqualTo(LlmClipKind.View);
        await Assert.That(result.noClipboard).IsFalse();
    }

    [Test]
    public async Task An_Events_row_menu_copies_the_selection_for_an_LLM_by_pointer()
    {
        var result = await InWindow(StreamPaneKind.Events, (window, model, _) =>
        {
            UiPump.Settle();
            EventsView events = window.Inspect.GetVisualDescendants().OfType<EventsView>().First();
            DataTableControl table = events.Grid;
            int index = Enumerable.Range(0, 4000).First(i => table.ContainerFromIndex(i) is { } c && Visible(c, table, window));
            Control row = table.ContainerFromIndex(index)!;
            Point at = row.TranslatePoint(new Point(40, row.Bounds.Height / 2), window)!.Value;
            window.MouseDown(at, MouseButton.Right, RawInputModifiers.None);
            window.MouseUp(at, MouseButton.Right, RawInputModifiers.None);
            UiPump.Frames();
            ContextMenu menu = window.Inspect.ContentMenu!;
            string described = Describe(menu);
            MenuItem parent = ItemOf(menu, "Copy for LLM");
            PointerClick(parent);
            string submenu = Describe(parent);
            bool subOpen = parent.IsSubMenuOpen;
            Pump(window.Clipboard!.SetTextAsync(""));
            PointerClick(ItemOf(parent, "Selection"));
            string? copied = ReadClipboard(window);
            string? expected = LlmClip.Build(LlmClipKind.Selection, model.Inspect.Session!, model.Documents.Sync);
            return (described, submenu, subOpen, copied, expected, MenuOpen: menu.IsOpen);
        });

        await Assert.That(result.described).Contains("| Copy | Copy value | Copy row as TSV | Copy for LLM | — | Copy as hex |");
        await Assert.That(result.subOpen).IsTrue();
        await Assert.That(result.submenu).IsEqualTo("Selection | Traced message | Current view | Scope (off)");
        await Assert.That(result.expected).IsNotNull();
        await Assert.That(result.copied).IsEqualTo(result.expected);
        await Assert.That(result.copied!).StartsWith("@0x");
        await Assert.That(result.MenuOpen).IsFalse();
    }

    [Test]
    public async Task The_timeline_menu_copies_the_current_view_and_the_scope_by_pointer()
    {
        var result = await InWindow(null, (window, model, picks) =>
        {
            TimelineControl timeline = window.Inspect.Timeline;
            LaneKey pipe = new(LaneKind.Pipe, picks.Pipe);
            LayoutRow row = timeline.Layout.Rows.ToArray().First(r => r.Lane?.Key == pipe);
            Point point = new(60, timeline.LanesTop + row.Top + (row.Height / 2) - timeline.LaneScroll);
            void OpenMenu()
            {
                Point at = timeline.TranslatePoint(point, window)!.Value;
                window.MouseDown(at, MouseButton.Right, RawInputModifiers.None);
                window.MouseUp(at, MouseButton.Right, RawInputModifiers.None);
                UiPump.Frames();
            }

            OpenMenu();
            ContextMenu menu = window.Inspect.TimelineMenu!;
            string described = Describe(menu);
            MenuItem parent = ItemOf(menu, "Copy for LLM");
            PointerClick(parent);
            PointerClick(ItemOf(parent, "Current view"));
            string? view = ReadClipboard(window);

            model.Documents.FilterToView();
            UiPump.Frames();
            OpenMenu();
            parent = ItemOf(window.Inspect.TimelineMenu!, "Copy for LLM");
            PointerClick(parent);
            string submenu = Describe(parent);
            PointerClick(ItemOf(parent, "Scope"));
            string? scope = ReadClipboard(window);
            return (described, view, submenu, scope);
        });

        await Assert.That(result.described).EndsWith("| — | Filter to this range | Export this range… | Copy for LLM");
        await Assert.That(result.view).IsNotNull();
        string[] view = result.view!.Split('\n');
        await Assert.That(view[0]).StartsWith("@0x");
        await Assert.That(view[1]).StartsWith("clip view +");
        await Assert.That(result.submenu).IsEqualTo("Selection | Traced message | Current view | Scope");
        await Assert.That(result.scope!.Split('\n')[1]).StartsWith("clip scope +");
    }

    [Test]
    public async Task The_app_menu_item_copies_the_selection_by_pointer_and_is_a_registry_command()
    {
        var result = await InWindow(null, (window, model, _) =>
        {
            CommandDefinition definition = CommandRegistry.Get(AppCommand.CopyForLlm);
            bool can = model.Commands.CanExecute(AppCommand.CopyForLlm);
            model.IsAppMenuOpen = true;
            UiPump.Frames();
            MenuItem item = window.AppMenu.CopyLlmItem;
            string header = (string)item.Header!;
            Pump(window.Clipboard!.SetTextAsync(""));
            PointerClick(item);
            UiPump.Frames();
            string? copied = ReadClipboard(window);
            string? expected = LlmClip.Build(LlmClipKind.Selection, model.Inspect.Session!, model.Documents.Sync);
            return (definition.Title, definition.Group, can, header, copied, expected, Open: model.IsAppMenuOpen);
        });

        await Assert.That(result.Title).IsEqualTo("Copy for LLM");
        await Assert.That(result.Group).IsEqualTo(CommandGroup.File);
        await Assert.That(result.can).IsTrue();
        await Assert.That(result.header).IsEqualTo("Copy for LLM");
        await Assert.That(result.copied).IsEqualTo(result.expected);
        await Assert.That(result.Open).IsFalse();
    }

    // Opens the chat fixture in a shown window and runs body, all in one dispatch: the headless platform's pointer input
    // reaches a menu in the window's overlay layer only within the dispatch that showed the window.
    private static Task<T> InWindow<T>(StreamPaneKind? left, Func<MainWindow, MainWindowViewModel, InspectLiveScreen.Picks, T> body) =>
        HeadlessSession.Run(() =>
        {
            using ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(Avalonia.Platform.PlatformThemeVariant.Light), AppTheme.Light);
            MainWindowViewModel model = new(themes, store: null, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, null));
            InspectLiveScreen.Picks picks = InspectLiveScreen.Open(model);
            MainWindow window = new() { DataContext = model, Width = 1440, Height = 900 };
            window.Inspect.Timeline.Layout.Animator.Enabled = false;
            if (left is { } kind) model.Inspect.Left.View.Value = kind;
            window.Show();
            UiPump.Frames();
            try
            {
                return body(window, model, picks);
            }
            finally
            {
                window.Close();
                Task closing = model.Documents.DisposeAsync().AsTask();
                Pump(closing);
            }
        });

    // A real left click on a menu item, sent to the window (whose overlay layer hosts the menus): the pointer travels in
    // from above and hovers, then presses (a parent opens its submenu on the press) and releases (an item runs).
    private static void PointerClick(Control target)
    {
        Window window = (Window)(TopLevel.GetTopLevel(target) ?? throw new InvalidOperationException("not shown: " + Text((target as MenuItem)?.Header)));
        Point at = target.TranslatePoint(new Point(20, target.Bounds.Height / 2), window)!.Value;
        window.MouseMove(new Point(at.X + 5, at.Y - 40));
        UiPump.Frames();
        window.MouseMove(at);
        UiPump.Settle(700);
        window.MouseDown(at, MouseButton.Left);
        UiPump.Frames();
        window.MouseUp(at, MouseButton.Left);
        UiPump.Settle(300);
        if (target is MenuItem { HasSubMenu: true, IsSubMenuOpen: false } parent)
            throw new InvalidOperationException("the press did not open " + Text(parent.Header));
    }

    private static bool Visible(Visual control, Visual viewport, Window window)
    {
        Point middle = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        Point top = viewport.TranslatePoint(default, window)!.Value;
        return middle.Y > top.Y + 30 && middle.Y < top.Y + viewport.Bounds.Height - 10;
    }

    private static void Pump(Task task)
    {
        for (int i = 0; i < 200 && !task.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
    }

    private static string? ReadClipboard(Window window)
    {
        Task<string?> reading = window.Clipboard!.TryGetTextAsync();
        Pump(reading);
        return reading.IsCompleted ? reading.Result : null;
    }

    private static string Text(object? header) => header switch
    {
        string s => s,
        TextBlock { Inlines: { } inlines } => string.Concat(inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)),
        TextBlock t => t.Text ?? "",
        _ => "",
    };

    private static string Describe(ItemsControl menu) => string.Join(" | ", menu.Items.Select(i => i switch
    {
        Separator => "—",
        MenuItem m => (m.IsChecked ? "✓" : "") + Text(m.Header) + (m.IsEnabled ? "" : " (off)"),
        _ => "?",
    }));

    private static MenuItem ItemOf(ItemsControl menu, string header) => menu.Items.OfType<MenuItem>().First(m => Text(m.Header) == header);
}
