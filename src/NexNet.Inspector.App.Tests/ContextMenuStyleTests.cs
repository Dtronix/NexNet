using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Controls;
using Path = Avalonia.Controls.Shapes.Path;

namespace NexNet.Inspector.App.Tests;

/// <summary>
/// Every menu is the design's menu surface (ported from Foreman's ContextMenuStyleTests, re-keyed to PKG controls/menu):
/// surface.panel in a 1 px border.strong outline, 8 px corners, shadow.menu, 4 px padding; 26 px items with 5 px corners,
/// an 18 px icon / check column, a 12 px Font.Ui label in text.primary, a state.selected fill on hover and selection, a 1 px
/// direction.inbound ring on keyboard focus, the InputGesture as a right-aligned mono 10.5 px tertiary hint, a tertiary ▸ on
/// a submenu, border.default separators and tertiary disabled items; plus the header, group-heading and embedded rows and
/// the 280 px app variant. Unlike Foreman, a MenuFlyout takes the same look. The live App, headless, in both themes.
/// </summary>
[NotInParallel("avalonia")]   // the headless session and Application.Current's theme are process-wide
public sealed class ContextMenuStyleTests
{
    private static readonly Dictionary<string, (Color Dark, Color Light)> Expected = new()
    {
        ["Brush.Surface.Panel"] = (Color.Parse("#1B1F26"), Color.Parse("#FFFFFF")),
        ["Brush.State.Selected"] = (Color.Parse("#283150"), Color.Parse("#E3E8FE")),
        ["Brush.Border.Strong"] = (Color.Parse("#3C4250"), Color.Parse("#B9BFC8")),
    };

    private static Color? ColorOf(object? value) => UiPump.ColorOf(value);

    /// <summary>The probe menu: a header, Pin (checked, P), Expand (Space), Copy (Ctrl+C), a disabled item and a submenu.</summary>
    private sealed record Probe(Window Window, ContextMenu Menu, MenuItem Header, Separator Separator, MenuItem Pin, MenuItem Expand,
        MenuItem Copy, MenuItem Disabled, MenuItem Sub, ThemeVariant Theme)
    {
        public Color? Token(string key) => UiPump.Token(key, Theme);
    }

    private static Probe Build(Window window, ThemeVariant theme)
    {
        MenuItem header = new() { Header = "Pipe 3·1 FileUpload · raw", Classes = { "header" } };
        Separator separator = new();
        MenuItem pin = new() { Header = "Pin lane", InputGesture = new KeyGesture(Key.P), ToggleType = MenuItemToggleType.CheckBox, IsChecked = true };
        MenuItem expand = new() { Header = "Expand lane", InputGesture = new KeyGesture(Key.Space), ToggleType = MenuItemToggleType.CheckBox };
        MenuItem copy = new() { Header = "Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
        MenuItem disabled = new() { Header = "Close pipe in view", InputGesture = new KeyGesture(Key.W, KeyModifiers.Control), IsEnabled = false };
        MenuItem sub = new() { Header = "Decode selection as", Items = { new MenuItem { Header = "Auto (schema)" }, new MenuItem { Header = "UTF-8 text" } } };
        ContextMenu menu = new() { Items = { header, separator, pin, expand, new Separator(), copy, disabled, sub } };
        return new Probe(window, menu, header, separator, pin, expand, copy, disabled, sub, theme);
    }

    /// <summary>Opens a probe menu on a live window in <paramref name="theme"/>, lets it lay out, and hands it to <paramref name="body"/>.</summary>
    private static Task<T> WithMenu<T>(ThemeVariant theme, Func<Probe, T> body) => HeadlessSession.Run(() => UiPump.InTheme(theme, () =>
    {
        Window window = new() { Width = 600, Height = 500 };
        window.Show();
        Probe probe = Build(window, theme);
        window.ContextMenu = probe.Menu;
        try
        {
            probe.Menu.Open(window);
            UiPump.Frames();
            if (!probe.Menu.IsOpen || probe.Sub.Bounds.Height <= 0) throw new InvalidOperationException("the probe menu did not open and lay out");
            return body(probe);
        }
        finally
        {
            probe.Menu.Close();
            window.ContextMenu = null;
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }));

    private static TextBlock? Label(Control item, string text) =>
        item.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible && t.Text == text);

    /// <summary>The shown text block that is not the header: the gesture hint.</summary>
    private static TextBlock? Hint(MenuItem item) =>
        item.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.IsEffectivelyVisible && t.Bounds.Width > 0 && t.Text is { Length: > 0 } s && s != (string?)item.Header);

