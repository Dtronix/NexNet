using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Controls;
using NexNet.Inspector.App.Controls.DataTable;
using NexNet.Inspector.App.Controls.Ladder;
using NexNet.Inspector.App.Controls.PropertyTree;
using NexNet.Inspector.App.Controls.Timeline;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Views.Inspect;

/// <summary>
/// The Inspect workspace; see InspectView.axaml. Code does what bindings cannot: the right pane's column gives its
/// width back when the pane closes, the timeline is held to a share of the height so the panes keep room, the timeline's
/// lane and group menus open on a right-click (Ctrl+C copies the hovered lane's name), the splitters resize the timeline,
/// the stream panes and the inspector (saved in the settings), and every content view's rows open the content menu on a
/// right-click and copy on Ctrl+C (Events, Ladder, Message trace, the Decoded and Handshake trees).
/// </summary>
public sealed partial class InspectView : UserControl
{
    /// <summary>The most of the workspace's height the timeline takes; past it its lanes scroll.</summary>
    public const double TimelineShare = 0.5;

    private InspectWorkspaceViewModel? _model;
    private PaneSizesViewModel? _sizes;
    private double _dragStart;

    public InspectView()
    {
        InitializeComponent();
        Timeline.ContextRequested += OnTimelineContextRequested;
        Timeline.KeyDown += OnTimelineKeyDown;
        AddHandler(ContextRequestedEvent, OnContentContextRequested, RoutingStrategies.Bubble);
        // Tunnelling: a list (ListBox) would take Ctrl+C for its own copy before it bubbled up here.
        AddHandler(KeyDownEvent, OnContentKeyDown, RoutingStrategies.Tunnel);

        TimelineSplitter.DragStarted += _ => _dragStart = TimelineArea.Bounds.Height;
        TimelineSplitter.DragDelta += (_, delta) => SetTimelineHeight(_dragStart + delta, save: false);
        TimelineSplitter.DragCompleted += _ => _sizes?.Save();
        TimelineSplitter.ResetRequested += _ => _sizes?.ResetTimeline();
        PanesSplitter.DragStarted += _ => _dragStart = LeftPane.Bounds.Width;
        PanesSplitter.DragDelta += (_, delta) => SetLeftWidth(_dragStart + delta, save: false);
        PanesSplitter.DragCompleted += _ => _sizes?.Save();
        PanesSplitter.ResetRequested += _ => _sizes?.ResetLeftShare();
        InspectorSplitter.DragStarted += _ => _dragStart = InspectorArea.Bounds.Width;
        InspectorSplitter.DragDelta += (_, delta) => SetInspectorWidth(_dragStart - delta, save: false);
        InspectorSplitter.DragCompleted += _ => _sizes?.Save();
        InspectorSplitter.ResetRequested += _ => _sizes?.ResetInspector();
    }

    /// <summary>The timeline menu last opened (tests and the capture read it).</summary>
    public ContextMenu? TimelineMenu { get; private set; }

    /// <summary>The content menu last opened (tests and the capture read it).</summary>
    public ContextMenu? ContentMenu { get; private set; }

    /// <summary>The lane menu's model for <paramref name="lane"/>, with the window's clipboard; null without that lane.</summary>
    public LaneMenuViewModel? CreateLaneMenu(LaneKey lane) => _model is { } model ? LaneMenuViewModel.For(model, lane, Copy) : null;

    /// <summary>Builds and opens the menu for what the timeline was right-clicked on (a lane, or a group header).</summary>
    public ContextMenu? OpenTimelineMenu() => OpenTimelineMenu(null);

    /// <summary>
    /// Builds and opens the timeline's menu: the lane's (or group header's) items, Export this range… (the time window as
    /// a recording of its own), and when <paramref name="frame"/> names a frame mark, that frame's content items.
    /// </summary>
    public ContextMenu? OpenTimelineMenu(FrameKey? frame)
    {
        if (_model is not { } model) return null;
        ContextMenu? menu = null;
        if ((Timeline.ContextLane ?? Timeline.HoveredLane) is { } lane && CreateLaneMenu(lane) is { } laneMenu)
            menu = InspectMenus.Lane(laneMenu);
        else if (Timeline.ContextGroup is { } group)
            menu = InspectMenus.Group(model.Timeline.Model, group);
        if (menu is null) return null;
        if (model.Session is not null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(InspectMenus.Item(menu, "Filter to this range", null, () => model.FilterToView(), icon: "⊡", enabled: model.CanFilterToView));
            if (model.ShowExport is { } export)
                menu.Items.Add(InspectMenus.Item(menu, "Export this range…", new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Shift),
                    () => export(ExportRangeKind.Window)));
        }

