using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace NexNet.Inspector.App.Controls;

/// <summary>
/// A pane boundary the user drags (the Inspect and Compare workspaces). It lies over the 1 px border.default line the
/// panes already draw, with a 7 px hit area centred on it, and draws that line in border.strong while hovered or
/// dragged (its theme, Themes/PaneSplitter.axaml, sets the brush). It reports the drag as the pointer's total travel since
/// the press, so the owner sizes from where the drag began and clamps freely; a double-click asks for the default size.
/// </summary>
[PseudoClasses(":dragging")]
public sealed class PaneSplitter : Control
{
    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<PaneSplitter, Orientation>(nameof(Orientation), Orientation.Vertical);

    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<PaneSplitter, IBrush?>(nameof(LineBrush));

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty.AddOwner<PaneSplitter>();

    public static readonly StyledProperty<bool> ShowsLineProperty =
        AvaloniaProperty.Register<PaneSplitter, bool>(nameof(ShowsLine));

    private Point _start;
    private bool _dragging;

    static PaneSplitter()
    {
        AffectsRender<PaneSplitter>(BackgroundProperty, LineBrushProperty, ShowsLineProperty, OrientationProperty);
        FocusableProperty.OverrideDefaultValue<PaneSplitter>(false);
    }

    public PaneSplitter() => UpdateCursor();

    /// <summary>Vertical: a line between columns, dragged left and right. Horizontal: between rows.</summary>
    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    /// <summary>The hit area's fill: transparent (its theme sets it), so the whole 7 px takes the pointer.</summary>
    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>The line's brush while it shows (border.strong).</summary>
    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    /// <summary>True while hovered or dragged (the theme sets it): the line is drawn over the panes' border.</summary>
    public bool ShowsLine
    {
        get => GetValue(ShowsLineProperty);
        set => SetValue(ShowsLineProperty, value);
    }

    /// <summary>True while a drag is in progress.</summary>
    public bool IsDragging => _dragging;

    /// <summary>A drag began.</summary>
    public event Action<PaneSplitter>? DragStarted;

    /// <summary>The pointer moved during a drag: its total travel along the drag axis since the press, in px.</summary>
    public event Action<PaneSplitter, double>? DragDelta;

    /// <summary>The drag ended (released or capture lost).</summary>
    public event Action<PaneSplitter>? DragCompleted;

    /// <summary>A double-click: the owner gives the pane its default size.</summary>
    public event Action<PaneSplitter>? ResetRequested;

    /// <summary>Starts a drag at a point in the parent's coordinates (tests drive the splitter through these).</summary>
    public void BeginDrag(Point point)
    {
        _start = point;
        _dragging = true;
        PseudoClasses.Set(":dragging", true);
        DragStarted?.Invoke(this);
    }

    /// <summary>Moves the drag to a point in the parent's coordinates.</summary>
    public void MoveDrag(Point point)
    {
        if (!_dragging) return;
        double delta = Orientation == Orientation.Vertical ? point.X - _start.X : point.Y - _start.Y;
        DragDelta?.Invoke(this, delta);
    }

    /// <summary>Ends the drag.</summary>
    public void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        PseudoClasses.Set(":dragging", false);
        DragCompleted?.Invoke(this);
    }

    /// <summary>Asks for the default size, as a double-click does.</summary>
    public void RequestReset() => ResetRequested?.Invoke(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OrientationProperty) UpdateCursor();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Parent is not Visual parent) return;
        if (e.ClickCount >= 2)
        {
            e.Handled = true;
            RequestReset();
            return;
        }

        e.Pointer.Capture(this);
        BeginDrag(e.GetPosition(parent));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging && Parent is Visual parent) MoveDrag(e.GetPosition(parent));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        e.Pointer.Capture(null);
        EndDrag();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    public override void Render(DrawingContext context)
    {
        // The whole hit area takes the pointer; only the 1 px line in its middle is ever visible.
        if (Background is { } background) context.FillRectangle(background, new Rect(Bounds.Size));
        if (!ShowsLine || LineBrush is not { } brush) return;
        Rect line = Orientation == Orientation.Vertical
            ? new Rect(Math.Floor(Bounds.Width / 2), 0, 1, Bounds.Height)
            : new Rect(0, Math.Floor(Bounds.Height / 2), Bounds.Width, 1);
        context.FillRectangle(brush, line);
    }

    private void UpdateCursor() =>
        Cursor = new Cursor(Orientation == Orientation.Vertical ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth);
}
