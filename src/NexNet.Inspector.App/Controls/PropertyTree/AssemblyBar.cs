using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using NexNet.Inspector.App.Controls.Primitives;

namespace NexNet.Inspector.App.Controls.PropertyTree;

/// <summary>What an assembly bar segment stands for.</summary>
public enum AssemblySegmentKind : byte
{
    /// <summary>MessageStart: "S", no payload bytes.</summary>
    Start = 0,

    /// <summary>A fragment carrying bytes (a MessageChunk, a DuplexPipeWrite of a run): numbered from 1.</summary>
    Chunk = 1,

    /// <summary>MessageAbort: "A", no payload bytes.</summary>
    Abort = 2,
}

/// <summary>One segment of the assembly bar: one fragment of the message.</summary>
/// <param name="Kind">Start, chunk or abort.</param>
/// <param name="Number">The chunk's 1-based number; 0 otherwise.</param>
/// <param name="Length">Payload bytes the fragment carried (its share of the bar).</param>
/// <param name="IsCurrent">True for the fragment of the selected frame.</param>
/// <param name="InterleavedBefore">Frames of other messages between the previous fragment and this one.</param>
public readonly record struct AssemblySegment(AssemblySegmentKind Kind, int Number, long Length, bool IsCurrent, int InterleavedBefore)
{
    /// <summary>The segment's label: S, A or the chunk number.</summary>
    public string Label => Kind switch
    {
        AssemblySegmentKind.Start => "S",
        AssemblySegmentKind.Abort => "A",
        _ => Number.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// The message-assembly bar of the inspector (property-tree footer): an 18 px bar of segments proportional to the
/// bytes each fragment carried, 2 px apart, 3 px radius. The current fragment is filled accent.trace with on-accent
/// text; the others hex.body-selected with a border.default edge; S and A take a fixed narrow width. Where frames of
/// other messages arrived between two fragments the gap widens and a tertiary tick marks it (interleaving).
/// </summary>
public sealed class AssemblyBar : Control
{
    /// <summary>The bar's height.</summary>
    public const double BarHeight = 18;

    /// <summary>The gap between two segments.</summary>
    public const double Gap = 2;

    /// <summary>The gap that holds an interleaving tick.</summary>
    public const double InterleavedGap = 5;

    /// <summary>The width of a segment that carries no bytes (S, A) and the least any segment gets.</summary>
    public const double MinSegmentWidth = 14;

    public static readonly StyledProperty<IReadOnlyList<AssemblySegment>?> SegmentsProperty =
        AvaloniaProperty.Register<AssemblyBar, IReadOnlyList<AssemblySegment>?>(nameof(Segments));

    private FormattedText?[] _labels = [];
    private ThemePalette? _palette;
    private IReadOnlyList<AssemblySegment>? _labelled;
    private (double X, double Width)[] _boxes = [];
    private IReadOnlyList<AssemblySegment>? _laidOut;
    private double _laidOutWidth = double.NaN;

    static AssemblyBar()
    {
        AffectsRender<AssemblyBar>(SegmentsProperty);
        SegmentsProperty.Changed.AddClassHandler<AssemblyBar>(static (bar, _) => bar.Describe());
    }

    public AssemblyBar()
    {
        Height = BarHeight;
        Describe();
    }

    /// <summary>The fragments in message order.</summary>
    public IReadOnlyList<AssemblySegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    /// <summary>
    /// Lays the segments out across <paramref name="width"/>: fixed width for S and A, the rest shared by bytes with a
    /// floor, the gaps widened where frames were interleaved. Pure, so the proportions are tested without drawing.
    /// </summary>
    /// <returns>Each segment's left edge and width.</returns>
    public static (double X, double Width)[] Layout(IReadOnlyList<AssemblySegment> segments, double width)
    {
        ArgumentNullException.ThrowIfNull(segments);
        (double X, double Width)[] boxes = new (double, double)[segments.Count];
        if (segments.Count == 0) return boxes;

        double gaps = 0;
        double fixedWidth = 0;
        long bytes = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            if (i > 0) gaps += segments[i].InterleavedBefore > 0 ? InterleavedGap : Gap;
            if (segments[i].Kind == AssemblySegmentKind.Chunk && segments[i].Length > 0) bytes += segments[i].Length;
            else fixedWidth += MinSegmentWidth;
        }

        double share = Math.Max(0, width - gaps - fixedWidth);
        double x = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            if (i > 0) x += segments[i].InterleavedBefore > 0 ? InterleavedGap : Gap;
            AssemblySegment s = segments[i];
            double w = s.Kind == AssemblySegmentKind.Chunk && s.Length > 0 && bytes > 0
                ? Math.Max(MinSegmentWidth, share * s.Length / bytes)
                : MinSegmentWidth;
            boxes[i] = (x, w);
            x += w;
        }

        // The floor can push the last segments past the edge: scale back to fit.
        if (x > width && x > 0)
        {
            double scale = width / x;
            for (int i = 0; i < boxes.Length; i++) boxes[i] = (boxes[i].X * scale, boxes[i].Width * scale);
        }

        return boxes;
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, BarHeight);

    public override void Render(DrawingContext context)
    {
        if (Segments is not { Count: > 0 } segments) return;
        ThemePalette palette = ThemeBrushes.For(this);
        if (!ReferenceEquals(palette, _palette) || !ReferenceEquals(segments, _labelled)) Label(palette, segments);

        // Laid out again only when the segments or the width change: a repaint allocates nothing.
        if (!ReferenceEquals(segments, _laidOut) || Bounds.Width != _laidOutWidth)
        {
            _boxes = Layout(segments, Bounds.Width);
            _laidOut = segments;
            _laidOutWidth = Bounds.Width;
        }

        (double X, double Width)[] boxes = _boxes;
        IPen edge = palette.Pen(ThemeBrush.BorderDefault, 1);
        for (int i = 0; i < segments.Count; i++)
        {
            AssemblySegment s = segments[i];
            (double x, double w) = boxes[i];
            if (i > 0 && s.InterleavedBefore > 0)
            {
                // The tick sits in the middle of the widened gap.
                double tick = x - (InterleavedGap / 2) - 0.5;
                context.DrawRectangle(palette[ThemeBrush.TextTertiary], null, new Rect(tick, 4, 1, BarHeight - 8));
            }

            Rect box = new(x, 0, w, BarHeight);
            if (s.IsCurrent) context.DrawRectangle(palette[ThemeBrush.AccentTrace], null, box, 3, 3);
            else context.DrawRectangle(palette[ThemeBrush.HexBodySelected], edge, box.Deflate(0.5), 2.5, 2.5);

            if (_labels[i] is { } label && label.Width <= w)
                context.DrawText(label, new Point(x + ((w - label.Width) / 2), (BarHeight - label.Height) / 2));
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty) InvalidateVisual();
    }

    private void Label(ThemePalette palette, IReadOnlyList<AssemblySegment> segments)
    {
        _palette = palette;
        _labelled = segments;
        _labels = new FormattedText?[segments.Count];
        Typeface bold = new(palette.Ui, FontStyle.Normal, FontWeight.Bold);
        for (int i = 0; i < segments.Count; i++)
        {
            ThemeBrush ink = segments[i].IsCurrent ? ThemeBrush.TextOnAccent : ThemeBrush.TextPrimary;
            _labels[i] = new FormattedText(segments[i].Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold, 10, palette[ink]);
        }
    }

    private void Describe()
    {
        int chunks = 0;
        int current = 0;
        if (Segments is { } segments)
        {
            foreach (AssemblySegment s in segments)
            {
                if (s.Kind == AssemblySegmentKind.Chunk) chunks++;
                if (s.IsCurrent) current = s.Number;
            }
        }

        AutomationProperties.SetName(this, current > 0
            ? $"Message assembly: chunk {current} of {chunks}"
            : $"Message assembly: {chunks} chunks");
    }
}
