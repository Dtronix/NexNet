using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Controls.Timeline;

/// <summary>What is under a point of the timeline.</summary>
public enum TimelineArea : byte
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>The overview strip (whole session).</summary>
    Overview = 1,

    /// <summary>The ruler.</summary>
    Ruler = 2,

    /// <summary>A group header.</summary>
    Group = 3,

    /// <summary>A lane's gutter (glyph and labels).</summary>
    Gutter = 4,

    /// <summary>A lane's track (its marks).</summary>
    Track = 5,

    /// <summary>A group's empty hint.</summary>
    Hint = 6,
}

/// <summary>A hit test's answer.</summary>
/// <param name="Area">What is there.</param>
/// <param name="Group">The group of a header, lane or hint.</param>
/// <param name="Lane">The lane, for a gutter or track.</param>
/// <param name="Mark">The mark's index in the lane's marks; -1 for none.</param>
/// <param name="Ticks">The time under the point (track, ruler and overview).</param>
public readonly record struct TimelineHit(TimelineArea Area, LaneGroupKind Group, TimelineLane? Lane, int Mark, long Ticks);

/// <summary>
/// The timeline (PKG controls/timeline-lane): one drawn control with the overview strip, the ruler, the lane groups and
/// their lanes (glyph, label, sub label, pin; marks for frames, ops, invocation spans, pipe lifetimes and splits), and the
/// trace band, gap hatching and playhead over them. It owns the time window, playhead and selection through the
/// <see cref="SyncBus"/>: Ctrl+Wheel zooms around the pointer, Wheel pans time, Shift+Wheel scrolls the lanes, a click
/// selects a mark (moving the playhead), dragging the overview moves the window, and P or a double-click on a gutter
/// pins the lane (<see cref="PinCommand"/>; the lane menu is I26's).
/// </summary>
/// <remarks>
/// Performance: marks are found by binary search on start tick and drawn only in the window, blocks that would land on
/// the pixels already painted are skipped, and labels are shaped once and cached (<see cref="TimelineText"/>), so a frame
/// that redraws the same window allocates nothing (TimelineAllocationTests).
/// </remarks>
public sealed class TimelineControl : Control
{
    /// <summary>The lane gutter (lane.gutter-width).</summary>
    public const double GutterWidth = 156;

    /// <summary>The narrowest block drawn: a frame or op shorter than this still shows.</summary>
    public const double MinBlockWidth = 4;

    private const double MinSpanWidth = 4;
    private const double MinGapWidth = 6;
    private const double LabelMaxWidth = 72;
    private const double OverviewBucket = 4;
    private const double MinTickSpacing = 70;
    private const double WheelPanFraction = 0.1;
    private const double WheelScroll = 36;
    private const double ZoomStep = 1.25;
    private const double HitSlop = 3;

    public static readonly StyledProperty<TimelineModel?> ModelProperty =
        AvaloniaProperty.Register<TimelineControl, TimelineModel?>(nameof(Model));

    public static readonly StyledProperty<SyncBus?> SyncProperty =
        AvaloniaProperty.Register<TimelineControl, SyncBus?>(nameof(Sync));

    public static readonly StyledProperty<bool> ShowAxisProperty =
        AvaloniaProperty.Register<TimelineControl, bool>(nameof(ShowAxis), true);

    public static readonly StyledProperty<ICommand?> PinCommandProperty =
        AvaloniaProperty.Register<TimelineControl, ICommand?>(nameof(PinCommand));

    private static readonly double DimmedOpacity = 0.4;

    private readonly TimelineText _text = new();
    private readonly Dictionary<(long Value, byte Unit), string> _rulerLabels = [];
    private readonly Action<TimeSpan> _onFrame;
    private TimelineModel? _model;
    private SyncBus? _sync;
    private ThemePalette? _palette;
    private TimelineTrace _trace = TimelineTrace.None;
    private int[] _bucketIn = [];
    private int[] _bucketAll = [];
    private string? _rangeLabel;
    private long _rangeTicks = -1;
    private bool _instant = true;
    private bool _frameRequested;
    private double _scroll;
    private Drag _drag;
    private string? _tip;
    private double _dimmed = DimmedOpacity;
    private (IPen Pen, IBrush Ground)? _stripes;
    private bool _attached;

    static TimelineControl()
    {
        AffectsRender<TimelineControl>(ShowAxisProperty);
        AffectsMeasure<TimelineControl>(ShowAxisProperty);
        FocusableProperty.OverrideDefaultValue<TimelineControl>(true);
        ClipToBoundsProperty.OverrideDefaultValue<TimelineControl>(true);
    }

    public TimelineControl()
    {
        _onFrame = _ =>
        {
            _frameRequested = false;
            InvalidateMeasure();
            InvalidateVisual();
        };
    }

    private enum Drag : byte
    {
        None,
        Overview,
        Playhead,
    }

    /// <summary>The lanes and marks drawn.</summary>
    public TimelineModel? Model
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

    /// <summary>False hides the overview strip and ruler (the package's lane-only states).</summary>
    public bool ShowAxis
    {
        get => GetValue(ShowAxisProperty);
        set => SetValue(ShowAxisProperty, value);
    }

    /// <summary>Pins or unpins the lane given as its <see cref="LaneKey"/>; without one the model's pin flips.</summary>
    public ICommand? PinCommand
    {
        get => GetValue(PinCommandProperty);
        set => SetValue(PinCommandProperty, value);
    }

    /// <summary>The lane under the pointer, drawn expanded; settable to show the hover state.</summary>
    public LaneKey? HoveredLane { get; set; }

    /// <summary>The lane the last right-click was on (the lane menu's target, I26).</summary>
    public LaneKey? ContextLane { get; private set; }

    /// <summary>The vertical layout of the last frame.</summary>
    public TimelineLayout Layout { get; } = new();

    /// <summary>What is traced (band, dimming, selected span), from the selection.</summary>
    public TimelineTrace Trace => _trace;

