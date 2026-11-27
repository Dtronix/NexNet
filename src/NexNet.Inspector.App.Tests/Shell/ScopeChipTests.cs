using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Controls.Timeline;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Model.Palette;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Services.Commands;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.Tests.Capture;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.App.Views.Scope;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Tests.Shell;

/// <summary>
/// The document scope's commands and title-bar chip (I43): Filter to time range… (Ctrl+Shift+F) and its dialog, Filter to
/// view, Back (Alt+Left and the chip) and ✕, the timeline's overview re-spanned to the scope, all through real keys and
/// clicks in the main window.
/// </summary>
[NotInParallel("avalonia")]
public sealed class ScopeChipTests
{
    private static T Find<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException(name + " not found");

    private static void Click(Window window, Control target)
    {
        Point at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)
                   ?? throw new InvalidOperationException(target.Name + " is not in the window");
        window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
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

    [Test]
    public async Task The_scope_commands_are_registered_in_View_with_free_gestures()
    {
        CommandDefinition range = CommandRegistry.Get(AppCommand.FilterToRange);
        CommandDefinition view = CommandRegistry.Get(AppCommand.FilterToView);
        CommandDefinition back = CommandRegistry.Get(AppCommand.ScopeBack);
        CommandDefinition clear = CommandRegistry.Get(AppCommand.ClearScope);
        await Assert.That(range.Title).IsEqualTo("Filter to time range…");
        await Assert.That(range.Gesture!.ToString()).IsEqualTo("Ctrl+Shift+F");
        await Assert.That(range.IsWindowShortcut).IsTrue();
        await Assert.That(back.Gesture!.ToString()).IsEqualTo("Alt+Left");
        await Assert.That(back.IsWindowShortcut).IsTrue();
        await Assert.That(view.Title).IsEqualTo("Filter to view");
        await Assert.That(clear.Title).IsEqualTo("Clear scope");
        foreach (CommandDefinition d in new[] { range, view, back, clear })
        {
            await Assert.That(d.Group).IsEqualTo(CommandGroup.View);
            await Assert.That(d.Glyph).IsEqualTo(CommandGlyph.Scope);
            // No other command takes its gesture.
            if (d.Gesture is { } g)
                await Assert.That(CommandRegistry.Definitions.Count(o => g.Equals(o.Gesture))).IsEqualTo(1);
        }
    }

