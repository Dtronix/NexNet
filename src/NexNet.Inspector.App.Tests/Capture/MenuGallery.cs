using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// The menu gallery section (PKG controls/menu, screens inspect-app-menu and context-menus): the lane context menu in each
/// state the package draws (default, hover, keyboard focus, disabled), the hex selection menu with its "Decode selection
/// as" submenu open (active), and the app menu with group headings and the Theme segmented row. The menus are hosted
/// inline, not in popups, so one window capture holds them all; states are set as pseudo-classes.
/// </summary>
public static class MenuGallery
{
    public static Control Build()
    {
        StackPanel rows = new() { Spacing = 16 };
        WrapPanel states = new() { ItemSpacing = 16, LineSpacing = 16 };
        states.Children.Add(Captioned("default", LaneMenu(LaneState.Default)));
        states.Children.Add(Captioned("hover", LaneMenu(LaneState.Hover)));
        states.Children.Add(Captioned("focus", LaneMenu(LaneState.Focus)));
        states.Children.Add(Captioned("disabled", LaneMenu(LaneState.Disabled)));
        rows.Children.Add(states);

        StackPanel second = new() { Orientation = Orientation.Horizontal, Spacing = 16 };
        second.Children.Add(Captioned("active (submenu open)", HexMenus()));
        second.Children.Add(Captioned("app menu", AppMenu()));
        rows.Children.Add(second);
        return rows;
    }

    private enum LaneState
    {
        Default,
        Hover,
        Focus,
        Disabled,
    }

    private static Control Captioned(string caption, Control content)
    {
        StackPanel panel = new() { Spacing = 6, VerticalAlignment = VerticalAlignment.Top };
        panel.Children.Add(ControlGallery.Text(caption, "FontSize.Meta", "Brush.Text.Tertiary"));
        panel.Children.Add(content);
        return panel;
    }

