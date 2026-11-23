using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.Core.Compare;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Controls.Compare;

/// <summary>A hit test's answer.</summary>
/// <param name="Area">What is there.</param>
/// <param name="Row">The row's index in <see cref="CompareRowsModel.Rows"/>; -1 above the rows.</param>
/// <param name="Side">The file of the row (A for the ruler and the Δ strip).</param>
/// <param name="Match">The entity under the point; -1 for none.</param>
/// <param name="Time">The compare time under the point.</param>
public readonly record struct CompareHit(CompareArea Area, int Row, CompareSide Side, int Match, long Time);

/// <summary>
/// The aligned sessions of screens/compare, drawn: the 20 px ruler (Δ from the alignment's anchor), the 18 px
/// "Δ DIFFERENCES" strip with its count badge and a mark per difference, then each file's 24 px header and rows in a
/// 196 px gutter (caret, label, mono count, 3 px Δ flag) and a track: collapsed categories as 16 px heat rows (frame
/// density as the opacity of the file's colour, 220 cells across the window) or 18 px packed rows, expanded categories
/// as one 18 px packed row of 8 px spans per group (timeline.span; spans that differ split.nexnet), the selected entity
/// pinned under its category in accent.trace; over the rows, the dashed diff band around the selection and the
/// playhead. Ctrl+Wheel zooms both files around the pointer, Shift+Wheel pans, Wheel scrolls the rows; a click picks an
/// entity (or moves the playhead), a click on a category's gutter expands it, a double-click opens the place in Inspect.
/// </summary>
/// <remarks>
/// Rows are laid out by the model once per change; a frame draws only the rows in view, finds heat counts by binary
/// search per cell and merges spans per pixel column through reusable buffers, and labels are shaped once
/// (<see cref="CompareTextCache"/>), so redrawing the same view allocates nothing.
/// </remarks>
public sealed class CompareRowsControl : Control
{
    /// <summary>The row gutter (compare.gutter-width).</summary>
    public const double GutterWidth = 196;

    /// <summary>The ruler's height.</summary>
    public const double RulerHeight = 20;

    /// <summary>The Δ strip's height.</summary>
    public const double DeltaHeight = 18;

    /// <summary>Where the rows start.</summary>
    public const double RowsTop = RulerHeight + DeltaHeight;

    /// <summary>Heat cells across the window.</summary>
    public const int HeatCells = 220;

    private const int HeatLevels = 8;
    private const double SpanHeight = 8;
    private const double MinSpanWidth = 3;
    private const double MinMarkWidth = 6;
    private const double MaxMarkWidth = 24;
    private const double MinBandWidth = 8;
    private const double MinTickSpacing = 80;
    private const double HitSlop = 3;
    private const double ZoomStep = 1.25;
    private const double WheelScroll = 36;
    private const double WheelPanFraction = 0.1;

    // Span, differing span, selected span: drawn in that order so the worst state shows on top.
    private static readonly ThemeBrush[] SpanInks = [ThemeBrush.TimelineSpan, ThemeBrush.SplitNexnet, ThemeBrush.AccentTrace];

    public static readonly StyledProperty<CompareRowsModel?> ModelProperty =
        AvaloniaProperty.Register<CompareRowsControl, CompareRowsModel?>(nameof(Model));

    public static readonly StyledProperty<double> ScrollOffsetProperty =
        AvaloniaProperty.Register<CompareRowsControl, double>(nameof(ScrollOffset), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay,
            coerce: static (o, v) => Math.Clamp(double.IsFinite(v) ? v : 0, 0, ((CompareRowsControl)o).ScrollMaximum));

    public static readonly DirectProperty<CompareRowsControl, double> ScrollMaximumProperty =
        AvaloniaProperty.RegisterDirect<CompareRowsControl, double>(nameof(ScrollMaximum), static o => o.ScrollMaximum);

    public static readonly DirectProperty<CompareRowsControl, double> ScrollViewportProperty =
        AvaloniaProperty.RegisterDirect<CompareRowsControl, double>(nameof(ScrollViewport), static o => o.ScrollViewport);

