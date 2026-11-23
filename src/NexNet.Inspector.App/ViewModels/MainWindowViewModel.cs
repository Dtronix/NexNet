using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>The main window's state. For now the shell placeholder and the theme choice; I16/I17 add the chrome and documents.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public const string AppName = "NexNet Inspector";

    private readonly ThemeService _themes;
    private readonly SettingsStore? _store;
    private readonly bool _ready;

    /// <param name="themes">Applies the theme choice.</param>
    /// <param name="store">Persists the choice; null keeps it for this run only (self-check, tests).</param>
    public MainWindowViewModel(ThemeService themes, SettingsStore? store)
    {
        ArgumentNullException.ThrowIfNull(themes);
        _themes = themes;
        _store = store;
        Theme = themes.Theme;
        _ready = true;
    }

    public string Title => AppName;

    /// <summary>What the empty window invites the user to do.</summary>
    public string EmptyStateHint => "Connect or open a recording";

    /// <summary>The user's theme choice: applied at once and saved.</summary>
    [ObservableProperty]
    public partial AppTheme Theme { get; set; }

    [RelayCommand]
    private void SetTheme(AppTheme theme) => Theme = theme;

    partial void OnThemeChanged(AppTheme value)
    {
        // The constructor's first assignment mirrors what is already applied and saved.
        if (!_ready) return;
        _themes.Theme = value;
        _store?.Update(s => s with { Theme = value });
    }
}
