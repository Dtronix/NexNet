namespace NexNet.Inspector.App.Controls.DataTable;

/// <summary>What a scroll does to following the newest row.</summary>
public enum FollowTailDecision : byte
{
    /// <summary>No change.</summary>
    None = 0,

    /// <summary>The user scrolled up: stop following.</summary>
    Pause = 1,

    /// <summary>The user scrolled back down to the last row: follow again.</summary>
    Resume = 2,
}

/// <summary>
/// Decides from one scroll change whether a table keeps following its newest row ("Follow live"). Only a change of the
/// offset alone counts as the user's: rows arriving (the extent grows) or the pane resizing (the viewport changes) move
/// nothing, and the table's own scroll to the tail only ever moves down.
/// </summary>
public static class FollowTailRule
{
    /// <summary>How close to the end, in pixels, still counts as at the end (layout rounding).</summary>
    public const double EndTolerance = 1;

    /// <summary>Decides what one scroll change does.</summary>
    /// <param name="offsetDelta">How far the vertical offset moved.</param>
    /// <param name="extentDelta">How much the content's height changed.</param>
    /// <param name="viewportDelta">How much the viewport's height changed.</param>
    /// <param name="offset">The vertical offset after the change.</param>
    /// <param name="viewport">The viewport's height.</param>
    /// <param name="extent">The content's height.</param>
    /// <param name="following">Whether the table follows now.</param>
    public static FollowTailDecision Decide(double offsetDelta, double extentDelta, double viewportDelta, double offset, double viewport,
        double extent, bool following)
    {
        if (extentDelta != 0 || viewportDelta != 0 || offsetDelta == 0)
            return FollowTailDecision.None;
        if (offsetDelta < 0)
            return following ? FollowTailDecision.Pause : FollowTailDecision.None;
        return !following && offset + viewport >= extent - EndTolerance ? FollowTailDecision.Resume : FollowTailDecision.None;
    }
}
