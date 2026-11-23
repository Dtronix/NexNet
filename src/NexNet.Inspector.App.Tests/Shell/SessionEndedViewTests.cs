using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.Tests.Capture;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.App.Views.Shell;

namespace NexNet.Inspector.App.Tests.Shell;

/// <summary>
/// The session-ended screen in the window: the 34 px strip under the title bar across the full width with its title,
/// detail and actions over the still navigable panes; Discard; and the unsaved-close prompt (OQ5) shown in the dialog
/// layer from the tab's close, with Esc to cancel and Enter to save.
/// </summary>
[NotInParallel("avalonia")]
public sealed class SessionEndedViewTests
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

    private static (MainWindow Window, MainWindowViewModel Model, LiveDocumentViewModel Live) Open(string savePath)
    {
        ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(Avalonia.Platform.PlatformThemeVariant.Light), AppTheme.Light);
        MainWindowViewModel model = new(themes, null, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, null));
        model.Documents.SavePicker = _ => Task.FromResult<string?>(savePath);
        LiveConnection live = LiveConnection.AttachAndEnd(model);
        MainWindow window = new() { DataContext = model, Width = 1440, Height = 900 };
        live.TearDownWith(window, model);
        window.Show();
        Settle();
        return (window, model, live.Document!);
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

    private static void Pump(Func<bool> done)
    {
        long deadline = Environment.TickCount64 + 15_000;
        while (!done() && Environment.TickCount64 < deadline)
        {
            Settle();
            Thread.Sleep(5);
        }
    }

    [Test]
    public async Task The_strip_sits_under_the_title_bar_and_Discard_closes_the_tab()
    {
        using var temp = new TempDirectory();
        var seen = await HeadlessSession.Run(() =>
        {
            (MainWindow window, MainWindowViewModel model, LiveDocumentViewModel live) = Open(temp.File("unused.nntl"));
            try
            {
                SessionEndedBanner banner = window.EndedBanner;
                Rect strip = banner.Strip.Bounds;
                Point origin = banner.Strip.TranslatePoint(default, window) ?? default;
                Point inspect = window.Inspect.TranslatePoint(default, window) ?? default;
                var before = (Visible: banner.IsEffectivelyVisible, strip.Height, strip.Width, Top: origin.Y, InspectTop: inspect.Y,
                    Title: banner.Strip.Title, Detail: banner.Strip.Content as string, Hint: banner.HintText.Text,
                    Buttons: new[] { banner.SaveButton, banner.AttachButton, banner.DiscardButton }.Select(b => b.Content as string ?? "").ToArray(),
                    Primary: banner.SaveButton.Classes.Contains("primary"), PanesVisible: window.Inspect.IsEffectivelyVisible,
                    TitleBarBottom: window.TitleBar.Bounds.Bottom);

                Click(window, banner.AttachButton);
                bool popover = model.Chrome.Session.IsPopoverOpen;
                model.Chrome.Session.IsPopoverOpen = false;
                Settle();

                Click(window, banner.DiscardButton);
                Pump(() => !model.Documents.HasDocuments);
                return (Before: before, Popover: popover, Closed: !model.Documents.HasDocuments, Dialog: model.Dialog,
                    After: banner.IsEffectivelyVisible, Live: live);
            }
            finally
            {
                window.Close();
            }
        });

        await Assert.That(seen.Before.Visible).IsTrue();
        await Assert.That(seen.Before.Height).IsEqualTo(34);
        await Assert.That(seen.Before.Width).IsEqualTo(1440);
        await Assert.That(seen.Before.Top).IsEqualTo(seen.Before.TitleBarBottom);
        await Assert.That(seen.Before.InspectTop).IsEqualTo(seen.Before.TitleBarBottom + 34);
        await Assert.That(seen.Before.Title).IsEqualTo("Session " + LiveDocumentViewModel.SessionHex(seen.Live.AttachedSession!.Value.Id) + " ended");
        await Assert.That(seen.Before.Detail).StartsWith("DisconnectGraceful (21) from client · ");
        await Assert.That(seen.Before.Hint).IsEqualTo(SessionEndedViewModel.StaysOpenHint);
        await Assert.That(seen.Before.Buttons).IsEquivalentTo(new[] { "Save .nntl…", "Attach to another session ▾", "Discard" });
        await Assert.That(seen.Before.Primary).IsTrue();
        await Assert.That(seen.Before.PanesVisible).IsTrue();
        await Assert.That(seen.Popover).IsTrue();
        await Assert.That(seen.Closed).IsTrue();
        await Assert.That(seen.Dialog).IsNull();
        await Assert.That(seen.After).IsFalse();
    }

    [Test]
    public async Task Closing_the_unsaved_tab_prompts_and_Enter_saves()
    {
        using var temp = new TempDirectory();
        string path = temp.File("prompted.nntl");
        var seen = await HeadlessSession.Run(() =>
        {
            (MainWindow window, MainWindowViewModel model, LiveDocumentViewModel live) = Open(path);
            try
            {
                model.Documents.CloseDocumentCommand.Execute(live);
                Settle();
                var dialog = model.Dialog as UnsavedCloseDialogViewModel;
                UnsavedCloseDialog? view = window.Dialogs.GetVisualDescendants().OfType<UnsavedCloseDialog>().FirstOrDefault();
                var shown = (Dialog: dialog is not null, View: view is not null, Title: dialog?.Title,
                    Focused: ReferenceEquals(window.FocusManager?.GetFocusedElement(), view?.SaveButton),
                    Buttons: view is null ? [] : new[] { view.CancelButton, view.DiscardButton, view.SaveButton }.Select(b => b.Content as string ?? "").ToArray());

                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
                Settle();
                var canceled = (Dialog: model.Dialog, Open: model.Documents.Documents.Contains(live));

                model.Documents.CloseDocumentCommand.Execute(live);
                Settle();
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
                Pump(() => !model.Documents.HasDocuments);
                return (Shown: shown, Canceled: canceled, Closed: !model.Documents.HasDocuments, Saved: File.Exists(path), live.AttachedSession);
            }
            finally
            {
                window.Close();
            }
        });

        await Assert.That(seen.Shown.Dialog).IsTrue();
        await Assert.That(seen.Shown.View).IsTrue();
        await Assert.That(seen.Shown.Title).IsEqualTo("Save " + LiveDocumentViewModel.TabTitle(seen.AttachedSession!.Value.Id) + "?");
        await Assert.That(seen.Shown.Focused).IsTrue();
        await Assert.That(seen.Shown.Buttons).IsEquivalentTo(new[] { "Cancel", "Discard", "Save .nntl…" });
        await Assert.That(seen.Canceled.Dialog).IsNull();
        await Assert.That(seen.Canceled.Open).IsTrue();
        await Assert.That(seen.Closed).IsTrue();
        await Assert.That(seen.Saved).IsTrue();
    }
}
