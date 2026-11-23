using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using NexNet.Inspector.App.Controls.Ladder;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Tests.Ladder;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// The ladder gallery section (PKG controls/ladder-row): the inspect-live Ladder pane's rows, 640 px wide as
/// ladder-row.html draws them, in the package's states — default (invocation #812 traced from its selected chunk 2/5:
/// solid bracket on its frames, dotted and dimmed on the frames interleaved with it), hover (a raised row), disabled
/// (nothing traced, no brackets) — and the same rows time-scaled (OQ6).
/// </summary>
public static class LadderGallery
{
    private const double StateWidth = 640;
    private const double StateHeight = 420;

    public static Control Build()
    {
        WrapPanel states = new() { ItemSpacing = 16, LineSpacing = 16 };
        states.Children.Add(Captioned("default", State(traced: true)));
        states.Children.Add(Captioned("hover", State(traced: true, hover: 2)));
        states.Children.Add(Captioned("disabled", State(traced: false)));
        states.Children.Add(Captioned("time-scaled · traced", State(traced: true, mode: LadderMode.TimeScaled)));
        return states;
    }

    private static Control State(bool traced, int hover = -1, LadderMode mode = LadderMode.Compact)
    {
        LadderModel model = LadderDataBuilder.Demo();
        SyncBus sync = new(AvaloniaUiDispatcher.Instance);
        if (traced) sync.Select(Selection.OfFrame(model[6].Frame), model[6].Ticks);
        LadderView view = new()
        {
            Model = model,
            Sync = sync,
            Mode = mode,
            Width = StateWidth,
            Height = StateHeight,
            HoveredIndex = hover,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Border frame = new() { BorderThickness = new Thickness(1), Child = view, HorizontalAlignment = HorizontalAlignment.Left };
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
