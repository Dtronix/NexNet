using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using NexNet.Inspector.App.Controls.Primitives;

namespace NexNet.Inspector.App.Controls;

/// <summary>What a <see cref="CountBadge"/> counts, which picks its fill.</summary>
public enum CountBadgeTone : byte
{
    /// <summary>A plain count (timeline group headers): border.default fill, primary text.</summary>
    Neutral = 0,

    /// <summary>The Compare differences count: split.nexnet fill, on-accent text.</summary>
    Difference = 1,
}

/// <summary>
/// A count badge (controls/chip): an 11 px pill with 8.5 px mono text, such as "3 rows · 9" on a timeline group header
/// or the differences count in Compare. Drawn; the text is measured when it or the theme changes, not per frame.
/// </summary>
public sealed class CountBadge : Control
{
    /// <summary>The badge's height (chip.md: 11 px).</summary>
    public const double BadgeHeight = 11;

    private const double HorizontalPadding = 4;
    private const double TextSize = 8.5;

    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<CountBadge, string?>(nameof(Text));

    public static readonly StyledProperty<CountBadgeTone> ToneProperty = AvaloniaProperty.Register<CountBadge, CountBadgeTone>(nameof(Tone));

    private FormattedText? _text;
    private ThemePalette? _textPalette;

    static CountBadge()
    {
        AffectsMeasure<CountBadge>(TextProperty, ToneProperty);
        AffectsRender<CountBadge>(TextProperty, ToneProperty);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public CountBadgeTone Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        FormattedText? text = Measured(ThemeBrushes.For(this));
        return new Size(text is null ? 0 : Math.Ceiling(text.Width + (2 * HorizontalPadding)), BadgeHeight);
    }

    public override void Render(DrawingContext context)
    {
        ThemePalette palette = ThemeBrushes.For(this);
        FormattedText? text = Measured(palette);
        if (text is null) return;
        Rect bounds = new(Bounds.Size);
        ThemeBrush fill = Tone == CountBadgeTone.Difference ? ThemeBrush.SplitNexnet : ThemeBrush.BorderDefault;
        context.DrawRectangle(palette[fill], null, new RoundedRect(bounds, BadgeHeight / 2));
        context.DrawText(text, new Point((bounds.Width - text.Width) / 2, (BadgeHeight - text.Height) / 2));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == ToneProperty)
        {
            _text = null;
            if (change.Property == TextProperty) AutomationProperties.SetName(this, Text);
        }
        else if (change.Property == ThemeVariantScope.ActualThemeVariantProperty)
        {
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    private FormattedText? Measured(ThemePalette palette)
    {
        if (string.IsNullOrEmpty(Text)) return null;
        if (_text is not null && ReferenceEquals(_textPalette, palette)) return _text;
        ThemeBrush ink = Tone == CountBadgeTone.Difference ? ThemeBrush.TextOnAccent : ThemeBrush.TextPrimary;
        _textPalette = palette;
        return _text = new FormattedText(Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(palette.Mono), TextSize, palette[ink]);
    }
}