    [Test]
    public async Task The_commands_follow_the_active_documents_scope_and_the_palette_lists_them()
    {
        var seen = await HeadlessSession.Run(() =>
        {
            using ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(), AppTheme.Light);
            MainWindowViewModel model = new(themes, null, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, null));
            CommandRegistry c = model.Commands;
            bool[] empty = [c.CanExecute(AppCommand.FilterToRange), c.CanExecute(AppCommand.FilterToView), c.CanExecute(AppCommand.ScopeBack)];
            InspectLiveScreen.Picks picks = InspectLiveScreen.Open(model);
            bool[] open = [c.CanExecute(AppCommand.FilterToRange), c.CanExecute(AppCommand.FilterToView), c.CanExecute(AppCommand.ScopeBack),
                c.CanExecute(AppCommand.ClearScope)];
            bool ran = c.Execute(AppCommand.FilterToView);
            InspectLiveScreen.Pump(static () => true);
            TimeWindow scope = model.Documents.Sync.Scope;
            bool[] scoped = [c.CanExecute(AppCommand.ScopeBack), c.CanExecute(AppCommand.ClearScope)];
            model.Palette.Open(">scope");
            string[] palette = [.. model.Palette.Rows.Where(r => r.Target.Kind == PaletteTargetKind.Command).Select(r => r.Label)];
            model.Palette.Close();
            bool back = c.Execute(AppCommand.ScopeBack);
            bool after = model.Documents.Sync.IsScoped;
            bool dialog = c.Execute(AppCommand.FilterToRange) && model.Dialog is ScopeDialogViewModel;
            bool blocked = c.CanExecute(AppCommand.FilterToView);
            (model.Dialog as ScopeDialogViewModel)?.CancelCommand.Execute(null);
            model.Documents.DisposeAsync().AsTask().Wait();
            return (empty, open, ran, scope, picks.Window, scoped, palette, back, after, dialog, blocked);
        });

        await Assert.That(seen.empty).IsEquivalentTo(new[] { false, false, false });
        await Assert.That(seen.open).IsEquivalentTo(new[] { true, true, false, false });
        await Assert.That(seen.ran).IsTrue();
        await Assert.That(seen.scope).IsEqualTo(seen.Window);
        await Assert.That(seen.scoped).IsEquivalentTo(new[] { true, true });
        await Assert.That(seen.palette).Contains("Back to previous scope");
        await Assert.That(seen.back).IsTrue();
        await Assert.That(seen.after).IsFalse();
        await Assert.That(seen.dialog).IsTrue();
        await Assert.That(seen.blocked).IsFalse().Because("a dialog is open");
    }

    [Test]
    public async Task Ctrl_Shift_F_opens_the_dialog_and_its_Filter_button_scopes_every_view_to_the_current_view()
    {
        var seen = await HeadlessSession.Run(() =>
        {
            (MainWindow window, MainWindowViewModel model, InspectLiveScreen.Picks picks, ThemeService themes) = Open();
            try
            {
                window.Inspect.Timeline.Focus();
                window.KeyPress(Key.F, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.F, "F");
                UiPump.Frames();
                ScopeDialogViewModel? dialog = model.Dialog as ScopeDialogViewModel;
                ScopeDialog? view = window.GetVisualDescendants().OfType<ScopeDialog>().FirstOrDefault();
                bool viewDefault = dialog?.Range == ScopeRangeKind.View;
                Click(window, Find<Button>(view!, "FilterButton"));
                InspectLiveScreen.Pump(static () => true);
                UiPump.Frames();
                TimelineControl timeline = window.Inspect.Timeline;
                Border chip = Find<Border>(window.TitleBar, "ScopeChip");
                return (Opened: dialog is not null && view is not null, viewDefault, Closed: model.Dialog is null,
                    Scope: model.Documents.Sync.Scope, picks.Window, Range: timeline.RangeBounds,
                    Chip: chip.IsEffectivelyVisible, Text: Find<TextBlock>(window.TitleBar, "ScopeText").Text,
                    Depth: Find<TextBlock>(window.TitleBar, "ScopeDepthText").IsEffectivelyVisible,
                    Expected: "Scope " + TimeWindowReadoutViewModel.FormatScope(picks.Window, model.Documents.Active!.Session!.StartTicks, model.Documents.Active.Frequency));
            }
            finally
            {
                window.Close();
                themes.Dispose();
            }
        });

        await Assert.That(seen.Opened).IsTrue();
        await Assert.That(seen.viewDefault).IsTrue();
        await Assert.That(seen.Closed).IsTrue();
        await Assert.That(seen.Scope).IsEqualTo(seen.Window);
        await Assert.That(seen.Range).IsEqualTo(seen.Window).Because("the overview spans the scope");
        await Assert.That(seen.Chip).IsTrue();
        await Assert.That(seen.Text).IsEqualTo(seen.Expected);
        await Assert.That(seen.Text).Matches("^Scope [0-9,]+\\.[0-9]–[0-9,]+\\.[0-9] (ms|s)$");
        await Assert.That(seen.Depth).IsFalse();
    }

    [Test]
    public async Task A_typed_range_nests_Alt_Left_and_the_chip_go_back_and_the_cross_clears()
    {
        var seen = await HeadlessSession.Run(() =>
        {
            (MainWindow window, MainWindowViewModel model, InspectLiveScreen.Picks picks, ThemeService themes) = Open();
            try
            {
                SyncBus sync = model.Documents.Sync;
                long origin = model.Documents.Active!.Session!.StartTicks;
                long frequency = model.Documents.Active.Frequency;
                model.Commands.Execute(AppCommand.FilterToView);
                InspectLiveScreen.Pump(static () => true);

                // The dialog again: Typed, the middle of the view typed in ms, Enter.
                ScopeDialogViewModel dialog = model.ShowScopeDialog()!;
                UiPump.Frames();
                ScopeDialog view = window.GetVisualDescendants().OfType<ScopeDialog>().First();
                Click(window, Find<RadioButton>(view, "TypedRow"));
                bool typed = dialog.Range == ScopeRangeKind.Typed;
                double from = Math.Round(TimeWindow.MillisecondsFromTicks(picks.Window.Start - origin, frequency) + 10, 3);
                double to = from + 20;
                foreach ((string box, double ms) in new[] { ("FromBox", from), ("ToBox", to) })
                {
                    TextBox input = Find<TextBox>(view, box);
                    Click(window, input);
                    window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
                    window.KeyTextInput(ms.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                    UiPump.Frames();
                }

                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                InspectLiveScreen.Pump(static () => true);
                UiPump.Frames();
                TimeWindow nested = sync.Scope;
                int depth = model.Documents.Active.ScopeDepth;
                string depthText = Find<TextBlock>(window.TitleBar, "ScopeDepthText").Text ?? "";
                bool depthShown = Find<TextBlock>(window.TitleBar, "ScopeDepthText").IsEffectivelyVisible;

                // Alt+Left: back to the outer scope.
                window.Inspect.Timeline.Focus();
                window.KeyPress(Key.Left, RawInputModifiers.Alt, PhysicalKey.ArrowLeft, null);
                InspectLiveScreen.Pump(static () => true);
                UiPump.Frames();
                TimeWindow afterAlt = sync.Scope;
                int depthAfterAlt = model.Documents.Active.ScopeDepth;

                // The chip: back to the whole session.
                Click(window, Find<Button>(window.TitleBar, "ScopeBackButton"));
                InspectLiveScreen.Pump(static () => true);
                UiPump.Frames();
                bool chipGone = !Find<Border>(window.TitleBar, "ScopeChip").IsEffectivelyVisible;
                bool unscoped = !sync.IsScoped;

                // Two scopes again, then ✕ clears both.
                model.Commands.Execute(AppCommand.FilterToView);
                model.Documents.PushScope(TimeScope.Of(nested, "again"));
                InspectLiveScreen.Pump(static () => true);
                UiPump.Frames();
                Click(window, Find<Button>(window.TitleBar, "ScopeClearButton"));
                InspectLiveScreen.Pump(static () => true);
                UiPump.Frames();
                return (typed, Nested: nested, Expected: new TimeWindow(origin + TimeWindow.TicksFromMilliseconds(from, frequency),
                        origin + TimeWindow.TicksFromMilliseconds(to, frequency)), depth, depthText, depthShown, afterAlt, picks.Window,
                    depthAfterAlt, chipGone, unscoped, Cleared: !sync.IsScoped && model.Documents.Active.ScopeDepth == 0,
                    ChipAfterClear: Find<Border>(window.TitleBar, "ScopeChip").IsEffectivelyVisible, Bounds: sync.Bounds,
                    Range: model.Documents.Active.TimeRange);
            }
            finally
            {
                window.Close();
                themes.Dispose();
            }
        });

        await Assert.That(seen.typed).IsTrue();
        await Assert.That(seen.Nested).IsEqualTo(seen.Expected);
        await Assert.That(seen.depth).IsEqualTo(2);
        await Assert.That(seen.depthText).IsEqualTo("‹2");
        await Assert.That(seen.depthShown).IsTrue();
        await Assert.That(seen.afterAlt).IsEqualTo(seen.Window);
        await Assert.That(seen.depthAfterAlt).IsEqualTo(1);
        await Assert.That(seen.chipGone).IsTrue();
        await Assert.That(seen.unscoped).IsTrue();
        await Assert.That(seen.Cleared).IsTrue();
        await Assert.That(seen.ChipAfterClear).IsFalse();
        await Assert.That(seen.Bounds).IsEqualTo(seen.Range);
    }

    [Test]
    public async Task A_range_outside_the_current_scope_is_refused_with_its_reason()
    {
        var seen = await HeadlessSession.Run(() =>
        {
            (MainWindow window, MainWindowViewModel model, InspectLiveScreen.Picks picks, ThemeService themes) = Open();
            try
            {
                model.Commands.Execute(AppCommand.FilterToView);
                InspectLiveScreen.Pump(static () => true);
                ScopeDialogViewModel dialog = model.ShowScopeDialog()!;
                dialog.IsTyped = true;
                dialog.FromText = "0";
                dialog.ToText = "0.001";
                dialog.ApplyCommand.Execute(null);
                string? outside = dialog.Error;
                bool open = model.Dialog is not null;
                dialog.FromText = "abc";
                dialog.ApplyCommand.Execute(null);
                string? unreadable = dialog.Error;
                dialog.CancelCommand.Execute(null);
                return (outside, open, unreadable, Depth: model.Documents.Active!.ScopeDepth, Closed: model.Dialog is null);
            }
            finally
            {
                window.Close();
                themes.Dispose();
            }
        });

        await Assert.That(seen.outside).IsEqualTo("The range lies outside the current scope");
        await Assert.That(seen.open).IsTrue();
        await Assert.That(seen.unreadable).StartsWith("Type the start and end in ms");
        await Assert.That(seen.Depth).IsEqualTo(1);
        await Assert.That(seen.Closed).IsTrue();
    }

    [Test]
    public async Task The_overview_names_the_scope_and_a_double_click_fits_the_scope_not_the_session()
    {
        var seen = await HeadlessSession.Run(() =>
        {
            (MainWindow window, MainWindowViewModel model, InspectLiveScreen.Picks picks, ThemeService themes) = Open();
            try
            {
                model.Commands.Execute(AppCommand.FilterToView);
                InspectLiveScreen.Pump(static () => true);
                SyncBus sync = model.Documents.Sync;
                TimeWindow zoomed = new(picks.Window.Start, picks.Window.Start + (picks.Window.Duration / 4));
                sync.SetWindow(zoomed);
                UiPump.Frames();
                TimelineControl timeline = window.Inspect.Timeline;
                (double x0, double x1) = timeline.ViewportEdges();
                double width = timeline.Bounds.Width - TimelineControl.GutterWidth;
                timeline.FitAll();
                UiPump.Frames();
                string label = TimelineControl.OverviewLabel(picks.Window, model.Documents.Active!.Session!.StartTicks, model.Documents.Active.Frequency, true);
                return (Fitted: sync.Window, picks.Window, BoxShare: (x1 - x0) / width, label);
            }
            finally
            {
                window.Close();
                themes.Dispose();
            }
        });

        await Assert.That(seen.Fitted).IsEqualTo(seen.Window);
        // A quarter of the scope fills a quarter of the overview: it spans the scope, not the session.
        await Assert.That(Math.Abs(seen.BoxShare - 0.25)).IsLessThan(0.02);
        await Assert.That(seen.label).Matches("^scope [0-9,]+\\.[0-9] – [0-9,]+\\.[0-9] (ms|s)$");
    }
}
