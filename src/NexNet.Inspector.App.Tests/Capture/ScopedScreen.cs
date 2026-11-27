using Avalonia.Controls;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// The scoped screen (I43, D2): inspect-live filtered to its view, then to the traced SendAttachment's half of it, so
/// the title bar's chip reads "‹2 Scope a–b ms ✕", the overview and ruler span the inner scope, Events, Ladder and Hex
/// list only its frames, and the Message trace marks the steps outside it.
/// </summary>
public static class ScopedScreen
{
    public static Window Create(CaptureContext context)
    {
        MainWindowViewModel viewModel = new(context.Themes, store: null, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, null));
        InspectLiveScreen.Picks picks = InspectLiveScreen.Open(viewModel);
        DocumentsViewModel documents = viewModel.Documents;
        documents.FilterToView();
        // The inner scope: from 4 ms before the trace's start, half the outer scope wide.
        MessageTrace trace = documents.Sync.Trace;
        long ms = documents.Active!.Frequency / 1000;
        long start = Math.Max(picks.Window.Start, trace.Start - (4 * ms));
        documents.PushScope(TimeScope.Of(new TimeWindow(start, start + (picks.Window.Duration / 2)), ScopeLabels.Typed));
        // A zoom inside the scope, so the overview's box sits within it.
        TimeWindow scope = documents.Sync.Scope;
        documents.Sync.SetWindow(new TimeWindow(scope.Start + (scope.Duration / 8), scope.End - (scope.Duration / 4)));
        // The Message trace tab: its steps outside the inner scope are marked.
        viewModel.Inspect.Inspector.TabIndex = 1;
        InspectLiveScreen.Pump(static () => true);
        MainWindow window = new() { DataContext = viewModel };
        window.Inspect.Timeline.Layout.Animator.Enabled = false;
        return window;
    }
}