    /// <summary>The lanes' vertical scroll (Shift+Wheel).</summary>
    public double LaneScroll => _scroll;

    /// <summary>The clock of the lane motion; Stopwatch ticks.</summary>
    internal Func<long> Clock { get; set; } = Stopwatch.GetTimestamp;

    /// <summary>The window drawn: the sync bus's, or the whole capture without one.</summary>
    public TimeWindow Window =>
        _sync is { Window.IsEmpty: false } sync ? sync.Window : _model?.Bounds ?? default;

    /// <summary>The top of the lane area.</summary>
    public double LanesTop => ShowAxis ? TimelineLayout.OverviewHeight + TimelineLayout.RulerHeight : 0;

    /// <summary>The tooltip text last set for the pointer's position.</summary>
    public string? Tip => _tip;

    /// <summary>The x of <paramref name="ticks"/> in the window (unclamped).</summary>
    public double XOf(long ticks)
    {
        TimeWindow window = Window;
        double width = TrackWidth;
        return GutterWidth + (window.Duration <= 0 ? 0 : (ticks - window.Start) * width / window.Duration);
    }

    /// <summary>The tick at <paramref name="x"/>.</summary>
    public long TicksAt(double x) => Window.FromPixel(x - GutterWidth, TrackWidth);

    private double TrackWidth => Math.Max(1, Bounds.Width - GutterWidth);

    /// <summary>What lies at <paramref name="point"/>.</summary>
    public TimelineHit HitTest(Point point)
    {
        if (_model is not { } model) return default;
        double axis = LanesTop;
        if (ShowAxis && point.Y < TimelineLayout.OverviewHeight)
            return new TimelineHit(TimelineArea.Overview, default, null, -1, OverviewTicksAt(point.X));
        if (ShowAxis && point.Y < axis)
            return new TimelineHit(TimelineArea.Ruler, default, null, -1, TicksAt(point.X));

        int at = Layout.RowAt(point.Y - axis + _scroll);
        if (at < 0) return default;
        LayoutRow row = Layout.Rows[at];
        switch (row.Kind)
        {
            case LayoutRowKind.Group:
                return new TimelineHit(TimelineArea.Group, row.Group, null, -1, 0);
            case LayoutRowKind.Hint:
                return new TimelineHit(TimelineArea.Hint, row.Group, null, -1, 0);
        }

        if (point.X < GutterWidth)
            return new TimelineHit(TimelineArea.Gutter, row.Group, row.Lane, -1, 0);
        return new TimelineHit(TimelineArea.Track, row.Group, row.Lane, MarkAt(model, row, point.X), TicksAt(point.X));
    }

    /// <summary>Selects what a click on <paramref name="hit"/> selects (a mark: its frame or invocation; elsewhere the playhead).</summary>
    public void Activate(in TimelineHit hit)
    {
        if (_sync is not { } sync || _model is not { } model) return;
        if (hit.Area is TimelineArea.Track or TimelineArea.Ruler && (hit.Lane is null || hit.Mark < 0))
        {
            sync.SetPlayhead(hit.Ticks);
            return;
        }

        if (hit.Lane is not { } lane || hit.Mark < 0) return;
        TimelineMark mark = lane.Marks[hit.Mark];
        if (mark.Kind == MarkKind.Span)
        {
            sync.Select(Selection.OfEntity(mark.Entity, mark.EntityIndex), mark.Start);
        }
        else if (mark.Seq >= 0)
        {
            sync.Select(Selection.OfFrame(new FrameKey(mark.Direction, mark.Seq)), mark.Start);
        }
        else if ((mark.Flags & MarkFlags.Op) != 0 && FramesLane(model, mark.Direction) is { } frames
                 && frames.LowerBound(mark.Start) is var first && first < frames.Count)
        {
            // An op selects the first frame that started in it.
            TimelineMark frame = frames.Marks[first];
            sync.Select(Selection.OfFrame(new FrameKey(frame.Direction, frame.Seq)), frame.Start);
        }
        else
        {
            sync.SetPlayhead(mark.Start);
        }
    }

    /// <summary>Pins or unpins <paramref name="lane"/> through <see cref="PinCommand"/>, or the model without one.</summary>
    public void TogglePin(LaneKey lane)
    {
        if (PinCommand is { } command)
        {
            // ICommand takes object: the lane key is boxed here, once per user action.
            object parameter = lane;
            if (command.CanExecute(parameter)) command.Execute(parameter);
            return;
        }

        _model?.TogglePinned(lane);
    }

