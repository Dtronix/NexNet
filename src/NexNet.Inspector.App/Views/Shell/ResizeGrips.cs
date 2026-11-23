using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Layout;

namespace NexNet.Inspector.App.Views.Shell;

/// <summary>
/// Invisible edge and corner grips over an undecorated window (Linux, <see cref="Model.ChromeMode.Undecorated"/>): with no
/// OS frame there is nothing else to resize it by. Each grip starts the window manager's own resize for its edge and
/// carries the matching element role. Hidden while the window is maximized.
/// </summary>
public static class ResizeGrips
{
    /// <summary>The grips' thickness inside the window's edge.</summary>
    public const double Thickness = 4;

    /// <summary>The corner grips' size.</summary>
    public const double Corner = 10;

    /// <summary>A panel of the eight grips for <paramref name="window"/>, to lay over its whole client area.</summary>
    public static Panel Create(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Panel layer = new() { Name = "ResizeGrips" };
        Add(layer, window, WindowEdge.North, WindowDecorationsElementRole.ResizeN, HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN, Thickness, StandardCursorType.TopSide);
        Add(layer, window, WindowEdge.South, WindowDecorationsElementRole.ResizeS, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, Thickness, StandardCursorType.BottomSide);
        Add(layer, window, WindowEdge.West, WindowDecorationsElementRole.ResizeW, HorizontalAlignment.Left, VerticalAlignment.Stretch, Thickness, double.NaN, StandardCursorType.LeftSide);
        Add(layer, window, WindowEdge.East, WindowDecorationsElementRole.ResizeE, HorizontalAlignment.Right, VerticalAlignment.Stretch, Thickness, double.NaN, StandardCursorType.RightSide);
        Add(layer, window, WindowEdge.NorthWest, WindowDecorationsElementRole.ResizeNW, HorizontalAlignment.Left, VerticalAlignment.Top, Corner, Corner, StandardCursorType.TopLeftCorner);
        Add(layer, window, WindowEdge.NorthEast, WindowDecorationsElementRole.ResizeNE, HorizontalAlignment.Right, VerticalAlignment.Top, Corner, Corner, StandardCursorType.TopRightCorner);
        Add(layer, window, WindowEdge.SouthWest, WindowDecorationsElementRole.ResizeSW, HorizontalAlignment.Left, VerticalAlignment.Bottom, Corner, Corner, StandardCursorType.BottomLeftCorner);
        Add(layer, window, WindowEdge.SouthEast, WindowDecorationsElementRole.ResizeSE, HorizontalAlignment.Right, VerticalAlignment.Bottom, Corner, Corner, StandardCursorType.BottomRightCorner);
        return layer;
    }

    private static void Add(Panel layer, Window window, WindowEdge edge, WindowDecorationsElementRole role, HorizontalAlignment h, VerticalAlignment v,
        double width, double height, StandardCursorType cursor)
    {
        // A fill at zero opacity, not none: an element without a background is not hit by the pointer.
        Border grip = new()
        {
            Opacity = 0,
            HorizontalAlignment = h,
            VerticalAlignment = v,
            Width = width,
            Height = height,
            Cursor = new Cursor(cursor),
        };
        grip.Bind(Border.BackgroundProperty, grip.GetResourceObservable("Brush.Surface.Chrome"));
        WindowDecorationProperties.SetElementRole(grip, role);
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            window.BeginResizeDrag(edge, e);
        };
        layer.Children.Add(grip);
    }
}
