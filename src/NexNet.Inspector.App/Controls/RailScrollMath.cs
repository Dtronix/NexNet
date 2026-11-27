namespace NexNet.Inspector.App.Controls;

/// <summary>
/// The rail scrollbar's numbers and arithmetic, in one place for the theme (Themes/ScrollRail.axaml) and the behaviour
/// (<see cref="ScrollRail"/>): a 2 px line at rest that widens to a 12 px rail while the mouse is within 16 px of the edge,
/// 2 px from the edge and 3 px from the ends, an 8 px thumb never under 28 px long, 0.9 × viewport track pages, a Shift+press
/// jump that centres the thumb on the pointer, and the motion (160 ms width and scroll, 120 ms colour, 80 ms when the OS's
/// animations are off, eased by cubic-bezier(.2,0,0,1)).
/// </summary>
public static class RailScrollMath
{
    /// <summary>The bar across at rest: a line.</summary>
    public const double RestWidth = 2;

    /// <summary>The bar across while the pointer is near: the rail.</summary>
    public const double NearWidth = 12;

    /// <summary>The thumb's inset inside the expanded rail (so the thumb is 8 px across).</summary>
    public const double ThumbInset = 2;

    /// <summary>How far from the viewport's edge the pointer expands the rail.</summary>
    public const double ProximityZone = 16;

    /// <summary>The thumb's shortest length.</summary>
    public const double MinThumb = 28;

    /// <summary>The bar's gap to the viewport's edge (right for the vertical bar, bottom for the horizontal one).</summary>
    public const double InsetX = 2;

    /// <summary>The bar's gap to the viewport's two ends.</summary>
    public const double InsetY = 3;

    /// <summary>A track page, of the viewport.</summary>
    public const double PageFraction = 0.9;

    /// <summary>The thumb's length: the viewport's share of the extent of the track, never under <see cref="MinThumb"/> nor over the track.</summary>
    public static double ThumbLength(double viewport, double extent, double track) =>
        Math.Min(track, Math.Max(MinThumb, extent > 0 ? viewport / extent * track : track));

    /// <summary>A page from <paramref name="offset"/> toward <paramref name="direction"/> (−1 or +1), clamped to [0, max].</summary>
    public static double PageTarget(double offset, double viewport, double max, int direction) =>
        Math.Clamp(offset + Math.Sign(direction) * PageFraction * viewport, 0, Math.Max(0, max));

    /// <summary>The offset that centres the thumb on the pointer at <paramref name="along"/> (px along the track), clamped to [0, max].</summary>
    public static double JumpTarget(double along, double track, double thumb, double max)
    {
        if (track <= thumb) return 0;
        return Math.Clamp((along - thumb / 2) / (track - thumb) * max, 0, Math.Max(0, max));
    }

    /// <summary>The offset after the thumb is dragged <paramref name="delta"/> px from where it was at <paramref name="start"/>.</summary>
    public static double DragOffset(double start, double delta, double max, double track, double thumb)
    {
        double top = Math.Max(0, max);
        if (track <= thumb) return Math.Clamp(start, 0, top);
        return Math.Clamp(start + delta * max / (track - thumb), 0, top);
    }

    /// <summary>Whether <paramref name="position"/> (across the bar) is within the last <see cref="ProximityZone"/> px of <paramref name="size"/>.</summary>
    public static bool InZone(double position, double size) => InZone(position, size, ProximityZone);

    /// <summary>Whether <paramref name="position"/> (across the bar) is within the last <paramref name="zone"/> px of <paramref name="size"/>.</summary>
    public static bool InZone(double position, double size, double zone) => position >= size - zone;

    /// <summary>The rail's widening and narrowing.</summary>
    public static TimeSpan WidthDuration(bool showsAnimations) => TimeSpan.FromMilliseconds(showsAnimations ? 160 : 80);

    /// <summary>The rail's and the thumb's colour changes.</summary>
    public static TimeSpan ColourDuration(bool showsAnimations) => TimeSpan.FromMilliseconds(showsAnimations ? 120 : 80);

    /// <summary>A page, jump or menu scroll.</summary>
    public static TimeSpan ScrollDuration(bool showsAnimations) => TimeSpan.FromMilliseconds(showsAnimations ? 160 : 80);

    /// <summary>cubic-bezier(.2,0,0,1) at time <paramref name="t"/> (0–1): the curve's x solved for t by bisection, then its y.</summary>
    public static double EaseOut(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        double lo = 0, hi = 1, s = t;
        for (int i = 0; i < 40; i++)
        {
            s = (lo + hi) / 2;
            if (Bezier(s, 0.2, 0) < t) lo = s;
            else hi = s;
        }

        return Bezier(s, 0, 1);
    }

    /// <summary>One coordinate of a cubic Bézier from 0 to 1 with control points <paramref name="p1"/> and <paramref name="p2"/>.</summary>
    private static double Bezier(double s, double p1, double p2)
    {
        double u = 1 - s;
        return 3 * u * u * s * p1 + 3 * u * s * s * p2 + s * s * s;
    }
}