        if (frame is { } key && model.Session is { } session && ContentItems.ForFrame(session, key) is { } item)
            ContentMenus.Append(menu, CreateContentMenu(item));
        TimelineMenu = menu;
        menu.Open(Timeline);
        return menu;
    }

    /// <summary>The content menu's model for <paramref name="item"/>, over the active session, the sync bus and the window.</summary>
    public ContentMenuViewModel CreateContentMenu(ContentItem item) =>
        new(item, _model?.Session, _model?.Sync, ViewServices.CopyTo(this), ViewServices.SaveBytesTo(this));

    /// <summary>
    /// The item a content view shows at <paramref name="source"/> (a row of Events, Ladder, Message trace or a property
    /// tree), or the focused row when <paramref name="point"/> is null; null when there is none. A right-clicked
    /// Events or Ladder row is selected first, so the menu acts on what the views then show.
    /// </summary>
    public ContentItem? DescribeAt(Visual source, Point? point)
    {
        if (_model is not { } model) return null;
        CaptureSession? session = model.Session;
        if (source.FindAncestorOfType<LadderView>(includeSelf: true) is { Model: { } ladderModel } ladder)
        {
            int index = point is { } p ? ladder.IndexAt(source.TranslatePoint(p, ladder) ?? p) : ladder.SelectedIndex;
            if ((uint)index >= (uint)ladderModel.Count) return null;
            if (point is not null) ladder.Select(index);
            return ContentItems.ForLadder(session, ladderModel, ladderModel[index]);
        }

        if (source.FindAncestorOfType<DataTableControl>(includeSelf: true) is { DataContext: EventsViewModel events } table)
        {
            int index = point is not null && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } container
                ? table.IndexFromContainer(container)
                : table.SelectedIndex;
            if (index < 0 || !events.Rows.TryGetRow(index, out var row)) return null;
            if (point is not null) table.SelectedIndex = index;
            return ContentItems.ForEvent(events.Rows.Session, row);
        }

        if (source.FindAncestorOfType<ListBox>(includeSelf: true) is { } list)
        {
            object? data = point is not null
                ? source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext
                : list.SelectedItem;
            switch (data)
            {
                case TraceStepRow step:
                    return ContentItems.ForTraceStep(session, step, model.Sync.Trace.Message);
                case PropertyRow property when list is PropertyTreeView:
                    return ContentItems.ForProperty(property);
            }
        }

        return null;
    }

    private void OnContentContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || e.Source is not Control source) return;
        Point? point = e.TryGetPosition(source, out Point at) ? at : null;
        if (DescribeAt(source, point) is not { } item) return;
        e.Handled = true;
        OpenContentMenu(item, source);
    }

    /// <summary>Opens the content menu for <paramref name="item"/> at <paramref name="target"/>.</summary>
    public ContextMenu OpenContentMenu(ContentItem item, Control target)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(target);
        Action? export = _model?.ShowExport is { } show && _model.Session is not null
            ? () => show(item.Bytes.Kind == ContentBytesKind.Frame && !_model.Sync.Selection.IsNone ? ExportRangeKind.Message : ExportRangeKind.Window)
            : null;
        ContextMenu menu = ContentMenus.Build(CreateContentMenu(item), export);
        ContentMenu = menu;
        menu.Open(target);
        return menu;
    }

    private void OnContentKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.C || (e.KeyModifiers & KeyModifiers.Control) == 0 || e.Source is not Visual source) return;
        if (source.FindAncestorOfType<TimelineControl>(includeSelf: true) is not null) return;
        if (DescribeAt(source, null) is not { } item) return;
        e.Handled = true;
        ViewServices.Run(() => Copy(item.Text), "copy");
    }

    private void OnTimelineContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        FrameKey? frame = null;
        if (e.TryGetPosition(Timeline, out Point point) && Timeline.HitTest(point) is { Lane: { } lane, Mark: >= 0 } hit
            && hit.Mark < lane.Marks.Length && lane.Marks[hit.Mark] is { Seq: >= 0 } mark && (mark.Flags & MarkFlags.Op) == 0)
            frame = new FrameKey(mark.Direction, mark.Seq);
        e.Handled = OpenTimelineMenu(frame) is not null;
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

    private Task Copy(string text) => ViewServices.Copy(this, text);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        _model = DataContext as InspectWorkspaceViewModel;
        if (_model is not null) _model.PropertyChanged += OnModelChanged;
        AttachSizes(_model?.Sizes);
        UpdatePanes();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && Timeline is not null) ApplyTimelineHeight();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InspectWorkspaceViewModel.ShowsRightPane)) UpdatePanes();
        else if (e.PropertyName == nameof(InspectWorkspaceViewModel.Sizes)) AttachSizes(_model?.Sizes);
    }

    private void AttachSizes(PaneSizesViewModel? sizes)
    {
        if (ReferenceEquals(_sizes, sizes)) return;
        if (_sizes is not null) _sizes.PropertyChanged -= OnSizesChanged;
        _sizes = sizes;
        if (_sizes is not null) _sizes.PropertyChanged += OnSizesChanged;
        ApplyTimelineHeight();
        UpdatePanes();
    }

    private void OnSizesChanged(object? sender, PropertyChangedEventArgs e)
    {
        ApplyTimelineHeight();
        UpdatePanes();
    }

    /// <summary>Drags the timeline's bottom edge to <paramref name="height"/>, clamped so the panes keep their room.</summary>
    public void SetTimelineHeight(double height, bool save)
    {
        double clamped = PaneLayoutPolicy.ClampTimeline(height, Bounds.Height);
        _sizes?.Update(s => s with { TimelineHeight = clamped }, save);
    }

    /// <summary>Drags the stream panes' boundary so the left pane is <paramref name="width"/> wide.</summary>
    public void SetLeftWidth(double width, bool save)
    {
        double share = PaneLayoutPolicy.ShareFor(width, Panes.Bounds.Width);
        _sizes?.Update(s => s with { LeftShare = share }, save);
    }

    /// <summary>Drags the inspector's left edge so it is <paramref name="width"/> wide (at most 280 px while narrowed, OQ10).</summary>
    public void SetInspectorWidth(double width, bool save)
    {
        double clamped = PaneLayoutPolicy.ClampInspector(width, Lower.Bounds.Width);
        if (_model is { } model && PaneLayoutPolicy.InspectorWidthFor(model.Layout, clamped) < clamped)
            clamped = Math.Max(PaneLayoutPolicy.MinInspectorWidth, PaneLayoutPolicy.InspectorWidthFor(model.Layout, clamped));
        _sizes?.Update(s => s with { InspectorWidth = clamped }, save);
    }

    private void ApplyTimelineHeight()
    {
        if (TimelineArea is null) return;
        if (_sizes?.Sizes.TimelineHeight is { } height && Bounds.Height > 0)
        {
            // A dragged height: the timeline area is that tall (its lanes scroll or leave room), within the workspace.
            TimelineArea.Height = PaneLayoutPolicy.ClampTimeline(height, Bounds.Height);
            Timeline.MaxHeight = double.PositiveInfinity;
        }
        else
        {
            TimelineArea.Height = double.NaN;
            Timeline.MaxHeight = Math.Max(0, Bounds.Height * TimelineShare);
        }
    }

    private void UpdatePanes()
    {
        if (Panes is null) return;
        double share = _sizes?.Sizes.LeftShare ?? PaneLayoutPolicy.DefaultLeftShare;
        bool split = _model is { ShowsRightPane: true };
        Panes.ColumnDefinitions[0].Width = new GridLength(split ? share : 1, GridUnitType.Star);
        Panes.ColumnDefinitions[1].Width = split ? new GridLength(1 - share, GridUnitType.Star) : new GridLength(0);
    }
}
