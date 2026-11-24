using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Controls.Ladder;

/// <summary>How a realized row is drawn right now: selection, hover and its part in the trace bracket.</summary>
/// <param name="Selected">The selected frame's row (state.selected fill).</param>
/// <param name="Hovered">Under the pointer (surface.raised fill).</param>
/// <param name="Bracket">Member (solid bar), interleaved (dotted bar, row at 60 %) or none.</param>
/// <param name="BracketStart">The first traced row: the bar starts mid-row.</param>
/// <param name="BracketEnd">The last traced row: the bar ends mid-row.</param>
public readonly record struct LadderRowState(bool Selected, bool Hovered, LadderBracket Bracket, bool BracketStart, bool BracketEnd);

/// <summary>
/// One ladder row (controls/ladder-row), drawn: a frame as an arrow between the two endpoints' lifelines, labelled near
/// its sender with its type, target and byte count and its flag chips, beside its time and the transport ops that
/// carried it, with its part of the trace bracket on the left. In time-scaled mode the row is taller by the gap above
/// it; the lifelines and bracket run through the gap.
/// </summary>
/// <remarks>
/// <see cref="LadderView"/>'s panel realizes only the rows in view and recycles them. A row shapes its text when it is
/// bound to another frame (or the theme changes) and keeps it, so redrawing what is already in view allocates nothing.
/// </remarks>
public sealed class LadderRowControl : Control
{
    /// <summary>The trace bracket's column (grid: 12 px · 66 px · ladder · 92 px, 10 px right padding).</summary>
    public const double BracketWidth = 12;

    /// <summary>The Time column.</summary>
    public const double TimeWidth = 66;

    /// <summary>The Transport column (op chips).</summary>
    public const double OpsWidth = 92;

    /// <summary>Space after the Transport column.</summary>
    public const double RightPadding = 10;

    /// <summary>The lifelines sit this fraction of the ladder's width in from each side.</summary>
    public const double LifelineInset = 0.03;

    /// <summary>The label starts this fraction of the ladder's width in from the sender's side.</summary>
    public const double LabelInset = 0.05;

    private const double TextSize = 10.5;
    private const double ArrowTop = 8;
    private const double ArrowThickness = 2;
    private const double HeadLength = 6;
    private const double HeadHeight = 8;
    private const double LabelPadding = 5;
    private const double LabelGap = 5;
    private const double GlyphGap = 3;
    private const double ChipTop = 3;
    private const double OpTop = 1.5;
    private const double OpGap = 2;
    private const double BracketLeft = 4;
    private const double BarWidth = 3;
    private const double DotSize = 2;

    // Arrowheads pointing right (inbound, at the tapped endpoint) and left (outbound, at the remote peer), tip at (0, 0).
    private static readonly Geometry RightHead = Head(right: true);
    private static readonly Geometry LeftHead = Head(right: false);

    private ThemePalette? _palette;
    private LadderRow _shapedRow;
    private int _shapedTotal = -1;
    private string? _shapedName;
    private long _shapedOrigin;
    private long _shapedFrequency;
    private TextLayout? _time;
    private TextLayout? _title;
    private TextLayout? _detail;
    private TextLayout? _bytes;
    private TextLayout? _firstOp;
    private TextLayout? _lastOp;
    private int _chunkTotal;
    private string? _name;
    private long _origin;
    private long _frequency = 1;

    /// <summary>The row index this control shows; -1 while pooled.</summary>
    public int Index { get; private set; } = -1;

    /// <summary>The row shown.</summary>
    public LadderRow Row { get; private set; }

    /// <summary>How the row is drawn.</summary>
    public LadderRowState State { get; private set; }

    /// <summary>The time-scaled gap above the row's 18 px (0 in compact mode).</summary>
    public double Gap { get; private set; }

    /// <summary>Times the row's text was shaped (a rebind to the same frame shapes nothing).</summary>
    internal int Shapes { get; private set; }

    /// <summary>Shows <paramref name="row"/>; redraws only when something it draws changed.</summary>
    internal void Bind(int index, in LadderRow row, double gap, in LadderRowState state, int chunkTotal, string? name,
        long origin, long frequency, double interleavedOpacity)
    {
        bool content = index != Index || row != Row || chunkTotal != _chunkTotal || !ReferenceEquals(name, _name)
            || origin != _origin || frequency != _frequency;
        bool look = content || gap != Gap || state != State;
        Index = index;
        Row = row;
        Gap = gap;
        State = state;
        _chunkTotal = chunkTotal;
        _name = name;
        _origin = origin;
        _frequency = Math.Max(1, frequency);
        Opacity = state.Bracket == LadderBracket.Interleaved ? interleavedOpacity : 1;
        if (content) AutomationProperties.SetName(this, LadderText.Describe(row, chunkTotal, name, origin, _frequency));
        if (look) InvalidateVisual();
    }

    /// <summary>Returns the row to the pool.</summary>
    internal void Unbind()
    {
        Index = -1;
        IsVisible = false;
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, LadderLayout.RowHeight + Gap);

