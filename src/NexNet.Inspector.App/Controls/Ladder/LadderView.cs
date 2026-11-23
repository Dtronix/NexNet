using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Controls.Ladder;

/// <summary>
/// The Ladder pane's view (controls/ladder-row, the inspect-live Ladder tab): a 20 px header over a virtualized list
/// of drawn <see cref="LadderRowControl"/>s, one per frame, compact (18 px rows) or time-scaled (OQ6) per
/// <see cref="Mode"/>. It follows the <see cref="SyncBus"/>: a click selects a frame and moves the playhead, a
/// double-click traces the frame's message, the selection scrolls into view and draws the trace bracket, and — while
/// <see cref="IsSynced"/> — scrolling pans the shared time window and moving the window scrolls the ladder.
/// </summary>
public sealed class LadderView : Control
{
    public static readonly StyledProperty<LadderModel?> ModelProperty =
        AvaloniaProperty.Register<LadderView, LadderModel?>(nameof(Model));

    public static readonly StyledProperty<SyncBus?> SyncProperty =
        AvaloniaProperty.Register<LadderView, SyncBus?>(nameof(Sync));

    public static readonly StyledProperty<LadderMode> ModeProperty =
        AvaloniaProperty.Register<LadderView, LadderMode>(nameof(Mode));

    public static readonly StyledProperty<bool> IsSyncedProperty =
        AvaloniaProperty.Register<LadderView, bool>(nameof(IsSynced), true);

    private readonly LadderHeader _header = new();
    private readonly ScrollViewer _scroller;
    private readonly LadderPanel _panel;
    private LadderModel? _model;
    private SyncBus? _sync;
    private LadderTrace _trace = LadderTrace.None;
    private int _selected = -1;
    private int _hovered = -1;
    private double _interleavedOpacity = 0.6;
    private bool _attached;
    private bool _scrolling;
    private bool _pushing;
    private TimeWindow _pushed;
    private double? _pendingOffset;

    static LadderView()
    {
        FocusableProperty.OverrideDefaultValue<LadderView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<LadderView>(true);
    }

