using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Controls.Metadata;
using Avalonia.Input;
using Avalonia.Threading;
using NexNet.Inspector.App.Controls.Primitives;

namespace NexNet.Inspector.App.Controls.DataTable;

/// <summary>
/// The data-table control (PKG controls/data-table): a header row over virtualized, dense, drawn rows, with hover,
/// keyboard focus, selection and trace-tint states, skeleton bars while loading and a tertiary hint when empty (OQ2). A
/// <see cref="ListBox"/> over a <see cref="VirtualizingStackPanel"/>, so only the visible rows exist; its items are a
/// <see cref="DataTableSource"/>, which prepares each realized <see cref="DataTableRow"/> from its index. Variants
/// (Events now; field diff and session list later) differ in their <see cref="Columns"/> and source.
/// </summary>
/// <remarks>
/// Select by <see cref="SelectingItemsControl.SelectedIndex"/> (the items are placeholders). With
/// <see cref="FollowTail"/> on, rows added at the end scroll into view; the user scrolling up turns it off and scrolling
/// back down to the end turns it on again (<see cref="FollowTailRule"/>), so bind it two-way.
/// </remarks>
[TemplatePart("PART_Header", typeof(DataTableHeader))]
[TemplatePart("PART_Skeleton", typeof(DataTableSkeleton))]
[TemplatePart("PART_Empty", typeof(Control))]
public sealed class DataTableControl : ListBox
{
    /// <summary>The columns, left to right.</summary>
    public static readonly StyledProperty<IReadOnlyList<DataTableColumn>?> ColumnsProperty =
        AvaloniaProperty.Register<DataTableControl, IReadOnlyList<DataTableColumn>?>(nameof(Columns));

    /// <summary>A row's height above its 1 px bottom border (Size.EventRow: 20; session lists 26).</summary>
    public static readonly StyledProperty<double> RowHeightProperty =
        AvaloniaProperty.Register<DataTableControl, double>(nameof(RowHeight), 20);

    /// <summary>The header row's height above its border; NaN (the default) follows <see cref="RowHeight"/>.</summary>
    public static readonly StyledProperty<double> HeaderHeightProperty =
        AvaloniaProperty.Register<DataTableControl, double>(nameof(HeaderHeight), double.NaN);

    /// <summary>Space before the first column and after the last; the right side leaves room for the scroll rail.</summary>
    public static readonly StyledProperty<Thickness> CellPaddingProperty =
        AvaloniaProperty.Register<DataTableControl, Thickness>(nameof(CellPadding), new Thickness(8, 0, 16, 0));

    /// <summary>True while more rows are coming (a file indexing): skeleton bars instead of, or after, the rows.</summary>
    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<DataTableControl, bool>(nameof(IsLoading));

    /// <summary>The hint shown when there are no rows and none are loading.</summary>
    public static readonly StyledProperty<string?> EmptyTextProperty =
        AvaloniaProperty.Register<DataTableControl, string?>(nameof(EmptyText));

