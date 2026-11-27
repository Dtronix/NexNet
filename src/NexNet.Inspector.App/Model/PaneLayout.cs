namespace NexNet.Inspector.App.Model;

/// <summary>
/// The sizes the user dragged the workspace's splitters to: the timeline's height, the left stream pane's share of the
/// lower panes, the inspector's width, Compare's sidebar width and its bottom detail panel's height. Kept in
/// <c>settings.json</c> (see <see cref="Settings.AppSettings"/>: setters so a value the file leaves out keeps its default).
/// </summary>
public sealed record PaneSizes
{
    /// <summary>The timeline's height; null: its lanes' own height, up to half the workspace (the default).</summary>
    public double? TimelineHeight { get; set; }

    /// <summary>The left stream pane's share of the two lower panes' width (equal widths by default).</summary>
    public double LeftShare { get; set; } = PaneLayoutPolicy.DefaultLeftShare;

    /// <summary>The inspector's width; null: size.inspector-width (324).</summary>
    public double? InspectorWidth { get; set; }

    /// <summary>Compare's entity sidebar width.</summary>
    public double CompareSidebarWidth { get; set; } = PaneLayoutPolicy.DefaultCompareSidebarWidth;

    /// <summary>Compare's bottom detail panel height (field diff and A ⇄ B hex).</summary>
    public double CompareDetailHeight { get; set; } = PaneLayoutPolicy.DefaultCompareDetailHeight;

    /// <summary>Sizes made safe to use: a hand-edited or corrupt value (NaN, negative, out of range) is clamped or reset.</summary>
    public PaneSizes Normalized() => this with
    {
        TimelineHeight = TimelineHeight is { } h && double.IsFinite(h) ? Math.Max(h, PaneLayoutPolicy.MinTimelineHeight) : null,
        LeftShare = PaneLayoutPolicy.ClampShare(LeftShare),
        InspectorWidth = InspectorWidth is { } w && double.IsFinite(w) ? PaneLayoutPolicy.ClampInspector(w, double.PositiveInfinity) : null,
        CompareSidebarWidth = double.IsFinite(CompareSidebarWidth)
            ? Math.Clamp(CompareSidebarWidth, PaneLayoutPolicy.MinCompareSidebarWidth, PaneLayoutPolicy.MaxCompareSidebarWidth)
            : PaneLayoutPolicy.DefaultCompareSidebarWidth,
        CompareDetailHeight = double.IsFinite(CompareDetailHeight)
            ? Math.Clamp(CompareDetailHeight, PaneLayoutPolicy.MinCompareDetailHeight, PaneLayoutPolicy.MaxCompareDetailHeight)
            : PaneLayoutPolicy.DefaultCompareDetailHeight,
    };
}

/// <summary>
/// The splitters' bounds and how a dragged size combines with the window's collapse rules (OQ10,
/// <see cref="ShellLayoutPolicy"/>): the collapse policy decides what shows, the user's sizes how big it is. Below 1200 px
/// the right stream pane closes whatever its share (the share applies again when it reopens); below 1150 px the inspector
/// is at most the narrowed 280 px, so a wider dragged width waits until the window is wide enough again (a drag there
/// stops at 280). Every drag is clamped so the panes it squeezes keep their minimums, and a double-click on a splitter
/// gives its pane back its default size.
/// </summary>
public static class PaneLayoutPolicy
{
    /// <summary>A splitter's hit area: 7 px centred on its 1 px line.</summary>
    public const double SplitterHitSize = 7;

    /// <summary>The smallest timeline (its ruler and one lane).</summary>
    public const double MinTimelineHeight = 48;

    /// <summary>The least height the lower panes keep under the timeline.</summary>
    public const double MinPanesHeight = 160;

    /// <summary>The narrowest stream pane.</summary>
    public const double MinStreamPaneWidth = 220;

    /// <summary>The narrowest inspector.</summary>
    public const double MinInspectorWidth = 220;

    /// <summary>The widest inspector.</summary>
    public const double MaxInspectorWidth = 720;

