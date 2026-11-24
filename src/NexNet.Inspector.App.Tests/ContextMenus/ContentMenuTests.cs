using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Controls.DataTable;
using NexNet.Inspector.App.Controls.DataTable.FieldDiff;
using NexNet.Inspector.App.Controls.DataTable.Sessions;
using NexNet.Inspector.App.Controls.Ladder;
using NexNet.Inspector.App.Controls.PropertyTree;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.Tests.Capture;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.Tests.Inspector;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.App.Views.Inspect;
using NexNet.Inspector.App.Views.Shell;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Compare;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Tests.ContextMenus;

/// <summary>
/// The content menus (I40): every content view's rows open a menu on a right-click whose text items reach the clipboard
/// (Copy, Copy value, Copy path, Copy row as TSV; Ctrl+C copies the focused row) and, for rows that map to bytes, whose
/// binary items copy them as hex / C# / Base64, save them to a file (a frame of a reassembled message also its body) and
/// open them in the inspector as "Decode selection as ▸" does.
/// </summary>
[NotInParallel("avalonia")]
public sealed class ContentMenuTests
{
    [Test]
    public async Task Binary_items_copy_the_frame_and_save_the_frame_or_the_reassembled_body()
    {
        await using var documents = new DocumentsViewModel(new TestUi(), null);
        var (document, picks) = await ChatFixture.OpenAsync(documents);
        CaptureSession session = document.Session!;
        using var temp = new TempDirectory();
        string? copied = null;
        List<string> names = [];
        var item = ContentItems.ForFrame(session, picks.Chunk)!;
        var model = new ContentMenuViewModel(item, session, documents.Sync, text =>
        {
            copied = text;
            return Task.CompletedTask;
        }, name =>
        {
            names.Add(name);
            return Task.FromResult<Stream?>(File.Create(temp.File(name)));
        });

        FrameRecordBytes(session, picks.Chunk, out byte[] frame, out byte[] body);
        await Assert.That(model.HasBytes).IsTrue();
        await Assert.That(model.HasMessageBody).IsTrue();
        await Assert.That(item.Title).StartsWith("Frame #");

        await Assert.That(await model.CopyBytesAsync(ByteCopyFormat.Hex)).IsTrue();
        await Assert.That(copied).IsEqualTo(ByteText.Hex(frame));
        await model.CopyBytesAsync(ByteCopyFormat.CSharp);
        await Assert.That(copied).IsEqualTo(ByteText.CSharp(frame));
        await model.CopyBytesAsync(ByteCopyFormat.Base64);
        await Assert.That(copied).IsEqualTo(Convert.ToBase64String(frame));
        await model.CopyTextAsync();
        await Assert.That(copied).IsEqualTo(item.Text);

        await Assert.That(await model.SaveAsync(ContentByteSource.Item)).IsTrue();
        await Assert.That(await model.SaveAsync(ContentByteSource.MessageBody)).IsTrue();
        await Assert.That(names).IsEquivalentTo(new[] { item.FileStem + ".bin", item.FileStem + "-body.bin" });
        await Assert.That(File.ReadAllBytes(temp.File(names[0])).SequenceEqual(frame)).IsTrue();
        await Assert.That(File.ReadAllBytes(temp.File(names[1])).SequenceEqual(body)).IsTrue();
        await Assert.That(body.Length).IsGreaterThan(frame.Length).Because("the body is every chunk's payload end to end");

        // Open as ▸ reads the frame's body bytes: they become the selection.
        await Assert.That(model.CanOpenAs).IsTrue();
        await Assert.That(model.OpenAs(DecodeAs.Raw)).IsTrue();
        await Assert.That(documents.Sync.Selection.Kind).IsEqualTo(SelectionKind.ByteRange);
        await Assert.That(documents.Sync.Selection.Bytes).IsEqualTo(item.Bytes.Body);
        await Assert.That(documents.Sync.Decode.Mode).IsEqualTo(DecodeAs.Raw);
    }

