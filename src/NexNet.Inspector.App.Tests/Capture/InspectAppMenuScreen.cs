using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// The inspect-app-menu screen (PLAN §8): inspect-live (the Chat fixture's traced SendAttachment) with a live connection
/// in the title bar and the logo's application menu open, its "Schema · Chat.nnschema (embedded)" item highlighted as
/// the package draws it.
/// </summary>
public static class InspectAppMenuScreen
{
    public static Window Create(CaptureContext context)
    {
        MainWindowViewModel viewModel = new(context.Themes, store: null, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, null));
        LiveConnection live = LiveConnection.Attach(viewModel);
        InspectLiveScreen.Open(viewModel);
        MainWindow window = new() { DataContext = viewModel };
        window.Inspect.Timeline.Layout.Animator.Enabled = false;
        live.TearDownWith(window, viewModel);
        window.Opened += (_, _) =>
        {
            viewModel.IsAppMenuOpen = true;
            ((IPseudoClasses)window.AppMenu.SchemaItem.Classes).Set(":pointerover", true);
        };
        return window;
    }
}
