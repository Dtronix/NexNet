using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Controls;
using NexNet.Inspector.App.Controls.Hex;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Tests.Compare;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.Views.Compare;
using NexNet.Inspector.Core.Compare;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using static NexNet.Inspector.App.Tests.Hex.HexCaptureBuilder;

namespace NexNet.Inspector.App.Tests.Hex;

/// <summary>
/// The virtual views' own scroll bars are the app's rail, as every ScrollViewer's is (user report: the hex bar stayed a
/// 2 px line): the hex stream's, the A ⇄ B hex view's and the compare rows' bar widen to the 12 px rail while the mouse
/// is within 16 px of the view's edge and collapse when it leaves, take the near thumb colour, page from the track, and
/// overlay the content so widening never reflows it.
/// </summary>
[NotInParallel("avalonia")]
public sealed class HexScrollRailTests
{
    private const double ViewWidth = 760, ViewHeight = 300;

    private static CaptureSession Long(int frames)
    {
        HexCaptureBuilder builder = new();
        builder.Op(TapDirection.Inbound, 0, Preamble()).Op(TapDirection.Outbound, 0.01, Preamble());
        for (int i = 0; i < frames; i++) builder.Op(TapDirection.Inbound, 1 + i, PipeWrite(0x0101, Pattern(100, i)));
        return builder.Session;
    }