    [Test]
    public async Task An_Events_row_right_click_selects_it_and_its_menu_and_Ctrl_C_copy_it()
    {
        // Events in the left pane from the start. (Headless only: a view swapped into a pane after the window is shown
        // renders, but the platform's hit test keeps missing it, so a real right-click cannot reach its rows there.)
        var (window, model, _) = await OpenAsync(left: StreamPaneKind.Events);
        try
        {
            var result = await HeadlessSession.Run(() =>
            {
                UiPump.Settle();
                EventsView events = window.Inspect.GetVisualDescendants().OfType<EventsView>().First();
                DataTableControl table = events.Grid;
                int index = Enumerable.Range(0, 4000).First(i => table.ContainerFromIndex(i) is { } c && Visible(c, table, window));
                Control row = table.ContainerFromIndex(index)!;
                RightClick(window, row, new Point(40, row.Bounds.Height / 2));
                ContextMenu menu = window.Inspect.ContentMenu!;
                string described = Describe(menu);
                ContentItem item = window.Inspect.DescribeAt(row, null)!;
                Click(ItemOf(menu, "Copy"));
                string? copied = ReadClipboard(window);
                Click(ItemOf(window.Inspect.OpenContentMenu(item, table), "Copy row as TSV"));
                string? tsv = ReadClipboard(window);

                // Ctrl+C on the focused table copies the selected row as shown.
                Pump(window.Clipboard!.SetTextAsync(""));
                UiPump.Frames();
                row.Focus(NavigationMethod.Tab);
                UiPump.Frames();
                row.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.C, KeyModifiers = KeyModifiers.Control });
                UiPump.Frames();
                string? keyed = ReadClipboard(window);
                return (described, Selected: table.SelectedIndex, index, item.Text, item.Row, copied, tsv, keyed);
            });

