namespace NexNet.Inspector.App.Services.Commands;

/// <summary>
/// The app's commands: one id each, shared by the command palette and the app menu, so a title, shortcut or enabled
/// state is defined once (<see cref="CommandRegistry"/>). Declaration order is the palette's order for an empty query.
/// </summary>
public enum AppCommand : byte
{
    /// <summary>No command.</summary>
    None = 0,

    /// <summary>Opens the command palette (Ctrl+Shift+P).</summary>
    CommandPalette,

    /// <summary>Opens a recording (Ctrl+O).</summary>
    OpenRecording,

    /// <summary>Saves the active timeline as a recording (Ctrl+S).</summary>
    SaveTimeline,

    /// <summary>Exports the selection as a recording (Ctrl+Shift+S).</summary>
    ExportSelection,

    /// <summary>Opens the Connect dialog (Ctrl+K).</summary>
    Connect,

    /// <summary>Records every session of the attached server (Ctrl+Shift+R).</summary>
    RecordAll,

    /// <summary>Jumps the playhead and window to a time: the palette's <c>:</c> scope (Ctrl+G).</summary>
    GoToTime,

    /// <summary>Traces the message under the pointer (T, in the view under it).</summary>
    TraceMessage,

    /// <summary>Opens the selected entity in the Compare workspace.</summary>
    OpenInCompare,

    /// <summary>Shows one or two lower stream panes.</summary>
    ToggleSplit,

    /// <summary>Activates the next document in most-recently-used order (Ctrl+Tab).</summary>
    NextDocument,

    /// <summary>Activates the previous document (Ctrl+Shift+Tab).</summary>
    PreviousDocument,

    /// <summary>Uses the dark theme.</summary>
    ThemeDark,

    /// <summary>Uses the light theme.</summary>
    ThemeLight,

    /// <summary>Follows the operating system's theme.</summary>
    ThemeSystem,

    /// <summary>Shows help and the shortcut list (F1).</summary>
    HelpShortcuts,
}

/// <summary>The app menu's section a command belongs to (inspect-app-menu: FILE, SESSION, VIEW, then help).</summary>
public enum CommandGroup : byte
{
    /// <summary>Recordings: open, save, export.</summary>
    File = 0,

    /// <summary>The live connection and its sessions.</summary>
    Session = 1,

    /// <summary>Navigation, layout and theme.</summary>
    View = 2,

    /// <summary>Help.</summary>
    Help = 3,
}

/// <summary>The glyph drawn in a command's icon column.</summary>
public enum CommandGlyph : byte
{
    /// <summary>No glyph.</summary>
    None = 0,

    /// <summary>The palette's search lens.</summary>
    Palette,

    /// <summary>A recording to open.</summary>
    Open,

    /// <summary>A recording to write (save, export).</summary>
    Save,

    /// <summary>The connection.</summary>
    Connect,

    /// <summary>Recording.</summary>
    Record,

    /// <summary>A jump in time.</summary>
    GoTo,

    /// <summary>The trace crosshair.</summary>
    Trace,

    /// <summary>Panes side by side (split, compare).</summary>
    Panes,

    /// <summary>Document switching.</summary>
    Documents,

    /// <summary>The theme.</summary>
    Theme,

    /// <summary>Help.</summary>
    Help,
}