    /// <summary>True while rows added at the end scroll into view ("Follow live").</summary>
    public static readonly StyledProperty<bool> FollowTailProperty =
        AvaloniaProperty.Register<DataTableControl, bool>(nameof(FollowTail), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Run by Enter or a double-click on a row (Events: trace the message), with no parameter.</summary>
    public static readonly StyledProperty<ICommand?> ActivateCommandProperty =
        AvaloniaProperty.Register<DataTableControl, ICommand?>(nameof(ActivateCommand));

    /// <summary>
    /// The selected row's 3 px left bar: accent.trace in Events (the selection is the trace's), direction.inbound in a
    /// session list (the open-recording table, like its radios).
    /// </summary>
    public static readonly StyledProperty<ThemeBrush> SelectionBarProperty =
        AvaloniaProperty.Register<DataTableControl, ThemeBrush>(nameof(SelectionBar), ThemeBrush.AccentTrace);

    /// <summary>What the table shows below its header.</summary>
    public static readonly DirectProperty<DataTableControl, DataTableState> StateProperty =
        AvaloniaProperty.RegisterDirect<DataTableControl, DataTableState>(nameof(State), static t => t.State);

    private DataTableColumn[] _columns = [];
    private DataTableColumnSlot[] _slots = [];
    private double _slotsWidth = -1;
    private DataTableSource? _source;
    private DataTableHeader? _header;
    private DataTableSkeleton? _skeleton;
    private Control? _empty;
    private ScrollViewer? _scroll;
    private DataTableState _state = DataTableState.Empty;
    private bool _tailPending;

    static DataTableControl()
    {
        SelectionModeProperty.OverrideDefaultValue<DataTableControl>(SelectionMode.Single);
    }

    public DataTableControl()
    {
        AddHandler(KeyDownEvent, OnKeyDownTunnel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    public IReadOnlyList<DataTableColumn>? Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public double RowHeight
    {
        get => GetValue(RowHeightProperty);
        set => SetValue(RowHeightProperty, value);
    }

    public double HeaderHeight
    {
        get => GetValue(HeaderHeightProperty);
        set => SetValue(HeaderHeightProperty, value);
    }

    public Thickness CellPadding
    {
        get => GetValue(CellPaddingProperty);
        set => SetValue(CellPaddingProperty, value);
    }

    public bool IsLoading
    {
        get => GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public string? EmptyText
    {
        get => GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    public ThemeBrush SelectionBar
    {
        get => GetValue(SelectionBarProperty);
        set => SetValue(SelectionBarProperty, value);
    }

    public bool FollowTail
    {
        get => GetValue(FollowTailProperty);
        set => SetValue(FollowTailProperty, value);
    }

    public ICommand? ActivateCommand
    {
        get => GetValue(ActivateCommandProperty);
        set => SetValue(ActivateCommandProperty, value);
    }

    public DataTableState State
    {
        get => _state;
        private set => SetAndRaise(StateProperty, ref _state, value);
    }

    /// <summary>Each column's slot across the current width (rows and the header draw on these).</summary>
    public ReadOnlySpan<DataTableColumnSlot> ColumnSlots => _slots;

    /// <summary>The source the table shows (its <see cref="ItemsControl.ItemsSource"/>).</summary>
    public DataTableSource? Source => _source;

    /// <summary>The scroll viewer of the rows, once templated.</summary>
    public ScrollViewer? Scroller => _scroll;

    /// <summary>The rows realized now: the visible ones and the panel's small buffer, never one per source row.</summary>
    public int RealizedCount => GetRealizedContainers().Count();

    protected override Type StyleKeyOverride => typeof(DataTableControl);

    /// <summary>Scrolls the last row into view.</summary>
    public void ScrollToTail()
    {
        int count = _source?.Count ?? 0;
        if (count > 0) ScrollIntoView(count - 1);
    }

    /// <summary>Shows hover and keyboard focus on rows without input (the control gallery); -1 for none.</summary>
    internal void Preview(int hoverIndex, int focusIndex)
    {
        foreach (Control container in GetRealizedContainers())
        {
            if (container is DataTableRow row)
            {
                int index = IndexFromContainer(row);
                row.Preview(index == hoverIndex, index == focusIndex);
            }
        }
    }

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        recycleKey = DefaultRecycleKey;
        return true;
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) => new DataTableRow();

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        // Not the base: it would make the placeholder the row's content and data context. The row draws from its index.
        if (container is DataTableRow row) PrepareRow(row, index);
    }

    protected override void ContainerIndexChangedOverride(Control container, int oldIndex, int newIndex)
    {
        base.ContainerIndexChangedOverride(container, oldIndex, newIndex);
        if (container is DataTableRow row) PrepareRow(row, newIndex);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_scroll is not null) _scroll.ScrollChanged -= OnScrollChanged;
        base.OnApplyTemplate(e);
        _header = e.NameScope.Find<DataTableHeader>("PART_Header");
        _skeleton = e.NameScope.Find<DataTableSkeleton>("PART_Skeleton");
        _empty = e.NameScope.Find<Control>("PART_Empty");
        _scroll = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
        if (_header is not null)
        {
            _header.Table = this;
            _header.RowHeight = HeaderRowHeight;
        }

        if (_skeleton is not null) _skeleton.BarHeight = RowHeight;
        if (_scroll is not null) _scroll.ScrollChanged += OnScrollChanged;
        UpdateState();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Size arranged = base.ArrangeOverride(finalSize);
        double width = Math.Max(0, finalSize.Width - BorderThickness.Left - BorderThickness.Right);
        if (width != _slotsWidth) Relayout(width);
        return arranged;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty)
        {
            if (_source is not null)
            {
                _source.CollectionChanged -= OnSourceChanged;
                _source.RowsChanged -= OnRowsChanged;
            }

            _source = change.NewValue as DataTableSource;
            if (_source is not null)
            {
                _source.CollectionChanged += OnSourceChanged;
                _source.RowsChanged += OnRowsChanged;
            }

            UpdateState();
        }
        else if (change.Property == ColumnsProperty || change.Property == CellPaddingProperty)
        {
            _columns = Columns is { } columns ? [.. columns] : [];
            if (_slotsWidth >= 0) Relayout(_slotsWidth);
        }
        else if (change.Property == RowHeightProperty || change.Property == HeaderHeightProperty)
        {
            if (_header is not null)
            {
                _header.RowHeight = HeaderRowHeight;
                _header.InvalidateMeasure();
            }

            if (_skeleton is not null) _skeleton.BarHeight = RowHeight;
            foreach (Control container in GetRealizedContainers()) container.Height = RowHeight + 1;
        }
        else if (change.Property == IsLoadingProperty)
        {
            UpdateState();
        }
        else if (change.Property == FollowTailProperty && FollowTail)
        {
            RequestTail();
        }
    }

    // Tunnelling: a focused row (ListBoxItem) takes Enter for itself before it would bubble up to the table.
    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        if (SelectedIndex >= 0 && ActivateCommand is { } command && command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
    }

    protected override void OnDoubleTapped(TappedEventArgs e)
    {
        base.OnDoubleTapped(e);
        if (SelectedIndex >= 0 && ActivateCommand is { } command && command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TableAutomationPeer(this);

    private double HeaderRowHeight => double.IsNaN(HeaderHeight) ? RowHeight : HeaderHeight;

    private void PrepareRow(DataTableRow row, int index)
    {
        row.Table = this;
        row.Height = RowHeight + 1;
        if (_source is { } source && (uint)index < (uint)source.Count)
        {
            row.Visual ??= source.CreateVisual();
            source.Prepare(row.Visual, index);
        }

        row.Refresh();
    }

    private void Relayout(double width)
    {
        _slotsWidth = width;
        if (_slots.Length != _columns.Length) _slots = new DataTableColumnSlot[_columns.Length];
        DataTableColumnLayout.Arrange(_columns, width, CellPadding.Left, CellPadding.Right, _slots);

        _header?.InvalidateVisual();
        foreach (Control container in GetRealizedContainers()) container.InvalidateVisual();
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateState();
        if (FollowTail && e.Action == NotifyCollectionChangedAction.Add) RequestTail();
    }

    private void OnRowsChanged()
    {
        foreach (Control container in GetRealizedContainers())
        {
            if (container is DataTableRow row) PrepareRow(row, IndexFromContainer(row));
        }
    }

    private void RequestTail()
    {
        // After the panel has taken the new rows in: scrolling to an index it has not heard of yet would stop short.
        if (_tailPending) return;
        _tailPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _tailPending = false;
            if (FollowTail) ScrollToTail();
        }, DispatcherPriority.Loaded);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scroll is { } scroll)
        {
            FollowTailDecision decision = FollowTailRule.Decide(e.OffsetDelta.Y, e.ExtentDelta.Y, e.ViewportDelta.Y, scroll.Offset.Y,
                scroll.Viewport.Height, scroll.Extent.Height, FollowTail);
            if (decision == FollowTailDecision.Pause) SetCurrentValue(FollowTailProperty, false);
            else if (decision == FollowTailDecision.Resume) SetCurrentValue(FollowTailProperty, true);
        }

        PlaceSkeleton();
    }

