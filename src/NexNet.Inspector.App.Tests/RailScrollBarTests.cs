using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

using NexNet.Inspector.App.Controls;

namespace NexNet.Inspector.App.Tests;

/// <summary>
/// Every ScrollBar is the rail (ported from Foreman, re-keyed to the design tokens): a 2 px pill line at rest (2 px from the
/// viewport's edge, 3 px from its ends, overlaid, in scroll.thumb-rest), a 12 px rounded rail on scroll.rail with a 1 px
/// border.default outline and an 8 px scroll.thumb-near thumb while the pointer is within 16 px of the edge, the OQ7 hover
/// and drag colours, no arrow buttons, 0.9 × viewport track pages and a Shift+press jump, and a sentence-case menu.
/// A probe ScrollViewer in a plain window on the App's headless session, both orientations, both themes.
/// </summary>
[NotInParallel("avalonia")]   // the headless session and Application.Current's theme are process-wide
public sealed class RailScrollBarTests
{
    private const double Size = 200, Big = 2000;

    private static Color? ColorOf(object? value) => UiPump.ColorOf(value);

    private static Color? Token(string key, ThemeVariant theme) => UiPump.Token(key, theme);

    private sealed record Probe(Window Window, ScrollViewer Scroll, Border Content);

    /// <summary>
    /// A 200 × 200 ScrollViewer at the window's top-left over a <paramref name="width"/> × <paramref name="height"/> content,
    /// both bars Auto; <paramref name="body"/> runs on the UI thread, the window closes after.
    /// </summary>
    private static Task<T> With<T>(double width, double height, Func<Probe, T> body, ThemeVariant? theme = null)
    {
        return HeadlessSession.Run(() => UiPump.InTheme(theme ?? ThemeVariant.Dark, () =>
        {
            Border content = new() { Width = width, Height = height, Background = Brushes.Transparent };
            ScrollViewer scroll = new()
            {
                Width = Size,
                Height = Size,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = content,
            };
            Window window = new() { Width = 400, Height = 400, Content = scroll };
            window.Show();
            Settle();
            try
            {
                return body(new Probe(window, scroll, content));
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }));
    }

    private static void Settle(double ms = 400) => UiPump.Settle(ms);

    private static ScrollBar Bar(ScrollViewer scroll, Orientation orientation) =>
        scroll.GetVisualDescendants().OfType<ScrollBar>().First(b => b.Orientation == orientation);

    private static Thumb ThumbOf(ScrollBar bar) => bar.GetVisualDescendants().OfType<Thumb>().First();

    /// <summary>The bar's bounds in the ScrollViewer's coordinates.</summary>
    private static Rect Place(ScrollBar bar, ScrollViewer scroll) =>
        new(bar.TranslatePoint(default, scroll) ?? default, bar.Bounds.Size);

    /// <summary>The colour the thumb draws: its own background, or its template's filled border.</summary>
    private static Color? ThumbFill(Thumb thumb) =>
        thumb.GetSelfAndVisualDescendants().OfType<Visual>().Select(v => v switch
        {
            Border b => ColorOf(b.Background),
            TemplatedControl t => ColorOf(t.Background),
            _ => null,
        }).FirstOrDefault(c => c is { A: > 0 });

    /// <summary>The rail's own fill and outline: the first border outside the thumb that has a background or an outline.</summary>
    private static Border? RailChrome(ScrollBar bar) =>
        bar.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => !b.GetVisualAncestors().OfType<Thumb>().Any() && (b.Background is not null || b.BorderThickness != default));

    private static bool Draws(Border rail) =>
        rail.IsEffectivelyVisible && rail.Opacity > 0
        && (ColorOf(rail.Background) is { A: > 0 } || (rail.BorderThickness != default && ColorOf(rail.BorderBrush) is { A: > 0 }));

    private static Point At(ScrollViewer scroll, Window window, double x, double y) => scroll.TranslatePoint(new Point(x, y), window)!.Value;

