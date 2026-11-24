using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NexNet.Inspector.App.ViewModels;

namespace NexNet.Inspector.App.Views.Shell;

/// <summary>The session popover's view (SessionPopover.axaml) over <see cref="ViewModels.SessionAreaViewModel"/>.</summary>
public sealed partial class SessionPopover : UserControl
{
    public SessionPopover()
    {
        InitializeComponent();
        // Esc closes the popover; while Connect waits for a pick that leaves the dialog open for another try (I37).
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
    }

    /// <summary>Gives the keyboard to the first session row (Connect's pick from the keyboard: arrows, Enter, Esc).</summary>
    public void FocusFirstRow()
    {
        foreach (Control item in Rows.GetRealizedContainers())
        {
            if (item is ListBoxItem { Content: SessionChoice } container && container.Presenter?.Child is Button row)
            {
                row.Focus(NavigationMethod.Tab);
                return;
            }
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.Handled || DataContext is not SessionAreaViewModel area) return;
        area.IsPopoverOpen = false;
        e.Handled = true;
    }
}
