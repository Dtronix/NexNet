using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Services.Commands;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.Tests.Capture;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.App.Tests.Shell;

/// <summary>
/// The application menu under the logo (screens/inspect-app-menu): its File / Session / View sections and shortcuts, the
/// Theme segmented row, the submenus' items, closing when an item runs; the window's Ctrl+S and Ctrl+Shift+R; the
/// Connect dialog's session pick going to the session popover; and the title bar's time window readout.
/// </summary>
[NotInParallel("avalonia")]
public sealed class AppMenuTests
{
    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static (MainWindow Window, MainWindowViewModel Model, ThemeService Themes) Open(SettingsStore? store = null)
    {
        ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(Avalonia.Platform.PlatformThemeVariant.Light), AppTheme.Light);
        MainWindowViewModel model = new(themes, store, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, store));
        MainWindow window = new() { DataContext = model, Width = 1440, Height = 900 };
        window.Show();
        Settle();
        return (window, model, themes);
    }

    private static void Click(Window window, Control target)
    {
        Point at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("not in the window");
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Settle();
    }

    private static string Text(object? header) => header switch
    {
        string s => s,
        _ => "",
    };

    [Test]
    public async Task The_logo_opens_the_grouped_menu_with_its_shortcuts()
    {
        var shown = await HeadlessSession.Run(() =>
        {
            (MainWindow window, MainWindowViewModel model, ThemeService themes) = Open();
            try
            {
                model.Chrome.RequestCommand.Execute(ShellRequest.AppMenu);
                Settle();
                var items = window.AppMenu.Items.OfType<MenuItem>().Select(i => (Header: Text(i.Header), Group: i.Classes.Contains("group"),
                    Gesture: i.InputGesture?.ToString() ?? "", Sub: i.Classes.Contains("choices"))).ToArray();
                return (Open: model.IsAppMenuOpen, Popup: window.AppMenuPopup.IsOpen, Visible: window.AppMenu.IsEffectivelyVisible,
                    Width: window.AppMenu.Bounds.Width, LogoOpen: window.TitleBar.AppMenuAnchor.Classes.Contains("open"),
                    Separators: window.AppMenu.Items.OfType<Separator>().Count(), Items: items,
                    Theme: window.AppMenu.ThemeSwitch.ItemsSource!.Cast<string>().ToArray(), ThemeIndex: window.AppMenu.ThemeSwitch.SelectedIndex);
            }
            finally
            {
                window.Close();
                themes.Dispose();
            }
        });

        using (Assert.Multiple())
        {
            await Assert.That(shown.Open).IsTrue();
            await Assert.That(shown.Popup).IsTrue();
            await Assert.That(shown.Visible).IsTrue();
            await Assert.That(shown.Width).IsEqualTo(280);
            await Assert.That(shown.LogoOpen).IsTrue();
            await Assert.That(shown.Separators).IsEqualTo(3);
            await Assert.That(shown.Items.Where(i => i.Group).Select(i => i.Header).ToArray()).IsEquivalentTo(new[] { "File", "Session", "View" });
            string[] gestures = [.. shown.Items.Where(i => i.Gesture.Length > 0).Select(i => i.Header + "=" + i.Gesture)];
            await Assert.That(gestures).IsEquivalentTo(new[]
            {
                "Open recording…=Ctrl+O", "Save timeline as .nntl…=Ctrl+S", "Connect to inspector…=Ctrl+K", "Record all sessions=Ctrl+Shift+R",
                "Command palette=Ctrl+Shift+P", "Help & shortcuts=F1",
            });
            await Assert.That(shown.Items.Where(i => i.Sub).Select(i => i.Header).ToArray())
                .IsEquivalentTo(new[] { "Recent", "Switch session", "Schema · none", "Overflow mode · Drop" });
            await Assert.That(shown.Theme).IsEquivalentTo(new[] { "Dark", "Light", "System" });
            await Assert.That(shown.ThemeIndex).IsEqualTo(1);
        }
    }

    [Test]
    public async Task The_theme_row_sets_the_theme_and_the_menu_stays_open()
    {
        var after = await HeadlessSession.Run(() =>
        {
            (MainWindow window, MainWindowViewModel model, ThemeService themes) = Open();
            try
            {
                model.IsAppMenuOpen = true;
                Settle();
                window.AppMenu.ThemeSwitch.SelectedIndex = 0;
                Settle();
                return (model.Theme, themes.Theme, model.IsAppMenuOpen, Embedded: window.AppMenu.ThemeItem.StaysOpenOnClick);
            }
            finally
            {
                window.Close();
                themes.Dispose();
            }
        });

        await Assert.That(after.Item1).IsEqualTo(AppTheme.Dark);
        await Assert.That(after.Item2).IsEqualTo(AppTheme.Dark);
        await Assert.That(after.IsAppMenuOpen).IsTrue();
        await Assert.That(after.Embedded).IsTrue();
    }

    [Test]
    public async Task Running_an_item_closes_the_menu_and_does_its_work()
    {
        var after = await HeadlessSession.Run(() =>
        {
            (MainWindow window, MainWindowViewModel model, ThemeService themes) = Open();
            try
            {
                model.IsAppMenuOpen = true;
                Settle();
                Click(window, window.AppMenu.PaletteItem);
                bool closedByPalette = !model.IsAppMenuOpen;
                bool paletteOpen = model.Palette.IsOpen;
                model.Palette.Close();
                Settle();

                model.IsAppMenuOpen = true;
                Settle();
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Settle();
                bool closedByEscape = !model.IsAppMenuOpen;

                model.IsAppMenuOpen = true;
                Settle();
                window.AppMenu.ConnectItem.Command!.Execute(null);
                window.AppMenu.Close();
                Settle();
                return (closedByPalette, closedByEscape, paletteOpen, Dialog: model.Dialog, Open: model.IsAppMenuOpen);
            }
            finally
            {
                window.Close();
                themes.Dispose();
            }
        });

        await Assert.That(after.closedByPalette).IsTrue();
        // The palette opens through the command registry, as Ctrl+Shift+P does.
        await Assert.That(after.paletteOpen).IsTrue();
        await Assert.That(after.closedByEscape).IsTrue();
        await Assert.That(after.Dialog is ConnectDialogViewModel).IsTrue();
        await Assert.That(after.Open).IsFalse();
    }

    [Test]
    public async Task Submenus_list_recents_schema_and_overflow_from_the_documents_and_settings()
    {
        using TempDirectory temp = new();
        var shown = await HeadlessSession.Run(() =>
        {
            SettingsStore store = new(temp.File("settings.json"));
            store.Update(s => s.WithRecentFile(temp.File("a.nntl"), 2, DateTimeOffset.UtcNow).WithRecentFile(temp.File("b.nntl"), 1, DateTimeOffset.UtcNow));
            (MainWindow window, MainWindowViewModel model, ThemeService themes) = Open(store);
            try
            {
                InspectSessionStartScreen.Open(model);
                model.IsAppMenuOpen = true;
                Settle();
                string[] recent = [.. model.RecentItems.Select(i => i.Header)];
                string schema = model.SchemaHeader;
                bool clearEnabled = model.SchemaItems[1].IsEnabled;
                string[] overflow = [.. model.OverflowItems.Select(i => i.Header + (i.IsChecked ? " ✓" : ""))];
                model.SetOverflowCommand.Execute(InspectorOverflow.Block);
                string header = model.OverflowHeader;
                InspectorOverflow saved = store.Current.Connect.Overflow;
                bool blockChecked = model.OverflowItems[1].IsChecked;
                return (recent, schema, clearEnabled, overflow, header, saved, blockChecked, Save: model.Commands.CanExecute(AppCommand.SaveTimeline));
            }
            finally
            {
                window.Close();
                themes.Dispose();
                InspectLiveScreen.Pump(() => model.Documents.DisposeAsync().AsTask().IsCompleted);
            }
        });

        // The fixture the screen opened went first.
        await Assert.That(shown.recent).IsEquivalentTo(new[] { "chat-server.nntl", "b.nntl", "a.nntl" });
        await Assert.That(shown.schema).IsEqualTo("Schema · Chat.nnschema (embedded)");
        await Assert.That(shown.clearEnabled).IsFalse();
        await Assert.That(shown.overflow).IsEquivalentTo(new[] { "Drop · show gap markers ✓", "Block · lossless, slows the connection" });
        await Assert.That(shown.header).IsEqualTo("Overflow mode · Block");
        await Assert.That(shown.saved).IsEqualTo(InspectorOverflow.Block);
        await Assert.That(shown.blockChecked).IsTrue();
        // The open recording's session can be saved as a .nntl of its own (I29's save handler).
        await Assert.That(shown.Save).IsTrue();
    }

    [Test]
    public async Task The_window_binds_save_and_record_all_and_the_connect_dialog_picks_in_the_popover()
    {
        var shown = await HeadlessSession.Run(() =>
        {
            (MainWindow window, MainWindowViewModel model, ThemeService themes) = Open();
            try
            {
                string[] bindings = [.. window.KeyBindings.Select(b => b.Gesture.ToString())];
                bool recordBound = window.KeyBindings.Any(b => b.Gesture.ToString() == "Ctrl+Shift+R" && ReferenceEquals(b.Command, model.Commands.CommandFor(AppCommand.RecordAll)));
                ConnectDialogViewModel dialog = model.ShowConnect(null)!;
                dialog.Connect.Endpoint = "tcp://10.0.4.12:7711";
                Task<InspectorSessionInfo?> pick = dialog.SessionChooser([new InspectorSessionInfo(0x3A21, NntlSessionRole.Server, InspectorSessionState.Connected, 0x3A21, "Tcp", null, "192.168.1.40:5100", 0, 0)], CancellationToken.None);
                Settle();
                bool popover = window.SessionPopup.IsOpen && window.SessionPopover.IsEffectivelyVisible;
                bool group = window.TitleBar.SessionAnchor.IsEffectivelyVisible;
                model.Chrome.Session.IsPopoverOpen = false;
                Settle();
                return (bindings, recordBound, popover, group, Picked: pick.IsCompleted && pick.Result is null);
            }
            finally
            {
                window.Close();
                themes.Dispose();
            }
        });

        await Assert.That(shown.bindings).Contains("Ctrl+S");
        await Assert.That(shown.recordBound).IsTrue();
        await Assert.That(shown.popover).IsTrue();
        await Assert.That(shown.group).IsTrue();
        await Assert.That(shown.Picked).IsTrue();
    }

    [Test]
    public async Task The_time_window_reads_the_range_and_zooms_around_its_centre()
    {
        await Assert.That(TimeWindowReadoutViewModel.Format(new Core.Model.TimeWindow(1_240_000, 1_290_000), 0, 1_000_000))
            .IsEqualTo("1,240.0 – 1,290.0 ms");
        await Assert.That(TimeWindowReadoutViewModel.Format(new Core.Model.TimeWindow(5_000, 42_100_000), 5_000, 1_000_000))
            .IsEqualTo("0.0 – 42.1 s");

        TestUi ui = new();
        await using DocumentsViewModel documents = new(ui, null);
        TimeWindowReadoutViewModel readout = new(documents);
        await Assert.That(readout.HasWindow).IsFalse();
        await Assert.That(readout.ZoomInCommand.CanExecute(null)).IsFalse();

        FileDocumentViewModel document = (await documents.OpenRecordingAsync(Wait.Fixture("chat-server.nntl")))!;
        await document.Indexing;
        await Wait.UntilAsync(() => readout.HasWindow, "the window");
        long start = document.Session!.StartTicks;
        documents.Sync.SetWindow(new Core.Model.TimeWindow(start, start + (50 * document.Frequency / 1000)));
        await Assert.That(readout.Text).IsEqualTo("0.0 – 50.0 ms");
        readout.ZoomInCommand.Execute(null);
        await Assert.That(readout.Text).IsEqualTo("12.5 – 37.5 ms");
        readout.ZoomOutCommand.Execute(null);
        await Assert.That(readout.Text).IsEqualTo("0.0 – 50.0 ms");
    }
}
