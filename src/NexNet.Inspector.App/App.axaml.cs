using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using NexNet.Inspector.App.Checks;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;

namespace NexNet.Inspector.App;

/// <summary>
/// The composition root. A normal launch loads the user's settings, applies the theme and opens the main window; a
/// <c>--self-check</c> launch runs <see cref="SelfCheck"/> on its own and never reads or writes the user's settings.
/// Hosted without a desktop lifetime (the tests' headless session) it only loads its styles.
/// </summary>
public sealed partial class App : Application
{
    private readonly AppOptions _options;

    /// <summary>A launch with no arguments (the designer's, and the tests' headless session).</summary>
    public App()
        : this(default)
    {
    }

    internal App(AppOptions options) => _options = options;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (_options.SelfCheck) StartSelfCheck(desktop);
            else StartDesktop(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void StartSelfCheck(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // The check opens and closes its own windows; only its verdict may end the process.
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Dispatcher.UIThread.Post(() =>
        {
            int exit;
            try
            {
                exit = SelfCheck.Report(SelfCheck.Run(this), Console.Out);
            }
            catch (Exception e)
            {
                // Anything escaping the steps is itself a failed check: say what, never hang CI.
                Console.Out.WriteLine("self-check: FAIL (" + e.GetType().Name + ": " + e.Message + ")");
                exit = 1;
            }

            Console.Out.Flush();
            desktop.Shutdown(exit);
        });
    }

    private void StartDesktop(IClassicDesktopStyleApplicationLifetime desktop)
    {
        SettingsStore? store = OpenSettings();
        AppSettings settings = store?.Current ?? new AppSettings();

        PlatformThemeSource os = new(PlatformSettings);
        ThemeService themes = new(this, os, settings.Theme);
        MainWindowViewModel viewModel = new(themes, store);
        MainWindow window = new() { DataContext = viewModel };
        window.Restore(settings.Window);
        if (store is not null) window.Closing += (_, _) => store.Update(s => s with { Window = window.Placement });

        desktop.MainWindow = window;
        desktop.Exit += (_, _) =>
        {
            themes.Dispose();
            os.Dispose();
            // Finishes a Record all in progress, ends live streams and closes recordings; unsaved-data prompts happen before
            // exit (I29).
            viewModel.Chrome.Session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            viewModel.Documents.DisposeAsync().AsTask().GetAwaiter().GetResult();
        };
    }

    /// <summary>
    /// The user's settings, or null when this environment has no settings folder (no LOCALAPPDATA / HOME): the app then
    /// runs on defaults and saves nothing.
    /// </summary>
    private static SettingsStore? OpenSettings()
    {
        string path;
        try
        {
            path = SettingsPaths.File();
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        SettingsStore store = new(path);
        store.Load();
        return store;
    }
}
