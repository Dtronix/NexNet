using Avalonia;
using NexNet.Inspector.App.Settings;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// The opt-in screen capture: every registered scenario in Light and Dark, written to the directory
/// <see cref="HeadlessCapture.DirectoryVariable"/> names. Without the variable each test is skipped and nothing renders.
/// Run: <c>NEXNET_INSPECTOR_CAPTURE_DIR=/path dotnet test src/NexNet.Inspector.App.Tests -c Release --treenode-filter "/*/*/CaptureTests/*"</c>.
/// </summary>
[NotInParallel("avalonia")]
public sealed class CaptureTests
{
    public static IEnumerable<Func<(CaptureScenario, AppTheme)>> Cases()
    {
        foreach (CaptureScenario scenario in Enum.GetValues<CaptureScenario>())
        {
            yield return () => (scenario, AppTheme.Light);
            yield return () => (scenario, AppTheme.Dark);
        }
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Scenario_is_captured(CaptureScenario scenario, AppTheme theme)
    {
        string? directory = Environment.GetEnvironmentVariable(HeadlessCapture.DirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory)) Skip.Test(HeadlessCapture.DirectoryVariable + " is not set: no capture");

        string path = await HeadlessCapture.RenderAsync(scenario, theme, directory!);

        await Assert.That(File.Exists(path)).IsTrue();
        PixelSize size = HeadlessCapture.PngSize(path);
        await Assert.That(size.Width).IsEqualTo((int)(CaptureScenarios.Width * CaptureScenarios.Scale));
        await Assert.That(size.Height).IsEqualTo((int)(CaptureScenarios.Get(scenario).Height * CaptureScenarios.Scale));
    }
}
