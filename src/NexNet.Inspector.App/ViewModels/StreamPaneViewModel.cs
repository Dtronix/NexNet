using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using NexNet.Inspector.App.Controls;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.App.Model;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// One lower pane of the Inspect workspace: which view it shows (Events | Ladder | Hex stream, its header's tabs) and
/// that view's model (Events, Ladder or Hex view-model), made once per kind by the workspace's factory and kept, so switching tabs back finds the view as
/// it was left. The view comes from the data template keyed by the content's type.
/// </summary>
public sealed partial class StreamPaneViewModel : ObservableObject, IDisposable
{
    private readonly Func<StreamPaneKind, PaneSide, object> _factory;
    private readonly object?[] _contents = new object?[3];

    /// <param name="side">Left or right.</param>
    /// <param name="initial">The view shown first.</param>
    /// <param name="factory">Makes a view's model for this pane.</param>
    public StreamPaneViewModel(PaneSide side, StreamPaneKind initial, Func<StreamPaneKind, PaneSide, object> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Side = side;
        _factory = factory;
        View = new EnumChoice<StreamPaneKind>([(StreamPaneKind.Events, "Events"), (StreamPaneKind.Ladder, "Ladder"), (StreamPaneKind.Hex, "Hex stream")], initial);
        View.ValueChanged += (_, _) => OnPropertyChanged(nameof(Content));
    }

    /// <summary>Left or right.</summary>
    public PaneSide Side { get; }

    /// <summary>The view shown (the header's tabs).</summary>
    public EnumChoice<StreamPaneKind> View { get; }

    /// <summary>The shown view's model.</summary>
    public object Content => ContentFor(View.Value);

    /// <summary>The header's link badge: synced while the view follows the timeline.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncState))]
    public partial bool IsSynced { get; set; } = true;

    /// <summary>The badge state.</summary>
    public PaneSyncState SyncState => IsSynced ? PaneSyncState.Synced : PaneSyncState.Unsynced;

    /// <summary>What the header's split button does (set by the workspace).</summary>
    [ObservableProperty]
    public partial PaneSplitAction SplitAction { get; internal set; }

    /// <summary>The split button's command (the workspace's toggle or close).</summary>
    [ObservableProperty]
    public partial ICommand? SplitCommand { get; internal set; }

    /// <summary>False while the window is too narrow for a second pane.</summary>
    [ObservableProperty]
    public partial bool CanSplit { get; internal set; } = true;

    /// <summary>A view's model, made on first use.</summary>
    public object ContentFor(StreamPaneKind kind)
    {
        if (_contents[(int)kind] is { } made) return made;
        object content = _factory(kind, Side);
        if (content is LadderViewModel ladder) ladder.IsSynced = IsSynced;
        return _contents[(int)kind] = content;
    }

    public void Dispose()
    {
        foreach (object? content in _contents)
            (content as IDisposable)?.Dispose();
    }

    // The ladder is the view whose scrolling can detach from the timeline: the badge and the view say the same.
    partial void OnIsSyncedChanged(bool value)
    {
        foreach (object? content in _contents)
            if (content is LadderViewModel ladder) ladder.IsSynced = value;
    }
}
