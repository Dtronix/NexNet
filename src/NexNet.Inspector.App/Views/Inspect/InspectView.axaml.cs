using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using NexNet.Inspector.App.ViewModels;

namespace NexNet.Inspector.App.Views.Inspect;

/// <summary>
/// The Inspect workspace; see InspectView.axaml. Code does what bindings cannot: the right pane's column gives its
/// width back when the pane closes, and the timeline is held to a share of the height so the panes keep room.
/// </summary>
public sealed partial class InspectView : UserControl
{
    /// <summary>The most of the workspace's height the timeline takes; past it its lanes scroll.</summary>
    public const double TimelineShare = 0.5;

    private InspectWorkspaceViewModel? _model;

    public InspectView() => InitializeComponent();

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
