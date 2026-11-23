using NexNet.Inspector.App.Controls.Timeline;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// The timeline's lane menu (screen context-menus): the lane it was opened on (the header's "⇄ Pipe 3·1 FileUpload ·
/// raw") and what the menu does with it: pin (✓), expand (✓), hide; trace its frames in view, and show them in the
/// Ladder or the Hex stream; copy its name; close a pipe that has closed in view. Made when the menu opens, over the
/// Inspect workspace and the timeline's model at that moment.
/// </summary>
public sealed class LaneMenuViewModel
{
    private readonly InspectWorkspaceViewModel _workspace;
    private readonly TimelineLane _lane;
    private readonly Func<string, Task>? _copy;

    private LaneMenuViewModel(InspectWorkspaceViewModel workspace, TimelineLane lane, Func<string, Task>? copy)
    {
        _workspace = workspace;
        _lane = lane;
        _copy = copy;
    }

    /// <summary>The menu for <paramref name="key"/>; null when the timeline has no such lane.</summary>
    /// <param name="workspace">The Inspect workspace (its timeline, panes and sync bus).</param>
    /// <param name="key">The lane right-clicked.</param>
    /// <param name="copy">Puts text on the clipboard; null disables "Copy name".</param>
    public static LaneMenuViewModel? For(InspectWorkspaceViewModel workspace, LaneKey key, Func<string, Task>? copy)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return workspace.Timeline.Model.FindLane(key) is { } lane ? new LaneMenuViewModel(workspace, lane, copy) : null;
    }

    private TimelineModel Model => _workspace.Timeline.Model;

    private TimeWindow Window => _workspace.Timeline.Sync.Window.IsEmpty ? Model.Bounds : _workspace.Timeline.Sync.Window;

    /// <summary>The lane.</summary>
    public LaneKey Lane => _lane.Key;

    /// <summary>The glyph before the header's name.</summary>
    public LaneGlyph Glyph => _lane.Glyph;

    /// <summary>The header's bold name ("Pipe 3·1", "row 2").</summary>
    public string Name => _lane.Key.Kind == LaneKind.InvocationRow ? TimelineModel.RowLabel(_lane.Key.Index) : _lane.Label;

    /// <summary>The header's secondary detail ("FileUpload · raw"); null for none.</summary>
    public string? Detail => _lane.SubLabel;

    /// <summary>What "Copy name" copies: the name and its detail.</summary>
    public string CopyText => Detail is null ? Name : Name + " " + Detail;

    /// <summary>The ✓ on "Pin lane".</summary>
    public bool IsPinned => Model.IsPinned(Lane);

    /// <summary>The ✓ on "Expand lane".</summary>
    public bool IsExpanded => Model.IsExpanded(Lane);

    /// <summary>True when the lane has frames in view to trace (and to show in the Ladder or Hex stream).</summary>
    public bool CanTrace => Model.TryGetTraceTarget(Lane, Window, out _, out _);

    /// <summary>
    /// True for a pipe, channel or collection lane whose entity closed in or before the window: "Close pipe in view"
    /// removes it now instead of when it scrolls out of view.
    /// </summary>
    public bool CanClose => _lane.Visibility == LaneVisibility.Lifetime && _lane.LifeEnd != long.MaxValue && _lane.LifeEnd <= Window.End;

    /// <summary>"Compare this pipe across files…" waits for the Compare workspace.</summary>
    public bool CanCompare => false;

    /// <summary>"Select all frames" waits for a selection that can hold many frames.</summary>
    public bool CanSelectAll => false;

    /// <summary>True when "Copy name" can reach the clipboard.</summary>
    public bool CanCopy => _copy is not null;

    /// <summary>Pins or unpins the lane (P).</summary>
    public void TogglePin()
    {
        // The timeline's own command, so the menu and P (and a gutter double-click) take one path.
        object parameter = Lane;
        if (_workspace.Timeline.TogglePinCommand.CanExecute(parameter)) _workspace.Timeline.TogglePinCommand.Execute(parameter);
    }

    /// <summary>Expands the lane to 24 px, or lets it go back (Space).</summary>
    public void ToggleExpanded() => Model.ToggleExpanded(Lane);

    /// <summary>Hides the lane (H); the group header's menu shows it again.</summary>
    public void Hide() => Model.SetHidden(Lane, true);

    /// <summary>Traces the lane's frames in view (T): its pipe, its first invocation or its first frame there.</summary>
    public bool Trace()
    {
        if (!Model.TryGetTraceTarget(Lane, Window, out Selection selection, out long ticks)) return false;
        _workspace.Timeline.Sync.Select(selection, ticks);
        return true;
    }

    /// <summary>Traces the lane and shows its frames in <paramref name="kind"/> (a lower pane switches to it).</summary>
    public bool ShowIn(StreamPaneKind kind)
    {
        _workspace.Show(kind);
        return Trace();
    }

    /// <summary>Removes a lane whose pipe closed in view.</summary>
    public void CloseInView()
    {
        if (CanClose) Model.SetHidden(Lane, true);
    }

    /// <summary>Copies the lane's name and detail.</summary>
    public async Task<bool> CopyNameAsync()
    {
        if (_copy is not { } copy) return false;
        await copy(CopyText).ConfigureAwait(true);
        return true;
    }
}
