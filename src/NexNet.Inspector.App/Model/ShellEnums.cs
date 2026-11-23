namespace NexNet.Inspector.App.Model;

/// <summary>The two workspaces the title bar switches between.</summary>
public enum Workspace : byte
{
    /// <summary>One document at a time: timeline, lower panes and the inspector.</summary>
    Inspect = 0,

    /// <summary>Two documents aligned side by side (OQ11: exactly two).</summary>
    Compare = 1,
}

/// <summary>The leading marker of a document tab.</summary>
public enum TabMarker : byte
{
    /// <summary>A live session: a recording-red circle.</summary>
    Live = 0,

    /// <summary>A file: an inbound-blue square.</summary>
    FileInbound = 1,

    /// <summary>A file: an outbound-orange square.</summary>
    FileOutbound = 2,
}

/// <summary>What the title bar asks the shell to do; the shell's later work orders handle each.</summary>
public enum ShellRequest : byte
{
    /// <summary>The logo button: open the application menu (I28).</summary>
    AppMenu = 0,

    /// <summary>"Not connected · Connect…": open the Connect dialog (I17).</summary>
    Connect = 1,

    /// <summary>The tab strip's +: open a recording (I17/I24).</summary>
    Open = 2,
}

/// <summary>How the window draws its frame on this platform.</summary>
public enum ChromeMode : byte
{
    /// <summary>
    /// The OS frame stays (border, shadow, snap) and the client area extends under the title bar: the bar's elements
    /// carry hit-test roles, so the OS moves, maximizes and snaps the window. Windows and macOS.
    /// </summary>
    Extended = 0,

    /// <summary>
    /// No OS decorations at all: the bar drags and maximizes the window itself, and edge grips resize it. Linux, where
    /// window managers disagree on extended client areas.
    /// </summary>
    Undecorated = 1,
}
