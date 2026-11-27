using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using NexNet.Inspector.App.Controls;
using NexNet.Inspector.App.Controls.DataTable.Events;
using NexNet.Inspector.App.Controls.Hex;
using NexNet.Inspector.App.Controls.Ladder;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.App.Controls.PropertyTree;
using NexNet.Inspector.App.Controls.Timeline;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Messages;

namespace NexNet.Inspector.App.Tests.Inspector;

/// <summary>
/// Channels and collections ride on duplex pipes, and the views say so (I41): the Chat fixture's TailLogs channel of
/// LogLine, its UploadFile raw pipes and its Users collection are named by what they carry (timeline lanes, Events and
/// ladder targets with the CH / CO chips and item counts, hex annotations), the inspector lists a channel frame's items
/// first and badges the channel's section, and the chips draw from caches.
/// </summary>
public class ChannelPipeTests
{
    private static (RecordingFile File, CaptureSession Session) Open()
    {
        RecordingFile file = RecordingFile.Open(Wait.Fixture("chat-server.nntl"));
        CaptureSession session = file.CreateSession(file.Sessions[0].SessionId);
        file.Index(session);
        return (file, session);
    }

    // The first pipe carrying content, and one of its data frames (a write that carried at least one item when it is a channel).
    private static (int Pipe, FrameKey Write) PipeOf(CaptureSession session, PipeContent content)
    {
        MessageAssembler assembler = session.Assembler;
        foreach (FrameKey key in session.FrameOrder)
        {
            if (!assembler.TryGetFrame(key.Direction, key.Seq, out FrameRecord frame) || frame.Type != MessageType.DuplexPipeWrite) continue;
            FrameLink link = assembler.GetLink(key.Direction, key.Seq);
            if (link.Entity != EntityKind.Pipe || assembler.Pipes[link.EntityIndex].Content != content) continue;
            if (content != PipeContent.Raw && PipeText.Find(assembler, link.EntityIndex, key.Direction, key.Seq).Count == 0) continue;
            return (link.EntityIndex, key);
        }

        throw new InvalidOperationException("the fixture has no " + content + " pipe write");
    }

