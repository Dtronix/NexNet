using System.Xml.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.App.Views.Shell;

namespace NexNet.Inspector.App.Tests.Shell;

/// <summary>
/// The title bar (controls/window-title-bar) and its tab strip (controls/document-tab): every interactive element in the
/// bar has an element role (without one, Windows takes a click on it as a caption drag), the empty and open states, the
/// caption buttons, tab clicks, the close glyph, middle-click, Ctrl+Tab, the Compare checkboxes, and the window's frame and
/// collapse state.
/// </summary>
[NotInParallel("avalonia")]
public sealed class TitleBarTests
{
    private static readonly string[] Interactive = ["Button", "ListBox", "TabStrip", "CheckBox"];

    private static string ShellSource(string file)
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "NexNet.slnx"))) return Path.Combine(d.FullName, "NexNet.Inspector.App", "Views", "Shell", file);
        }

        throw new InvalidOperationException("NexNet.slnx not found above " + AppContext.BaseDirectory);
    }

    private const string RoleAttribute = "WindowDecorationProperties.ElementRole";

    /// <summary>The element's role, or the nearest ancestor's when the element has none (Avalonia's hit test walks up).</summary>
    private static string? RoleOf(XElement e) =>
        e.AncestorsAndSelf().Select(a => (string?)a.Attribute(RoleAttribute)).FirstOrDefault(r => r is not null);

    [Test]
    public async Task Every_interactive_element_in_the_bar_has_a_role_other_than_the_drag_area()
    {
        List<string> missing = [];
        foreach (string file in new[] { "TitleBar.axaml", "DocumentTabStrip.axaml" })
        {
            XDocument doc = XDocument.Load(ShellSource(file));
            XElement root = doc.Root!;
            foreach (XElement e in root.DescendantsAndSelf().Where(e => Interactive.Contains(e.Name.LocalName)))
            {
                if (RoleOf(e) is null or "TitleBar") missing.Add(file + ": " + ((string?)e.Attribute("Name") ?? e.Name.LocalName));
            }
        }

        // The strip sits in the bar as one element; its role is on its own root.
        string? stripRole = (string?)XDocument.Load(ShellSource("DocumentTabStrip.axaml")).Root!.Attribute(RoleAttribute);
        if (stripRole != "User") missing.Add("DocumentTabStrip.axaml: the root");

        await Assert.That(missing).IsEmpty().Because("give it WindowDecorationProperties.ElementRole=\"User\" (or its caption role)");
    }

    [Test]
    public async Task The_bar_is_the_drag_area_and_the_caption_buttons_carry_their_roles_and_names()
    {
        XDocument doc = XDocument.Load(ShellSource("TitleBar.axaml"));
        XElement Named(string name) => doc.Descendants().Single(e => (string?)e.Attribute("Name") == name);

        using (Assert.Multiple())
        {
            await Assert.That((string?)Named("Bar").Attribute(RoleAttribute)).IsEqualTo("TitleBar");
            await Assert.That((string?)Named("MinimizeButton").Attribute(RoleAttribute)).IsEqualTo("MinimizeButton");
            await Assert.That((string?)Named("MaximizeButton").Attribute(RoleAttribute)).IsEqualTo("MaximizeButton");
            await Assert.That((string?)Named("CloseButton").Attribute(RoleAttribute)).IsEqualTo("CloseButton");
            foreach (string name in new[] { "MinimizeButton", "MaximizeButton", "CloseButton", "LogoButton", "AddButton", "WorkspaceSwitch" })
                await Assert.That((string?)Named(name).Attribute("AutomationProperties.Name")).IsNotNull().Because(name + " is named for screen readers");
        }
    }

    private static Window Host(ShellChromeViewModel chrome)
    {
        Window window = new() { Width = 1440, Height = 120, Content = new TitleBar { Mode = ChromeMode.Extended, DataContext = chrome, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top } };
        window.Show();
        Settle();
        return window;
    }

    private static void Settle()
    {
        for (int i = 0; i < 2; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static T Find<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException(name + " not found");

    private static Point CentreOf(Visual target, Visual root) =>
        target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), root) ?? throw new InvalidOperationException("not in the window");

    private static (ShellChromeViewModel Chrome, TabDocument[] Tabs) Chrome(params (DocumentKind Kind, string Title, bool Dirty)[] tabs)
    {
        TestUi ui = new();
        DocumentsViewModel documents = new(ui, null);
        TabDocument[] opened = [.. tabs.Select(t => new TabDocument(ui, t.Kind, t.Title, t.Dirty))];
        foreach (TabDocument tab in opened) documents.Add(tab);
        if (opened.Length > 0) documents.Activate(opened[0]);
        return (new ShellChromeViewModel(documents), opened);
    }

    [Test]
    public async Task Empty_the_bar_says_no_documents_and_offers_connect()
    {
        var shown = await HeadlessSession.Run(() =>
        {
            (ShellChromeViewModel chrome, _) = Chrome();
            List<ShellRequest> requests = [];
            chrome.Requested += requests.Add;
            Window window = Host(chrome);
            try
            {
                TitleBar bar = (TitleBar)window.Content!;
                Find<Button>(bar, "ConnectButton").Command!.Execute(ShellRequest.Connect);
                foreach (string name in new[] { "LogoButton", "AddButton" })
                {
                    Point at = CentreOf(Find<Button>(bar, name), window);
                    window.MouseDown(at, MouseButton.Left);
                    window.MouseUp(at, MouseButton.Left);
                    Settle();
                }
                return (Height: bar.Bounds.Height,
                    NoDocuments: Find<TextBlock>(bar, "NoDocuments").IsEffectivelyVisible,
                    Tabs: Find<DocumentTabStrip>(bar, "Tabs").IsVisible,
                    Connect: Find<Button>(bar, "ConnectButton").IsEffectivelyVisible,
                    NoTimeline: Find<Border>(bar, "NoTimeline").IsEffectivelyVisible,
                    Captions: Find<StackPanel>(bar, "CaptionButtons").Children.OfType<Button>().Select(b => b.Bounds.Size).ToArray(),
                    Requests: requests.ToArray());
            }
            finally
            {
                window.Close();
            }
        });

        using (Assert.Multiple())
        {
            await Assert.That(shown.Height).IsEqualTo(36);
            await Assert.That(shown.NoDocuments).IsTrue();
            await Assert.That(shown.Tabs).IsFalse();
            await Assert.That(shown.Connect).IsTrue();
            await Assert.That(shown.NoTimeline).IsTrue();
            await Assert.That(shown.Captions).IsEquivalentTo(new[] { new Size(46, 36), new Size(46, 36), new Size(46, 36) }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(shown.Requests).IsEquivalentTo(new[] { ShellRequest.Connect, ShellRequest.AppMenu, ShellRequest.Open }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task Tabs_select_on_click_and_close_on_the_glyph_or_a_middle_click()
    {
        var seen = await HeadlessSession.Run(() =>
        {
            (ShellChromeViewModel chrome, TabDocument[] t) = Chrome((DocumentKind.Live, "Live · 0x3A21", false), (DocumentKind.File, "chat-repro.nntl", false),
                (DocumentKind.File, "server-all.nntl", false));
            Window window = Host(chrome);
            try
            {
                TitleBar bar = (TitleBar)window.Content!;
                DocumentTabStrip strip = Find<DocumentTabStrip>(bar, "Tabs");
                bool emptyHidden = !Find<TextBlock>(bar, "NoDocuments").IsVisible;
                TabStripItem first = strip.ContainerFor(t[0])!;
                double tabHeight = first.Bounds.Height;
                bool firstSelected = first.IsSelected;

                Point second = CentreOf(strip.ContainerFor(t[1])!, window);
                window.MouseDown(second, MouseButton.Left);
                window.MouseUp(second, MouseButton.Left);
                Settle();
                DocumentViewModel? afterClick = chrome.Documents.Active;

                Find<Button>(strip.ContainerFor(t[2])!, "CloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Settle();
                bool glyphClosed = !chrome.Documents.Documents.Contains(t[2]);

                Point firstAt = CentreOf(strip.ContainerFor(t[0])!, window);
                window.MouseDown(firstAt, MouseButton.Middle);
                window.MouseUp(firstAt, MouseButton.Middle);
                Settle();
                bool middleClosed = !chrome.Documents.Documents.Contains(t[0]);
                return (emptyHidden, tabHeight, firstSelected, afterClick, glyphClosed, middleClosed, Left: chrome.Documents.Documents.ToArray(), t);
            }
            finally
            {
                window.Close();
            }
        });

        using (Assert.Multiple())
        {
            await Assert.That(seen.emptyHidden).IsTrue();
            await Assert.That(seen.tabHeight).IsEqualTo(24);
            await Assert.That(seen.firstSelected).IsTrue().Because("the active document's tab is selected");
            await Assert.That(seen.afterClick).IsSameReferenceAs(seen.t[1]);
            await Assert.That(seen.glyphClosed).IsTrue();
            await Assert.That(seen.middleClosed).IsTrue();
            await Assert.That(seen.Left.Length).IsEqualTo(1);
            await Assert.That(seen.t[0].Disposed && seen.t[2].Disposed).IsTrue();
        }
    }

    [Test]
    public async Task Compare_shows_the_checkboxes_and_disables_a_third()
    {
        var seen = await HeadlessSession.Run(() =>
        {
            (ShellChromeViewModel chrome, TabDocument[] t) = Chrome((DocumentKind.File, "a.nntl", false), (DocumentKind.File, "b.nntl", false), (DocumentKind.Live, "Live · 0x0001", false));
            Window window = Host(chrome);
            try
            {
                DocumentTabStrip strip = Find<DocumentTabStrip>((Visual)window.Content!, "Tabs");
                CheckBox Box(int i) => Find<CheckBox>(strip.ContainerFor(t[i])!, "CompareBox");
                bool hiddenInInspect = !Box(0).IsVisible;
                chrome.Workspace.Value = Workspace.Compare;
                Settle();
                bool shownInCompare = Box(0).IsEffectivelyVisible && Box(2).IsEffectivelyVisible;

                // The checkbox drives the inclusion through its two-way binding.
                Box(0).IsChecked = true;
                Box(1).IsChecked = true;
                Settle();
                return (hiddenInInspect, shownInCompare, chrome.Documents.ComparedCount, ThirdEnabled: Box(2).IsEnabled, FirstEnabled: Box(0).IsEnabled,
                    Name: AutomationProperties.GetName(Box(2)));
            }
            finally
            {
                window.Close();
            }
        });

        using (Assert.Multiple())
        {
            await Assert.That(seen.hiddenInInspect).IsTrue();
            await Assert.That(seen.shownInCompare).IsTrue();
            await Assert.That(seen.ComparedCount).IsEqualTo(2);
            await Assert.That(seen.ThirdEnabled).IsFalse();
            await Assert.That(seen.FirstEnabled).IsTrue();
            await Assert.That(seen.Name).IsEqualTo("Include in Compare");
        }
    }

    [Test]
    public async Task The_main_window_cycles_tabs_on_ctrl_tab_and_reports_its_layout()
    {
        var seen = await HeadlessSession.Run(() =>
        {
            using ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(), AppTheme.Light);
            TestUi ui = new();
            DocumentsViewModel documents = new(ui, null);
            TabDocument a = new(ui, DocumentKind.File, "a.nntl"), b = new(ui, DocumentKind.File, "b.nntl"), c = new(ui, DocumentKind.File, "c.nntl");
            documents.Add(a);
            documents.Add(b);
            documents.Add(c);
            MainWindowViewModel model = new(themes, null, documents);
            MainWindow window = new(ChromeMode.Extended) { DataContext = model, Width = 1180, Height = 760 };
            window.Show();
            try
            {
                Settle();
                ShellLayoutState narrow = model.Chrome.Layout;
                window.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);
                window.KeyRelease(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);
                DocumentViewModel? forward = documents.Active;
                window.KeyPress(Key.Tab, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Tab, null);
                window.KeyRelease(Key.Tab, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Tab, null);
                DocumentViewModel? backward = documents.Active;
                window.Width = 1440;
                Settle();
                return (narrow, Wide: model.Chrome.Layout, forward, backward, a, b, Bar: window.FindControl<TitleBar>("TitleBar")!.Bounds.Height,
                    window.WindowDecorations, window.ExtendClientAreaToDecorationsHint);
            }
            finally
            {
                window.Close();
            }
        });

        using (Assert.Multiple())
        {
            await Assert.That(seen.narrow).IsEqualTo(ShellLayoutState.RightPaneClosed);
            await Assert.That(seen.Wide).IsEqualTo(ShellLayoutState.Full);
            await Assert.That(seen.forward?.Title).IsEqualTo(seen.b.Title).Because("Ctrl+Tab: the most recent other document");
            await Assert.That(seen.backward?.Title).IsEqualTo(seen.a.Title).Because("Ctrl+Shift+Tab: the least recent");
            await Assert.That(seen.Bar).IsEqualTo(36);
            await Assert.That(seen.WindowDecorations).IsEqualTo(WindowDecorations.BorderOnly);
            await Assert.That(seen.ExtendClientAreaToDecorationsHint).IsTrue();
        }
    }

    [Test]
    public async Task Undecorated_the_window_has_no_os_frame_and_grips_on_its_edges()
    {
        var seen = await HeadlessSession.Run(() =>
        {
            MainWindow window = new(ChromeMode.Undecorated) { Width = 1200, Height = 760 };
            window.Show();
            try
            {
                Settle();
                Panel grips = window.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "ResizeGrips");
                WindowDecorationsElementRole[] roles = [.. grips.Children.Select(g => WindowDecorationProperties.GetElementRole(g))];
                Button maximize = Find<Button>(window, "MaximizeButton");
                maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Settle();
                return (window.WindowDecorations, window.ExtendClientAreaToDecorationsHint, roles, State: window.WindowState,
                    Restore: Find<PathIcon>(window, "RestoreGlyph").IsVisible, Name: AutomationProperties.GetName(maximize), GripsShown: grips.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });

        using (Assert.Multiple())
        {
            await Assert.That(seen.WindowDecorations).IsEqualTo(WindowDecorations.None);
            await Assert.That(seen.ExtendClientAreaToDecorationsHint).IsFalse();
            await Assert.That(seen.roles.Length).IsEqualTo(8);
            await Assert.That(seen.roles).Contains(WindowDecorationsElementRole.ResizeSE);
            await Assert.That(seen.State).IsEqualTo(WindowState.Maximized);
            await Assert.That(seen.Restore).IsTrue();
            await Assert.That(seen.Name).IsEqualTo("Restore");
            await Assert.That(seen.GripsShown).IsFalse().Because("a maximized window is not resized by its edges");
        }
    }
}
