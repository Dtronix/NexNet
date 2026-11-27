using Avalonia.Controls;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// The bookmarks screen (I44, D4): inspect-live with three bookmarks of the Chat fixture, the second carrying a scope,
/// and the title bar's ★ dropdown open over it: Alt+n, name, range, the scope badge, ✕, and the Add action.
/// </summary>
public static class BookmarksScreen
{
    public static Window Create(CaptureContext context)
    {
        MainWindowViewModel viewModel = new(context.Themes, store: null, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, null));
        InspectLiveScreen.Picks picks = InspectLiveScreen.Open(viewModel);
        DocumentsViewModel documents = viewModel.Documents;
        SyncBus sync = documents.Sync;
        TimeWindow window = picks.Window;
        documents.AddBookmark("SendAttachment · 5 chunks");
        documents.PushScope(TimeScope.Of(window, ScopeLabels.View));
        sync.SetWindow(new TimeWindow(window.Start + (window.Duration / 4), window.Start + (window.Duration / 2)));
        documents.AddBookmark("FileUpload interleaving");
        documents.ClearScope();
        sync.SetWindow(new TimeWindow(window.Start + (window.Duration / 2), window.End));
        documents.AddBookmark();
        sync.SetWindow(window, byUser: false);
        InspectLiveScreen.Pump(static () => true);
        MainWindow shown = new() { DataContext = viewModel };
        shown.Inspect.Timeline.Layout.Animator.Enabled = false;
        // The dropdown opens once the window is up (a popup needs its placement target laid out).
        shown.Opened += (_, _) => viewModel.Chrome.Bookmarks.IsOpen = true;
        return shown;
    }
}