    [Test]
    public async Task Pipes_are_named_by_what_they_carry()
    {
        await Assert.That(PipeText.Title(PipeContent.Channel, 36 | (36 << 8), "LogLine")).IsEqualTo("Channel<LogLine> 36·36");
        await Assert.That(PipeText.Title(PipeContent.Channel, 36 | (36 << 8), null)).IsEqualTo("Channel 36·36");
        await Assert.That(PipeText.Title(PipeContent.Collection, 1 | (1 << 8), "Users")).IsEqualTo("Collection Users 1·1");
        await Assert.That(PipeText.Title(PipeContent.Raw, 3 | (1 << 8), "ignored")).IsEqualTo("Pipe 3·1");
        await Assert.That(PipeText.Glyph(PipeContent.Channel)).IsEqualTo("≋");
        await Assert.That(PipeText.Glyph(PipeContent.Raw)).IsEqualTo("⇄");
        await Assert.That(PipeText.Glyph(PipeContent.Collection)).IsEqualTo("☰");
        await Assert.That(PipeText.Chip(PipeContent.Channel)).IsEqualTo("CH");
        await Assert.That(PipeText.Chip(PipeContent.Collection)).IsEqualTo("CO");
        await Assert.That(PipeText.Chip(PipeContent.Raw)).IsNull();
        await Assert.That(PipeText.WithItems("LogLine", 2, false)).IsEqualTo("LogLine ×2");
        await Assert.That(PipeText.WithItems("LogLine", 1, true)).IsEqualTo("LogLine (part)");
        await Assert.That(PipeText.WithItems("LogLine", 1, false)).IsEqualTo("LogLine");
        await Assert.That(PipeText.WithItems(null, 3, false)).IsEqualTo("×3");

        (RecordingFile file, CaptureSession session) = Open();
        using (file)
        {
            int channel = PipeOf(session, PipeContent.Channel).Pipe;
            int raw = PipeOf(session, PipeContent.Raw).Pipe;
            int collection = PipeOf(session, PipeContent.Collection).Pipe;
            string ids(int pipe) => PipeText.Ids(session.Assembler.Pipes[pipe].PipeId);
            await Assert.That(PipeText.Title(session, channel)).IsEqualTo("Channel<LogLine> " + ids(channel));
            await Assert.That(PipeText.Title(session, raw)).IsEqualTo("Pipe " + ids(raw));
            await Assert.That(PipeText.Title(session, collection)).IsEqualTo("Collection Users " + ids(collection));

            // Timeline lanes: ≋ Channel<LogLine> with its opener, ⇄ Pipe, ☰ the collection.
            TimelineModel model = new();
            new TimelineProjection(model).Update(session);
            TimelineLane Lane(int pipe) => model.FindLane(new LaneKey(LaneKind.Pipe, pipe))!;
            await Assert.That(Lane(channel).Glyph).IsEqualTo(LaneGlyph.Channel);
            await Assert.That(Lane(channel).Label).IsEqualTo("Channel<LogLine> " + ids(channel));
            await Assert.That(Lane(channel).SubLabel).StartsWith("TailLogs");
            await Assert.That(Lane(raw).Glyph).IsEqualTo(LaneGlyph.Pipe);
            await Assert.That(Lane(raw).Label).IsEqualTo("Pipe " + ids(raw));
            await Assert.That(Lane(collection).Glyph).IsEqualTo(LaneGlyph.Collection);
            await Assert.That(TimelineControl.PipeGlyphOf(LaneGlyph.Channel)).IsEqualTo(PipeContent.Channel);
            await Assert.That(TimelineControl.PipeGlyphOf(LaneGlyph.Pipe)).IsEqualTo(PipeContent.Raw);
            await Assert.That(TimelineControl.PipeGlyphOf(LaneGlyph.Collection)).IsEqualTo(PipeContent.Collection);
            await Assert.That(TimelineControl.PipeGlyphOf(LaneGlyph.Inbound)).IsNull();
        }
    }

    [Test]
    public async Task Events_and_ladder_rows_keep_the_wire_type_and_add_the_chip_and_item_count()
    {
        (RecordingFile file, CaptureSession session) = Open();
        using (file)
        {
            EventsTableSource events = new();
            events.Attach(session);
            LadderModel ladder = new();
            new LadderProjection(ladder).Update(session);

            List<EventRow> writes = [];
            for (int i = 0; i < events.Count; i++)
            {
                if (events.TryGetRow(i, out EventRow row) && row.Type == MessageType.DuplexPipeWrite) writes.Add(row);
            }

            EventRow channel = writes.First(r => r.Target == EventTargetKind.Channel);
            await Assert.That(EventText.Type(channel)).IsEqualTo("DuplexPipeWrite");
            await Assert.That(EventText.PipeKind(channel)).IsEqualTo(PipeContent.Channel);
            await Assert.That(EventText.Target(channel)).StartsWith("chan ");
            await Assert.That(EventText.Target(channel)).Contains(" LogLine");
            await Assert.That(channel.ItemCount).IsGreaterThanOrEqualTo(1);

            EventRow raw = writes.First(r => r.Target == EventTargetKind.Pipe);
            await Assert.That(EventText.PipeKind(raw)).IsEqualTo(PipeContent.Raw);
            await Assert.That(EventText.Target(raw)).StartsWith("pipe ");
            await Assert.That(raw.ItemCount).IsEqualTo(0);

            EventRow collection = writes.First(r => r.Target == EventTargetKind.Collection);
            await Assert.That(EventText.PipeKind(collection)).IsEqualTo(PipeContent.Collection);
            await Assert.That(EventText.Target(collection)).StartsWith("coll ");
            await Assert.That(EventText.Target(collection)).Contains(" Users");

            // The count follows the items: ×n only past one, "(part)" for an item that crosses frames.
            foreach (EventRow row in writes.Where(r => r.Target == EventTargetKind.Channel))
            {
                string target = EventText.Target(row);
                if (row.ItemCount > 1) await Assert.That(target).Contains(" LogLine ×" + row.ItemCount.ToString(CultureInfo.InvariantCulture));
                else if (row.ItemPartial) await Assert.That(target).Contains(" LogLine (part)");
                else await Assert.That(target).DoesNotContain("×");
            }

            // The ladder names the same targets, with the chip kind beside the title.
            LadderRow[] rows = Enumerable.Range(0, ladder.Count).Select(i => ladder[i]).Where(r => r.Type == MessageType.DuplexPipeWrite).ToArray();
            LadderRow ladderChannel = rows.First(r => r.Target == LadderTarget.Channel);
            await Assert.That(LadderText.PipeKind(ladderChannel)).IsEqualTo(PipeContent.Channel);
            await Assert.That(LadderText.Title(ladderChannel, 0)).IsEqualTo("DuplexPipeWrite");
            await Assert.That(LadderText.Detail(ladderChannel, ladder.GetLabel(ladderChannel.Name))).StartsWith("chan ");
            await Assert.That(ladderChannel.Items).IsGreaterThanOrEqualTo(1);
            await Assert.That(LadderText.PipeKind(rows.First(r => r.Target == LadderTarget.Pipe))).IsEqualTo(PipeContent.Raw);
            await Assert.That(LadderText.PipeKind(rows.First(r => r.Target == LadderTarget.Collection))).IsEqualTo(PipeContent.Collection);
            await Assert.That(LadderText.Describe(ladderChannel, 0, null, 0, 1)).Contains(" channel ");
        }
    }

