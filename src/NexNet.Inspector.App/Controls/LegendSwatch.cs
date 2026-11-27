using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using NexNet.Inspector.App.Controls.Compare;
using NexNet.Inspector.App.Controls.Hex;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.App.Controls.PropertyTree;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Controls;

/// <summary>
/// One legend symbol (<see cref="LegendSymbol"/>), drawn as the views draw it: the flag, op and count chips through their
/// own static draw paths, the hex fills through the hex renderer's role map, the assembly bar and the compare swatches as
/// the real controls, and the rest with the same tokens and shapes as the timeline, ladder and compare rows. A legend
/// is a still picture: it shapes its text when it draws, which a dialog can afford.
/// </summary>
public sealed class LegendSwatch : Decorator
{
    /// <summary>The swatch's size: every symbol fits it, so the legend's text lines up.</summary>
    public const double SwatchWidth = 84, SwatchHeight = 20;

    public static readonly StyledProperty<LegendSymbol> SymbolProperty =
        AvaloniaProperty.Register<LegendSwatch, LegendSymbol>(nameof(Symbol));

    static LegendSwatch()
    {
        AffectsRender<LegendSwatch>(SymbolProperty);
        SymbolProperty.Changed.AddClassHandler<LegendSwatch>(static (swatch, _) => swatch.Child = swatch.CreateChild());
    }

    public LegendSwatch()
    {
        Width = SwatchWidth;
        Height = SwatchHeight;
        ClipToBounds = true;
        Child = CreateChild();
    }

