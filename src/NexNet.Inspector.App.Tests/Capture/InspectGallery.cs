using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using NexNet.Inspector.App.Controls;
using NexNet.Inspector.App.Controls.PropertyTree;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// The gallery sections of I23: the pane header (controls/pane-header: default, hover, focus and disabled at 620 px with
/// the package's Ladder content, then the inspector, right-pane and titled variants) and the property tree
/// (controls/property-tree: default, hover, active and empty at 324 px with the package's MessageChunk 2/5 content
/// and its assembly bar). States are forced, as the other galleries do.
/// </summary>
public static class InspectGallery
{
    private static readonly string[] StreamTabs = ["Events", "Ladder", "Hex stream"];
    private static readonly string[] InspectorTabs = ["Decoded", "Message trace"];

    public static IEnumerable<GallerySection> Sections()
    {
        yield return new("Pane header", PaneHeaders);
        yield return new("Property tree", PropertyTrees);
        yield return new("Message trace and handshake (I25)", Traces);
    }

    /// <summary>The height <see cref="Traces"/> adds to the gallery.</summary>
    public const double TraceHeight = 360;

    // The Message trace tab with the package's #812 sample (chunk 2 current, interleaved runs dimmed and indented) and
    // the handshake assembly bar with Greet ▼ current.
    private static Control Traces()
    {
        long ms = System.Diagnostics.Stopwatch.Frequency / 1000;
        long At(double milliseconds) => (long)(milliseconds * ms);
        TraceStep Step(TraceStepKind kind, TapDirection direction, long seq, double time, int bytes, string label, Core.Framing.FrameFlags flags = default,
            int frames = 1) =>
            new(kind, new Core.Session.FrameKey(direction, seq), At(time), bytes, kind == TraceStepKind.Chunk ? 0 : -1, 0x1A3F00, bytes,
                4466, flags == default ? 4466 : 4467, flags, label, frames);
        TraceStep[] steps =
        [
            Step(TraceStepKind.Start, TapDirection.Inbound, 18_200, 1258.102, 7, "MessageStart"),
            Step(TraceStepKind.Chunk, TapDirection.Inbound, 18_210, 1259.880, 4100, "Chunk 1/5", Core.Framing.FrameFlags.TransportSplit),
            Step(TraceStepKind.Interleaved, TapDirection.Inbound, 18_211, 1260.812, 16_392, "↳ pipe 3·1, Ping", frames: 2),
            Step(TraceStepKind.Chunk, TapDirection.Inbound, 18_233, 1262.418, 4100, "Chunk 2/5", Core.Framing.FrameFlags.TransportSplit),
            Step(TraceStepKind.Interleaved, TapDirection.Inbound, 18_234, 1263.611, 16_384, "↳ pipe 3·1"),
            Step(TraceStepKind.Chunk, TapDirection.Inbound, 18_240, 1265.003, 4100, "Chunk 3/5"),
            Step(TraceStepKind.Whole, TapDirection.Outbound, 18_262, 1273.106, 23, "InvocationResult"),
        ];
        TraceStepRow[] rows = [.. steps.Select(s => new TraceStepRow(s, 0, System.Diagnostics.Stopwatch.Frequency))];
        ListBox list = new() { ItemsSource = rows, SelectedIndex = 3, Height = 170 };
        ControlGallery.Bind(list, StyledElement.ThemeProperty, "MessageTraceList");
        StackPanel trace = new() { Margin = new Thickness(0, 4) };
        TextBlock title = ControlGallery.Text("Invocation #812 · SendAttachment  7 frames · 15.0 ms · 3 interleaved", "FontSize.Data", "Brush.Text.Secondary");
        title.Margin = new Thickness(10, 4, 14, 4);
        trace.Children.Add(title);
        trace.Children.Add(list);

        AssemblySegment[] handshake =
        [
            new(AssemblySegmentKind.Preamble, 0, 1, false, 0, TapDirection.Inbound),
            new(AssemblySegmentKind.Greeting, 0, 2, true, 0, TapDirection.Inbound),
            new(AssemblySegmentKind.Preamble, 0, 1, false, 0, TapDirection.Outbound),
            new(AssemblySegmentKind.Greeting, 0, 2, false, 0, TapDirection.Outbound),
        ];
        StackPanel footer = new() { Margin = new Thickness(10, 8, 10, 10) };
        footer.Children.Add(ControlGallery.Text("Handshake  4 frames · 3.1 ms · auth ok", "FontSize.Data", "Brush.Text.Secondary"));
        footer.Children.Add(new AssemblyBar { Segments = handshake, Margin = new Thickness(0, 6) });
        footer.Children.Add(ControlGallery.Text("Preamble and ClientGreeting arrived in one read (#1) · token redacted", "FontSize.Meta", "Brush.Text.Secondary"));

        WrapPanel states = new() { ItemSpacing = 16, LineSpacing = 16 };
        states.Children.Add(Captioned("message trace (chunk 2 current)", Framed(trace)));
        states.Children.Add(Captioned("handshake bar (Greet ▼ current)", Framed(footer)));
        return states;
    }

