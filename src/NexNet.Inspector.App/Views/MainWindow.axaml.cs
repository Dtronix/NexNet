using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using NexNet.Inspector.App.Settings;

namespace NexNet.Inspector.App.Views;

public sealed partial class MainWindow : Window
{
    private static readonly Uri IconUri = new("avares://nexnet-inspector/Assets/Logo/logo-256.png");

    private PixelPoint _normalPosition;
    private Size _normalSize;

    public MainWindow()
    {
        InitializeComponent();
        using (Stream icon = AssetLoader.Open(IconUri)) Icon = new WindowIcon(icon);

        // A maximized window's bounds are the screen's; the placement keeps the last normal ones so a restore after
        // relaunch returns to them.
        _normalSize = new Size(Width, Height);
        PositionChanged += (_, e) =>
        {
            if (WindowState == WindowState.Normal) _normalPosition = e.Point;
        };
        SizeChanged += (_, e) =>
        {
            if (WindowState == WindowState.Normal) _normalSize = e.NewSize;
        };
    }

    /// <summary>The window's normal bounds and state, as saved in the settings.</summary>
    public WindowPlacement Placement => new(_normalPosition.X, _normalPosition.Y, _normalSize.Width, _normalSize.Height,
        WindowState == WindowState.Maximized ? SavedWindowState.Maximized : SavedWindowState.Normal);

    /// <summary>
    /// Applies a saved placement before the window is shown. A position no screen contains any more (a monitor was
    /// unplugged) is dropped, so the window never opens out of reach.
    /// </summary>
    public void Restore(WindowPlacement? placement)
    {
        if (placement is not { IsUsable: true } p) return;

        Width = Math.Max(p.Width, MinWidth);
        Height = Math.Max(p.Height, MinHeight);
        _normalSize = new Size(Width, Height);

        PixelPoint at = new(p.X, p.Y);
        if (Screens.ScreenFromPoint(at) is not null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = at;
            _normalPosition = at;
        }

        if (p.State == SavedWindowState.Maximized) WindowState = WindowState.Maximized;
    }
}
