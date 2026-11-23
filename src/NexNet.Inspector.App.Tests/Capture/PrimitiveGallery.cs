using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using NexNet.Inspector.App.Controls;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>A control state the gallery shows, matching the package's PNG names (control--state--theme.png).</summary>
public enum GalleryState : byte
{
    Default,
    Hover,
    Focus,
    Active,
    Disabled,
    Error,
    Empty,
}

/// <summary>
/// The gallery sections of the primitive control themes (I14): button, segmented-control, text-input, select,
/// checkbox, radio-card and chip, each in every state its package PNGs show, with the package's sample content. States
/// are forced: pseudo-classes (:pointerover, :focus-visible, :pressed, :error) set on the control, since a headless
/// capture has no pointer or keyboard.
/// </summary>
public static class PrimitiveGallery
{
    public static IEnumerable<GallerySection> Sections()
    {
        yield return new("Button", Buttons);
        yield return new("Segmented control", Segmented);
        yield return new("Text input", TextInputs);
        yield return new("Select", Selects);
        yield return new("Checkbox", CheckBoxes);
        yield return new("Radio card", RadioCards);
        yield return new("Chip", Chips);
    }

    /// <summary>Sets a pseudo-class the way the control would on input (Avalonia's own IPseudoClasses seam).</summary>
    public static void Force(StyledElement element, string pseudoClass) => ((IPseudoClasses)element.Classes).Set(pseudoClass, true);