    /// <summary>Equal lower panes.</summary>
    public const double DefaultLeftShare = 0.5;

    /// <summary>The left pane's least and greatest share.</summary>
    public const double MinLeftShare = 0.15;

    /// <inheritdoc cref="MinLeftShare"/>
    public const double MaxLeftShare = 0.85;

    /// <summary>Compare's sidebar (232 px in the package).</summary>
    public const double DefaultCompareSidebarWidth = 232;

    /// <summary>Compare's sidebar bounds.</summary>
    public const double MinCompareSidebarWidth = 160;

    /// <inheritdoc cref="MinCompareSidebarWidth"/>
    public const double MaxCompareSidebarWidth = 520;

    /// <summary>Compare's detail panel (200 px in the package).</summary>
    public const double DefaultCompareDetailHeight = 200;

    /// <summary>Compare's detail panel bounds.</summary>
    public const double MinCompareDetailHeight = 96;

    /// <inheritdoc cref="MinCompareDetailHeight"/>
    public const double MaxCompareDetailHeight = 600;

    /// <summary>The inspector's width in a collapse state, given the user's dragged width (null: the default).</summary>
    public static double InspectorWidthFor(ShellLayoutState state, double? userWidth)
    {
        double width = userWidth ?? ShellLayoutPolicy.InspectorWidth;
        return state == ShellLayoutState.InspectorNarrow ? Math.Min(width, ShellLayoutPolicy.NarrowInspectorWidth) : width;
    }

    /// <summary>A timeline height that leaves the lower panes <see cref="MinPanesHeight"/> of a workspace this tall.</summary>
    public static double ClampTimeline(double height, double workspaceHeight)
    {
        double max = Math.Max(MinTimelineHeight, Known(workspaceHeight) - MinPanesHeight);
        return Math.Clamp(double.IsFinite(height) ? height : MinTimelineHeight, MinTimelineHeight, max);
    }

    /// <summary>An inspector width within its bounds that leaves one stream pane its minimum in a workspace this wide.</summary>
    public static double ClampInspector(double width, double workspaceWidth)
    {
        double max = Math.Min(MaxInspectorWidth, Math.Max(MinInspectorWidth, Known(workspaceWidth) - MinStreamPaneWidth));
        return Math.Clamp(double.IsFinite(width) ? width : ShellLayoutPolicy.InspectorWidth, MinInspectorWidth, max);
    }

    /// <summary>A left-pane share within its bounds.</summary>
    public static double ClampShare(double share) =>
        double.IsFinite(share) ? Math.Clamp(share, MinLeftShare, MaxLeftShare) : DefaultLeftShare;

    /// <summary>The left pane's share when its width is dragged to <paramref name="leftWidth"/> of <paramref name="panesWidth"/>.</summary>
    public static double ShareFor(double leftWidth, double panesWidth)
    {
        if (!(panesWidth > 0)) return DefaultLeftShare;
        double min = Math.Max(MinLeftShare, Math.Min(0.5, MinStreamPaneWidth / panesWidth));
        return Math.Clamp(leftWidth / panesWidth, min, 1 - min);
    }

    /// <summary>A Compare detail height that leaves the aligned rows <see cref="MinPanesHeight"/>.</summary>
    public static double ClampCompareDetail(double height, double workspaceHeight) =>
        Math.Clamp(height, MinCompareDetailHeight,
            Math.Max(MinCompareDetailHeight, Math.Min(MaxCompareDetailHeight, Known(workspaceHeight) - MinPanesHeight)));

    /// <summary>A Compare sidebar width that leaves the aligned rows room.</summary>
    public static double ClampCompareSidebar(double width, double workspaceWidth) =>
        Math.Clamp(width, MinCompareSidebarWidth, Math.Max(MinCompareSidebarWidth, Math.Min(MaxCompareSidebarWidth, Known(workspaceWidth) - 400)));

    // A size not laid out yet (0) bounds nothing.
    private static double Known(double size) => size > 0 ? size : double.PositiveInfinity;
}
