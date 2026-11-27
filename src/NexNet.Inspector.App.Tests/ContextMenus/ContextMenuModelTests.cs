using System.Globalization;
using NexNet.Inspector.App.Controls;
using NexNet.Inspector.App.Controls.PropertyTree;
using NexNet.Inspector.App.Controls.Timeline;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.Tests.Inspector;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Tests.ContextMenus;

/// <summary>
/// The context menus' models (screen context-menus) over the Chat fixture: the lane menu (pin, expand, hide, trace,
/// show in a pane, close, copy name), the hex selection menu (copy as hex / C# / Base64, go to frame start, go to
/// offset, bookmark, export) and "Decode selection as ▸" read in the inspector.
/// </summary>
public class ContextMenuModelTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(DocumentsViewModel documents, InspectWorkspaceViewModel workspace, FileDocumentViewModel document, Capture.InspectLiveScreen.Picks picks)
        {
            Documents = documents;
            Workspace = workspace;
            Document = document;
            Picks = picks;
        }

        public DocumentsViewModel Documents { get; }

        public InspectWorkspaceViewModel Workspace { get; }

        public FileDocumentViewModel Document { get; }

        public Capture.InspectLiveScreen.Picks Picks { get; }

        public CaptureSession Session => Document.Session!;

        public SyncBus Sync => Documents.Sync;

        public List<string> Copied { get; } = [];

        public Task Copy(string text)
        {
            Copied.Add(text);
            return Task.CompletedTask;
        }

        public HexViewModel Hex => (HexViewModel)Workspace.Right.ContentFor(StreamPaneKind.Hex);

        public static async Task<Fixture> OpenAsync()
        {
            DocumentsViewModel documents = new(new TestUi(), null);
            InspectWorkspaceViewModel workspace = new(documents, new ShellChromeViewModel(documents));
            var (document, picks) = await ChatFixture.OpenAsync(documents);
            documents.Sync.SetWindow(picks.Window, byUser: false);
            workspace.Timeline.Refresh();
            return new Fixture(documents, workspace, document, picks);
        }

        /// <summary>The FileName member's bytes in SendAttachment's argument: one MessagePack string in one fragment.</summary>
        public PropertyBytes FileName()
        {
            InspectorContent content;
            lock (Session.Gate) content = InspectorProjection.Build(Session, Picks.Chunk)!;
            return content.Tree.Rows.First(r => r.Name == "FileName").Bytes;
        }

        /// <summary>The frame holding a stream byte.</summary>
        public FrameKey FrameAt(TapDirection direction, long offset)
        {
            lock (Session.Gate)
            {
                ReadOnlySpan<FrameRecord> frames = Session.Assembler.GetFrames(direction);
                int at = Controls.Hex.HexLayout.FrameAt(frames, offset);
                return new FrameKey(direction, at >= 0 ? frames[at].Seq : -1);
            }
        }

        /// <summary>Selects bytes as a hex drag does: with the frame that holds their first byte.</summary>
        public void SelectBytes(TapDirection direction, StreamRange range) =>
            Sync.Select(Selection.OfBytes(direction, range) with { Frame = FrameAt(direction, range.Offset) });

        public async ValueTask DisposeAsync()
        {
            Workspace.Dispose();
            await Documents.DisposeAsync();
        }
    }

    [Test]
    public async Task Bytes_copy_as_hex_a_CSharp_array_and_Base64_and_the_header_names_the_range()
    {
        byte[] bytes = [0x01, 0x72, 0x00, 0xFF];
        await Assert.That(ByteText.Hex(bytes)).IsEqualTo("01 72 00 FF");
        await Assert.That(ByteText.Hex([])).IsEqualTo("");
        await Assert.That(ByteText.CSharp(bytes).ReplaceLineEndings("\n")).IsEqualTo("new byte[]\n{\n    0x01, 0x72, 0x00, 0xFF\n}");
        await Assert.That(ByteText.CSharp(new byte[17]).ReplaceLineEndings("\n").Split('\n').Length).IsEqualTo(5);
        await Assert.That(ByteText.Base64(bytes)).IsEqualTo("AXIA/w==");
        await Assert.That(ByteText.Count(16)).IsEqualTo("16 bytes selected");
        await Assert.That(ByteText.Count(1)).IsEqualTo("1 byte selected");
        await Assert.That(ByteText.Range(0x1A3F21, 16)).IsEqualTo("1A3F21 – 1A3F30");
        await Assert.That(ByteText.Range(0x21, 1)).IsEqualTo("000021");
    }

    [Test]
    public async Task A_picked_decode_lasts_while_the_selection_stays_inside_its_bytes()
    {
        SyncBus sync = new(new TestUi());
        List<SyncChanges> seen = [];
        sync.Changed += seen.Add;

        sync.SetDecode(DecodeAs.Utf8);
        await Assert.That(sync.Decode.IsAuto).IsTrue(); // only a byte range can be decoded so

        sync.Select(Selection.OfBytes(TapDirection.Inbound, new StreamRange(100, 16)));
        sync.SetDecode(DecodeAs.Utf8);
        await Assert.That(sync.Decode).IsEqualTo(new SelectionDecode(DecodeAs.Utf8, TapDirection.Inbound, new StreamRange(100, 16)));
        await Assert.That(seen[^1] & SyncChanges.Decode).IsEqualTo(SyncChanges.Decode);

        sync.Select(Selection.OfBytes(TapDirection.Inbound, new StreamRange(104, 4)));
        await Assert.That(sync.Decode.Mode).IsEqualTo(DecodeAs.Utf8);

        sync.Select(Selection.OfBytes(TapDirection.Outbound, new StreamRange(104, 4)));
        await Assert.That(sync.Decode.IsAuto).IsTrue();
        await Assert.That(seen[^1] | seen[^2]).IsEqualTo(SyncChanges.Selection | SyncChanges.Decode);
    }

    [Test]
    public async Task Go_to_offset_parses_hex_and_refuses_what_the_stream_does_not_hold()
    {
        await Assert.That(GoToOffsetDialogViewModel.TryParse("1A3F21", out long a) ? a : -1).IsEqualTo(0x1A3F21);
        await Assert.That(GoToOffsetDialogViewModel.TryParse(" 0x10 ", out long b) ? b : -1).IsEqualTo(16);
        await Assert.That(GoToOffsetDialogViewModel.TryParse("xyz", out _)).IsFalse();
        await Assert.That(GoToOffsetDialogViewModel.TryParse("", out _)).IsFalse();

        GoToOffsetDialogViewModel dialog = new(new HexPosition(TapDirection.Outbound, 0x20), d => d == TapDirection.Inbound ? (0, 0x100) : (0, 0));
        int closed = 0;
        dialog.Closed += _ => closed++;
        await Assert.That(dialog.Text).IsEqualTo("000020");
        await Assert.That(dialog.Direction.Value).IsEqualTo(TapDirection.Outbound);

        dialog.SubmitCommand.Execute(null);
        await Assert.That(dialog.Error).IsEqualTo("No outbound bytes are held");
        dialog.Direction.Value = TapDirection.Inbound;
        await Assert.That(dialog.HasError).IsFalse();
        dialog.Text = "200";
        dialog.SubmitCommand.Execute(null);
        await Assert.That(dialog.Error).IsEqualTo("Outside the stream: 000000 – 0000FF");
        dialog.Text = "nope";
        dialog.SubmitCommand.Execute(null);
        await Assert.That(dialog.HasError).IsTrue();
        dialog.Text = "0xFF";
        dialog.SubmitCommand.Execute(null);
        await Assert.That(await dialog.Result).IsEqualTo(new HexPosition(TapDirection.Inbound, 0xFF));
        await Assert.That(closed).IsEqualTo(1);
        dialog.CancelCommand.Execute(null);
        await Assert.That(closed).IsEqualTo(1);
    }

    [Test]
    public async Task The_lane_menu_pins_expands_hides_traces_and_shows_a_pipe_in_the_panes()
    {
        await using Fixture f = await Fixture.OpenAsync();
        LaneKey pipe = new(LaneKind.Pipe, f.Picks.Pipe);
        LaneMenuViewModel menu = LaneMenuViewModel.For(f.Workspace, pipe, f.Copy)!;
        TimelineModel model = f.Workspace.Timeline.Model;

        await Assert.That(menu.Glyph).IsEqualTo(LaneGlyph.Pipe);
        await Assert.That(menu.Name).StartsWith("Pipe ");
        await Assert.That(menu.Detail).IsNotNull();
        await Assert.That(menu.IsPinned).IsFalse();
        await Assert.That(menu.CanTrace).IsTrue();
        // The upload's pipe closes inside the window: "Close pipe in view" can drop it now; a pipe still open cannot.
        await Assert.That(menu.CanClose).IsTrue();
        TimelineLane open = model.Lanes.First(l => l.Visibility == LaneVisibility.Lifetime && l.LifeEnd == long.MaxValue);
        await Assert.That(LaneMenuViewModel.For(f.Workspace, open.Key, f.Copy)!.CanClose).IsFalse();
        await Assert.That(menu.CanCompare).IsFalse();
        await Assert.That(menu.CanSelectAll).IsFalse();
        await Assert.That(LaneMenuViewModel.For(f.Workspace, new LaneKey(LaneKind.Pipe, 9999), f.Copy)).IsNull();

        menu.TogglePin();
        await Assert.That(model.IsPinned(pipe)).IsTrue();
        await Assert.That(LaneMenuViewModel.For(f.Workspace, pipe, f.Copy)!.IsPinned).IsTrue();
        menu.ToggleExpanded();
        await Assert.That(menu.IsExpanded).IsTrue();

        await Assert.That(await menu.CopyNameAsync()).IsTrue();
        await Assert.That(f.Copied[^1]).IsEqualTo(menu.Name + " " + menu.Detail);

        f.Workspace.Left.View.Value = StreamPaneKind.Events;
        await Assert.That(menu.ShowIn(StreamPaneKind.Ladder)).IsTrue();
        await Assert.That(f.Workspace.Left.View.Value).IsEqualTo(StreamPaneKind.Ladder);
        await Assert.That(f.Sync.Selection).IsEqualTo(Selection.OfEntity(EntityKind.Pipe, f.Picks.Pipe));
        await Assert.That(f.Sync.Trace.Kind).IsEqualTo(TraceKind.Pipe);
        f.Workspace.Right.View.Value = StreamPaneKind.Events;
        menu.ShowIn(StreamPaneKind.Hex);
        await Assert.That(f.Workspace.Right.View.Value).IsEqualTo(StreamPaneKind.Hex);
        await Assert.That(f.Workspace.Left.View.Value).IsEqualTo(StreamPaneKind.Ladder);

        menu.Hide();
        await Assert.That(model.IsHidden(pipe)).IsTrue();
        await Assert.That(model.IsPinned(pipe)).IsFalse(); // a pin would keep it
        TimelineLayout layout = new(new LaneAnimator(enabled: false));
        layout.Update(model, f.Picks.Window, 0);
        await Assert.That(layout.Rows.ToArray().Any(r => r.Lane?.Key == pipe)).IsFalse();
        await Assert.That(model.HiddenCount(LaneGroupKind.Pipes)).IsEqualTo(1);
        await Assert.That(model.ShowHidden(LaneGroupKind.Pipes)).IsEqualTo(1);
        layout.Update(model, f.Picks.Window, 0);
        LayoutRow row = layout.Rows.ToArray().Single(r => r.Lane?.Key == pipe);
        await Assert.That(row.Expanded).IsTrue(); // expanded by hand, without a pin
        await Assert.That(row.Height).IsEqualTo(TimelineLayout.ExpandedHeight);
    }

    [Test]
    public async Task Close_pipe_in_view_applies_only_to_a_pipe_that_closed_in_view_and_frame_lanes_trace_their_first_frame()
    {
        await using Fixture f = await Fixture.OpenAsync();
        TimelineModel model = f.Workspace.Timeline.Model;
        TimelineLane? closed = model.Lanes.FirstOrDefault(l => l.Visibility == LaneVisibility.Lifetime && l.LifeEnd != long.MaxValue);
        await Assert.That(closed).IsNotNull();
        f.Sync.SetWindow(new TimeWindow(closed!.LifeStart, closed.LifeEnd + 1), byUser: false);
        LaneMenuViewModel menu = LaneMenuViewModel.For(f.Workspace, closed.Key, f.Copy)!;
        await Assert.That(menu.CanClose).IsTrue();
        menu.CloseInView();
        await Assert.That(model.IsHidden(closed.Key)).IsTrue();

        f.Sync.SetWindow(f.Picks.Window, byUser: false);
        LaneMenuViewModel frames = LaneMenuViewModel.For(f.Workspace, new LaneKey(LaneKind.InboundFrames, 0), f.Copy)!;
        await Assert.That(frames.CanClose).IsFalse();
        await Assert.That(frames.Trace()).IsTrue();
        await Assert.That(f.Sync.Selection.Kind).IsEqualTo(SelectionKind.Frame);
        CaptureSession session = f.Session;
        long start;
        lock (session.Gate)
        {
            session.TryGetFrame(f.Sync.Selection.Frame, out FrameRecord frame);
            start = frame.StartTicks;
        }

        await Assert.That(start).IsGreaterThanOrEqualTo(f.Picks.Window.Start - (session.Header.StopwatchFrequency / 10));
        await Assert.That(f.Sync.Playhead).IsEqualTo(start);
    }

    [Test]
    public async Task The_hex_menu_copies_exports_bookmarks_and_goes_to_the_frame_start()
    {
        await using Fixture f = await Fixture.OpenAsync();
        PropertyBytes name = f.FileName();
        f.SelectBytes(name.Direction, name.Stream);
        HexSelectionViewModel menu = new(f.Hex, f.Copy);
        byte[] bytes = menu.ReadBytes()!;

        await Assert.That(menu.HasBytes).IsTrue();
        await Assert.That(bytes.Length).IsEqualTo((int)name.Stream.Length);
        await Assert.That(menu.CountText).IsEqualTo(ByteText.Count(name.Stream.Length));
        await Assert.That(menu.RangeText).IsEqualTo(ByteText.Range(name.Stream.Offset, name.Stream.Length));
        await Assert.That(menu.CanCopy).IsTrue();
        await Assert.That(await menu.CopyAsync(ByteCopyFormat.Hex)).IsTrue();
        await Assert.That(await menu.CopyAsync(ByteCopyFormat.CSharp)).IsTrue();
        await Assert.That(await menu.CopyAsync(ByteCopyFormat.Base64)).IsTrue();
        await Assert.That(f.Copied).IsEquivalentTo(new[] { ByteText.Hex(bytes), ByteText.CSharp(bytes), ByteText.Base64(bytes) });
        await Assert.That(new HexSelectionViewModel(f.Hex, null).CanCopy).IsFalse();

        using MemoryStream exported = new();
        await Assert.That(await menu.ExportAsync(exported)).IsTrue();
        await Assert.That(exported.ToArray()).IsEquivalentTo(bytes);
        await Assert.That(menu.ExportName).IsEqualTo((name.Direction == TapDirection.Inbound ? "inbound-" : "outbound-")
                                                    + ByteText.Offset(name.Stream.Offset) + "-" + name.Stream.Length.ToString(CultureInfo.InvariantCulture) + "B.bin");

        await Assert.That(menu.IsBookmarked).IsFalse();
        await Assert.That(menu.ToggleBookmark()).IsTrue();
        await Assert.That(f.Hex.Bookmarks).IsEquivalentTo(new[] { new HexPosition(name.Direction, name.Stream.Offset) });
        await Assert.That(new HexSelectionViewModel(f.Hex, f.Copy).IsBookmarked).IsTrue();
        await Assert.That(menu.ToggleBookmark()).IsFalse();
        await Assert.That(f.Hex.Bookmarks.Count).IsEqualTo(0);

        await Assert.That(menu.CanGoToFrameStart).IsTrue();
        await Assert.That(menu.GoToFrameStart()).IsTrue();
        await Assert.That(f.Sync.Selection).IsEqualTo(Selection.OfFrame(f.FrameAt(name.Direction, name.Stream.Offset)));

        f.Sync.Select(Selection.OfFrame(f.Picks.Chunk));
        HexSelectionViewModel none = new(f.Hex, f.Copy);
        await Assert.That(none.HasBytes).IsFalse();
        await Assert.That(none.CountText).IsEqualTo("No bytes selected");
        await Assert.That(none.CanCopy).IsFalse();
    }

    [Test]
    public async Task Go_to_offset_opens_its_dialog_through_the_workspace_and_selects_the_byte()
    {
        await using Fixture f = await Fixture.OpenAsync();
        PropertyBytes name = f.FileName();
        f.SelectBytes(name.Direction, name.Stream);
        // Without a dialog host nothing opens and nothing moves.
        await Assert.That(await new HexSelectionViewModel(f.Hex, f.Copy).GoToOffsetAsync()).IsNull();
        await Assert.That(f.Sync.Selection.Bytes).IsEqualTo(name.Stream);

        GoToOffsetDialogViewModel? shown = null;
        f.Workspace.ShowDialog = d =>
        {
            shown = d;
            return true;
        };
        HexSelectionViewModel menu = new(f.Hex, f.Copy);
        await Assert.That(menu.CanGoToOffset).IsTrue();
        Task<HexPosition?> going = menu.GoToOffsetAsync();
        await Assert.That(shown).IsNotNull();
        await Assert.That(shown!.Text).IsEqualTo(ByteText.Offset(name.Stream.Offset));
        long target = name.Stream.Offset + 2;
        shown.Text = target.ToString("X", CultureInfo.InvariantCulture);
        shown.SubmitCommand.Execute(null);
        await Assert.That(await going).IsEqualTo(new HexPosition(name.Direction, target));
        await Assert.That(f.Sync.Selection.Kind).IsEqualTo(SelectionKind.ByteRange);
        await Assert.That(f.Sync.Selection.Bytes).IsEqualTo(new StreamRange(target, 1));
        await Assert.That(f.Sync.Selection.Frame).IsEqualTo(f.FrameAt(name.Direction, target));

        // A canceled dialog goes nowhere.
        Task<HexPosition?> canceled = new HexSelectionViewModel(f.Hex, f.Copy).GoToOffsetAsync();
        shown.CancelCommand.Execute(null);
        await Assert.That(await canceled).IsNull();
    }

    [Test]
    public async Task Decode_selection_as_shows_the_bytes_read_another_way_in_the_inspector_until_the_selection_leaves_them()
    {
        await using Fixture f = await Fixture.OpenAsync();
        InspectorViewModel inspector = f.Workspace.Inspector;
        PropertyBytes name = f.FileName();
        f.SelectBytes(name.Direction, name.Stream);
        string fileName = inspector.Rows!.First(r => r.Name == "FileName").Value;
        HexSelectionViewModel menu = new(f.Hex, f.Copy);
        await Assert.That(menu.Decode).IsEqualTo(DecodeAs.Auto);

        menu.DecodeSelectionAs(DecodeAs.MessagePack);
        await Assert.That(new HexSelectionViewModel(f.Hex, f.Copy).Decode).IsEqualTo(DecodeAs.MessagePack);
        PropertyRow[] rows = [.. inspector.Rows!];
        await Assert.That(rows[0].Name).IsEqualTo("MessagePack value");
        await Assert.That(rows[0].Value).IsEqualTo(name.Stream.Length.ToString("N0", CultureInfo.InvariantCulture) + " B · " + ByteText.Range(name.Stream.Offset, name.Stream.Length));
        await Assert.That(rows[1].Name).IsEqualTo("value");
        await Assert.That(rows[1].Value).IsEqualTo(fileName);
        await Assert.That(rows[1].Bytes.Stream).IsEqualTo(name.Stream);
        await Assert.That(inspector.Assembly).IsNull();

        menu.DecodeSelectionAs(DecodeAs.Utf8);
        rows = [.. inspector.Rows!];
        await Assert.That(rows[0].Name).IsEqualTo("UTF-8 text");
        await Assert.That(rows[1].Name).IsEqualTo("Text");
        await Assert.That(rows[1].Value).EndsWith(fileName.Trim('"')); // the string's header byte comes first

        menu.DecodeSelectionAs(DecodeAs.Leb128);
        rows = [.. inspector.Rows!];
        await Assert.That(rows[0].Name).IsEqualTo("LEB128 length + item");
        await Assert.That(rows[1].Name).IsEqualTo("Length");
        // 0xAE (fixstr) has its high bit set, so the length runs on into the next byte.
        await Assert.That(rows[1].Bytes.Stream).IsEqualTo(new StreamRange(name.Stream.Offset, 2));

        menu.DecodeSelectionAs(DecodeAs.Raw);
        rows = [.. inspector.Rows!];
        await Assert.That(rows[0].Name).IsEqualTo("Raw bytes");
        await Assert.That(rows[1].Name).IsEqualTo(ByteText.Offset(name.Stream.Offset));
        await Assert.That(rows[1].Value).IsEqualTo(ByteText.Hex(menu.ReadBytes().AsSpan(0, (int)Math.Min(16, name.Stream.Length))));

        // A click on a decoded row selects bytes inside the decode: it stays.
        f.SelectBytes(name.Direction, new StreamRange(name.Stream.Offset, 1));
        await Assert.That(inspector.Rows![0].Name).IsEqualTo("Raw bytes");

        // Anything else ends it: the frame's own tree comes back.
        f.Sync.Select(Selection.OfFrame(f.Picks.Chunk));
        await Assert.That(f.Sync.Decode.IsAuto).IsTrue();
        await Assert.That(inspector.Rows![0].Name).IsEqualTo("Frame header");

        f.SelectBytes(name.Direction, name.Stream);
        menu = new HexSelectionViewModel(f.Hex, f.Copy);
        menu.DecodeSelectionAs(DecodeAs.Utf8);
        menu.DecodeSelectionAs(DecodeAs.Auto);
        await Assert.That(inspector.Rows!.Any(r => r.Name == "FileName")).IsTrue();
    }
}
