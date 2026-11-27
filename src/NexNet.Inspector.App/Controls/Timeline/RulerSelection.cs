using Avalonia;
using Avalonia.Media;
using NexNet.Inspector.App.Controls.Primitives;

namespace NexNet.Inspector.App.Controls.Timeline;

/// <summary>
/// A drag across a ruler, shared by the timeline and Compare's rows: a press is a click until the pointer travels
/// <see cref="DragThreshold"/> pixels, then it selects the range between the press and the pointer, drawn as a band
/// (<see cref="DrawBand"/>) until the release zooms the window to it or Esc cancels it. Pixel state only: the owner maps
/// the band's edges to its own time and window. Allocation-free; one per control.
/// </summary>
public sealed class RulerSelection
{
    /// <summary>How far the pointer travels before a press on the ruler becomes a selection.</summary>
    public const double DragThreshold = 4;

    /// <summary>True from a press until its release or cancel.</summary>
    public bool IsPressed { get; private set; }

    /// <summary>True once the pressed pointer travelled far enough to select (the band shows).</summary>
    public bool IsSelecting { get; private set; }

    /// <summary>Where the press was.</summary>
    public double Anchor { get; private set; }

    /// <summary>Where the pointer is now.</summary>
    public double Current { get; private set; }

    /// <summary>The band's left and right x.</summary>
    public (double Left, double Right) Band => (Math.Min(Anchor, Current), Math.Max(Anchor, Current));

    /// <summary>Starts a press at <paramref name="x"/>.</summary>
    public void Press(double x)
    {
        IsPressed = true;
        IsSelecting = false;
        Anchor = Current = x;
    }

    /// <summary>Follows the pressed pointer to <paramref name="x"/>.</summary>
    /// <returns>True when the band changed (it shows, and its edge moved).</returns>
    public bool Move(double x)
    {
        if (!IsPressed || x == Current) return false;
        Current = x;
        if (!IsSelecting && Math.Abs(x - Anchor) >= DragThreshold) IsSelecting = true;
        return IsSelecting;
    }

    /// <summary>Ends the press.</summary>
    /// <param name="left">The selected range's left x.</param>
    /// <param name="right">The selected range's right x.</param>
    /// <returns>True when the press had become a selection; false for a click.</returns>
    public bool Release(out double left, out double right)
    {
        (left, right) = Band;
        bool selected = IsSelecting;
        Cancel();
        return selected;
    }

    /// <summary>Drops the press and its band (Esc, a lost capture).</summary>
    public void Cancel()
    {
        IsPressed = false;
        IsSelecting = false;
    }

    /// <summary>
    /// The selection band: the trace band's translucent fill (accent.trace-band) with solid accent.trace edges, so it
    /// reads apart from the traced message's dashed band.
    /// </summary>
    public static void DrawBand(DrawingContext context, ThemePalette palette, double left, double right, double top, double bottom)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(palette);
        if (bottom <= top) return;
        left = Math.Round(left);
        right = Math.Max(left + 2, Math.Round(right));
        context.FillRectangle(palette[ThemeBrush.AccentTraceBand], new Rect(left, top, right - left, bottom - top));
        IBrush edge = palette[ThemeBrush.AccentTrace];
        context.FillRectangle(edge, new Rect(left, top, 1, bottom - top));
        context.FillRectangle(edge, new Rect(right - 1, top, 1, bottom - top));
    }
}
