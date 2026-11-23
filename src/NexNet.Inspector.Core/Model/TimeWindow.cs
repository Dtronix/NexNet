namespace NexNet.Inspector.Core.Model;

/// <summary>
/// A span of capture time, in Stopwatch ticks of the capturing machine: what the timeline shows and every other view
/// follows. Immutable; each helper returns a new window.
/// </summary>
/// <param name="Start">First tick shown.</param>
/// <param name="End">Tick at the right edge (exclusive).</param>
public readonly record struct TimeWindow(long Start, long End)
{
    /// <summary>The narrowest window any helper produces: one tick.</summary>
    public const long MinDuration = 1;

    /// <summary>Ticks covered.</summary>
    public long Duration => End - Start;

    /// <summary>True when the window covers no time (the default value).</summary>
    public bool IsEmpty => End <= Start;

    /// <summary>The middle tick.</summary>
    public long Center => Start + (Duration / 2);

    /// <summary>A window of <paramref name="duration"/> ticks from <paramref name="start"/>.</summary>
    public static TimeWindow FromDuration(long start, long duration) => new(start, start + Math.Max(MinDuration, duration));

    /// <summary>Converts milliseconds to ticks at a Stopwatch frequency.</summary>
    /// <param name="milliseconds">Milliseconds.</param>
    /// <param name="frequency">Ticks per second.</param>
    public static long TicksFromMilliseconds(double milliseconds, long frequency) => (long)Math.Round(milliseconds * frequency / 1000.0);

    /// <summary>Converts ticks to milliseconds at a Stopwatch frequency.</summary>
    /// <param name="ticks">Ticks.</param>
    /// <param name="frequency">Ticks per second.</param>
    public static double MillisecondsFromTicks(long ticks, long frequency) => ticks * 1000.0 / frequency;

    /// <summary>True when <paramref name="ticks"/> lies in [Start, End).</summary>
    public bool Contains(long ticks) => ticks >= Start && ticks < End;

    /// <summary>True when [<paramref name="start"/>, <paramref name="end"/>] touches the window.</summary>
    public bool Overlaps(long start, long end) => end >= Start && start < End;

    /// <summary>The window moved by <paramref name="delta"/> ticks.</summary>
    public TimeWindow Pan(long delta) => new(Start + delta, End + delta);

    /// <summary>
    /// The window scaled by <paramref name="factor"/> around <paramref name="anchor"/>, which keeps its place on screen
    /// (Ctrl+Wheel zooms around the pointer). A factor below 1 zooms in.
    /// </summary>
    /// <param name="anchor">The tick that stays put.</param>
    /// <param name="factor">New duration over old duration; must be positive.</param>
    public TimeWindow ZoomAround(long anchor, double factor)
    {
        if (!(factor > 0) || double.IsInfinity(factor))
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "The zoom factor must be positive and finite.");

        var duration = Math.Max(MinDuration, (long)Math.Round(Duration * factor));
        var before = (long)Math.Round((anchor - Start) * ((double)duration / Math.Max(MinDuration, Duration)));
        var start = anchor - before;
        return new TimeWindow(start, start + duration);
    }

    /// <summary>
    /// The window kept inside [<paramref name="min"/>, <paramref name="max"/>]: shifted back in when it hangs over an
    /// edge, and narrowed to the bounds when it is wider than they are.
    /// </summary>
    public TimeWindow Clamp(long min, long max)
    {
        if (max - min < MinDuration)
            return FromDuration(min, MinDuration);
        if (Duration >= max - min)
            return new TimeWindow(min, max);
        if (Start < min)
            return new TimeWindow(min, min + Duration);
        if (End > max)
            return new TimeWindow(max - Duration, max);
        return this;
    }

    /// <summary>The window with the same duration ending at <paramref name="latest"/> (following a live capture).</summary>
    public TimeWindow Follow(long latest) => new(latest - Math.Max(MinDuration, Duration), latest);

    /// <summary>Ticks per device-independent pixel at a view width.</summary>
    /// <param name="width">The view's width in pixels.</param>
    public double TicksPerPixel(double width) => width > 0 ? Duration / width : 0;

    /// <summary>The x of a tick in a view <paramref name="width"/> pixels wide (unclamped; outside ticks fall outside).</summary>
    public double ToPixel(long ticks, double width) => Duration <= 0 || width <= 0 ? 0 : (ticks - Start) * width / Duration;

    /// <summary>The tick at x in a view <paramref name="width"/> pixels wide.</summary>
    public long FromPixel(double x, double width) => width <= 0 ? Start : Start + (long)Math.Round(x * Duration / width);

    /// <summary>The ticks a horizontal drag of <paramref name="pixels"/> covers (Wheel pans by it).</summary>
    public long PixelsToTicks(double pixels, double width) => width <= 0 ? 0 : (long)Math.Round(pixels * Duration / width);

    /// <summary>
    /// The same position and zoom relative to another capture's clock: the offset from <paramref name="fromOrigin"/>
    /// and the duration carry over in seconds, so documents recorded on machines with different Stopwatch frequencies
    /// keep the same view when the active tab changes.
    /// </summary>
    public TimeWindow Rebase(long fromOrigin, long fromFrequency, long toOrigin, long toFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fromFrequency, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(toFrequency, 1);
        var scale = (double)toFrequency / fromFrequency;
        var start = toOrigin + (long)Math.Round((Start - fromOrigin) * scale);
        return FromDuration(start, (long)Math.Round(Duration * scale));
    }
}
