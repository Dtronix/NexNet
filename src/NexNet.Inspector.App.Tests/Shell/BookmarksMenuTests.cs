using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Services.Commands;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.Tests.Capture;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.App.Views.Shell;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Tests.Shell;

/// <summary>
/// The title bar's ★ bookmarks (I44, D4) through real keys and clicks in the main window: Ctrl+Shift+B adds the view
/// and edits its name in the dropdown, the dropdown lists name, range and scope badge, a click or Alt+1…9 jumps, F2
/// renames in place, ✕ and Delete delete.
/// </summary>
[NotInParallel("avalonia")]
public sealed class BookmarksMenuTests
{
    private static T Find<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException(name + " not found");

    private static List<T> FindAll<T>(Visual root, string name)
        where T : Control =>
        [.. root.GetVisualDescendants().OfType<T>().Where(c => c.Name == name && c.IsEffectivelyVisible)];

    private static void Click(Window window, Control target)
    {
        Point at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)
                   ?? throw new InvalidOperationException(target.Name + " is not in the window");
        window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
        UiPump.Frames();
    }

    private static void Settle()
    {
        InspectLiveScreen.Pump(static () => true);
        UiPump.Frames();
    }

    private static (MainWindow Window, MainWindowViewModel Model, InspectLiveScreen.Picks Picks, ThemeService Themes) Open()
    {
        ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(), AppTheme.Light);
        MainWindowViewModel model = new(themes, null, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, null));
        InspectLiveScreen.Picks picks = InspectLiveScreen.Open(model);
        MainWindow window = new() { DataContext = model };
        window.Inspect.Timeline.Layout.Animator.Enabled = false;
        window.Show();
        UiPump.Frames();
        return (window, model, picks, themes);
    }

    private static TimeWindow Quarter(TimeWindow w, int n) => new(w.Start + (w.Duration * n / 4), w.Start + (w.Duration * (n + 1) / 4));

    [Test]
    public async Task The_bookmark_commands_have_free_gestures_print_as_Alt_n_and_the_legend_has_a_star()
    {
        CommandDefinition add = CommandRegistry.Get(AppCommand.AddBookmark);
        await Assert.That(add.Title).IsEqualTo("Bookmark current view");
        await Assert.That(add.ShortcutText).IsEqualTo("Ctrl+Shift+B");
        await Assert.That(add.IsWindowShortcut).IsTrue();
        await Assert.That(add.Group).IsEqualTo(CommandGroup.View);
        await Assert.That(add.InPalette).IsTrue();
        // Help lists every jump with its key.
        HelpRow[] help = HelpDialogViewModel.Build();
        await Assert.That(help.Single(r => r.Text == "Jump to bookmark 9").Keys).IsEqualTo("Alt+9");
        List<CommandDefinition> all = [add];
        for (int n = 1; n <= 9; n++)
        {
            CommandDefinition jump = CommandRegistry.Get(CommandRegistry.JumpBookmark(n));
            await Assert.That(jump.Title).IsEqualTo("Jump to bookmark " + n);
            await Assert.That(jump.ShortcutText).IsEqualTo("Alt+" + n);
            await Assert.That(jump.IsWindowShortcut).IsTrue();
            await Assert.That(jump.Glyph).IsEqualTo(CommandGlyph.Bookmark);
            // Shortcut only: a number typed in the palette finds invocations, not "Jump to bookmark n".
            await Assert.That(jump.InPalette).IsFalse();
            all.Add(jump);
        }

        // No other command takes these gestures (Ctrl+B stays the hex view's bookmark offset; I43 has Ctrl+Shift+F, Alt+Left).
        foreach (CommandDefinition d in all)
            await Assert.That(CommandRegistry.Definitions.Count(o => d.Gesture!.Equals(o.Gesture))).IsEqualTo(1);
        await Assert.That(CommandRegistry.Definitions.Any(o => o.ShortcutText == "Ctrl+B")).IsFalse();
        await Assert.That(() => CommandRegistry.JumpBookmark(10)).Throws<ArgumentOutOfRangeException>();

        LegendEntry star = Legend.Get(LegendSymbol.Bookmarks);
        await Assert.That(star.Section).IsEqualTo(LegendSection.Badges);
        await Assert.That(star.Name).StartsWith("★");
        await Assert.That(Legend.Entries.Count(e => e.Name.StartsWith('★'))).IsEqualTo(1);
    }

    [Test]
    public async Task Ctrl_Shift_B_adds_the_view_and_opens_the_dropdown_to_name_it()
    {
        var seen = await HeadlessSession.Run(() =>
        {
            (MainWindow window, MainWindowViewModel model, InspectLiveScreen.Picks picks, ThemeService themes) = Open();
            try
            {
                Border group = Find<Border>(window.TitleBar, "BookmarksGroup");
                bool buttonShown = group.IsEffectivelyVisible;
                window.Inspect.Timeline.Focus();
                window.KeyPress(Key.B, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.B, "B");
                Settle();
                BookmarksViewModel bookmarks = model.Chrome.Bookmarks;
                bool open = bookmarks.IsOpen;
                TextBox? editor = TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement() as TextBox;
                string? defaultText = editor?.Text;
                string? selected = editor?.SelectedText;
                string expected = TimeWindowReadoutViewModel.Format(picks.Window, model.Documents.Active!.Session!.StartTicks, model.Documents.Active.Frequency);

                // Type a name over the selected default and keep it with Enter.
                window.KeyTextInput("SendAttachment chunks");
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                Settle();
                ViewBookmark[] list = [.. model.Documents.ActiveBookmarks];
                BookmarksPopover popover = window.BookmarksPopover;
                string[] names = [.. FindAll<TextBlock>(popover, "BookmarkName").Select(t => t.Text ?? "")];
                string count = Find<TextBlock>(window.TitleBar, "BookmarksCount").Text ?? "";
                bool stillOpen = bookmarks.IsOpen;

                // Esc closes the dropdown; a second Ctrl+Shift+B keeps the default name when Esc ends the edit.
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Settle();
                bool closed = !bookmarks.IsOpen;
                model.Documents.Sync.SetWindow(Quarter(picks.Window, 1));
                Settle();
                window.KeyPress(Key.B, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.B, "B");
                Settle();
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Settle();
                string second = model.Documents.ActiveBookmarks[1].Name;
                string expectedSecond = TimeWindowReadoutViewModel.Format(Quarter(picks.Window, 1), model.Documents.Active.Session.StartTicks,
                    model.Documents.Active.Frequency);
                return (buttonShown, open, Editor: editor?.Name, defaultText, selected, expected, Count: list.Length, Name: list[0].Name,
                    Window: list[0].Window, Picked: picks.Window, names, count, stillOpen, closed, second, expectedSecond,
                    Total: model.Documents.ActiveBookmarks.Count);
            }
            finally
            {
                window.Close();
                themes.Dispose();
            }
        });

        await Assert.That(seen.buttonShown).IsTrue();
        await Assert.That(seen.open).IsTrue();
        await Assert.That(seen.Editor).IsEqualTo("NameEditor");
        await Assert.That(seen.defaultText).IsEqualTo(seen.expected);
        await Assert.That(seen.selected).IsEqualTo(seen.expected);
        await Assert.That(seen.Count).IsEqualTo(1);
        await Assert.That(seen.Name).IsEqualTo("SendAttachment chunks");
        await Assert.That(seen.Window).IsEqualTo(seen.Picked);
        await Assert.That(seen.names).IsEquivalentTo(new[] { "SendAttachment chunks" });
        await Assert.That(seen.count).IsEqualTo("1");
        await Assert.That(seen.stillOpen).IsTrue();
        await Assert.That(seen.closed).IsTrue();
        await Assert.That(seen.second).IsEqualTo(seen.expectedSecond);
        await Assert.That(seen.Total).IsEqualTo(2);
    }

    [Test]
    public async Task The_star_lists_name_range_and_scope_badge_and_a_click_jumps_and_pushes_the_scope()
    {
        var seen = await HeadlessSession.Run(() =>
        {
            (MainWindow window, MainWindowViewModel model, InspectLiveScreen.Picks picks, ThemeService themes) = Open();
            try
            {
                DocumentsViewModel documents = model.Documents;
                SyncBus sync = documents.Sync;
                documents.AddBookmark("plain");
                documents.FilterToView();
                TimeWindow scope = sync.Scope;
                sync.SetWindow(Quarter(picks.Window, 2));
                documents.AddBookmark("scoped");
                documents.ClearScope();
                sync.SetWindow(Quarter(picks.Window, 0));
                Settle();

                Click(window, Find<Button>(window.TitleBar, "BookmarksButton"));
                Settle();
                bool open = model.Chrome.Bookmarks.IsOpen;
                BookmarksPopover popover = window.BookmarksPopover;
                string[] names = [.. FindAll<TextBlock>(popover, "BookmarkName").Select(t => t.Text ?? "")];
                string[] ranges = [.. FindAll<TextBlock>(popover, "BookmarkRange").Select(t => t.Text ?? "")];
                int badges = FindAll<Border>(popover, "ScopeBadge").Count;
                string header = Find<TextBlock>(popover, "Header").Text ?? "";

                Click(window, FindAll<Button>(popover, "BookmarkRow")[1]);
                Settle();
                return (open, names, ranges, badges, header, Closed: !model.Chrome.Bookmarks.IsOpen, Window: sync.Window,
                    Expected: Quarter(picks.Window, 2), Scope: sync.Scope, Wanted: scope,
                    Chip: Find<Border>(window.TitleBar, "ScopeChip").IsEffectivelyVisible,
                    Range0: TimeWindowReadoutViewModel.Format(picks.Window, documents.Active!.Session!.StartTicks, documents.Active.Frequency));
            }
            finally
            {
                window.Close();
                themes.Dispose();
            }
        });

        await Assert.That(seen.open).IsTrue();
        await Assert.That(seen.names).IsEquivalentTo(new[] { "plain", "scoped" });
        await Assert.That(seen.ranges[0]).IsEqualTo(seen.Range0);
        await Assert.That(seen.badges).IsEqualTo(1);
        await Assert.That(seen.header).IsEqualTo("Bookmarks · 2 of " + AppSettings.MaxBookmarksPerRecording);
        await Assert.That(seen.Closed).IsTrue();
        await Assert.That(seen.Window).IsEqualTo(seen.Expected);
        await Assert.That(seen.Scope).IsEqualTo(seen.Wanted);
        await Assert.That(seen.Chip).IsTrue();
    }

    [Test]
    public async Task F2_renames_in_place_Esc_cancels_and_the_legend_stays_shut()
    {
        var seen = await HeadlessSession.Run(() =>
        {
            (MainWindow window, MainWindowViewModel model, InspectLiveScreen.Picks picks, ThemeService themes) = Open();
            try
            {
                model.Documents.AddBookmark("first");
                Settle();
                Click(window, Find<Button>(window.TitleBar, "BookmarksButton"));
                Settle();
                IInputElement? focused = TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement();
                string? focusedName = (focused as Control)?.Name;

                window.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
                Settle();
                BookmarksPopover popover = window.BookmarksPopover;
                bool editing = FindAll<TextBox>(popover, "NameEditor").Count == 1;
                window.KeyTextInput("nope");
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Settle();
                string afterEsc = model.Documents.ActiveBookmarks[0].Name;
                bool stillOpen = model.Chrome.Bookmarks.IsOpen;
                string? refocused = (TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement() as Control)?.Name;

                window.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
                Settle();
                window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
                window.KeyTextInput("renamed");
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                Settle();
                return (focusedName, editing, afterEsc, stillOpen, refocused, Renamed: model.Documents.ActiveBookmarks[0].Name,
                    Shown: FindAll<TextBlock>(popover, "BookmarkName").Select(t => t.Text).FirstOrDefault(), Dialog: model.Dialog is null);
            }
            finally
            {
                window.Close();
                themes.Dispose();
            }
        });

        await Assert.That(seen.focusedName).IsEqualTo("BookmarkRow");
        await Assert.That(seen.editing).IsTrue();
        await Assert.That(seen.afterEsc).IsEqualTo("first");
        await Assert.That(seen.stillOpen).IsTrue();
        await Assert.That(seen.refocused).IsEqualTo("BookmarkRow");
        await Assert.That(seen.Renamed).IsEqualTo("renamed");
        await Assert.That(seen.Shown).IsEqualTo("renamed");
        await Assert.That(seen.Dialog).IsTrue().Because("F2 in the list renames; it does not open the legend");
    }

    [Test]
    public async Task The_cross_and_Delete_remove_bookmarks()
    {
        var seen = await HeadlessSession.Run(() =>
        {
            (MainWindow window, MainWindowViewModel model, InspectLiveScreen.Picks picks, ThemeService themes) = Open();
            try
            {
                SyncBus sync = model.Documents.Sync;
                for (int i = 0; i < 3; i++)
                {
                    sync.SetWindow(Quarter(picks.Window, i));
                    model.Documents.AddBookmark("b" + i);
                }

                Settle();
                Click(window, Find<Button>(window.TitleBar, "BookmarksButton"));
                Settle();
                BookmarksPopover popover = window.BookmarksPopover;
                Click(window, FindAll<Button>(popover, "DeleteButton")[1]);
                Settle();
                string[] afterCross = [.. model.Documents.ActiveBookmarks.Select(b => b.Name)];

                // Delete on the focused (first) row.
                popover.FocusRow(0);
                UiPump.Frames();
                window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
                Settle();
                return (afterCross, AfterDelete: model.Documents.ActiveBookmarks.Select(b => b.Name).ToArray(),
                    Rows: FindAll<TextBlock>(popover, "BookmarkName").Select(t => t.Text ?? "").ToArray(),
                    Count: Find<TextBlock>(window.TitleBar, "BookmarksCount").Text);
            }
            finally
            {
                window.Close();
                themes.Dispose();
            }
        });

        await Assert.That(seen.afterCross).IsEquivalentTo(new[] { "b0", "b2" });
        await Assert.That(seen.AfterDelete).IsEquivalentTo(new[] { "b2" });
        await Assert.That(seen.Rows).IsEquivalentTo(new[] { "b2" });
        await Assert.That(seen.Count).IsEqualTo("1");
    }

    [Test]
    public async Task Alt_1_to_9_jump_to_the_first_nine_through_the_window()
    {
        var seen = await HeadlessSession.Run(() =>
        {
            (MainWindow window, MainWindowViewModel model, InspectLiveScreen.Picks picks, ThemeService themes) = Open();
            try
            {
                SyncBus sync = model.Documents.Sync;
                for (int i = 0; i < 3; i++)
                {
                    sync.SetWindow(Quarter(picks.Window, i));
                    model.Documents.AddBookmark("b" + i);
                }

                sync.SetWindow(picks.Window);
                Settle();
                window.Inspect.Timeline.Focus();
                window.KeyPress(Key.D2, RawInputModifiers.Alt, PhysicalKey.Digit2, "2");
                Settle();
                TimeWindow two = sync.Window;
                window.KeyPress(Key.D1, RawInputModifiers.Alt, PhysicalKey.Digit1, "1");
                Settle();
                TimeWindow one = sync.Window;
                window.KeyPress(Key.D3, RawInputModifiers.Alt, PhysicalKey.Digit3, "3");
                Settle();
                TimeWindow three = sync.Window;
                // No fifth bookmark: Alt+5 does nothing.
                bool canFive = model.Commands.CanExecute(AppCommand.JumpBookmark5);
                window.KeyPress(Key.D5, RawInputModifiers.Alt, PhysicalKey.Digit5, "5");
                Settle();
                return (two, one, three, canFive, AfterFive: sync.Window, Picks: picks.Window);
            }
            finally
            {
                window.Close();
                themes.Dispose();
            }
        });

        await Assert.That(seen.two).IsEqualTo(Quarter(seen.Picks, 1));
        await Assert.That(seen.one).IsEqualTo(Quarter(seen.Picks, 0));
        await Assert.That(seen.three).IsEqualTo(Quarter(seen.Picks, 2));
        await Assert.That(seen.canFive).IsFalse();
        await Assert.That(seen.AfterFive).IsEqualTo(Quarter(seen.Picks, 2));
    }
}
