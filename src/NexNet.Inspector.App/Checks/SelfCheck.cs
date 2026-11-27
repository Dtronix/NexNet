using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.App.Views.Home;

namespace NexNet.Inspector.App.Checks;

/// <summary>What the self-check verifies, in order.</summary>
public enum SelfCheckStep : byte
{
    /// <summary>The main window builds, binds and lays out (the empty state's text is bound).</summary>
    MainWindow,

    /// <summary>One frame of the main window renders through Skia with something drawn on it.</summary>
    Render,

    /// <summary>Light and Dark each paint the window background with their own <c>Color.Surface.App</c> token.</summary>
    Themes,

    /// <summary>The bundled UI and mono fonts resolve, in every weight the tokens use.</summary>
    Fonts,

    /// <summary>An AvaloniaEdit editor gets its theme, lays out its lines and renders.</summary>
    Editor,
}

/// <summary>One step's verdict and a short reason.</summary>
public readonly record struct SelfCheckResult(SelfCheckStep Step, bool Passed, string Detail);

/// <summary>
/// <c>--headless --self-check</c>: proves a (Native AOT) build can start its UI. Runs on the headless platform's UI
/// thread inside the real <see cref="App"/>, so the styles, compiled bindings, fonts and AvaloniaEdit go through exactly
/// what a desktop launch loads; trimming or AOT damage to any of them fails a step instead of crashing a user later.
/// </summary>
public static class SelfCheck
{
    public const string UiFamily = "Atkinson Hyperlegible Next";
    public const string MonoFamily = "JetBrains Mono NL";

    private static readonly FontWeight[] UiWeights = [FontWeight.Regular, FontWeight.Medium, FontWeight.SemiBold, FontWeight.Bold];
    private static readonly FontWeight[] MonoWeights = [FontWeight.Regular, FontWeight.Medium, FontWeight.SemiBold];

    /// <summary>Runs every step, each isolated so one failure still reports the rest. Call on the UI thread.</summary>
    public static SelfCheckResult[] Run(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        ThemeVariant? requested = application.RequestedThemeVariant;
        using PlatformThemeSource os = new(application.PlatformSettings);
        using ThemeService themes = new(application, os, AppTheme.Light);
        MainWindow window = new() { DataContext = new MainWindowViewModel(themes, store: null), Width = 1440, Height = 900 };
        try
        {
            window.Show();
            Pump();
            return
            [
                Step(SelfCheckStep.MainWindow, () => CheckMainWindow(window)),
                Step(SelfCheckStep.Render, () => CheckRender(window)),
                Step(SelfCheckStep.Themes, () => CheckThemes(application, themes, window)),
                Step(SelfCheckStep.Fonts, () => CheckFonts(application)),
                Step(SelfCheckStep.Editor, () => CheckEditor(application)),
            ];
        }
        finally
        {
            window.Close();
            application.RequestedThemeVariant = requested;
            Pump();
        }
    }

    /// <summary>Writes one line per step and the verdict; the process exit code (0 all passed, 1 otherwise).</summary>
    public static int Report(IReadOnlyList<SelfCheckResult> results, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(output);
        bool passed = results.Count > 0;
        foreach (SelfCheckResult r in results)
        {
            output.WriteLine($"self-check: {Name(r.Step),-11} {(r.Passed ? "ok  " : "FAIL")} {r.Detail}");
            passed &= r.Passed;
        }

        output.WriteLine(passed ? "self-check: PASS" : "self-check: FAIL");
        return passed ? 0 : 1;
    }