    /// <summary>Whether any of the item's template borders is filled with <paramref name="fill"/>.</summary>
    private static bool Filled(Control item, Color? fill) =>
        fill is not null && item.GetSelfAndVisualDescendants().OfType<Visual>().Any(v => v switch
        {
            Border b => ColorOf(b.Background) == fill,
            ContentPresenter p => ColorOf(p.Background) == fill,
            Panel p => ColorOf(p.Background) == fill,
            _ => false,
        });

    private static T? Part<T>(Control owner, string name) where T : Control =>
        owner.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

    private static double LeftIn(Visual child, Visual owner) => child.TranslatePoint(default, owner)?.X ?? double.NaN;

    [Test]
    [Arguments("Dark")]
    [Arguments("Light")]
    public async Task The_menu_tokens_hold_the_designs_colours(string variant)
    {
        ThemeVariant theme = UiPump.Variant(variant);
        Dictionary<string, Color?> seen = await HeadlessSession.Run(() => Expected.ToDictionary(e => e.Key, e => UiPump.Token(e.Key, theme)));
        using (Assert.Multiple())
        {
            foreach ((string key, (Color dark, Color light)) in Expected)
                await Assert.That(seen[key]).IsEqualTo(variant == "Dark" ? dark : light).Because($"{key} in {variant}");
        }
    }

    [Test]
    [Arguments("Dark")]
    [Arguments("Light")]
    public async Task A_context_menu_is_the_menu_surface(string variant)
    {
        List<string> wrong = await WithMenu(UiPump.Variant(variant), p =>
        {
            List<string> w = [];
            ContextMenu m = p.Menu;
            if (ColorOf(m.Background) != p.Token("Brush.Surface.Panel")) w.Add($"background {ColorOf(m.Background)} is not surface.panel");
            if (ColorOf(m.BorderBrush) != p.Token("Brush.Border.Strong")) w.Add($"border {ColorOf(m.BorderBrush)} is not border.strong");
            if (ColorOf(m.Foreground) != p.Token("Brush.Text.Primary")) w.Add($"text {ColorOf(m.Foreground)} is not text.primary");
            if (m.BorderThickness != new Thickness(1)) w.Add($"border thickness {m.BorderThickness}");
            if (m.CornerRadius != new CornerRadius(8)) w.Add($"corner radius {m.CornerRadius}");
            if (m.Padding != new Thickness(4)) w.Add($"padding {m.Padding}");
            if (m.MinWidth != MenuMetrics.MinWidth) w.Add($"min width {m.MinWidth}");
            if (Part<Border>(m, "PART_Chrome") is not { } chrome) w.Add("the template has no PART_Chrome");
            else
            {
                if (ColorOf(chrome.Background) != ColorOf(m.Background)) w.Add("the template does not draw the background");
                if (!Equals(chrome.BoxShadow, UiPump.Resource("Shadow.Menu", p.Theme))) w.Add($"shadow {chrome.BoxShadow} is not shadow.menu");
                if (Math.Abs(chrome.Bounds.Width - m.Bounds.Width) > 0.5) w.Add($"the surface is {chrome.Bounds.Width} px of the menu's {m.Bounds.Width}");
            }

            if (m.Bounds.Width < MenuMetrics.MinWidth) w.Add($"width {m.Bounds.Width} under {MenuMetrics.MinWidth}");
            // The design's rows touch: 26 px apart, no gap.
            if (Math.Abs(p.Expand.Bounds.Top - p.Pin.Bounds.Bottom) > 0.5) w.Add($"gap between rows {p.Expand.Bounds.Top - p.Pin.Bounds.Bottom}");
            return w;
        });
        await Assert.That(wrong).IsEmpty();
    }