    private readonly CompareTextCache _text = new();
    private readonly Dictionary<long, string> _rulerLabels = [];
    private readonly int[] _cells = new int[HeatCells];
    private readonly int[][] _cover = [[], [], []];
    private readonly IBrush[] _heatA = new IBrush[HeatLevels + 1];
    private readonly IBrush[] _heatB = new IBrush[HeatLevels + 1];
    private readonly Action<CompareChanges> _onChanged;
    private CompareRowsModel? _model;
    private ThemePalette? _palette;
    private ThemeBrush _heatBrushA = (ThemeBrush)255;
    private ThemeBrush _heatBrushB = (ThemeBrush)255;
    private double _scrollMaximum;
    private double _scrollViewport;
    private bool _attached;

    static CompareRowsControl()
    {
        AffectsRender<CompareRowsControl>(ScrollOffsetProperty);
        FocusableProperty.OverrideDefaultValue<CompareRowsControl>(true);
        ClipToBoundsProperty.OverrideDefaultValue<CompareRowsControl>(true);
    }

    public CompareRowsControl()
    {
        _onChanged = OnModelChanged;
    }

    /// <summary>The rows and state drawn.</summary>
    public CompareRowsModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    /// <summary>The rows' vertical scroll (two-way, for a scroll bar beside the control).</summary>
    public double ScrollOffset
    {
        get => GetValue(ScrollOffsetProperty);
        set => SetValue(ScrollOffsetProperty, value);
    }

    /// <summary>The largest <see cref="ScrollOffset"/>: the rows' height past the viewport.</summary>
    public double ScrollMaximum
    {
        get => _scrollMaximum;
        private set => SetAndRaise(ScrollMaximumProperty, ref _scrollMaximum, value);
    }

    /// <summary>The height the rows show in.</summary>
    public double ScrollViewport
    {
        get => _scrollViewport;
        private set => SetAndRaise(ScrollViewportProperty, ref _scrollViewport, value);
    }

    /// <summary>Text layouts shaped and kept (a second identical frame adds none).</summary>
    internal int CachedText => _text.Count;

    /// <summary>Draws each kept label once: what Skia's glyph runs for this view's labels cost, whoever draws them.</summary>
    internal void DrawCachedText(DrawingContext context) => _text.DrawAll(context);

    /// <summary>Labels the last frame drew (each costs Skia a glyph run, whoever draws it).</summary>
    internal int TextDraws { get; private set; }

    private double TrackWidth => Math.Max(1, Bounds.Width - GutterWidth);

    /// <summary>The x of a compare time (unclamped).</summary>
    public double XOf(long time)
    {
        TimeWindow window = _model?.Window ?? default;
        return GutterWidth + window.ToPixel(time, TrackWidth);
    }

    /// <summary>The compare time at an x.</summary>
    public long TimeAt(double x) => (_model?.Window ?? default).FromPixel(x - GutterWidth, TrackWidth);

    /// <summary>The y of a row's top in the control (after the scroll).</summary>
    public double YOf(in CompareRow row) => RowsTop + row.Top - ScrollOffset;

    /// <summary>What lies at a point.</summary>
    public CompareHit HitTest(Point point)
    {
        if (_model is not { Session: { } session } model) return default;
        long time = TimeAt(point.X);
        if (point.Y < RulerHeight) return new CompareHit(CompareArea.Ruler, -1, CompareSide.A, -1, time);
        if (point.Y < RowsTop)
        {
            int delta = point.X >= GutterWidth ? DeltaAt(session, point.X) : -1;
            int match = delta >= 0 ? session.Deltas.Entries[delta].Match : -1;
            return new CompareHit(CompareArea.Deltas, -1, CompareSide.A, match, time);
        }

        int at = model.RowAt(point.Y - RowsTop + ScrollOffset);
        if (at < 0) return default;
        CompareRow row = model.Rows[at];
        if (point.X < GutterWidth)
        {
            int entity = row.Kind == CompareRowKind.Pinned ? model.Selected : -1;
            return new CompareHit(CompareArea.Gutter, at, row.Side, entity, time);
        }

        return new CompareHit(CompareArea.Track, at, row.Side, EntityAt(session, model, row, point.X - GutterWidth), time);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty)
        {
            if (_attached) Subscribe();
        }
        else if (change.Property == BoundsProperty)
        {
            UpdateScroll();
        }
        else if (change.Property == ThemeVariantScope.ActualThemeVariantProperty)
        {
            InvalidateVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Subscribe();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        if (_model is not null) _model.Changed -= _onChanged;
        _model = null;
    }