            await Assert.That(result.Selected).IsEqualTo(result.index);
            await Assert.That(result.described).Matches(
                "^Frame #[0-9,]+  .+ · [0-9,]+ B \\| — \\| Copy \\| Copy value \\| Copy row as TSV \\| — \\| Copy as hex \\| Copy as C# byte array "
                + "\\| Copy as Base64 \\| (Save bytes to file…|Save frame bytes to file… \\| Save message body to file…) \\| Open as \\| — \\| Export section as \\.nntl…$");
            await Assert.That(result.copied).IsEqualTo(result.Text);
            await Assert.That(result.tsv).IsEqualTo(result.Row);
            await Assert.That(result.tsv!.Split('\t').Length).IsEqualTo(7);
            await Assert.That(result.keyed).IsEqualTo(result.Text);
        }
        finally
        {
            await CloseAsync(window, model);
        }
    }

    [Test]
    public async Task A_decoded_field_copies_its_value_and_path_and_opens_as_UTF8_in_the_inspector()
    {
        var (window, model, _) = await OpenAsync();
        try
        {
            var result = await HeadlessSession.Run(() =>
            {
                InspectorViewModel inspector = model.Inspect.Inspector;
                PropertyRow room = inspector.Rows!.First(r => r.Name == "Room");
                inspector.SelectedRow = room;
                UiPump.Frames();
                PropertyTreeView tree = window.Inspect.GetVisualDescendants().OfType<PropertyTreeView>().First(t => t.Name == "Tree");
                ContentItem item = window.Inspect.DescribeAt(tree, null)!;
                ContextMenu menu = window.Inspect.OpenContentMenu(item, tree);
                string described = Describe(menu);
                string openAs = Describe(ItemOf(menu, "Open as"));
                Click(ItemOf(menu, "Copy path"));
                string? path = ReadClipboard(window);
                Click(ItemOf(window.Inspect.OpenContentMenu(item, tree), "Copy value"));
                string? value = ReadClipboard(window);
                Click(ItemOf(ItemOf(window.Inspect.OpenContentMenu(item, tree), "Open as"), "UTF-8 text"));
                string section = inspector.Rows![0].Name;
                return (described, openAs, path, value, Room: room.Value, section, item.Bytes.Kind, Selection: model.Documents.Sync.Selection.Kind);
            });

            await Assert.That(result.described).Contains("| Copy | Copy value | Copy path | Copy row as TSV | — | Copy as hex |");
            await Assert.That(result.openAs).IsEqualTo("MessagePack value | UTF-8 text | LEB128 length + item | — | Raw only");
            await Assert.That(result.path).EndsWith(".Room");
            await Assert.That(result.value).IsEqualTo(result.Room);
            await Assert.That(result.Kind).IsEqualTo(ContentBytesKind.Range);
            await Assert.That(result.section).IsEqualTo("UTF-8 text");
            await Assert.That(result.Selection).IsEqualTo(SelectionKind.ByteRange);
        }
        finally
        {
            await CloseAsync(window, model);
        }
    }

    [Test]
    public async Task Ladder_rows_and_Message_trace_rows_open_the_content_menu_on_their_frame()
    {
        var (window, model, picks) = await OpenAsync();
        try
        {
            var result = await HeadlessSession.Run(() =>
            {
                LadderView ladder = window.Inspect.GetVisualDescendants().OfType<LadderView>().First();
                LadderRowControl row = ladder.RealizedRows.First(r => Visible(r, ladder.Scroller, window));
                RightClick(window, row, new Point(row.Bounds.Width / 2, row.Bounds.Height / 2));
                ContextMenu? menu = window.Inspect.ContentMenu;
                string? ladderTitle = menu is null ? null : Describe(menu).Split(" | ")[0];
                string expected = LadderText.Title(row.Row, model.Inspect.Timeline.Model is null ? 0 : ladder.Model!.ChunkTotal(row.Row.Message));
                menu?.Close();

                TraceStepRow step = model.Inspect.Inspector.TraceRows.First(r => !r.IsInterleaved);
                ContentItem trace = ContentItems.ForTraceStep(model.Inspect.Session, step, model.Documents.Sync.Trace.Message);
                return (ladderTitle, expected, Selected: ladder.SelectedIndex, row.Index, trace.Title, StepLabel: step.Label, trace.Bytes.Kind,
                    TraceFrame: trace.FileStem);
            });

            await Assert.That(result.ladderTitle).StartsWith(result.expected);
            await Assert.That(result.Selected).IsEqualTo(result.Index);
            await Assert.That(result.Title).IsEqualTo(result.StepLabel);
            await Assert.That(result.Kind).IsEqualTo(ContentBytesKind.Frame);
            await Assert.That(result.TraceFrame).Contains("-frame-");
            _ = picks;
        }
        finally
        {
            await CloseAsync(window, model);
        }
    }

    [Test]
    public async Task Compare_rows_entities_and_session_rows_copy_their_text()
    {
        ContentItem diff = ContentItems.ForFieldDiff(new FieldDiffCells(FieldDiffEntryKind.Field, 2, "Text", "message.Text", "\"hi\"", "\"ho\"",
            FieldDiffKind.Changed), "a.nntl", "b.nntl");
        await Assert.That(diff.Text).IsEqualTo("Text  a.nntl: \"hi\"  b.nntl: \"ho\"");
        await Assert.That(diff.Value).IsEqualTo("\"ho\"");
        await Assert.That(diff.Path).IsEqualTo("message.Text");
        await Assert.That(diff.Row).IsEqualTo("message.Text\t\"hi\"\t\"ho\"");
        await Assert.That(diff.Bytes.IsNone).IsTrue();

        ContentItem entity = ContentItems.ForEntity("SendMessage", "12 · 11", hasDelta: true);
        await Assert.That(entity.Text).IsEqualTo("SendMessage  12 · 11");
        await Assert.That(entity.Detail).IsEqualTo("12 · 11 · differs");

        ContentItem session = ContentItems.ForSession(new SessionListRow(0x3A21, "0x3A21", "10.0.4.12:51234", "TCP", "00:41.3", "4:18.7", "18,248"));
        await Assert.That(session.Value).IsEqualTo("0x3A21");
        await Assert.That(session.Row).IsEqualTo("0x3A21\t10.0.4.12:51234\tTCP\t00:41.3\t4:18.7\t18,248");
        await Assert.That(ContentItem.Tsv("a\tb", "c\nd", null)).IsEqualTo("a b\tc d\t");

        var menu = await HeadlessSession.Run(() =>
        {
            var model = new ContentMenuViewModel(diff, null, null, _ => Task.CompletedTask, null);
            return Describe(ContentMenus.Build(model));
        });
        await Assert.That(menu).IsEqualTo("Text  differs | — | Copy | Copy value | Copy path | Copy row as TSV");
    }

    [Test]
    public async Task A_document_tab_offers_save_and_close_and_the_app_menu_lists_export_and_detach()
    {
        var (window, model, picks) = await OpenAsync();
        try
        {
            var result = await HeadlessSession.Run(() =>
            {
                DocumentTabStrip strip = window.GetVisualDescendants().OfType<DocumentTabStrip>().First();
                string tab = Describe(strip.CreateTabMenu(picks.Document)!);
                model.IsAppMenuOpen = true;
                UiPump.Frames();
                MenuItem export = window.AppMenu.ExportItem;
                MenuItem detach = window.AppMenu.DetachItem;
                var shown = (Export: (string)export.Header! + "=" + export.InputGesture, ExportOn: export.Command!.CanExecute(null),
                    Detach: (string)detach.Header!, DetachOn: detach.Command!.CanExecute(null));
                model.IsAppMenuOpen = false;
                return (tab, shown);
            });

            await Assert.That(result.tab).IsEqualTo(picks.Document.Title + " | — | Save .nntl… | — | Close");
            await Assert.That(result.shown.Export).IsEqualTo("Export section as .nntl…=Ctrl+Shift+S");
            await Assert.That(result.shown.Detach).IsEqualTo("Detach from session");
            await Assert.That(result.shown.DetachOn).IsFalse().Because("no live session is attached");
            await Assert.That(result.shown.ExportOn).IsTrue().Because("a document is open and the window has a save picker");
        }
        finally
        {
            await CloseAsync(window, model);
        }
    }

    private static void FrameRecordBytes(CaptureSession session, FrameKey key, out byte[] frame, out byte[] body)
    {
        lock (session.Gate)
        {
            session.TryGetFrame(key, out var record);
            frame = new byte[record.Length];
            session.GetStore(key.Direction).TryCopyTo(record.Offset, frame);
            var message = session.Assembler.GetMessage(key.Direction, key.Seq);
            var length = session.Assembler.Messages[message.Index].Length;
            session.Assembler.TryGetPayload(new PayloadRange(message, 0, length), out var sequence);
            body = System.Buffers.BuffersExtensions.ToArray(sequence);
        }
    }

    private static Task<(MainWindow Window, MainWindowViewModel Model, InspectLiveScreen.Picks Picks)> OpenAsync(bool animator = false, StreamPaneKind? left = null) => HeadlessSession.Run(() =>
    {
        using ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(Avalonia.Platform.PlatformThemeVariant.Light), AppTheme.Light);
        MainWindowViewModel model = new(themes, store: null, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, null));
        InspectLiveScreen.Picks picks = InspectLiveScreen.Open(model);
        MainWindow window = new() { DataContext = model, Width = 1440, Height = 900 };
        window.Inspect.Timeline.Layout.Animator.Enabled = animator;
        if (left is { } kind) model.Inspect.Left.View.Value = kind;
        window.Show();
        UiPump.Frames();
        return (window, model, picks);
    });

    private static Task CloseAsync(MainWindow window, MainWindowViewModel model) => HeadlessSession.Run(() =>
    {
        window.Close();
        return model.Documents.DisposeAsync().AsTask();
    });

    // True when the control's middle shows inside the viewport: realized rows include some scrolled out of view.
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
        for (int i = 0; i < 200 && !reading.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }

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
}