    private static Control PaneHeaders()
    {
        StackPanel rows = new() { Spacing = 12 };
        rows.Children.Add(Captioned("default", Stream(PaneSyncState.Synced)));
        PaneHeader hover = Stream(PaneSyncState.Synced);
        ForceTab(hover, 0, ":pointerover");
        rows.Children.Add(Captioned("hover", hover));
        PaneHeader focus = Stream(PaneSyncState.Synced);
        ForceTab(focus, 1, ":focus-visible");
        rows.Children.Add(Captioned("focus", focus));
        rows.Children.Add(Captioned("disabled (unsynced)", Stream(PaneSyncState.Unsynced)));
        rows.Children.Add(Captioned("left pane alone (◫ Split)", Stream(PaneSyncState.Synced, PaneSplitAction.Split)));
        PaneHeader right = Stream(PaneSyncState.Synced, PaneSplitAction.Close);
        right.SelectedIndex = 2;
        right.ViewControls = "▼ IN | ▲ OUT · 16 B/row";
        rows.Children.Add(Captioned("right pane (✕)", right));
        rows.Children.Add(Captioned("inspector", new PaneHeader
        {
            Variant = PaneHeaderVariant.Inspector, Tabs = InspectorTabs, SelectedIndex = 0, Trailing = "#18,233", Width = 324,
            HorizontalAlignment = HorizontalAlignment.Left,
        }));
        rows.Children.Add(Captioned("titled (Compare)", new PaneHeader
        {
            Variant = PaneHeaderVariant.Titled, Title = "Field diff", Subtitle = "#812 · SendAttachment", Width = 620,
            HorizontalAlignment = HorizontalAlignment.Left,
        }));
        return rows;
    }

