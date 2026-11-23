using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NexNet.Inspector.App.Model;

namespace NexNet.Inspector.App.Views.Shell;

/// <summary>
/// The title bar's window side. The caption buttons act through their Click handlers (their element roles alone give
/// Windows the hit-test codes, not the action) and the maximize glyph follows the window state. With
/// <see cref="ChromeMode.Undecorated"/> (Linux) no OS frame moves the window, so a press on the bar's empty area drags it
/// and a double-click maximizes or restores it.
/// </summary>
public sealed partial class TitleBar : UserControl
{
    private Window? _window;

    public TitleBar()
    {
        InitializeComponent();
        Bar.PointerPressed += OnBarPressed;
    }

    /// <summary>Where the application menu opens: under the logo button.</summary>
    public Control AppMenuAnchor => LogoButton;

    /// <summary>Where the session popover opens: under the connection group.</summary>
    public Control SessionAnchor => ConnectionGroup;

    /// <summary>How the window draws its frame; <see cref="ChromeMode.Undecorated"/> makes the bar move the window itself.</summary>
    public ChromeMode Mode { get; set; } = WindowChromePolicy.Current;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is null) return;
        _window.PropertyChanged += OnWindowChanged;
        SyncWindowState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null) _window.PropertyChanged -= OnWindowChanged;
        _window = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty) SyncWindowState();
    }

    private void SyncWindowState()
    {
        bool maximized = _window?.WindowState is WindowState.Maximized;
        MaximizeGlyph.IsVisible = !maximized;
        RestoreGlyph.IsVisible = maximized;
        AutomationProperties.SetName(MaximizeButton, maximized ? "Restore" : "Maximize");
    }

    private void OnBarPressed(object? sender, PointerPressedEventArgs e)
    {
        // Presses on the bar's buttons, tabs and switch are handled before they reach it; what arrives is the drag area.
        if (Mode != ChromeMode.Undecorated || _window is not { } window) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (e.ClickCount == 2) ToggleMaximized(window);
        else window.BeginMoveDrag(e);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e)
    {
        if (_window is { } w) w.WindowState = WindowState.Minimized;
    }

    private void OnMaximize(object? sender, RoutedEventArgs e)
    {
        if (_window is { } w) ToggleMaximized(w);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => _window?.Close();

    private static void ToggleMaximized(Window window) =>
        window.WindowState = window.WindowState is WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