    [Test]
    [Arguments("Vertical")]
    [Arguments("Horizontal")]
    public async Task NoBarHasLineButtonsAndItsTrackAndThumbArePills(string which)
    {
        Orientation orientation = which == "Vertical" ? Orientation.Vertical : Orientation.Horizontal;
        List<string> wrong = await With(Big, Big, p =>
        {
            List<string> w = [];
            ScrollBar bar = Bar(p.Scroll, orientation);
            foreach (string part in new[] { "PART_LineUpButton", "PART_LineDownButton" })
            {
                if (bar.GetVisualDescendants().OfType<Control>().Any(c => c.Name == part)) w.Add($"the template has {part}");
            }

            if (bar.GetVisualDescendants().OfType<Control>().Any(c => c.Name is { } n && n.Contains("Line", StringComparison.Ordinal) && c is Button && c.IsEffectivelyVisible))
                w.Add("a line button is shown");

            p.Window.MouseMove(orientation == Orientation.Vertical ? At(p.Scroll, p.Window, Size - 6, Size / 2) : At(p.Scroll, p.Window, Size / 2, Size - 6), RawInputModifiers.None);
            Settle();
            if (bar.GetVisualDescendants().OfType<Button>().Any(b => b is not RepeatButton { Name: "PART_PageUpButton" or "PART_PageDownButton" } && b.IsEffectivelyVisible && b.Bounds.Width > 0))
                w.Add("the expanded rail shows a button other than the page buttons");
            Thumb thumb = ThumbOf(bar);
            double across = orientation == Orientation.Vertical ? thumb.Bounds.Width : thumb.Bounds.Height;
            Border? thumbBorder = thumb.GetSelfAndVisualDescendants().OfType<Border>().FirstOrDefault(b => ColorOf(b.Background) is { A: > 0 });
            CornerRadius thumbRadius = thumbBorder?.CornerRadius ?? thumb.CornerRadius;
            if (thumbRadius.TopLeft < across / 2 - 0.01) w.Add($"thumb radius {thumbRadius} under half of {across}");
            if (RailChrome(bar) is not { } rail) w.Add("the expanded rail draws no chrome");
            else
            {
                double railAcross = orientation == Orientation.Vertical ? rail.Bounds.Width : rail.Bounds.Height;
                if (rail.CornerRadius.TopLeft < railAcross / 2 - 0.01) w.Add($"rail radius {rail.CornerRadius} under half of {railAcross}");
            }

            return w;
        });
        await Assert.That(wrong).IsEmpty();
    }

    [Test]
    [Arguments("Vertical")]
    [Arguments("Horizontal")]
    public async Task AtRestTheBarIsA2PxLineInset2PxFromTheEdgeAnd3PxFromTheEnds(string which)
    {
        Orientation orientation = which == "Vertical" ? Orientation.Vertical : Orientation.Horizontal;
        List<string> wrong = await With(Big, Big, p =>
        {
            List<string> w = [];
            ScrollBar bar = Bar(p.Scroll, orientation);
            Rect at = Place(bar, p.Scroll);
            if (orientation == Orientation.Vertical)
            {
                if (Math.Abs(at.Width - 2) > 0.01) w.Add($"width {at.Width}");
                if (Math.Abs(Size - at.Right - 2) > 0.01) w.Add($"{Size - at.Right} px from the right edge");
                if (Math.Abs(at.Top - 3) > 0.01) w.Add($"{at.Top} px from the top");
                if (Math.Abs(Size - at.Bottom - 3) > 0.01) w.Add($"{Size - at.Bottom} px from the bottom");
            }
            else
            {
                if (Math.Abs(at.Height - 2) > 0.01) w.Add($"height {at.Height}");
                if (Math.Abs(Size - at.Bottom - 2) > 0.01) w.Add($"{Size - at.Bottom} px from the bottom edge");
                if (Math.Abs(at.Left - 3) > 0.01) w.Add($"{at.Left} px from the left");
                if (Math.Abs(Size - at.Right - 3) > 0.01) w.Add($"{Size - at.Right} px from the right");
            }

            Thumb thumb = ThumbOf(bar);
            double across = orientation == Orientation.Vertical ? thumb.Bounds.Width : thumb.Bounds.Height;
            if (Math.Abs(across - 2) > 0.01) w.Add($"thumb {across} px across");
            if (ThumbFill(thumb) != Token("Brush.Scroll.ThumbRest", ThemeVariant.Dark)) w.Add($"thumb {ThumbFill(thumb)} is not scroll.thumb-rest");
            if (RailChrome(bar) is { } rail && Draws(rail)) w.Add("the resting bar draws a track");
            return w;
        });
        await Assert.That(wrong).IsEmpty();
    }

