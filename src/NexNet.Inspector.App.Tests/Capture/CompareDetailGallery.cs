using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using NexNet.Inspector.App.Tests.Compare;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views.Compare;
using NexNet.Inspector.Core.Compare;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// The Compare bottom panel (PKG compare: Field diff data-table variant + Hex · A ⇄ B, 200 px tall, the hex pane at
/// 42 %), as wide as the compare screen's centre: the variant run's changed first message with its Text field picked
/// (tinted rows, B in split.nexnet, the field ringed in both rows, differing bytes marked), the variant's extra call
/// (only in B: dashes for A, B's rows alone), the attachment two views of one run agree on (no differences), and the
/// empty panel.
/// </summary>
public static class CompareDetailGallery
{
    /// <summary>The section's height in the gallery.</summary>
    public const double Height = 4 * (PanelHeight + 2 + 30);

    private const double PanelHeight = 200;
    private const double PanelWidth = 1207;

    public static Control Build()
    {
        CompareSession variant = CompareFixture.Variant();
        EntityMatch[] sends = CompareFixture.Sends(variant);
        CompareSession mirrored = CompareFixture.Mirrored();
        EntityMatch attachment = CompareFixture.First(mirrored, "SendAttachment", CompareEntityKind.Invocation);

        StackPanel states = new() { Spacing = 16 };
        states.Children.Add(Captioned("default · changed message, Text picked (ringed in A and B, differing bytes marked)",
            Panel(variant, sends[0].Index, "chat-client.nntl", "chat-client-variant.nntl", d => d.FieldDiff.SelectRow(CompareFixture.TextRow(d.FieldDiff.Rows.Diff!)))));
        states.Children.Add(Captioned("only in B · the extra call (A shows dashes and no bytes)",
            Panel(variant, sends[1].Index, "chat-client.nntl", "chat-client-variant.nntl", d => d.FieldDiff.SelectRow(1))));
        states.Children.Add(Captioned("no differences · SendAttachment, client and server views of one run",
            Panel(mirrored, attachment.Index, "chat-client.nntl", "chat-server.nntl", _ => { })));
        states.Children.Add(Captioned("empty · nothing picked", Panel(null, -1, "chat-client.nntl", "chat-client-variant.nntl", _ => { })));
        return states;
    }

    private static Control Panel(CompareSession? compare, int match, string nameA, string nameB, Action<CompareDetailViewModel> setup)
    {
        CompareDetailViewModel model = new();
        model.SetFiles(nameA, nameB);
        model.Show(compare, match);
        setup(model);
        Border frame = new()
        {
            BorderThickness = new Thickness(1),
            Width = PanelWidth + 2,
            Height = PanelHeight + 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new CompareDetailView { DataContext = model },
        };
        ControlGallery.Bind(frame, Border.BorderBrushProperty, "Brush.Border.Default");
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
