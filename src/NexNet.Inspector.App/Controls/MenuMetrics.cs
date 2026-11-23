namespace NexNet.Inspector.App.Controls;

/// <summary>
/// The menu and tooltip numbers no token names (PKG controls/menu, OQ9), in one place for the themes and their tests.
/// </summary>
public static class MenuMetrics
{
    /// <summary>A context menu's narrowest, so short menus (the rail's) still read as a menu (Foreman's 184 px).</summary>
    public const double MinWidth = 184;

    /// <summary>The app (logo) menu's width.</summary>
    public const double AppWidth = 280;

    /// <summary>How long the pointer rests before a tooltip shows, in ms (OQ9).</summary>
    public const int ToolTipDelay = 400;

    /// <summary>A tooltip wraps rather than running across the window.</summary>
    public const double ToolTipMaxWidth = 320;
}
