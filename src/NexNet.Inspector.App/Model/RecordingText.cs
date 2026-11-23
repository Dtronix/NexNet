using System.Globalization;

namespace NexNet.Inspector.App.Model;

/// <summary>
/// How the open-recording dialog writes a recording's facts: sizes, the recorded span, session start offsets and
/// durations, schema hashes. Invariant culture, so a capture reads the same on every machine.
/// </summary>
public static class RecordingText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>A file size: "512 B", "16.8 KB", "41.2 MB", "1.3 GB" (1024-based).</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1024) return Math.Max(0, bytes).ToString(Invariant) + " B";
        double value = bytes / 1024.0;
        string unit = "KB";
        if (value >= 1024)
        {
            value /= 1024;
            unit = "MB";
        }

        if (value >= 1024)
        {
            value /= 1024;
            unit = "GB";
        }

        return value.ToString("0.0", Invariant) + " " + unit;
    }

    /// <summary>The recorded span (the info strip): "472 ms", "12.4 s", "5 m 00 s", "1 h 05 m".</summary>
    /// <param name="ticks">Stopwatch ticks.</param>
    /// <param name="frequency">Stopwatch ticks per second.</param>
    public static string Span(long ticks, long frequency)
    {
        double seconds = Seconds(ticks, frequency);
        if (seconds < 1) return Math.Round(seconds * 1000).ToString("0", Invariant) + " ms";
        if (seconds < 60) return seconds.ToString("0.0", Invariant) + " s";
        long whole = (long)seconds;
        return whole < 3600
            ? (whole / 60).ToString(Invariant) + " m " + (whole % 60).ToString("00", Invariant) + " s"
            : (whole / 3600).ToString(Invariant) + " h " + (whole / 60 % 60).ToString("00", Invariant) + " m";
    }

    /// <summary>A session's start from the recording's first one (the Start column): "00:41.3", "63:12.9".</summary>
    public static string Offset(long ticks, long frequency)
    {
        long tenths = Tenths(ticks, frequency);
        return (tenths / 600).ToString("00", Invariant) + ":" + (tenths / 10 % 60).ToString("00", Invariant) + "." + (tenths % 10).ToString(Invariant);
    }

    /// <summary>A session's recorded length (the Duration column): "4:18.7", "0:00.4".</summary>
    public static string Length(long ticks, long frequency)
    {
        long tenths = Tenths(ticks, frequency);
        return (tenths / 600).ToString(Invariant) + ":" + (tenths / 10 % 60).ToString("00", Invariant) + "." + (tenths % 10).ToString(Invariant);
    }

    /// <summary>A schema hash as the banner shows it: "0xB144AAE0007D23DF".</summary>
    public static string Hash(ulong hash) => "0x" + hash.ToString("X16", Invariant);

    /// <summary>A count with thousands separators: "18,248".</summary>
    public static string Count(long count) => count.ToString("N0", Invariant);

    /// <summary>"1 gap", "3 sessions": a count and its noun, plural unless one.</summary>
    public static string Plural(long count, string noun) => Count(count) + " " + noun + (count == 1 ? "" : "s");

    private static double Seconds(long ticks, long frequency) => Math.Max(0, ticks) / (double)Math.Max(1, frequency);

    // Rounded to the nearest tenth of a second, so "4:18.7" never shows a 60 in its seconds.
    private static long Tenths(long ticks, long frequency) => (long)Math.Round(Seconds(ticks, frequency) * 10);
}
