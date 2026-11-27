using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;

namespace NexNet.Inspector.App.Services.Commands;

/// <summary>
/// Every <see cref="AppCommand"/> with its title, shortcut and handler. The palette and the app menu read the same
/// definitions and run commands through here, so a command is enabled in one exactly when it is in the other.
/// </summary>
/// <remarks>
/// The owner of a command's behaviour binds it (<see cref="Bind"/>); a command nobody has bound yet is listed but
/// disabled, so a work order can add its handler without touching the palette or the menu. Call
/// <see cref="Refresh"/> when what a <c>canExecute</c> reads has changed. Use from the UI thread.
/// </remarks>
public sealed class CommandRegistry
{
    private static readonly CommandDefinition[] Table =
    [
        new(AppCommand.CommandPalette, "Command palette", CommandGroup.View, CommandGlyph.Palette, Gesture(Key.P, KeyModifiers.Control | KeyModifiers.Shift)),
        new(AppCommand.OpenRecording, "Open recording…", CommandGroup.File, CommandGlyph.Open, Gesture(Key.O, KeyModifiers.Control)),
        new(AppCommand.SaveTimeline, "Save timeline as .nntl…", CommandGroup.File, CommandGlyph.Save, Gesture(Key.S, KeyModifiers.Control)),
        new(AppCommand.ExportSelection, "Export section as .nntl…", CommandGroup.File, CommandGlyph.Save,
            Gesture(Key.S, KeyModifiers.Control | KeyModifiers.Shift)),
        new(AppCommand.Connect, "Connect to inspector…", CommandGroup.Session, CommandGlyph.Connect, Gesture(Key.K, KeyModifiers.Control)),
        new(AppCommand.RecordAll, "Record all sessions", CommandGroup.Session, CommandGlyph.Record, Gesture(Key.R, KeyModifiers.Control | KeyModifiers.Shift)),
        new(AppCommand.GoToTime, "Go to time…", CommandGroup.View, CommandGlyph.GoTo, Gesture(Key.G, KeyModifiers.Control), "e.g. :1262.4"),
        new(AppCommand.TraceMessage, "Trace message under cursor", CommandGroup.View, CommandGlyph.Trace, Gesture(Key.T, KeyModifiers.None)),
        new(AppCommand.OpenInCompare, "Open in Compare", CommandGroup.View, CommandGlyph.Panes),
        new(AppCommand.ToggleSplit, "Split stream panes", CommandGroup.View, CommandGlyph.Panes),
        new(AppCommand.NextDocument, "Next document", CommandGroup.View, CommandGlyph.Documents, Gesture(Key.Tab, KeyModifiers.Control)),
        new(AppCommand.PreviousDocument, "Previous document", CommandGroup.View, CommandGlyph.Documents, Gesture(Key.Tab, KeyModifiers.Control | KeyModifiers.Shift)),
        new(AppCommand.ThemeDark, "Theme: Dark", CommandGroup.View, CommandGlyph.Theme),
        new(AppCommand.ThemeLight, "Theme: Light", CommandGroup.View, CommandGlyph.Theme),
        new(AppCommand.ThemeSystem, "Theme: System", CommandGroup.View, CommandGlyph.Theme),
        new(AppCommand.HelpShortcuts, "Help & shortcuts", CommandGroup.Help, CommandGlyph.Help, Gesture(Key.F1, KeyModifiers.None)),
        new(AppCommand.Legend, "Legend & symbols", CommandGroup.View, CommandGlyph.Help, Gesture(Key.F2, KeyModifiers.None)),
        new(AppCommand.Detach, "Detach from session", CommandGroup.Session, CommandGlyph.Connect),
        new(AppCommand.FilterToRange, "Filter to time range…", CommandGroup.View, CommandGlyph.Scope,
            Gesture(Key.F, KeyModifiers.Control | KeyModifiers.Shift), "scope every view"),
        new(AppCommand.FilterToView, "Filter to view", CommandGroup.View, CommandGlyph.Scope),
        new(AppCommand.ScopeBack, "Back to previous scope", CommandGroup.View, CommandGlyph.Scope, Gesture(Key.Left, KeyModifiers.Alt)),
        new(AppCommand.ClearScope, "Clear scope", CommandGroup.View, CommandGlyph.Scope),
    ];

    // The commands the palette offers when a query matches no command title (the package's "812" list), in its order.
    private static readonly AppCommand[] SuggestionOrder =
        [AppCommand.TraceMessage, AppCommand.GoToTime, AppCommand.OpenInCompare, AppCommand.ExportSelection];

    // Indexed by the command's value; Table's order is the enum's, which the constructor checks.
    private readonly Action?[] _execute = new Action?[Table.Length + 1];
    private readonly Func<bool>?[] _canExecute = new Func<bool>?[Table.Length + 1];
    private readonly RelayCommand?[] _commands = new RelayCommand?[Table.Length + 1];

    public CommandRegistry()
    {
        for (int i = 0; i < Table.Length; i++)
        {
            if ((int)Table[i].Command != i + 1) throw new InvalidOperationException($"{Table[i].Command} is out of order in the command table");
        }
    }

    /// <summary>Raised after <see cref="Bind"/> or <see cref="Refresh"/>: enabled states may have changed.</summary>
    public event Action? Changed;

    /// <summary>Every command, in <see cref="AppCommand"/> order.</summary>
    public static IReadOnlyList<CommandDefinition> Definitions => Table;

    /// <summary>What the palette lists under COMMANDS when the query matches no command's title.</summary>
    public static IReadOnlyList<AppCommand> Suggestions => SuggestionOrder;

    /// <summary>The definition of <paramref name="command"/>.</summary>
    public static CommandDefinition Get(AppCommand command) =>
        command != AppCommand.None && (int)command <= Table.Length
            ? Table[(int)command - 1]
            : throw new ArgumentOutOfRangeException(nameof(command), command, "not a command");

    /// <summary>
    /// Gives <paramref name="command"/> its behaviour, replacing any earlier one.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="execute">What it does.</param>
    /// <param name="canExecute">Whether it can run now; null: always.</param>
    public void Bind(AppCommand command, Action execute, Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        int i = (int)Get(command).Command;
        _execute[i] = execute;
        _canExecute[i] = canExecute;
        Refresh();
    }

    /// <summary>True when the command has a handler.</summary>
    public bool IsBound(AppCommand command) => _execute[(int)Get(command).Command] is not null;

    /// <summary>True when the command is bound and can run now.</summary>
    public bool CanExecute(AppCommand command)
    {
        int i = (int)Get(command).Command;
        return _execute[i] is not null && (_canExecute[i]?.Invoke() ?? true);
    }

    /// <summary>Runs the command when it can run.</summary>
    /// <returns>True when it ran.</returns>
    public bool Execute(AppCommand command)
    {
        if (!CanExecute(command)) return false;
        _execute[(int)command]!.Invoke();
        return true;
    }

    /// <summary>The command as an <see cref="System.Windows.Input.ICommand"/> for a menu item or key binding (one instance per command).</summary>
    public IRelayCommand CommandFor(AppCommand command)
    {
        int i = (int)Get(command).Command;
        return _commands[i] ??= new RelayCommand(() => Execute(command), () => CanExecute(command));
    }

    /// <summary>Re-reads every enabled state: tells the bound ICommands and <see cref="Changed"/>'s listeners.</summary>
    public void Refresh()
    {
        foreach (RelayCommand? command in _commands) command?.NotifyCanExecuteChanged();
        Changed?.Invoke();
    }

    private static KeyGesture Gesture(Key key, KeyModifiers modifiers) => new(key, modifiers);
}
