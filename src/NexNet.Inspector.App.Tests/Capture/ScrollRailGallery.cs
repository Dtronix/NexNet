using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// The scroll-rail gallery section (PKG controls/scroll-rail): a 180 × 160 list in each state — rest (the 2 px line),
/// hover (the pointer near: the 12 px rail), focus (the thumb hovered) and active (the thumb dragged). The states are set
/// once the bar exists, with its transitions removed, so the capture shows the end state rather than a frame of the 160 ms
/// widening.
/// </summary>
public static class ScrollRailGallery
{
    private enum RailState
    {
        Default,
        Hover,
        Focus,
        Active,
    }

    public static Control Build()
    {
        StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 32 };
        row.Children.Add(Box(RailState.Default, "rest · 2px line"));
        row.Children.Add(Box(RailState.Hover, "near (pointer within 16px) · 12px rail"));
        row.Children.Add(Box(RailState.Focus, "thumb hovered"));
        row.Children.Add(Box(RailState.Active, "thumb dragged"));
        return row;
    }

    private static Control Box(RailState state, string caption)
    {
        StackPanel lines = new() { Spacing = 10, Margin = new Thickness(10, 12, 20, 12) };
        for (int i = 0; i < 22; i++)
        {
            Border line = new() { Height = 8, CornerRadius = new CornerRadius(2) };
            ControlGallery.Bind(line, Border.BackgroundProperty, "Brush.Surface.Raised");
            lines.Children.Add(line);
        }

        ScrollViewer scroll = new() { Content = lines, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Border box = new() { Width = 180, Height = 160, BorderThickness = new Thickness(1), Child = scroll };
        ControlGallery.Bind(box, Border.BackgroundProperty, "Brush.Surface.Panel");
        ControlGallery.Bind(box, Border.BorderBrushProperty, "Brush.Border.Default");
        scroll.Loaded += (_, _) => Apply(scroll, state);

        StackPanel cell = new() { Spacing = 6 };
        cell.Children.Add(box);
        cell.Children.Add(ControlGallery.Text(caption, "FontSize.Data", "Brush.Text.Secondary"));
        return cell;
    }

    private static void Apply(ScrollViewer scroll, RailState state)
    {
        // The design's thumb sits a fifth of the way down.
        scroll.Offset = new Vector(0, (scroll.Extent.Height - scroll.Viewport.Height) / 3);
        if (state == RailState.Default) return;
        if (scroll.GetVisualDescendants().OfType<ScrollBar>().FirstOrDefault(b => b.Orientation == Orientation.Vertical) is not { } bar) return;
        bar.Transitions = null;
        foreach (Control part in bar.GetVisualDescendants().OfType<Control>()) part.Transitions = null;
        bar.Classes.Add("near");
        if (bar.GetVisualDescendants().OfType<Thumb>().FirstOrDefault() is not { } thumb) return;
        if (state == RailState.Focus) ((IPseudoClasses)thumb.Classes).Set(":pointerover", true);
        if (state == RailState.Active) ((IPseudoClasses)thumb.Classes).Set(":pressed", true);
    }
}
