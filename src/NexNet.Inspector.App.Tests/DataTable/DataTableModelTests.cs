using NexNet.Inspector.App.Controls.DataTable;
using NexNet.Inspector.App.Controls.DataTable.Events;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Messages;

namespace NexNet.Inspector.App.Tests.DataTable;

/// <summary>The table's pure parts: the column model, the follow-live rule and the Events text.</summary>
public sealed class DataTableModelTests
{
    [Test]
    public async Task Events_columns_lay_out_like_the_package_grid()
    {
        DataTableColumn[] columns = [.. EventColumns.All];
        DataTableColumnSlot[] slots = new DataTableColumnSlot[columns.Length];

        // data-table.html: 640 px, padding 0 16px 0 8px, 50 74 14 1fr 104 52 34.
        DataTableColumnLayout.Arrange(columns, 640, 8, 16, slots);

        await Assert.That(slots[EventColumns.Seq]).IsEqualTo(new DataTableColumnSlot(8, 50));
        await Assert.That(slots[EventColumns.Time]).IsEqualTo(new DataTableColumnSlot(58, 74));
        await Assert.That(slots[EventColumns.Direction]).IsEqualTo(new DataTableColumnSlot(132, 14));
        await Assert.That(slots[EventColumns.Type]).IsEqualTo(new DataTableColumnSlot(146, 640 - 8 - 16 - 328));
        await Assert.That(slots[EventColumns.Flags].Right).IsEqualTo(640 - 16);
        await Assert.That(columns[EventColumns.Bytes].Alignment).IsEqualTo(DataTableAlignment.Right);
    }

    [Test]
    public async Task Star_columns_share_by_weight_and_shrink_to_nothing_before_fixed_ones_move()
    {
        DataTableColumn[] columns = [DataTableColumn.Star("A"), DataTableColumn.Fixed("B", 100), DataTableColumn.Star("C", 3)];
        DataTableColumnSlot[] slots = new DataTableColumnSlot[3];

        DataTableColumnLayout.Arrange(columns, 500, 0, 0, slots);
        await Assert.That(slots[0].Width).IsEqualTo(100);
        await Assert.That(slots[2]).IsEqualTo(new DataTableColumnSlot(200, 300));

        DataTableColumnLayout.Arrange(columns, 60, 0, 0, slots);
        await Assert.That(slots[0].Width).IsEqualTo(0);
        await Assert.That(slots[1]).IsEqualTo(new DataTableColumnSlot(0, 100));
        await Assert.That(slots[2].Width).IsEqualTo(0);
    }

    [Test]
    public async Task Scrolling_up_pauses_following_and_scrolling_back_to_the_end_resumes_it()
    {
        // The user scrolls up while following.
        await Assert.That(FollowTailRule.Decide(-21, 0, 0, 500, 300, 1000, following: true)).IsEqualTo(FollowTailDecision.Pause);

        // Rows arriving or the pane resizing are not the user's.
        await Assert.That(FollowTailRule.Decide(-21, -500, 0, 0, 300, 500, following: true)).IsEqualTo(FollowTailDecision.None);
        await Assert.That(FollowTailRule.Decide(-21, 0, 40, 660, 340, 1000, following: true)).IsEqualTo(FollowTailDecision.None);

        // Down, but not yet at the end; then at the end.
        await Assert.That(FollowTailRule.Decide(21, 0, 0, 600, 300, 1000, following: false)).IsEqualTo(FollowTailDecision.None);
        await Assert.That(FollowTailRule.Decide(21, 0, 0, 700, 300, 1000, following: false)).IsEqualTo(FollowTailDecision.Resume);
        await Assert.That(FollowTailRule.Decide(21, 0, 0, 700, 300, 1000, following: true)).IsEqualTo(FollowTailDecision.None);
    }

    [Test]
    public async Task Events_text_formats_numbers_times_types_and_targets()
    {
        EventRow chunk = EventRowArray.Row(18_229, 1_259.88, TapDirection.Inbound, MessageType.MessageChunk, EventTargetKind.Invocation,
            812, 4_096, FrameFlags.TransportSplit, chunk: 1, chunks: 5);
        await Assert.That(EventText.Number(chunk)).IsEqualTo("18,229");
        await Assert.That(EventText.Time(chunk)).IsEqualTo("1,259.880");
        await Assert.That(EventText.Bytes(chunk)).IsEqualTo("4,096");
        await Assert.That(EventText.Type(chunk)).IsEqualTo("MessageChunk 1/5");
        await Assert.That(EventText.Type(chunk with { ChunkCount = 0 })).IsEqualTo("MessageChunk 1");
        await Assert.That(EventText.Target(chunk)).IsEqualTo("inv 812");
        await Assert.That(EventText.Target(chunk with { MethodName = "SendAttachment" })).IsEqualTo("inv 812 SendAttachment");
        await Assert.That(EventText.Target(chunk with { Target = EventTargetKind.Pipe, TargetId = 3 | (1 << 8) })).IsEqualTo("pipe 3·1");
        await Assert.That(EventText.Target(chunk with { Target = EventTargetKind.Channel, TargetId = 4 | (2 << 8) })).IsEqualTo("chan 4·2");
        await Assert.That(EventText.Target(chunk with { Target = EventTargetKind.None })).IsEqualTo("");
        await Assert.That(EventText.Type(chunk with { Kind = FrameKind.Preamble })).IsEqualTo("Preamble");
        await Assert.That(EventText.TypeName((MessageType)36)).IsEqualTo("Disconnect 36");
        await Assert.That(EventText.TypeName((MessageType)0xEE)).IsEqualTo("0xEE");
        await Assert.That(EventText.Accessible(chunk)).IsEqualTo("MessageChunk 1/5, inbound, 1,259.880 ms");
    }
}
