using Avalonia;
using Avalonia.Headless;

namespace NexNet.Inspector.App;

internal static class Program
{
    /// <summary>The exit code of a launch whose arguments were refused (sysexits' EX_USAGE).</summary>
    public const int UsageExit = 64;

    /// <summary>The arguments are read before Avalonia starts, so a bad one is reported on stderr and no window opens.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (!AppOptions.TryParse(args, out AppOptions options, out string? error))
        {
            Console.Error.WriteLine("nexnet-inspector: " + error);
            Console.Error.WriteLine("usage: " + AppOptions.Usage);
            return UsageExit;
        }

        return BuildAvaloniaApp(options).StartWithClassicDesktopLifetime(args);
    }

    /// <summary>The designer's entry point: a launch with no arguments.</summary>
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(default);

    /// <summary>
    /// With <c>--headless</c>, Avalonia's headless platform drawn by Skia (not the headless drawing stub), so the
    /// self-check exercises the same renderer and font stack a desktop launch uses, with no display.
    /// </summary>
    internal static AppBuilder BuildAvaloniaApp(AppOptions options) => options.Headless
        ? AppBuilder.Configure(() => new App(options))
            .UseSkia()
            .UseHarfBuzz()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .LogToTrace()
        : AppBuilder.Configure(() => new App(options))
            .UsePlatformDetect()
            .LogToTrace();
}