    private void Subscribe()
    {
        if (_model is not null) _model.Changed -= _onChanged;
        _model = Model;
        if (_model is not null) _model.Changed += _onChanged;
        UpdateScroll();
        InvalidateVisual();
    }

    private void OnModelChanged(CompareChanges changes)
    {
        if ((changes & CompareChanges.Rows) != 0) UpdateScroll();
        InvalidateVisual();
    }

    private void UpdateScroll()
    {
        double viewport = Math.Max(0, Bounds.Height - RowsTop);
        ScrollViewport = viewport;
        ScrollMaximum = Math.Max(0, (_model?.Height ?? 0) - viewport);
        CoerceValue(ScrollOffsetProperty);
    }

    public override void Render(DrawingContext context)
    {
        ThemePalette palette = ThemeBrushes.For(this);
        if (!ReferenceEquals(palette, _palette))
        {
            _palette = palette;
            _text.Clear();
            _heatBrushA = _heatBrushB = (ThemeBrush)255;
        }

        TextDraws = 0;
        Rect bounds = new(Bounds.Size);
        context.FillRectangle(palette[ThemeBrush.SurfacePanel], bounds);
        if (_model is not { Session: { } session } model || bounds.Width <= GutterWidth) return;

        EnsureHeat(palette, model);
        TimeWindow window = model.Window;
        double width = TrackWidth;
        double scroll = ScrollOffset;
        using (context.PushClip(new Rect(0, RowsTop, bounds.Width, Math.Max(0, bounds.Height - RowsTop))))
        {
            ReadOnlySpan<CompareRow> rows = model.Rows;
            int first = Math.Max(0, model.RowAt(scroll));
            for (int i = first; i < rows.Length; i++)
            {
                ref readonly CompareRow row = ref rows[i];
                double y = RowsTop + row.Top - scroll;
                if (y >= bounds.Height) break;
                DrawRow(context, palette, session, model, row, y, bounds.Width, width, window);
            }

            context.FillRectangle(palette[ThemeBrush.BorderDefault], new Rect(GutterWidth - 1, RowsTop, 1, bounds.Height - RowsTop));
            DrawBand(context, palette, session, model, window, width, bounds.Height);
            if (window.Contains(model.Playhead))
            {
                double x = Math.Round(XOf(model.Playhead));
                context.FillRectangle(palette[ThemeBrush.TextPrimary], new Rect(x - 0.75, RowsTop, 1.5, bounds.Height - RowsTop));
            }
        }

        DrawRuler(context, palette, session, window, bounds.Width, width);
        DrawDeltas(context, palette, session, window, bounds.Width, width);
    }

    private void EnsureHeat(ThemePalette palette, CompareRowsModel model)
    {
        if (_heatBrushA != model.BrushA)
        {
            _heatBrushA = model.BrushA;
            FillHeat(_heatA, palette[model.BrushA]);
        }

        if (_heatBrushB != model.BrushB)
        {
            _heatBrushB = model.BrushB;
            FillHeat(_heatB, palette[model.BrushB]);
        }
    }

    // Density as opacity of the file's colour: the faintest level still reads against the track.
    private static void FillHeat(IBrush[] levels, IBrush brush)
    {
        Color color = brush is ISolidColorBrush solid ? solid.Color : default;
        for (int i = 1; i <= HeatLevels; i++) levels[i] = new ImmutableSolidColorBrush(color, 0.12 + (0.88 * i / HeatLevels));
        levels[0] = levels[1];
    }

