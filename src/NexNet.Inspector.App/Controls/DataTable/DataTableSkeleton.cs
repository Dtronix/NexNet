using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NexNet.Inspector.App.Controls.Primitives;

namespace NexNet.Inspector.App.Controls.DataTable;

/// <summary>
/// The loading state's skeleton bars (data-table.html <c>.ld</c>; OQ2): row-high surface.raised bars with a 3 px radius,
/// 8 px in from the sides and 4 px apart, as many as fit. Drawn; it never takes the pointer.
/// </summary>
public sealed class DataTableSkeleton : Control
{
    /// <summary>The bars' height (the table's row height).</summary>
    public static readonly StyledProperty<double> BarHeightProperty =
        AvaloniaProperty.Register<DataTableSkeleton, double>(nameof(BarHeight), 20);

    /// <summary>Space above each bar and the bars' side inset.</summary>
    public const double Gap = 4;

    /// <summary>The bars' inset from the sides.</summary>
    public const double Inset = 8;

    /// <summary>The bars' corner radius (radius.sm).</summary>
    public const double Radius = 3;

    static DataTableSkeleton()
    {
        AffectsRender<DataTableSkeleton>(BarHeightProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<DataTableSkeleton>(false);
    }

    public double BarHeight
    {
        get => GetValue(BarHeightProperty);
        set => SetValue(BarHeightProperty, value);
    }

    /// <summary>How many bars a height of <paramref name="height"/> shows.</summary>
    public static int BarsFor(double height, double barHeight) =>
        barHeight <= 0 ? 0 : Math.Max(0, (int)Math.Floor((height - Gap) / (barHeight + Gap)));

    public override void Render(DrawingContext context)
    {
        Size size = Bounds.Size;
        double bar = BarHeight;
        int count = BarsFor(size.Height, bar);
        if (count == 0 || size.Width <= 2 * Inset) return;
        IBrush fill = ThemeBrushes.For(this)[ThemeBrush.SurfaceRaised];
        for (int i = 0; i < count; i++)
        {
            Rect rect = new(Inset, Gap + (i * (bar + Gap)), size.Width - (2 * Inset), bar);
            context.DrawRectangle(fill, null, new RoundedRect(rect, Radius));
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty || change.Property == BoundsProperty)
            InvalidateVisual();
    }
}
