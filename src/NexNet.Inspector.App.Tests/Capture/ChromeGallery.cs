using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.Tests.Shell;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views.Shell;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// The window chrome's gallery sections (I16): the title bar in the states its package PNGs show (empty, default, active =
/// the Compare workspace, hover = the logo button held down as while its menu is open) and the document tab strip (default,
/// hover, focus, active = Compare checkboxes, empty), with the package's sample documents. States are forced as
/// pseudo-classes, since a headless capture has no pointer or keyboard.
/// </summary>
public static class ChromeGallery
{
    /// <summary>The title bar's width in the gallery: the capture's width less the gallery's margins.</summary>
    private const double BarWidth = CaptureScenarios.Width - 48;

    public static IEnumerable<GallerySection> Sections()
    {
        yield return new("Window title bar", TitleBars);
        yield return new("Document tab", DocumentTabs);
    }

    private enum Shown : byte
    {
        Empty,
        Default,
        Active,
        Hover,
        Focus,
    }

    /// <summary>The package's three documents (a live session and two files), the first one active.</summary>
    private static ShellChromeViewModel Chrome(bool withDocuments, bool compare)
    {
        TestUi ui = new();
        DocumentsViewModel documents = new(ui, null);
        if (withDocuments)
        {
            TabDocument live = new(ui, DocumentKind.Live, "Live · 0x3A21");
            documents.Add(live);
            documents.Add(new TabDocument(ui, DocumentKind.File, "chat-repro.nntl"));
            documents.Add(new TabDocument(ui, DocumentKind.File, "server-all.nntl"));
            documents.Activate(live);
            if (compare) documents.Documents[1].IsIncludedInCompare = true;
        }

        ShellChromeViewModel chrome = new(documents);
        if (compare) chrome.Workspace.Value = Workspace.Compare;
        return chrome;
    }

    private static Control Captioned(string caption, Control content)
    {
        StackPanel panel = new() { Spacing = 6 };
        panel.Children.Add(ControlGallery.Text(caption, "FontSize.Meta", "Brush.Text.Tertiary", fontKey: "Font.Mono"));
        panel.Children.Add(content);
        return panel;
    }

    private static Control TitleBars()
    {
        StackPanel column = new() { Spacing = 16 };
        column.Children.Add(Captioned("empty", Bar(Shown.Empty)));
        column.Children.Add(Captioned("default", Bar(Shown.Default)));
        column.Children.Add(Captioned("active (Compare)", Bar(Shown.Active)));
        column.Children.Add(Captioned("hover (app menu open)", Bar(Shown.Hover)));
        return column;
    }

    private static Control Bar(Shown state)
    {
        TitleBar bar = new()
        {
            Mode = ChromeMode.Extended,
            Width = BarWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            DataContext = Chrome(withDocuments: state != Shown.Empty, compare: state == Shown.Active),
        };
        if (state == Shown.Hover)
        {
            bar.Loaded += static (sender, _) =>
            {
                Button logo = ((TitleBar)sender!).FindControl<Button>("LogoButton")!;
                PrimitiveGallery.Force(logo, ":pointerover");
                PrimitiveGallery.Force(logo, ":pressed");
            };
        }

        // The package draws the bar with a 1 px border all round; the window gives it only its bottom line.
        Border frame = new() { BorderThickness = new Thickness(1, 1, 1, 0), HorizontalAlignment = HorizontalAlignment.Left, Child = bar };
        ControlGallery.Bind(frame, Border.BorderBrushProperty, "Brush.Border.Default");
        return frame;
    }

    private static Control DocumentTabs()
    {
        WrapPanel panel = new() { ItemSpacing = 24, LineSpacing = 16 };
        panel.Children.Add(Captioned("default", Strip(Shown.Default)));
        panel.Children.Add(Captioned("hover", Strip(Shown.Hover)));
        panel.Children.Add(Captioned("focus", Strip(Shown.Focus)));
        panel.Children.Add(Captioned("active (Compare)", Strip(Shown.Active)));
        panel.Children.Add(Captioned("empty", Strip(Shown.Empty)));
        return panel;
    }

    /// <summary>The strip on a 36 px chrome bar, followed by + (as in the package's document-tab.html).</summary>
    private static Control Strip(Shown state)
    {
        ShellChromeViewModel chrome = Chrome(withDocuments: state != Shown.Empty, compare: state == Shown.Active);
        StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (state == Shown.Empty)
        {
            row.Children.Add(ControlGallery.Text("No documents open", "FontSize.Body", "Brush.Text.Tertiary"));
        }
        else
        {
            DocumentTabStrip strip = new() { DataContext = chrome, IsCompare = chrome.IsCompare };
            if (state is Shown.Hover or Shown.Focus)
            {
                strip.Loaded += (_, _) =>
                {
                    DocumentViewModel target = chrome.Documents.Documents[state == Shown.Hover ? 1 : 0];
                    if (strip.ContainerFor(target) is TabStripItem tab) PrimitiveGallery.Force(tab, state == Shown.Hover ? ":pointerover" : ":focus-visible");
                };
            }

            row.Children.Add(strip);
        }

        Button add = new() { Content = "+", Classes = { "add" } };
        add.Bind(StyledElement.ThemeProperty, add.GetResourceObservable("ChromeButton"));
        row.Children.Add(add);

        Border bar = new() { Height = 36, Padding = new Thickness(8, 0), BorderThickness = new Thickness(1), Child = row, HorizontalAlignment = HorizontalAlignment.Left };
        ControlGallery.Bind(bar, Border.BackgroundProperty, "Brush.Surface.Chrome");
        ControlGallery.Bind(bar, Border.BorderBrushProperty, "Brush.Border.Default");
        return bar;
    }
}