    private static string Name(GalleryState state) => state switch
    {
        GalleryState.Default => "default",
        GalleryState.Hover => "hover",
        GalleryState.Focus => "focus",
        GalleryState.Active => "active",
        GalleryState.Disabled => "disabled",
        GalleryState.Error => "error",
        GalleryState.Empty => "empty",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    /// <summary>Applies a state to a control: a forced pseudo-class, or disabling it.</summary>
    private static T In<T>(T control, GalleryState state)
        where T : Control
    {
        switch (state)
        {
            case GalleryState.Hover: Force(control, ":pointerover"); break;
            case GalleryState.Focus: Force(control, ":focus-visible"); break;
            // A Button re-reads :pressed from IsPressed when it is attached, so force it once it is loaded.
            case GalleryState.Active: control.Loaded += static (sender, _) => Force((Control)sender!, ":pressed"); break;
            case GalleryState.Disabled: control.IsEnabled = false; break;
        }

        return control;
    }

    /// <summary>The states side by side: each a small label over its sample.</summary>
    private static Control States(ReadOnlySpan<GalleryState> states, Func<GalleryState, Control> build)
    {
        WrapPanel panel = new() { ItemSpacing = 32, LineSpacing = 16 };
        foreach (GalleryState state in states)
        {
            StackPanel cell = new() { Spacing = 6 };
            cell.Children.Add(ControlGallery.Text(Name(state), "FontSize.Meta", "Brush.Text.Tertiary", fontKey: "Font.Mono"));
            cell.Children.Add(build(state));
            panel.Children.Add(cell);
        }

        return panel;
    }

    private static StackPanel Row(double spacing = 10) => new() { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Top };

    private static Control Buttons() => States([GalleryState.Default, GalleryState.Hover, GalleryState.Focus, GalleryState.Active, GalleryState.Disabled], static state =>
    {
        StackPanel row = Row();
        row.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(In(new Button { Content = "Connect", Classes = { "md", "primary" } }, state));
        row.Children.Add(In(new Button { Content = "Cancel", Classes = { "md" } }, state));
        row.Children.Add(In(new Button { Content = "Save .nntl…" }, state));
        row.Children.Add(In(new Button { Content = "00:42", Classes = { "record" } }, state));
        row.Children.Add(In(Caption("Glyph.Caption.Minimize", "Minimize", close: false), state));
        row.Children.Add(In(Caption("Glyph.Caption.Close", "Close", close: true), state));
        row.Children.Add(In(new Button { Content = "◫ Single", Classes = { "split" }, VerticalAlignment = VerticalAlignment.Center }, state));
        return row;
    });

    private static Button Caption(string glyph, string name, bool close)
    {
        PathIcon icon = new() { Width = 10, Height = 10 };
        ControlGallery.Bind(icon, PathIcon.DataProperty, glyph);
        Button button = new() { Content = icon, Classes = { "caption" } };
        if (close) button.Classes.Add("caption-close");
        Avalonia.Automation.AutomationProperties.SetName(button, name);
        return button;
    }

    private static Control Segmented() => States([GalleryState.Default, GalleryState.Hover, GalleryState.Focus, GalleryState.Disabled], static state =>
    {
        StackPanel column = new() { Spacing = 12 };
        StackPanel row = Row();
        row.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(SegmentedList(["Inspect", "Compare"], 0, null, state));
        row.Children.Add(SegmentedList(["Relative", "Wall-clock", "On event · Session ready"], 2, "sm", state));
        row.Children.Add(SegmentedList(["Heat", "Packed"], 0, "sm", state));
        column.Children.Add(row);

        // The dialog row, backed by the enum the Connect dialog binds (InspectorOverflow).
        EnumChoice<InspectorOverflow> overflow = new([(InspectorOverflow.Drop, "Drop · show gap markers"), (InspectorOverflow.Block, "Block · lossless, slows the connection")], InspectorOverflow.Drop);
        ListBox dialog = SegmentedList(overflow.Labels, overflow.SelectedIndex, "dialog", state);
        dialog.Width = 520;
        dialog.HorizontalAlignment = HorizontalAlignment.Left;
        column.Children.Add(dialog);
        return column;
    });

    private static ListBox SegmentedList(IReadOnlyList<string> labels, int selected, string? size, GalleryState state)
    {
        ListBox list = new() { ItemsSource = labels, SelectedIndex = selected };
        ControlGallery.Bind(list, StyledElement.ThemeProperty, "Segmented");
        if (size is not null) list.Classes.Add(size);
        if (state == GalleryState.Disabled) list.IsEnabled = false;
        // The package's hover marks the segment after the first; focus rings the selected one.
        int target = state == GalleryState.Hover ? (selected == 1 ? 0 : 1) : selected;
        string? pseudo = state switch { GalleryState.Hover => ":pointerover", GalleryState.Focus => ":focus-visible", _ => null };
        if (pseudo is not null)
        {
            list.ContainerPrepared += (_, e) =>
            {
                if (e.Index == target) Force(e.Container, pseudo);
            };
        }

        return list;
    }

    private static Control TextInputs() => States([GalleryState.Default, GalleryState.Hover, GalleryState.Focus, GalleryState.Error, GalleryState.Disabled, GalleryState.Empty], static state =>
    {
        StackPanel row = Row();
        bool empty = state == GalleryState.Empty;
        TextBox endpoint = new() { Text = empty ? null : "10.0.4.12:7711", PlaceholderText = "Find method, pipe, collection…", Classes = { "mono" } };
        TextBox token = new() { Text = empty ? null : "secret-token", Classes = { "password" } };
        foreach (TextBox box in new[] { endpoint, token })
        {
            switch (state)
            {
                case GalleryState.Hover: Force(box, ":pointerover"); break;
                case GalleryState.Focus: Force(box, ":focus"); break;
                case GalleryState.Disabled: box.IsEnabled = false; break;
            }
        }

        if (state == GalleryState.Error)
        {
            DataValidationErrors.SetErrors(endpoint, ["Can't reach 10.0.4.12:7711 — connection refused"]);
            Force(token, ":error");
        }

        row.Children.Add(Field("Inspector endpoint", endpoint));
        row.Children.Add(Field("Inspector token", token));
        return row;
    });

    private static Control Field(string label, TextBox box)
    {
        StackPanel field = new() { Width = 300 };
        field.Children.Add(new TextBlock { Text = label, Classes = { "field-label" } });
        field.Children.Add(box);
        return field;
    }

    private static Control Selects() => States([GalleryState.Default, GalleryState.Hover, GalleryState.Focus, GalleryState.Active, GalleryState.Disabled], static state =>
    {
        StackPanel row = Row();
        // The open list is drawn on the window's overlay layer, so the capture shows it; leave it room.
        if (state == GalleryState.Active) row.MinHeight = 210;
        ComboBox transport = new() { ItemsSource = new[] { "TCP", "TLS", "UDS", "QUIC", "WebSocket", "HttpSocket" }, SelectedIndex = 0 };
        ComboBox filter = new() { ItemsSource = new[] { "Filter" }, SelectedIndex = 0, Classes = { "sm" }, VerticalAlignment = VerticalAlignment.Top };
        ComboBox scale = new() { ItemsSource = new[] { "Time-scaled" }, SelectedIndex = 0, Classes = { "sm" }, VerticalAlignment = VerticalAlignment.Top };
        transport.VerticalAlignment = VerticalAlignment.Top;
        foreach (ComboBox box in new[] { transport, filter, scale })
        {
            switch (state)
            {
                case GalleryState.Hover: Force(box, ":pointerover"); break;
                case GalleryState.Focus: Force(box, ":focus-visible"); break;
                case GalleryState.Active when box != transport: Force(box, ":dropdownopen"); break;
                case GalleryState.Disabled: box.IsEnabled = false; break;
            }
        }

        if (state == GalleryState.Active)
        {
            transport.TemplateApplied += static (_, e) =>
            {
                if (e.NameScope.Find<Popup>("PART_Popup") is { } popup) popup.ShouldUseOverlayLayer = true;
            };
            transport.Loaded += static (sender, _) => ((ComboBox)sender!).IsDropDownOpen = true;
        }

        row.Children.Add(transport);
        row.Children.Add(filter);
        row.Children.Add(scale);
        return row;
    });

    private static Control CheckBoxes() => States([GalleryState.Default, GalleryState.Hover, GalleryState.Focus, GalleryState.Disabled], static state =>
    {
        StackPanel column = new() { Spacing = 10 };
        CheckBox snapshot = In(new CheckBox { Content = "Request a state snapshot on attach", IsChecked = true }, state);
        ControlHint.SetText(snapshot, "(open pipes, pending invocations)");
        column.Children.Add(snapshot);
        column.Children.Add(In(new CheckBox { Content = "Only differences", IsChecked = false }, state));
        column.Children.Add(In(new CheckBox { Content = "Follow live", IsChecked = true }, state));
        return column;
    });

    private static int _radioGroup;

    private static Control RadioCards() => States([GalleryState.Default, GalleryState.Hover, GalleryState.Focus, GalleryState.Disabled], static state =>
    {
        int group = ++_radioGroup;
        StackPanel row = Row();
        RadioButton server = Card("Server", "Pick any one connected session", isChecked: true, group);
        RadioButton client = Card("Client", "Its single connection", isChecked: false, group);
        switch (state)
        {
            case GalleryState.Hover: Force(client, ":pointerover"); break;
            case GalleryState.Focus: Force(server, ":focus-visible"); break;
            case GalleryState.Disabled: server.IsEnabled = false; client.IsEnabled = false; break;
        }

        row.Children.Add(server);
        row.Children.Add(client);

        StackPanel rows = new();
        RadioButton embedded = RowOption("Use the embedded schema", "matches what was on the wire", isChecked: true, group);
        if (state == GalleryState.Hover) Force(embedded, ":pointerover");
        if (state == GalleryState.Disabled) embedded.IsEnabled = false;
        rows.Children.Add(embedded);
        rows.Children.Add(RowOption("Use the workspace schema", null, isChecked: false, group));
        rows.Children.Add(RowOption("Raw bytes only", null, isChecked: false, group));
        row.Children.Add(rows);
        return row;
    });

    private static RadioButton Card(string title, string description, bool isChecked, int group)
    {
        RadioButton card = new() { Content = title, IsChecked = isChecked, Width = 240, GroupName = $"target-{group}", VerticalAlignment = VerticalAlignment.Top };
        ControlGallery.Bind(card, StyledElement.ThemeProperty, "RadioCard");
        ControlHint.SetText(card, description);
        return card;
    }

    private static RadioButton RowOption(string label, string? hint, bool isChecked, int group)
    {
        RadioButton row = new() { Content = label, IsChecked = isChecked, GroupName = $"schema-{group}" };
        ControlGallery.Bind(row, StyledElement.ThemeProperty, "RadioRow");
        ControlHint.SetText(row, hint);
        return row;
    }

    private static Control Chips() => States([GalleryState.Default], static _ =>
    {
        StackPanel row = Row();
        row.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(Label("flags"));
        foreach (FrameFlags flag in FlagChip.DisplayOrder) row.Children.Add(new FlagChip { Flag = flag, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(Label("count badge"));
        row.Children.Add(new CountBadge { Text = "3 rows · 9", VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new CountBadge { Text = "7", Tone = CountBadgeTone.Difference, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(Label("transport op"));
        row.Children.Add(new TransportOpChip { OpId = 4466, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TransportOpChip { OpId = 4471, IsSplit = true, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TransportOpChip { OpId = 3104, Direction = TapDirection.Outbound, VerticalAlignment = VerticalAlignment.Center });
        return row;
    });

    private static TextBlock Label(string text)
    {
        TextBlock label = ControlGallery.Text(text, "FontSize.Data", "Brush.Text.Secondary");
        label.Margin = new Thickness(16, 0, 4, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        return label;
    }
}
