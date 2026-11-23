using Avalonia.Controls;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// The screens the capture harness renders, one PNG per scenario and theme (<c>&lt;stem&gt;--&lt;theme&gt;.png</c>). A later
/// work order adds its screen here and registers it in <see cref="CaptureScenarios"/>.
/// </summary>
public enum CaptureScenario
{
    /// <summary>The main window as it opens: the shell placeholder and empty state.</summary>
    Main,

    /// <summary>Every control registered in <see cref="ControlGallery"/>, in its states.</summary>
    ControlGallery,

    /// <summary>The connect screen: empty window and recents, the Connect dialog open, endpoint focused, probe reached.</summary>
    Connect,

    /// <summary>inspect-live: the Chat fixture's SendAttachment traced, chunk 2 decoded, FileUpload pinned, the gap in view.</summary>
    InspectLive,
}

/// <summary>What a scenario's setup is given: the theme in effect and the service that applied it.</summary>
public sealed record CaptureContext(AppTheme Theme, ThemeService Themes);

/// <summary>
/// One scenario: its file stem, the window height it needs (the width is always <see cref="CaptureScenarios.Width"/>),
/// and the setup that builds the window to render.
/// </summary>
public sealed record CaptureSetup(string Stem, Func<CaptureContext, Window> Create, double Height = CaptureScenarios.DefaultHeight);

/// <summary>The scenario registry. Every <see cref="CaptureScenario"/> must be registered (CaptureHarnessTests checks).</summary>
public static class CaptureScenarios
{
    /// <summary>Logical width of every capture; with <see cref="Scale"/> 2 the PNG is 2880 px wide.</summary>
    public const double Width = 1440;

    public const double DefaultHeight = 900;

    public const double Scale = 2;

    private static readonly Dictionary<CaptureScenario, CaptureSetup> Registry = new()
    {
        [CaptureScenario.Main] = new("main", static c => new MainWindow { DataContext = new MainWindowViewModel(c.Themes, store: null) }),
        [CaptureScenario.ControlGallery] = new("control-gallery", ControlGallery.CreateWindow, ControlGallery.Height),
        [CaptureScenario.Connect] = new("connect", ConnectScreen.Create),
        [CaptureScenario.InspectLive] = new("inspect-live", InspectLiveScreen.Create),
    };

    public static IReadOnlyCollection<CaptureScenario> Registered => Registry.Keys;

    public static CaptureSetup Get(CaptureScenario scenario) =>
        Registry.TryGetValue(scenario, out CaptureSetup? setup) ? setup : throw new KeyNotFoundException($"capture scenario {scenario} is not registered");
}
