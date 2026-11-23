using Avalonia;
using NexNet.Inspector.App.Settings;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>The capture harness itself, always run (unlike <see cref="CaptureTests"/>), so it cannot rot unnoticed.</summary>
[NotInParallel("avalonia")]
public sealed class CaptureHarnessTests
{
    [Test]
    public async Task Every_scenario_is_registered_with_a_unique_stem()
    {
        CaptureScenario[] all = Enum.GetValues<CaptureScenario>();
        await Assert.That(CaptureScenarios.Registered.Count).IsEqualTo(all.Length);
        await Assert.That(all.Select(s => CaptureScenarios.Get(s).Stem).Distinct().Count()).IsEqualTo(all.Length);
    }

    [Test]
    public async Task File_names_are_stem_and_theme()
    {
        await Assert.That(HeadlessCapture.FileName(CaptureScenario.Main, AppTheme.Light)).IsEqualTo("main--light.png");
        await Assert.That(HeadlessCapture.FileName(CaptureScenario.ControlGallery, AppTheme.Dark)).IsEqualTo("control-gallery--dark.png");
    }

    [Test]
    public async Task The_main_scenario_renders_at_2x_into_the_directory()
    {
        using TempDirectory dir = new();

        string path = await HeadlessCapture.RenderAsync(CaptureScenario.Main, AppTheme.Dark, dir.Path);

        await Assert.That(path).IsEqualTo(dir.File("main--dark.png"));
        await Assert.That(HeadlessCapture.PngSize(path)).IsEqualTo(new PixelSize(2880, 1800));
    }

    [Test]
    public async Task A_capture_names_its_theme()
    {
        using TempDirectory dir = new();
        // Refused before anything renders, so the throw is synchronous.
        Action render = () => HeadlessCapture.RenderAsync(CaptureScenario.Main, AppTheme.System, dir.Path);
        await Assert.That(render).Throws<ArgumentException>();
    }
}
