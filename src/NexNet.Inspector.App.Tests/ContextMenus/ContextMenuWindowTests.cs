using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Controls;
using NexNet.Inspector.App.Controls.PropertyTree;
using NexNet.Inspector.App.Controls.Timeline;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.Tests.Capture;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.App.Views.Inspect;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Tests.ContextMenus;

/// <summary>
/// The context menus in the window (screen context-menus): a right-click on a timeline lane opens the lane menu and on
/// a group header the group menu; a right-click in the hex view opens the selection menu, whose Copy items reach the
/// window's clipboard, whose "Decode selection as ▸" submenu checks the reading shown, and whose "Go to offset…" opens
/// its dialog over the window.
/// </summary>
[NotInParallel("avalonia")]
public sealed class ContextMenuWindowTests
{
    private static Task<(MainWindow Window, MainWindowViewModel Model, InspectLiveScreen.Picks Picks)> OpenAsync() => HeadlessSession.Run(() =>
    {
        using ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(Avalonia.Platform.PlatformThemeVariant.Light), AppTheme.Light);
        MainWindowViewModel model = new(themes, store: null, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, null));
        InspectLiveScreen.Picks picks = InspectLiveScreen.Open(model);
        MainWindow window = new() { DataContext = model, Width = 1440, Height = 900 };
        window.Inspect.Timeline.Layout.Animator.Enabled = false;
        window.Show();
        UiPump.Frames();
        return (window, model, picks);
    });

    private static Task CloseAsync(MainWindow window, MainWindowViewModel model) => HeadlessSession.Run(() =>
    {
        window.Close();
        return model.Documents.DisposeAsync().AsTask();
    });

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

    private static void Click(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        UiPump.Frames();
    }

    private static void RightClick(Window window, Visual target, Point at)
    {
        Point point = target.TranslatePoint(at, window) ?? throw new InvalidOperationException("the target is not in the window");
        window.MouseDown(point, MouseButton.Right, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Right, RawInputModifiers.None);
        UiPump.Frames();
    }

    [Test]
    public async Task A_right_click_on_a_lane_opens_its_menu_and_its_items_act_on_that_lane()
    {
        var (window, model, picks) = await OpenAsync();
        try
        {
            LaneKey pipe = new(LaneKind.Pipe, picks.Pipe);
            var result = await HeadlessSession.Run(() =>
            {
                TimelineControl timeline = window.Inspect.Timeline;
                LayoutRow row = timeline.Layout.Rows.ToArray().First(r => r.Lane?.Key == pipe);
                RightClick(window, timeline, new Point(60, timeline.LanesTop + row.Top + (row.Height / 2) - timeline.LaneScroll));
                ContextMenu menu = window.Inspect.TimelineMenu!;
                string described = Describe(menu);
                bool open = menu.IsOpen;
                Click(ItemOf(menu, "Expand lane"));
                bool expanded = model.Inspect.Timeline.Model.IsExpanded(pipe);
                bool closed = !menu.IsOpen;

                // A group header opens the group menu.
                LayoutRow group = timeline.Layout.Rows.ToArray().First(r => r.Kind == LayoutRowKind.Group && r.Group == LaneGroupKind.Pipes);
                RightClick(window, timeline, new Point(60, timeline.LanesTop + group.Top + (group.Height / 2) - timeline.LaneScroll));
                string groupMenu = Describe(window.Inspect.TimelineMenu!);
                window.Inspect.TimelineMenu!.Close();
                return (described, open, expanded, closed, groupMenu);
            });

            await Assert.That(result.open).IsTrue();
            await Assert.That(result.described).Matches(
                "^⇄  Pipe \\S+  \\S+.* \\| — \\| ✓Pin lane \\| Expand lane \\| Hide lane \\| — \\| Trace frames in view \\| Select all frames \\(off\\) "
                + "\\| Show in Ladder \\| Show in Hex stream \\| — \\| Compare this pipe across files… \\(off\\) \\| Copy name \\| — \\| Close pipe in view "
                + "\\| — \\| Filter to this range \\| Export this range… \\| Copy for LLM$");
            await Assert.That(result.expanded).IsTrue();
            await Assert.That(result.closed).IsTrue();
            await Assert.That(result.groupMenu).IsEqualTo("PIPES · CHANNELS | — | Collapse group | Show hidden lanes (off) | — | Filter to this range | Export this range… | Copy for LLM");
        }
        finally
        {
            await CloseAsync(window, model);
        }
    }

    [Test]
    public async Task The_hex_menu_copies_to_the_clipboard_and_its_decode_submenu_drives_the_inspector()
    {
        var (window, model, _) = await OpenAsync();
        try
        {
            var result = await HeadlessSession.Run(() =>
            {
                HexPane pane = window.Inspect.GetVisualDescendants().OfType<HexPane>().First();
                // The selected field's bytes, as a click on the inspector's row selects them.
                InspectorViewModel inspector = model.Inspect.Inspector;
                PropertyRow room = inspector.Rows!.First(r => r.Name == "Room");
                inspector.SelectedRow = room;
                UiPump.Frames();

                ContextMenu menu = pane.OpenMenu()!;
                UiPump.Frames();
                string described = Describe(menu);
                MenuItem decode = ItemOf(menu, "Decode selection as");
                string submenu = Describe(decode);
                Click(ItemOf(menu, "Copy as hex"));
                Task<string?> reading = window.Clipboard!.TryGetTextAsync();
                for (int i = 0; i < 200 && !reading.IsCompleted; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(1);
                }

                string? copied = reading.IsCompleted ? reading.Result : null;

                menu = pane.OpenMenu()!;
                Click(ItemOf(ItemOf(menu, "Decode selection as"), "UTF-8 text"));
                string section = inspector.Rows![0].Name;
                menu = pane.OpenMenu()!;
                string checkedNow = Describe(ItemOf(menu, "Decode selection as"));
                menu.Close();
                return (described, submenu, copied, room.Bytes.Stream.Length, section, checkedNow);
            });

            string count = ByteText.Count(result.Length);
            await Assert.That(result.described).StartsWith(count + "  ");
            await Assert.That(result.described[(result.described.IndexOf('|') - 1)..]).IsEqualTo(
                " | — | Copy as hex | Copy as C# byte array | Copy as Base64 | — | Decode selection as | Go to frame start | Go to offset… | — "
                + "| Bookmark offset | Export bytes… | — | Export section as .nntl…");
            await Assert.That(result.submenu).IsEqualTo("✓Auto (schema) | MessagePack value | UTF-8 text | LEB128 length + item | — | Raw only");
            await Assert.That(result.copied).IsNotNull();
            await Assert.That(result.copied!.Length).IsEqualTo((int)(result.Length * 3) - 1);
            await Assert.That(result.section).IsEqualTo("UTF-8 text");
            await Assert.That(result.checkedNow).IsEqualTo("Auto (schema) | MessagePack value | ✓UTF-8 text | LEB128 length + item | — | Raw only");
        }
        finally
        {
            await CloseAsync(window, model);
        }
    }

    [Test]
    public async Task Go_to_offset_opens_its_dialog_over_the_window_and_Enter_goes_there()
    {
        var (window, model, _) = await OpenAsync();
        try
        {
            var result = await HeadlessSession.Run(() =>
            {
                HexPane pane = window.Inspect.GetVisualDescendants().OfType<HexPane>().First();
                InspectorViewModel inspector = model.Inspect.Inspector;
                PropertyRow room = inspector.Rows!.First(r => r.Name == "Room");
                inspector.SelectedRow = room;
                UiPump.Frames();
                Task<ViewModels.HexPosition?> going = pane.CreateSelection()!.GoToOffsetAsync();
                UiPump.Frames();
                bool shown = model.Dialog is GoToOffsetDialogViewModel && window.GetVisualDescendants().OfType<GoToOffsetDialog>().Any(d => d.IsEffectivelyVisible);
                var dialog = (GoToOffsetDialogViewModel)model.Dialog!;
                dialog.Text = ByteText.Offset(room.Bytes.Stream.Offset + 1);
                // Enter from the focused offset field bubbles to the dialog frame, which runs Go.
                TextBox box = (TextBox)window.FocusManager!.GetFocusedElement()!;
                box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = box });
                for (int i = 0; i < 200 && !going.IsCompleted; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(1);
                }

                UiPump.Frames();
                return (shown, going.IsCompleted ? going.Result : null, model.Dialog, model.Documents.Sync.Selection, room.Bytes);
            });

            await Assert.That(result.shown).IsTrue();
            await Assert.That(result.Item2).IsEqualTo(new ViewModels.HexPosition(result.Item5.Direction, result.Item5.Stream.Offset + 1));
            await Assert.That(result.Dialog).IsNull();
            await Assert.That(result.Selection.Bytes).IsEqualTo(new StreamRange(result.Item5.Stream.Offset + 1, 1));
        }
        finally
        {
            await CloseAsync(window, model);
        }
    }
}
