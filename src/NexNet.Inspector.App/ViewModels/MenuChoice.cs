using System.Windows.Input;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// One item of an app-menu submenu (Recent ▸, Switch session ▸, Schema ▸, Overflow mode ▸): its words, what it runs, and
/// for a pick-one list whether it is the current choice. A few per menu, rebuilt when the menu opens.
/// </summary>
/// <param name="Header">The item's text.</param>
/// <param name="Command">What it runs.</param>
/// <param name="Parameter">The command's parameter.</param>
/// <param name="IsChecked">True for the current choice of a pick-one list.</param>
/// <param name="IsEnabled">False greys the item out.</param>
public sealed record MenuChoice(string Header, ICommand Command, object? Parameter = null, bool IsChecked = false, bool IsEnabled = true);

/// <summary>What the app menu asks another part of the shell for (the registry's command has no view of its own yet).</summary>
public enum MenuRequest : byte
{
    /// <summary>Help &amp; shortcuts (F1).</summary>
    Help = 0,
}
