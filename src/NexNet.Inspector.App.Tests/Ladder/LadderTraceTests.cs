using NexNet.Inspector.App.Controls.Ladder;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using static NexNet.Inspector.App.Tests.Ladder.LadderDataBuilder;

namespace NexNet.Inspector.App.Tests.Ladder;

/// <summary>
/// The trace bracket (ladder-row.md): it joins every frame of the traced message, from the first to the last, and the
/// frames between that are not part of it are interleaved (dotted bracket, 60 %). A selected frame traces its
/// invocation as the timeline does; a double-click (a message selection) traces just that message.
/// </summary>
public sealed class LadderTraceTests
{
    // Demo rows of invocation #812: MessageStart, the five chunks and the result.
    private static readonly int[] Invocation812Rows = [0, 2, 6, 9, 11, 13, 14];

    private static readonly int[] Argument812Rows = [0, 2, 6, 9, 11, 13];

    [Test]
    public async Task A_selected_chunk_brackets_its_invocation_from_its_first_frame_to_its_result()
    {
        LadderModel model = Demo();
        LadderTrace trace = model.GetTrace(Selection.OfFrame(model[6].Frame));

        await Assert.That(trace.IsActive).IsTrue();
        await Assert.That(trace.Kind).IsEqualTo(LadderTraceKind.Entity);
        await Assert.That(trace.Entity).IsEqualTo(EntityKind.Invocation);
        await Assert.That(trace.First).IsEqualTo(0);
        await Assert.That(trace.Last).IsEqualTo(14);
        await Assert.That(Brackets(model, trace, LadderBracket.Member)).IsEquivalentTo(Invocation812Rows);
        await Assert.That(Brackets(model, trace, LadderBracket.Interleaved)).IsEquivalentTo(new[] { 1, 3, 4, 5, 7, 8, 10, 12 });

        // Rows after the result are outside the bracket.
        for (int i = 15; i < model.Count; i++)
            await Assert.That(trace.BracketOf(i, model[i])).IsEqualTo(LadderBracket.None);
    }

    [Test]
    public async Task A_traced_message_brackets_only_its_own_frames()
    {
        LadderModel model = Demo();
        MessageRef message = model.MessageOf(Argument812);
        LadderTrace trace = model.GetTrace(Selection.OfMessage(message, model[6].Frame));

        await Assert.That(message.Kind).IsEqualTo(MessageRefKind.Assembled);
        await Assert.That(trace.Kind).IsEqualTo(LadderTraceKind.Message);
        await Assert.That(trace.First).IsEqualTo(0);
        await Assert.That(trace.Last).IsEqualTo(13);
        await Assert.That(Brackets(model, trace, LadderBracket.Member)).IsEquivalentTo(Argument812Rows);
        await Assert.That(trace.BracketOf(14, model[14])).IsEqualTo(LadderBracket.None);
    }

    [Test]
    public async Task An_invocation_selected_on_the_timeline_brackets_the_same_rows()
    {
        LadderModel model = Demo();
        LadderTrace fromTimeline = model.GetTrace(Selection.OfEntity(EntityKind.Invocation, Invocation812));
        LadderTrace fromLadder = model.GetTrace(Selection.OfFrame(model[2].Frame));

        await Assert.That(fromTimeline).IsEqualTo(fromLadder);
    }

    [Test]
    public async Task A_lone_frame_traces_nothing_but_a_traced_lone_message_brackets_its_one_row()
    {
        LadderModel model = Demo();
        FrameKey ping = model[4].Frame;

        await Assert.That(model.GetTrace(Selection.OfFrame(ping)).IsActive).IsFalse();
        await Assert.That(model.GetTrace(Selection.None).IsActive).IsFalse();
        await Assert.That(model.GetTrace(Selection.OfBytes(TapDirection.Inbound, new StreamRange(0, 4))).IsActive).IsFalse();

        LadderTrace one = model.GetTrace(Selection.OfMessage(MessageRef.ForFrame(ping.Direction, ping.Seq), ping));
        await Assert.That(one.Kind).IsEqualTo(LadderTraceKind.Frame);
        await Assert.That(one.First).IsEqualTo(4);
        await Assert.That(one.Last).IsEqualTo(4);
        await Assert.That(one.BracketOf(4, model[4])).IsEqualTo(LadderBracket.Member);
        await Assert.That(one.BracketOf(3, model[3])).IsEqualTo(LadderBracket.None);
    }

    [Test]
    public async Task Selection_maps_to_its_row_and_frames_are_found_by_key_and_time()
    {
        LadderModel model = Demo();
        await Assert.That(model.SelectedIndex(Selection.OfFrame(model[9].Frame))).IsEqualTo(9);
        await Assert.That(model.SelectedIndex(Selection.OfMessage(model.MessageOf(Argument812), model[11].Frame))).IsEqualTo(11);
        await Assert.That(model.SelectedIndex(Selection.OfEntity(EntityKind.Invocation, 0))).IsEqualTo(-1);
        await Assert.That(model.IndexOf(new FrameKey(TapDirection.Outbound, 99))).IsEqualTo(-1);
        await Assert.That(model.IndexAtOrAfter(Ticks(1_262.1))).IsEqualTo(6);
        await Assert.That(model.IndexAtOrAfter(Ticks(5_000))).IsEqualTo(model.Count);
        await Assert.That(model.ChunkTotal(Argument812)).IsEqualTo(5);
    }

    private static int[] Brackets(LadderModel model, LadderTrace trace, LadderBracket kind) =>
        Enumerable.Range(0, model.Count).Where(i => trace.BracketOf(i, model[i]) == kind).ToArray();
}
