using Avalonia.Controls;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// The export-section screen (I40): inspect-live with its panes resized by the splitters (a taller timeline, the left
/// pane narrower, a wider inspector) and Export section as .nntl… open over it on the selected SendAttachment.
/// </summary>
public static class ExportSectionScreen
{
    public static Window Create(CaptureContext context)
    {
        MainWindowViewModel viewModel = new(context.Themes, store: null, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, null));
        InspectLiveScreen.Open(viewModel);
        viewModel.Panes.Update(static s => s with { TimelineHeight = 300, LeftShare = 0.42, InspectorWidth = 380 }, save: false);
        viewModel.Documents.SavePicker ??= static _ => Task.FromResult<string?>(null);
        viewModel.ShowExportSection(ExportRangeKind.Message);
        MainWindow window = new() { DataContext = viewModel };
        window.Inspect.Timeline.Layout.Animator.Enabled = false;
        return window;
    }
}