    [Test]
    public async Task Every_channel_item_is_found_in_each_frame_that_carried_it()
    {
        (RecordingFile file, CaptureSession session) = Open();
        using (file)
        {
            MessageAssembler assembler = session.Assembler;
            int checkedItems = 0;
            foreach (ChannelItemRow item in assembler.ChannelItems.ToArray())
            {
                for (long seq = item.FirstSeq; seq <= item.LastSeq; seq++)
                {
                    if (assembler.GetLink(item.Direction, seq) is not { Entity: EntityKind.Pipe } link || link.EntityIndex != item.Pipe) continue;
                    FrameItems found = PipeText.Find(assembler, item.Pipe, item.Direction, seq);
                    int[] order = assembler.GetPipeItems(item.Pipe, item.Direction).ToArray();
                    int at = Array.BinarySearch(order, found.First);
                    await Assert.That(at).IsGreaterThanOrEqualTo(0);
                    await Assert.That(order.AsSpan(at, found.Count).Contains(item.Index)).IsTrue();
                    if (item.FragmentCount > 1 && found.Count == 1) await Assert.That(found.Partial).IsTrue();
                }

                await Assert.That(PipeText.Ordinal(assembler, item)).IsGreaterThan(0);
                checkedItems++;
            }

            await Assert.That(checkedItems).IsGreaterThan(0);
            int raw = PipeOf(session, PipeContent.Raw).Pipe;
            FrameKey rawWrite = PipeOf(session, PipeContent.Raw).Write;
            await Assert.That(PipeText.Find(assembler, raw, rawWrite.Direction, rawWrite.Seq)).IsEqualTo(FrameItems.None);
        }
    }

