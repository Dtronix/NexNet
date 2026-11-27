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

    /// <summary>
    /// The narrowest window a range drag makes (the overview's handles, a ruler selection): 10 µs at
    /// <paramref name="frequency"/>, never under one tick.
    /// </summary>
    /// <param name="frequency">Ticks per second.</param>
    public static long MinSpan(long frequency) => Math.Max(MinDuration, (frequency + 99_999) / 100_000);

    /// <summary>
    /// The window with its start moved to <paramref name="start"/> and its end kept (the overview's left handle): the
    /// start stays at least <paramref name="minDuration"/> before the end and inside <paramref name="bounds"/>.
    /// </summary>
    /// <param name="start">The tick the start is dragged to.</param>
    /// <param name="minDuration">The narrowest window allowed (at least one tick).</param>
    /// <param name="bounds">The range the start stays in; empty for none.</param>
    public TimeWindow WithStart(long start, long minDuration, TimeWindow bounds)
    {
        minDuration = Math.Max(MinDuration, minDuration);
        if (!bounds.IsEmpty && start < bounds.Start) start = bounds.Start;
        if (start > End - minDuration) start = End - minDuration;
        return new TimeWindow(start, End);
    }

    /// <summary>
    /// The window with its end moved to <paramref name="end"/> and its start kept (the overview's right handle): the end
    /// stays at least <paramref name="minDuration"/> after the start and inside <paramref name="bounds"/>.
    /// </summary>
    /// <param name="end">The tick the end is dragged to.</param>
    /// <param name="minDuration">The narrowest window allowed (at least one tick).</param>
    /// <param name="bounds">The range the end stays in; empty for none.</param>
    public TimeWindow WithEnd(long end, long minDuration, TimeWindow bounds)
    {
        minDuration = Math.Max(MinDuration, minDuration);
        if (!bounds.IsEmpty && end > bounds.End) end = bounds.End;
        if (end < Start + minDuration) end = Start + minDuration;
        return new TimeWindow(Start, end);
    }

    /// <summary>
    /// The window moved by <paramref name="delta"/> ticks but kept inside <paramref name="bounds"/> at the same duration
    /// (the overview's middle drag); a window as wide as the bounds or wider does not move.
    /// </summary>
    /// <param name="delta">Ticks to move by.</param>
    /// <param name="bounds">The range the window stays in; empty for none.</param>
    public TimeWindow PanWithin(long delta, TimeWindow bounds)
    {
        if (bounds.IsEmpty) return Pan(delta);
        if (Duration >= bounds.Duration) return this;
        long start = Math.Clamp(Start + delta, bounds.Start, bounds.End - Duration);
        return new TimeWindow(start, start + Duration);
    }

    /// <summary>
    /// The window between two ticks in either order (a ruler selection), at least <paramref name="minDuration"/> wide
    /// (widened around its middle) and kept inside <paramref name="bounds"/>.
    /// </summary>
    /// <param name="a">One end.</param>
    /// <param name="b">The other end.</param>
    /// <param name="minDuration">The narrowest window allowed (at least one tick).</param>
    /// <param name="bounds">The range the window stays in; empty for none.</param>
    public static TimeWindow Between(long a, long b, long minDuration, TimeWindow bounds)
    {
        minDuration = Math.Max(MinDuration, minDuration);
        long start = Math.Min(a, b), end = Math.Max(a, b);
        if (!bounds.IsEmpty)
        {
            start = Math.Clamp(start, bounds.Start, bounds.End);
            end = Math.Clamp(end, bounds.Start, bounds.End);
        }

        if (end - start < minDuration)
        {
            start = start + ((end - start) / 2) - (minDuration / 2);
            end = start + minDuration;
        }

        TimeWindow window = new(start, end);
        return bounds.IsEmpty ? window : window.Clamp(bounds.Start, bounds.End);
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
