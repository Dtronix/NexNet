using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NexNet.Inspector.App.Controls.Primitives;

namespace NexNet.Inspector.App.Controls.Compare;

/// <summary>What a legend swatch shows.</summary>
public enum CompareLegendKind : byte
{
    /// <summary>Frame density: a ramp of the first file's colour.</summary>
    Density = 0,

    /// <summary>A packed span (timeline.span).</summary>
    Span = 1,

    /// <summary>The 3 px Δ flag (split.nexnet).</summary>
    Differs = 2,
}

/// <summary>
/// A swatch of the aligned view's legend row, drawn with the same token brushes as the rows it explains (a density ramp
/// cannot be a token: it is the file colour at several opacities).
/// </summary>
public sealed class CompareLegendSwatch : Control
{
    public static readonly StyledProperty<CompareLegendKind> KindProperty =
        AvaloniaProperty.Register<CompareLegendSwatch, CompareLegendKind>(nameof(Kind));

    static CompareLegendSwatch()
    {
        AffectsRender<CompareLegendSwatch>(KindProperty);
        AffectsMeasure<CompareLegendSwatch>(KindProperty);
    }

    /// <summary>The swatch.</summary>
    public CompareLegendKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => Kind switch
    {
        CompareLegendKind.Density => new Size(24, 10),
        CompareLegendKind.Span => new Size(16, 10),
        _ => new Size(3, 12),
    };

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty) InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        ThemePalette palette = ThemeBrushes.For(this);
        double middle = Bounds.Height / 2;
        switch (Kind)
        {
            case CompareLegendKind.Density:
                IBrush ink = palette[ThemeBrush.DirectionInbound];
                for (int i = 0; i < 6; i++)
                {
                    using (context.PushOpacity(0.15 + (0.17 * i)))
                        context.FillRectangle(ink, new Rect(i * 4, middle - 3, 4.5, 6));
                }

                break;
            case CompareLegendKind.Span:
                context.DrawRectangle(palette[ThemeBrush.TimelineSpan], null, new Rect(0, middle - 3, 16, 6), 2, 2);
                break;
            default:
                context.FillRectangle(palette[ThemeBrush.SplitNexnet], new Rect(0, middle - 6, 3, 12));
                break;
        }
    }
}