    /// <summary>The symbol drawn.</summary>
    public LegendSymbol Symbol
    {
        get => GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty) InvalidateVisual();
    }

    // The symbols another control draws: the swatch hosts that control rather than copy its drawing.
    private Control? CreateChild() => Symbol switch
    {
        LegendSymbol.AssemblyStart => Bar(new AssemblySegment(AssemblySegmentKind.Start, 0, 0, false, 0),
            new AssemblySegment(AssemblySegmentKind.Chunk, 1, 40, false, 0)),
        LegendSymbol.AssemblyChunks => Bar(new AssemblySegment(AssemblySegmentKind.Chunk, 1, 30, false, 0),
            new AssemblySegment(AssemblySegmentKind.Chunk, 2, 30, true, 0), new AssemblySegment(AssemblySegmentKind.Chunk, 3, 12, false, 0)),
        LegendSymbol.AssemblyAbort => Bar(new AssemblySegment(AssemblySegmentKind.Chunk, 1, 40, false, 0),
            new AssemblySegment(AssemblySegmentKind.Abort, 0, 0, true, 0)),
        LegendSymbol.AssemblyInterleaved => Bar(new AssemblySegment(AssemblySegmentKind.Chunk, 1, 30, false, 0),
            new AssemblySegment(AssemblySegmentKind.Chunk, 2, 30, false, 3)),
        LegendSymbol.AssemblyHandshake => Bar(new AssemblySegment(AssemblySegmentKind.Preamble, 0, 20, false, 0, TapDirection.Outbound),
            new AssemblySegment(AssemblySegmentKind.Greeting, 0, 40, true, 0, TapDirection.Inbound)),
        LegendSymbol.Heat => new CompareLegendSwatch { Kind = CompareLegendKind.Density, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
        LegendSymbol.PackedSpan => new CompareLegendSwatch { Kind = CompareLegendKind.Span, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
        LegendSymbol.Differs => new CompareLegendSwatch { Kind = CompareLegendKind.Differs, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
        _ => null,
    };

    private static AssemblyBar Bar(params AssemblySegment[] segments) =>
        new() { Segments = segments, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Height = AssemblyBar.BarHeight };

    protected override Size MeasureOverride(Size availableSize)
    {
        Child?.Measure(new Size(SwatchWidth, SwatchHeight));
        return new Size(SwatchWidth, SwatchHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is { } child)
        {
            // A compare swatch keeps its own size, left-aligned; the assembly bar takes the whole width.
            double width = child is AssemblyBar ? finalSize.Width : child.DesiredSize.Width;
            double height = Math.Min(finalSize.Height, child.DesiredSize.Height);
            child.Arrange(new Rect(0, (finalSize.Height - height) / 2, width, height));
        }

        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Child is not null) return;
        ThemePalette palette = ThemeBrushes.For(this);
        if (Legend.FlagOf(Symbol) is { } flag)
        {
            FlagChip.Draw(context, palette, flag, new Point(0, (SwatchHeight - FlagChip.ChipHeight) / 2));
            return;
        }

        if (Legend.RoleOf(Symbol) is { } role)
        {
            HexCells(context, palette, role, HexCellMarks.None);
            return;
        }

        if (Legend.PipeOf(Symbol) is { } pipe)
        {
            // As the timeline gutter and the ladder draw it, before a lane label.
            PipeGlyph.Draw(context, palette, pipe, ThemeBrush.TextPrimary, new Point(1, Math.Round((SwatchHeight - PipeGlyph.Size) / 2)));
            Text(context, palette, pipe switch { PipeContent.Channel => "LogLine", PipeContent.Collection => "Users", _ => "3·1" }, 10,
                ThemeBrush.TextSecondary, PipeGlyph.Size + 5);
            return;
        }

        if (Legend.ChipOf(Symbol) is { } chip)
        {
            // As the events and ladder rows draw it, after the wire type.
            PipeChip.Draw(context, palette, chip, new Point(0, (SwatchHeight - FlagChip.ChipHeight) / 2));
            return;
        }

        Draw(context, palette);
    }

    private const double W = SwatchWidth, H = SwatchHeight, Mid = SwatchHeight / 2;

    private void Draw(DrawingContext context, ThemePalette palette)
    {
        switch (Symbol)
        {
            case LegendSymbol.Inbound:
                Text(context, palette, "▼ IN", 10, ThemeBrush.DirectionInbound, 0, FontWeight.SemiBold);
                break;
            case LegendSymbol.Outbound:
                Text(context, palette, "▲ OUT", 10, ThemeBrush.DirectionOutbound, 0, FontWeight.SemiBold);
                break;
            case LegendSymbol.LadderArrows:
                Lifelines(context, palette);
                Arrow(context, palette[ThemeBrush.DirectionInbound], 4, W - 5, 6);
                Arrow(context, palette[ThemeBrush.DirectionOutbound], W - 5, 4, 14);
                break;
            case LegendSymbol.Lifelines:
                Lifelines(context, palette);
                Text(context, palette, "peer", 8.5, ThemeBrush.TextTertiary, 8);
                Text(context, palette, "tap", 8.5, ThemeBrush.TextTertiary, W - 24);
                break;
            case LegendSymbol.ReadOp:
                OpChip(context, palette, TransportOpChip.CreateLabel(palette, 4471, TapDirection.Inbound, false), TapDirection.Inbound, false);
                break;
            case LegendSymbol.WriteOp:
                OpChip(context, palette, TransportOpChip.CreateLabel(palette, 3104, TapDirection.Outbound, false), TapDirection.Outbound, false);
                break;
            case LegendSymbol.OpRange:
                OpChip(context, palette, new TextLayout("#4471–#4473", new Typeface(palette.Mono), 9.5, palette[ThemeBrush.SplitTransport]),
                    TapDirection.Inbound, true);
                break;
            case LegendSymbol.SplitOp:
                OpChip(context, palette, TransportOpChip.CreateLabel(palette, 4472, TapDirection.Inbound, true), TapDirection.Inbound, true);
                break;
            case LegendSymbol.CountBadge:
                CountBadge.Draw(context, palette, "3 rows · 9", CountBadgeTone.Neutral, new Point(0, (H - CountBadge.BadgeHeight) / 2));
                break;
            case LegendSymbol.DifferenceBadge:
                CountBadge.Draw(context, palette, "12", CountBadgeTone.Difference, new Point(0, (H - CountBadge.BadgeHeight) / 2));
                break;
            case LegendSymbol.SyncedBadge:
                Text(context, palette, "synced", 10.5, ThemeBrush.StatusOk, 0);
                break;
            case LegendSymbol.RecordingPill:
                Pill(context, palette, "00:42");
                break;
            case LegendSymbol.RecordAllPill:
                Pill(context, palette, "ALL · 3");
                break;
            case LegendSymbol.Overview:
            {
                context.FillRectangle(palette[ThemeBrush.SurfaceChrome], new Rect(0, 2, W, 16));
                ReadOnlySpan<double> heights = [3, 6, 10, 4, 12, 7, 2, 9, 5, 11, 3, 6, 8, 4];
                for (int i = 0; i < heights.Length; i++)
                    context.FillRectangle(palette[ThemeBrush.DirectionInbound], new Rect(2 + (i * 6), 17 - heights[i], 4, heights[i]));
                context.DrawRectangle(null, palette.Pen(ThemeBrush.TextSecondary, 1), new Rect(30.5, 2.5, 24, 15));
                break;
            }

            case LegendSymbol.RangeHandles:
                // As the overview draws its box: text.primary edges, a grip on each, the hovered one in direction.inbound.
                context.FillRectangle(palette[ThemeBrush.SurfaceChrome], new Rect(0, 2, W, 16));
                context.DrawRectangle(null, palette.Pen(ThemeBrush.TextPrimary, 1.5), new Rect(22, 2.75, 40, 14.5));
                Timeline.TimelineControl.DrawRangeHandle(context, palette, 22, 2, 16, hot: false);
                Timeline.TimelineControl.DrawRangeHandle(context, palette, 62, 2, 16, hot: true);
                break;
            case LegendSymbol.Ruler:
                context.FillRectangle(palette[ThemeBrush.SurfacePanel], new Rect(0, 2, W, 16));
                for (double x = 0; x < W; x += 28) context.FillRectangle(palette[ThemeBrush.BorderDefault], new Rect(x, 2, 1, 15));
                Text(context, palette, "10 ms", 9.5, ThemeBrush.TextTertiary, 32, mono: true);
                break;
            case LegendSymbol.RulerSelection:
                context.FillRectangle(palette[ThemeBrush.SurfacePanel], new Rect(0, 2, W, 16));
                for (double x = 0; x < W; x += 28) context.FillRectangle(palette[ThemeBrush.BorderDefault], new Rect(x, 2, 1, 15));
                Timeline.RulerSelection.DrawBand(context, palette, 18, 62, 0, H);
                break;
            case LegendSymbol.LaneGroups:
                context.FillRectangle(palette[ThemeBrush.SurfaceRaised], new Rect(0, 3, W, 14));
                Text(context, palette, "▾ FRAMES", 9, ThemeBrush.TextSecondary, 4, FontWeight.SemiBold);
                break;
            case LegendSymbol.Block:
                Lane(context, palette);
                Block(context, palette, ThemeBrush.DirectionInbound, 6, 14);
                Block(context, palette, ThemeBrush.DirectionOutbound, 30, 10);
                Block(context, palette, ThemeBrush.StatusError, 52, 8);
                break;
            case LegendSymbol.SplitMarks:
                Lane(context, palette);
                context.FillRectangle(palette[ThemeBrush.SplitTransport], new Rect(20, 4, 2, 12));
                context.FillRectangle(palette[ThemeBrush.SplitNexnet], new Rect(50, 4, 2, 12));
                break;
            case LegendSymbol.Span:
                Lane(context, palette);
                Span(context, palette, ThemeBrush.TimelineSpan, cancelled: false);
                break;
            case LegendSymbol.TracedSpan:
                Lane(context, palette);
                Span(context, palette, ThemeBrush.AccentTrace, cancelled: false);
                break;
            case LegendSymbol.CancelledSpan:
                Lane(context, palette);
                Span(context, palette, ThemeBrush.TimelineSpan, cancelled: true);
                break;
            case LegendSymbol.PipeLifetime:
                Lane(context, palette);
                context.FillRectangle(palette[ThemeBrush.BorderDefault], new Rect(8, Mid - 1, W - 16, 2));
                context.FillRectangle(palette[ThemeBrush.StatusOk], new Rect(8, 6, 2, 8));
                context.FillRectangle(palette[ThemeBrush.StatusRecording], new Rect(W - 10, 6, 2, 8));
                break;
            case LegendSymbol.TraceBand:
                Lane(context, palette);
                context.FillRectangle(palette[ThemeBrush.AccentTraceBand], new Rect(20, 0, 40, H));
                DashedEdge(context, palette[ThemeBrush.AccentTrace], 20);
                DashedEdge(context, palette[ThemeBrush.AccentTrace], 59);
                break;
            case LegendSymbol.Gap:
                Lane(context, palette);
                context.FillRectangle(palette[ThemeBrush.TimelineGap], new Rect(24, 0, 32, H));
                break;
            case LegendSymbol.Playhead:
                Lane(context, palette);
                context.FillRectangle(palette[ThemeBrush.TextPrimary], new Rect(W / 2 - 1, 0, 2, H));
                break;
            case LegendSymbol.PinnedLane:
                context.FillRectangle(palette[ThemeBrush.SurfaceLane], new Rect(0, 0, W, H));
                context.FillRectangle(palette[ThemeBrush.DirectionInbound], new Rect(0, 0, 2, H));
                Text(context, palette, "Pipe 3·1", 10.5, ThemeBrush.TextPrimary, 6, FontWeight.Bold);
                Text(context, palette, "◆", 9, ThemeBrush.AccentTrace, W - 12);
                break;
            case LegendSymbol.BracketMember:
                Rows(context, palette, 1);
                context.DrawRectangle(palette[ThemeBrush.AccentTrace], null, new RoundedRect(new Rect(4, 2, 3, H - 4), 2));
                break;
            case LegendSymbol.BracketInterleaved:
                using (context.PushOpacity(0.6)) Rows(context, palette, 1);
                for (double y = 1; y < H; y += 4) context.FillRectangle(palette[ThemeBrush.AccentTrace], new Rect(4, y, 2, 2));
                break;
            case LegendSymbol.TimeScaled:
                context.FillRectangle(palette[ThemeBrush.BorderRow], new Rect(0, 3, W, 1));
                context.FillRectangle(palette[ThemeBrush.BorderRow], new Rect(0, 7, W, 1));
                context.FillRectangle(palette[ThemeBrush.BorderRow], new Rect(0, 18, W, 1));
                break;
            case LegendSymbol.HexReadRule:
                context.FillRectangle(palette[ThemeBrush.SplitTransport], new Rect(0, 3, W, 1.5));
                Text(context, palette, "read #12 →", 9.5, ThemeBrush.SplitTransport, 2, top: 6);
                break;
            case LegendSymbol.HexFrameAnnotation:
                Text(context, palette, "▸ Ping #4", 9.5, ThemeBrush.AccentTrace, 0);
                break;
            case LegendSymbol.HexRedacted:
                HexCells(context, palette, HexCellRole.Body, HexCellMarks.Redacted);
                break;
            case LegendSymbol.HexSelectedField:
                HexCells(context, palette, HexCellRole.BodySelected, HexCellMarks.Selected);
                break;
            case LegendSymbol.HexHoveredField:
                HexCells(context, palette, HexCellRole.Body, HexCellMarks.Hovered);
                break;
            case LegendSymbol.HexBookmark:
            {
                StreamGeometry diamond = new();
                using (StreamGeometryContext g = diamond.Open())
                {
                    g.BeginFigure(new Point(6, Mid - 4), true);
                    g.LineTo(new Point(10, Mid));
                    g.LineTo(new Point(6, Mid + 4));
                    g.LineTo(new Point(2, Mid));
                    g.EndFigure(true);
                }

                context.DrawGeometry(palette[ThemeBrush.AccentTrace], null, diamond);
                Text(context, palette, "0040", 10, ThemeBrush.TextTertiary, 14, mono: true);
                break;
            }

            case LegendSymbol.FileA:
                FileMark(context, palette, "A", ThemeBrush.DirectionInbound);
                break;
            case LegendSymbol.FileB:
                FileMark(context, palette, "B", ThemeBrush.DirectionOutbound);
                break;
            case LegendSymbol.DeltaStrip:
                context.FillRectangle(palette[ThemeBrush.SurfaceChrome], new Rect(0, 1, W, 18));
                context.FillRectangle(palette[ThemeBrush.SplitNexnet], new Rect(14, Mid - 5, 4, 10));
                context.FillRectangle(palette[ThemeBrush.SplitNexnet], new Rect(40, Mid - 5, 10, 10));
                context.FillRectangle(palette[ThemeBrush.SplitNexnet], new Rect(66, Mid - 5, 3, 10));
                break;
            case LegendSymbol.DiffBand:
            {
                context.FillRectangle(palette[ThemeBrush.DiffBand], new Rect(20, 0, 40, H));
                IPen pen = palette.Pen(ThemeBrush.SplitNexnet, 1, dashed: true);
                context.DrawLine(pen, new Point(20.5, 0), new Point(20.5, H));
                context.DrawLine(pen, new Point(59.5, 0), new Point(59.5, H));
                break;
            }

            case LegendSymbol.PinnedEntity:
            {
                if (this.TryFindResource("Glyph.Compare.Pinned", ActualThemeVariant, out object? found) && found is Geometry glyph)
                {
                    using (context.PushTransform(Matrix.CreateTranslation(2, Mid - 4)))
                        context.DrawGeometry(palette[ThemeBrush.DirectionInbound], null, glyph);
                }

                Text(context, palette, "Chat.Send", 10, ThemeBrush.TextPrimary, 14, FontWeight.SemiBold);
                break;
            }
        }
    }

    private static void Lane(DrawingContext context, ThemePalette palette) =>
        context.FillRectangle(palette[ThemeBrush.SurfaceLane], new Rect(0, 4, W, 12));

    private static void Block(DrawingContext context, ThemePalette palette, ThemeBrush ink, double x, double width) =>
        context.DrawRectangle(palette[ink], null, new RoundedRect(new Rect(x, 6, width, 8), 1.5));

    // As the timeline's span: 10 px, 3 px radius; a cancelled one keeps a 3 px status.error right edge.
    private static void Span(DrawingContext context, ThemePalette palette, ThemeBrush fill, bool cancelled)
    {
        Rect span = new(8, 5, W - 16, 10);
        if (cancelled)
        {
            context.DrawRectangle(palette[ThemeBrush.StatusError], null, new RoundedRect(span, 3));
            context.DrawRectangle(palette[fill], null, new RoundedRect(new Rect(span.X, span.Y, span.Width - 3, 10), 3, 0, 0, 3));
        }
        else
        {
            context.DrawRectangle(palette[fill], null, new RoundedRect(span, 3));
        }
    }

    private static void DashedEdge(DrawingContext context, IBrush brush, double x)
    {
        for (double y = 0; y < H; y += 4) context.FillRectangle(brush, new Rect(x, y, 1, Math.Min(2, H - y)));
    }

    private static void Lifelines(DrawingContext context, ThemePalette palette)
    {
        context.FillRectangle(palette[ThemeBrush.BorderDefault], new Rect(3, 0, 1, H));
        context.FillRectangle(palette[ThemeBrush.BorderDefault], new Rect(W - 4, 0, 1, H));
    }

    private static void Arrow(DrawingContext context, IBrush ink, double from, double to, double y)
    {
        double head = to > from ? -5 : 5;
        context.FillRectangle(ink, new Rect(Math.Min(from, to + head), y - 0.75, Math.Abs(to + head - from), 1.5));
        StreamGeometry geometry = new();
        using (StreamGeometryContext g = geometry.Open())
        {
            g.BeginFigure(new Point(to, y), true);
            g.LineTo(new Point(to + head, y - 3));
            g.LineTo(new Point(to + head, y + 3));
            g.EndFigure(true);
        }

        context.DrawGeometry(ink, null, geometry);
    }

    private static void Rows(DrawingContext context, ThemePalette palette, double thickness)
    {
        context.FillRectangle(palette[ThemeBrush.BorderRow], new Rect(10, 6, W - 10, thickness));
        context.FillRectangle(palette[ThemeBrush.BorderRow], new Rect(10, 13, W - 10, thickness));
        context.FillRectangle(palette[ThemeBrush.DirectionInbound], new Rect(24, 9, 40, 1.5));
    }

    private static void OpChip(DrawingContext context, ThemePalette palette, TextLayout label, TapDirection direction, bool split) =>
        TransportOpChip.Draw(context, palette, label, direction, split, new Point(0, (H - TransportOpChip.ChipHeight) / 2));

    // The title bar's record button: the status.recording outline and dot around the timer.
    private static void Pill(DrawingContext context, ThemePalette palette, string text)
    {
        TextLayout label = Layout(palette, text, 10, ThemeBrush.StatusRecording, FontWeight.Normal, mono: true);
        double width = Math.Ceiling(label.Width) + 22;
        context.DrawRectangle(null, palette.Pen(ThemeBrush.StatusRecording, 1), new RoundedRect(new Rect(0.5, 1.5, width, H - 3), 4));
        context.DrawEllipse(palette[ThemeBrush.StatusRecording], null, new Point(8, Mid), 3.5, 3.5);
        label.Draw(context, new Point(15, (H - label.Height) / 2));
    }

    private static void FileMark(DrawingContext context, ThemePalette palette, string letter, ThemeBrush ink)
    {
        Text(context, palette, letter, 11, ink, 0, FontWeight.Bold, mono: true);
        context.DrawRectangle(palette[ink], null, new RoundedRect(new Rect(14, Mid - 3, W - 20, 6), 2));
    }

    // Three hex cells as the hex view fills and marks them.
    private static void HexCells(DrawingContext context, ThemePalette palette, HexCellRole role, HexCellMarks marks)
    {
        const double cell = 21, ring = 1.5;
        ReadOnlySpan<string> bytes = ["1F", "00", "2A"];
        bool redacted = (marks & HexCellMarks.Redacted) != 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            Rect rect = new(i * cell, 0, cell, H);
            if (redacted) context.DrawRectangle(palette[ThemeBrush.HexRedacted], null, rect, 2, 2);
            else if (HexBackgroundRenderer.Fill(role) is { } fill) context.DrawRectangle(palette[fill], null, rect, 2, 2);
            ThemeBrush ink = redacted || role == HexCellRole.OtherFrame ? ThemeBrush.TextTertiary : ThemeBrush.TextPrimary;
            TextLayout text = Layout(palette, redacted ? "··" : bytes[i], 10, ink, FontWeight.Normal, mono: true);
            text.Draw(context, new Point(rect.X + ((cell - text.Width) / 2), (H - text.Height) / 2));
        }

        Rect field = new(0, 0, cell * 2, H);
        if ((marks & HexCellMarks.Selected) != 0)
            context.DrawRectangle(null, palette.Pen(ThemeBrush.AccentTrace, ring), field.Deflate(ring / 2), 2, 2);
        if ((marks & HexCellMarks.Hovered) != 0)
            context.DrawRectangle(null, palette.Pen(ThemeBrush.DirectionInbound, ring), field.Deflate(ring / 2), 2, 2);
    }

    private static void Text(DrawingContext context, ThemePalette palette, string text, double size, ThemeBrush ink, double x,
        FontWeight weight = FontWeight.Normal, bool mono = false, double? top = null)
    {
        TextLayout layout = Layout(palette, text, size, ink, weight, mono);
        layout.Draw(context, new Point(x, top ?? ((H - layout.Height) / 2)));
    }

    private static TextLayout Layout(ThemePalette palette, string text, double size, ThemeBrush ink, FontWeight weight, bool mono) =>
        new(text, new Typeface(mono ? palette.Mono : palette.Ui, FontStyle.Normal, weight), size, palette[ink]);
}
