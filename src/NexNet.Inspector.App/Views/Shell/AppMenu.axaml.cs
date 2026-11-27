using Avalonia.Controls;
using NexNet.Inspector.App.Services.Commands;
using NexNet.Inspector.App.ViewModels;

namespace NexNet.Inspector.App.Views.Shell;

/// <summary>
/// The application menu's items (AppMenu.axaml). It is not opened as a context menu: the window hosts it in a popup under
/// the logo, so it renders in the window's overlay layer like any part of the window. When the menu's own keyboard and
/// pointer handling closes it (an item ran, Esc at the top level) it asks that popup to close (<see cref="CloseRequested"/>);
/// the embedded Theme row and submenu parents keep it open. Its command items read their title, shortcut and command
/// from the window's <see cref="CommandRegistry"/>, so the menu and the palette agree on each.
/// </summary>
public sealed partial class AppMenu : ContextMenu
{
    public AppMenu()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindCommands((DataContext as MainWindowViewModel)?.Commands);
    }

    /// <summary>Raised when the menu is done: an item ran, or Esc was pressed with no submenu open.</summary>
    public event EventHandler? CloseRequested;

    protected override Type StyleKeyOverride => typeof(ContextMenu);

    public override void Close()
    {
        base.Close();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void BindCommands(CommandRegistry? commands)
    {
        Bind(OpenItem, AppCommand.OpenRecording, commands);
        Bind(SaveItem, AppCommand.SaveTimeline, commands);
        Bind(ExportItem, AppCommand.ExportSelection, commands);
        Bind(CopyLlmItem, AppCommand.CopyForLlm, commands);
        Bind(DetachItem, AppCommand.Detach, commands);
        Bind(ConnectItem, AppCommand.Connect, commands);
        Bind(RecordAllItem, AppCommand.RecordAll, commands);
        Bind(FilterRangeItem, AppCommand.FilterToRange, commands);
        Bind(FilterViewItem, AppCommand.FilterToView, commands);
        Bind(ScopeBackItem, AppCommand.ScopeBack, commands);
        Bind(PaletteItem, AppCommand.CommandPalette, commands);
        Bind(LegendItem, AppCommand.Legend, commands);
        Bind(HelpItem, AppCommand.HelpShortcuts, commands);
    }

    private static void Bind(MenuItem item, AppCommand command, CommandRegistry? commands)
    {
        CommandDefinition definition = CommandRegistry.Get(command);
        item.Header = definition.Title;
        item.InputGesture = definition.Gesture;
        item.Command = commands?.CommandFor(command);
    }
}