    [Test]
    public async Task TheBarReservesNoLayoutWidth()
    {
        double overflowing = await With(Size - 4, Big, p => { Border c = new() { Height = Big }; p.Scroll.Content = c; Settle(); return c.Bounds.Width; });
        double fitting = await With(Size - 4, 10, p => { Border c = new() { Height = 10 }; p.Scroll.Content = c; Settle(); return c.Bounds.Width; });
        await Assert.That(overflowing).IsEqualTo(fitting).Because("the content is arranged as wide with a bar as without");
    }

    [Test]
    public async Task WhenTheContentFitsNoBarIsShown()
    {
        bool shown = await With(100, 100, p => p.Scroll.GetVisualDescendants().OfType<ScrollBar>()
            .Any(b => b.IsEffectivelyVisible && b.Bounds.Width > 0 && b.Bounds.Height > 0 && b.Opacity > 0));
        await Assert.That(shown).IsFalse();
    }

    [Test]
    [Arguments("Vertical", "Dark")]
    [Arguments("Vertical", "Light")]
    [Arguments("Horizontal", "Dark")]
    [Arguments("Horizontal", "Light")]
    public async Task ThePointerWithin16PxExpandsTheRailAndLeavingCollapsesIt(string which, string variant)
    {
        Orientation orientation = which == "Vertical" ? Orientation.Vertical : Orientation.Horizontal;
        ThemeVariant theme = variant == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        List<string> wrong = await With(Big, Big, p =>
        {
            List<string> w = [];
            ScrollBar bar = Bar(p.Scroll, orientation);
            Point Near(double inset) => orientation == Orientation.Vertical
                ? At(p.Scroll, p.Window, Size - inset, Size / 2) : At(p.Scroll, p.Window, Size / 2, Size - inset);

            // 15 px in: inside the zone, outside the 12 px rail (2 px from the edge), so only proximity can expand it.
            p.Window.MouseMove(Near(15), RawInputModifiers.None);
            Settle();
            Rect at = Place(bar, p.Scroll);
            double across = orientation == Orientation.Vertical ? at.Width : at.Height;
            if (Math.Abs(across - 12) > 0.01) w.Add($"near: {across} px across, not 12");
            double edge = orientation == Orientation.Vertical ? Size - at.Right : Size - at.Bottom;
            if (Math.Abs(edge - 2) > 0.01) w.Add($"near: {edge} px from the edge, not 2");
            if (RailChrome(bar) is not { } rail) w.Add("near: no rail chrome");
            else
            {
                if (ColorOf(rail.Background) != Token("Brush.Scroll.Rail", theme)) w.Add($"near: rail {ColorOf(rail.Background)} is not scroll.rail");
                if (ColorOf(rail.BorderBrush) != Token("Brush.Border.Default", theme)) w.Add($"near: outline {ColorOf(rail.BorderBrush)} is not border.default");
                if (rail.BorderThickness != new Thickness(1)) w.Add($"near: outline {rail.BorderThickness}");
            }

            Thumb thumb = ThumbOf(bar);
            double thumbAcross = orientation == Orientation.Vertical ? thumb.Bounds.Width : thumb.Bounds.Height;
            if (Math.Abs(thumbAcross - 8) > 0.01) w.Add($"near: thumb {thumbAcross} px across, not 8");
            if (ThumbFill(thumb) != Token("Brush.Scroll.ThumbNear", theme)) w.Add($"near: thumb {ThumbFill(thumb)} is not scroll.thumb-near");

            p.Window.MouseMove(Near(40), RawInputModifiers.None);
            Settle();
            at = Place(bar, p.Scroll);
            across = orientation == Orientation.Vertical ? at.Width : at.Height;
            if (Math.Abs(across - 2) > 0.01) w.Add($"left the zone: {across} px across, not 2");
            return w;
        }, theme);
        await Assert.That(wrong).IsEmpty();
    }

