namespace NexNet.Inspector.App.Model;

/// <summary>Which panes the Inspect workspace shows at the window's width (OQ10).</summary>
public enum ShellLayoutState : byte
{
    /// <summary>1200 px and wider: both lower panes and the 324 px inspector.</summary>
    Full = 0,

    /// <summary>Below 1200 px: the right lower pane closes first.</summary>
    RightPaneClosed = 1,

    /// <summary>Below 1150 px: the right pane stays closed and the inspector narrows to 280 px.</summary>
    InspectorNarrow = 2,
}

/// <summary>
/// The window's collapse rules (OQ10): a pure function of the width, so the breakpoints are tested without a window. The
/// minimum window (1100 x 700) keeps every state usable; the panes themselves (I23) read the state from the shell.
/// </summary>
public static class ShellLayoutPolicy
{
    /// <summary>The window's minimum width.</summary>
    public const double MinWidth = 1100;

    /// <summary>The window's minimum height.</summary>
    public const double MinHeight = 700;

    /// <summary>Below this width the right lower pane closes.</summary>
    public const double RightPaneBreakpoint = 1200;

    /// <summary>
    /// Below this width the inspector narrows as well. The package says only "then"; halfway between the first
    /// breakpoint and the minimum gives each step 50 px of its own.
    /// </summary>
    public const double InspectorBreakpoint = 1150;

    /// <summary>The inspector's width (size.inspector-width).</summary>
    public const double InspectorWidth = 324;

    /// <summary>The narrowed inspector's width.</summary>
    public const double NarrowInspectorWidth = 280;

    /// <summary>The state at a window width (logical px). A width not yet known (0 or NaN) is the full layout.</summary>
    public static ShellLayoutState For(double width)
    {
        if (!(width > 0) || width >= RightPaneBreakpoint) return ShellLayoutState.Full;
        return width >= InspectorBreakpoint ? ShellLayoutState.RightPaneClosed : ShellLayoutState.InspectorNarrow;
    }

    /// <summary>True when the right lower pane shows.</summary>
    public static bool ShowsRightPane(ShellLayoutState state) => state == ShellLayoutState.Full;

    /// <summary>The inspector's width in a state.</summary>
    public static double InspectorWidthFor(ShellLayoutState state) =>
        state == ShellLayoutState.InspectorNarrow ? NarrowInspectorWidth : InspectorWidth;
}

/// <summary>Which <see cref="ChromeMode"/> a platform gets.</summary>
public static class WindowChromePolicy
{
    /// <summary>Linux draws its own frame; everything else extends into the OS frame.</summary>
    public static ChromeMode For(bool isLinux) => isLinux ? ChromeMode.Undecorated : ChromeMode.Extended;

    /// <summary>The mode of the platform this process runs on.</summary>
    public static ChromeMode Current => For(OperatingSystem.IsLinux());
}
