using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Assembly;

namespace NexNet.Inspector.App.Controls;

/// <summary>
/// The pipe-kind chip beside a frame's wire type: "CH" when the duplex pipe carries a channel, "CO" when it carries a
/// collection; a raw pipe gets none. The wire type stays the truth ("DuplexPipeWrite"); the chip says what rides on it.
/// Drawn like <see cref="FlagChip"/> (12 px, 8.5 px bold letters) but outlined in text.secondary rather than filled, so
/// it reads as a tag of the frame's kind and not as one more flag. <see cref="Draw"/> is public for dense rows.
/// </summary>
public sealed class PipeChip : Control
{
    private const double HorizontalPadding = 3;
    private const double CornerRadius = 3;
    private const double LetterSize = 8.5;

    /// <summary>What the pipe carries; <see cref="PipeContent.Raw"/> draws nothing.</summary>
    public static readonly StyledProperty<PipeContent> KindProperty =
        AvaloniaProperty.Register<PipeChip, PipeContent>(nameof(Kind), PipeContent.Channel);

    // Per palette: the two letter pairs, shaped once (the outline pen is the palette's), so a chip per dense row allocates nothing.
    private static readonly ConditionalWeakTable<ThemePalette, Shapes> Cache = new();

    static PipeChip()
    {
        AffectsMeasure<PipeChip>(KindProperty);
        AffectsRender<PipeChip>(KindProperty);
        KindProperty.Changed.AddClassHandler<PipeChip>(static (chip, _) => chip.Describe());
    }

    public PipeChip() => Describe();

    public PipeContent Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>The width the chip of <paramref name="content"/> takes; 0 for a raw pipe.</summary>
    public static double MeasureWidth(ThemePalette palette, PipeContent content)
    {
        ArgumentNullException.ThrowIfNull(palette);
        return LetterOf(palette, content) is { } letters ? ChipWidth(letters) : 0;
    }

    /// <summary>Paints the chip of <paramref name="content"/> with its top-left at <paramref name="origin"/>; returns its width (0 for a raw pipe).</summary>
    public static double Draw(DrawingContext context, ThemePalette palette, PipeContent content, Point origin)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(palette);
        if (LetterOf(palette, content) is not { } letters) return 0;
        double width = ChipWidth(letters);
        // The pen sits on the half pixel so the 1 px outline stays crisp.
        Rect box = new(origin.X + 0.5, origin.Y + 0.5, width - 1, FlagChip.ChipHeight - 1);
        context.DrawRectangle(null, palette.Pen(ThemeBrush.TextSecondary, 1), new RoundedRect(box, CornerRadius));
        letters.Draw(context, new Point(origin.X + ((width - letters.Width) / 2), origin.Y + ((FlagChip.ChipHeight - letters.Height) / 2)));
        return width;
    }

    protected override Size MeasureOverride(Size availableSize) => new(MeasureWidth(ThemeBrushes.For(this), Kind), FlagChip.ChipHeight);

    public override void Render(DrawingContext context) => Draw(context, ThemeBrushes.For(this), Kind, default);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty)
        {
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    private void Describe()
    {
        string description = PipeText.Describe(Kind);
        AutomationProperties.SetName(this, description);
        ToolTip.SetTip(this, description);
    }

    private static double ChipWidth(TextLayout letters) => Math.Max(FlagChip.MinChipWidth, Math.Ceiling(letters.Width) + (2 * HorizontalPadding));

    private static TextLayout? LetterOf(ThemePalette palette, PipeContent content) => content switch
    {
        PipeContent.Channel => Cache.GetValue(palette, Create).Channel,
        PipeContent.Collection => Cache.GetValue(palette, Create).Collection,
        _ => null,
    };

    private static Shapes Create(ThemePalette palette)
    {
        Typeface bold = new(palette.Ui, FontStyle.Normal, FontWeight.Bold);
        IBrush ink = palette[ThemeBrush.TextSecondary];
        return new Shapes(new TextLayout("CH", bold, LetterSize, ink), new TextLayout("CO", bold, LetterSize, ink));
    }

    private sealed record Shapes(TextLayout Channel, TextLayout Collection);
}