    private void DrawRow(DrawingContext context, ThemePalette palette, CompareSession session, CompareRowsModel model, in CompareRow row,
        double y, double fullWidth, double width, TimeWindow window)
    {
        double middle = y + (row.Height / 2);
        if (row.Kind == CompareRowKind.FileHeader)
        {
            context.FillRectangle(palette[ThemeBrush.SurfaceChrome], new Rect(0, y, fullWidth, row.Height));
            context.FillRectangle(palette[ThemeBrush.BorderDefault], new Rect(0, y + row.Height - 1, fullWidth, 1));
            ThemeBrush fileBrush = row.Side == CompareSide.A ? model.BrushA : model.BrushB;
            context.DrawRectangle(palette[fileBrush], null, new Rect(10, middle - 5, 9, 9), 2, 2);
            TextLayout name = _text.Get(palette, row.Label, CompareTextStyle.FileName, fullWidth / 2);
            DrawText(context, name, new Point(26, middle - (name.Height / 2)));
            TextLayout detail = _text.Get(palette, row.Side == CompareSide.A ? model.DetailA : model.DetailB, CompareTextStyle.FileDetail,
                Math.Max(0, fullWidth - 40 - name.WidthIncludingTrailingWhitespace));
            DrawText(context, detail, new Point(26 + name.WidthIncludingTrailingWhitespace + 8, middle - (detail.Height / 2)));
            return;
        }

        context.FillRectangle(palette[ThemeBrush.BorderRow], new Rect(0, y + row.Height - 1, fullWidth, 1));
        DrawGutter(context, palette, model, row, y, middle);
        using (context.PushClip(new Rect(GutterWidth, y, width, row.Height)))
        {
            switch (row.Kind)
            {
                case CompareRowKind.Collapsed when row.Category == CompareCategory.Transport || model.RowMode == CompareRowMode.Heat:
                    DrawHeat(context, session, model, row, y, width, window);
                    break;
                case CompareRowKind.Collapsed:
                    (int first, int count) = model.GroupsOf(row.Category);
                    if (count > 0)
                    {
                        int from = session.Groups[first].FirstMatch;
                        EntityGroupRow last = session.Groups[first + count - 1];
                        DrawSpans(context, palette, session, model, row.Side, from, last.FirstMatch + last.Count - from, middle, width, window, false);
                    }

                    break;
                case CompareRowKind.Group:
                    EntityGroupRow group = session.Groups[row.Group];
                    DrawSpans(context, palette, session, model, row.Side, group.FirstMatch, group.Count, middle, width, window, false);
                    break;
                case CompareRowKind.Pinned:
                    DrawSpans(context, palette, session, model, row.Side, model.Selected, 1, middle, width, window, true);
                    break;
            }
        }
    }

    private void DrawGutter(DrawingContext context, ThemePalette palette, CompareRowsModel model, in CompareRow row, double y, double middle)
    {
        double labelX;
        CompareTextStyle style = CompareTextStyle.RowLabel;
        switch (row.Kind)
        {
            case CompareRowKind.Collapsed or CompareRowKind.Expanded:
                if (row.Category != CompareCategory.Transport)
                {
                    TextLayout caret = _text.Get(palette, row.Kind == CompareRowKind.Expanded ? "▾" : "▸", CompareTextStyle.Caret);
                    DrawText(context, caret, new Point(12, middle - (caret.Height / 2)));
                }

                labelX = 26;
                if (row.Kind == CompareRowKind.Expanded) style = CompareTextStyle.RowLabelStrong;
                break;
            case CompareRowKind.Pinned:
                // ◆: a 7 px square on its corner.
                using (context.PushTransform(Matrix.CreateTranslation(-3.5, -3.5) * Matrix.CreateRotation(Math.PI / 4) * Matrix.CreateTranslation(38, middle)))
                    context.FillRectangle(palette[ThemeBrush.TextSecondary], new Rect(0, 0, 7, 7));
                labelX = 47;
                style = CompareTextStyle.PinnedLabel;
                break;
            default:
                labelX = 47;
                break;
        }

        double countRight = GutterWidth - 16;
        double countLeft = countRight;
        if (row.Count.Length > 0)
        {
            TextLayout count = _text.Get(palette, row.Count, CompareTextStyle.Count);
            countLeft = countRight - count.WidthIncludingTrailingWhitespace;
            DrawText(context, count, new Point(countLeft, middle - (count.Height / 2)));
        }

        TextLayout label = _text.Get(palette, row.Label, style, Math.Max(0, countLeft - labelX - 6));
        DrawText(context, label, new Point(labelX, middle - (label.Height / 2)));
        if (row.HasDelta) context.FillRectangle(palette[ThemeBrush.SplitNexnet], new Rect(GutterWidth - 10, y + 3, 3, row.Height - 7));
    }

