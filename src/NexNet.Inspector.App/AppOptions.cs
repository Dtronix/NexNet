namespace NexNet.Inspector.App;

/// <summary>The command-line switches the app understands.</summary>
[Flags]
public enum AppSwitches : byte
{
    None = 0,

    /// <summary><c>--headless</c>: Avalonia's headless platform, rendered by Skia; no window is shown.</summary>
    Headless = 1,

    /// <summary><c>--self-check</c>: build the main window, render, verify themes, fonts and AvaloniaEdit, then exit 0/1.</summary>
    SelfCheck = 2,
}

/// <summary>A launch's parsed arguments.</summary>
public readonly record struct AppOptions(AppSwitches Switches)
{
    public const string Usage = "nexnet-inspector [--headless] [--self-check]";

    public bool Headless => (Switches & AppSwitches.Headless) != 0;

    public bool SelfCheck => (Switches & AppSwitches.SelfCheck) != 0;

    /// <summary>
    /// Parses <paramref name="args"/>; false with <paramref name="error"/> on an unknown or repeated switch, so a typo
    /// in CI fails loudly instead of launching a window that never exits.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<string> args, out AppOptions options, out string? error)
    {
        AppSwitches switches = AppSwitches.None;
        foreach (string arg in args)
        {
            AppSwitches one = Switch(arg);
            if (one == AppSwitches.None)
            {
                options = default;
                error = $"unknown argument '{arg}'";
                return false;
            }

            if ((switches & one) != 0)
            {
                options = default;
                error = $"'{arg}' is given twice";
                return false;
            }

            switches |= one;
        }

        // The check reads back what the headless compositor drew; on a desktop platform there is no such frame.
        if ((switches & AppSwitches.SelfCheck) != 0 && (switches & AppSwitches.Headless) == 0)
        {
            options = default;
            error = "--self-check needs --headless";
            return false;
        }

        options = new AppOptions(switches);
        error = null;
        return true;
    }

    private static AppSwitches Switch(string arg) => arg switch
    {
        "--headless" => AppSwitches.Headless,
        "--self-check" => AppSwitches.SelfCheck,
        _ => AppSwitches.None,
    };
}