    [Test]
    public async Task A_channel_frame_decodes_its_items_first_then_the_frame_then_the_channel_on_its_pipe()
    {
        (RecordingFile file, CaptureSession session) = Open();
        using (file)
        {
            (int channel, FrameKey write) = PipeOf(session, PipeContent.Channel);
            string ids = PipeText.Ids(session.Assembler.Pipes[channel].PipeId);
            InspectorContent content = InspectorProjection.Build(session, write)!;
            PropertyRow[] sections = [.. content.Tree.Rows.Where(r => r.IsSection)];
            int items = PipeText.Find(session.Assembler, channel, write.Direction, write.Seq).Count;

            for (int i = 0; i < items; i++)
            {
                await Assert.That(sections[i].Name).StartsWith("Item #");
                await Assert.That(sections[i].Name).EndsWith(" · LogLine");
                await Assert.That(sections[i].IsExpanded).IsTrue();
            }

            await Assert.That(string.Join("|", sections.Skip(items).Select(s => s.Name)))
                .IsEqualTo("Frame header|Transport|Channel<LogLine> " + ids);
            PropertyRow pipe = sections[^1];
            await Assert.That(pipe.Badge).IsEqualTo("CHANNEL");
            await Assert.That(content.Tree.Rows.First(r => r.Name == "Carried by").Value).IsEqualTo("duplex pipe " + ids);
            await Assert.That(content.Tree.Rows.First(r => r.Name == "Opened by").Value).Contains("TailLogs");
            // The item's own fields show under it (LogLine's members), before the frame header.
            int header = Array.IndexOf(content.Tree.Rows.ToArray(), sections[items]);
            await Assert.That(content.Tree.Rows.Take(header).Count(r => !r.IsSection && !r.Bytes.IsNone)).IsGreaterThan(1);

            (int raw, FrameKey rawWrite) = PipeOf(session, PipeContent.Raw);
            InspectorContent rawContent = InspectorProjection.Build(session, rawWrite)!;
            PropertyRow[] rawSections = [.. rawContent.Tree.Rows.Where(r => r.IsSection)];
            await Assert.That(rawSections[0].Name).IsEqualTo("Frame header");
            await Assert.That(rawSections[^1].Name).IsEqualTo("Pipe " + PipeText.Ids(session.Assembler.Pipes[raw].PipeId));
            await Assert.That(rawSections[^1].Value).StartsWith("raw · ");
            await Assert.That(rawSections[^1].HasBadge).IsFalse();
            await Assert.That(rawContent.Tree.Rows.Any(r => r.Name == "Carried by")).IsFalse();

            (_, FrameKey collectionWrite) = PipeOf(session, PipeContent.Collection);
            PropertyRow[] collectionSections = [.. InspectorProjection.Build(session, collectionWrite)!.Tree.Rows.Where(r => r.IsSection)];
            await Assert.That(collectionSections[0].Name).StartsWith("Message #");
            await Assert.That(collectionSections[^1].Name).StartsWith("Collection Users ");
            await Assert.That(collectionSections[^1].Badge).IsEqualTo("COLLECTION");

            // The hex annotation names the channel on its frame: "▸ DuplexPipeWrite #n ≋ LogLine @…".
            await Assert.That(MessageTrace.Resolve(session, Selection.OfEntity(EntityKind.Pipe, channel)).Title).IsEqualTo("Channel<LogLine> " + ids);
        }
    }

    [Test]
    public async Task Hovering_an_item_field_lights_its_bytes_and_header_rows_keep_frame_coordinates()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        var (document, _) = await ChatFixture.OpenAsync(documents);
        CaptureSession session = document.Session!;
        FrameKey write;
        lock (session.Gate) write = PipeOf(session, PipeContent.Channel).Write;
        using InspectorViewModel inspector = new(documents);
        documents.Sync.Select(Selection.OfFrame(write));
        HexLinker linker = new();

        int itemRows = 0;
        List<string> wrong = [];
        foreach (PropertyRow row in inspector.Rows!.ToArray())
        {
            if (row.Bytes.IsNone) continue;
            if (row.Bytes.Message.Kind == MessageRefKind.ChannelItem) itemRows++;
            inspector.HoveredRow = row;
            List<HexRange> ranges = [];
            lock (session.Gate) linker.AddHoverRanges(session, documents.Sync.Hover, ranges);
            if (ranges.Count == 0 || ranges[0].Direction != row.Bytes.Direction || ranges[0].Start != row.Bytes.Stream.Offset)
                wrong.Add(row.Name);
        }