    private void DrawHeat(DrawingContext context, CompareSession session, CompareRowsModel model, in CompareRow row, double y, double width,
        TimeWindow window)
    {
        CompareDensity density = row.Side == CompareSide.A ? model.DensityA : model.DensityB;
        if (density.Total(row.Category) == 0 || window.IsEmpty) return;
        CompareClock clock = session.Clock;
        int max = 0;
        long previous = clock.ToTicks(row.Side, window.Start);
        for (int i = 0; i < HeatCells; i++)
        {
            long end = clock.ToTicks(row.Side, window.Start + (long)((i + 1) * (double)window.Duration / HeatCells));
            int count = density.Count(row.Category, previous, end);
            _cells[i] = count;
            if (count > max) max = count;
            previous = end;
        }

        if (max == 0) return;
        IBrush[] levels = row.Side == CompareSide.A ? _heatA : _heatB;
        double cell = width / HeatCells;
        double top = y + 2, height = row.Height - 5;
        for (int i = 0; i < HeatCells; i++)
        {
            if (_cells[i] == 0) continue;
            int level = Math.Clamp((int)Math.Ceiling(_cells[i] * (double)HeatLevels / max), 1, HeatLevels);
            // Cells overlap by a hair so adjacent busy cells read as one band, not a comb.
            context.FillRectangle(levels[level], new Rect(GutterWidth + (i * cell), top, cell + 0.5, height));
        }
    }

    // Spans merged per pixel column, worst state on top: a row of thousands of invocations costs its entities plus its width.
    private void DrawSpans(DrawingContext context, ThemePalette palette, CompareSession session, CompareRowsModel model, CompareSide side,
        int first, int count, double middle, double width, TimeWindow window, bool pinned)
    {
        if (count <= 0 || window.IsEmpty) return;
        int columns = (int)Math.Ceiling(width) + 2;
        for (int level = 0; level < _cover.Length; level++)
        {
            if (_cover[level].Length < columns) _cover[level] = new int[columns + 256];
            else Array.Clear(_cover[level], 0, columns);
        }

        ReadOnlySpan<EntityMatch> matches = session.Matches;
        bool any = false;
        for (int i = first; i < first + count; i++)
        {
            ref readonly EntityMatch match = ref matches[i];
            if (!match.Get(side).IsPresent) continue;
            double x0 = window.ToPixel(session.GetStart(match, side), width);
            double x1 = window.ToPixel(session.GetEnd(match, side), width);
            if (x1 - x0 < MinSpanWidth)
            {
                double centre = (x0 + x1) / 2;
                x0 = centre - (MinSpanWidth / 2);
                x1 = centre + (MinSpanWidth / 2);
            }

            if (x1 < 0 || x0 > width) continue;
            int c0 = Math.Clamp((int)Math.Floor(x0), 0, columns - 1);
            int c1 = Math.Clamp((int)Math.Ceiling(x1), c0 + 1, columns - 1);
            int level = pinned || i == model.Selected ? 2 : match.HasDifferences ? 1 : 0;
            _cover[level][c0]++;
            _cover[level][c1]--;
            any = true;
        }

        if (!any) return;
        double top = Math.Round(middle - (SpanHeight / 2));
        for (int level = 0; level < _cover.Length; level++)
        {
            int[] cover = _cover[level];
            IBrush brush = palette[SpanInks[level]];
            int depth = 0, start = -1;
            for (int c = 0; c < columns; c++)
            {
                depth += cover[c];
                if (depth > 0 && start < 0) start = c;
                else if (depth <= 0 && start >= 0)
                {
                    // Square ends: a rounded rectangle costs Skia a native object per call, and spans are many.
                    context.FillRectangle(brush, new Rect(GutterWidth + start, top, c - start, SpanHeight));
                    start = -1;
                }
            }
        }
    }

