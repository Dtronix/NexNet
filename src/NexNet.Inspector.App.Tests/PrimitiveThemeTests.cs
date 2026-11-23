using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Checks;
using NexNet.Inspector.App.Controls;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.App.Tests.Capture;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Tests;

/// <summary>
/// The primitive control themes (I14) against the package's control READMEs and OQ1/OQ2: sizes, ring and border
/// thicknesses, and the token each state takes, read from the realized templates in a headless window.
/// </summary>
[NotInParallel("avalonia")]
public sealed class PrimitiveThemeTests
{
    private static Window Show(Control content, ThemeVariant? variant = null)
    {
        Window window = new() { Width = 640, Height = 240, Content = content, RequestedThemeVariant = variant ?? ThemeVariant.Light };
        window.Show();
        Settle();
        return window;
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static T Part<T>(Visual control, string name)
        where T : Control =>
        control.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"{name} ({typeof(T).Name}) is not in the template");

    /// <summary>A token's colour, read on the session's UI thread (the application lives there).</summary>
    private static Task<Color> Token(string key, ThemeVariant? variant = null) =>
        HeadlessSession.Run(() =>
        {
            HeadlessSession.App.TryGetResource(key, variant ?? ThemeVariant.Light, out object? value);
            return value switch
            {
                Color colour => colour,
                ISolidColorBrush brush => brush.Color,
                _ => throw new InvalidOperationException($"{key} is not a colour"),
            };
        });

    private static Color? ColourOf(IBrush? brush) => (brush as ISolidColorBrush)?.Color;

    private static void Use(Control control, string theme) => ControlGallery.Bind(control, StyledElement.ThemeProperty, theme);

    [Test]
    public async Task Button_focus_ring_is_2_px_inbound_1_px_outside()
    {
        (bool visible, Thickness thickness, Thickness margin, Color? brush, bool clips, bool hiddenUnfocused) = await HeadlessSession.Run(() =>
        {
            Button button = new() { Content = "Cancel" };
            Window window = Show(button);
            try
            {
                Border ring = Part<Border>(button, "PART_FocusRing");
                bool hidden = !ring.IsVisible;
                PrimitiveGallery.Force(button, ":focus-visible");
                return (ring.IsVisible, ring.BorderThickness, ring.Margin, ColourOf(ring.BorderBrush), button.ClipToBounds, hidden);
            }
            finally
            {
                window.Close();
            }
        });

        using (Assert.Multiple())
        {
            await Assert.That(hiddenUnfocused).IsTrue();
            await Assert.That(visible).IsTrue();
            await Assert.That(thickness).IsEqualTo(new Thickness(2));
            await Assert.That(margin).IsEqualTo(new Thickness(-3));
            await Assert.That(brush).IsEqualTo(await Token("Color.Direction.Inbound"));
            await Assert.That(clips).IsFalse();
        }
    }

    [Test]
    public async Task Button_sizes_follow_the_tokens()
    {
        (Size sm, Size md, Size caption, Size split) = await HeadlessSession.Run(() =>
        {
            Button small = new() { Content = "Save .nntl…" };
            Button medium = new() { Content = "Cancel", Classes = { "md" } };
            Button captionButton = new() { Content = "x", Classes = { "caption" } };
            Button splitButton = new() { Content = "◫ Single", Classes = { "split" } };
            StackPanel panel = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Children = { small, medium, captionButton, splitButton } };
            Window window = Show(panel);
            try
            {
                return (small.Bounds.Size, medium.Bounds.Size, captionButton.Bounds.Size, splitButton.Bounds.Size);
            }
            finally
            {
                window.Close();
            }
        });

