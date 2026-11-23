using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using NexNet.Inspector.App.Controls.Timeline;
using NexNet.Inspector.App.ViewModels;

namespace NexNet.Inspector.App.Views.Inspect;

/// <summary>
/// The Inspect workspace; see InspectView.axaml. Code does what bindings cannot: the right pane's column gives its
/// width back when the pane closes, the timeline is held to a share of the height so the panes keep room, and the
/// timeline's lane and group menus open on a right-click (Ctrl+C copies the hovered lane's name).
/// </summary>
public sealed partial class InspectView : UserControl
{
    /// <summary>The most of the workspace's height the timeline takes; past it its lanes scroll.</summary>
    public const double TimelineShare = 0.5;

    private InspectWorkspaceViewModel? _model;

    public InspectView()
    {
        InitializeComponent();
        Timeline.ContextRequested += OnTimelineContextRequested;
        Timeline.KeyDown += OnTimelineKeyDown;
    }

    /// <summary>The timeline menu last opened (tests and the capture read it).</summary>
    public ContextMenu? TimelineMenu { get; private set; }

    /// <summary>The lane menu's model for <paramref name="lane"/>, with the window's clipboard; null without that lane.</summary>
    public LaneMenuViewModel? CreateLaneMenu(LaneKey lane) => _model is { } model ? LaneMenuViewModel.For(model, lane, Copy) : null;

    /// <summary>Builds and opens the menu for what the timeline was right-clicked on (a lane, or a group header).</summary>
    public ContextMenu? OpenTimelineMenu()
    {
        if (_model is not { } model) return null;
        ContextMenu? menu = null;
        if ((Timeline.ContextLane ?? Timeline.HoveredLane) is { } lane && CreateLaneMenu(lane) is { } laneMenu)
            menu = InspectMenus.Lane(laneMenu);
        else if (Timeline.ContextGroup is { } group)
            menu = InspectMenus.Group(model.Timeline.Model, group);
        if (menu is null) return null;
        TimelineMenu = menu;
        menu.Open(Timeline);
        return menu;
    }

    private void OnTimelineContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        e.Handled = OpenTimelineMenu() is not null;
    }

    private async void OnTimelineKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.C || (e.KeyModifiers & KeyModifiers.Control) == 0) return;
        if (Timeline.HoveredLane is not { } lane || CreateLaneMenu(lane) is not { } menu) return;
        e.Handled = true;
        try
        {
            await menu.CopyNameAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or AccessViolationException or InvalidProgramException))
        {
            // A shortcut runs on the dispatcher: a failed copy must not end the app.
            System.Diagnostics.Trace.TraceError("copy lane name failed: " + ex);
        }
    }

    private Task Copy(string text) =>
        TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard ? clipboard.SetTextAsync(text) : Task.CompletedTask;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        _model = DataContext as InspectWorkspaceViewModel;
        if (_model is not null) _model.PropertyChanged += OnModelChanged;
        UpdatePanes();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && Timeline is not null) Timeline.MaxHeight = Math.Max(0, Bounds.Height * TimelineShare);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InspectWorkspaceViewModel.ShowsRightPane)) UpdatePanes();
    }

    private void UpdatePanes()
    {
        if (Panes is null) return;
        Panes.ColumnDefinitions[1].Width = _model is { ShowsRightPane: true } ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }
}
