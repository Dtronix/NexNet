using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Model.Palette;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.Tests.Capture;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.App.Views.Palette;
using NexNet.Inspector.Core.Assembly;

namespace NexNet.Inspector.App.Tests.Palette;

/// <summary>The palette in the main window: Ctrl+Shift+P, typing, the keys, the scrim, its geometry.</summary>
[NotInParallel("avalonia")]
public sealed class CommandPaletteViewTests
{
    private readonly record struct Seen(
        bool Opened, bool InputFocused, string Query, double Width, double Top, int Results, Selection Selection, bool Closed, bool FocusBack);

    [Test]
    public async Task Ctrl_Shift_P_opens_the_palette_typing_searches_and_Enter_traces_the_first_entity()
    {
        Seen seen = await HeadlessSession.Run(() =>
        {
            using ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(), AppTheme.Light);
            MainWindowViewModel viewModel = new(themes, null, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, null));
            InspectLiveScreen.Open(viewModel);
            string id = CommandPaletteScreen.Query(viewModel.Documents.Active!.Session!);
            MainWindow window = new() { DataContext = viewModel };
            window.Show();
            try
            {
                UiPump.Frames();
                window.Inspect.Timeline.Focus();
                IInputElement? before = window.FocusManager?.GetFocusedElement();
                window.KeyPress(Key.P, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.P, "P");
                UiPump.Frames();
                CommandPalette palette = window.FindControl<CommandPalette>("Palette")!;
                TextBox input = palette.FindControl<TextBox>("Input")!;
                bool opened = viewModel.Palette.IsOpen && palette.IsEffectivelyVisible;
                bool focused = ReferenceEquals(window.FocusManager?.GetFocusedElement(), input);
                window.KeyTextInput(id);
                UiPump.Frames();
                Border surface = palette.FindControl<Border>("Surface")!;
                Point origin = surface.TranslatePoint(default, window) ?? default;
                int results = palette.FindControl<ItemsControl>("Results")!.GetVisualDescendants().OfType<Border>()
                    .Count(b => b.Classes.Contains("palette-item") && b.IsEffectivelyVisible);
                string query = viewModel.Palette.Query;
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                UiPump.Frames();
                return new Seen(opened, focused, query, surface.Bounds.Width, origin.Y, results, viewModel.Documents.Sync.Selection,
                    !viewModel.Palette.IsOpen && !palette.IsEffectivelyVisible, ReferenceEquals(window.FocusManager?.GetFocusedElement(), before));
            }
            finally
            {
                window.Close();
            }
        });

        await Assert.That(seen.Opened).IsTrue();
        await Assert.That(seen.InputFocused).IsTrue();
        await Assert.That(seen.Width).IsEqualTo(600);
        await Assert.That(seen.Top).IsEqualTo(56);
        await Assert.That(seen.Results).IsGreaterThan(3);
        await Assert.That(seen.Selection.Kind).IsEqualTo(SelectionKind.Entity);
        await Assert.That(seen.Selection.Entity).IsEqualTo(EntityKind.Invocation);
        await Assert.That(seen.Closed).IsTrue();
        await Assert.That(seen.FocusBack).IsTrue();
        await Assert.That(seen.Query.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Down_Tab_and_Escape_are_the_palettes_and_a_press_on_the_scrim_closes_it()
    {
        (int selected, PaletteScope scope, bool escClosed, bool scrimClosed, bool commandsOk) = await HeadlessSession.Run(() =>
        {
            using ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(), AppTheme.Dark);
            MainWindowViewModel viewModel = new(themes, null);
            MainWindow window = new() { DataContext = viewModel };
            window.Show();
            try
            {
                UiPump.Frames();
                bool ok = viewModel.Commands.Execute(Services.Commands.AppCommand.CommandPalette);
                UiPump.Frames();
                window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                int index = viewModel.Palette.SelectedIndex;
                window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
                PaletteScope picked = viewModel.Palette.Scope.Value;
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                bool esc = !viewModel.Palette.IsOpen;

                viewModel.ShowPalette();
                UiPump.Frames();
                CommandPalette palette = window.FindControl<CommandPalette>("Palette")!;
                Border scrim = palette.FindControl<Border>("Scrim")!;
                Point corner = scrim.TranslatePoint(new Point(20, 600), window) ?? default;
                window.MouseDown(corner, MouseButton.Left);
                window.MouseUp(corner, MouseButton.Left);
                UiPump.Frames();
                return (index, picked, esc, !viewModel.Palette.IsOpen, ok);
            }
            finally
            {
                window.Close();
            }
        });

        await Assert.That(commandsOk).IsTrue();
        // An empty query lists commands only: the heading is row 0, Down moves from row 1 to row 2.
        await Assert.That(selected).IsEqualTo(2);
        await Assert.That(scope).IsEqualTo(PaletteScope.Commands);
        await Assert.That(escClosed).IsTrue();
        await Assert.That(scrimClosed).IsTrue();
    }

    [Test]
    public async Task An_open_dialog_keeps_the_palette_shut()
    {
        (bool shown, bool canRun) = await HeadlessSession.Run(() =>
        {
            using ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(), AppTheme.Light);
            MainWindowViewModel viewModel = new(themes, null);
            viewModel.ShowConnect(null);
            return (viewModel.ShowPalette(), viewModel.Commands.CanExecute(Services.Commands.AppCommand.CommandPalette));
        });

        await Assert.That(shown).IsFalse();
        await Assert.That(canRun).IsFalse();
    }
}
