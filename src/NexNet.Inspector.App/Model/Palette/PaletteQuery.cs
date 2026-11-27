using System.Globalization;

namespace NexNet.Inspector.App.Model.Palette;

/// <summary>What the palette searches (OQ14): its scope pills, and the time jump of the <c>:</c> prefix.</summary>
public enum PaletteScope : byte
{
    /// <summary>Entities, frames, commands and sessions, grouped.</summary>
    All = 0,

    /// <summary>Commands only (prefix <c>&gt;</c>).</summary>
    Commands = 1,

    /// <summary>Frames by number or type (prefix <c>#</c>).</summary>
    Frames = 2,

    /// <summary>Invocations, pipes, channels and collections.</summary>
    Entities = 3,

    /// <summary>Open documents (prefix <c>@</c>).</summary>
    Sessions = 4,

    /// <summary>A time to jump to (prefix <c>:</c>); not a pill.</summary>
    Time = 5,
}

/// <summary>
/// A parsed palette query: a leading prefix picks the scope (<c>&gt;</c> commands, <c>#</c> frames, <c>@</c> sessions,
/// <c>:</c> time) and is not part of the searched text; without one, the selected pill's scope applies.
/// </summary>
/// <param name="Scope">What to search.</param>
/// <param name="Text">The text to match, prefix and surrounding spaces removed.</param>
/// <param name="HasPrefix">True when the scope came from a prefix.</param>
public readonly record struct PaletteQuery(PaletteScope Scope, string Text, bool HasPrefix)
{
    /// <summary>Parses <paramref name="raw"/>; <paramref name="pill"/> is the scope when there is no prefix.</summary>
    public static PaletteQuery Parse(string? raw, PaletteScope pill)
    {
        string text = (raw ?? "").Trim();
        PaletteScope scope = text.Length > 0 ? ScopeOf(text[0]) : PaletteScope.All;
        return scope != PaletteScope.All
            ? new PaletteQuery(scope, text[1..].Trim(), true)
            : new PaletteQuery(pill, text, false);
    }

    /// <summary>The scope a prefix character selects; <see cref="PaletteScope.All"/> for none.</summary>
    public static PaletteScope ScopeOf(char prefix) => prefix switch
    {
        '>' => PaletteScope.Commands,
        '#' => PaletteScope.Frames,
        '@' => PaletteScope.Sessions,
        ':' => PaletteScope.Time,
        _ => PaletteScope.All,
    };

    /// <summary>The prefix of a scope; none (<c>'\0'</c>) for All and Entities.</summary>
    public static char PrefixOf(PaletteScope scope) => scope switch
    {
        PaletteScope.Commands => '>',
        PaletteScope.Frames => '#',
        PaletteScope.Sessions => '@',
        PaletteScope.Time => ':',
        _ => '\0',
    };

    /// <summary>
    /// Reads a time from the session's start in milliseconds: <c>1262.4</c>, <c>1,262.4</c>, <c>1262.4 ms</c> or
    /// <c>1.2624 s</c>.
    /// </summary>
    public static bool TryParseTime(ReadOnlySpan<char> text, out double milliseconds)
    {
        milliseconds = 0;
        text = text.Trim();
        double scale = 1;
        if (text.EndsWith("ms", StringComparison.OrdinalIgnoreCase)) text = text[..^2];
        else if (text.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^1];
            scale = 1000;
        }

        text = text.TrimEnd();
        if (text.IsEmpty
            || !double.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value))
            return false;
        milliseconds = value * scale;
        return true;
    }
}