    /// <summary>
    /// The rail fades by its Opacity alone: sampled before the 120 ms colour motion ends, expanding and collapsing, its fill
    /// and outline are already scroll.rail and border.default. A brush fade from Transparent (#00FFFFFF) drew light greys there.
    /// </summary>
    [Test]
    [Arguments("Vertical", "Dark")]
    [Arguments("Vertical", "Light")]
    [Arguments("Horizontal", "Dark")]
    [Arguments("Horizontal", "Light")]
    public async Task TheRailFadesByOpacityAndKeepsItsColoursWhileExpandingAndCollapsing(string which, string variant)
    {
        Orientation orientation = which == "Vertical" ? Orientation.Vertical : Orientation.Horizontal;
        ThemeVariant theme = variant == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        List<string> wrong = await With(Big, Big, p =>
        {
            List<string> w = [];
            ScrollBar bar = Bar(p.Scroll, orientation);
            Border rail = bar.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Rail");
            Point Near(double inset) => orientation == Orientation.Vertical
                ? At(p.Scroll, p.Window, Size - inset, Size / 2) : At(p.Scroll, p.Window, Size / 2, Size - inset);

            void Check(string when)
            {
                if (ColorOf(rail.Background) != Token("Brush.Scroll.Rail", theme)) w.Add($"{when}: rail {ColorOf(rail.Background)} is not scroll.rail");
                if (ColorOf(rail.BorderBrush) != Token("Brush.Border.Default", theme)) w.Add($"{when}: outline {ColorOf(rail.BorderBrush)} is not border.default");
            }

            p.Window.MouseMove(Near(15), RawInputModifiers.None);
            Settle(40);
            Check("expanding");
            Settle();
            if (Math.Abs(rail.Opacity - 1) > 0.001) w.Add($"near: opacity {rail.Opacity}, not 1");
            Check("near");

            p.Window.MouseMove(Near(40), RawInputModifiers.None);
            Settle(40);
            Check("collapsing");
            Settle();
            if (rail.Opacity != 0) w.Add($"at rest: opacity {rail.Opacity}, not 0");
            return w;
        }, theme);
        await Assert.That(wrong).IsEmpty();
    }

    /// <summary>
    /// A ScrollViewer may widen its own proximity zone (a view with a marker lane at its edge); every other keeps 16 px.
    /// 20 px in is outside the default zone and inside a 24 px one.
    /// </summary>
    [Test]
    public async Task AScrollViewersOwnProximityZoneWidensOnlyItsRail()
    {
        (double zone, bool defaultNear, bool widenedNear, bool widenedFar) = await With(Big, Big, p =>
        {
            ScrollBar bar = Bar(p.Scroll, Orientation.Vertical);
            double before = ScrollRail.GetProximityZone(p.Scroll);
            p.Window.MouseMove(At(p.Scroll, p.Window, Size - 20, Size / 2), RawInputModifiers.None);
            Settle(40);
            bool d = bar.Classes.Contains("near");

            ScrollRail.SetProximityZone(p.Scroll, 24);
            p.Window.MouseMove(At(p.Scroll, p.Window, Size - 21, Size / 2), RawInputModifiers.None);
            p.Window.MouseMove(At(p.Scroll, p.Window, Size - 20, Size / 2), RawInputModifiers.None);
            Settle(40);
            bool n = bar.Classes.Contains("near");
            p.Window.MouseMove(At(p.Scroll, p.Window, Size - 30, Size / 2), RawInputModifiers.None);
            Settle(40);
            return (before, d, n, bar.Classes.Contains("near"));
        });

        using (Assert.Multiple())
        {
            await Assert.That(zone).IsEqualTo(RailScrollMath.ProximityZone).Because("a ScrollViewer keeps the 16 px zone unless it sets its own");
            await Assert.That(defaultNear).IsFalse().Because("20 px in is outside the default zone");
            await Assert.That(widenedNear).IsTrue().Because("inside a 24 px zone");
            await Assert.That(widenedFar).IsFalse().Because("30 px in is outside it");
        }
    }

    [Test]
    public async Task TheWheelNeverExpandsTheRail()
    {
        (double width, double offset) = await With(Big, Big, p =>
        {
            Point middle = At(p.Scroll, p.Window, Size / 2, Size / 2);
            p.Window.MouseMove(middle, RawInputModifiers.None);
            p.Window.MouseWheel(middle, new Vector(0, -3), RawInputModifiers.None);
            Settle();
            return (Place(Bar(p.Scroll, Orientation.Vertical), p.Scroll).Width, p.Scroll.Offset.Y);
        });
        using (Assert.Multiple())
        {
            await Assert.That(offset).IsGreaterThan(0).Because("the wheel scrolled");
            await Assert.That(width).IsEqualTo(2).Within(0.01).Because("the wheel left the bar at rest");
        }
    }

