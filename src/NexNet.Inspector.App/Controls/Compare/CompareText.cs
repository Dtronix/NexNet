using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using NexNet.Inspector.App.Controls.Primitives;

namespace NexNet.Inspector.App.Controls.Compare;

/// <summary>The text styles the aligned view draws (sizes from SYSTEM.md's compare section; colours are tokens).</summary>
internal enum CompareTextStyle : byte
{
    RowLabel,
    RowLabelStrong,
    PinnedLabel,
    Count,
    Caret,
    Ruler,
    RulerCaption,
    DeltaTitle,
    DeltaBadge,
    FileName,
    FileDetail,
    Hint,
}

/// <summary>
/// Shaped labels per palette, style, text and width, as the timeline keeps them: a redraw of the same rows shapes
/// nothing and allocates nothing. A theme switch or too many entries (a long zoom) drops the lot.
/// </summary>
internal sealed class CompareTextCache
{
    private const int MaxEntries = 2048;

    private readonly Dictionary<Key, TextLayout> _layouts = [];
    private ThemePalette? _palette;

    public int Count => _layouts.Count;

    /// <summary>The shaped <paramref name="text"/>, ellipsised to <paramref name="maxWidth"/>.</summary>
    public TextLayout Get(ThemePalette palette, string text, CompareTextStyle style, double maxWidth = double.PositiveInfinity)
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

    /// <summary>Draws every kept layout once (tests: Skia's own cost of this frame's labels).</summary>
    public void DrawAll(DrawingContext context)
    {
        foreach (TextLayout layout in _layouts.Values) layout.Draw(context, default);
    }

    public void Clear()
    {
        foreach (TextLayout layout in _layouts.Values) layout.Dispose();
        _layouts.Clear();
    }

    private static (bool Mono, double Size, FontWeight Weight, ThemeBrush Ink, double Spacing) Style(CompareTextStyle style) => style switch
    {
        // The row gutter: 11 px text, the count in mono; an expanded category's name bold.
        CompareTextStyle.RowLabel => (false, 11, FontWeight.Normal, ThemeBrush.TextPrimary, 0),
        CompareTextStyle.RowLabelStrong => (false, 11, FontWeight.SemiBold, ThemeBrush.TextPrimary, 0),
        CompareTextStyle.PinnedLabel => (false, 11, FontWeight.SemiBold, ThemeBrush.AccentTrace, 0),
        CompareTextStyle.Count => (true, 10.5, FontWeight.Normal, ThemeBrush.TextSecondary, 0),
        CompareTextStyle.Caret => (false, 9.5, FontWeight.Bold, ThemeBrush.TextSecondary, 0),
        CompareTextStyle.Ruler => (true, 10.5, FontWeight.Normal, ThemeBrush.TextSecondary, 0),
        CompareTextStyle.RulerCaption => (false, 11, FontWeight.Normal, ThemeBrush.TextSecondary, 0),

        // "Δ DIFFERENCES": 9.5 px bold caps in split.nexnet; the count badge mono on-accent.
        CompareTextStyle.DeltaTitle => (false, 9.5, FontWeight.Bold, ThemeBrush.SplitNexnet, 9.5 * 0.05),
        CompareTextStyle.DeltaBadge => (true, 8.5, FontWeight.Medium, ThemeBrush.TextOnAccent, 0),

        // A file header: the name 11.5 px bold, then side · recorded · offset secondary.
        CompareTextStyle.FileName => (false, 11.5, FontWeight.Bold, ThemeBrush.TextPrimary, 0),
        CompareTextStyle.FileDetail => (false, 11.5, FontWeight.Normal, ThemeBrush.TextSecondary, 0),
        CompareTextStyle.Hint => (false, 12, FontWeight.Normal, ThemeBrush.TextTertiary, 0),
        _ => (false, 11, FontWeight.Normal, ThemeBrush.TextPrimary, 0),
    };

    private readonly record struct Key(string Text, CompareTextStyle Style, int Width);
}

/// <summary>Number and time formats of the Compare workspace.</summary>
public static class CompareFormat
{
    /// <summary>An entity count in full: "2,418".</summary>
    public static string Count(long count) => count.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A frame count, compact from a thousand: "318", "1.2k", "9.9k", "12k", "1.4M".</summary>
    public static string Compact(long count) => count switch
    {
        < 1_000 => count.ToString(CultureInfo.InvariantCulture),
        < 10_000 => (count / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        < 1_000_000 => (count / 1000).ToString(CultureInfo.InvariantCulture) + "k",
        _ => (count / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M",
    };

    /// <summary>A duration from milliseconds: "15.3 ms", "820 µs", "2.41 s".</summary>
    public static string Duration(double milliseconds) => Math.Abs(milliseconds) switch
    {
        < 1 => (milliseconds * 1000).ToString("0", CultureInfo.InvariantCulture) + " µs",
        < 100 => milliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms",
        < 1000 => milliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms",
        _ => (milliseconds / 1000).ToString("0.##", CultureInfo.InvariantCulture) + " s",
    };

    /// <summary>A file offset from milliseconds, signed with a true minus: "0", "−0.412 ms", "+3.1 s".</summary>
    public static string Offset(double milliseconds)
    {
        if (Math.Abs(milliseconds) < 0.0005) return "0";
        string sign = milliseconds < 0 ? "\u2212" : "+";
        double size = Math.Abs(milliseconds);
        return sign + (size < 1000
            ? size.ToString("0.###", CultureInfo.InvariantCulture) + " ms"
            : (size / 1000).ToString("0.###", CultureInfo.InvariantCulture) + " s");
    }

    /// <summary>A span of compare time: "42.1 s" (milliseconds below one second).</summary>
    public static string Span(long timeSpanTicks)
    {
        double ms = timeSpanTicks / (double)TimeSpan.TicksPerMillisecond;
        return ms < 1000 ? ms.ToString("0.#", CultureInfo.InvariantCulture) + " ms" : (ms / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " s";
    }
}