    public LadderView()
    {
        _panel = new LadderPanel(this);
        _scroller = new ScrollViewer
        {
            Content = _panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _scroller.PropertyChanged += OnScrollerPropertyChanged;
        VisualChildren.Add(_header);
        VisualChildren.Add(_scroller);
        LogicalChildren.Add(_header);
        LogicalChildren.Add(_scroller);
    }

    /// <summary>The rows drawn.</summary>
    public LadderModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    /// <summary>The time window, playhead and selection shared with every view.</summary>
    public SyncBus? Sync
    {
        get => GetValue(SyncProperty);
        set => SetValue(SyncProperty, value);
    }

    /// <summary>Compact (18 px rows, the default) or time-scaled; the pane header's select sets it (I23).</summary>
    public LadderMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>True while scrolling and the shared time window move together (the pane header's "synced").</summary>
    public bool IsSynced
    {
        get => GetValue(IsSyncedProperty);
        set => SetValue(IsSyncedProperty, value);
    }

    /// <summary>The vertical layout of the rows.</summary>
    public LadderLayout Layout { get; } = new();

    /// <summary>What the trace bracket joins, from the selection.</summary>
    public LadderTrace Trace => _trace;

    /// <summary>The selected frame's row; -1 for none.</summary>
    public int SelectedIndex => _selected;

    /// <summary>The row drawn hovered (the one under the pointer); settable to show the hover state.</summary>
    public int HoveredIndex
    {
        get => _hovered;
        set
        {
            if (value == _hovered) return;
            _hovered = value;
            _panel.Rebind();
        }
    }

    /// <summary>The header.</summary>
    public LadderHeader Header => _header;

    /// <summary>The scroll viewer holding the rows.</summary>
    public ScrollViewer Scroller => _scroller;

    /// <summary>The realized row controls, in row order from <see cref="FirstRealized"/>.</summary>
    public IReadOnlyList<LadderRowControl> RealizedRows => _panel.Active;

    /// <summary>The first realized row's index.</summary>
    public int FirstRealized => _panel.First;

    /// <summary>Row controls ever created: a screenful and a few, however many rows there are.</summary>
    public int RowControls => _panel.Created;

    /// <summary>The realized control of row <paramref name="index"/>; null when it is not in view.</summary>
    public LadderRowControl? RowControl(int index) => _panel.Realized(index);

    /// <summary>The row at <paramref name="point"/> (in this control's coordinates); -1 above or below the rows.</summary>
    public int IndexAt(Point point)
    {
        double y = point.Y - LadderHeader.HeaderHeight + _scroller.Offset.Y;
        if (point.Y < LadderHeader.HeaderHeight || y >= Layout.TotalHeight) return -1;
        return Layout.IndexAt(y);
    }

    /// <summary>Selects row <paramref name="index"/>'s frame and moves the playhead to it.</summary>
    public void Select(int index)
    {
        if (_model is not { } model || _sync is not { } sync || (uint)index >= (uint)model.Count) return;
        ref readonly LadderRow row = ref model[index];
        sync.Select(Selection.OfFrame(row.Frame), row.Ticks);
    }

    /// <summary>Traces row <paramref name="index"/>'s message (a double-click): selects it, from that frame.</summary>
    public void TraceRow(int index)
    {
        if (_model is not { } model || _sync is not { } sync || (uint)index >= (uint)model.Count) return;
        ref readonly LadderRow row = ref model[index];
        MessageRef message = row.Message >= 0 ? model.MessageOf(row.Message) : MessageRef.None;
        if (message.IsNone) message = MessageRef.ForFrame(row.Direction, row.Frame.Seq);
        sync.Select(Selection.OfMessage(message, row.Frame), row.Ticks);
    }

    /// <summary>Scrolls the least distance that shows row <paramref name="index"/> whole.</summary>
    public void ScrollIntoView(int index)
    {
        if ((uint)index >= (uint)Layout.Count) return;
        double top = Layout.Top(index);
        double bottom = top + Layout.Height(index);
        double offset = _scroller.Offset.Y;
        double viewport = _scroller.Viewport.Height;
        if (viewport <= 0) viewport = Math.Max(0, Bounds.Height - LadderHeader.HeaderHeight);
        if (top < offset) ScrollTo(top);
        else if (bottom > offset + viewport) ScrollTo(bottom - viewport);
    }

    /// <summary>Binds a realized row to row <paramref name="index"/> with its current state.</summary>
    internal void BindRow(LadderRowControl control, int index)
    {
        if (_model is not { } model || (uint)index >= (uint)model.Count) return;
        ref readonly LadderRow row = ref model[index];
        LadderBracket bracket = _trace.BracketOf(index, row);
        LadderRowState state = new(index == _selected, index == _hovered, bracket,
            bracket == LadderBracket.Member && index == _trace.First, bracket == LadderBracket.Member && index == _trace.Last);
        control.Bind(index, row, Layout.Gap(index), state, row.Message >= 0 ? model.ChunkTotal(row.Message) : 0, model.GetLabel(row.Name),
            model.Origin, model.Frequency, _interleavedOpacity);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _header.Measure(new Size(availableSize.Width, LadderHeader.HeaderHeight));
        double height = double.IsFinite(availableSize.Height) ? Math.Max(0, availableSize.Height - LadderHeader.HeaderHeight) : double.PositiveInfinity;
        _scroller.Measure(new Size(availableSize.Width, height));
        return new Size(0, LadderHeader.HeaderHeight + (double.IsFinite(height) ? 0 : _scroller.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _header.Arrange(new Rect(0, 0, finalSize.Width, LadderHeader.HeaderHeight));
        _scroller.Arrange(new Rect(0, LadderHeader.HeaderHeight, finalSize.Width, Math.Max(0, finalSize.Height - LadderHeader.HeaderHeight)));

        // An offset asked for before the rows were measured was clamped to the old extent: apply it now that it is known.
        if (_pendingOffset is { } pending)
        {
            _pendingOffset = null;
            if (_scroller.Offset.Y != pending) ScrollTo(pending);
        }

        return finalSize;
    }

    public override void Render(DrawingContext context) =>
        context.FillRectangle(ThemeBrushes.Get(this, ThemeBrush.SurfacePanel), new Rect(Bounds.Size));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty || change.Property == SyncProperty)
        {
            if (_attached) Subscribe();
        }
        else if (change.Property == ModeProperty)
        {
            Relayout(keepTopRow: true);
        }
        else if (change.Property == ThemeVariantScope.ActualThemeVariantProperty)
        {
            InvalidateVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (this.TryFindResource("Opacity.InterleavedRow", out object? value) && value is double opacity) _interleavedOpacity = opacity;
        _attached = true;
        Subscribe();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Unsubscribe();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        int index = IndexAt(e.GetPosition(this));
        if (index < 0) return;
        Focus();
        if (e.ClickCount >= 2) TraceRow(index);
        else Select(index);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        HoveredIndex = IndexAt(e.GetPosition(this));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        HoveredIndex = -1;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_model is not { Count: > 0 } model || e.Handled) return;
        int page = Math.Max(1, (int)(_scroller.Viewport.Height / LadderLayout.RowHeight) - 1);
        int from = _selected < 0 ? -1 : _selected;
        int target = e.Key switch
        {
            Key.Down => from + 1,
            Key.Up => from < 0 ? 0 : from - 1,
            Key.PageDown => from + page,
            Key.PageUp => from - page,
            Key.Home => 0,
            Key.End => model.Count - 1,
            _ => int.MinValue,
        };
        if (e.Key == Key.Enter && _selected >= 0)
        {
            TraceRow(_selected);
            e.Handled = true;
            return;
        }

        if (target == int.MinValue) return;
        target = Math.Clamp(target, 0, model.Count - 1);
        Select(target);
        ScrollIntoView(target);
        e.Handled = true;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new LadderAutomationPeer(this);

    private void Subscribe()
    {
        Unsubscribe();
        _model = Model;
        _sync = Sync;
        if (_model is not null) _model.Changed += OnModelChanged;
        if (_sync is not null) _sync.Changed += OnSyncChanged;
        Refresh();
        if (_sync is { Window.IsEmpty: false } sync && IsSynced) FollowWindow(sync.Window);
    }

    private void Unsubscribe()
    {
        if (_model is not null) _model.Changed -= OnModelChanged;
        if (_sync is not null) _sync.Changed -= OnSyncChanged;
        _model = null;
        _sync = null;
    }

    private void OnModelChanged()
    {
        Refresh();
        if (_sync is { FollowLive: true } sync && IsSynced && !sync.Window.IsEmpty) FollowWindow(sync.Window);
    }

    private void OnSyncChanged(SyncChanges changes)
    {
        if ((changes & SyncChanges.Selection) != 0)
        {
            int before = _selected;
            RefreshSelection();
            _panel.Rebind();
            if (_selected >= 0 && _selected != before) ScrollIntoView(_selected);
        }

        if ((changes & SyncChanges.Window) != 0 && !_pushing && _sync is { } sync && IsSynced && sync.Window != _pushed) FollowWindow(sync.Window);
    }

    private void Refresh()
    {
        if (_model is { } model) _header.SetEndpoints(model.LeftEndpoint, model.RightEndpoint);
        else _header.SetEndpoints("", "");
        Relayout(keepTopRow: false);
    }

    private void Relayout(bool keepTopRow)
    {
        int top = keepTopRow && Layout.Count > 0 ? Layout.IndexAt(_scroller.Offset.Y) : -1;
        Layout.Update(_model, Mode);
        RefreshSelection();
        _panel.InvalidateMeasure();
        _panel.Rebind();
        if (top >= 0 && top < Layout.Count) ScrollTo(Layout.Top(top));
    }

    private void RefreshSelection()
    {
        if (_model is { } model && _sync is { } sync)
        {
            _selected = model.SelectedIndex(sync.Selection);
            _trace = model.GetTrace(sync.Selection);
        }
        else
        {
            _selected = -1;
            _trace = LadderTrace.None;
        }
    }

    // The window moved elsewhere (timeline zoom or pan, live follow): put its first frame at the top.
    private void FollowWindow(TimeWindow window)
    {
        if (_model is not { Count: > 0 } model) return;
        int index = Math.Min(model.Count - 1, model.IndexAtOrAfter(window.Start));
        ScrollTo(Layout.Top(index));
    }

    private void ScrollTo(double y)
    {
        y = Math.Max(0, y);
        _scrolling = true;
        try
        {
            _scroller.Offset = new Vector(0, y);
        }
        finally
        {
            _scrolling = false;
        }

        // Clamped to an extent not yet measured: the arrange after the rows' measure applies it.
        _pendingOffset = Math.Abs(_scroller.Offset.Y - y) > 0.5 ? y : null;
        if (_pendingOffset is not null) InvalidateArrange();
    }

    // The user scrolled: pan the shared window so it starts at the top row's time, at the same zoom.
    private void OnScrollerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ScrollViewer.OffsetProperty || _scrolling) return;
        _pendingOffset = null;
        if (!IsSynced) return;
        if (_model is not { Count: > 0 } model || _sync is not { Window.IsEmpty: false } sync) return;
        int top = Layout.IndexAt(_scroller.Offset.Y);
        if ((uint)top >= (uint)model.Count) return;
        TimeWindow window = sync.Window;
        _pushing = true;
        try
        {
            sync.SetWindow(window.Pan(model[top].Ticks - window.Start));
        }
        finally
        {
            _pushing = false;
        }

        // The bus raises Changed on a later UI turn; that notification is this push coming back, not a move elsewhere.
        _pushed = sync.Window;
    }

    private sealed class LadderAutomationPeer(LadderView owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

        protected override string GetClassNameCore() => nameof(LadderView);

        protected override string? GetNameCore() => "Ladder";
    }
}