    public static string Name(SelfCheckStep step) => step switch
    {
        SelfCheckStep.MainWindow => "main-window",
        SelfCheckStep.Render => "render",
        SelfCheckStep.Themes => "themes",
        SelfCheckStep.Fonts => "fonts",
        SelfCheckStep.Editor => "editor",
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, null),
    };

    private static SelfCheckResult Step(SelfCheckStep step, Func<(bool Passed, string Detail)> body)
    {
        try
        {
            (bool passed, string detail) = body();
            return new SelfCheckResult(step, passed, detail);
        }
        catch (Exception e)
        {
            // A step that throws is a failed step; the report names the exception so CI logs show the cause.
            return new SelfCheckResult(step, false, e.GetType().Name + ": " + e.Message);
        }
    }

    private static (bool, string) CheckMainWindow(MainWindow window)
    {
        TextBlock? hint = window.FindControl<HomeView>("Home")?.FindControl<TextBlock>("AppTitle");
        Views.Shell.TitleBar? titleBar = window.FindControl<Views.Shell.TitleBar>("TitleBar");
        if (hint?.Text != MainWindowViewModel.AppName) return (false, $"home title is '{hint?.Text}'");
        if (titleBar is null || titleBar.Bounds.Height != 36) return (false, $"title bar is {titleBar?.Bounds.Height} px high, not 36");
        if (window.MinWidth != 1100 || window.MinHeight != 700) return (false, $"minimum size is {window.MinWidth}x{window.MinHeight}");
        return (true, $"{window.ClientSize.Width:0}x{window.ClientSize.Height:0}, title bar 36 px, bound");
    }

    private static (bool, string) CheckRender(MainWindow window)
    {
        using WriteableBitmap frame = Capture(window);
        int colours = FramePixels.DistinctColours(frame, minimum: 8);
        return colours >= 8
            ? (true, $"{frame.PixelSize.Width}x{frame.PixelSize.Height} px frame drawn")
            : (false, $"the frame holds only {colours} colour(s): nothing was drawn");
    }

    private static (bool, string) CheckThemes(Application application, ThemeService themes, MainWindow window)
    {
        Color light = Background(application, themes, window, AppTheme.Light, out Color lightToken);
        Color dark = Background(application, themes, window, AppTheme.Dark, out Color darkToken);
        themes.Theme = AppTheme.Light;
        Pump();
        if (light != lightToken) return (false, $"Light background {light} is not Color.Surface.App {lightToken}");
        if (dark != darkToken) return (false, $"Dark background {dark} is not Color.Surface.App {darkToken}");
        if (light == dark) return (false, "Light and Dark paint the same background");
        return (true, $"light {light}, dark {dark}");
    }

    private static Color Background(Application application, ThemeService themes, MainWindow window, AppTheme theme, out Color token)
    {
        themes.Theme = theme;
        Pump();
        ThemeVariant variant = themes.Effective;
        if (!application.TryGetResource("Color.Surface.App", variant, out object? value) || value is not Color colour)
            throw new InvalidOperationException($"no Color.Surface.App resource for {variant}");
        token = colour;

        // Bottom-left corner: below the empty state and outside the title bar, so it is plain window background.
        using WriteableBitmap frame = Capture(window);
        return FramePixels.At(frame, 2, frame.PixelSize.Height - 3);
    }

    private static (bool, string) CheckFonts(Application application)
    {
        string? fault = Family(application, "Font.Ui", UiFamily, UiWeights) ?? Family(application, "Font.Mono", MonoFamily, MonoWeights);
        return fault is null
            ? (true, $"{UiFamily} ({UiWeights.Length} weights), {MonoFamily} ({MonoWeights.Length} weights)")
            : (false, fault);
    }

    /// <summary>Null when every weight of the token's family resolves to a real face of that family and weight.</summary>
    private static string? Family(Application application, string key, string expected, FontWeight[] weights)
    {
        if (!application.TryGetResource(key, ThemeVariant.Light, out object? value) || value is not FontFamily family)
            return $"no {key} resource";

        foreach (FontWeight weight in weights)
        {
            // Avalonia falls back to a system font, or synthesizes a weight, rather than failing: check what it chose.
            if (!FontManager.Current.TryGetGlyphTypeface(new Typeface(family, FontStyle.Normal, weight), out GlyphTypeface? face))
                return $"{key} {weight}: no typeface";
            // A non-RIBBI weight's legacy family name carries the weight ("… Next Medium"); the typographic one does not.
            string name = string.IsNullOrEmpty(face.TypographicFamilyName) ? face.FamilyName : face.TypographicFamilyName;
            if (name != expected) return $"{key} {weight}: resolved to '{name}', not '{expected}'";
            if (face.Weight != weight) return $"{key} {weight}: resolved to weight {face.Weight}";
        }

        return null;
    }

    private static (bool, string) CheckEditor(Application application)
    {
        if (!application.TryGetResource("Font.Mono", ThemeVariant.Light, out object? value) || value is not FontFamily mono)
            return (false, "no Font.Mono resource");

        TextEditor editor = new()
        {
            FontFamily = mono,
            FontSize = 12,
            ShowLineNumbers = true,
            Text = "0000  4E 58 4E 54 01 00 00 00\n0008  2A 00 10 00 03 68 69 21\n0010  00 00 00 00 00 00 00 00",
        };
        Window host = new() { Width = 640, Height = 240, Content = editor };
        try
        {
            host.Show();
            Pump();
            AvaloniaEdit.Rendering.TextView view = editor.TextArea.TextView;
            if (editor.Template is null) return (false, "the AvaloniaEdit control theme did not apply (no template)");
            if (!view.VisualLinesValid || view.VisualLines.Count != 3) return (false, $"{view.VisualLines.Count} visual line(s) laid out, not 3");

            using WriteableBitmap frame = Capture(host);
            int colours = FramePixels.DistinctColours(frame, step: 2, minimum: 4);
            return colours >= 4
                ? (true, $"3 lines laid out and drawn ({view.DocumentHeight:0} px)")
                : (false, "the editor drew nothing");
        }
        finally
        {
            host.Close();
            Pump();
        }
    }

    private static WriteableBitmap Capture(TopLevel top)
    {
        Pump();
        return top.CaptureRenderedFrame() ?? throw new InvalidOperationException("the headless platform rendered no frame");
    }

    /// <summary>Runs queued UI work, a layout pass and a render tick, so the next read sees the current state.</summary>
    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