    private void UpdateState()
    {
        int count = _source?.Count ?? 0;
        State = count > 0 ? DataTableState.Rows : IsLoading ? DataTableState.Loading : DataTableState.Empty;
        if (_empty is not null) _empty.IsVisible = State == DataTableState.Empty;
        PlaceSkeleton();
    }

    // Loading with rows: the bars continue below the last row, in whatever of the viewport the rows leave.
    private void PlaceSkeleton()
    {
        if (_skeleton is null) return;
        if (State == DataTableState.Loading)
        {
            _skeleton.IsVisible = true;
            _skeleton.Margin = default;
            return;
        }

        if (State == DataTableState.Rows && IsLoading && _scroll is { } scroll)
        {
            double top = ((_source?.Count ?? 0) * (RowHeight + 1)) - scroll.Offset.Y;
            bool visible = top < scroll.Viewport.Height;
            _skeleton.IsVisible = visible;
            if (visible) _skeleton.Margin = new Thickness(0, Math.Max(0, top), 0, 0);
            return;
        }

        _skeleton.IsVisible = false;
    }

    // A grid for assistive technology (README: grid role, row and column headers).
    private sealed class TableAutomationPeer(DataTableControl owner) : ListBoxAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataGrid;

        protected override string? GetNameCore()
        {
            IReadOnlyList<DataTableColumn>? columns = ((DataTableControl)Owner).Columns;
            if (columns is null) return base.GetNameCore();
            List<string> headers = [];
            foreach (DataTableColumn column in columns)
            {
                if (column.Header.Length > 0) headers.Add(column.Header);
            }

            return "Table: " + string.Join(", ", headers);
        }
    }
}
