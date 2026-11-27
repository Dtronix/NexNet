using System.Text;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.Core.Text;

/// <summary>
/// Which records a rendering keeps. Part of a cursor's identity: a cursor resumes only a rendering with the same filter
/// (see <see cref="Fingerprint"/>). Extend it with new properties and fold each into <see cref="Fingerprint"/>.
/// </summary>
public sealed record TextFilter
{
    /// <summary>Everything.</summary>
    public static TextFilter All { get; } = new();

    /// <summary>
    /// Keeps records that start inside this window, in the session's Stopwatch ticks (see
    /// <see cref="CaptureSession.StartTicks"/>); null keeps all. Header lines are always kept.
    /// </summary>
    public TimeWindow? Window { get; init; }

    /// <summary>Directions kept (gaps follow their direction); undirected records (header, handshake, end) are always kept.</summary>
    public TextDirections Directions { get; init; } = TextDirections.Both;

    /// <summary>
    /// An extra test on every record before it is collapsed (method globs, greps, "errors only": the CLI's filters).
    /// Give <see cref="MatchKey"/> too, so cursors tell renderings with different tests apart.
    /// </summary>
    public Predicate<TextRecord>? Match { get; init; }

    /// <summary>A canonical description of <see cref="Match"/> (for example its command-line text) for the fingerprint.</summary>
    public string? MatchKey { get; init; }

    /// <summary>A window from milliseconds since the session's start.</summary>
    /// <param name="session">The session.</param>
    /// <param name="fromMs">Start, in ms since <see cref="CaptureSession.StartTicks"/>.</param>
    /// <param name="toMs">End (exclusive), in ms since the start.</param>
    public static TimeWindow WindowFromMilliseconds(CaptureSession session, double fromMs, double toMs)
    {
        ArgumentNullException.ThrowIfNull(session);
        var frequency = session.Header.StopwatchFrequency;
        return new TimeWindow(session.StartTicks + TimeWindow.TicksFromMilliseconds(fromMs, frequency),
            session.StartTicks + TimeWindow.TicksFromMilliseconds(toMs, frequency));
    }

    /// <summary>A stable 64-bit hash of the filter, carried by cursors.</summary>
    public ulong Fingerprint()
    {
        var hash = 0xcbf29ce484222325UL;
        hash = Mix(hash, Window is { } window ? 1 : 0);
        if (Window is { } w)
        {
            hash = Mix(hash, w.Start);
            hash = Mix(hash, w.End);
        }

        hash = Mix(hash, (long)Directions);
        if (MatchKey is { } key)
        {
            foreach (var c in key)
                hash = Mix(hash, c);
        }
        else if (Match is not null)
        {
            hash = Mix(hash, -1);
        }

        return hash;
    }

    private static ulong Mix(ulong hash, long value)
    {
        for (var i = 0; i < 8; i++)
        {
            hash ^= (byte)(value >> (i * 8));
            hash *= 0x100000001b3UL;
        }

        return hash;
    }
}

/// <summary>Options of a <see cref="TextRenderer"/>.</summary>
public sealed record TextOptions
{
    /// <summary>The defaults: messages level, text, collapsing, values 32 characters wide, depth 2, no budget.</summary>
    public static TextOptions Default { get; } = new();

    /// <summary>How much to say.</summary>
    public TextLevel Level { get; init; } = TextLevel.Messages;

    /// <summary>Text or ndjson.</summary>
    public TextFormat Format { get; init; } = TextFormat.Text;

    /// <summary>Collapse pings, credits, window updates, item runs and repeated identical lines into `×N`.</summary>
    public bool Collapse { get; init; } = true;

    /// <summary>Longest text of one value (strings, numbers, names) before it is cut with `…`; at least 4.</summary>
    public int ValueWidth { get; init; } = 32;

    /// <summary>Nesting shown inside a value; deeper objects show as `{…}`, deeper sequences as `[n]`.</summary>
    public int Depth { get; init; } = 2;

    /// <summary>Bytes shown per frame at <see cref="TextLevel.Bytes"/>.</summary>
    public int ByteLimit { get; init; } = 32;

    /// <summary>Most lines one <see cref="TextRenderer.Render"/> writes; 0 for no limit.</summary>
    public int MaxLines { get; init; }

    /// <summary>Most approximate tokens (see <see cref="TextBudget"/>) one render writes; 0 for no limit.</summary>
    public long MaxTokens { get; init; }

    /// <summary>Which records to keep.</summary>
    public TextFilter Filter { get; init; } = TextFilter.All;

    /// <summary>Start after this cursor (from a previous render's budget line) instead of at the session's start.</summary>
    public TextCursor? After { get; init; }

    /// <summary>Write the header line when starting at the session's start.</summary>
    public bool Header { get; init; } = true;
}

/// <summary>The token estimate behind <see cref="TextOptions.MaxTokens"/>.</summary>
public static class TextBudget
{
    /// <summary>
    /// Approximate tokens of a line: characters (plus the newline) over 4, rounded up, counting a character outside
    /// ASCII (the glyphs ≋ ☰ ⇄ · × … and any non-English text) as 3. Deliberately pessimistic, so a budget errs low.
    /// </summary>
    /// <param name="line">The line, without its newline.</param>
    public static long Estimate(ReadOnlySpan<char> line)
    {
        long weight = 1;
        foreach (var c in line)
            weight += c < 0x80 ? 1 : 3;
        return (weight + 3) / 4;
    }

    internal static long Estimate(StringBuilder line)
    {
        long weight = 1;
        foreach (var chunk in line.GetChunks())
        {
            foreach (var c in chunk.Span)
                weight += c < 0x80 ? 1 : 3;
        }

        return (weight + 3) / 4;
    }
}