    private void DrawBand(DrawingContext context, ThemePalette palette, CompareSession session, CompareRowsModel model, TimeWindow window,
        double width, double height)
    {
        if (model.Selected < 0 || window.IsEmpty) return;
        EntityMatch match = session.Matches[model.Selected];
        double x0 = window.ToPixel(session.GetStart(match), width);
        double x1 = window.ToPixel(session.GetEnd(match), width);
        if (x1 - x0 < MinBandWidth)
        {
            double centre = (x0 + x1) / 2;
            x0 = centre - (MinBandWidth / 2);
            x1 = centre + (MinBandWidth / 2);
        }

        if (x1 < 0 || x0 > width) return;
        x0 = Math.Round(GutterWidth + Math.Max(-1, x0));
        x1 = Math.Round(GutterWidth + Math.Min(width + 1, x1));
        context.FillRectangle(palette[ThemeBrush.DiffBand], new Rect(x0, RowsTop, x1 - x0, height - RowsTop));
        IPen pen = palette.Pen(ThemeBrush.SplitNexnet, 1, dashed: true);
        context.DrawLine(pen, new Point(x0 + 0.5, RowsTop), new Point(x0 + 0.5, height));
        context.DrawLine(pen, new Point(x1 - 0.5, RowsTop), new Point(x1 - 0.5, height));
    }

    private void DrawRuler(DrawingContext context, ThemePalette palette, CompareSession session, TimeWindow window, double fullWidth,
        double width)
    {
        context.FillRectangle(palette[ThemeBrush.SurfacePanel], new Rect(0, 0, fullWidth, RulerHeight));
        context.FillRectangle(palette[ThemeBrush.BorderDefault], new Rect(0, RulerHeight - 1, fullWidth, 1));
        CompareClock clock = session.Clock;
        TextLayout caption = _text.Get(palette, RulerCaption(clock), CompareTextStyle.RulerCaption, GutterWidth - 16);
        DrawText(context, caption, new Point(10, (RulerHeight - caption.Height) / 2));
        context.FillRectangle(palette[ThemeBrush.BorderDefault], new Rect(GutterWidth - 1, 0, 1, RulerHeight));
        if (window.IsEmpty) return;

        double startMs = (window.Start - clock.AnchorTime) / (double)TimeSpan.TicksPerMillisecond;
        double spanMs = window.Duration / (double)TimeSpan.TicksPerMillisecond;
        double step = NiceStep(spanMs * MinTickSpacing / width);
        IBrush tick = palette[ThemeBrush.BorderDefault];
        double firstTick = Math.Ceiling(startMs / step) * step;
        using (context.PushClip(new Rect(GutterWidth, 0, width, RulerHeight)))
        {
            for (double v = firstTick; v <= startMs + spanMs; v += step)
            {
                double x = Math.Round(GutterWidth + ((v - startMs) * width / spanMs));
                context.FillRectangle(tick, new Rect(x, 0, 1, RulerHeight - 1));
                TextLayout label = _text.Get(palette, RulerLabel(v, step), CompareTextStyle.Ruler);
                DrawText(context, label, new Point(x + 4, (RulerHeight - label.Height) / 2));
            }
        }
    }

    private void DrawText(DrawingContext context, TextLayout layout, Point origin)
    {
        layout.Draw(context, origin);
        TextDraws++;
    }

    private static string RulerCaption(in CompareClock clock) => clock.Mode switch
    {
        Alignment.OnEvent => clock.EventResolved ? "Δ from session ready" : "Δ from session start",
        Alignment.WallClock => "wall clock · from A's start",
        _ => "from session start",
    };

    /// <summary>The ruler step: 1, 2 or 5 times a power of ten milliseconds, at least <paramref name="raw"/>.</summary>
    internal static double NiceStep(double raw)
    {
        if (!(raw > 0)) return 1;
        double power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double scaled = raw / power;
        return (scaled <= 1 ? 1 : scaled <= 2 ? 2 : scaled <= 5 ? 5 : 10) * power;
    }

