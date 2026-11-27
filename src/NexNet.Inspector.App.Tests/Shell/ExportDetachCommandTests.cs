using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Services.Commands;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.Tests.Capture;
using NexNet.Inspector.App.ViewModels;

namespace NexNet.Inspector.App.Tests.Shell;

/// <summary>
/// I40's commands: Export section as .nntl… (Ctrl+Shift+S, FILE) opens its dialog over the active document once there is
/// somewhere to save; Detach from session (SESSION, palette) is listed and enabled only while a live document streams.
/// </summary>
[NotInParallel("avalonia")]
public sealed class ExportDetachCommandTests
{
    [Test]
    public async Task Export_section_and_Detach_are_registered_in_their_menu_groups()
    {
        var export = CommandRegistry.Get(AppCommand.ExportSelection);
        await Assert.That(export.Title).IsEqualTo("Export section as .nntl…");
        await Assert.That(export.Group).IsEqualTo(CommandGroup.File);
        await Assert.That(export.Gesture!.ToString()).IsEqualTo("Ctrl+Shift+S");
        var detach = CommandRegistry.Get(AppCommand.Detach);
        await Assert.That(detach.Title).IsEqualTo("Detach from session");
        await Assert.That(detach.Group).IsEqualTo(CommandGroup.Session);
        await Assert.That(CommandRegistry.Definitions.Select(d => d.Command)).Contains(AppCommand.Detach);
    }

    [Test]
    public async Task Ctrl_Shift_S_opens_the_export_dialog_over_the_active_document()
    {
        var result = await HeadlessSession.Run(() =>
        {
            using ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(Avalonia.Platform.PlatformThemeVariant.Light), AppTheme.Light);
            MainWindowViewModel model = new(themes, store: null, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, null));
            bool before = model.Commands.CanExecute(AppCommand.ExportSelection);
            InspectLiveScreen.Open(model);
            bool noPicker = model.Commands.CanExecute(AppCommand.ExportSelection);
            model.Documents.SavePicker = static _ => Task.FromResult<string?>(null);
            model.Commands.Refresh();
            bool ready = model.Commands.CanExecute(AppCommand.ExportSelection);
            bool ran = model.Commands.Execute(AppCommand.ExportSelection);
            var dialog = model.Dialog as ExportSectionDialogViewModel;
            bool again = model.Commands.CanExecute(AppCommand.ExportSelection);
            bool detach = model.Commands.CanExecute(AppCommand.Detach);
            dialog?.CancelCommand.Execute(null);
            bool closed = model.Dialog is null;
            model.Documents.DisposeAsync().AsTask().Wait();
            return (before, noPicker, ready, ran, Range: dialog?.Range, Document: ReferenceEquals(dialog?.Document, model.Documents.Active), again, detach, closed);
        });

        await Assert.That(result.before).IsFalse().Because("no document is open");
        await Assert.That(result.noPicker).IsFalse().Because("there is nowhere to save");
        await Assert.That(result.ready).IsTrue();
        await Assert.That(result.ran).IsTrue();
        await Assert.That(result.Range).IsEqualTo(ExportRangeKind.Window);
        await Assert.That(result.Document).IsTrue();
        await Assert.That(result.again).IsFalse().Because("a dialog is open");
        await Assert.That(result.detach).IsFalse().Because("no live document streams");
        await Assert.That(result.closed).IsTrue();
    }
}
