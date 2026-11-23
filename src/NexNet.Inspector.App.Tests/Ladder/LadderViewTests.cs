using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using NexNet.Inspector.App.Controls.Ladder;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;
using static NexNet.Inspector.App.Tests.Ladder.LadderDataBuilder;

namespace NexNet.Inspector.App.Tests.Ladder;

/// <summary>
/// The ladder view on the App's headless session: rows are virtualized (a screenful of controls for any number of
/// frames), a click selects a frame and moves the playhead, a double-click traces its message, the selection made
/// elsewhere scrolls into view and brackets its trace, scrolling and the shared window follow each other while synced,
/// and switching to time-scaled rows keeps the top row in place.
/// </summary>
[NotInParallel("avalonia")]
public sealed class LadderViewTests
{
    private const double Width = 760;

    internal sealed record Probe(Window Window, LadderView View, SyncBus Sync, LadderModel Model);

    internal static Task<T> With<T>(LadderModel model, Func<Probe, T> body, double height = 400, TimeWindow? window = null,
        ThemeVariant? theme = null) =>
        HeadlessSession.Run(() => UiPump.InTheme(theme ?? ThemeVariant.Light, () =>
        {
            SyncBus sync = new(new TestUi());
            if (model.Count > 0) sync.SetBounds(new TimeWindow(model[0].Ticks, model[model.Count - 1].Ticks + 1));
            if (window is { } w) sync.SetWindow(w, byUser: false);
            LadderView view = new() { Model = model, Sync = sync };
            Window host = new() { Width = Width, Height = height, Content = view };
            host.Show();
            UiPump.Frames();
            try
            {
                return body(new Probe(host, view, sync, model));
            }
            finally
            {
                host.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }));

    private static Point RowPoint(LadderView view, int index, double x = 300) =>
        new(x, LadderHeader.HeaderHeight + view.Layout.Top(index) + view.Layout.Gap(index) + 9 - view.Scroller.Offset.Y);

    [Test]
    public async Task A_hundred_thousand_frames_realize_only_a_screenful_of_rows()
    {
        (int controls, int realized, double extent, int first) = await With(Busy(100_000), p =>
        {
            p.View.Scroller.Offset = new Vector(0, 18 * 50_000);
            UiPump.Frames();
            return (p.View.RowControls, p.View.RealizedRows.Count, p.View.Scroller.Extent.Height, p.View.FirstRealized);
        });

        await Assert.That(extent).IsEqualTo(18.0 * 100_000);
        await Assert.That(realized).IsBetween(20, 40);
        await Assert.That(controls).IsLessThan(80);
        await Assert.That(first).IsBetween(49_990, 50_000);
    }

    [Test]
    public async Task Clicking_a_row_selects_its_frame_and_moves_the_playhead()
    {
        (Selection selection, long playhead, int selected, bool drawnSelected, LadderTrace trace) = await With(Demo(), p =>
        {
            Point point = RowPoint(p.View, 6);
            p.Window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            p.Window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            UiPump.Frames();
            return (p.Sync.Selection, p.Sync.Playhead, p.View.SelectedIndex, p.View.RowControl(6)!.State.Selected, p.View.Trace);
        });

        Selection expected = Selection.OfFrame(Demo()[6].Frame);
        await Assert.That(selection).IsEqualTo(expected);
        await Assert.That(playhead).IsEqualTo(Ticks(1_262.418));
        await Assert.That(selected).IsEqualTo(6);
        await Assert.That(drawnSelected).IsTrue();
        await Assert.That(trace.First).IsEqualTo(0);
        await Assert.That(trace.Last).IsEqualTo(14);
    }

    [Test]
    public async Task Double_click_or_enter_traces_the_rows_message()
    {
        (Selection clicked, Selection entered) = await With(Demo(), p =>
        {
            Point point = RowPoint(p.View, 9);
            for (int click = 0; click < 2; click++)
            {
                p.Window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
                p.Window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            }

            UiPump.Frames();
            Selection traced = p.Sync.Selection;

            p.Sync.Select(Selection.OfFrame(p.Model[4].Frame));
            UiPump.Frames();
            p.View.Focus();
            p.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            UiPump.Frames();
            return (traced, p.Sync.Selection);
        });

        LadderModel model = Demo();
        await Assert.That(clicked).IsEqualTo(Selection.OfMessage(model.MessageOf(Argument812), model[9].Frame));
        await Assert.That(entered).IsEqualTo(Selection.OfMessage(MessageRef.ForFrame(TapDirection.Inbound, model[4].Frame.Seq), model[4].Frame));
    }

    [Test]
    public async Task The_trace_bracket_and_dimming_follow_a_selection_made_elsewhere()
    {
        (LadderRowState[] states, double[] opacity) = await With(Demo(), p =>
        {
            p.Sync.Select(Selection.OfEntity(EntityKind.Invocation, Invocation812));
            UiPump.Frames();
            return (Enumerable.Range(0, 16).Select(i => p.View.RowControl(i)!.State).ToArray(),
                Enumerable.Range(0, 16).Select(i => p.View.RowControl(i)!.Opacity).ToArray());
        }, height: 500);

        await Assert.That(states[0]).IsEqualTo(new LadderRowState(false, false, LadderBracket.Member, true, false));
        await Assert.That(states[1].Bracket).IsEqualTo(LadderBracket.Interleaved);
        await Assert.That(opacity[1]).IsEqualTo(0.6);
        await Assert.That(states[6]).IsEqualTo(new LadderRowState(false, false, LadderBracket.Member, false, false));
        await Assert.That(opacity[6]).IsEqualTo(1.0);
        await Assert.That(states[14]).IsEqualTo(new LadderRowState(false, false, LadderBracket.Member, false, true));
        await Assert.That(states[15].Bracket).IsEqualTo(LadderBracket.None);
    }

    [Test]
    public async Task Arrow_keys_step_the_selection_and_a_far_selection_scrolls_into_view()
    {
        (int afterDown, double offset, bool visible) = await With(Busy(5_000), p =>
        {
            p.View.Select(10);
            UiPump.Frames();
            p.View.Focus();
            p.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            UiPump.Frames();
            int down = p.View.SelectedIndex;

            p.Sync.Select(Selection.OfFrame(p.Model[3_000].Frame));
            UiPump.Frames();
            return (down, p.View.Scroller.Offset.Y, p.View.RowControl(3_000) is { IsVisible: true });
        });

        await Assert.That(afterDown).IsEqualTo(11);
        await Assert.That(visible).IsTrue();
        await Assert.That(offset).IsBetween((3_000 * 18) - 400, 3_000 * 18);
    }

    [Test]
    public async Task Scrolling_pans_the_shared_window_and_moving_the_window_scrolls_the_ladder()
    {
        LadderModel model = Busy(5_000);
        (TimeWindow scrolled, double followed, bool unsynced) = await With(model, p =>
        {
            long duration = p.Sync.Window.Duration;
            p.View.Scroller.Offset = new Vector(0, 18 * 1_000);
            UiPump.Frames();
            TimeWindow afterScroll = p.Sync.Window;

            p.Sync.SetWindow(TimeWindow.FromDuration(p.Model[2_500].Ticks, duration));
            UiPump.Frames();
            double offset = p.View.Scroller.Offset.Y;

            p.View.IsSynced = false;
            p.View.Scroller.Offset = new Vector(0, 18 * 10);
            UiPump.Frames();
            return (afterScroll, offset, p.Sync.Window.Start == p.Model[2_500].Ticks);
        }, window: Window(0, 50));

        await Assert.That(scrolled.Start).IsEqualTo(model[1_000].Ticks);
        await Assert.That(scrolled.Duration).IsEqualTo(Ticks(50));
        await Assert.That(followed).IsEqualTo(18.0 * 2_500);
        await Assert.That(unsynced).IsTrue();
    }

    [Test]
    public async Task Time_scaled_rows_are_taller_and_the_switch_keeps_the_top_row()
    {
        (double compact, double scaled, int topBefore, int topAfter, double gap) = await With(Busy(2_000), p =>
        {
            p.View.Scroller.Offset = new Vector(0, 18 * 700);
            UiPump.Frames();
            double before = p.View.Scroller.Extent.Height;
            int top = p.View.Layout.IndexAt(p.View.Scroller.Offset.Y);
            p.View.Mode = LadderMode.TimeScaled;
            UiPump.Frames();
            return (before, p.View.Scroller.Extent.Height, top, p.View.Layout.IndexAt(p.View.Scroller.Offset.Y), p.View.RowControl(704)!.Gap);
        });

        await Assert.That(compact).IsEqualTo(18.0 * 2_000);
        await Assert.That(scaled).IsGreaterThan(compact);
        await Assert.That(topAfter).IsEqualTo(topBefore);
        await Assert.That(gap).IsGreaterThan(0);
    }

    [Test]
    public async Task The_header_names_both_endpoints_and_hovering_raises_a_row()
    {
        (string left, string right, bool hovered, int index) = await With(Demo(), p =>
        {
            p.Window.MouseMove(RowPoint(p.View, 3));
            UiPump.Frames();
            return (p.View.Header.LeftEndpoint, p.View.Header.RightEndpoint, p.View.RowControl(3)!.State.Hovered, p.View.HoveredIndex);
        });

        await Assert.That(left).IsEqualTo("Client · 192.168.1.40");
        await Assert.That(right).IsEqualTo("Server");
        await Assert.That(hovered).IsTrue();
        await Assert.That(index).IsEqualTo(3);
    }
}
