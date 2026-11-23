using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using NexNet.Inspector.App.Controls.Primitives;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>One gallery section: a heading and the control(s) it shows, typically every state side by side.</summary>
public sealed record GallerySection(string Title, Func<Control> Build);

/// <summary>
/// The control gallery capture scenario: control work orders (buttons, inputs, menus, chips, ...) add a section per
/// control to <see cref="Sections"/>, so one capture shows every control state in both themes for review against the
/// package's control PNGs.
/// </summary>
public static class ControlGallery
{
    /// <summary>Tall enough for many sections; raise it when the gallery outgrows it.</summary>
    public const double Height = 10450 + DataTableGallery.Height + SessionListGallery.Height + InspectGallery.TraceHeight
                                + CompareDetailGallery.Height;

    /// <summary>The sections, in display order. Add new controls here.</summary>
    public static readonly List<GallerySection> Sections =
    [
        new("Type", Typography),
        new("Colour tokens", Swatches),
        new("Editor (AvaloniaEdit)", Editor),
        .. PrimitiveGallery.Sections(),
        new("Menu", MenuGallery.Build),
        new("Scroll rail", ScrollRailGallery.Build),
        .. ChromeGallery.Sections(),
        .. HomeGallery.Sections(),
        new("Timeline", TimelineGallery.Build),
        new("Data table", DataTableGallery.Build),
        new("Data table · session list", SessionListGallery.Build),
        new("Ladder", LadderGallery.Build),
        new("Hex view", HexGallery.Build),
        .. InspectGallery.Sections(),
        new("Compare · field diff + hex A ⇄ B", CompareDetailGallery.Build),
    ];

    public static Window CreateWindow(CaptureContext context)
    {
        StackPanel body = new() { Spacing = 24, Margin = new Thickness(24) };
        foreach (GallerySection section in Sections)
        {
            StackPanel block = new() { Spacing = 8 };
            block.Children.Add(Text(section.Title.ToUpperInvariant(), "FontSize.CapsLabel", "Brush.Text.Secondary", FontWeight.SemiBold));
            block.Children.Add(section.Build());
            body.Children.Add(block);
        }

        Window window = new() { Content = body };
        Bind(window, TemplatedControl.BackgroundProperty, "Brush.Surface.App");
        Bind(window, TemplatedControl.ForegroundProperty, "Brush.Text.Primary");
        Bind(window, TemplatedControl.FontFamilyProperty, "Font.Ui");
        Bind(window, TemplatedControl.FontSizeProperty, "FontSize.Body");
        return window;
    }

    /// <summary>A text block whose size, colour and family follow the tokens (and so the theme).</summary>
    public static TextBlock Text(string text, string sizeKey, string brushKey, FontWeight weight = FontWeight.Regular, string fontKey = "Font.Ui")
    {
        TextBlock block = new() { Text = text, FontWeight = weight };
        Bind(block, TextBlock.FontSizeProperty, sizeKey);
        Bind(block, TextBlock.ForegroundProperty, brushKey);
        Bind(block, TextBlock.FontFamilyProperty, fontKey);
        return block;
    }

    /// <summary>Binds <paramref name="property"/> to a token as a DynamicResource does, so a theme switch repaints it.</summary>
    public static void Bind(Control control, AvaloniaProperty property, string key) =>
        control.Bind(property, control.GetResourceObservable(key));

    private static Control Typography()
    {
        StackPanel panel = new() { Spacing = 6 };
        panel.Children.Add(Text("NexNet Inspector · display 20", "FontSize.Display", "Brush.Text.Primary", FontWeight.SemiBold));
        panel.Children.Add(Text("Connect to inspector · large 14 medium", "FontSize.Large", "Brush.Text.Primary", FontWeight.Medium));
        panel.Children.Add(Text("Pick any one connected session · body 12", "FontSize.Body", "Brush.Text.Primary"));
        panel.Children.Add(Text("Recent endpoints · meta 10.5 bold", "FontSize.Meta", "Brush.Text.Secondary", FontWeight.Bold));
        panel.Children.Add(Text("tcp://10.0.4.12:7711 · 00:00:01.250 · 0x2A · mono data 11", "FontSize.Data", "Brush.Text.Secondary", fontKey: "Font.Mono"));
        panel.Children.Add(Text("Tertiary hint text", "FontSize.Small", "Brush.Text.Tertiary"));
        return panel;
    }

    /// <summary>Every brush a drawn control can use (ThemeBrush), as a labelled swatch on a panel.</summary>
    private static Control Swatches()
    {
        WrapPanel panel = new() { ItemSpacing = 8, LineSpacing = 8 };
        foreach (ThemeBrush brush in Enum.GetValues<ThemeBrush>())
        {
            string key = ThemeBrushes.Key(brush);
            Border swatch = new() { Width = 128, Height = 28, BorderThickness = new Thickness(1) };
            Bind(swatch, Border.BackgroundProperty, key);
            Bind(swatch, Border.BorderBrushProperty, "Brush.Border.Default");
            StackPanel cell = new() { Spacing = 2, Width = 128 };
            cell.Children.Add(swatch);
            cell.Children.Add(Text(key["Brush.".Length..], "FontSize.Meta", "Brush.Text.Secondary", fontKey: "Font.Mono"));
            panel.Children.Add(cell);
        }

        return panel;
    }

    private static Control Editor()
    {
        TextEditor editor = new()
        {
            Width = 640,
            Height = 90,
            ShowLineNumbers = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            Text = "0000  4E 58 4E 54 01 00 00 00  2A 00 10 00 03 68 69 21\n0010  00 01 02 03 04 05 06 07  08 09 0A 0B 0C 0D 0E 0F\n0020  FF FE FD FC",
        };
        Bind(editor, TemplatedControl.FontFamilyProperty, "Font.Mono");
        Bind(editor, TemplatedControl.FontSizeProperty, "FontSize.Small");
        Bind(editor, TemplatedControl.BackgroundProperty, "Brush.Surface.Panel");
        Bind(editor, TemplatedControl.ForegroundProperty, "Brush.Text.Primary");
        return editor;
    }
}
