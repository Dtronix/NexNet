using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit.Rendering;
using NexNet.Inspector.App.Controls.Hex;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;
using static NexNet.Inspector.App.Tests.Hex.HexCaptureBuilder;

namespace NexNet.Inspector.App.Tests.Hex;

/// <summary>
/// The hex view on the App's headless session: the AvaloniaEdit document holds only the visible window, hovering a byte
/// rings its field and publishes the decoded field, a field hovered in the tree rings its bytes, a click selects the
/// field and a drag a range, a selection made elsewhere scrolls into view, the wheel and keys scroll, and a 100 MB
/// capture opens at once.
/// </summary>
[NotInParallel("avalonia")]
public sealed class HexViewTests
{
    internal sealed record Probe(Window Window, HexView View, SyncBus Sync, HexLayout Layout);

    internal static Task<T> With<T>(CaptureSession? session, Func<Probe, T> body, double height = 300) =>
        HeadlessSession.Run(() => UiPump.InTheme(ThemeVariant.Light, () =>
        {
            SyncBus sync = new(new TestUi());
            HexLayout layout = new();
            if (session is not null)
            {
                lock (session.Gate)
                    layout.Update(session, HexDirections.Both);
            }

            HexView view = new() { Layout = layout, Sync = sync, Height = height, VerticalAlignment = VerticalAlignment.Top };
            Window host = new() { Width = 760, Height = height + 40, Content = view };
            host.Show();
            UiPump.Frames();
            try
            {
                return body(new Probe(host, view, sync, layout));
            }
            finally
            {
                host.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }));

    /// <summary>The window point over a byte's cell (it must be on screen).</summary>
    internal static Point CellPoint(Probe p, TapDirection direction, long offset)
    {
        HexView view = p.View;
        long line = p.Layout.FindLine(direction, offset);
        int index = (int)(line - view.TopLine);
        VisualLine visual = view.TextView.GetVisualLine(index + 1)
                            ?? throw new InvalidOperationException("the byte's line is not on screen");
        int cell = (int)(offset & 15);
        double x = visual.GetVisualPosition(HexText.CellColumn(view.Digits, cell), VisualYPosition.LineTop).X + 4;
        double y = visual.VisualTop - view.TextView.VerticalOffset + (visual.Height / 2);
        return view.TextView.TranslatePoint(new Point(x, y), p.Window) ?? throw new InvalidOperationException("not in the window");
    }

    private static CaptureSession Chunked() => new HexCaptureBuilder()
        .Op(TapDirection.Inbound, 0.1, Join(Preamble(), Ping(), Chunk(0x032C, 0, Pattern(20))))
        .Op(TapDirection.Inbound, 0.2, PipeWrite(0x0103, Pattern(8)))
        .Session;

    /// <summary>A stream of <paramref name="frames"/> pipe writes, one read each, alternating with a one-byte write.</summary>
    private static CaptureSession Long(int frames)
    {
        HexCaptureBuilder builder = new();
        builder.Op(TapDirection.Inbound, 0, Preamble()).Op(TapDirection.Outbound, 0.01, Preamble());
        for (int i = 0; i < frames; i++)
        {
            builder.Op(TapDirection.Inbound, 1 + i, PipeWrite(0x0101, Pattern(100, i)));
            if (i % 4 == 0)
                builder.Op(TapDirection.Outbound, 1.5 + i, Ping());
        }

        return builder.Session;
    }

    [Test]
    public async Task The_document_holds_only_the_visible_window()
    {
        CaptureSession session = Long(400);
        (long lines, int window, int visible, int documentLines, string first, bool hint) = await With(session, p =>
            (p.Layout.LineCount, p.View.WindowLineCount, p.View.VisibleLines, p.View.TextView.Document.LineCount,
                p.View.TextView.Document.GetText(p.View.TextView.Document.GetLineByNumber(2)).TrimEnd(), p.View.ShowsHint));

        await Assert.That(lines).IsGreaterThan(2000);
        await Assert.That(visible).IsEqualTo((300 - 12) / 20);
        await Assert.That(window).IsLessThanOrEqualTo(visible + 1);
        await Assert.That(documentLines).IsEqualTo(window);
        await Assert.That(first).IsEqualTo("000000   4E 6E 50 14 00 00 00 01                          NnP·····");
        await Assert.That(hint).IsFalse();
        await Assert.That(await With(null, p => (p.View.ShowsHint, p.View.WindowLineCount))).IsEqualTo((true, 0));
    }

    [Test]
    public async Task Hovering_a_byte_rings_its_header_field()
    {
        (bool hit, long offset, HexRange? local, bool marked, FieldHover hover, HexRange? left) = await With(Chunked(), p =>
        {
            Point point = CellPoint(p, TapDirection.Inbound, 11);
            p.Window.MouseMove(point, RawInputModifiers.None);
            UiPump.Frames();
            bool ok = p.View.TryHitTest(p.Window.TranslatePoint(point, p.View.TextView)!.Value, out _, out long at);
            int index = (int)(p.Layout.FindLine(TapDirection.Inbound, 11) - p.View.TopLine);
            bool ringed = (((ReadOnlySpan<HexCellMarks>)p.View.RowAt(index).Marks)[10] & HexCellMarks.Hovered) != 0;
            HexRange? before = p.View.HoveredBytes;
            FieldHover published = p.Sync.Hover;
            p.Window.MouseMove(new Point(1, 1), RawInputModifiers.None);
            UiPump.Frames();
            return (ok, at, before, ringed, published, p.View.HoveredBytes);
        });

        await Assert.That((hit, offset)).IsEqualTo((true, 11L));
        await Assert.That(local).IsEqualTo(new HexRange(TapDirection.Inbound, 10, 12));
        await Assert.That(marked).IsTrue();
        await Assert.That(hover.IsNone).IsTrue();
        await Assert.That(left).IsNull();
    }

    [Test]
    public async Task Hovering_a_decoded_byte_publishes_its_field_and_a_tree_hover_rings_its_bytes()
    {
        (RecordingFile file, CaptureSession session) = HexLinkTests.Open("chat-server.nntl");
        using (file)
        {
            (FrameRecord frame, DecodedValue value, int index) = HexLinkTests.DecodedArgument(session);
            DecodedField field = value.Fields[index];
            long offset = frame.BodyOffset + value.Range.Offset + field.Offset;
            (FieldHover published, int rings) = await With(session, p =>
            {
                p.View.Reveal(frame.Direction, offset);
                UiPump.Frames();
                p.Window.MouseMove(CellPoint(p, frame.Direction, offset), RawInputModifiers.None);
                UiPump.Frames();
                FieldHover fromHex = p.Sync.Hover;

                // The tree hovers the field: its bytes are ringed.
                p.View.HoverByte(default, -1);
                p.Sync.SetHover(new FieldHover(value.Range.Message, index, value.Range.Offset + field.Offset, field.Length));
                UiPump.Frames();
                return (fromHex, p.View.Highlight.Hovered.Sum(r => (int)(r.End - r.Start)));
            });

            await Assert.That(published.IsNone).IsFalse();
            await Assert.That(published.Message).IsEqualTo(value.Range.Message);
            await Assert.That(published.Offset).IsGreaterThanOrEqualTo(value.Range.Offset + field.Offset);
            await Assert.That(rings).IsEqualTo(field.Length);
        }
    }

    [Test]
    public async Task A_click_selects_the_field_and_a_drag_selects_a_range()
    {
        (Selection click, long playhead, Selection drag) = await With(Chunked(), p =>
        {
            Point type = CellPoint(p, TapDirection.Inbound, 9);
            p.Window.MouseDown(type, MouseButton.Left, RawInputModifiers.None);
            p.Window.MouseUp(type, MouseButton.Left, RawInputModifiers.None);
            UiPump.Frames();
            Selection clicked = p.Sync.Selection;
            long at = p.Sync.Playhead;

            Point end = CellPoint(p, TapDirection.Inbound, 14);
            p.Window.MouseDown(type, MouseButton.Left, RawInputModifiers.None);
            p.Window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            p.Window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
            UiPump.Frames();
            return (clicked, at, p.Sync.Selection);
        });

        await Assert.That(click.Kind).IsEqualTo(SelectionKind.ByteRange);
        await Assert.That((click.Direction, click.Bytes)).IsEqualTo((TapDirection.Inbound, new StreamRange(9, 1)));
        await Assert.That(click.Frame).IsEqualTo(new FrameKey(TapDirection.Inbound, 2));
        await Assert.That(playhead).IsEqualTo(100L);
        await Assert.That((drag.Kind, drag.Bytes)).IsEqualTo((SelectionKind.ByteRange, new StreamRange(9, 6)));
    }

    [Test]
    public async Task A_selection_made_elsewhere_scrolls_into_view()
    {
        CaptureSession session = Long(300);
        FrameRecord target;
        lock (session.Gate)
            target = session.Assembler.GetFrames(TapDirection.Inbound)[250];
        (long before, long after, long line, int visible, bool ringed) = await With(session, p =>
        {
            long top = p.View.TopLine;
            p.Sync.Select(Selection.OfFrame(new FrameKey(TapDirection.Inbound, target.Seq)), target.StartTicks);
            UiPump.Frames();
            return (top, p.View.TopLine, p.Layout.FindLine(TapDirection.Inbound, target.Offset), p.View.VisibleLines,
                p.View.Highlight.Selected.Contains(new HexRange(TapDirection.Inbound, target.Offset, target.Offset + 1)));
        });

        await Assert.That(before).IsEqualTo(0);
        await Assert.That(line).IsGreaterThanOrEqualTo(after);
        await Assert.That(line).IsLessThan(after + visible);
        await Assert.That(ringed).IsTrue();
    }

    [Test]
    public async Task The_wheel_and_keys_scroll_and_the_timeline_window_follows()
    {
        CaptureSession session = Long(300);
        (long wheel, long page, long end, long home, TimeWindow window) = await With(session, p =>
        {
            p.Sync.SetBounds(new TimeWindow(0, 400_000));
            p.Sync.SetWindow(new TimeWindow(0, 10_000), byUser: false);
            Point inside = CellPoint(p, TapDirection.Inbound, 0);
            p.Window.MouseWheel(inside, new Vector(0, -1), RawInputModifiers.None);
            UiPump.Frames();
            long afterWheel = p.View.TopLine;
            p.View.Focus();
            p.Window.KeyPress(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
            long afterPage = p.View.TopLine;
            p.Window.KeyPress(Key.End, RawInputModifiers.Control, PhysicalKey.End, null);
            long atEnd = p.View.TopLine;
            TimeWindow moved = p.Sync.Window;
            p.Window.KeyPress(Key.Home, RawInputModifiers.Control, PhysicalKey.Home, null);
            return (afterWheel, afterPage, atEnd, p.View.TopLine, moved);
        });

        await Assert.That(wheel).IsEqualTo(HexView.WheelLines);
        await Assert.That(page).IsGreaterThan(wheel);
        await Assert.That(end).IsGreaterThan(page);
        await Assert.That(home).IsEqualTo(0);
        // Scrolled to the last ops (near 300 ms), the window panned to keep them in view.
        await Assert.That(window.End).IsGreaterThan(290_000);
    }

    [Test]
    public async Task A_100_MB_capture_opens_at_once()
    {
        // 1,600 reads of 64 KB, four 16 KB pipe writes each: the shape of a large recording.
        HexCaptureBuilder builder = new();
        byte[] frame = PipeWrite(0x0101, Pattern(16_379));
        byte[] op = Join(frame, frame, frame, frame);
        builder.Op(TapDirection.Inbound, 0, Preamble());
        for (int i = 0; i < 1_600; i++)
            builder.Op(TapDirection.Inbound, 1 + (i * 0.01), op);
        CaptureSession session = builder.Session;
        await Assert.That(session.ByteCount).IsGreaterThan(100L * 1000 * 1000);

        (long layoutMs, long openMs, long lines, int text) = await HeadlessSession.Run(() =>
        {
            Stopwatch clock = Stopwatch.StartNew();
            HexLayout layout = new();
            lock (session.Gate)
                layout.Update(session, HexDirections.Both);
            long laid = clock.ElapsedMilliseconds;
            HexView view = new() { Layout = layout, Sync = new SyncBus(new TestUi()), Height = 600 };
            Window host = new() { Width = 760, Height = 640, Content = view };
            host.Show();
            UiPump.Frames();
            view.ScrollToLine(layout.LineCount / 2);
            UiPump.Frames();
            long opened = clock.ElapsedMilliseconds;
            int length = view.TextView.Document.TextLength;
            host.Close();
            Dispatcher.UIThread.RunJobs();
            return (laid, opened, layout.LineCount, length);
        });

        await Assert.That(lines).IsGreaterThan(100L * 1000 * 1000 / 16);
        // The document is the window, never the capture.
        await Assert.That(text).IsLessThan(10_000);
        await Assert.That(layoutMs).IsLessThan(500);
        await Assert.That(openMs).IsLessThan(2_000);
    }
}
