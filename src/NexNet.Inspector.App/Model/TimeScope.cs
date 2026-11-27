using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Model;

/// <summary>The labels of the scopes the app makes (the chip's tooltip names where a scope came from).</summary>
public static class ScopeLabels
{
    /// <summary>"Filter to view", or the dialog's Current view.</summary>
    public const string View = "Current view";

    /// <summary>The dialog's typed start–end.</summary>
    public const string Typed = "Typed range";
}

/// <summary>
/// A document scope (D2, "Filter to time range"): the span of capture time every view is limited to, in Stopwatch ticks
/// of the document's capture, and what it was made from ("Current view", "Invocation #812", "Typed range"). The timeline's
/// overview and ruler span it, the window stays inside it, and Events, Ladder, Hex, the palette, Export and Compare list
/// only what falls in it.
/// </summary>
/// <param name="Start">First tick in scope.</param>
/// <param name="End">The tick the scope ends at (exclusive).</param>
/// <param name="Label">Where the scope came from, for the title bar chip's tooltip.</param>
public readonly record struct TimeScope(long Start, long End, string Label)
{
    /// <summary>The scope as a time window.</summary>
    public TimeWindow Window => new(Start, End);

    /// <summary>True when the scope covers no time (the default value).</summary>
    public bool IsEmpty => End <= Start;

    /// <summary>Ticks covered.</summary>
    public long Duration => End - Start;

    /// <summary>A scope over <paramref name="window"/>.</summary>
    public static TimeScope Of(TimeWindow window, string label) => new(window.Start, window.End, label ?? "");

    /// <summary>True when <paramref name="ticks"/> lies in [Start, End).</summary>
    public bool Contains(long ticks) => ticks >= Start && ticks < End;
}