    public override void Render(DrawingContext context)
    {
        if (Index < 0) return;
        ThemePalette palette = ThemeBrushes.For(this);
        Shape(palette);

        double width = Bounds.Width;
        double height = Bounds.Height;
        double top = Gap;
        double rowHeight = LadderLayout.RowHeight;
        LadderRowState state = State;
        IBrush fill = palette[state.Selected ? ThemeBrush.StateSelected : state.Hovered ? ThemeBrush.SurfaceRaised : ThemeBrush.SurfacePanel];

        if (top > 0) context.FillRectangle(palette[ThemeBrush.SurfacePanel], new Rect(0, 0, width, top));
        context.FillRectangle(fill, new Rect(0, top, width, rowHeight));
        context.FillRectangle(palette[ThemeBrush.BorderRow], new Rect(0, height - 1, width, 1));

        double ladderLeft = BracketWidth + TimeWidth;
        double ladderRight = Math.Max(ladderLeft, width - RightPadding - OpsWidth);
        double span = ladderRight - ladderLeft;
        double left = Math.Round(ladderLeft + (span * LifelineInset));
        double right = Math.Max(left, Math.Round(ladderRight - (span * LifelineInset)) - 1);
        IBrush lifeline = palette[ThemeBrush.BorderDefault];
        context.FillRectangle(lifeline, new Rect(left, 0, 1, height));
        context.FillRectangle(lifeline, new Rect(right, 0, 1, height));

        bool inbound = Row.Direction == TapDirection.Inbound;
        IBrush ink = palette[inbound ? ThemeBrush.DirectionInbound : ThemeBrush.DirectionOutbound];
        double labelWidth = LabelWidth(palette);
        double labelX = Math.Round(Math.Max(left + 1,
            inbound ? ladderLeft + (span * LabelInset) : ladderRight - (span * LabelInset) - labelWidth));

        // The arrow stops at the label and resumes after it rather than running beneath it: the line breaks under the
        // text even when the row is drawn translucent (an interleaved row), where an opaque label fill would not hide it.
        double arrowY = top + ArrowTop;
        double shaftFrom = inbound ? left : left + HeadLength;
        double shaftTo = inbound ? right + 1 - HeadLength : right + 1;
        Shaft(context, ink, shaftFrom, Math.Min(shaftTo, labelX), arrowY);
        Shaft(context, ink, Math.Max(shaftFrom, labelX + labelWidth), shaftTo, arrowY);
        using (context.PushTransform(Matrix.CreateTranslation(inbound ? right + 1 : left, arrowY + (ArrowThickness / 2))))
            context.DrawGeometry(ink, null, inbound ? RightHead : LeftHead);

        DrawLabel(context, palette, fill, labelX, labelWidth, left, right, top);

        TextLayout time = _time!;
        time.Draw(context, new Point(BracketWidth, top + ((rowHeight - time.Height) / 2)));

        double x = ladderRight;
        bool split = (Row.Flags & FrameFlags.TransportSplit) != 0;
        x += TransportOpChip.Draw(context, palette, _firstOp!, Row.Direction, split, new Point(x, top + OpTop)) + OpGap;
        if (_lastOp is { } last && x + TransportOpChip.MeasureWidth(last) <= width - RightPadding)
            TransportOpChip.Draw(context, palette, last, Row.Direction, split, new Point(x, top + OpTop));

        DrawBracket(context, palette, state, top, height);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty) InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new RowAutomationPeer(this);

    private static void Shaft(DrawingContext context, IBrush ink, double from, double to, double y)
    {
        if (to > from) context.FillRectangle(ink, new Rect(from, y, to - from, ArrowThickness));
    }

    private double LabelWidth(ThemePalette palette)
    {
        double width = LabelPadding + _title!.Width;
        PipeContent? pipe = LadderText.PipeKind(Row);
        if (pipe is { } chip && chip != PipeContent.Raw) width += LabelGap + PipeChip.MeasureWidth(palette, chip);
        if (_detail is not null) width += LabelGap + (pipe is null ? 0 : PipeGlyph.Size + GlyphGap) + _detail.Width;
        if (_bytes is not null) width += LabelGap + _bytes.Width;
        foreach (FrameFlags flag in FlagChip.DisplayOrder)
        {
            if ((Row.Flags & flag) != 0) width += LabelGap + FlagChip.MeasureWidth(palette, flag);
        }

        return Math.Ceiling(width + LabelPadding);
    }

