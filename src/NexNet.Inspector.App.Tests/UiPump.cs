using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace NexNet.Inspector.App.Tests;

/// <summary>Shared steps of the headless control tests: pumping the dispatcher and render timer, and reading tokens.</summary>
internal static class UiPump
{
    /// <summary>Runs the dispatcher and the render timer for <paramref name="ms"/> of wall time, past every 160 ms transition.</summary>
    public static void Settle(double ms = 400)
    {
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed.TotalMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(8);
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A few frames with no wall time: bindings, layout and the first render.</summary>
    public static void Frames()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    public static Color? ColorOf(object? value) => value switch
    {
        ISolidColorBrush b => b.Color,
        Color c => c,
        _ => null,
    };

    /// <summary>The colour of a token (a Brush.* key) in <paramref name="theme"/>.</summary>
    public static Color? Token(string key, ThemeVariant theme) =>
        Application.Current!.TryGetResource(key, theme, out object? value) ? ColorOf(value) : null;

    public static object? Resource(string key, ThemeVariant theme) =>
        Application.Current!.TryGetResource(key, theme, out object? value) ? value : null;

    public static ThemeVariant Variant(string name) => name == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

    /// <summary>Runs <paramref name="body"/> with the app in <paramref name="theme"/>, restoring the requested theme after.</summary>
    public static T InTheme<T>(ThemeVariant theme, Func<T> body)
    {
        Application app = Application.Current!;
        ThemeVariant? before = app.RequestedThemeVariant;
        app.RequestedThemeVariant = theme;
        try
        {
            return body();
        }
        finally
        {
            app.RequestedThemeVariant = before;
            Dispatcher.UIThread.RunJobs();
        }
    }
}
