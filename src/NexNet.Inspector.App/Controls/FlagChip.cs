using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.Core.Framing;

namespace NexNet.Inspector.App.Controls;

/// <summary>
/// A frame flag chip (controls/chip): a 12 px coloured tile with the flag's letter in 8.5 px bold on-accent text. One
/// chip shows one <see cref="FrameFlags"/> bit; a row with several flags draws one per set bit in <see cref="DisplayOrder"/>.
/// Drawn rather than templated, and <see cref="Draw"/> is public, so dense rows (events, ladder) can paint chips without
/// a control per chip. Colour is paired with a letter, and the chip's accessible name and tooltip spell the flag out.
/// </summary>
public sealed class FlagChip : Control
{
    /// <summary>The chip's height (chip.md: 12 px).</summary>
    public const double ChipHeight = 12;

    /// <summary>The narrowest chip: chip.html's min-width 12 px plus its 2 px side padding.</summary>
    public const double MinChipWidth = 16;

    private const double HorizontalPadding = 2;
    private const double CornerRadius = 3;
    private const double LetterSize = 8.5;

    /// <summary>The flag this chip shows: a single bit of <see cref="FrameFlags"/>.</summary>
    public static readonly StyledProperty<FrameFlags> FlagProperty =
        AvaloniaProperty.Register<FlagChip, FrameFlags>(nameof(Flag), FrameFlags.TransportSplit);

    private static readonly FrameFlags[] Order = [FrameFlags.TransportSplit, FrameFlags.NexNetSplit, FrameFlags.Cancel, FrameFlags.Redacted, FrameFlags.Incomplete];

    // A letter per flag per palette: the text is measured once per theme, never per frame.
    private static readonly ConditionalWeakTable<ThemePalette, FormattedText?[]> Letters = new();

    static FlagChip()
    {
        AffectsMeasure<FlagChip>(FlagProperty);
        AffectsRender<FlagChip>(FlagProperty);
        FlagProperty.Changed.AddClassHandler<FlagChip>(static (chip, _) => chip.Describe());
    }

    public FlagChip() => Describe();

    public FrameFlags Flag
    {
        get => GetValue(FlagProperty);
        set => SetValue(FlagProperty, value);
    }

    /// <summary>The order a row lists its flags in: T, N, X, R, then … (the package's chip strip).</summary>
    public static ReadOnlySpan<FrameFlags> DisplayOrder => Order;

    /// <summary>The letter a chip shows for <paramref name="flag"/>.</summary>
    public static string Letter(FrameFlags flag) => flag switch
    {
        FrameFlags.TransportSplit => "T",
        FrameFlags.NexNetSplit => "N",
        FrameFlags.Cancel => "X",
        FrameFlags.Redacted => "R",
        FrameFlags.Incomplete => "…",
        _ => throw new ArgumentOutOfRangeException(nameof(flag), flag, "a chip shows exactly one flag"),
    };

    /// <summary>The chip's fill token for <paramref name="flag"/>.</summary>
    public static ThemeBrush Fill(FrameFlags flag) => flag switch
    {
        FrameFlags.TransportSplit => ThemeBrush.SplitTransport,
        FrameFlags.NexNetSplit => ThemeBrush.SplitNexnet,
        FrameFlags.Cancel => ThemeBrush.StatusError,
        FrameFlags.Redacted or FrameFlags.Incomplete => ThemeBrush.TextTertiary,
        _ => throw new ArgumentOutOfRangeException(nameof(flag), flag, "a chip shows exactly one flag"),
    };

    /// <summary>The accessible name and tooltip of <paramref name="flag"/>'s chip.</summary>
    public static string Description(FrameFlags flag) => flag switch
    {
        FrameFlags.TransportSplit => "Transport split: the frame arrived across several socket reads",
        FrameFlags.NexNetSplit => "NexNet split: the message spans several NexNet frames",
        FrameFlags.Cancel => "Cancellation: the frame cancels an invocation or carries a canceled result",
        FrameFlags.Redacted => "Redacted: a secret in the frame was removed before capture",
        FrameFlags.Incomplete => "Partial: the rest of the frame has not arrived",
        _ => throw new ArgumentOutOfRangeException(nameof(flag), flag, "a chip shows exactly one flag"),
    };

    /// <summary>The width <paramref name="flag"/>'s chip takes in <paramref name="palette"/>'s font.</summary>
    public static double MeasureWidth(ThemePalette palette, FrameFlags flag) =>
        Math.Max(MinChipWidth, Math.Ceiling(LetterOf(palette, flag).Width) + (2 * HorizontalPadding));

    /// <summary>Paints <paramref name="flag"/>'s chip with its top-left at <paramref name="origin"/>; returns its width.</summary>
    public static double Draw(DrawingContext context, ThemePalette palette, FrameFlags flag, Point origin)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(palette);
        FormattedText letter = LetterOf(palette, flag);
        double width = Math.Max(MinChipWidth, Math.Ceiling(letter.Width) + (2 * HorizontalPadding));
        context.DrawRectangle(palette[Fill(flag)], null, new RoundedRect(new Rect(origin, new Size(width, ChipHeight)), CornerRadius));
        context.DrawText(letter, new Point(origin.X + ((width - letter.Width) / 2), origin.Y + ((ChipHeight - letter.Height) / 2)));
        return width;
    }

    protected override Size MeasureOverride(Size availableSize) => new(MeasureWidth(ThemeBrushes.For(this), Flag), ChipHeight);

    public override void Render(DrawingContext context) => Draw(context, ThemeBrushes.For(this), Flag, default);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // The palette (fill and letter colours, font) follows the theme variant.
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty)
        {
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    private void Describe()
    {
        string description = Description(Flag);
        AutomationProperties.SetName(this, description);
        ToolTip.SetTip(this, description);
    }

    private static FormattedText LetterOf(ThemePalette palette, FrameFlags flag)
    {
        FormattedText?[] letters = Letters.GetValue(palette, static _ => new FormattedText?[Order.Length]);
        int slot = Array.IndexOf(Order, flag);
        if (slot < 0) throw new ArgumentOutOfRangeException(nameof(flag), flag, "a chip shows exactly one flag");
        return letters[slot] ??= new FormattedText(Letter(flag), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(palette.Ui, FontStyle.Normal, FontWeight.Bold), LetterSize, palette[ThemeBrush.TextOnAccent]);
    }
}
