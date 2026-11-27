using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using NexNet.Inspector.App.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Controls.Compare;
using NexNet.Inspector.App.Controls.DataTable;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views.Inspect;

namespace NexNet.Inspector.App.Views.Compare;

/// <summary>
/// The Compare workspace over a <c>CompareWorkspaceViewModel</c>. Code sizes the sidebar and the bottom panel from the
/// splitters (saved in the settings), and opens the content menu (Copy, Copy value, Copy path, Copy row as TSV) on the
/// field diff's rows and the entity sidebar's entries, which Ctrl+C copies as well.
/// </summary>
public partial class CompareView : UserControl
{
    private CompareWorkspaceViewModel? _model;
    private PaneSizesViewModel? _sizes;
    private double _dragStart;

    public CompareView()
    {
        InitializeComponent();
        ScrollRail.Attach(RowsScroll, RowsHost);
        AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Bubble);
        // Tunnelling: a list (ListBox) would take Ctrl+C for its own copy before it bubbled up here.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        SidebarSplitter.DragStarted += _ => _dragStart = Sidebar.Bounds.Width;
        SidebarSplitter.DragDelta += (_, delta) => SetSidebarWidth(_dragStart + delta, save: false);
        SidebarSplitter.DragCompleted += _ => _sizes?.Save();
        SidebarSplitter.ResetRequested += _ => _sizes?.ResetCompareSidebar();
        DetailSplitter.DragStarted += _ => _dragStart = DetailArea.Bounds.Height;
        DetailSplitter.DragDelta += (_, delta) => SetDetailHeight(_dragStart - delta, save: false);
        DetailSplitter.DragCompleted += _ => _sizes?.Save();
        DetailSplitter.ResetRequested += _ => _sizes?.ResetCompareDetail();
    }

    /// <summary>The rows' own scroll bar (the rail).</summary>
    public Avalonia.Controls.Primitives.ScrollBar RowsScrollBar => RowsScroll;

    /// <summary>The drawn aligned rows.</summary>
    public CompareRowsControl RowsView => Rows;

    /// <summary>The Entities sidebar's list.</summary>
    public ListBox EntityTree => EntityList;

    /// <summary>The content menu last opened (tests read it).</summary>
    public ContextMenu? ContentMenu { get; private set; }

    /// <summary>Drags the sidebar's edge so it is <paramref name="width"/> wide.</summary>
    public void SetSidebarWidth(double width, bool save)
    {
        double clamped = PaneLayoutPolicy.ClampCompareSidebar(width, Bounds.Width);
        _sizes?.Update(s => s with { CompareSidebarWidth = clamped }, save);
    }

    /// <summary>Drags the bottom panel's top edge so it is <paramref name="height"/> tall.</summary>
    public void SetDetailHeight(double height, bool save)
    {
        double clamped = PaneLayoutPolicy.ClampCompareDetail(height, Bounds.Height - 120);
        _sizes?.Update(s => s with { CompareDetailHeight = clamped }, save);
    }

    /// <summary>The item at <paramref name="source"/> (a field-diff row, an entity), or the focused one without a point.</summary>
    public ContentItem? DescribeAt(Visual source, Point? point)
    {
        if (source.FindAncestorOfType<DataTableControl>(includeSelf: true) is { DataContext: FieldDiffViewModel diff } table)
        {
            int index = point is not null && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } container
                ? table.IndexFromContainer(container)
                : table.SelectedIndex;
            if ((uint)index >= (uint)diff.Rows.Count) return null;
            return ContentItems.ForFieldDiff(diff.Rows.Format(index), diff.NameA, diff.NameB);
        }

        if (source.FindAncestorOfType<ListBox>(includeSelf: true) == EntityList)
        {
            object? data = point is not null ? source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext : EntityList.SelectedItem;
            if (data is CompareEntityItem entity) return ContentItems.ForEntity(entity.Label, entity.Count, entity.HasDelta);
        }

        return null;
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
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
        ArgumentNullException.ThrowIfNull(target);
        ContextMenu menu = ContentMenus.Build(new ContentMenuViewModel(item, null, null, ViewServices.CopyTo(this), null));
        ContentMenu = menu;
        menu.Open(target);
        return menu;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.C || (e.KeyModifiers & KeyModifiers.Control) == 0 || e.Source is not Visual source) return;
        if (DescribeAt(source, null) is not { } item) return;
        e.Handled = true;
        ViewServices.Run(() => ViewServices.Copy(this, item.Text), "copy");
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        _model = DataContext as CompareWorkspaceViewModel;
        if (_model is not null) _model.PropertyChanged += OnModelChanged;
        AttachSizes(_model?.Sizes);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CompareWorkspaceViewModel.Sizes)) AttachSizes(_model?.Sizes);
    }

    private void AttachSizes(PaneSizesViewModel? sizes)
    {
        if (ReferenceEquals(_sizes, sizes)) return;
        if (_sizes is not null) _sizes.PropertyChanged -= OnSizesChanged;
        _sizes = sizes;
        if (_sizes is not null) _sizes.PropertyChanged += OnSizesChanged;
        ApplySizes();
    }

    private void OnSizesChanged(object? sender, PropertyChangedEventArgs e) => ApplySizes();

    private void ApplySizes()
    {
        PaneSizes sizes = _sizes?.Sizes ?? new PaneSizes();
        Layout.ColumnDefinitions[0].Width = new GridLength(sizes.CompareSidebarWidth);
        Aligned.RowDefinitions[3].Height = new GridLength(sizes.CompareDetailHeight);
    }
}
