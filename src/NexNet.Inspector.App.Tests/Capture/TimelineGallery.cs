using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using NexNet.Inspector.App.Controls.Timeline;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Tests.Timeline;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;
using static NexNet.Inspector.App.Tests.Timeline.TimelineDataBuilder;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// The timeline gallery section (PKG controls/timeline-lane): the package's five states on its demo data, 760 px wide
/// without the axis as timeline-lane.html draws them — default (trace band, playhead, pinned expanded Pipe 3·1), hover
/// (the lane under the pointer: a raised gutter, never taller), active (the traced span), disabled (nothing traced) and empty (INVOCATIONS with
/// its hint) — then a full-width busy session with the overview strip and ruler.
/// </summary>
public static class TimelineGallery
{
    public static Control Build()
    {
        StackPanel rows = new() { Spacing = 16 };
        WrapPanel states = new() { ItemSpacing = 16, LineSpacing = 16 };
        states.Children.Add(Captioned("default", State(Demo(), traced: true)));
        states.Children.Add(Captioned("hover", State(Demo(pinned: false), traced: true, hover: new LaneKey(LaneKind.Pipe, 3))));
        states.Children.Add(Captioned("active", State(Demo(pinned: false), traced: true)));
        states.Children.Add(Captioned("disabled", State(Demo(pinned: false), traced: false)));
        states.Children.Add(Captioned("empty", State(EmptyInvocations(), traced: false, playhead: false)));
        rows.Children.Add(states);

        TimelineModel busy = Busy(2_000, durationMs: 1_000);
        busy.SetPinned(new LaneKey(LaneKind.Pipe, 1), true);
        SyncBus sync = Sync(busy, Window(300, 420));
        sync.Select(Selection.OfEntity(EntityKind.Invocation, 90), Ticks(360));
        TimelineControl session = new() { Model = busy, Sync = sync, Width = 1392, HorizontalAlignment = HorizontalAlignment.Left };
        rows.Children.Add(Captioned("session · overview, ruler, groups, trace band, gap, playhead", Framed(session)));
        return rows;
    }

    private static Control State(TimelineModel model, bool traced, LaneKey? hover = null, bool playhead = true)
    {
        SyncBus sync = Sync(model, Window(0, 100));
        if (traced) sync.Select(Selection.OfEntity(EntityKind.Invocation, 812), Ticks(36.2));
        sync.SetPlayhead(playhead ? Ticks(44.8) : -1);
        TimelineControl timeline = new()
        {
            Model = model,
            Sync = sync,
            ShowAxis = false,
            Width = 760,
            HoveredLane = hover,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        timeline.Layout.Animator.Enabled = false;
        return Framed(timeline);
    }

    private static SyncBus Sync(TimelineModel model, TimeWindow window)
    {
        SyncBus sync = new(AvaloniaUiDispatcher.Instance);
        sync.SetBounds(model.Bounds);
        sync.SetWindow(window, byUser: false);
        return sync;
    }

    private static Control Framed(Control content)
    {
        Border frame = new() { BorderThickness = new Thickness(1), Child = content, HorizontalAlignment = HorizontalAlignment.Left };
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