    /// <summary>Scrolls the lanes by <paramref name="delta"/> pixels (Shift+Wheel).</summary>
    public void ScrollLanes(double delta)
    {
        double max = Math.Max(0, LanesTop + Layout.Height - Bounds.Height);
        double next = Math.Clamp(_scroll + delta, 0, max);
        if (next == _scroll) return;
        _scroll = next;
        InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TimelineAutomationPeer(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty || change.Property == SyncProperty)
        {
            if (_attached) Subscribe();
        }
        else if (change.Property == ThemeVariantScope.ActualThemeVariantProperty)
        {
            InvalidateVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (this.TryFindResource("Opacity.DimmedUntraced", out object? value) && value is double opacity) _dimmed = opacity;
        _attached = true;
        Subscribe();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Unsubscribe();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        UpdateLayout(Clock());
        return new Size(0, _model is null ? LanesTop : LanesTop + Layout.Height);
    }

    public override void Render(DrawingContext context)
    {
        ThemePalette palette = ThemeBrushes.For(this);
        if (!ReferenceEquals(palette, _palette))
        {
            _palette = palette;
            _text.Clear();
            _stripes = Stripes(palette[ThemeBrush.TimelineGap]);
        }

        TextDraws = 0;
        MarksDrawn = 0;
        Rect bounds = new(Bounds.Size);
        context.FillRectangle(palette[ThemeBrush.SurfacePanel], bounds);
        if (_model is not { } model || bounds.Width <= GutterWidth) return;

        UpdateLayout(Clock());
        TimeWindow window = Window;
        double top = LanesTop;
        if (ShowAxis)
        {
            DrawOverview(context, palette, model, window, bounds.Width);
            DrawRuler(context, palette, model, window, bounds.Width);
        }

        double max = Math.Max(0, top + Layout.Height - bounds.Height);
        if (_scroll > max) _scroll = max;
        using (context.PushClip(new Rect(0, top, bounds.Width, Math.Max(0, bounds.Height - top))))
        {
            foreach (ref readonly LayoutRow row in Layout.Rows)
            {
                double y = top + row.Top - _scroll;
                if (y >= bounds.Height) break;
                if (y + row.Height <= top) continue;
                switch (row.Kind)
                {
                    case LayoutRowKind.Group:
                        DrawGroup(context, palette, model, row, y, bounds.Width);
                        break;
                    case LayoutRowKind.Hint:
                        DrawHint(context, palette, row, y);
                        break;
                    default:
                        DrawLane(context, palette, model, row, y, bounds.Width, window);
                        break;
                }
            }

            double contentBottom = Math.Min(bounds.Height, top + Layout.Height - _scroll);
            using (context.PushClip(new Rect(GutterWidth, top, TrackWidth, Math.Max(0, contentBottom - top))))
                DrawOverlays(context, palette, model, window, top, contentBottom);
        }

        DrawPlayhead(context, palette, window, bounds.Height);
        if (Layout.IsAnimating) RequestFrame();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Point point = e.GetPosition(this);
        PointerPointProperties properties = e.GetCurrentPoint(this).Properties;
        TimelineHit hit = HitTest(point);
        if (properties.IsRightButtonPressed)
        {
            // The lane menu (I26) reads the lane it was opened on.
            ContextLane = hit.Lane?.Key;
            return;
        }

        if (!properties.IsLeftButtonPressed) return;
        Focus();
        switch (hit.Area)
        {
            case TimelineArea.Overview:
                _drag = Drag.Overview;
                MoveWindowTo(hit.Ticks);
                e.Pointer.Capture(this);
                break;
            case TimelineArea.Ruler:
                _drag = Drag.Playhead;
                _sync?.SetPlayhead(hit.Ticks);
                e.Pointer.Capture(this);
                break;
            case TimelineArea.Group:
                _model?.ToggleCollapsed(hit.Group);
                break;
            case TimelineArea.Gutter when e.ClickCount == 2 && hit.Lane is { } lane:
                TogglePin(lane.Key);
                break;
            case TimelineArea.Track:
                Activate(hit);
                if (hit.Mark < 0)
                {
                    _drag = Drag.Playhead;
                    e.Pointer.Capture(this);
                }

                break;
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point point = e.GetPosition(this);
        switch (_drag)
        {
            case Drag.Overview:
                MoveWindowTo(OverviewTicksAt(point.X));
                return;
            case Drag.Playhead:
                _sync?.SetPlayhead(TicksAt(Math.Clamp(point.X, GutterWidth, Bounds.Width)));
                return;
        }

        Hover(HitTest(point));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Drag.None) return;
        _drag = Drag.None;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Hover(default);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Wheel(e.GetPosition(this), e.Delta, e.KeyModifiers);
        e.Handled = true;
    }

    /// <summary>
    /// A wheel step (UX rules: Ctrl+Wheel zooms around the pointer, Wheel pans time, Shift+Wheel scrolls the lanes).
    /// </summary>
    public void Wheel(Point point, Vector delta, KeyModifiers modifiers)
    {
        TimeWindow window = Window;
        if ((modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            if (_sync is null || delta.Y == 0) return;
            long anchor = point.X >= GutterWidth ? TicksAt(point.X) : window.Center;
            _sync.ZoomAround(anchor, Math.Pow(ZoomStep, -delta.Y));
        }
        else if ((modifiers & KeyModifiers.Shift) != 0)
        {
            // Some platforms turn a Shift+Wheel into a horizontal delta.
            double amount = delta.Y != 0 ? delta.Y : delta.X;
            ScrollLanes(-amount * WheelScroll);
        }
        else if (_sync is not null)
        {
            double steps = -delta.Y - delta.X;
            if (steps == 0) return;
            _sync.Pan(window.PixelsToTicks(steps * TrackWidth * WheelPanFraction, TrackWidth));
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.P when HoveredLane is { } lane:
                TogglePin(lane);
                e.Handled = true;
                break;
            case Key.Left or Key.Right:
                e.Handled = Step(e.Key == Key.Right ? 1 : -1);
                break;
        }
    }

    /// <summary>Selects the previous or next frame in the selected frame's lane (keyboard Left/Right).</summary>
    public bool Step(int direction)
    {
        if (_sync is not { Selection.Kind: SelectionKind.Frame or SelectionKind.Message } sync || _model is not { } model) return false;
        if (FramesLane(model, sync.Selection.Frame.Direction) is not { } lane) return false;
        int at = lane.IndexOfSeq(sync.Selection.Frame.Seq) + direction;
        if (at < 0 || at >= lane.Count) return false;
        TimelineMark mark = lane.Marks[at];
        sync.Select(Selection.OfFrame(new FrameKey(mark.Direction, mark.Seq)), mark.Start);
        return true;
    }

    internal static string? Describe(TimelineModel model, in TimelineHit hit)
    {
        if (hit.Lane is not { } lane) return null;
        if (hit.Area == TimelineArea.Gutter)
            return lane.SubLabel is null ? lane.Label : lane.Label + " " + lane.SubLabel;
        if (hit.Area != TimelineArea.Track) return null;
        if (hit.Mark < 0) return null;
        TimelineMark mark = lane.Marks[hit.Mark];
        string ms = Milliseconds(mark.End - mark.Start, model.Frequency);
        string bytes = mark.Bytes.ToString("N0", CultureInfo.InvariantCulture) + " B";
        string direction = mark.Direction == TapDirection.Inbound ? "inbound" : "outbound";
        return mark.Kind switch
        {
            MarkKind.Span => (model.GetLabel(mark.Label) ?? "Invocation") + " · " + ms
                + ((mark.Flags & MarkFlags.Cancelled) != 0 ? " · canceled" : (mark.Flags & MarkFlags.Pending) != 0 ? " · pending" : ""),
            MarkKind.Block when (mark.Flags & MarkFlags.Op) != 0 =>
                (mark.Direction == TapDirection.Inbound ? "read · " : "write · ") + bytes,
            MarkKind.Block => direction + " frame #" + mark.Seq.ToString(CultureInfo.InvariantCulture) + " · " + bytes,
            MarkKind.Open => lane.Label + " opened",
            MarkKind.Close => lane.Label + " closed",
            MarkKind.TransportSplit => "transport split: a frame ended in a later " + (mark.Direction == TapDirection.Inbound ? "read" : "write"),
            MarkKind.NexNetSplit => "NexNet split: the message continues in another frame",
            _ => null,
        };
    }

    /// <summary>Glyph runs drawn by the last <see cref="Render"/> (Skia allocates a little per glyph run, whoever draws it).</summary>
    internal int TextDraws { get; private set; }

    /// <summary>Marks drawn by the last <see cref="Render"/>: what is in view, not what is captured.</summary>
    internal int MarksDrawn { get; private set; }

    private void DrawText(DrawingContext context, TextLayout text, Point origin)
    {
        IReadOnlyList<TextLine> lines = text.TextLines;
        for (int l = 0; l < lines.Count; l++)
        {
            IReadOnlyList<TextRun> runs = lines[l].TextRuns;
            for (int r = 0; r < runs.Count; r++)
            {
                if (runs[r] is ShapedTextRun) TextDraws++;
            }
        }

        text.Draw(context, origin);
    }

    private static string Milliseconds(long ticks, long frequency) =>
        (ticks * 1000.0 / Math.Max(1, frequency)).ToString("0.###", CultureInfo.InvariantCulture) + " ms";

    private static TimelineLane? FramesLane(TimelineModel model, TapDirection direction) =>
        model.FindLane(new LaneKey(direction == TapDirection.Inbound ? LaneKind.InboundFrames : LaneKind.OutboundFrames, 0));

    private void Hover(in TimelineHit hit)
    {
        LaneKey? lane = hit.Area is TimelineArea.Gutter or TimelineArea.Track ? hit.Lane?.Key : null;
        if (lane != HoveredLane)
        {
            HoveredLane = lane;
            InvalidateMeasure();
            InvalidateVisual();
        }

        string? tip = _model is { } model ? Describe(model, hit) ?? GapTip(model, hit) : null;
        if (string.Equals(tip, _tip, StringComparison.Ordinal)) return;
        _tip = tip;
        ToolTip.SetTip(this, tip);
    }

    private string? GapTip(TimelineModel model, in TimelineHit hit)
    {
        if (hit.Area != TimelineArea.Track) return null;
        double slop = Window.PixelsToTicks(MinGapWidth / 2, TrackWidth);
        foreach (ref readonly TimelineGap gap in model.Gaps)
        {
            if (hit.Ticks >= gap.Start - slop && hit.Ticks <= gap.End + slop)
            {
                return "Dropped events · " + gap.DroppedOps.ToString("N0", CultureInfo.InvariantCulture) + " ops · "
                    + gap.DroppedBytes.ToString("N0", CultureInfo.InvariantCulture) + " B "
                    + (gap.Direction == TapDirection.Inbound ? "inbound" : "outbound");
            }
        }

        return null;
    }

    private void MoveWindowTo(long center)
    {
        if (_sync is not { } sync) return;
        TimeWindow window = Window;
        sync.SetWindow(window.Pan(center - window.Center));
    }

    private long OverviewTicksAt(double x)
    {
        TimeWindow bounds = _model?.Bounds ?? default;
        return bounds.FromPixel(x - GutterWidth, TrackWidth);
    }

    private int MarkAt(TimelineModel model, in LayoutRow row, double x)
    {
        if (row.Lane is not { } lane) return -1;
        TimeWindow window = Window;
        double perPixel = window.TicksPerPixel(TrackWidth);
        long at = TicksAt(x);
        long slop = (long)Math.Ceiling(perPixel * Math.Max(HitSlop, MinBlockWidth));
        ReadOnlySpan<TimelineMark> marks = lane.Marks;
        int found = -1;
        for (int i = lane.FirstCandidate(at - slop); i < marks.Length && marks[i].Start <= at + slop; i++)
        {
            ref readonly TimelineMark mark = ref marks[i];
            double x0 = XOf(mark.Start);
            double min = mark.Kind == MarkKind.Span ? MinSpanWidth : MinBlockWidth;
            double x1 = Math.Max(XOf(mark.End), x0 + min);
            if (x < x0 - HitSlop || x > x1 + HitSlop) continue;

            // The last drawn wins, and a block or span over a line mark.
            if (found < 0 || mark.Kind is MarkKind.Block or MarkKind.Span || marks[found].Kind is not (MarkKind.Block or MarkKind.Span))
                found = i;
        }

        return found;
    }

    private void Subscribe()
    {
        Unsubscribe();
        _model = Model;
        _sync = Sync;
        if (_model is not null) _model.Changed += OnModelChanged;
        if (_sync is not null) _sync.Changed += OnSyncChanged;
        _instant = true;
        RefreshTrace();
        InvalidateMeasure();
        InvalidateVisual();
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
        if (_model is { Lanes.Count: 0 })
        {
            // A new document (or a reset) lays its lanes out at once: only later changes animate.
            _instant = true;
            Layout.Animator.Reset();
        }

        RefreshTrace();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnSyncChanged(SyncChanges changes)
    {
        if ((changes & SyncChanges.Selection) != 0) RefreshTrace();
        if ((changes & (SyncChanges.Window | SyncChanges.Bounds | SyncChanges.Selection)) != 0) InvalidateMeasure();
        InvalidateVisual();
    }

    private void RefreshTrace() =>
        _trace = _model is { } model && _sync is { } sync ? model.GetTrace(sync.Selection) : TimelineTrace.None;

    private void UpdateLayout(long now)
    {
        if (_model is not { } model) return;
        Layout.Update(model, Window, HoveredLane, now, _instant);
        if (model.Lanes.Count > 0) _instant = false;
    }

    private void RequestFrame()
    {
        if (_frameRequested || TopLevel.GetTopLevel(this) is not { } top) return;
        _frameRequested = true;
        top.RequestAnimationFrame(_onFrame);
    }

    private void DrawOverview(DrawingContext context, ThemePalette palette, TimelineModel model, TimeWindow window, double width)
    {
        const double h = TimelineLayout.OverviewHeight;
        context.FillRectangle(palette[ThemeBrush.SurfaceChrome], new Rect(0, 0, width, h));
        TimeWindow bounds = model.Bounds;
        if (bounds.IsEmpty) return;

        if (_rangeTicks != bounds.Duration || _rangeLabel is null)
        {
            _rangeTicks = bounds.Duration;
            _rangeLabel = "0 – " + (bounds.Duration / (double)Math.Max(1, model.Frequency)).ToString("0.0", CultureInfo.InvariantCulture) + " s";
        }

        TextLayout range = _text.Get(palette, _rangeLabel, TimelineTextStyle.OverviewRange, GutterWidth - 12);
        DrawText(context, range, new Point(6, (h - range.Height) / 2));

        double trackWidth = width - GutterWidth;
        int buckets = Math.Max(1, (int)(trackWidth / OverviewBucket));
        if (_bucketAll.Length < buckets)
        {
            _bucketAll = new int[buckets];
            _bucketIn = new int[buckets];
        }

        TimelineLane? inbound = FramesLane(model, TapDirection.Inbound);
        TimelineLane? outbound = FramesLane(model, TapDirection.Outbound);
        int max = 1;
        for (int b = 0; b < buckets; b++)
        {
            long t0 = bounds.Start + (long)((double)bounds.Duration * b / buckets);
            long t1 = b == buckets - 1 ? bounds.End + 1 : bounds.Start + (long)((double)bounds.Duration * (b + 1) / buckets);
            int i = inbound?.CountStarting(t0, t1) ?? 0;
            int o = outbound?.CountStarting(t0, t1) ?? 0;
            _bucketIn[b] = i;
            _bucketAll[b] = i + o;
            if (i + o > max) max = i + o;
        }

        for (int b = 0; b < buckets; b++)
        {
            int all = _bucketAll[b];
            if (all == 0) continue;
            double barHeight = 2 + ((h - 6) * Math.Sqrt((double)all / max));
            ThemeBrush ink = _bucketIn[b] * 2 >= all ? ThemeBrush.DirectionInbound : ThemeBrush.DirectionOutbound;
            context.FillRectangle(palette[ink], new Rect(GutterWidth + (b * OverviewBucket), h - 1 - barHeight, OverviewBucket - 1, barHeight));
        }

        double x0 = GutterWidth + bounds.ToPixel(window.Start, trackWidth);
        double x1 = GutterWidth + bounds.ToPixel(window.End, trackWidth);
        if (x1 - x0 < 3) x1 = x0 + 3;
        context.DrawRectangle(null, palette.Pen(ThemeBrush.TextPrimary, 1.5), new Rect(x0, 0.75, x1 - x0, h - 1.5));
    }

    private void DrawRuler(DrawingContext context, ThemePalette palette, TimelineModel model, TimeWindow window, double width)
    {
        const double top = TimelineLayout.OverviewHeight;
        const double h = TimelineLayout.RulerHeight;
        context.FillRectangle(palette[ThemeBrush.SurfacePanel], new Rect(0, top, width, h));
        context.FillRectangle(palette[ThemeBrush.BorderDefault], new Rect(0, top + h - 1, width, 1));
        if (window.IsEmpty) return;

        double frequency = Math.Max(1, model.Frequency);
        double startMs = (window.Start - model.Bounds.Start) * 1000.0 / frequency;
        double spanMs = window.Duration * 1000.0 / frequency;
        double trackWidth = width - GutterWidth;
        double step = NiceStep(spanMs * MinTickSpacing / Math.Max(1, trackWidth));
        byte unit = step >= 1000 ? (byte)2 : startMs + spanMs < 1000 ? (byte)1 : (byte)0;
        IBrush tick = palette[ThemeBrush.BorderStrong];
        double first = Math.Ceiling(startMs / step) * step;
        for (double v = first; v <= startMs + spanMs; v += step)
        {
            double x = GutterWidth + ((v - startMs) * trackWidth / spanMs);
            context.FillRectangle(tick, new Rect(Math.Round(x), top, 1, h - 1));
            TextLayout label = _text.Get(palette, RulerLabel(v, step, unit), TimelineTextStyle.Ruler);
            DrawText(context, label, new Point(Math.Round(x) + 3, top + ((h - label.Height) / 2)));
        }
    }

    /// <summary>The ruler step: 1, 2 or 5 times a power of ten milliseconds, at least <paramref name="raw"/>.</summary>
    internal static double NiceStep(double raw)
    {
        if (!(raw > 0)) return 1;
        double power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double scaled = raw / power;
        return (scaled <= 1 ? 1 : scaled <= 2 ? 2 : scaled <= 5 ? 5 : 10) * power;
    }

    private string RulerLabel(double ms, double step, byte unit)
    {
        long key = (long)Math.Round(ms * 1000);
        if (_rulerLabels.TryGetValue((key, unit), out string? label)) return label;
        if (_rulerLabels.Count > 512) _rulerLabels.Clear();
        int decimals = step >= 1 ? 0 : (int)Math.Ceiling(-Math.Log10(step) - 1e-9);
        label = unit switch
        {
            2 => (ms / 1000).ToString("#,0.###", CultureInfo.InvariantCulture) + " s",
            1 => ms.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) + " ms",
            _ => ms.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
        };
        _rulerLabels[(key, unit)] = label;
        return label;
    }

    private void DrawGroup(DrawingContext context, ThemePalette palette, TimelineModel model, in LayoutRow row, double y, double width)
    {
        context.FillRectangle(palette[ThemeBrush.SurfaceRaised], new Rect(0, y, width, row.Height));
        context.FillRectangle(palette[ThemeBrush.BorderDefault], new Rect(0, y + row.Height - 1, width, 1));
        double middle = y + ((row.Height - 1) / 2);
        TextLayout caret = _text.Get(palette, model.IsCollapsed(row.Group) ? "▸" : "▾", TimelineTextStyle.Caret);
        DrawText(context, caret, new Point(4, middle - (caret.Height / 2)));

        // .gl: 6 px padding, the caret, a 4 px gap: the title starts where the lanes' labels do.
        const double x = 14;

        string? badge = Layout.Badge(row.Group);
        double badgeLeft = GutterWidth - 6;
        if (badge is not null)
        {
            TextLayout text = _text.Get(palette, badge, TimelineTextStyle.Badge);
            double w = Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 8;
            badgeLeft = GutterWidth - 6 - w;
            const double badgeHeight = 11;
            Rect pill = new(badgeLeft, middle - (badgeHeight / 2), w, badgeHeight);
            context.DrawRectangle(palette[ThemeBrush.BorderDefault], null, new RoundedRect(pill, 6));
            DrawText(context, text, new Point(pill.X + 4, pill.Y + ((badgeHeight - text.Height) / 2)));
        }

        TextLayout title = _text.Get(palette, TimelineModel.Title(row.Group), TimelineTextStyle.GroupTitle, Math.Max(0, badgeLeft - x - 4));
        DrawText(context, title, new Point(x, middle - (title.Height / 2)));
    }

    private void DrawHint(DrawingContext context, ThemePalette palette, in LayoutRow row, double y)
    {
        TextLayout hint = _text.Get(palette, HintText(row.Group), TimelineTextStyle.Hint, Math.Max(0, Bounds.Width - 24));
        DrawText(context, hint, new Point(12, y + 12));
    }

    private static readonly string[] Hints = BuildHints();

    /// <summary>The empty-group hint (timeline-lane--empty): built once per group.</summary>
    public static string HintText(LaneGroupKind group) => Hints[(int)group];

    private static string[] BuildHints()
    {
        string[] hints = new string[TimelineModel.GroupCount];
        for (int g = 0; g < hints.Length; g++)
            hints[g] = "No " + TimelineModel.Noun((LaneGroupKind)g) + " in this window · zoom out (Ctrl+Wheel) or scrub";
        return hints;
    }

    private void DrawLane(DrawingContext context, ThemePalette palette, TimelineModel model, in LayoutRow row, double y, double width,
        TimeWindow window)
    {
        TimelineLane lane = row.Lane!;
        double h = row.Height;
        bool expanded = h >= (TimelineLayout.LaneHeight + TimelineLayout.ExpandedHeight) / 2;
        context.FillRectangle(palette[ThemeBrush.SurfaceLane], new Rect(GutterWidth, y, width - GutterWidth, h));
        context.FillRectangle(palette[ThemeBrush.BorderRow], new Rect(0, y + h - 1, width, 1));
        if (row.Expanded && expanded)
            context.FillRectangle(palette[ThemeBrush.DirectionInbound], new Rect(0, y, 2, h - 1));

        using (context.PushClip(new Rect(0, y, GutterWidth, h - 1)))
            DrawGutter(context, palette, row, lane, y, h - 1, expanded);
        using (context.PushClip(new Rect(GutterWidth, y, width - GutterWidth, h - 1)))
            DrawMarks(context, palette, model, lane, y, h - 1, expanded, window, width - GutterWidth);
    }

    private void DrawGutter(DrawingContext context, ThemePalette palette, in LayoutRow row, TimelineLane lane, double y, double h, bool expanded)
    {
        double middle = y + (h / 2);
        double x = 14;
        if (lane.Key.Kind == LaneKind.InvocationRow)
        {
            TextLayout label = _text.Get(palette, TimelineModel.RowLabel(Math.Max(0, row.Ordinal - 1)), TimelineTextStyle.RowLabel);
            DrawText(context, label, new Point(x, middle - (label.Height / 2)));
            return;
        }

        if (Glyph(lane.Glyph) is { } glyph)
        {
            TextLayout g = _text.Get(palette, glyph.Text, glyph.Style);
            DrawText(context, g, new Point(x, middle - (g.Height / 2)));
            x += Math.Ceiling(g.WidthIncludingTrailingWhitespace) + 4;
        }

        double right = GutterWidth - 6;
        if (row.Pinned)
        {
            TextLayout pin = _text.Get(palette, "◆", TimelineTextStyle.Pin);
            right -= pin.WidthIncludingTrailingWhitespace;
            DrawText(context, pin, new Point(right, middle - (pin.Height / 2)));
            right -= 4;
        }

        bool strong = lane.Key.Kind == LaneKind.Pipe && lane.Group == LaneGroupKind.Pipes;
        TimelineTextStyle style = expanded
            ? strong ? TimelineTextStyle.LaneLabelExpandedStrong : TimelineTextStyle.LaneLabelExpanded
            : strong ? TimelineTextStyle.LaneLabelStrong : TimelineTextStyle.LaneLabel;
        TextLayout name = _text.Get(palette, lane.Label, style, Math.Min(LabelMaxWidth, Math.Max(0, right - x)));
        DrawText(context, name, new Point(x, middle - (name.Height / 2)));
        x += Math.Ceiling(name.WidthIncludingTrailingWhitespace) + 4;
        if (lane.SubLabel is { } sub && right - x > 8)
        {
            TextLayout text = _text.Get(palette, sub, expanded ? TimelineTextStyle.SubLabelExpanded : TimelineTextStyle.SubLabel, right - x);
            DrawText(context, text, new Point(x, middle - (text.Height / 2)));
        }
    }

    private static (string Text, TimelineTextStyle Style)? Glyph(LaneGlyph glyph) => glyph switch
    {
        LaneGlyph.Inbound => ("▼", TimelineTextStyle.GlyphInbound),
        LaneGlyph.Outbound => ("▲", TimelineTextStyle.GlyphOutbound),
        LaneGlyph.Pipe => ("⇄", TimelineTextStyle.GlyphNeutral),
        LaneGlyph.Collection => ("≡", TimelineTextStyle.GlyphNeutral),
        LaneGlyph.Session => ("◆", TimelineTextStyle.GlyphSession),
        LaneGlyph.Control => ("·", TimelineTextStyle.GlyphControl),
        _ => null,
    };

    private void DrawMarks(DrawingContext context, ThemePalette palette, TimelineModel model, TimelineLane lane, double y, double h,
        bool expanded, TimeWindow window, double trackWidth)
    {
        if (lane.Visibility == LaneVisibility.Lifetime)
        {
            long end = Math.Min(lane.LifeEnd, model.Bounds.End);
            double l0 = Math.Max(GutterWidth, XOf(lane.LifeStart));
            double l1 = Math.Min(GutterWidth + trackWidth, XOf(end));
            if (l1 > l0) context.FillRectangle(palette[ThemeBrush.BorderDefault], new Rect(l0, y + 5, l1 - l0, 2));
        }

        if (lane.Count == 0) return;
        if (_trace.IsActive)
        {
            using (context.PushOpacity(_dimmed))
                DrawPass(context, palette, model, lane, y, h, expanded, window, trackWidth, Pass.Dimmed);
            DrawPass(context, palette, model, lane, y, h, expanded, window, trackWidth, Pass.Lit);
        }
        else
        {
            DrawPass(context, palette, model, lane, y, h, expanded, window, trackWidth, Pass.All);
        }
    }

    private enum Pass : byte
    {
        All,
        Dimmed,
        Lit,
    }

    private void DrawPass(DrawingContext context, ThemePalette palette, TimelineModel model, TimelineLane lane, double y, double h,
        bool expanded, TimeWindow window, double trackWidth, Pass pass)
    {
        ReadOnlySpan<TimelineMark> marks = lane.Marks;
        double perPixel = Math.Max(window.TicksPerPixel(trackWidth), 1e-9);
        long pad = (long)Math.Ceiling(perPixel * MinBlockWidth);
        double scale = trackWidth / Math.Max(1, window.Duration);
        double right = double.NegativeInfinity;

        // Untraced transport, frame, handshake and control blocks dim; a pipe's data and the invocation spans keep
        // their colour (the traced span turns violet instead), as the package's states draw them.
        bool dimsBlocks = lane.Group is not (LaneGroupKind.Pipes or LaneGroupKind.Collections);
        for (int i = lane.FirstCandidate(window.Start - pad); i < marks.Length; i++)
        {
            ref readonly TimelineMark mark = ref marks[i];
            if (mark.Start > window.End) break;
            if (mark.End < window.Start - pad) continue;
            bool dims = mark.Kind == MarkKind.Block && dimsBlocks;
            if (pass != Pass.All && dims && (pass == Pass.Dimmed) == _trace.Lights(mark)) continue;
            if (pass != Pass.All && !dims && pass == Pass.Dimmed) continue;

            double x0 = GutterWidth + ((mark.Start - window.Start) * scale);
            double x1 = GutterWidth + ((mark.End - window.Start) * scale);
            MarksDrawn++;
            switch (mark.Kind)
            {
                case MarkKind.Block:
                {
                    double w = Math.Max(MinBlockWidth, x1 - x0);

                    // Blocks closer than a pixel to what this lane already painted would only repaint it.
                    if (x0 + w <= right + 1)
                    {
                        MarksDrawn--;
                        continue;
                    }

                    right = x0 + w;
                    ThemeBrush ink = (mark.Flags & MarkFlags.Cancelled) != 0 ? ThemeBrush.StatusError
                        : mark.Direction == TapDirection.Inbound ? ThemeBrush.DirectionInbound : ThemeBrush.DirectionOutbound;
                    Rect block = expanded ? new Rect(x0, y + 4, w, 16) : new Rect(x0, y + 2, w, 8);
                    context.DrawRectangle(palette[ink], null, new RoundedRect(block, expanded ? 2 : 1.5));
                    if (expanded && w >= 12 && model.GetLabel(mark.Label) is { } bytes)
                    {
                        // .big: overflow hidden, no ellipsis: a narrow block shows the start of its count.
                        TextLayout text = _text.Get(palette, bytes, TimelineTextStyle.BlockBytes);
                        using (context.PushClip(block))
                            DrawText(context, text, new Point(x0 + 2, block.Y + ((block.Height - text.Height) / 2)));
                    }

                    break;
                }

                case MarkKind.Span:
                    DrawSpan(context, palette, model, mark, x0, Math.Max(MinSpanWidth, x1 - x0), expanded ? y + ((h - 10) / 2) : y + 1);
                    break;
                case MarkKind.Open:
                case MarkKind.Close:
                    context.FillRectangle(palette[mark.Kind == MarkKind.Open ? ThemeBrush.StatusOk : ThemeBrush.StatusRecording],
                        new Rect(x0, y + 2, 2, Math.Max(0, h - 4)));
                    break;
                case MarkKind.TransportSplit:
                case MarkKind.NexNetSplit:
                    context.FillRectangle(palette[mark.Kind == MarkKind.TransportSplit ? ThemeBrush.SplitTransport : ThemeBrush.SplitNexnet],
                        new Rect(x0, y, 2, h));
                    break;
                case MarkKind.Lifetime:
                    context.FillRectangle(palette[ThemeBrush.BorderDefault], new Rect(x0, y + 5, Math.Max(0, x1 - x0), 2));
                    break;
            }
        }
    }

    private void DrawSpan(DrawingContext context, ThemePalette palette, TimelineModel model, in TimelineMark mark, double x, double w, double top)
    {
        bool selected = _trace.Selects(mark);
        bool cancelled = (mark.Flags & MarkFlags.Cancelled) != 0;
        Rect span = new(x, top, w, 10);
        if (cancelled)
        {
            // The 3 px status.error right edge: the span's fill stops short of it.
            context.DrawRectangle(palette[ThemeBrush.StatusError], null, new RoundedRect(span, 3));
            context.DrawRectangle(palette[selected ? ThemeBrush.AccentTrace : ThemeBrush.TimelineSpan], null,
                new RoundedRect(new Rect(x, top, Math.Max(0, w - 3), 10), 3, 0, 0, 3));
        }
        else
        {
            context.DrawRectangle(palette[selected ? ThemeBrush.AccentTrace : ThemeBrush.TimelineSpan], null, new RoundedRect(span, 3));
        }

        double room = w - 4 - (cancelled ? 4 : 1);
        if (room < 8 || model.GetLabel(mark.Label) is not { } label) return;
        TextLayout text = _text.Get(palette, label, selected ? TimelineTextStyle.SpanLabelSelected : TimelineTextStyle.SpanLabel, room);
        DrawText(context, text, new Point(x + 4, top + ((10 - text.Height) / 2)));
    }

    private void DrawOverlays(DrawingContext context, ThemePalette palette, TimelineModel model, TimeWindow window, double top, double bottom)
    {
        if (bottom <= top) return;
        double height = bottom - top;
        foreach (ref readonly TimelineGap gap in model.Gaps)
        {
            if (gap.End < window.Start || gap.Start > window.End) continue;
            double x0 = XOf(gap.Start);
            double w = Math.Max(MinGapWidth, XOf(gap.End) - x0);
            DrawGap(context, palette, new Rect(x0, top, w, height));
        }

        if (!_trace.IsActive || _trace.End < window.Start || _trace.Start > window.End) return;
        double b0 = XOf(_trace.Start);
        double b1 = Math.Max(b0 + 2, XOf(_trace.End));
        context.FillRectangle(palette[ThemeBrush.AccentTraceBand], new Rect(b0, top, b1 - b0, height));
        IBrush edge = palette[ThemeBrush.AccentTrace];
        DashedEdge(context, edge, Math.Round(b0), top, bottom);
        DashedEdge(context, edge, Math.Round(b1) - 1, top, bottom);
    }

    /// <summary>
    /// The gap hatch (color.timeline.gap, repeating-linear-gradient(135deg, a 0 4px, b 4px 8px)): drawn as 4 px diagonal
    /// strokes over the ground colour, read once per theme from the token's tile, because Skia re-renders a tiled drawing
    /// brush (and allocates) on every fill. The tile brush itself is the fallback for a token of another shape.
    /// </summary>
    private void DrawGap(DrawingContext context, ThemePalette palette, Rect area)
    {
        if (_stripes is not { } stripes)
        {
            context.FillRectangle(palette[ThemeBrush.TimelineGap], area);
            return;
        }

        const double period = 8 * 1.4142135623730951;
        context.FillRectangle(stripes.Ground, area);
        IPen pen = stripes.Pen;
        using (context.PushClip(area))
        {
            // Strokes rising left to right, one period apart along x; each spans the area's height.
            double start = Math.Floor((area.X - area.Height) / period) * period;
            for (double x = start; x < area.Right; x += period)
                context.DrawLine(pen, new Point(x, area.Bottom), new Point(x + area.Height, area.Y));
        }
    }

    /// <summary>The gap token's tile as a 4 px stripe pen and a ground; null when it is not the two-band tile.</summary>
    private static (IPen Pen, IBrush Ground)? Stripes(IBrush gap)
    {
        if (gap is not DrawingBrush { Drawing: DrawingGroup group } || group.Children.Count != 2) return null;
        if (group.Children[0] is not GeometryDrawing { Brush: ISolidColorBrush stripe }
            || group.Children[1] is not GeometryDrawing { Brush: ISolidColorBrush ground })
        {
            return null;
        }

        return (new ImmutablePen(stripe.ToImmutable(), 4), ground.ToImmutable());
    }

    /// <summary>
    /// The band's 1 px dashed edge (border: 1px dashed), as 2 px dashes 2 px apart: filled rectangles cost the renderer
    /// nothing per frame, where a dashed pen builds its path effect on every line.
    /// </summary>
    private static void DashedEdge(DrawingContext context, IBrush brush, double x, double top, double bottom)
    {
        for (double y = top; y < bottom; y += 4)
            context.FillRectangle(brush, new Rect(x, y, 1, Math.Min(2, bottom - y)));
    }

    private void DrawPlayhead(DrawingContext context, ThemePalette palette, TimeWindow window, double height)
    {
        if (_sync is not { } sync || window.IsEmpty || sync.Playhead < window.Start || sync.Playhead > window.End) return;
        double x = XOf(sync.Playhead);
        double top = ShowAxis ? TimelineLayout.OverviewHeight : 0;
        double bottom = Math.Min(height, LanesTop + Layout.Height - _scroll);
        if (bottom > top) context.FillRectangle(palette[ThemeBrush.TextPrimary], new Rect(Math.Round(x) - 1, top, 2, bottom - top));
    }

    /// <summary>Lanes as a list for assistive technology: the timeline names its lanes in view.</summary>
    private sealed class TimelineAutomationPeer(TimelineControl owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

        protected override string GetClassNameCore() => nameof(TimelineControl);

        protected override string? GetNameCore()
        {
            TimelineControl timeline = (TimelineControl)Owner;
            ReadOnlySpan<LayoutRow> rows = timeline.Layout.Rows;
            List<string> names = [];
            foreach (ref readonly LayoutRow row in rows)
            {
                if (row.Lane is { } lane && row.Ordinal > 0)
                    names.Add(lane.Key.Kind == LaneKind.InvocationRow ? TimelineModel.RowLabel(row.Ordinal - 1) : lane.Label);
            }

            return names.Count == 0 ? "Timeline" : "Timeline: " + string.Join(", ", names);
        }
    }
}