    [Test]
    [Arguments("Dark")]
    [Arguments("Light")]
    public async Task An_item_is_26_px_with_a_12_px_ui_label_in_text_primary_after_the_icon_column(string variant)
    {
        List<string> wrong = await WithMenu(UiPump.Variant(variant), p =>
        {
            List<string> w = [];
            foreach (MenuItem item in new[] { p.Pin, p.Expand, p.Copy, p.Disabled, p.Sub })
            {
                if (Math.Abs(item.Bounds.Height - 26) > 0.5) w.Add($"{item.Header}: height {item.Bounds.Height}");
            }

            MenuItem copy = p.Copy;
            if (copy.Padding != new Thickness(8, 0)) w.Add($"padding {copy.Padding}");
            if (copy.CornerRadius != new CornerRadius(5)) w.Add($"corner radius {copy.CornerRadius}");
            if (Label(copy, "Copy") is not { } label) w.Add("no Copy label");
            else
            {
                if (label.FontSize != 12) w.Add($"label size {label.FontSize}");
                if (label.FontFamily != (UiPump.Resource("Font.Ui", p.Theme) as FontFamily)) w.Add($"label family {label.FontFamily}");
                if (ColorOf(label.Foreground) != p.Token("Brush.Text.Primary")) w.Add($"label {ColorOf(label.Foreground)} is not text.primary");
                // 8 px padding, the 18 px icon column, the 8 px gap.
                if (Math.Abs(LeftIn(label, copy) - 34) > 1) w.Add($"label {LeftIn(label, copy)} px in, not 34");
            }

            return w;
        });
        await Assert.That(wrong).IsEmpty();
    }

    [Test]
    [Arguments("Dark", "pointerover")]
    [Arguments("Light", "pointerover")]
    [Arguments("Dark", "selected")]
    [Arguments("Light", "selected")]
    public async Task A_hovered_or_selected_item_fills_with_state_selected_and_keeps_its_text(string variant, string how)
    {
        List<string> wrong = await WithMenu(UiPump.Variant(variant), p =>
        {
            List<string> w = [];
            Color? fill = p.Token("Brush.State.Selected");
            if (Filled(p.Copy, fill)) w.Add("a resting item is already filled");
            if (how == "pointerover") ((IPseudoClasses)p.Copy.Classes).Set(":pointerover", true);
            else p.Copy.IsSelected = true;
            UiPump.Frames();
            if (!Filled(p.Copy, fill)) w.Add($"the {how} item is not filled with state.selected");
            if (ColorOf(Label(p.Copy, "Copy")?.Foreground) != p.Token("Brush.Text.Primary")) w.Add($"the {how} label is not text.primary");
            if (Part<Border>(p.Copy, "PART_FocusRing") is { IsVisible: true }) w.Add($"the {how} item shows the keyboard ring");
            return w;
        });
        await Assert.That(wrong).IsEmpty();
    }

    [Test]
    [Arguments("Dark")]
    [Arguments("Light")]
    public async Task Keyboard_focus_adds_a_1_px_inbound_ring_over_the_fill(string variant)
    {
        List<string> wrong = await WithMenu(UiPump.Variant(variant), p =>
        {
            List<string> w = [];
            if (!p.Copy.Focus(NavigationMethod.Directional)) w.Add("the item took no focus");
            UiPump.Frames();
            if (!p.Copy.Classes.Contains(":focus-visible")) w.Add("no :focus-visible");
            if (!Filled(p.Copy, p.Token("Brush.State.Selected"))) w.Add("the focused item is not filled");
            if (Part<Border>(p.Copy, "PART_FocusRing") is not { } ring) return [.. w, "no PART_FocusRing"];
            if (!ring.IsEffectivelyVisible) w.Add("the ring is hidden");
            if (ring.BorderThickness != new Thickness(1)) w.Add($"ring {ring.BorderThickness}");
            if (ColorOf(ring.BorderBrush) != p.Token("Brush.Direction.Inbound")) w.Add($"ring {ColorOf(ring.BorderBrush)} is not direction.inbound");
            if (ring.CornerRadius != new CornerRadius(5)) w.Add($"ring radius {ring.CornerRadius}");
            return w;
        });
        await Assert.That(wrong).IsEmpty();
    }

