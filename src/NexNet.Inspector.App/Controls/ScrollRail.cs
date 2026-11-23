using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Platform;

namespace NexNet.Inspector.App.Controls;

/// <summary>
/// The rail scrollbar's behaviour on a <see cref="ScrollViewer"/>, switched on for every one by an app-wide style
/// (Themes/ScrollRail.axaml); the look is the ScrollBar theme there. It
/// <list type="bullet">
/// <item>widens a bar to the rail (class <c>near</c>) while the mouse is within its ScrollViewer's <see cref="ProximityZoneProperty"/>
/// (16 px unless set) of its edge, and while its thumb is
/// dragged (any pointer); leaving collapses it at once. It never marks an event handled, so the content keeps its clicks;</item>
/// <item>owns the one smooth scroll a track page, a Shift+press jump or a menu item runs (<see cref="RailScrollMath.EaseOut"/>
/// over <see cref="RailScrollMath.ScrollDuration"/>): a repeat retargets it, and any other change of the offset (the wheel,
/// a drag, a viewer following its output) stops it;</item>
/// <item>gives each bar its sentence-case <see cref="ContextMenu"/>, made on the first right-press, closed when the
/// content scrolls under it.</item>
/// </list>
/// </summary>
public static class ScrollRail
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("IsEnabled", typeof(ScrollRail));

    /// <summary>The OS animation preference (<see cref="SystemMotion"/>), read once.</summary>
    private static readonly bool Motion = SystemMotion.ShowsAnimations();

    /// <summary>The rail's widening (the theme's transitions).</summary>
    public static TimeSpan WidthMotion { get; } = RailScrollMath.WidthDuration(Motion);

    /// <summary>The rail's and the thumb's colour changes (the theme's transitions).</summary>
    public static TimeSpan ColourMotion { get; } = RailScrollMath.ColourDuration(Motion);

    /// <summary>The thumb's inset inside the expanded rail (the theme's track margin).</summary>
    public static Thickness ThumbInset { get; } = new(RailScrollMath.ThumbInset);

    /// <summary>The design's ease-out, cubic-bezier(.2,0,0,1).</summary>
    public static Easing Ease { get; } = new RailEase();

    private static readonly TimeSpan ScrollMotion = RailScrollMath.ScrollDuration(Motion);

    /// <summary>One frame at 60 Hz.</summary>
    private static readonly TimeSpan Frame = TimeSpan.FromSeconds(1 / 60.0);

    private static readonly ConditionalWeakTable<ScrollViewer, Rail> Rails = [];

    static ScrollRail() => IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>(OnEnabledChanged);

    /// <summary>How far from the edge the pointer widens this ScrollViewer's bars (the transcript's covers its marker lane).</summary>
    public static readonly AttachedProperty<double> ProximityZoneProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, double>("ProximityZone", typeof(ScrollRail), RailScrollMath.ProximityZone);

    public static double GetProximityZone(ScrollViewer scroll)
    {
        ArgumentNullException.ThrowIfNull(scroll);
        return scroll.GetValue(ProximityZoneProperty);
    }

    public static void SetProximityZone(ScrollViewer scroll, double value)
    {
        ArgumentNullException.ThrowIfNull(scroll);
        scroll.SetValue(ProximityZoneProperty, value);
    }

    public static bool GetIsEnabled(ScrollViewer scroll)
    {
        ArgumentNullException.ThrowIfNull(scroll);
        return scroll.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(ScrollViewer scroll, bool value)
    {
        ArgumentNullException.ThrowIfNull(scroll);
        scroll.SetValue(IsEnabledProperty, value);
    }

    private static void OnEnabledChanged(ScrollViewer scroll, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.GetNewValue<bool>())
        {
            if (!Rails.TryGetValue(scroll, out _)) Rails.Add(scroll, new Rail(scroll));
        }
        else if (Rails.TryGetValue(scroll, out Rail? rail))
        {
            rail.Detach();
            Rails.Remove(scroll);
        }
    }

    /// <summary>A press on the track (a page button) runs a page toward <paramref name="along"/>, unless the thumb is already there.</summary>
    internal static void Page(Control button, double along)
    {
        if (RailOf(button) is { } found) found.Rail.PageToward(found.Bar, along);
    }

    /// <summary>A Shift+press on the track centres the thumb on <paramref name="along"/>.</summary>
    internal static void Jump(Control button, double along)
    {
        if (RailOf(button) is { } found) found.Rail.JumpTo(found.Bar, along);
    }

    private static (Rail Rail, ScrollBar Bar)? RailOf(Control part) =>
        part.FindAncestorOfType<ScrollBar>() is { TemplatedParent: ScrollViewer scroll } bar && Rails.TryGetValue(scroll, out Rail? rail)
            ? (rail, bar) : null;

    private static double Along(Orientation o, Vector v) => o == Orientation.Vertical ? v.Y : v.X;

    private static double Along(Orientation o, Size s) => o == Orientation.Vertical ? s.Height : s.Width;

    private static double Along(Orientation o, Point p) => o == Orientation.Vertical ? p.Y : p.X;

    private static Vector With(Orientation o, Vector v, double value) => o == Orientation.Vertical ? v.WithY(value) : v.WithX(value);

    /// <summary>One ScrollViewer's rail state: its bars, the pointer, the drags, the smooth scroll and the menus.</summary>
    private sealed class Rail
    {
        private readonly ScrollViewer _scroll;
        private ScrollBar? _vertical, _horizontal;
        private Point? _pointer;
        private ScrollBar? _dragged;

        // The one smooth scroll: from, to, the offset it last wrote, its first frame's time.
        private bool _running;
        private Vector _from, _to, _written;
        private TimeSpan? _start;

        // The bar a right-press was on, and where along its track, for "Scroll here".
        private double _menuAlong;

        public Rail(ScrollViewer scroll)
        {
            _scroll = scroll;
            scroll.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
            scroll.AddHandler(InputElement.PointerExitedEvent, OnPointerExited, RoutingStrategies.Direct, handledEventsToo: true);
            scroll.AddHandler(InputElement.PointerPressedEvent, OnPressedTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
            scroll.AddHandler(Thumb.DragStartedEvent, OnDragStarted, RoutingStrategies.Bubble, handledEventsToo: true);
            scroll.AddHandler(Thumb.DragCompletedEvent, OnDragCompleted, RoutingStrategies.Bubble, handledEventsToo: true);
            scroll.TemplateApplied += OnTemplateApplied;
            scroll.ScrollChanged += OnScrollChanged;
            FindBars(scroll.GetVisualDescendants().OfType<ScrollBar>().Where(b => b.TemplatedParent == scroll));
        }

        public void Detach()
        {
            _scroll.RemoveHandler(InputElement.PointerMovedEvent, OnPointerMoved);
            _scroll.RemoveHandler(InputElement.PointerExitedEvent, OnPointerExited);
            _scroll.RemoveHandler(InputElement.PointerPressedEvent, OnPressedTunnel);
            _scroll.RemoveHandler(Thumb.DragStartedEvent, OnDragStarted);
            _scroll.RemoveHandler(Thumb.DragCompletedEvent, OnDragCompleted);
            _scroll.TemplateApplied -= OnTemplateApplied;
            _scroll.ScrollChanged -= OnScrollChanged;
            _running = false;
            Collapse(_vertical);
            Collapse(_horizontal);
        }

        private void OnTemplateApplied(object? sender, TemplateAppliedEventArgs e) =>
            FindBars(new[] { e.NameScope.Find<ScrollBar>("PART_VerticalScrollBar"), e.NameScope.Find<ScrollBar>("PART_HorizontalScrollBar") }.OfType<ScrollBar>());

        private void FindBars(IEnumerable<ScrollBar> bars)
        {
            foreach (ScrollBar bar in bars)
            {
                if (bar.Orientation == Orientation.Vertical) _vertical = bar;
                else _horizontal = bar;
            }
        }

        private static void Collapse(ScrollBar? bar) => bar?.Classes.Set("near", false);

        // ===== Proximity =====

        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            if (e.Pointer.Type != PointerType.Mouse) return;
            _pointer = e.GetPosition(_scroll);
            Update();
        }

        private void OnPointerExited(object? sender, PointerEventArgs e)
        {
            _pointer = null;
            Update();
        }

        private void OnDragStarted(object? sender, VectorEventArgs e)
        {
            if (BarOf(e.Source) is not { } bar) return;
            _dragged = bar;
            Update();
        }

        private void OnDragCompleted(object? sender, VectorEventArgs e)
        {
            if (BarOf(e.Source) is null) return;
            _dragged = null;
            Update();
        }

        private ScrollBar? BarOf(object? source) =>
            (source as Visual)?.FindAncestorOfType<ScrollBar>() is { } bar && (bar == _vertical || bar == _horizontal) ? bar : null;

        private void Update()
        {
            Size size = _scroll.Bounds.Size;
            bool inside = _pointer is { } p && p.X >= 0 && p.Y >= 0 && p.X <= size.Width && p.Y <= size.Height;
            double zone = GetProximityZone(_scroll);
            _vertical?.Classes.Set("near", _dragged == _vertical || (inside && RailScrollMath.InZone(_pointer!.Value.X, size.Width, zone)));
            _horizontal?.Classes.Set("near", _dragged == _horizontal || (inside && RailScrollMath.InZone(_pointer!.Value.Y, size.Height, zone)));
        }

        // ===== Track presses =====

        private static (Track Track, Thumb Thumb)? Parts(ScrollBar bar) =>
            bar.GetVisualDescendants().OfType<Track>().FirstOrDefault() is { Thumb: { } thumb } track ? (track, thumb) : null;

        /// <summary>The target the next page or step starts from: the running scroll's, else the offset.</summary>
        private Vector Target => _running ? _to : _scroll.Offset;

        public void PageToward(ScrollBar bar, double along)
        {
            if (Parts(bar) is not var (track, thumb)) return;
            Orientation o = bar.Orientation;
            double max = Along(o, _scroll.ScrollBarMaximum), from = Along(o, Target);
            double trackLength = Along(o, track.Bounds.Size), thumbLength = Along(o, thumb.Bounds.Size);
            double thumbAt = max > 0 ? from / max * Math.Max(0, trackLength - thumbLength) : 0;
            int direction = along < thumbAt ? -1 : along > thumbAt + thumbLength ? 1 : 0;
            if (direction == 0) return;
            ScrollTo(With(o, Target, RailScrollMath.PageTarget(from, Along(o, _scroll.Viewport), max, direction)));
        }

        public void JumpTo(ScrollBar bar, double along)
        {
            if (Parts(bar) is not var (track, thumb)) return;
            Orientation o = bar.Orientation;
            double target = RailScrollMath.JumpTarget(along, Along(o, track.Bounds.Size), Along(o, thumb.Bounds.Size), Along(o, _scroll.ScrollBarMaximum));
            ScrollTo(With(o, Target, target));
        }

        // ===== The smooth scroll =====

        private void ScrollTo(Vector to)
        {
            Vector max = _scroll.ScrollBarMaximum;
            to = new Vector(Math.Clamp(to.X, 0, max.X), Math.Clamp(to.Y, 0, max.Y));
            if (TopLevel.GetTopLevel(_scroll) is not { } top)
            {
                _running = false;
                _scroll.SetCurrentValue(ScrollViewer.OffsetProperty, to);
                return;
            }

            _from = _scroll.Offset;
            _written = _from;
            _to = to;
            _start = null;
            if (_running) return;
            _running = true;
            top.RequestAnimationFrame(Tick);
        }

        private void Tick(TimeSpan at)
        {
            try
            {
                Step(at);
            }
            catch (Exception e) when (e is not (OutOfMemoryException or AccessViolationException or InvalidProgramException))
            {
                // A fault in one frame must not reach the dispatcher and end the app: the scroll stops where it is.
                _running = false;
                Trace.TraceError("ScrollRail: the smooth scroll stopped: " + e);
            }
        }

        private void Step(TimeSpan at)
        {
            if (!_running) return;
            if (_scroll.Offset != _written)
            {
                // Something else moved the offset (the wheel, a drag, a viewer following its output): it wins.
                _running = false;
                return;
            }

            // The first frame already moves (one frame in): a frame that draws nothing new can leave the next one unscheduled.
            _start ??= at - Frame;
            double t = Math.Clamp((at - _start.Value) / ScrollMotion, 0, 1);
            _scroll.SetCurrentValue(ScrollViewer.OffsetProperty, _from + (_to - _from) * RailScrollMath.EaseOut(t));
            _written = _scroll.Offset;
            if (t < 1 && TopLevel.GetTopLevel(_scroll) is { } top) top.RequestAnimationFrame(Tick);
            else _running = false;
        }

        // ===== The menu =====

        private void OnPressedTunnel(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(_scroll).Properties.IsRightButtonPressed || BarOf(e.Source) is not { } bar) return;
            _menuAlong = Parts(bar) is var (track, _) ? Along(bar.Orientation, e.GetPosition(track)) : 0;
            if (bar.ContextMenu is null) bar.ContextMenu = MenuFor(bar);
        }

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (_running) return;
            CloseUnfocusedMenu(_vertical);
            CloseUnfocusedMenu(_horizontal);
        }

        private static void CloseUnfocusedMenu(ScrollBar? bar)
        {
            if (bar?.ContextMenu is { IsOpen: true } menu && !menu.IsKeyboardFocusWithin) menu.Close();
        }

        private ContextMenu MenuFor(ScrollBar bar)
        {
            bool vertical = bar.Orientation == Orientation.Vertical;
            Orientation o = bar.Orientation;
            ContextMenu menu = new();

            MenuItem Item(string header, Key? key, Func<double> target)
            {
                MenuItem item = new() { Header = header, InputGesture = key is { } k ? new KeyGesture(k) : null };
                item.Click += (_, _) =>
                {
                    menu.Close();
                    try
                    {
                        ScrollTo(With(o, Target, target()));
                    }
                    catch (Exception e) when (e is not (OutOfMemoryException or AccessViolationException or InvalidProgramException))
                    {
                        // A menu click runs on the dispatcher: a fault there would end the app over a scroll.
                        Trace.TraceError("ScrollRail: " + header + " failed: " + e);
                    }
                };
                return item;
            }

            double Max() => Along(o, _scroll.ScrollBarMaximum);
            double From() => Along(o, Target);
            double Page(int direction) => RailScrollMath.PageTarget(From(), Along(o, _scroll.Viewport), Max(), direction);
            double Step(int direction) => Math.Clamp(From() + direction * Along(o, _scroll.SmallChange), 0, Max());
            double Here() => Parts(bar) is var (track, thumb)
                ? RailScrollMath.JumpTarget(_menuAlong, Along(o, track.Bounds.Size), Along(o, thumb.Bounds.Size), Max())
                : From();

            menu.Items.Add(Item("Scroll here", null, Here));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(vertical ? "Top" : "Left edge", vertical ? Key.Home : null, () => 0));
            menu.Items.Add(Item(vertical ? "Bottom" : "Right edge", vertical ? Key.End : null, Max));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(vertical ? "Page up" : "Page left", vertical ? Key.PageUp : null, () => Page(-1)));
            menu.Items.Add(Item(vertical ? "Page down" : "Page right", vertical ? Key.PageDown : null, () => Page(+1)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(vertical ? "Scroll up" : "Scroll left", vertical ? Key.Up : Key.Left, () => Step(-1)));
            menu.Items.Add(Item(vertical ? "Scroll down" : "Scroll right", vertical ? Key.Down : Key.Right, () => Step(+1)));
            // Keyboard users land on the first item, as the menu design asks.
            menu.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (menu.Items.OfType<MenuItem>().FirstOrDefault() is { } first && menu.IsOpen && !first.Focus(NavigationMethod.Directional))
                    first.IsSelected = true;
            });
            return menu;
        }
    }

    /// <summary>cubic-bezier(.2,0,0,1) as an Avalonia easing, for the theme's transitions.</summary>
    private sealed class RailEase : Easing
    {
        public override double Ease(double progress) => RailScrollMath.EaseOut(progress);
    }
}

/// <summary>
/// A track page button of the rail scrollbar (named <c>PART_PageUpButton</c> / <c>PART_PageDownButton</c>, so ScrollBar keeps
/// its parts). A click, and each repeat while held, pages 0.9 × viewport toward the pointer through <see cref="ScrollRail"/>
/// rather than ScrollBar's LargeChange (so it raises no Click); a Shift+press jumps the thumb to the pointer instead.
/// </summary>
public sealed class RailPageButton : RepeatButton
{
    // Where the pointer is along the track, for the page's direction and its stop.
    private double _along;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Remember(e);
        PointerPointProperties properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            ScrollRail.Jump(this, _along);
            return;
        }

        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Remember(e);
    }

    protected override void OnClick()
    {
        if (IsEffectivelyEnabled) ScrollRail.Page(this, _along);
    }

    private void Remember(PointerEventArgs e)
    {
        if (this.FindAncestorOfType<Track>() is { } track)
        {
            Point at = e.GetPosition(track);
            _along = track.Orientation == Orientation.Vertical ? at.Y : at.X;
        }
    }
}