    private void DrawLabel(DrawingContext context, ThemePalette palette, IBrush fill, double x, double width, double left, double right, double top)
    {
        TextLayout title = _title!;
        double rowHeight = LadderLayout.RowHeight;

        // The label sits on the arrow over the row's own opaque fill (ladder-row.md), clipped to the ladder.
        using (context.PushClip(new Rect(left + 1, top, Math.Max(0, right - left - 1), rowHeight)))
        {
            context.FillRectangle(fill, new Rect(x, top, width, rowHeight - 1));
            x += LabelPadding;
            title.Draw(context, new Point(x, top + ((rowHeight - title.Height) / 2)));
            x += title.Width;
            PipeContent? pipe = LadderText.PipeKind(Row);
            if (pipe is { } chip && chip != PipeContent.Raw)
            {
                // The wire type stays the title; the chip after it says a channel or collection rides on the pipe.
                x += LabelGap;
                x += PipeChip.Draw(context, palette, chip, new Point(x, top + ChipTop));
            }

            if (_detail is { } detail)
            {
                x += LabelGap;
                if (pipe is { } kind)
                {
                    PipeGlyph.Draw(context, palette, kind, ThemeBrush.TextSecondary, new Point(x, Math.Round(top + ((rowHeight - PipeGlyph.Size) / 2))));
                    x += PipeGlyph.Size + GlyphGap;
                }

                detail.Draw(context, new Point(x, top + ((rowHeight - detail.Height) / 2)));
                x += detail.Width;
            }

            if (_bytes is { } bytes)
            {
                x += LabelGap;
                bytes.Draw(context, new Point(x, top + ((rowHeight - bytes.Height) / 2)));
                x += bytes.Width;
            }

            foreach (FrameFlags flag in FlagChip.DisplayOrder)
            {
                if ((Row.Flags & flag) == 0) continue;
                x += LabelGap;
                x += FlagChip.Draw(context, palette, flag, new Point(x, top + ChipTop));
            }
        }
    }

    private static void DrawBracket(DrawingContext context, ThemePalette palette, in LadderRowState state, double top, double height)
    {
        IBrush trace = palette[ThemeBrush.AccentTrace];
        double middle = top + (LadderLayout.RowHeight / 2);
        switch (state.Bracket)
        {
            case LadderBracket.Member:
            {
                double from = state.BracketStart ? middle : 0;
                double to = state.BracketEnd ? middle : height;
                if (state.BracketStart && state.BracketEnd)
                {
                    // A trace of one row: a short bar rather than nothing.
                    from = top + 3;
                    to = top + LadderLayout.RowHeight - 3;
                }

                context.DrawRectangle(trace, null, new RoundedRect(new Rect(BracketLeft, from, BarWidth, Math.Max(0, to - from)), 2));
                break;
            }

            case LadderBracket.Interleaved:
                // A 2 px dotted line: 2 px dots every 4 px, as plain rectangles so a frame allocates nothing.
                for (double y = 0; y < height; y += 2 * DotSize)
                    context.FillRectangle(trace, new Rect(BracketLeft, y, DotSize, Math.Min(DotSize, height - y)));
                break;
        }
    }

    private void Shape(ThemePalette palette)
    {
        if (ReferenceEquals(palette, _palette) && _shapedRow == Row && _shapedTotal == _chunkTotal && ReferenceEquals(_shapedName, _name)
            && _shapedOrigin == _origin && _shapedFrequency == _frequency && _time is not null)
            return;

        Dispose(ref _time);
        Dispose(ref _title);
        Dispose(ref _detail);
        Dispose(ref _bytes);
        Dispose(ref _firstOp);
        Dispose(ref _lastOp);

        LadderRow row = Row;
        Typeface ui = new(palette.Ui);
        Typeface mono = new(palette.Mono);
        _time = new TextLayout(LadderText.Time(row.Ticks, _origin, _frequency), mono, TextSize, palette[ThemeBrush.TextSecondary]);
        _title = new TextLayout(LadderText.Title(row, _chunkTotal), new Typeface(palette.Ui, FontStyle.Normal, FontWeight.SemiBold), TextSize,
            palette[ThemeBrush.TextPrimary]);
        string detail = LadderText.Detail(row, _name);
        _detail = detail.Length == 0 ? null : new TextLayout(detail, ui, TextSize, palette[ThemeBrush.TextSecondary]);
        _bytes = LadderText.Bytes(row) is { } bytes ? new TextLayout(bytes, mono, TextSize, palette[ThemeBrush.TextSecondary]) : null;
        bool split = (row.Flags & FrameFlags.TransportSplit) != 0;
        _firstOp = TransportOpChip.CreateLabel(palette, row.FirstOpId, row.Direction, split);
        _lastOp = row.LastOpId != row.FirstOpId ? TransportOpChip.CreateLabel(palette, row.LastOpId, row.Direction, split) : null;

        _palette = palette;
        _shapedRow = row;
        _shapedTotal = _chunkTotal;
        _shapedName = _name;
        _shapedOrigin = _origin;
        _shapedFrequency = _frequency;
        Shapes++;
    }

    private static void Dispose(ref TextLayout? layout)
    {
        layout?.Dispose();
        layout = null;
    }

    private static StreamGeometry Head(bool right)
    {
        StreamGeometry geometry = new();
        using (StreamGeometryContext g = geometry.Open())
        {
            double back = right ? -HeadLength : HeadLength;
            g.BeginFigure(default, true);
            g.LineTo(new Point(back, -HeadHeight / 2));
            g.LineTo(new Point(back, HeadHeight / 2));
            g.EndFigure(true);
        }

        return geometry;
    }

    private sealed class RowAutomationPeer(LadderRowControl owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

        protected override string GetClassNameCore() => nameof(LadderRowControl);
    }
}
