using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using NexNet.Inspector.App.Controls;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views.Home;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// The gallery sections of I17's controls, each state as its package PNG shows it (card, banner, dialog), with the
/// package's sample content. States are forced as in <see cref="PrimitiveGallery"/>.
/// </summary>
public static class HomeGallery
{
    public static IEnumerable<GallerySection> Sections()
    {
        yield return new("Card", Cards);
        yield return new("Banner", Banners);
        yield return new("Dialog", Dialogs);
    }

    private static Control Labelled(string state, Control sample)
    {
        StackPanel cell = new() { Spacing = 6 };
        cell.Children.Add(ControlGallery.Text(state, "FontSize.Meta", "Brush.Text.Tertiary", fontKey: "Font.Mono"));
        cell.Children.Add(sample);
        return cell;
    }

    private static Control Cards()
    {
        WrapPanel panel = new() { ItemSpacing = 32, LineSpacing = 24 };
        foreach (GalleryState state in new[] { GalleryState.Default, GalleryState.Hover, GalleryState.Focus, GalleryState.Active, GalleryState.Empty })
            panel.Children.Add(Labelled(state.ToString().ToLowerInvariant(), CardSet(state)));
        return panel;
    }

    /// <summary>The home's two action cards over the Recent endpoints card, 760 px wide (card.html).</summary>
    public static Control CardSet(GalleryState state)
    {
        ActionCard connect = new()
        {
            Title = "Connect to a live session",
            Description = "Attach to the inspector endpoint of a server or client. Only data sent while attached is shown.",
            Shortcut = "Ctrl+K",
        };
        ControlGallery.Bind(connect, ActionCard.IconProperty, "Glyph.Pulse");
        ControlGallery.Bind(connect, ActionCard.IconBrushProperty, "Brush.Direction.Inbound");
        ActionCard open = new()
        {
            Title = "Open a recording",
            Description = "Open a .nntl timeline, or drop one here. Recordings from nexnet-tap or server-side capture.",
            Shortcut = "Ctrl+O",
            IsDropTarget = true,
            DropText = "Drop to open chat-repro.nntl",
        };
        ControlGallery.Bind(open, ActionCard.IconProperty, "Glyph.Folder");
        ControlGallery.Bind(open, ActionCard.IconBrushProperty, "Brush.Direction.Outbound");
        switch (state)
        {
            case GalleryState.Hover: PrimitiveGallery.Force(connect, ":pointerover"); break;
            case GalleryState.Focus: PrimitiveGallery.Force(connect, ":focus-visible"); break;
            case GalleryState.Active: open.IsDragOver = true; break;
        }

        Grid actions = new() { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        actions.Children.Add(connect);
        Grid.SetColumn(open, 2);
        actions.Children.Add(open);

        DateTimeOffset now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        RecentEndpointRow[] rows = state == GalleryState.Empty
            ? []
            :
            [
                new(new RecentEndpoint("tcp://10.0.4.12:7711", InspectorTargetKind.Session, now), "Server", "today"),
                new(new RecentEndpoint("uds:///tmp/chat-inspect.sock", InspectorTargetKind.Client, now.AddDays(-1)), "Client", "yesterday"),
            ];
        Grid lists = new() { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        lists.Children.Add(EndpointList(rows));

        StackPanel set = new() { Width = 760, Spacing = 12 };
        set.Children.Add(actions);
        set.Children.Add(lists);
        return set;
    }

    private static ListCard EndpointList(RecentEndpointRow[] rows)
    {
        ListCard card = new()
        {
            Title = "Recent endpoints",
            EmptyText = "No recent endpoints yet",
            ItemsSource = rows,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<RecentEndpointRow>(static (row, _) =>
            {
                Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("52,8,*,8,Auto") };
                TextBlock kind = ControlGallery.Text(row.Kind, "FontSize.Body", "Brush.Text.Secondary");
                kind.VerticalAlignment = VerticalAlignment.Center;
                TextBlock endpoint = ControlGallery.Text(row.Endpoint, "FontSize.Data", "Brush.Text.Primary", fontKey: "Font.Mono");
                endpoint.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(endpoint, 2);
                TextBlock when = ControlGallery.Text(row.When, "FontSize.Data", "Brush.Text.Tertiary");
                when.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(when, 4);
                grid.Children.Add(kind);
                grid.Children.Add(endpoint);
                grid.Children.Add(when);
                return grid;
            }, supportsRecycling: false),
        };
        return card;
    }

    private static Control Banners()
    {
        StackPanel column = new() { Spacing = 16 };

        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(new Button { Content = "Save .nntl…", Classes = { "primary" } });
        actions.Children.Add(new Button { Content = "Discard" });
        Banner ended = new()
        {
            Kind = BannerKind.SessionEnded,
            Width = 820,
            HorizontalAlignment = HorizontalAlignment.Left,
            Title = "Session 0x3A21 ended",
            Content = "DisconnectGraceful (21) from client · 12:04 attached · 18,248 events · not saved",
            Actions = actions,
        };
        column.Children.Add(Labelled("default", ended));

        TextBlock detail = new() { TextWrapping = TextWrapping.Wrap };
        detail.Inlines!.Add(new Run("Recording: Chat.nnschema · export hash "));
        detail.Inlines.Add(Mono("0x5E21A9C4"));
        detail.Inlines.Add(new Run(". Workspace hash "));
        detail.Inlines.Add(Mono("0x7C0391E2"));
        detail.Inlines.Add(new Run(" (2 methods added)."));
        Banner mismatch = new()
        {
            Kind = BannerKind.SchemaMismatch,
            Width = 620,
            HorizontalAlignment = HorizontalAlignment.Left,
            Title = "The embedded schema differs from your workspace schema",
            Content = detail,
        };
        column.Children.Add(Labelled("error", mismatch));

        const string reached = "Reached 10.0.4.12:7711 · server policy: recording allowed · secrets redacted · schema Chat.nnschema provided";
        column.Children.Add(Labelled("active", Status(StatusTone.Ok, reached)));
        column.Children.Add(Labelled("busy", Status(StatusTone.Busy, "Connecting to 10.0.4.12:7711…")));
        column.Children.Add(Labelled("status error", Status(StatusTone.Error, "Connection refused by 10.0.4.12:7711")));
        return column;
    }

    private static Run Mono(string text)
    {
        Run run = new(text);
        if (Application.Current!.TryGetResource("Font.Mono", null, out object? font) && font is FontFamily mono) run.FontFamily = mono;
        return run;
    }

    private static Banner Status(StatusTone tone, string text) =>
        new() { Kind = BannerKind.Status, Tone = tone, Content = text, Width = 520, HorizontalAlignment = HorizontalAlignment.Left };

    private static Control Dialogs()
    {
        WrapPanel panel = new() { ItemSpacing = 32, LineSpacing = 24 };
        panel.Children.Add(Labelled("default", DialogSample(StatusTone.Ok, "Reached 10.0.4.12:7711 · server policy: recording allowed · secrets redacted", "Connect")));
        panel.Children.Add(Labelled("loading", DialogSample(StatusTone.Busy, "Connecting to 10.0.4.12:7711…", "Connecting…")));
        panel.Children.Add(Labelled("error", DialogSample(StatusTone.Error, "Connection refused by 10.0.4.12:7711", "Connect")));
        return panel;
    }

    /// <summary>dialog.html's sample: a 520 px Connect frame over the scrim, in a 600 × 330 box.</summary>
    public static Control DialogSample(StatusTone tone, string status, string primary)
    {
        Border scrim = new();
        ControlGallery.Bind(scrim, Border.BackgroundProperty, "Brush.Overlay.Scrim");
        ControlGallery.Bind(scrim, Visual.OpacityProperty, "Opacity.Scrim.Dialog");

        TextBox endpoint = new() { Text = "10.0.4.12:7711", Classes = { "mono" } };
        PrimitiveGallery.Force(endpoint, ":focus");
        StackPanel body = new() { Spacing = 10 };
        body.Children.Add(new TextBlock { Text = "Inspector endpoint", Classes = { "field-label" }, Margin = default });
        body.Children.Add(endpoint);
        Banner line = Status(tone, status);
        line.Width = double.NaN;
        line.HorizontalAlignment = HorizontalAlignment.Stretch;
        body.Children.Add(line);

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(new Button { Content = "Cancel", Classes = { "md" } });
        buttons.Children.Add(new Button { Content = primary, Classes = { "md", "primary" } });
        DialogFrame frame = new()
        {
            Title = "Connect to inspector",
            Hint = "Enter to connect · Esc to cancel",
            Width = 520,
            Content = body,
            Footer = buttons,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(40, 20, 0, 0),
        };

        Panel box = new() { Width = 600, Height = 330, ClipToBounds = true };
        ControlGallery.Bind(box, Panel.BackgroundProperty, "Brush.Surface.App");
        box.Children.Add(scrim);
        box.Children.Add(frame);
        return box;
    }
}