    private static PaneHeader Stream(PaneSyncState sync, PaneSplitAction split = PaneSplitAction.Single) => new()
    {
        Variant = PaneHeaderVariant.Tabbed,
        Tabs = StreamTabs,
        SelectedIndex = 1,
        ViewControls = "18 px rows",
        SyncState = sync,
        SplitAction = split,
        Width = 620,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    private static void ForceTab(PaneHeader header, int index, string pseudoClass)
    {
        header.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<ListBox>("PART_Tabs") is { } tabs)
                tabs.ContainerPrepared += (_, c) =>
                {
                    if (c.Index == index) PrimitiveGallery.Force(c.Container, pseudoClass);
                };
        };
    }

    private static Control PropertyTrees()
    {
        WrapPanel states = new() { ItemSpacing = 16, LineSpacing = 16 };
        states.Children.Add(Captioned("default", Tree(selected: 1, hovered: -1)));
        states.Children.Add(Captioned("hover", Tree(selected: -1, hovered: 2)));
        states.Children.Add(Captioned("active", Tree(selected: 1, hovered: -1)));
        states.Children.Add(Captioned("empty", Empty()));
        return states;
    }

    /// <summary>The package's sample: MessageChunk 2/5 of invocation #812.</summary>
    public static PropertyTreeModel SampleTree()
    {
        PropertyTreeModel tree = new();
        PayloadMap frame = PayloadMap.ForFrame(TapDirection.Inbound, 18_233, 0x1A3F24);
        tree.Section("Frame header", "inbound · 1,262.418 ms");
        tree.Field("Type", "0x72 · MessageChunk", bytes: frame.Map(-6, 1));
        tree.Field("Body length", "4,096 · LE 00 10", bytes: frame.Map(-5, 2));
        tree.Field("Invocation", "812 · LE 2C 03", bytes: frame.Map(-3, 2));
        tree.Section("Transport", "split across 2 reads");
        tree.Field("#4471", "1A3F21 – 1A3FDF", PropertyTone.Transport, frame.Map(-3, 0xBF));
        tree.Section("Invocation #812", "reassembled");
        tree.Field("NexNet split", "Start + 5 chunks", PropertyTone.NexNet);
        tree.Field("Method", "IChatServer.SendAttachment");
        tree.Field("authenticationToken", "bin 96 B · redacted · sha256 3f9a…c1", PropertyTone.NexNet);
        tree.Complete();
        return tree;
    }

    /// <summary>The package's assembly bar: S, then chunks of 4,096 / 4,096 / 4,096 / 4,096 / 2,038 B, chunk 2 current.</summary>
    public static AssemblySegment[] SampleSegments() =>
    [
        new(AssemblySegmentKind.Start, 0, 0, false, 0),
        new(AssemblySegmentKind.Chunk, 1, 4096, false, 0),
        new(AssemblySegmentKind.Chunk, 2, 4096, true, 2),
        new(AssemblySegmentKind.Chunk, 3, 4096, false, 1),
        new(AssemblySegmentKind.Chunk, 4, 4096, false, 2),
        new(AssemblySegmentKind.Chunk, 5, 2038, false, 1),
    ];

    private static Control Tree(int selected, int hovered)
    {
        PropertyTreeModel model = SampleTree();
        PropertyTreeView view = new() { ItemsSource = model.Rows, Height = 230, Padding = new Thickness(0) };
        if (selected >= 0) view.SelectedIndex = selected;
        if (hovered >= 0)
            view.ContainerPrepared += (_, c) =>
            {
                if (c.Index == hovered) PrimitiveGallery.Force(c.Container, ":pointerover");
            };

        StackPanel footer = new() { Margin = new Thickness(10, 8, 10, 10) };
        TextBlock title = ControlGallery.Text("Invocation #812  18,422 B · 5 chunks · 15.0 ms", "FontSize.Data", "Brush.Text.Secondary");
        footer.Children.Add(title);
        footer.Children.Add(new AssemblyBar { Segments = SampleSegments(), Margin = new Thickness(0, 6) });
        footer.Children.Add(ControlGallery.Text("Chunk 2 arrived in reads #4471 + #4472 · 6 frames interleaved", "FontSize.Meta", "Brush.Text.Secondary"));
        Border rule = new() { BorderThickness = new Thickness(0, 1, 0, 0), Child = footer };
        ControlGallery.Bind(rule, Border.BorderBrushProperty, "Brush.Border.Default");

        StackPanel pane = new() { Children = { view, rule } };
        return Framed(pane);
    }

    private static Control Empty()
    {
        TextBlock hint = ControlGallery.Text(ViewModels.InspectorViewModel.EmptyHint, "FontSize.Body", "Brush.Text.Tertiary");
        hint.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        hint.Margin = new Thickness(10, 16);
        return Framed(hint);
    }

    private static Control Framed(Control content)
    {
        Border frame = new() { Width = 324, BorderThickness = new Thickness(1), Padding = new Thickness(0, 4), Child = content, VerticalAlignment = VerticalAlignment.Top };
        ControlGallery.Bind(frame, Border.BorderBrushProperty, "Brush.Border.Default");
        ControlGallery.Bind(frame, Border.BackgroundProperty, "Brush.Surface.Panel");
        return frame;
    }

    private static Control Captioned(string caption, Control content)
    {
        StackPanel cell = new() { Spacing = 6 };
        cell.Children.Add(content);
        cell.Children.Add(ControlGallery.Text(caption, "FontSize.Data", "Brush.Text.Secondary"));
        return cell;
    }
}
