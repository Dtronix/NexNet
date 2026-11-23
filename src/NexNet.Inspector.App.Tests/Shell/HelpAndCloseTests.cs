using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Services.Commands;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.App.Views.Shell;

namespace NexNet.Inspector.App.Tests.Shell;

/// <summary>
/// Help &amp; shortcuts (F1: the registry's commands and the views' gestures) and closing the window with unsaved live
/// data (OQ5: each unsaved tab asks Save .nntl… / Discard / Cancel before the window goes).
/// </summary>
[NotInParallel("avalonia")]
public sealed class HelpAndCloseTests
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

    private static (MainWindow Window, MainWindowViewModel Model, ThemeService Themes) Open(bool confirms)
    {
        ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(Avalonia.Platform.PlatformThemeVariant.Light), AppTheme.Light);
        MainWindowViewModel model = new(themes, null, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, null));
        MainWindow window = new() { DataContext = model, Width = 1440, Height = 900, ConfirmsUnsavedClose = confirms };
        window.Show();
        Settle();
        return (window, model, themes);
    }

    [Test]
    public async Task The_help_list_holds_every_command_with_its_shortcut_by_menu_section_then_the_views_gestures()
    {
        HelpRow[] rows = HelpDialogViewModel.Build();
        string[] headings = rows.Where(r => r.IsHeading).Select(r => r.Text).ToArray();
        await Assert.That(headings).IsEquivalentTo(new[] { "File", "Session", "View", "Help", "In the views" },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        foreach (CommandDefinition definition in CommandRegistry.Definitions)
        {
            HelpRow row = rows.Single(r => !r.IsHeading && r.Text == definition.Title);
            await Assert.That(row.Keys).IsEqualTo(definition.ShortcutText ?? "");
        }

        await Assert.That(rows.Any(r => r.Keys == "Ctrl+Wheel")).IsTrue();
        await Assert.That(rows.Single(r => r.Text == "Help & shortcuts").Keys).IsEqualTo("F1");
    }

    [Test]
    public async Task F1_opens_the_help_dialog_and_Close_dismisses_it()
    {
        var result = await HeadlessSession.Run(() =>
        {
            (MainWindow window, MainWindowViewModel model, ThemeService themes) = Open(confirms: false);
            try
            {
                bool ran = model.Commands.Execute(AppCommand.HelpShortcuts);
                Settle();
                HelpDialogViewModel? dialog = model.Dialog as HelpDialogViewModel;
                bool shown = window.GetVisualDescendants().OfType<HelpDialog>().Any(v => v.IsEffectivelyVisible);
                bool again = model.ShowHelp() is not null;
                dialog?.CloseCommand.Execute(null);
                Settle();
                return (ran, Opened: dialog is not null, shown, again, Closed: model.Dialog is null, Done: dialog?.Result.IsCompleted ?? false);
            }
            finally
            {
                window.Close();
                themes.Dispose();
            }
        });

        using (Assert.Multiple())
        {
            await Assert.That(result.ran).IsTrue();
            await Assert.That(result.Opened).IsTrue();
            await Assert.That(result.shown).IsTrue();
            await Assert.That(result.again).IsFalse().Because("one dialog at a time");
            await Assert.That(result.Closed).IsTrue();
            await Assert.That(result.Done).IsTrue();
        }
    }

    [Test]
    public async Task Closing_the_window_with_an_unsaved_tab_asks_first_and_Cancel_keeps_it_open()
    {
        var result = await HeadlessSession.Run(() =>
        {
            (MainWindow window, MainWindowViewModel model, ThemeService themes) = Open(confirms: true);
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            TabDocument clean = new(AvaloniaUiDispatcher.Instance, DocumentKind.File, "clean.nntl");
            TabDocument dirty = new(AvaloniaUiDispatcher.Instance, DocumentKind.Live, "Live · 0x3A21", dirty: true);
            model.Documents.Add(clean);
            model.Documents.Add(dirty);
            model.Documents.Activate(clean);
            Settle();
            try
            {
                bool unsaved = model.HasUnsavedDocuments;
                window.Close();
                Settle();
                UnsavedCloseDialogViewModel? first = model.Dialog as UnsavedCloseDialogViewModel;
                bool openWhileAsked = !closed;
                bool brought = ReferenceEquals(model.Documents.Active, dirty);
                first?.CancelCommand.Execute(null);
                Settle();
                bool openAfterCancel = !closed && model.Dialog is null;

                window.Close();
                Settle();
                UnsavedCloseDialogViewModel? second = model.Dialog as UnsavedCloseDialogViewModel;
                second?.DiscardCommand.Execute(null);
                Settle();
                return (unsaved, Asked: first?.Document == dirty, openWhileAsked, brought, openAfterCancel, AskedAgain: second is not null, closed);
            }
            finally
            {
                if (!closed) window.Close();
                themes.Dispose();
            }
        });

        using (Assert.Multiple())
        {
            await Assert.That(result.unsaved).IsTrue();
            await Assert.That(result.Asked).IsTrue();
            await Assert.That(result.openWhileAsked).IsTrue();
            await Assert.That(result.brought).IsTrue().Because("the tab in question comes to the front");
            await Assert.That(result.openAfterCancel).IsTrue();
            await Assert.That(result.AskedAgain).IsTrue();
            await Assert.That(result.closed).IsTrue().Because("Discard lets the window close");
        }
    }

    [Test]
    public async Task Without_unsaved_data_the_window_closes_at_once()
    {
        var result = await HeadlessSession.Run(() =>
        {
            (MainWindow window, MainWindowViewModel model, ThemeService themes) = Open(confirms: true);
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            model.Documents.Add(new TabDocument(AvaloniaUiDispatcher.Instance, DocumentKind.File, "clean.nntl"));
            try
            {
                window.Close();
                Settle();
                return (closed, Dialog: model.Dialog is null);
            }
            finally
            {
                if (!closed) window.Close();
                themes.Dispose();
            }
        });

        await Assert.That(result.closed).IsTrue();
        await Assert.That(result.Dialog).IsTrue();
    }
}