    [Test]
    public async Task The_down_key_moves_the_selection_and_skips_the_header()
    {
        string seen = await WithMenu(ThemeVariant.Dark, p =>
        {
            p.Menu.Focus(NavigationMethod.Directional);
            UiPump.Frames();
            KeyEventArgs down = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down, Source = p.Menu };
            p.Menu.RaiseEvent(down);
            UiPump.Frames();
            MenuItem? selected = p.Menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.IsSelected);
            return $"{selected?.Header}; header focusable {p.Header.Focusable}";
        });
        await Assert.That(seen).IsEqualTo("Pin lane; header focusable False");
    }

    [Test]
    [Arguments("Dark")]
    [Arguments("Light")]
    public async Task The_input_gesture_is_a_right_aligned_mono_hint_in_text_tertiary(string variant)
    {
        List<string> wrong = await WithMenu(UiPump.Variant(variant), p =>
        {
            List<string> w = [];
            MenuItem copy = p.Copy;
            if (Hint(copy) is not { } hint) return ["no hint shown"];
            if (!(hint.Text ?? "").Contains("Ctrl", StringComparison.Ordinal)) w.Add($"hint reads {hint.Text}");
            if (hint.FontFamily != (UiPump.Resource("Font.Mono", p.Theme) as FontFamily)) w.Add($"hint family {hint.FontFamily}");
            if (hint.FontSize != 10.5) w.Add($"hint size {hint.FontSize}");
            if (ColorOf(hint.Foreground) != p.Token("Brush.Text.Tertiary")) w.Add($"hint {ColorOf(hint.Foreground)} is not text.tertiary");
            double right = copy.Bounds.Width - (LeftIn(hint, copy) + hint.Bounds.Width);
            if (Math.Abs(right - 8) > 1) w.Add($"hint ends {right} px from the item's right edge, not 8");
            if (Hint(p.Expand)?.Text != "Space") w.Add($"Space reads {Hint(p.Expand)?.Text}");
            return w;
        });
        await Assert.That(wrong).IsEmpty();
    }

    [Test]
    [Arguments("Dark")]
    [Arguments("Light")]
    public async Task A_checked_item_shows_an_inbound_check_in_the_icon_column(string variant)
    {
        List<string> wrong = await WithMenu(UiPump.Variant(variant), p =>
        {
            List<string> w = [];
            if (Part<Path>(p.Pin, "PART_CheckPath") is not { } check) return ["no PART_CheckPath"];
            if (!check.IsEffectivelyVisible) w.Add("the checked item shows no check");
            if (ColorOf(check.Stroke) != p.Token("Brush.Direction.Inbound")) w.Add($"check {ColorOf(check.Stroke)} is not direction.inbound");
            if (Part<Path>(p.Expand, "PART_CheckPath") is { IsEffectivelyVisible: true }) w.Add("an unchecked item shows a check");
            if (Part<Panel>(p.Pin, "PART_IconColumn") is not { } column || Math.Abs(column.Bounds.Width - 18) > 0.01) w.Add("the icon column is not 18 px");
            return w;
        });
        await Assert.That(wrong).IsEmpty();
    }

    [Test]
    [Arguments("Dark")]
    [Arguments("Light")]
    public async Task A_separator_is_a_1_px_border_default_line_with_a_3_by_4_margin(string variant)
    {
        List<string> wrong = await WithMenu(UiPump.Variant(variant), p =>
        {
            List<string> w = [];
            Separator s = p.Separator;
            if (Math.Abs(s.Bounds.Height - 1) > 0.01) w.Add($"height {s.Bounds.Height}");
            if (s.Margin != new Thickness(4, 3)) w.Add($"margin {s.Margin}");
            if (ColorOf(s.Background) != p.Token("Brush.Border.Default")) w.Add($"fill {ColorOf(s.Background)} is not border.default");
            return w;
        });
        await Assert.That(wrong).IsEmpty();
    }

    [Test]
    [Arguments("Dark")]
    [Arguments("Light")]
    public async Task A_disabled_item_is_tertiary_with_no_hover_and_a_submenu_shows_a_tertiary_chevron(string variant)
    {
        List<string> wrong = await WithMenu(UiPump.Variant(variant), p =>
        {
            List<string> w = [];
            Color? tertiary = p.Token("Brush.Text.Tertiary");
            if (ColorOf(Label(p.Disabled, "Close pipe in view")?.Foreground) != tertiary) w.Add("the disabled label is not text.tertiary");
            ((IPseudoClasses)p.Disabled.Classes).Set(":pointerover", true);
            UiPump.Frames();
            if (Filled(p.Disabled, p.Token("Brush.State.Selected"))) w.Add("a disabled item takes the hover fill");
            if (Part<Path>(p.Sub, "PART_ChevronPath") is not { IsEffectivelyVisible: true } chevron) w.Add("the submenu item shows no chevron");
            else if (ColorOf(chevron.Fill) != tertiary) w.Add($"chevron {ColorOf(chevron.Fill)} is not text.tertiary");
            if (Part<Path>(p.Copy, "PART_ChevronPath") is { IsEffectivelyVisible: true }) w.Add("a leaf item shows a chevron");
            return w;
        });
        await Assert.That(wrong).IsEmpty();
    }

    [Test]
    [Arguments("Dark")]
    [Arguments("Light")]
    public async Task A_submenu_is_the_same_surface_and_opens_right_level_with_its_parent(string variant)
    {
        List<string> wrong = await WithMenu<List<string>>(UiPump.Variant(variant), p =>
        {
            List<string> w = [];
            p.Sub.IsSubMenuOpen = true;
            UiPump.Frames();
            Popup? popup = Part<Popup>(p.Sub, "PART_Popup");
            if (popup is null) return ["the submenu item has no PART_Popup"];
            if (!popup.IsOpen) w.Add("the submenu popup did not open");
            if (!p.Sub.Classes.Contains(":open") || !Filled(p.Sub, p.Token("Brush.State.Selected"))) w.Add("the open parent is not highlighted");
            if (popup.Child is not Border b) return [.. w, $"the submenu popup's child is {popup.Child?.GetType().Name}, not a Border"];
            if (ColorOf(b.Background) != p.Token("Brush.Surface.Panel")) w.Add($"submenu background {ColorOf(b.Background)} is not surface.panel");
            if (ColorOf(b.BorderBrush) != p.Token("Brush.Border.Strong")) w.Add($"submenu border {ColorOf(b.BorderBrush)} is not border.strong");
            if (b.BorderThickness != new Thickness(1)) w.Add($"submenu border thickness {b.BorderThickness}");
            if (b.CornerRadius != new CornerRadius(8)) w.Add($"submenu corner radius {b.CornerRadius}");
            if (b.Padding != new Thickness(4)) w.Add($"submenu padding {b.Padding}");
            if (!Equals(b.BoxShadow, UiPump.Resource("Shadow.Menu", p.Theme))) w.Add($"submenu shadow {b.BoxShadow} is not shadow.menu");
            MenuItem first = (MenuItem)p.Sub.Items[0]!;
            if (Math.Abs(first.Bounds.Height - 26) > 0.5) w.Add($"first item height {first.Bounds.Height}");
            PixelPoint parent = p.Sub.PointToScreen(default), child = first.PointToScreen(default);
            if (Math.Abs(child.Y - parent.Y) > 1) w.Add($"the first item is {child.Y - parent.Y} px below its parent item");
            PixelPoint menuRight = p.Menu.PointToScreen(new Point(p.Menu.Bounds.Width, 0));
            if (b.PointToScreen(default).X < menuRight.X - 8) w.Add("the submenu does not open to the right");
            p.Sub.IsSubMenuOpen = false;
            return w;
        });
        await Assert.That(wrong).IsEmpty();
    }

    [Test]
    [Arguments("Dark")]
    [Arguments("Light")]
    public async Task The_header_row_is_24_px_of_11_px_secondary_text_that_takes_no_focus_or_pointer(string variant)
    {
        List<string> wrong = await WithMenu(UiPump.Variant(variant), p =>
        {
            List<string> w = [];
            MenuItem h = p.Header;
            if (Math.Abs(h.Bounds.Height - 24) > 0.5) w.Add($"height {h.Bounds.Height}");
            if (Label(h, (string)h.Header!) is not { } text) return [.. w, "no header text"];
            if (text.FontSize != 11) w.Add($"size {text.FontSize}");
            if (ColorOf(text.Foreground) != p.Token("Brush.Text.Secondary")) w.Add($"text {ColorOf(text.Foreground)} is not text.secondary");
            if (Math.Abs(LeftIn(text, h) - 8) > 1) w.Add($"text {LeftIn(text, h)} px in, not 8");
            if (h.Focusable || h.IsHitTestVisible) w.Add("the header can take focus or the pointer");
            ((IPseudoClasses)h.Classes).Set(":pointerover", true);
            UiPump.Frames();
            if (Filled(h, p.Token("Brush.State.Selected"))) w.Add("the header fills on hover");
            return w;
        });
        await Assert.That(wrong).IsEmpty();
    }

    /// <summary>The app (logo) menu: 280 px, no icon column, FILE / SESSION / VIEW headings and an embedded Theme row.</summary>
    [Test]
    [Arguments("Dark")]
    [Arguments("Light")]
    public async Task The_app_menu_has_group_headings_an_embedded_row_and_no_icon_column(string variant)
    {
        ThemeVariant theme = UiPump.Variant(variant);
        List<string> wrong = await HeadlessSession.Run(() => UiPump.InTheme(theme, () =>
        {
            List<string> w = [];
            Window window = new() { Width = 600, Height = 500 };
            window.Show();
            Border segmented = new() { Width = 120, Height = 22, Focusable = true };
            MenuItem group = new() { Header = "File", Classes = { "group" } };
            MenuItem open = new() { Header = "Open recording…", InputGesture = new KeyGesture(Key.O, KeyModifiers.Control) };
            MenuItem row = new() { Header = segmented, Classes = { "embedded" } };
            ContextMenu menu = new() { Classes = { "app" }, Items = { group, open, new Separator(), row } };
            window.ContextMenu = menu;
            try
            {
                menu.Open(window);
                UiPump.Frames();
                if (Math.Abs(menu.Bounds.Width - MenuMetrics.AppWidth) > 0.5) w.Add($"width {menu.Bounds.Width}");
                if (Part<Border>(menu, "PART_Chrome") is not { } chrome || Math.Abs(chrome.Bounds.Width - MenuMetrics.AppWidth) > 0.5) w.Add("the surface is not 280 px");
                if (Label(group, "FILE") is not { } heading) w.Add("the heading is not drawn in capitals");
                else
                {
                    if (heading.FontSize != 10) w.Add($"heading size {heading.FontSize}");
                    if (heading.FontWeight != FontWeight.Bold) w.Add($"heading weight {heading.FontWeight}");
                    if (Math.Abs(heading.LetterSpacing - 0.5) > 0.001) w.Add($"heading tracking {heading.LetterSpacing}");
                    if (UiPump.ColorOf(heading.Foreground) != UiPump.Token("Brush.Text.Tertiary", theme)) w.Add("heading is not text.tertiary");
                    if (Label(open, "Open recording…") is { } label && Math.Abs(LeftIn(heading, menu) - LeftIn(label, menu)) > 1)
                        w.Add($"heading at {LeftIn(heading, menu)}, items at {LeftIn(label, menu)}");
                }

                if ((string?)group.Header != "File") w.Add("the heading's header text changed");
                if (group.Focusable) w.Add("the heading takes focus");
                if (Label(open, "Open recording…") is { } openLabel && Math.Abs(LeftIn(openLabel, open) - 8) > 1)
                    w.Add($"app item label {LeftIn(openLabel, open)} px in: the icon column is still there");
                if (Math.Abs(row.Bounds.Height - 28) > 0.5) w.Add($"embedded row height {row.Bounds.Height}");
                if (!segmented.IsEffectivelyVisible || segmented.Bounds.Width <= 0) w.Add("the embedded control is not shown");
                if (!row.StaysOpenOnClick) w.Add("the embedded row closes the menu when used");
                ((IPseudoClasses)row.Classes).Set(":pointerover", true);
                UiPump.Frames();
                if (Filled(row, UiPump.Token("Brush.State.Selected", theme))) w.Add("the embedded row fills on hover");
                row.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                UiPump.Frames();
                if (!menu.IsOpen) w.Add("using the embedded row closed the menu");
                return w;
            }
            finally
            {
                menu.Close();
                window.ContextMenu = null;
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }));
        await Assert.That(wrong).IsEmpty();
    }

    /// <summary>A MenuFlyout (the logo menu, select popups) takes the same surface and items as a ContextMenu.</summary>
    [Test]
    [Arguments("Dark")]
    [Arguments("Light")]
    public async Task A_menu_flyout_is_the_same_surface(string variant)
    {
        ThemeVariant theme = UiPump.Variant(variant);
        List<string> wrong = await HeadlessSession.Run(() => UiPump.InTheme(theme, () =>
        {
            List<string> w = [];
            Button anchor = new() { Content = "Logo", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            Window window = new() { Width = 600, Height = 500, Content = anchor };
            window.Show();
            MenuItem item = new() { Header = "Settings", InputGesture = new KeyGesture(Key.S, KeyModifiers.Control) };
            MenuFlyout flyout = new() { Items = { item } };
            try
            {
                flyout.ShowAt(anchor);
                UiPump.Frames();
                MenuFlyoutPresenter? presenter = item.FindAncestorOfType<MenuFlyoutPresenter>();
                if (presenter is null) return ["no presenter"];
                if (ColorOf(presenter.Background) != UiPump.Token("Brush.Surface.Panel", theme)) w.Add($"background {ColorOf(presenter.Background)}");
                if (ColorOf(presenter.BorderBrush) != UiPump.Token("Brush.Border.Strong", theme)) w.Add($"border {ColorOf(presenter.BorderBrush)}");
                if (presenter.CornerRadius != new CornerRadius(8) || presenter.Padding != new Thickness(4)) w.Add($"radius {presenter.CornerRadius} padding {presenter.Padding}");
                if (Math.Abs(item.Bounds.Height - 26) > 0.5) w.Add($"item height {item.Bounds.Height}");
                if (Hint(item) is not { FontSize: 10.5 }) w.Add("the item's hint is not the menu's");
                return w;
            }
            finally
            {
                flyout.Hide();
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }));
        await Assert.That(wrong).IsEmpty();
    }

    /// <summary>The menu and its items keep the roles a screen reader reads (a guard: true on the base too).</summary>
    [Test]
    public async Task The_restyled_menu_keeps_its_automation_roles()
    {
        List<string> wrong = await WithMenu(ThemeVariant.Dark, p =>
        {
            List<string> w = [];
            if (ControlAutomationPeer.CreatePeerForElement(p.Menu).GetAutomationControlType() != AutomationControlType.Menu) w.Add("menu role");
            if (ControlAutomationPeer.CreatePeerForElement(p.Copy).GetAutomationControlType() != AutomationControlType.MenuItem) w.Add("menuitem role");
            if (ControlAutomationPeer.CreatePeerForElement(p.Sub).GetAutomationControlType() != AutomationControlType.MenuItem) w.Add("submenu menuitem role");
            return w;
        });
        await Assert.That(wrong).IsEmpty();
    }
}