        using (Assert.Multiple())
        {
            await Assert.That(sm.Height).IsEqualTo(24);
            await Assert.That(md.Height).IsEqualTo(28);
            await Assert.That(caption).IsEqualTo(new Size(46, 36));
            await Assert.That(split.Height).IsEqualTo(20);
        }
    }

    [Test]
    public async Task Button_states_take_the_OQ1_fills()
    {
        var r = await HeadlessSession.Run(() =>
        {
            Button hover = new() { Content = "a" };
            Button pressed = new() { Content = "b" };
            Button primaryHover = new() { Content = "c", Classes = { "primary" } };
            Button primaryPressed = new() { Content = "d", Classes = { "primary" } };
            Button close = new() { Content = "e", Classes = { "caption", "caption-close" } };
            Button disabled = new() { Content = "f", Classes = { "primary" }, IsEnabled = false };
            Button record = new() { Content = "00:42", Classes = { "record" } };
            StackPanel panel = new() { Children = { hover, pressed, primaryHover, primaryPressed, close, disabled, record } };
            Window window = Show(panel);
            try
            {
                PrimitiveGallery.Force(hover, ":pointerover");
                PrimitiveGallery.Force(pressed, ":pressed");
                PrimitiveGallery.Force(primaryHover, ":pointerover");
                PrimitiveGallery.Force(primaryPressed, ":pressed");
                PrimitiveGallery.Force(close, ":pointerover");
                return (Hover: ColourOf(hover.Background), Pressed: ColourOf(pressed.Background),
                    PrimaryHover: ColourOf(primaryHover.Background), PrimaryPressed: ColourOf(primaryPressed.Background),
                    Close: ColourOf(close.Background), CloseGlyph: ColourOf(close.Foreground),
                    DisabledFill: ColourOf(disabled.Background), DisabledText: ColourOf(disabled.Foreground), DisabledOpacity: disabled.Opacity,
                    RecordBorder: ColourOf(record.BorderBrush), RecordDot: Part<Ellipse>(record, "PART_RecordDot").IsVisible,
                    SecondaryDot: Part<Ellipse>(hover, "PART_RecordDot").IsVisible);
            }
            finally
            {
                window.Close();
            }
        });

        using (Assert.Multiple())
        {
            await Assert.That(r.Hover).IsEqualTo(await Token("Color.Surface.Raised"));
            await Assert.That(r.Pressed).IsEqualTo(await Token("Color.State.Selected"));
            await Assert.That(r.PrimaryHover).IsEqualTo(await Token("Brush.Button.PrimaryHover"));
            await Assert.That(r.PrimaryPressed).IsEqualTo(await Token("Brush.Button.PrimaryPressed"));
            await Assert.That(r.Close).IsEqualTo(await Token("Brush.Caption.CloseHover"));
            await Assert.That(r.CloseGlyph).IsEqualTo(Color.Parse("#FFFFFF"));
            await Assert.That(r.DisabledFill).IsEqualTo(await Token("Color.Surface.Raised"));
            await Assert.That(r.DisabledText).IsEqualTo(await Token("Color.Text.Tertiary"));
            await Assert.That(r.DisabledOpacity).IsEqualTo(0.7);
            await Assert.That(r.RecordBorder).IsEqualTo(await Token("Color.Status.Recording"));
            await Assert.That(r.RecordDot).IsTrue();
            await Assert.That(r.SecondaryDot).IsFalse();
        }
    }

    [Test]
    public async Task Primary_hover_and_pressed_are_the_design_brightness_of_inbound()
    {
        foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Color inbound = await Token("Color.Direction.Inbound", variant);
            await Assert.That(Token("Brush.Button.PrimaryHover", variant)).IsEqualTo(Brightness(inbound, 1.08));
            await Assert.That(Token("Brush.Button.PrimaryPressed", variant)).IsEqualTo(Brightness(inbound, 0.92));
        }

        // CSS filter: brightness(k) multiplies each sRGB channel and clamps.
        static Color Brightness(Color c, double k) =>
            Color.FromRgb(Channel(c.R, k), Channel(c.G, k), Channel(c.B, k));

        static byte Channel(byte v, double k) => (byte)Math.Min(255, Math.Round(v * k, MidpointRounding.AwayFromZero));
    }

    [Test]
    public async Task Checkbox_box_is_14_px_with_a_1_5_px_strong_border()
    {
        var r = await HeadlessSession.Run(() =>
        {
            CheckBox unchecked_ = new() { Content = "Only differences", IsChecked = false };
            CheckBox checked_ = new() { Content = "Follow live", IsChecked = true };
            ControlHint.SetText(checked_, "(open pipes, pending invocations)");
            Window window = Show(new StackPanel { Children = { unchecked_, checked_ } });
            try
            {
                Border box = Part<Border>(unchecked_, "PART_Box");
                Border on = Part<Border>(checked_, "PART_Box");
                PrimitiveGallery.Force(unchecked_, ":focus-visible");
                Border ring = Part<Border>(unchecked_, "PART_FocusRing");
                return (Box: box.Bounds.Size, Thickness: box.BorderThickness, Radius: box.CornerRadius, Border: ColourOf(box.BorderBrush),
                    Fill: ColourOf(on.Background), Check: Part<Path>(checked_, "PART_Check").IsVisible, NoCheck: Part<Path>(unchecked_, "PART_Check").IsVisible,
                    Ring: ring.IsVisible, RingThickness: ring.BorderThickness, Hint: Part<TextBlock>(checked_, "PART_Hint").IsVisible,
                    NoHint: Part<TextBlock>(unchecked_, "PART_Hint").IsVisible, HelpText: AutomationProperties.GetHelpText(checked_));
            }
            finally
            {
                window.Close();
            }
        });

        using (Assert.Multiple())
        {
            await Assert.That(r.Box).IsEqualTo(new Size(14, 14));
            await Assert.That(r.Thickness).IsEqualTo(new Thickness(1.5));
            await Assert.That(r.Radius).IsEqualTo(new CornerRadius(3));
            await Assert.That(r.Border).IsEqualTo(await Token("Color.Border.Strong"));
            await Assert.That(r.Fill).IsEqualTo(await Token("Color.Direction.Inbound"));
            await Assert.That(r.Check).IsTrue();
            await Assert.That(r.NoCheck).IsFalse();
            await Assert.That(r.Ring).IsTrue();
            await Assert.That(r.RingThickness).IsEqualTo(new Thickness(2));
            await Assert.That(r.Hint).IsTrue();
            await Assert.That(r.NoHint).IsFalse();
            await Assert.That(r.HelpText).IsEqualTo("(open pipes, pending invocations)");
        }
    }

    [Test]
    public async Task Segmented_sizes_and_the_selected_segment()
    {
        var r = await HeadlessSession.Run(() =>
        {
            ListBox md = new() { ItemsSource = new[] { "Inspect", "Compare" }, SelectedIndex = 0 };
            ListBox sm = new() { ItemsSource = new[] { "Heat", "Packed" }, SelectedIndex = 1, Classes = { "sm" } };
            ListBox dialog = new() { ItemsSource = new[] { "Drop · show gap markers", "Block" }, SelectedIndex = 0, Classes = { "dialog" }, Width = 400 };
            foreach (ListBox list in new[] { md, sm, dialog }) Use(list, "Segmented");
            Window window = Show(new StackPanel { Spacing = 8, Children = { md, sm, dialog } });
            try
            {
                Control[] dialogItems = [.. dialog.GetRealizedContainers()];
                ListBoxItem selected = (ListBoxItem)md.ContainerFromIndex(0)!;
                ListBoxItem other = (ListBoxItem)md.ContainerFromIndex(1)!;
                return (Md: md.Bounds.Height, Sm: sm.Bounds.Height, Dialog: dialog.Bounds.Height,
                    EqualWidths: dialogItems.Length == 2 && Math.Abs(dialogItems[0].Bounds.Width - dialogItems[1].Bounds.Width) < 0.01,
                    Weight: selected.FontWeight, OtherWeight: other.FontWeight, Fill: ColourOf(selected.Background),
                    Ring: Part<Border>(selected, "PART_SelectedRing").IsVisible, OtherRing: Part<Border>(other, "PART_SelectedRing").IsVisible,
                    Track: ColourOf(md.Background), SmFont: ((ListBoxItem)sm.ContainerFromIndex(0)!).FontSize);
            }
            finally
            {
                window.Close();
            }
        });

        using (Assert.Multiple())
        {
            await Assert.That(r.Md).IsEqualTo(24);
            await Assert.That(r.Sm).IsEqualTo(18);
            await Assert.That(r.Dialog).IsEqualTo(28);
            await Assert.That(r.EqualWidths).IsTrue();
            await Assert.That(r.Weight).IsEqualTo(FontWeight.SemiBold);
            await Assert.That(r.OtherWeight).IsEqualTo(FontWeight.Normal);
            await Assert.That(r.Fill).IsEqualTo(await Token("Color.Surface.Panel"));
            await Assert.That(r.Ring).IsTrue();
            await Assert.That(r.OtherRing).IsFalse();
            await Assert.That(r.Track).IsEqualTo(await Token("Color.Surface.Raised"));
            await Assert.That(r.SmFont).IsEqualTo(11);
        }
    }

    [Test]
    public async Task Segmented_follows_an_enum_choice_both_ways()
    {
        (int afterClick, InspectorThemeProbe value, int afterValue) = await HeadlessSession.Run(() =>
        {
            EnumChoice<InspectorThemeProbe> choice = new([(InspectorThemeProbe.Dark, "Dark"), (InspectorThemeProbe.Light, "Light"), (InspectorThemeProbe.System, "System")], InspectorThemeProbe.System);
            ListBox list = new() { ItemsSource = choice.Labels };
            Use(list, "Segmented");
            list.Bind(SelectingItemsControl.SelectedIndexProperty, new Avalonia.Data.Binding(nameof(EnumChoice<InspectorThemeProbe>.SelectedIndex)) { Source = choice, Mode = Avalonia.Data.BindingMode.TwoWay });
            Window window = Show(list);
            try
            {
                list.SelectedIndex = 1;
                Settle();
                int clicked = choice.SelectedIndex;
                InspectorThemeProbe v = choice.Value;
                choice.Value = InspectorThemeProbe.Dark;
                Settle();
                return (clicked, v, list.SelectedIndex);
            }
            finally
            {
                window.Close();
            }
        });

        using (Assert.Multiple())
        {
            await Assert.That(afterClick).IsEqualTo(1);
            await Assert.That(value).IsEqualTo(InspectorThemeProbe.Light);
            await Assert.That(afterValue).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Text_input_field_focus_and_error_borders()
    {
        var r = await HeadlessSession.Run(() =>
        {
            TextBox plain = new() { Text = "10.0.4.12:7711", Classes = { "mono" } };
            TextBox focused = new() { Text = "x" };
            TextBox failed = new() { Text = "y" };
            TextBox search = new() { Classes = { "search" } };
            TextBox token = new() { Text = "secret", Classes = { "password" } };
            Window window = Show(new StackPanel { Spacing = 8, Children = { plain, focused, failed, search, token } });
            try
            {
                PrimitiveGallery.Force(focused, ":focus");
                DataValidationErrors.SetErrors(failed, ["Can't reach 10.0.4.12:7711 — connection refused"]);
                Settle();
                Border plainBorder = Part<Border>(plain, "PART_BorderElement");
                Border focusBorder = Part<Border>(focused, "PART_BorderElement");
                Border errorBorder = Part<Border>(failed, "PART_BorderElement");
                plain.TryFindResource("Font.Mono", out object? mono);
                return (Height: plainBorder.Bounds.Height, Fill: ColourOf(plainBorder.Background), Border: ColourOf(plainBorder.BorderBrush),
                    Thickness: plainBorder.BorderThickness, FocusThickness: focusBorder.BorderThickness, FocusBrush: ColourOf(focusBorder.BorderBrush),
                    ErrorThickness: errorBorder.BorderThickness, ErrorBrush: ColourOf(errorBorder.BorderBrush),
                    ErrorLine: failed.Bounds.Height > plain.Bounds.Height, Mono: ReferenceEquals(plain.FontFamily, mono),
                    Search: search.Bounds.Height, Password: token.PasswordChar);
            }
            finally
            {
                window.Close();
            }
        });

        using (Assert.Multiple())
        {
            await Assert.That(r.Height).IsEqualTo(28);
            await Assert.That(r.Fill).IsEqualTo(await Token("Color.Surface.Raised"));
            await Assert.That(r.Border).IsEqualTo(await Token("Color.Border.Default"));
            await Assert.That(r.Thickness).IsEqualTo(new Thickness(1));
            await Assert.That(r.FocusThickness).IsEqualTo(new Thickness(1.5));
            await Assert.That(r.FocusBrush).IsEqualTo(await Token("Color.Direction.Inbound"));
            await Assert.That(r.ErrorThickness).IsEqualTo(new Thickness(1.5));
            await Assert.That(r.ErrorBrush).IsEqualTo(await Token("Color.Status.Error"));
            await Assert.That(r.ErrorLine).IsTrue();
            await Assert.That(r.Mono).IsTrue();
            await Assert.That(r.Search).IsEqualTo(24);
            await Assert.That(r.Password).IsEqualTo('•');
        }
    }

    [Test]
    public async Task Select_field_caret_and_menu_items()
    {
        var r = await HeadlessSession.Run(() =>
        {
            ComboBox field = new() { ItemsSource = new[] { "TCP", "TLS" }, SelectedIndex = 0 };
            ComboBox focused = new() { ItemsSource = new[] { "TCP" }, SelectedIndex = 0 };
            ComboBox small = new() { ItemsSource = new[] { "Filter" }, SelectedIndex = 0, Classes = { "sm" } };
            ComboBoxItem current = new() { Content = "TCP", IsSelected = true };
            ComboBoxItem other = new() { Content = "TLS" };
            Window window = Show(new StackPanel { Spacing = 8, Children = { field, focused, small, current, other } });
            try
            {
                PrimitiveGallery.Force(focused, ":focus-visible");
                return (Size: field.Bounds.Size, Caret: Part<Path>(field, "PART_Caret").IsVisible, Fill: ColourOf(field.Background),
                    FocusThickness: focused.BorderThickness, FocusBrush: ColourOf(focused.BorderBrush),
                    SmallHeight: small.Bounds.Height, SmallBorder: small.BorderThickness,
                    ItemHeight: current.Bounds.Height, Check: Part<Path>(current, "PART_Check").IsVisible,
                    NoCheck: Part<Path>(other, "PART_Check").IsVisible, ItemFill: ColourOf(current.Background),
                    Popup: Part<Popup>(field, "PART_Popup").Child is Border { CornerRadius.TopLeft: 8, BorderThickness.Left: 1 });
            }
            finally
            {
                window.Close();
            }
        });

        using (Assert.Multiple())
        {
            await Assert.That(r.Size.Height).IsEqualTo(28);
            await Assert.That(r.Size.Width).IsGreaterThanOrEqualTo(120);
            await Assert.That(r.Caret).IsTrue();
            await Assert.That(r.Fill).IsEqualTo(await Token("Color.Surface.Raised"));
            await Assert.That(r.FocusThickness).IsEqualTo(new Thickness(1.5));
            await Assert.That(r.FocusBrush).IsEqualTo(await Token("Color.Direction.Inbound"));
            await Assert.That(r.SmallHeight).IsEqualTo(20);
            await Assert.That(r.SmallBorder).IsEqualTo(new Thickness(0));
            await Assert.That(r.ItemHeight).IsEqualTo(26);
            await Assert.That(r.Check).IsTrue();
            await Assert.That(r.NoCheck).IsFalse();
            await Assert.That(r.ItemFill).IsEqualTo(await Token("Color.State.Selected"));
            await Assert.That(r.Popup).IsTrue();
        }
    }

    [Test]
    public async Task Radio_card_and_row_selection()
    {
        var r = await HeadlessSession.Run(() =>
        {
            RadioButton server = new() { Content = "Server", IsChecked = true, GroupName = "t" };
            RadioButton client = new() { Content = "Client", GroupName = "t" };
            RadioButton row = new() { Content = "Use the embedded schema", IsChecked = true, GroupName = "s" };
            RadioButton plainRow = new() { Content = "Raw bytes only", GroupName = "s" };
            Use(server, "RadioCard");
            Use(client, "RadioCard");
            Use(row, "RadioRow");
            Use(plainRow, "RadioRow");
            ControlHint.SetText(server, "Pick any one connected session");
            Window window = Show(new StackPanel { Spacing = 8, Children = { server, client, row, plainRow } });
            try
            {
                PrimitiveGallery.Force(client, ":pointerover");
                PrimitiveGallery.Force(server, ":focus-visible");
                return (Thickness: server.BorderThickness, Border: ColourOf(server.BorderBrush), Fill: ColourOf(server.Background),
                    Dot: Part<Border>(server, "PART_Radio").BorderThickness, HoverBorder: ColourOf(client.BorderBrush),
                    ClientDot: Part<Border>(client, "PART_Radio").BorderThickness, Ring: Part<Border>(server, "PART_FocusRing").IsVisible,
                    Hint: Part<TextBlock>(server, "PART_Hint").Text, RowHeight: row.Bounds.Height,
                    RowWeight: Part<ContentPresenter>(row, "PART_ContentPresenter").FontWeight,
                    PlainWeight: Part<ContentPresenter>(plainRow, "PART_ContentPresenter").FontWeight);
            }
            finally
            {
                window.Close();
            }
        });

        using (Assert.Multiple())
        {
            await Assert.That(r.Thickness).IsEqualTo(new Thickness(1.5));
            await Assert.That(r.Border).IsEqualTo(await Token("Color.Direction.Inbound"));
            await Assert.That(r.Fill).IsEqualTo(await Token("Color.State.Selected"));
            await Assert.That(r.Dot).IsEqualTo(new Thickness(4));
            await Assert.That(r.HoverBorder).IsEqualTo(await Token("Color.Border.Strong"));
            await Assert.That(r.ClientDot).IsEqualTo(new Thickness(1.5));
            await Assert.That(r.Ring).IsTrue();
            await Assert.That(r.Hint).IsEqualTo("Pick any one connected session");
            await Assert.That(r.RowHeight).IsEqualTo(24);
            await Assert.That(r.RowWeight).IsEqualTo(FontWeight.Bold);
            await Assert.That(r.PlainWeight).IsEqualTo(FontWeight.Normal);
        }
    }

    [Test]
    public async Task Flag_chips_map_each_flag_to_its_letter_and_token()
    {
        using (Assert.Multiple())
        {
            await Assert.That(FlagChip.DisplayOrder.ToArray()).IsEquivalentTo(
                new[] { FrameFlags.TransportSplit, FrameFlags.NexNetSplit, FrameFlags.Cancel, FrameFlags.Redacted, FrameFlags.Incomplete },
                TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(string.Concat(FlagChip.DisplayOrder.ToArray().Select(FlagChip.Letter))).IsEqualTo("TNXR…");
            await Assert.That(FlagChip.Fill(FrameFlags.TransportSplit)).IsEqualTo(ThemeBrush.SplitTransport);
            await Assert.That(FlagChip.Fill(FrameFlags.NexNetSplit)).IsEqualTo(ThemeBrush.SplitNexnet);
            await Assert.That(FlagChip.Fill(FrameFlags.Cancel)).IsEqualTo(ThemeBrush.StatusError);
            await Assert.That(FlagChip.Fill(FrameFlags.Redacted)).IsEqualTo(ThemeBrush.TextTertiary);
            await Assert.That(FlagChip.Fill(FrameFlags.Incomplete)).IsEqualTo(ThemeBrush.TextTertiary);
            await Assert.That(() => FlagChip.Letter(FrameFlags.TransportSplit | FrameFlags.Cancel)).Throws<ArgumentOutOfRangeException>();
        }
    }

    [Test]
    public async Task Flag_chip_paints_its_token_and_repaints_on_a_theme_switch()
    {
        (Size size, Color light, Color dark, string? name) = await HeadlessSession.Run(() =>
        {
            FlagChip chip = new() { Flag = FrameFlags.TransportSplit, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            Window window = Show(chip, ThemeVariant.Light);
            try
            {
                Color Sample()
                {
                    using WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame");
                    // The left edge, half way down: inside the fill, clear of the rounded corners and the letter.
                    return FramePixels.At(frame, 1, 6);
                }

                Color l = Sample();
                window.RequestedThemeVariant = ThemeVariant.Dark;
                Settle();
                return (chip.Bounds.Size, l, Sample(), AutomationProperties.GetName(chip));
            }
            finally
            {
                window.Close();
            }
        });

        using (Assert.Multiple())
        {
            await Assert.That(size.Height).IsEqualTo(FlagChip.ChipHeight);
            await Assert.That(size.Width).IsEqualTo(FlagChip.MinChipWidth);
            await Assert.That(light).IsEqualTo(await Token("Color.Split.Transport", ThemeVariant.Light));
            await Assert.That(dark).IsEqualTo(await Token("Color.Split.Transport", ThemeVariant.Dark));
            await Assert.That(name).StartsWith("Transport split");
        }
    }

    [Test]
    public async Task Transport_op_chip_and_count_badge()
    {
        (Size op, Size badge, string? opName) = await HeadlessSession.Run(() =>
        {
            TransportOpChip chip = new() { OpId = 4471, IsSplit = true, HorizontalAlignment = HorizontalAlignment.Left };
            CountBadge count = new() { Text = "3 rows · 9", HorizontalAlignment = HorizontalAlignment.Left };
            Window window = Show(new StackPanel { Children = { chip, count } });
            try
            {
                return (chip.Bounds.Size, count.Bounds.Size, AutomationProperties.GetName(chip));
            }
            finally
            {
                window.Close();
            }
        });

        using (Assert.Multiple())
        {
            await Assert.That(TransportOpChip.Label(4471, TapDirection.Inbound)).IsEqualTo("#4471");
            await Assert.That(TransportOpChip.Label(3104, TapDirection.Outbound)).IsEqualTo("w3104");
            await Assert.That(TransportOpChip.Inks(TapDirection.Inbound, isSplit: true)).IsEqualTo((ThemeBrush.SplitTransport, ThemeBrush.SplitTransport));
            await Assert.That(TransportOpChip.Inks(TapDirection.Outbound, isSplit: false)).IsEqualTo((ThemeBrush.BorderDefault, ThemeBrush.DirectionOutbound));
            await Assert.That(TransportOpChip.Inks(TapDirection.Inbound, isSplit: false)).IsEqualTo((ThemeBrush.BorderDefault, ThemeBrush.TextSecondary));
            await Assert.That(op.Height).IsEqualTo(TransportOpChip.ChipHeight);
            await Assert.That(op.Width).IsGreaterThan(20);
            await Assert.That(opName).IsEqualTo("Read #4471, split");
            await Assert.That(badge.Height).IsEqualTo(CountBadge.BadgeHeight);
            await Assert.That(badge.Width).IsGreaterThan(30);
        }
    }

    [Test]
    public async Task The_gallery_has_a_section_per_primitive_control()
    {
        string[] titles = [.. ControlGallery.Sections.Select(s => s.Title)];
        foreach (string expected in new[] { "Button", "Segmented control", "Text input", "Select", "Checkbox", "Radio card", "Chip" })
            await Assert.That(titles).Contains(expected);
    }
}

/// <summary>An enum for the EnumChoice tests, so they do not depend on an app enum's members.</summary>
public enum InspectorThemeProbe : byte
{
    System = 0,
    Light = 1,
    Dark = 2,
}
