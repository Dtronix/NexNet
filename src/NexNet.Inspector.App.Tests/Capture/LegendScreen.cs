using Avalonia.Controls;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>The legend screen: inspect-live behind the Legend &amp; symbols dialog (F2), scrolled to its top.</summary>
public static class LegendScreen
{
    public static Window Create(CaptureContext context)
    {
        MainWindowViewModel viewModel = new(context.Themes, store: null, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, null));
        InspectLiveScreen.Open(viewModel);
        MainWindow window = new() { DataContext = viewModel };
        window.Inspect.Timeline.Layout.Animator.Enabled = false;
        viewModel.Commands.Execute(Services.Commands.AppCommand.Legend);
        return window;
    }
}