    [Test]
    [Arguments("Dark")]
    [Arguments("Light")]
    public async Task TheThumbTakesTheHoverAndDragColours(string variant)
    {
        ThemeVariant theme = variant == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        // OQ7: hover = text.secondary, drag = direction.inbound (the added Brush.Scroll.* keys).
        (string hover, string drag) = ("Brush.Scroll.ThumbHover", "Brush.Scroll.ThumbDrag");
        List<string> wrong = await With(Big, Big, p =>
        {
            List<string> w = [];
            ScrollBar bar = Bar(p.Scroll, Orientation.Vertical);
            p.Window.MouseMove(At(p.Scroll, p.Window, Size - 10, Size / 2), RawInputModifiers.None);
            Settle();
            Thumb thumb = ThumbOf(bar);
            Point centre = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), p.Window)!.Value;
            p.Window.MouseMove(centre, RawInputModifiers.None);
            Settle();
            if (ThumbFill(thumb) != Token(hover, theme)) w.Add($"hover: {ThumbFill(thumb)} is not {hover}");
            p.Window.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
            Settle();
            if (ThumbFill(thumb) != Token(drag, theme)) w.Add($"pressed: {ThumbFill(thumb)} is not {drag}");

            // Dragged out of the zone: the rail stays expanded until the release, then collapses.
            Point outside = At(p.Scroll, p.Window, Size / 2, Size / 2 + 20);
            p.Window.MouseMove(outside, RawInputModifiers.LeftMouseButton);
            Settle();
            if (Math.Abs(Place(bar, p.Scroll).Width - 12) > 0.01) w.Add($"dragging outside the zone: {Place(bar, p.Scroll).Width} px, not 12");
            if (ThumbFill(thumb) != Token(drag, theme)) w.Add($"dragging: {ThumbFill(thumb)} is not {drag}");
            p.Window.MouseUp(outside, MouseButton.Left, RawInputModifiers.None);
            Settle();
            if (Math.Abs(Place(bar, p.Scroll).Width - 2) > 0.01) w.Add($"released outside the zone: {Place(bar, p.Scroll).Width} px, not 2");
            return w;
        }, theme);
        await Assert.That(wrong).IsEmpty();
    }

    [Test]
    public async Task AThumbIsNeverShorterThan28Px()
    {
        double length = await With(100, 100_000, p =>
        {
            p.Window.MouseMove(At(p.Scroll, p.Window, Size - 10, Size / 2), RawInputModifiers.None);
            Settle();
            return ThumbOf(Bar(p.Scroll, Orientation.Vertical)).Bounds.Height;
        });
        await Assert.That(length).IsGreaterThanOrEqualTo(28 - 0.01);
    }

    /// <summary>The thumb's travel and its press point in window coordinates, for the vertical bar expanded by the pointer.</summary>
    private static (ScrollBar Bar, Thumb Thumb) Expand(Probe p)
    {
        p.Window.MouseMove(At(p.Scroll, p.Window, Size - 8, Size / 2), RawInputModifiers.None);
        Settle();
        ScrollBar bar = Bar(p.Scroll, Orientation.Vertical);
        return (bar, ThumbOf(bar));
    }

    [Test]
    public async Task ADragMovesTheOffsetDirectlyWithThePointer()
    {
        (double offset, double expected) = await With(Size - 4, Big, p =>
        {
            (ScrollBar bar, Thumb thumb) = Expand(p);
            Track track = bar.GetVisualDescendants().OfType<Track>().First();
            double travel = track.Bounds.Height - thumb.Bounds.Height;
            double max = p.Scroll.Extent.Height - p.Scroll.Viewport.Height;
            Point start = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), p.Window)!.Value;
            p.Window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
            p.Window.MouseMove(start + new Point(0, 30), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            double now = p.Scroll.Offset.Y;
            p.Window.MouseUp(start + new Point(0, 30), MouseButton.Left, RawInputModifiers.None);
            return (now, 30 * max / travel);
        });
        await Assert.That(offset).IsEqualTo(expected).Within(1).Because("the offset follows the pointer at once, with no animation");
    }

    [Test]
    public async Task PressingTheTrackBelowTheThumbPagesNineTenthsOfTheViewport()
    {
        double offset = await With(Size - 4, Big, p =>
        {
            (ScrollBar bar, _) = Expand(p);
            Point below = bar.TranslatePoint(new Point(bar.Bounds.Width / 2, bar.Bounds.Height - 10), p.Window)!.Value;
            p.Window.MouseDown(below, MouseButton.Left, RawInputModifiers.None);
            p.Window.MouseUp(below, MouseButton.Left, RawInputModifiers.None);
            Settle();
            return p.Scroll.Offset.Y;
        });
        await Assert.That(offset).IsEqualTo(0.9 * Size).Within(0.5);
    }

    [Test]
    public async Task AShiftPressOnTheTrackCentresTheThumbOnThePointer()
    {
        (double offset, double max) = await With(Size - 4, Big, p =>
        {
            (ScrollBar bar, _) = Expand(p);
            Point middle = bar.TranslatePoint(new Point(bar.Bounds.Width / 2, bar.Bounds.Height / 2), p.Window)!.Value;
            p.Window.MouseDown(middle, MouseButton.Left, RawInputModifiers.Shift);
            p.Window.MouseUp(middle, MouseButton.Left, RawInputModifiers.Shift);
            Settle();
            return (p.Scroll.Offset.Y, p.Scroll.Extent.Height - p.Scroll.Viewport.Height);
        });
        await Assert.That(offset).IsEqualTo(max / 2).Within(10);
    }

    [Test]
    public async Task ARepeatStopsOnceTheThumbCoversThePointer()
    {
        double offset = await With(Size - 4, Big, p =>
        {
            (ScrollBar bar, Thumb thumb) = Expand(p);
            Track track = bar.GetVisualDescendants().OfType<Track>().First();

            // Just below the thumb: the first page brings the thumb over the pointer, so the second press pages no further.
            double along = thumb.Bounds.Height + 7;
            Point press = track.TranslatePoint(new Point(track.Bounds.Width / 2, along), p.Window)!.Value;
            p.Window.MouseDown(press, MouseButton.Left, RawInputModifiers.None);
            p.Window.MouseUp(press, MouseButton.Left, RawInputModifiers.None);

            // The hold's repeat, as the page button raises it: toward the same point, while the first page runs.
            RepeatButton down = track.GetVisualDescendants().OfType<RepeatButton>().First(b => b.Name == "PART_PageDownButton");
            ScrollRail.Page(down, along);
            Settle();
            return p.Scroll.Offset.Y;
        });
        await Assert.That(offset).IsEqualTo(0.9 * Size).Within(0.5).Because("one page, then the thumb covers the pointer");
    }

    [Test]
    public async Task AnotherOffsetChangeStopsTheSmoothScroll()
    {
        double offset = await With(Size - 4, Big, p =>
        {
            (ScrollBar bar, _) = Expand(p);
            Point below = bar.TranslatePoint(new Point(bar.Bounds.Width / 2, bar.Bounds.Height - 10), p.Window)!.Value;
            p.Window.MouseDown(below, MouseButton.Left, RawInputModifiers.None);
            p.Window.MouseUp(below, MouseButton.Left, RawInputModifiers.None);

            // Before the page's first frame, something else (a wheel, a viewer following) moves the offset: it wins.
            p.Scroll.Offset = new Vector(0, 50);
            Settle();
            return p.Scroll.Offset.Y;
        });
        await Assert.That(offset).IsEqualTo(50).Within(0.01);
    }

    [Test]
    public async Task TheMenuClosesWhenTheContentScrollsUnderIt()
    {
        string state = await With(Size - 4, Big, p =>
        {
            ContextMenu? menu = RightClick(p, Orientation.Vertical);
            if (menu is null) return "no menu opened";

            // Focus back in the window: the menu is open but unfocused, so the scroll closes it.
            p.Content.Focusable = true;
            p.Content.Focus();
            Dispatcher.UIThread.RunJobs();
            if (!menu.IsOpen) return "closed before the scroll";
            if (menu.IsKeyboardFocusWithin) return "the menu kept the focus";
            p.Scroll.Offset = new Vector(0, 100);
            Settle(150);
            bool open = menu.IsOpen;
            if (open) menu.Close();
            return open ? "still open" : "closed";
        });
        await Assert.That(state).IsEqualTo("closed");
    }

    /// <summary>The open context menu a right-click on the bar raised: on the bar, its template, or the ScrollViewer.</summary>
    private static ContextMenu? OpenMenu(ScrollViewer scroll) =>
        scroll.GetSelfAndVisualDescendants().OfType<Control>().Select(c => c.ContextMenu).FirstOrDefault(m => m is { IsOpen: true });

    private static ContextMenu? RightClick(Probe p, Orientation orientation)
    {
        Point on = orientation == Orientation.Vertical ? At(p.Scroll, p.Window, Size - 8, Size / 2) : At(p.Scroll, p.Window, Size / 2, Size - 8);
        p.Window.MouseMove(on, RawInputModifiers.None);
        Settle();
        p.Window.MouseDown(on, MouseButton.Right, RawInputModifiers.None);
        p.Window.MouseUp(on, MouseButton.Right, RawInputModifiers.None);
        Settle(150);
        return OpenMenu(p.Scroll);
    }

    private static string Describe(ContextMenu menu) => string.Join(" | ", menu.Items.Select(i => i switch
    {
        Separator => "—",
        MenuItem m => m.InputGesture is { } g ? $"{m.Header} [{g.Key}]" : $"{m.Header}",
        _ => "?",
    }));

    [Test]
    public async Task TheVerticalMenuIsSentenceCaseWithKeyHints()
    {
        string seen = await With(Big, Big, p =>
        {
            ContextMenu? menu = RightClick(p, Orientation.Vertical);
            if (menu is null) return "no menu opened";
            string items = Describe(menu);
            bool firstFocused = menu.Items.OfType<MenuItem>().First() is { IsSelected: true } or { IsFocused: true };
            bool allEnabled = menu.Items.OfType<MenuItem>().All(m => m.IsEffectivelyEnabled);
            menu.Close();
            return $"{items}; first focused {firstFocused}; all enabled {allEnabled}";
        });
        await Assert.That(seen).IsEqualTo("Scroll here | — | Top [Home] | Bottom [End] | — | Page up [PageUp] | Page down [PageDown] | — | Scroll up [Up] | Scroll down [Down]; first focused True; all enabled True");
    }

    [Test]
    public async Task TheHorizontalMenuNamesLeftAndRight()
    {
        string seen = await With(Big, Big, p =>
        {
            ContextMenu? menu = RightClick(p, Orientation.Horizontal);
            if (menu is null) return "no menu opened";
            string items = Describe(menu);
            menu.Close();
            return items;
        });
        await Assert.That(seen).IsEqualTo("Scroll here | — | Left edge | Right edge | — | Page left | Page right | — | Scroll left [Left] | Scroll right [Right]");
    }

    [Test]
    public async Task TheMenusPageDownPagesNineTenthsOfTheViewportAndCloses()
    {
        (string state, double offset) = await With(Size - 4, Big, p =>
        {
            ContextMenu? menu = RightClick(p, Orientation.Vertical);
            if (menu is null) return ("no menu opened", 0);
            MenuItem? page = menu.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) == "Page down");
            if (page is null)
            {
                menu.Close();
                return ($"no Page down item in {Describe(menu)}", 0);
            }

            page.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Settle();
            bool open = menu.IsOpen;
            if (open) menu.Close();
            return (open ? "still open" : "closed", p.Scroll.Offset.Y);
        });
        using (Assert.Multiple())
        {
            await Assert.That(state).IsEqualTo("closed");
            await Assert.That(offset).IsEqualTo(0.9 * Size).Within(0.5);
        }
    }

    [Test]
    public async Task TheMenusBottomGoesToTheEnd()
    {
        (string state, double offset, double max) = await With(Size - 4, Big, p =>
        {
            ContextMenu? menu = RightClick(p, Orientation.Vertical);
            if (menu is null) return ("no menu opened", 0, 1);
            MenuItem? bottom = menu.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) == "Bottom");
            if (bottom is null)
            {
                menu.Close();
                return ($"no Bottom item in {Describe(menu)}", 0, 1);
            }

            bottom.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Settle();
            if (menu.IsOpen) menu.Close();
            return ("ran", p.Scroll.Offset.Y, p.Scroll.Extent.Height - p.Scroll.Viewport.Height);
        });
        using (Assert.Multiple())
        {
            await Assert.That(state).IsEqualTo("ran");
            await Assert.That(offset).IsEqualTo(max).Within(0.5);
        }
    }

    /// <summary>The bar keeps the role a screen reader reads (a guard: true on the base too).</summary>
    [Test]
    public async Task TheBarKeepsItsScrollBarRole()
    {
        Avalonia.Automation.Peers.AutomationControlType role = await With(Big, Big, p =>
            Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(Bar(p.Scroll, Orientation.Vertical)).GetAutomationControlType());
        await Assert.That(role).IsEqualTo(Avalonia.Automation.Peers.AutomationControlType.ScrollBar);
    }
}