        await Assert.That(wrong).IsEmpty();
        await Assert.That(itemRows).IsGreaterThan(0);
        // The frame header's Type row stays in frame coordinates: the byte before the body.
        await Assert.That(inspector.Rows!.First(r => r.Name == "Type").Bytes.Offset).IsLessThan(0);
    }

    [Test]
    public async Task Hex_annotations_name_the_channel_on_its_frame()
    {
        (RecordingFile file, CaptureSession session) = Open();
        using (file)
        {
            (_, FrameKey write) = PipeOf(session, PipeContent.Channel);
            HexLayout layout = new();
            layout.Update(session, write.Direction == TapDirection.Inbound ? HexDirections.Inbound : HexDirections.Outbound);
            session.Assembler.TryGetFrame(write.Direction, write.Seq, out FrameRecord frame);
            HexLine[] lines = new HexLine[layout.LineCount];
            lines = lines[..layout.Read(0, lines)];
            string? text = null;
            foreach (HexLine line in lines)
            {
                if (text is not null) break;
                if (line.Kind != HexLineKind.Frames) continue;
                string frames = HexText.Frames(layout, line);
                if (frames.Contains("#" + frame.Seq.ToString("N0", CultureInfo.InvariantCulture) + " ", StringComparison.Ordinal)) text = frames;
            }

            await Assert.That(text).IsNotNull();
            await Assert.That(text!).Contains("▸ DuplexPipeWrite #" + frame.Seq.ToString("N0", CultureInfo.InvariantCulture) + " ≋ LogLine @");
        }
    }

    [Test]
    [NotInParallel("avalonia")]
    public async Task Pipe_chips_and_glyphs_draw_from_caches_without_allocating_per_row()
    {
        (long perChip, long perText, double channel, double collection, double raw) = await HeadlessSession.Run(() =>
        {
            ThemePalette palette = ThemeBrushes.For(Avalonia.Styling.ThemeVariant.Light);
            using RenderTargetBitmap bitmap = new(new PixelSize(200, 40));
            using DrawingContext context = bitmap.CreateDrawingContext();
            for (int i = 0; i < 2; i++)
            {
                PipeChip.Draw(context, palette, PipeContent.Channel, default);
                PipeChip.Draw(context, palette, PipeContent.Collection, new Point(30, 0));
                PipeGlyph.Draw(context, palette, PipeContent.Channel, ThemeBrush.TextPrimary, new Point(60, 0));
                PipeGlyph.Draw(context, palette, PipeContent.Raw, ThemeBrush.TextPrimary, new Point(80, 0));
                PipeGlyph.Draw(context, palette, PipeContent.Collection, ThemeBrush.TextPrimary, new Point(100, 0));
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                PipeChip.Draw(context, palette, PipeContent.Channel, default);
                PipeGlyph.Draw(context, palette, PipeContent.Channel, ThemeBrush.TextPrimary, new Point(60, 0));
            }

            long chips = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;

            // Skia's own bytes per glyph run, measured on the same context: the floor any drawn text has.
            using TextLayout label = new("CH", new Typeface(palette.Ui), 8.5, palette[ThemeBrush.TextSecondary]);
            label.Draw(context, default);
            long calibrate = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) label.Draw(context, default);
            long text = (GC.GetAllocatedBytesForCurrentThread() - calibrate) / 100;
            return (chips, text, PipeChip.MeasureWidth(palette, PipeContent.Channel), PipeChip.MeasureWidth(palette, PipeContent.Collection),
                PipeChip.MeasureWidth(palette, PipeContent.Raw));
        });

        // A chip and a glyph cost no more than the chip's one glyph run plus the transform push of the glyph.
        await Assert.That(perChip).IsLessThanOrEqualTo(perText + 256);
        await Assert.That(channel).IsGreaterThanOrEqualTo(FlagChip.MinChipWidth);
        await Assert.That(collection).IsGreaterThanOrEqualTo(FlagChip.MinChipWidth);
        await Assert.That(raw).IsEqualTo(0);
    }
}
