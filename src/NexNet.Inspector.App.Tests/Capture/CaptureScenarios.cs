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

    /// <summary>open-recording: the empty window behind the dialog on a two-session recording whose schema differs.</summary>
    OpenRecording,

    /// <summary>
    /// inspect-session-start: the Chat fixture's first 50 ms, the handshake traced from the ClientGreeting; a live
    /// connection recording all sessions and the session popover open.
    /// </summary>
    InspectSessionStart,

    /// <summary>context-menus: inspect-live with the lane menu on the FileUpload pipe, the hex selection menu and its Decode-as submenu.</summary>
    ContextMenus,
    /// <summary>command-palette: inspect-live with the palette open, queried for the traced invocation's id.</summary>
    CommandPalette,

    /// <summary>inspect-app-menu: inspect-live with a live connection and the logo's application menu open.</summary>
    InspectAppMenu,

    /// <summary>session-ended: a live session that disconnected, its strip under the title bar, Events and Ladder still navigable.</summary>
    SessionEnded,

    /// <summary>compare: a client-side and a server-side Chat capture aligned on session ready, SendAttachment selected.</summary>
    Compare,

    /// <summary>legend: inspect-live behind the Legend &amp; symbols dialog.</summary>
    Legend,

    /// <summary>export-section: inspect-live with its panes resized by the splitters and Export section as .nntl… open.</summary>
    ExportSection,

    /// <summary>scoped: inspect-live filtered to its view and then to a nested scope; the chip, the re-spanned overview, scoped panes.</summary>
    Scoped,

    /// <summary>bookmarks: inspect-live with three bookmarks (one scoped) and the title bar's ★ dropdown open.</summary>
    Bookmarks,
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
        [CaptureScenario.OpenRecording] = new("open-recording", OpenRecordingScreen.Create),
        [CaptureScenario.InspectSessionStart] = new("inspect-session-start", InspectSessionStartScreen.Create),
        [CaptureScenario.ContextMenus] = new("context-menus", ContextMenusScreen.Create),
        [CaptureScenario.CommandPalette] = new("command-palette", CommandPaletteScreen.Create),
        [CaptureScenario.InspectAppMenu] = new("inspect-app-menu", InspectAppMenuScreen.Create),
        [CaptureScenario.SessionEnded] = new("session-ended", SessionEndedScreen.Create),
        [CaptureScenario.Compare] = new("compare", CompareScreen.Create),
        [CaptureScenario.Legend] = new("legend", LegendScreen.Create),
        [CaptureScenario.ExportSection] = new("export-section", ExportSectionScreen.Create),
        [CaptureScenario.Scoped] = new("scoped", ScopedScreen.Create),
        [CaptureScenario.Bookmarks] = new("bookmarks", BookmarksScreen.Create),
    };

    public static IReadOnlyCollection<CaptureScenario> Registered => Registry.Keys;

    public static CaptureSetup Get(CaptureScenario scenario) =>
        Registry.TryGetValue(scenario, out CaptureSetup? setup) ? setup : throw new KeyNotFoundException($"capture scenario {scenario} is not registered");
}
