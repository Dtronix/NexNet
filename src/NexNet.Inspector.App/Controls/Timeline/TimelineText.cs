using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using NexNet.Inspector.App.Controls.Primitives;

namespace NexNet.Inspector.App.Controls.Timeline;

/// <summary>The text styles the timeline draws (sizes from timeline-lane.html; colours are tokens).</summary>
internal enum TimelineTextStyle : byte
{
    GroupTitle,
    Caret,
    Badge,
    LaneLabel,
    LaneLabelStrong,
    LaneLabelExpanded,
    LaneLabelExpandedStrong,
    SubLabel,
    SubLabelExpanded,
    RowLabel,
    SpanLabel,
    SpanLabelSelected,
    BlockBytes,
    Ruler,
    OverviewRange,
    Hint,
    GlyphInbound,
    GlyphOutbound,
    GlyphNeutral,
    GlyphSession,
    GlyphControl,
    Pin,
}

/// <summary>
/// Shaped labels per palette, style, text and width: the timeline asks for the same few hundred labels every frame, so a
/// frame that shows what the last one showed shapes nothing and allocates nothing. A theme switch (another palette) or
/// too many entries (a long zoom) drops the lot.
/// </summary>
internal sealed class TimelineText
{
    private const int MaxEntries = 4096;

    private readonly Dictionary<Key, TextLayout> _layouts = [];
    private ThemePalette? _palette;

    public int Count => _layouts.Count;

    /// <summary>The shaped <paramref name="text"/>, ellipsised to <paramref name="maxWidth"/>.</summary>
    public TextLayout Get(ThemePalette palette, string text, TimelineTextStyle style, double maxWidth = double.PositiveInfinity)
    {
        if (!ReferenceEquals(palette, _palette))
        {
            Clear();
            _palette = palette;
        }

        int width = double.IsFinite(maxWidth) ? Math.Max(0, (int)maxWidth) : int.MaxValue;
        Key key = new(text, style, width);
        if (_layouts.TryGetValue(key, out TextLayout? layout)) return layout;
        if (_layouts.Count >= MaxEntries) Clear();

        (bool mono, double size, FontWeight weight, ThemeBrush ink, double spacing) = Style(style);
        layout = new TextLayout(text, new Typeface(mono ? palette.Mono : palette.Ui, FontStyle.Normal, weight), size, palette[ink],
            textTrimming: width == int.MaxValue ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            maxWidth: width == int.MaxValue ? double.PositiveInfinity : width, letterSpacing: spacing, maxLines: 1);
        _layouts.Add(key, layout);
        return layout;
    }

    public void Clear()
    {
        foreach (TextLayout layout in _layouts.Values) layout.Dispose();
        _layouts.Clear();
    }

    private static (bool Mono, double Size, FontWeight Weight, ThemeBrush Ink, double Spacing) Style(TimelineTextStyle style) => style switch
    {
        // .g .gl: 9.5 px bold uppercase, letter-spacing .05em, text.secondary.
        TimelineTextStyle.GroupTitle => (false, 9.5, FontWeight.Bold, ThemeBrush.TextSecondary, 9.5 * 0.05),
        TimelineTextStyle.Caret => (false, 9.5, FontWeight.Bold, ThemeBrush.TextSecondary, 0),

        // .bd: mono 8.5 px, weight 500, text.primary on border.default.
        TimelineTextStyle.Badge => (true, 8.5, FontWeight.Medium, ThemeBrush.TextPrimary, 0),

        // .ln .gt: 10 px (10.5 expanded); the entity's name is bold (<b>Pipe 3·1</b>), its sub label tertiary.
        TimelineTextStyle.LaneLabel => (false, 10, FontWeight.Normal, ThemeBrush.TextPrimary, 0),
        TimelineTextStyle.LaneLabelStrong => (false, 10, FontWeight.Bold, ThemeBrush.TextPrimary, 0),
        TimelineTextStyle.LaneLabelExpanded => (false, 10.5, FontWeight.Normal, ThemeBrush.TextPrimary, 0),
        TimelineTextStyle.LaneLabelExpandedStrong => (false, 10.5, FontWeight.Bold, ThemeBrush.TextPrimary, 0),
        TimelineTextStyle.SubLabel => (false, 10, FontWeight.Normal, ThemeBrush.TextTertiary, 0),
        TimelineTextStyle.SubLabelExpanded => (false, 10.5, FontWeight.Normal, ThemeBrush.TextTertiary, 0),
        TimelineTextStyle.RowLabel => (false, 10, FontWeight.Normal, ThemeBrush.TextTertiary, 0),

        // .sp: 8.5 px text.secondary; selected bold on-accent. .big: 8.5 px on-accent.
        TimelineTextStyle.SpanLabel => (false, 8.5, FontWeight.Normal, ThemeBrush.TextSecondary, 0),
        TimelineTextStyle.SpanLabelSelected => (false, 8.5, FontWeight.Bold, ThemeBrush.TextOnAccent, 0),
        TimelineTextStyle.BlockBytes => (false, 8.5, FontWeight.Normal, ThemeBrush.TextOnAccent, 0),

        // Ruler and overview: mono 9.5 px tick labels.
        TimelineTextStyle.Ruler => (true, 9.5, FontWeight.Normal, ThemeBrush.TextTertiary, 0),
        TimelineTextStyle.OverviewRange => (true, 9.5, FontWeight.Normal, ThemeBrush.TextSecondary, 0),

        // .e: 12 px tertiary.
        TimelineTextStyle.Hint => (false, 12, FontWeight.Normal, ThemeBrush.TextTertiary, 0),
        TimelineTextStyle.GlyphInbound => (false, 10, FontWeight.Normal, ThemeBrush.DirectionInbound, 0),
        TimelineTextStyle.GlyphOutbound => (false, 10, FontWeight.Normal, ThemeBrush.DirectionOutbound, 0),
        TimelineTextStyle.GlyphNeutral => (false, 10, FontWeight.Normal, ThemeBrush.TextPrimary, 0),
        TimelineTextStyle.GlyphSession => (false, 10, FontWeight.Normal, ThemeBrush.AccentTrace, 0),
        TimelineTextStyle.GlyphControl => (false, 10, FontWeight.Bold, ThemeBrush.TextSecondary, 0),
        TimelineTextStyle.Pin => (false, 9, FontWeight.Normal, ThemeBrush.AccentTrace, 0),
        _ => (false, 10, FontWeight.Normal, ThemeBrush.TextPrimary, 0),
    };

    private readonly record struct Key(string Text, TimelineTextStyle Style, int Width);
}