    private string RulerLabel(double ms, double step)
    {
        long key = (long)Math.Round(ms * 1000) * 4 + (step >= 1000 ? 1 : 0);
        if (_rulerLabels.TryGetValue(key, out string? label)) return label;
        if (_rulerLabels.Count > 512) _rulerLabels.Clear();
        if (Math.Abs(ms) < step / 1e6) ms = 0;
        int decimals = step >= 1 ? 0 : (int)Math.Ceiling(-Math.Log10(step) - 1e-9);
        label = step >= 1000
            ? (ms / 1000).ToString("#,0.###", CultureInfo.InvariantCulture) + " s"
            : ms.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) + " ms";
        _rulerLabels[key] = label;
        return label;
    }

    private void DrawDeltas(DrawingContext context, ThemePalette palette, CompareSession session, TimeWindow window, double fullWidth,
        double width)
    {
        const double top = RulerHeight;
        context.FillRectangle(palette[ThemeBrush.SurfaceChrome], new Rect(0, top, fullWidth, DeltaHeight));
        context.FillRectangle(palette[ThemeBrush.BorderDefault], new Rect(0, top + DeltaHeight - 1, fullWidth, 1));
        context.FillRectangle(palette[ThemeBrush.BorderDefault], new Rect(GutterWidth - 1, top, 1, DeltaHeight));
        double middle = top + ((DeltaHeight - 1) / 2);
        TextLayout title = _text.Get(palette, "Δ DIFFERENCES", CompareTextStyle.DeltaTitle);
        DrawText(context, title, new Point(10, middle - (title.Height / 2)));

        DeltaList deltas = session.Deltas;
        TextLayout badge = _text.Get(palette, BadgeText(deltas.Count), CompareTextStyle.DeltaBadge);
        double badgeWidth = Math.Max(14, badge.WidthIncludingTrailingWhitespace + 8);
        double badgeX = GutterWidth - 16 - badgeWidth;
        context.DrawRectangle(palette[ThemeBrush.SplitNexnet], null, new Rect(badgeX, middle - 6, badgeWidth, 12), 6, 6);
        DrawText(context, badge, new Point(badgeX + ((badgeWidth - badge.WidthIncludingTrailingWhitespace) / 2), middle - (badge.Height / 2)));
        if (window.IsEmpty) return;

        IBrush mark = palette[ThemeBrush.SplitNexnet];
        // A gap is a plain tertiary mark: the timeline's hatch tile would cost Skia a shader per mark.
        IBrush gap = palette[ThemeBrush.TextTertiary];
        double drawnTo = double.NegativeInfinity;
        using (context.PushClip(new Rect(GutterWidth, top, width, DeltaHeight)))
        {
            foreach (ref readonly DeltaEntry entry in deltas.Entries)
            {
                (double x0, double x1) = MarkSpan(entry, window, width);
                if (x1 < 0 || x0 > width || x1 <= drawnTo) continue;
                context.FillRectangle(entry.Kind == DeltaKind.Gap ? gap : mark, new Rect(GutterWidth + x0, middle - 5, x1 - x0, 10));
                drawnTo = x1;
            }
        }
    }

    private string BadgeText(int count)
    {
        // The badge is one of a handful of strings for a given compare; keep it out of the per-frame path.
        if (_rulerLabels.TryGetValue(-1 - count, out string? text)) return text;
        text = count.ToString(CultureInfo.InvariantCulture);
        _rulerLabels[-1 - count] = text;
        return text;
    }

    private static (double X0, double X1) MarkSpan(in DeltaEntry entry, TimeWindow window, double width)
    {
        double x0 = window.ToPixel(entry.Time, width);
        double x1 = window.ToPixel(entry.EndTime, width);
        if (x1 - x0 < MinMarkWidth)
        {
            double centre = (x0 + x1) / 2;
            return (centre - (MinMarkWidth / 2), centre + (MinMarkWidth / 2));
        }

        // A mark says where a difference starts: one that lasts the whole session (a channel, a collection) must not
        // paint over every other mark.
        return (x0, x0 + Math.Min(x1 - x0, MaxMarkWidth));
    }

    private int DeltaAt(CompareSession session, double x)
    {
        TimeWindow window = session.Range.IsEmpty || _model is null ? default : _model.Window;
        if (window.IsEmpty) return -1;
        double track = x - GutterWidth;
        int best = -1;
        double bestDistance = double.MaxValue;
        ReadOnlySpan<DeltaEntry> entries = session.Deltas.Entries;
        for (int i = 0; i < entries.Length; i++)
        {
            (double x0, double x1) = MarkSpan(entries[i], window, TrackWidth);
            if (track < x0 - HitSlop || track > x1 + HitSlop) continue;
            double distance = Math.Abs(((x0 + x1) / 2) - track);
            if (distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }

        return best;
    }

    private int EntityAt(CompareSession session, CompareRowsModel model, in CompareRow row, double track)
    {
        int first, count;
        switch (row.Kind)
        {
            case CompareRowKind.Pinned:
                return model.Selected;
            case CompareRowKind.Group:
                EntityGroupRow group = session.Groups[row.Group];
                (first, count) = (group.FirstMatch, group.Count);
                break;
            case CompareRowKind.Collapsed when row.Category != CompareCategory.Transport && model.RowMode == CompareRowMode.Packed:
                (int g, int n) = model.GroupsOf(row.Category);
                if (n == 0) return -1;
                first = session.Groups[g].FirstMatch;
                EntityGroupRow last = session.Groups[g + n - 1];
                count = last.FirstMatch + last.Count - first;
                break;
            default:
                return -1;
        }

        TimeWindow window = model.Window;
        double width = TrackWidth;
        int best = -1;
        double bestDistance = double.MaxValue;
        ReadOnlySpan<EntityMatch> matches = session.Matches;
        for (int i = first; i < first + count; i++)
        {
            ref readonly EntityMatch match = ref matches[i];
            if (!match.Get(row.Side).IsPresent) continue;
            double x0 = window.ToPixel(session.GetStart(match, row.Side), width);
            double x1 = window.ToPixel(session.GetEnd(match, row.Side), width);
            if (track < x0 - HitSlop || track > x1 + HitSlop) continue;
            double distance = track >= x0 && track <= x1 ? 0 : Math.Min(Math.Abs(track - x0), Math.Abs(track - x1));
            if (distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }

        return best;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        PointerPoint point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || _model is not { Session: not null } model) return;
        Focus();
        CompareHit hit = HitTest(point.Position);
        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            if (hit.Area is CompareArea.Track or CompareArea.Gutter or CompareArea.Deltas && (hit.Match >= 0 || hit.Area == CompareArea.Track))
                model.RequestOpen(hit.Side, hit.Match, hit.Time);
            return;
        }

        Click(model, hit);
    }

    /// <summary>A single click's action (public for tests): expand, pick an entity, or move the playhead.</summary>
    public void Click(CompareRowsModel model, in CompareHit hit)
    {
        ArgumentNullException.ThrowIfNull(model);
        switch (hit.Area)
        {
            case CompareArea.Gutter when model.Rows[hit.Row].Kind is CompareRowKind.Collapsed or CompareRowKind.Expanded:
                model.ToggleExpanded(model.Rows[hit.Row].Category);
                break;
            case CompareArea.Gutter when model.Rows[hit.Row].Kind == CompareRowKind.Group && model.Session is { } session:
                EntityGroupRow group = session.Groups[model.Rows[hit.Row].Group];
                model.RequestSelect(FirstDiffering(session, group));
                break;
            case CompareArea.Deltas or CompareArea.Track:
                if (hit.Match >= 0) model.RequestSelect(hit.Match);
                model.SetPlayhead(hit.Time);
                break;
            case CompareArea.Ruler:
                model.SetPlayhead(hit.Time);
                break;
        }
    }

    /// <summary>A group's first entity that differs, else its first.</summary>
    public static int FirstDiffering(CompareSession session, in EntityGroupRow group)
    {
        ArgumentNullException.ThrowIfNull(session);
        ReadOnlySpan<EntityMatch> matches = session.Matches;
        for (int i = group.FirstMatch; i < group.FirstMatch + group.Count; i++)
        {
            if (matches[i].HasDifferences) return i;
        }

        return group.FirstMatch;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Wheel(e.GetPosition(this), e.Delta, e.KeyModifiers);
        e.Handled = true;
    }

    /// <summary>A wheel step: Ctrl zooms both files around the pointer, Shift pans time, plain scrolls the rows.</summary>
    public void Wheel(Point point, Vector delta, KeyModifiers modifiers)
    {
        if (_model is not { Session: not null } model) return;
        TimeWindow window = model.Window;
        if ((modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            if (delta.Y == 0) return;
            long anchor = point.X >= GutterWidth ? TimeAt(point.X) : window.Center;
            model.ZoomAround(anchor, Math.Pow(ZoomStep, -delta.Y));
        }
        else if ((modifiers & KeyModifiers.Shift) != 0)
        {
            double steps = -(delta.Y != 0 ? delta.Y : delta.X);
            if (steps != 0) model.Pan(window.PixelsToTicks(steps * TrackWidth * WheelPanFraction, TrackWidth));
        }
        else if (delta.Y != 0)
        {
            ScrollOffset -= delta.Y * WheelScroll;
        }
    }
}
