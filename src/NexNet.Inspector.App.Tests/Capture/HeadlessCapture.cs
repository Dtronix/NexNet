using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// Renders a <see cref="CaptureScenario"/> on the shared headless session with Skia: <see cref="CaptureScenarios.Width"/>
/// logical px wide at render scaling <see cref="CaptureScenarios.Scale"/>, in one theme, into
/// <c>&lt;directory&gt;/&lt;stem&gt;--&lt;theme&gt;.png</c>.
/// </summary>
public static class HeadlessCapture
{
    /// <summary>The environment variable that turns the capture tests on and names their output directory.</summary>
    public const string DirectoryVariable = "NEXNET_INSPECTOR_CAPTURE_DIR";

    /// <summary>The file a capture of <paramref name="scenario"/> in <paramref name="theme"/> is written to.</summary>
    public static string FileName(CaptureScenario scenario, AppTheme theme) =>
        $"{CaptureScenarios.Get(scenario).Stem}--{(theme == AppTheme.Dark ? "dark" : "light")}.png";

    /// <summary>Renders and writes the PNG; returns its full path.</summary>
    public static Task<string> RenderAsync(CaptureScenario scenario, AppTheme theme, string directory)
    {
        if (theme == AppTheme.System) throw new ArgumentException("a capture names its theme: Light or Dark", nameof(theme));
        string path = Path.Combine(Path.GetFullPath(directory), FileName(scenario, theme));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return HeadlessSession.Run(() =>
        {
            Render(scenario, theme, path);
            return path;
        });
    }

    private static void Render(CaptureScenario scenario, AppTheme theme, string path)
    {
        CaptureSetup setup = CaptureScenarios.Get(scenario);
        using ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(PlatformThemeVariant.Light), theme);
        Window window = setup.Create(new CaptureContext(theme, themes));
        window.Width = CaptureScenarios.Width;
        window.Height = setup.Height;
        window.Show();
        try
        {
            window.SetRenderScaling(CaptureScenarios.Scale);
            Settle();
            using WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("the headless window rendered no frame");
            frame.Save(path, new PngBitmapEncoderOptions());
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>A few frames, so bindings, layout and the first render (and anything they queue) are done.</summary>
    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A PNG's pixel size, read from its IHDR chunk.</summary>
    public static PixelSize PngSize(string path)
    {
        Span<byte> header = stackalloc byte[24];
        using (FileStream stream = File.OpenRead(path)) stream.ReadExactly(header);
        return new PixelSize(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[16..]),
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[20..]));
    }
}
