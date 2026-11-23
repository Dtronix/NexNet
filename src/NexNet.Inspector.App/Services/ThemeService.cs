using Avalonia;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using NexNet.Inspector.App.Settings;

namespace NexNet.Inspector.App.Services;

/// <summary>The OS's light/dark preference and its changes; a seam so a test can play an OS switch.</summary>
public interface IOsThemeSource
{
    PlatformThemeVariant Current { get; }

    event EventHandler? Changed;
}

/// <summary>
/// The OS preference from Avalonia's <see cref="IPlatformSettings"/>. A platform without settings (some headless or
/// minimal desktops) reads as Light and never changes.
/// </summary>
public sealed class PlatformThemeSource : IOsThemeSource, IDisposable
{
    private readonly IPlatformSettings? _settings;

    public PlatformThemeSource(IPlatformSettings? settings)
    {
        _settings = settings;
        if (_settings is not null) _settings.ColorValuesChanged += OnColorValuesChanged;
    }

    public PlatformThemeVariant Current => _settings?.GetColorValues().ThemeVariant ?? PlatformThemeVariant.Light;

    public event EventHandler? Changed;

    public void Dispose()
    {
        if (_settings is not null) _settings.ColorValuesChanged -= OnColorValuesChanged;
    }

    private void OnColorValuesChanged(object? sender, PlatformColorValues e) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Applies the user's <see cref="AppTheme"/> to the application. System is resolved to an explicit Light or Dark from
/// the OS and re-resolved on every OS change, rather than left at <see cref="ThemeVariant.Default"/>, so the theme the
/// app shows is always the one this service reports (and a test can drive the OS side).
/// </summary>
public sealed class ThemeService : IDisposable
{
    private readonly Application _application;
    private readonly IOsThemeSource _os;
    private AppTheme _theme;

    public ThemeService(Application application, IOsThemeSource os, AppTheme theme)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(os);
        _application = application;
        _os = os;
        _theme = theme;
        _os.Changed += OnOsChanged;
        Apply();
    }

    /// <summary>The user's choice. Setting it applies at once.</summary>
    public AppTheme Theme
    {
        get => _theme;
        set
        {
            if (_theme == value) return;
            _theme = value;
            Apply();
        }
    }

    /// <summary>The variant in effect: <see cref="Theme"/> with System resolved against the OS.</summary>
    public ThemeVariant Effective => Resolve(_theme, _os.Current);

    /// <summary>Raised after the effective variant may have changed (a new choice, or the OS switched under System).</summary>
    public event EventHandler? Applied;

    public static ThemeVariant Resolve(AppTheme theme, PlatformThemeVariant os) => theme switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.Dark => ThemeVariant.Dark,
        _ => os == PlatformThemeVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light,
    };

    public void Dispose() => _os.Changed -= OnOsChanged;

    private void OnOsChanged(object? sender, EventArgs e)
    {
        if (_theme != AppTheme.System) return;

        // Some platforms raise the change off the UI thread; the theme is UI state.
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    private void Apply()
    {
        _application.RequestedThemeVariant = Effective;
        Applied?.Invoke(this, EventArgs.Empty);
    }
}
