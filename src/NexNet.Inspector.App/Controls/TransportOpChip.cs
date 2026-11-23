using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Controls;

/// <summary>
/// A transport-op chip (controls/chip, the ladder's Transport column): a 15 px outlined tag with the op's id in 9.5 px
/// mono: a read as #4471, a write as w3104. A read the frame was split across takes split.transport for its
/// border and text; a write takes direction.outbound text. Drawn; the label is built when the op changes.
/// </summary>
public sealed class TransportOpChip : Control
{
    /// <summary>The chip's height: chip.md's 13 px content box plus its 1 px border above and below (as chip.html draws it).</summary>
    public const double ChipHeight = 15;

    private const double HorizontalPadding = 3;
    private const double TextSize = 9.5;

    public static readonly StyledProperty<long> OpIdProperty = AvaloniaProperty.Register<TransportOpChip, long>(nameof(OpId));

    public static readonly StyledProperty<TapDirection> DirectionProperty =
        AvaloniaProperty.Register<TransportOpChip, TapDirection>(nameof(Direction));

    public static readonly StyledProperty<bool> IsSplitProperty = AvaloniaProperty.Register<TransportOpChip, bool>(nameof(IsSplit));

    private FormattedText? _text;
    private ThemePalette? _textPalette;

    static TransportOpChip()
    {
        AffectsMeasure<TransportOpChip>(OpIdProperty, DirectionProperty, IsSplitProperty);
        AffectsRender<TransportOpChip>(OpIdProperty, DirectionProperty, IsSplitProperty);
    }

    public TransportOpChip() => Describe();

    /// <summary>The transport op's sequence id (the read or write number).</summary>
    public long OpId
    {
        get => GetValue(OpIdProperty);
        set => SetValue(OpIdProperty, value);
    }

    /// <summary>Inbound is a read, outbound a write.</summary>
    public TapDirection Direction
    {
        get => GetValue(DirectionProperty);
        set => SetValue(DirectionProperty, value);
    }

    /// <summary>Whether the frame was split across this op and another (the T flag).</summary>
    public bool IsSplit
    {
        get => GetValue(IsSplitProperty);
        set => SetValue(IsSplitProperty, value);
    }

    /// <summary>The chip's label: "#id" for a read, "w" + id for a write.</summary>
    public static string Label(long opId, TapDirection direction) =>
        (direction == TapDirection.Outbound ? "w" : "#") + opId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The chip's border and text tokens.</summary>
    public static (ThemeBrush Border, ThemeBrush Text) Inks(TapDirection direction, bool isSplit) =>
        isSplit ? (ThemeBrush.SplitTransport, ThemeBrush.SplitTransport)
        : direction == TapDirection.Outbound ? (ThemeBrush.BorderDefault, ThemeBrush.DirectionOutbound)
        : (ThemeBrush.BorderDefault, ThemeBrush.TextSecondary);

    /// <summary>
    /// The shaped label of a chip, for dense rows (the ladder) that draw chips with <see cref="Draw"/> and keep the
    /// label while the row shows the same op, so a redraw shapes nothing.
    /// </summary>
    public static TextLayout CreateLabel(ThemePalette palette, long opId, TapDirection direction, bool isSplit)
    {
        ArgumentNullException.ThrowIfNull(palette);
        (_, ThemeBrush ink) = Inks(direction, isSplit);
        return new TextLayout(Label(opId, direction), new Typeface(palette.Mono), TextSize, palette[ink]);
    }

    /// <summary>The width of a chip showing <paramref name="label"/>.</summary>
    public static double MeasureWidth(TextLayout label)
    {
        ArgumentNullException.ThrowIfNull(label);
        return Math.Ceiling(label.Width + (2 * HorizontalPadding) + 2);
    }

    /// <summary>Paints a chip with its top-left at <paramref name="origin"/>; returns its width.</summary>
    public static double Draw(DrawingContext context, ThemePalette palette, TextLayout label, TapDirection direction, bool isSplit, Point origin)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(palette);
        double width = MeasureWidth(label);
        (ThemeBrush border, _) = Inks(direction, isSplit);
        // Inset half a pixel so the 1 px border lands on whole pixels.
        Rect outline = new Rect(origin, new Size(width, ChipHeight)).Deflate(0.5);
        context.DrawRectangle(null, palette.Pen(border, 1), new RoundedRect(outline, 3));
        label.Draw(context, new Point(origin.X + ((width - label.Width) / 2), origin.Y + ((ChipHeight - label.Height) / 2)));
        return width;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Ceiling(Measured(ThemeBrushes.For(this)).Width + (2 * HorizontalPadding) + 2), ChipHeight);

    public override void Render(DrawingContext context)
    {
        ThemePalette palette = ThemeBrushes.For(this);
        FormattedText text = Measured(palette);
        (ThemeBrush border, _) = Inks(Direction, IsSplit);
        // Inset half a pixel so the 1 px border lands on whole pixels.
        Rect outline = new Rect(Bounds.Size).Deflate(0.5);
        context.DrawRectangle(null, palette.Pen(border, 1), new RoundedRect(outline, 3));
        context.DrawText(text, new Point((Bounds.Width - text.Width) / 2, (ChipHeight - text.Height) / 2));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OpIdProperty || change.Property == DirectionProperty || change.Property == IsSplitProperty)
        {
            _text = null;
            Describe();
        }
        else if (change.Property == ThemeVariantScope.ActualThemeVariantProperty)
        {
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    private void Describe() =>
        AutomationProperties.SetName(this, (Direction == TapDirection.Outbound ? "Write " : "Read ") + Label(OpId, Direction) + (IsSplit ? ", split" : ""));

    private FormattedText Measured(ThemePalette palette)
    {
        if (_text is not null && ReferenceEquals(_textPalette, palette)) return _text;
        (_, ThemeBrush ink) = Inks(Direction, IsSplit);
        _textPalette = palette;
        return _text = new FormattedText(Label(OpId, Direction), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(palette.Mono), TextSize, palette[ink]);
    }
}
