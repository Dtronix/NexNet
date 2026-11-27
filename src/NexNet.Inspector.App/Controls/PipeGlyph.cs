using Avalonia;
using Avalonia.Media;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.Core.Assembly;

namespace NexNet.Inspector.App.Controls;

/// <summary>
/// The pipe-kind glyphs drawn as shapes: ≋ a channel, ⇄ a raw pipe, ☰ a collection. The bundled fonts lack ⇄ and ☰
/// (Atkinson has none of the three), so drawn controls paint them as 10 px strokes, as the title bar draws its caption
/// glyphs, instead of depending on whatever fallback font the platform has. Text-only places write the characters
/// (<see cref="Model.PipeText.Glyph"/>).
/// </summary>
public static class PipeGlyph
{
    /// <summary>The glyph's box: a square this many px wide and tall.</summary>
    public const double Size = 10;

    private const double Stroke = 1.2;

    // Built on first use: geometry needs the platform's render interface, which a type initializer may run before.
    private static Geometry? _channel;
    private static Geometry? _raw;
    private static Geometry? _collection;

    /// <summary>
    /// Paints the glyph of <paramref name="content"/> in <paramref name="ink"/> with its box's top-left at
    /// <paramref name="origin"/>; returns the box's width.
    /// </summary>
    public static double Draw(DrawingContext context, ThemePalette palette, PipeContent content, ThemeBrush ink, Point origin)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(palette);
        Geometry geometry = GeometryOf(content);
        using (context.PushTransform(Matrix.CreateTranslation(origin.X, origin.Y)))
            context.DrawGeometry(null, palette.Pen(ink, Stroke), geometry);
        return Size;
    }

    /// <summary>The glyph of <paramref name="content"/> as a geometry in a <see cref="Size"/> px box (for templated places).</summary>
    public static Geometry GeometryOf(PipeContent content) => content switch
    {
        // Three waves: "≋".
        PipeContent.Channel => _channel ??= Geometry.Parse(
            "M0.5,2.6 C2,1.2 3.5,1.2 5,2.6 C6.5,4 8,4 9.5,2.6 " +
            "M0.5,5.4 C2,4 3.5,4 5,5.4 C6.5,6.8 8,6.8 9.5,5.4 " +
            "M0.5,8.2 C2,6.8 3.5,6.8 5,8.2 C6.5,9.6 8,9.6 9.5,8.2"),
        // Three bars: "☰".
        PipeContent.Collection => _collection ??= Geometry.Parse("M0.5,2 L9.5,2 M0.5,5 L9.5,5 M0.5,8 L9.5,8"),
        // Right over left arrows: "⇄".
        _ => _raw ??= Geometry.Parse("M0.5,3 L9,3 M6.6,0.8 L9,3 L6.6,5.2 M9.5,7 L1,7 M3.4,4.8 L1,7 L3.4,9.2"),
    };
}
