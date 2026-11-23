using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Platform;
using NexNet.Inspector.App.Services.Commands;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views.Shell;

namespace NexNet.Inspector.App.Views;

/// <summary>
/// The shell window. It draws its own title bar (<see cref="ChromeMode"/>): on Windows the OS frame stays and the client
/// area extends under the bar, re-applied by <see cref="TitleBarRecovery"/> when a display or session change drops it,
/// and the window pads itself by its off-screen margin when maximized; on Linux it has no OS decorations, and edge grips
/// resize it. It tells the chrome its width (the OQ10 collapse rules) and holds Ctrl+Tab / Ctrl+Shift+Tab.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly Uri IconUri = new("avares://nexnet-inspector/Assets/Logo/logo-256.png");

    private PixelPoint _normalPosition;
    private Size _normalSize;
    private TitleBarRecovery? _titleBarRecovery;
    private Panel? _resizeGrips;
    private readonly List<KeyBinding> _commandBindings = [];

    public MainWindow()
        : this(WindowChromePolicy.Current)
    {
    }

    /// <param name="chrome">How the window draws its frame; tests pick the Linux mode on any OS.</param>
    internal MainWindow(ChromeMode chrome)
    {
        InitializeComponent();
        using (Stream icon = AssetLoader.Open(IconUri)) Icon = new WindowIcon(icon);
        Chrome = chrome;
        TitleBar.Mode = chrome;
        AppMenuPopup.PlacementTarget = TitleBar.AppMenuAnchor;
        SessionPopup.PlacementTarget = TitleBar.SessionAnchor;
        AppMenu.CloseRequested += (_, _) => AppMenuPopup.IsOpen = false;
        AppMenuPopup.Opened += (_, _) =>
        {
            TitleBar.AppMenuAnchor.Classes.Set("open", true);
            // Keyboard navigation starts in the menu, as a context menu opened from the keyboard does.
            AppMenu.Focus(NavigationMethod.Directional);
        };
        AppMenuPopup.Closed += (_, _) => TitleBar.AppMenuAnchor.Classes.Set("open", false);
        if (chrome == ChromeMode.Undecorated)
        {
            // No OS frame: the bar moves and maximizes the window, the grips resize it.
            ExtendClientAreaToDecorationsHint = false;
            WindowDecorations = WindowDecorations.None;
            _resizeGrips = ResizeGrips.Create(this);
            Grid.SetRowSpan(_resizeGrips, 2);
            Root.Children.Add(_resizeGrips);
        }

        // Tunnelling, so Ctrl+Tab cycles documents before focus navigation takes the Tab.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        Opened += (_, _) =>
        {
            // The platform handle exists from here on.
            // Only a real Win32 window has a procedure to hook (not the headless platform's).
            if (Chrome == ChromeMode.Extended && TryGetPlatformHandle() is { Handle: not 0, HandleDescriptor: "HWND" })
                _titleBarRecovery = TitleBarRecovery.Start(OperatingSystem.IsWindows(), () => new Win32TitleBarWindow(this), new DispatcherScheduler(),
                    static line => Trace.TraceInformation(line));
        };
        Closed += (_, _) => _titleBarRecovery?.Dispose();

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

    /// <summary>How the window draws its frame.</summary>
    public ChromeMode Chrome { get; }

    private ShellChromeViewModel? ChromeModel => (DataContext as MainWindowViewModel)?.Chrome;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ChromeModel?.UpdateWidth(Bounds.Width);
        BindShortcuts((DataContext as MainWindowViewModel)?.Commands);
        if (DataContext is MainWindowViewModel model) model.PickSchemaFile ??= PickSchemaAsync;
    }

    /// <summary>
    /// Binds every command's window-wide shortcut (Ctrl+Shift+P, Ctrl+G, …) to the registry, so a shortcut runs exactly
    /// when the palette and the menu show its command enabled. A gesture the window already binds keeps that binding.
    /// </summary>
    private void BindShortcuts(CommandRegistry? commands)
    {
        foreach (KeyBinding binding in _commandBindings) KeyBindings.Remove(binding);
        _commandBindings.Clear();
        if (commands is null) return;
        foreach (CommandDefinition definition in CommandRegistry.Definitions)
        {
            if (!definition.IsWindowShortcut || definition.Gesture is not { } gesture) continue;
            bool taken = false;
            foreach (KeyBinding existing in KeyBindings) taken |= gesture.Equals(existing.Gesture);
            if (taken) continue;
            KeyBinding binding = new() { Gesture = gesture, Command = commands.CommandFor(definition.Command) };
            KeyBindings.Add(binding);
            _commandBindings.Add(binding);
        }
    }

    private static readonly FilePickerFileType Schemas = new("NexNet schemas") { Patterns = ["*.nnschema"] };

    private async Task<string?> PickSchemaAsync()
    {
        if (StorageProvider is not { CanOpen: true } storage) return null;
        IReadOnlyList<IStorageFile> picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load workspace schema",
            AllowMultiple = false,
            FileTypeFilter = [Schemas],
        });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty) ChromeModel?.UpdateWidth(Bounds.Width);

        // Properties set while the XAML loads arrive before the named parts exist.
        if ((change.Property == WindowStateProperty || change.Property == OffScreenMarginProperty) && Root is not null)
        {
            // Maximized, an extended window reaches past the screen by its off-screen margin: pad it back in.
            Root.Margin = WindowState is WindowState.Maximized ? OffScreenMargin : default;
            if (_resizeGrips is not null) _resizeGrips.IsVisible = WindowState is WindowState.Normal;
        }
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab || !e.KeyModifiers.HasFlag(KeyModifiers.Control) || ChromeModel is not { } chrome) return;
        chrome.Documents.Cycle(backwards: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        e.Handled = true;
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