    private static ContextMenu Menu(double width, params Control[] items)
    {
        ContextMenu menu = new() { Width = width, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        foreach (Control item in items) menu.Items.Add(item);
        return menu;
    }

    private static MenuItem Item(string header, Key? key = null, KeyModifiers modifiers = KeyModifiers.None, bool? check = null, string? icon = null)
    {
        MenuItem item = new() { Header = header, InputGesture = key is { } k ? new KeyGesture(k, modifiers) : null };
        if (check is { } c)
        {
            item.ToggleType = MenuItemToggleType.CheckBox;
            item.IsChecked = c;
        }

        if (icon is not null) item.Icon = new TextBlock { Text = icon };
        return item;
    }

    private static void Set(MenuItem item, string pseudo) => ((IPseudoClasses)item.Classes).Set(pseudo, true);

    /// <summary>The context header: a glyph in direction.inbound, the target in bold primary, then secondary detail.</summary>
    private static MenuItem Header(string? glyph, string name, string detail, bool monoDetail = false)
    {
        TextBlock text = new();
        if (glyph is not null)
        {
            Run g = new(glyph + "  ");
            g.Bind(TextElement.ForegroundProperty, text.GetResourceObservable("Brush.Direction.Inbound"));
            text.Inlines!.Add(g);
        }

        Run n = new(name) { FontWeight = FontWeight.Bold };
        n.Bind(TextElement.ForegroundProperty, text.GetResourceObservable("Brush.Text.Primary"));
        text.Inlines!.Add(n);
        Run d = new("  " + detail);
        if (monoDetail) d.Bind(TextElement.FontFamilyProperty, text.GetResourceObservable("Font.Mono"));
        text.Inlines!.Add(d);
        return new MenuItem { Header = text, Classes = { "header" } };
    }

    private static ContextMenu LaneMenu(LaneState state)
    {
        MenuItem hide = Item("Hide lane", Key.H, check: false);
        MenuItem trace = Item("Trace frames in view", Key.T, icon: "⌖");
        MenuItem close = Item("Close pipe in view");
        switch (state)
        {
            case LaneState.Hover:
                Set(trace, ":pointerover");
                break;
            case LaneState.Focus:
                Set(hide, ":selected");
                Set(hide, ":focus-visible");
                break;
            case LaneState.Disabled:
                close.IsEnabled = false;
                break;
        }

        return Menu(250,
            Header("⇄", "Pipe 3·1", "FileUpload · raw"), new Separator(),
            Item("Pin lane", Key.P, check: true), Item("Expand lane", Key.Space, check: false), hide, new Separator(),
            trace, Item("Show in Ladder"), new Separator(),
            close);
    }

    private static Control HexMenus()
    {
        MenuItem decode = new() { Header = "Decode selection as", Items = { new MenuItem { Header = "Auto (schema)" } } };
        Set(decode, ":selected");
        ContextMenu parent = Menu(250,
            Header(null, "16 bytes selected", "1A3F21 – 1A3F30", monoDetail: true), new Separator(),
            Item("Copy as hex", Key.C, KeyModifiers.Control), Item("Copy as C# byte array"), Item("Copy as Base64"), new Separator(),
            decode, Item("Go to frame start", Key.Home), Item("Go to offset…", Key.G, KeyModifiers.Control));

        // The submenu as it opens: to the right, its first item level with "Decode selection as" (popups are not captured).
        ContextMenu child = Menu(180,
            Item("Auto (schema)", check: true), Item("MessagePack value", check: false), Item("UTF-8 text", check: false),
            Item("LEB128 length + item", check: false), new Separator(), Item("Raw only", check: false));
        // The parent item sits 121 px down (border, padding, header, separator, three items, separator); the child's own
        // border and padding (5 px) sit above its first item.
        child.Margin = new Thickness(-6, 116, 0, 0);
        StackPanel pair = new() { Orientation = Orientation.Horizontal };
        pair.Children.Add(parent);
        pair.Children.Add(child);
        return pair;
    }

    private static ContextMenu AppMenu()
    {
        MenuItem schema = new() { Header = "Schema · Chat.nnschema (embedded)", Items = { new MenuItem { Header = "Load schema…" } } };
        Set(schema, ":pointerover");
        ContextMenu menu = Menu(280,
            Group("File"), Item("Open recording…", Key.O, KeyModifiers.Control), Item("Save timeline as .nntl…", Key.S, KeyModifiers.Control),
            new MenuItem { Header = "Recent", Items = { new MenuItem { Header = "chat-repro.nntl" } } }, new Separator(),
            Group("Session"), Item("Connect to inspector…", Key.K, KeyModifiers.Control),
            new MenuItem { Header = "Switch session", Items = { new MenuItem { Header = "0x3A21" } } }, schema, new Separator(),
            Group("View"), ThemeRow(), Item("Command palette", Key.P, KeyModifiers.Control | KeyModifiers.Shift), new Separator(),
            Item("Help & shortcuts", Key.F1));
        menu.Classes.Add("app");
        return menu;
    }

    private static MenuItem Group(string text) => new() { Header = text, Classes = { "group" } };

    /// <summary>
    /// The embedded Theme row (I28 wires it). It hosts the "Segmented" ListBox theme when that is merged (I14); until then a
    /// plain stand-in drawn from the tokens, so the row's layout can still be reviewed.
    /// </summary>
    private static MenuItem ThemeRow()
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        TextBlock label = new() { Text = "Theme", VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(label);
        Control segmented = Application.Current!.TryGetResource("Segmented", null, out object? theme) && theme is ControlTheme segmentedTheme
            ? new ListBox { Theme = segmentedTheme, ItemsSource = new[] { "Dark", "Light", "System" }, SelectedIndex = 1 }
            : StandIn();
        Grid.SetColumn(segmented, 1);
        row.Children.Add(segmented);
        return new MenuItem { Header = row, Classes = { "embedded" } };
    }

    private static Control StandIn()
    {
        StackPanel options = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (string option in new[] { "Dark", "Light", "System" })
        {
            Border cell = new() { Padding = new Thickness(8, 2), CornerRadius = new CornerRadius(4) };
            TextBlock text = ControlGallery.Text(option, "FontSize.Small", option == "Light" ? "Brush.Text.Primary" : "Brush.Text.Secondary");
            cell.Child = text;
            if (option == "Light")
            {
                ControlGallery.Bind(cell, Border.BackgroundProperty, "Brush.Surface.Panel");
                ControlGallery.Bind(cell, Border.BorderBrushProperty, "Brush.Border.Default");
                cell.BorderThickness = new Thickness(1);
            }

            options.Children.Add(cell);
        }

        Border track = new() { Padding = new Thickness(2), CornerRadius = new CornerRadius(6), Child = options };
        ControlGallery.Bind(track, Border.BackgroundProperty, "Brush.Surface.Raised");
        return track;
    }
}