    private static Task<T> WithHex<T>(Func<Window, HexView, T> body, ThemeVariant? theme = null) =>
        HeadlessSession.Run(() => UiPump.InTheme(theme ?? ThemeVariant.Light, () =>
        {
            CaptureSession session = Long(200);
            HexLayout layout = new();
            lock (session.Gate) layout.Update(session, HexDirections.Both);
            HexView view = new()
            {
                Layout = layout, Sync = new SyncBus(new TestUi()), Width = ViewWidth, Height = ViewHeight,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            };
            Window host = new() { Width = ViewWidth + 40, Height = ViewHeight + 40, Content = view };
            host.Show();
            UiPump.Settle();
            try
            {
                return body(host, view);
            }
            finally
            {
                host.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }));

    private static Point In(Control view, Window window, double fromRight) =>
        view.TranslatePoint(new Point(view.Bounds.Width - fromRight, view.Bounds.Height / 2), window)!.Value;

    private static Thumb ThumbOf(ScrollBar bar) => bar.GetVisualDescendants().OfType<Thumb>().First();

    private static Color? ThumbFill(Thumb thumb) =>
        thumb.GetSelfAndVisualDescendants().OfType<Border>().Select(b => UiPump.ColorOf(b.Background)).FirstOrDefault(c => c is { A: > 0 });

    [Test]
    [Arguments("Light")]
    [Arguments("Dark")]
    public async Task The_hex_bar_widens_to_the_rail_near_the_edge_and_collapses_away(string variant)
    {
        ThemeVariant theme = UiPump.Variant(variant);
        var seen = await WithHex((window, view) =>
        {
            ScrollBar bar = view.ScrollBar;
            double rest = bar.Bounds.Width;
            double textWidth = view.TextView.Bounds.Width;
            window.MouseMove(In(view, window, 15), RawInputModifiers.None);
            UiPump.Settle();
            double near = bar.Bounds.Width;
            bool thumb = ThumbFill(ThumbOf(bar)) == UiPump.Token("Brush.Scroll.ThumbNear", theme);
            double textNear = view.TextView.Bounds.Width;
            window.MouseMove(In(view, window, 60), RawInputModifiers.None);
            UiPump.Settle();
            return (Visible: bar.IsEffectivelyVisible, Rail: ScrollRail.IsRail(bar), rest, near, thumb, Away: bar.Bounds.Width,
                Right: view.Bounds.Width - (bar.TranslatePoint(new Point(bar.Bounds.Width, 0), view)?.X ?? 0), textWidth, textNear);
        }, theme);

        using (Assert.Multiple())
        {
            await Assert.That(seen.Visible).IsTrue();
            await Assert.That(seen.Rail).IsTrue();
            await Assert.That(seen.rest).IsEqualTo(RailScrollMath.RestWidth).Within(0.01);
            await Assert.That(seen.near).IsEqualTo(RailScrollMath.NearWidth).Within(0.01);
            await Assert.That(seen.thumb).IsTrue().Because("the near thumb is scroll.thumb-near");
            await Assert.That(seen.Away).IsEqualTo(RailScrollMath.RestWidth).Within(0.01);
            await Assert.That(seen.Right).IsEqualTo(RailScrollMath.InsetX).Within(0.01).Because("2 px from the edge, like every rail");
            await Assert.That(seen.textNear).IsEqualTo(seen.textWidth).Because("the rail overlays the bytes");
        }
    }

    [Test]
    public async Task Pressing_the_hex_track_below_the_thumb_pages_and_the_wheel_leaves_it_at_rest()
    {
        var seen = await WithHex((window, view) =>
        {
            Point middle = view.TranslatePoint(new Point(view.Bounds.Width / 2, view.Bounds.Height / 2), window)!.Value;
            window.MouseWheel(middle, new Vector(0, -1), RawInputModifiers.None);
            UiPump.Settle();
            double afterWheel = view.ScrollBar.Bounds.Width;
            view.ScrollToLine(0);
            window.MouseMove(In(view, window, 8), RawInputModifiers.None);
            UiPump.Settle();
            ScrollBar bar = view.ScrollBar;
            Point below = bar.TranslatePoint(new Point(bar.Bounds.Width / 2, bar.Bounds.Height - 10), window)!.Value;
            window.MouseDown(below, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(below, MouseButton.Left, RawInputModifiers.None);
            UiPump.Settle();
            return (afterWheel, view.TopLine, view.VisibleLines);
        });

        await Assert.That(seen.afterWheel).IsEqualTo(RailScrollMath.RestWidth).Within(0.01);
        await Assert.That((double)seen.TopLine).IsEqualTo(Math.Round(RailScrollMath.PageFraction * seen.VisibleLines)).Within(1);
    }

    [Test]
    public async Task The_A_B_hex_bar_is_the_rail_too()
    {
        CompareSession compare = CompareFixture.Variant();
        EntityFieldDiff diff = compare.GetFieldDiff(CompareFixture.Sends(compare)[0].Index);
        var seen = await HeadlessSession.Run(() => UiPump.InTheme(ThemeVariant.Light, () =>
        {
            CompareHexModel model = new();
            model.Load(compare, diff);
            // Short enough that the two frames overflow it.
            CompareHexView view = new() { Model = model, Width = 600, Height = 70, VerticalAlignment = VerticalAlignment.Top };
            Window window = new() { Width = 640, Height = 120, Content = view };
            window.Show();
            UiPump.Settle();
            try
            {
                ScrollBar bar = view.ScrollBar;
                double rest = bar.Bounds.Width;
                window.MouseMove(In(view, window, 15), RawInputModifiers.None);
                UiPump.Settle();
                double near = bar.Bounds.Width;
                window.MouseMove(In(view, window, 60), RawInputModifiers.None);
                UiPump.Settle();
                return (bar.IsEffectivelyVisible, ScrollRail.IsRail(bar), rest, near, Away: bar.Bounds.Width);
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }));

        using (Assert.Multiple())
        {
            await Assert.That(seen.Item1).IsTrue().Because("the frames overflow the view");
            await Assert.That(seen.Item2).IsTrue();
            await Assert.That(seen.rest).IsEqualTo(RailScrollMath.RestWidth).Within(0.01);
            await Assert.That(seen.near).IsEqualTo(RailScrollMath.NearWidth).Within(0.01);
            await Assert.That(seen.Away).IsEqualTo(RailScrollMath.RestWidth).Within(0.01);
        }
    }

    [Test]
    public async Task The_compare_rows_bar_is_the_rail_and_overlays_the_rows()
    {
        var seen = await HeadlessSession.Run(() => UiPump.InTheme(ThemeVariant.Light, () =>
        {
            CompareView view = new();
            return (ScrollRail.IsRail(view.RowsScrollBar), Column: Grid.GetColumn(view.RowsScrollBar),
                Columns: (view.RowsScrollBar.Parent as Grid)?.ColumnDefinitions.Count ?? -1);
        }));

        await Assert.That(seen.Item1).IsTrue();
        await Assert.That(seen.Column).IsEqualTo(0);
        await Assert.That(seen.Columns).IsEqualTo(0).Because("a column of its own would narrow the rows as the rail widens");
    }

    [Test]
    public async Task Attaching_a_bar_twice_keeps_one_rail_and_a_plain_bar_is_none()
    {
        var seen = await HeadlessSession.Run(() =>
        {
            ScrollBar plain = new();
            ScrollBar bar = new();
            Border host = new();
            ScrollRail.Attach(bar, host);
            ScrollRail.Attach(bar, host);
            return (ScrollRail.IsRail(plain), ScrollRail.IsRail(bar));
        });

        await Assert.That(seen.Item1).IsFalse();
        await Assert.That(seen.Item2).IsTrue();
    }
}
