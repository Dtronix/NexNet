using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.ViewModels;

namespace NexNet.Inspector.App.Tests;

/// <summary>System / Light / Dark, and System following the OS live (OQ15).</summary>
[NotInParallel("avalonia")]
public sealed class ThemeServiceTests
{
    private static Color SurfaceApp(Application app) =>
        app.TryGetResource("Color.Surface.App", app.ActualThemeVariant, out object? value) && value is Color c ? c : default;

    [Test]
    [Arguments(AppTheme.Light, PlatformThemeVariant.Dark, "Light")]
    [Arguments(AppTheme.Dark, PlatformThemeVariant.Light, "Dark")]
    [Arguments(AppTheme.System, PlatformThemeVariant.Light, "Light")]
    [Arguments(AppTheme.System, PlatformThemeVariant.Dark, "Dark")]
    public async Task The_choice_resolves_against_the_os(AppTheme theme, PlatformThemeVariant os, string expected)
    {
        await Assert.That(ThemeService.Resolve(theme, os).Key.ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task System_follows_an_os_switch_live()
    {
        (ThemeVariant before, ThemeVariant after, ThemeVariant back, Color dark) = await HeadlessSession.Run(() =>
        {
            Application app = HeadlessSession.App;
            FakeOsTheme os = new(PlatformThemeVariant.Light);
            using ThemeService themes = new(app, os, AppTheme.System);
            ThemeVariant b = app.ActualThemeVariant;
            os.Switch(PlatformThemeVariant.Dark);
            ThemeVariant a = app.ActualThemeVariant;
            Color surface = SurfaceApp(app);
            os.Switch(PlatformThemeVariant.Light);
            return (b, a, app.ActualThemeVariant, surface);
        });

        await Assert.That(before).IsEqualTo(ThemeVariant.Light);
        await Assert.That(after).IsEqualTo(ThemeVariant.Dark);
        await Assert.That(back).IsEqualTo(ThemeVariant.Light);
        await Assert.That(dark).IsEqualTo(Color.Parse("#111318"));
    }

    [Test]
    public async Task An_explicit_choice_ignores_os_switches()
    {
        (ThemeVariant light, ThemeVariant dark) = await HeadlessSession.Run(() =>
        {
            Application app = HeadlessSession.App;
            FakeOsTheme os = new(PlatformThemeVariant.Dark);
            using ThemeService themes = new(app, os, AppTheme.Light);
            os.Switch(PlatformThemeVariant.Light);
            os.Switch(PlatformThemeVariant.Dark);
            ThemeVariant l = app.ActualThemeVariant;
            themes.Theme = AppTheme.Dark;
            os.Switch(PlatformThemeVariant.Light);
            return (l, app.ActualThemeVariant);
        });

        await Assert.That(light).IsEqualTo(ThemeVariant.Light);
        await Assert.That(dark).IsEqualTo(ThemeVariant.Dark);
    }

    [Test]
    public async Task Choosing_system_picks_up_the_current_os_theme()
    {
        ThemeVariant actual = await HeadlessSession.Run(() =>
        {
            Application app = HeadlessSession.App;
            FakeOsTheme os = new(PlatformThemeVariant.Light);
            using ThemeService themes = new(app, os, AppTheme.Light);
            os.Switch(PlatformThemeVariant.Dark);
            themes.Theme = AppTheme.System;
            return app.ActualThemeVariant;
        });

        await Assert.That(actual).IsEqualTo(ThemeVariant.Dark);
    }

    [Test]
    public async Task An_os_switch_raised_off_the_ui_thread_is_applied_on_it()
    {
        (ThemeVariant beforeJobs, ThemeVariant afterJobs) = await HeadlessSession.Run(() =>
        {
            Application app = HeadlessSession.App;
            FakeOsTheme os = new(PlatformThemeVariant.Light);
            using ThemeService themes = new(app, os, AppTheme.System);
            // A dedicated thread: Task.Run(...).Wait() may inline the work onto this (UI) thread.
            Thread platform = new(() => os.Switch(PlatformThemeVariant.Dark));
            platform.Start();
            platform.Join();
            ThemeVariant b = app.ActualThemeVariant;
            Dispatcher.UIThread.RunJobs();
            return (b, app.ActualThemeVariant);
        });

        await Assert.That(beforeJobs).IsEqualTo(ThemeVariant.Light);
        await Assert.That(afterJobs).IsEqualTo(ThemeVariant.Dark);
    }

    [Test]
    public async Task A_disposed_service_stops_listening()
    {
        int subscribers = await HeadlessSession.Run(() =>
        {
            FakeOsTheme os = new();
            ThemeService themes = new(HeadlessSession.App, os, AppTheme.System);
            themes.Dispose();
            return os.Subscribers;
        });

        await Assert.That(subscribers).IsEqualTo(0);
    }

    [Test]
    public async Task The_view_model_applies_and_saves_the_choice()
    {
        using TempDirectory dir = new();
        SettingsStore store = new(dir.File("settings.json"));
        (ThemeVariant actual, bool saved) = await HeadlessSession.Run(() =>
        {
            Application app = HeadlessSession.App;
            using ThemeService themes = new(app, new FakeOsTheme(), AppTheme.Light);
            MainWindowViewModel vm = new(themes, store);
            bool before = File.Exists(store.Path);
            vm.SetThemeCommand.Execute(AppTheme.Dark);
            return (app.ActualThemeVariant, !before && File.Exists(store.Path));
        });

        await Assert.That(actual).IsEqualTo(ThemeVariant.Dark);
        await Assert.That(saved).IsTrue();
        SettingsStore reader = new(store.Path);
        reader.Load();
        await Assert.That(reader.Current.Theme).IsEqualTo(AppTheme.Dark);
    }
}
