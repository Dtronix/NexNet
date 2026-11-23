using NexNet.Inspector.App.Controls.DataTable.Events;
using NexNet.Inspector.App.Controls.Ladder;
using NexNet.Inspector.App.Controls.PropertyTree;
using NexNet.Inspector.App.Controls.Timeline;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Messages;

namespace NexNet.Inspector.App.Tests.Inspector;

/// <summary>
/// Following one message (SYSTEM.md flow 2) over the Chat fixture: the trace a selection resolves to on the sync bus
/// (an invocation's request, interleaved frames and result; the handshake; a channel item), and every view drawing that
/// one trace — the timeline's band and lit marks, the ladder's bracket, the tinted Events rows, the inspector's Message
/// trace and Handshake tabs with the handshake assembly bar — plus hex → tree hover linking.
/// </summary>
public class MessageTraceTests
{
    private static MessageTrace Resolve(CaptureSession session, Selection selection)
    {
        lock (session.Gate) return MessageTrace.Resolve(session, selection);
    }

    private static FrameKey ClientGreeting(CaptureSession session)
    {
        lock (session.Gate)
        {
            GreetingRow greeting = session.Assembler.Greetings.ToArray().First(g => g.Kind == GreetingKind.Client);
            return new FrameKey(greeting.Direction, greeting.Seq);
        }
    }

    [Test]
    public async Task A_chunk_traces_its_invocation_from_MessageStart_to_the_result_with_interleaved_runs()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        var (document, picks) = await ChatFixture.OpenAsync(documents);
        MessageTrace trace = Resolve(document.Session!, Selection.OfFrame(picks.Chunk));
        TraceStep[] steps = trace.Steps.ToArray();

        await Assert.That(trace.Kind).IsEqualTo(TraceKind.Invocation);
        await Assert.That(trace.Title).Contains("SendAttachment");
        await Assert.That(trace.Message).IsEqualTo(picks.Message);
        await Assert.That(steps[0].Kind).IsEqualTo(TraceStepKind.Start);
        await Assert.That(steps[0].Label).IsEqualTo("MessageStart");
        await Assert.That(steps.Where(s => s.Kind == TraceStepKind.Chunk).Select(s => s.Label).ToArray())
            .IsEquivalentTo(new[] { "Chunk 1/5", "Chunk 2/5", "Chunk 3/5", "Chunk 4/5", "Chunk 5/5" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(steps[^1].Label).IsEqualTo("InvocationResult");
        await Assert.That(steps[^1].Frame.Direction).IsNotEqualTo(picks.Chunk.Direction);
        // Runs of other frames between the chunks: a pipe write and a ping among them, never two runs in a row.
        await Assert.That(steps.Any(s => s.IsInterleaved && s.Label.Contains("pipe ", StringComparison.Ordinal))).IsTrue();
        await Assert.That(steps.Any(s => s.IsInterleaved && s.Label.Contains("Ping", StringComparison.Ordinal))).IsTrue();
        await Assert.That(steps.Zip(steps.Skip(1)).Any(p => p.First.IsInterleaved && p.Second.IsInterleaved)).IsFalse();
        await Assert.That(trace.InterleavedCount).IsGreaterThan(0);

        // Members: the request's six frames and the result; an interleaved frame is no member.
        await Assert.That(trace.MemberCount).IsEqualTo(7);
        await Assert.That(trace.Contains(picks.Chunk)).IsTrue();
        await Assert.That(trace.Contains(steps.First(s => s.IsInterleaved).Frame)).IsFalse();
        await Assert.That(steps.First(s => s.Kind == TraceStepKind.Chunk).MessageOffset).IsEqualTo(0);
        await Assert.That(steps.First(s => s.Frame == picks.Chunk).Length).IsGreaterThan(0);
        await Assert.That(trace.End).IsGreaterThan(trace.Start);

        // The whole message, the invocation entity and the result frame trace the same.
        await Assert.That(trace.SameAs(Resolve(document.Session!, Selection.OfMessage(picks.Message, picks.Chunk)))).IsTrue();
        await Assert.That(trace.SameAs(Resolve(document.Session!, Selection.OfEntity(EntityKind.Invocation, trace.EntityIndex)))).IsTrue();
        await Assert.That(trace.SameAs(Resolve(document.Session!, Selection.OfFrame(steps[^1].Frame)))).IsTrue();
    }

    [Test]
    public async Task The_handshake_traces_both_preambles_and_both_greetings_and_a_ping_traces_nothing()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        var (document, _) = await ChatFixture.OpenAsync(documents);
        CaptureSession session = document.Session!;
        MessageTrace trace = Resolve(session, Selection.OfFrame(ClientGreeting(session)));

        await Assert.That(trace.Kind).IsEqualTo(TraceKind.Handshake);
        await Assert.That(trace.Title).IsEqualTo("Handshake");
        await Assert.That(trace.Steps.ToArray().Select(s => s.Label).ToArray())
            .IsEquivalentTo(new[] { "Preamble", "ClientGreeting", "Preamble", "ServerGreeting" }, TUnit.Assertions.Enums.CollectionOrdering.Any);
        await Assert.That(trace.Steps[^1].Label).IsEqualTo("ServerGreeting");
        await Assert.That(trace.MemberCount).IsEqualTo(4);
        await Assert.That(trace.SameAs(Resolve(session, Selection.OfEntity(EntityKind.Greeting, 0)))).IsTrue();

        FrameKey ping;
        lock (session.Gate) ping = session.FrameOrder.ToArray().First(k => session.TryGetFrame(k, out FrameRecord f) && f.Type == MessageType.Ping);
        await Assert.That(Resolve(session, Selection.OfFrame(ping)).IsActive).IsFalse();
        await Assert.That(Resolve(session, Selection.None).IsActive).IsFalse();
    }

    [Test]
    public async Task A_channel_item_traces_its_fragments()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        var (document, _) = await ChatFixture.OpenAsync(documents);
        CaptureSession session = document.Session!;
        MessageRef item;
        int fragments;
        lock (session.Gate)
        {
            ChannelItemRow row = session.Assembler.ChannelItems.ToArray().First(i => i.State == AssemblyState.Complete);
            item = row.Ref;
            fragments = row.FragmentCount;
        }

        MessageTrace trace = Resolve(session, Selection.OfMessage(item, new FrameKey(item.Direction, item.Seq)));
        await Assert.That(trace.Kind).IsEqualTo(TraceKind.Message);
        await Assert.That(trace.Title).EndsWith(" item");
        await Assert.That(trace.MemberCount).IsEqualTo(fragments);
        await Assert.That(trace.Contains(new FrameKey(item.Direction, item.Seq))).IsTrue();
    }

    [Test]
    public async Task The_bus_resolves_the_trace_with_the_selection_and_keeps_an_unchanged_one()
    {
        QueuedUi ui = new();
        await using DocumentsViewModel documents = new(new TestUi(), null);
        var (_, picks) = await ChatFixture.OpenAsync(documents);
        SyncBus sync = documents.Sync;
        List<SyncChanges> changes = [];
        sync.Changed += changes.Add;

        sync.Select(Selection.OfFrame(picks.Chunk));
        MessageTrace first = sync.Trace;
        await Assert.That(sync.ResolvesTrace).IsTrue();
        await Assert.That(first.Kind).IsEqualTo(TraceKind.Invocation);
        await Assert.That(changes.Any(c => (c & (SyncChanges.Selection | SyncChanges.Trace)) == (SyncChanges.Selection | SyncChanges.Trace))).IsTrue();

        // Another frame of the same invocation: a new selection, the same trace object.
        changes.Clear();
        sync.Select(Selection.OfMessage(picks.Message, picks.Chunk));
        await Assert.That(sync.Trace).IsSameReferenceAs(first);
        await Assert.That(changes.All(c => (c & SyncChanges.Trace) == 0)).IsTrue();

        sync.Select(Selection.None);
        await Assert.That(sync.Trace.IsActive).IsFalse();
        _ = ui;
    }

    [Test]
    public async Task The_timeline_bands_the_trace_and_lights_only_its_frames()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        var (_, picks) = await ChatFixture.OpenAsync(documents);
        using TimelineViewModel timeline = new(documents);
        documents.Sync.Select(Selection.OfFrame(picks.Chunk));
        MessageTrace trace = documents.Sync.Trace;
        TimelineTrace band = timeline.Model.GetTrace(trace);
        FrameKey interleaved = trace.Steps.ToArray().First(s => s.IsInterleaved).Frame;

        TimelineLane frames = timeline.Model.FindLane(new LaneKey(LaneKind.InboundFrames, 0))!;
        TimelineMark chunk = frames.Marks[frames.IndexOfSeq(picks.Chunk.Seq)];
        TimelineMark other = frames.Marks[frames.IndexOfSeq(interleaved.Seq)];

        await Assert.That(band.IsActive).IsTrue();
        await Assert.That(band.Entity).IsEqualTo(EntityKind.Invocation);
        await Assert.That(band.Start).IsLessThanOrEqualTo(trace.Start);
        await Assert.That(band.End).IsGreaterThanOrEqualTo(trace.End);
        await Assert.That(band.Lights(chunk)).IsTrue();
        await Assert.That(band.Lights(other)).IsFalse();

        // A channel item's band spans exactly its frames.
        MessageRef item;
        lock (documents.Active!.Session!.Gate) item = documents.Active.Session.Assembler.ChannelItems.ToArray().First(i => i.State == AssemblyState.Complete).Ref;
        documents.Sync.Select(Selection.OfMessage(item, new FrameKey(item.Direction, item.Seq)));
        TimelineTrace itemBand = timeline.Model.GetTrace(documents.Sync.Trace);
        await Assert.That((itemBand.Start, itemBand.End)).IsEqualTo((documents.Sync.Trace.Start, documents.Sync.Trace.End));
        await Assert.That(itemBand.Entity).IsEqualTo(EntityKind.None);
    }

    [Test]
    public async Task The_ladder_brackets_the_invocation_and_marks_interleaved_rows()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        var (_, picks) = await ChatFixture.OpenAsync(documents);
        using LadderViewModel ladder = new(documents);
        documents.Sync.Select(Selection.OfFrame(picks.Chunk));
        MessageTrace trace = documents.Sync.Trace;
        LadderTrace bracket = ladder.Model.GetTrace(trace);
        TraceStep[] steps = trace.Steps.ToArray();

        await Assert.That(bracket.IsActive).IsTrue();
        await Assert.That(bracket.First).IsEqualTo(ladder.Model.IndexOf(steps[0].Frame));
        await Assert.That(bracket.Last).IsEqualTo(ladder.Model.IndexOf(steps[^1].Frame));
        int chunk = ladder.Model.IndexOf(picks.Chunk);
        int interleaved = ladder.Model.IndexOf(steps.First(s => s.IsInterleaved).Frame);
        await Assert.That(bracket.BracketOf(chunk, ladder.Model[chunk])).IsEqualTo(LadderBracket.Member);
        await Assert.That(bracket.BracketOf(interleaved, ladder.Model[interleaved])).IsEqualTo(LadderBracket.Interleaved);
    }

    [Test]
    public async Task Events_tint_every_frame_of_the_trace_and_the_handshake_rows()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        var (document, picks) = await ChatFixture.OpenAsync(documents);
        using EventsViewModel events = new(documents);

        documents.Sync.Select(Selection.OfFrame(picks.Chunk));
        MessageTrace trace = documents.Sync.Trace;
        await Assert.That(events.Rows.Members).IsSameReferenceAs(trace);
        TraceStep[] steps = trace.Steps.ToArray();
        await Assert.That(Traced(events, steps[0].Frame)).IsTrue();
        await Assert.That(Traced(events, steps[^1].Frame)).IsTrue();
        await Assert.That(Traced(events, steps.First(s => s.IsInterleaved).Frame)).IsFalse();

        // The ClientGreeting traces the handshake: the session's first four frames.
        documents.Sync.Select(Selection.OfFrame(ClientGreeting(document.Session!)));
        bool[] first = [.. Enumerable.Range(0, 6).Select(i => events.Rows.TryGetRow(i, out EventRow row) && row.IsTraced)];
        await Assert.That(first).IsEquivalentTo(new[] { true, true, true, true, false, false }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    private static bool Traced(EventsViewModel events, FrameKey frame) =>
        events.Rows.TryGetRow(events.Rows.IndexOf(frame), out EventRow row) && row.IsTraced;

    [Test]
    public async Task The_message_trace_tab_lists_the_steps_follows_the_frame_and_a_click_moves_there()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        var (_, picks) = await ChatFixture.OpenAsync(documents);
        using InspectorViewModel inspector = new(documents);
        await Assert.That(inspector.ShowsTraceHint).IsFalse();
        inspector.TabIndex = 1;
        await Assert.That(inspector.ShowsTraceHint).IsTrue();

        documents.Sync.Select(Selection.OfMessage(picks.Message, picks.Chunk));
        MessageTrace trace = documents.Sync.Trace;
        await Assert.That(inspector.ShowsTraceList).IsTrue();
        await Assert.That(inspector.TraceRows.Count).IsEqualTo(trace.StepCount);
        await Assert.That(inspector.TraceTitle).Contains("SendAttachment");
        await Assert.That(inspector.TraceSummary).StartsWith("7 frames · ");
        await Assert.That(inspector.TraceSummary).Contains("interleaved");
        await Assert.That(inspector.SelectedTraceRow!.Step.Frame).IsEqualTo(picks.Chunk);
        await Assert.That(inspector.SelectedTraceRow.Label).IsEqualTo("Chunk 2/5");
        TraceStepRow run = inspector.TraceRows.First(r => r.IsInterleaved);
        await Assert.That(run.RowOpacity).IsEqualTo(0.6);
        await Assert.That(run.LabelMargin.Left).IsEqualTo(12);
        await Assert.That(run.Glyph).IsEqualTo("");
        await Assert.That(inspector.TraceRows[0].Detail).Contains("stream 0x");

        // A click on the result: the inspector and the playhead move there, the trace stays.
        TraceStepRow result = inspector.TraceRows[^1];
        inspector.SelectedTraceRow = result;
        await Assert.That(documents.Sync.Selection.Frame).IsEqualTo(result.Step.Frame);
        await Assert.That(documents.Sync.Playhead).IsEqualTo(result.Step.Ticks);
        await Assert.That(documents.Sync.Trace).IsSameReferenceAs(trace);
        await Assert.That(inspector.Content!.Frame).IsEqualTo(result.Step.Frame);
        await Assert.That(inspector.SelectedTraceRow).IsSameReferenceAs(result);
    }

    [Test]
    public async Task The_handshake_tab_decodes_every_handshake_frame_under_a_four_segment_bar()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        var (document, _) = await ChatFixture.OpenAsync(documents);
        using InspectorViewModel inspector = new(documents);
        documents.Sync.Select(Selection.OfFrame(ClientGreeting(document.Session!)));
        inspector.TabIndex = 1;

        await Assert.That(inspector.ShowsHandshake).IsTrue();
        await Assert.That(inspector.ShowsTraceList).IsFalse();
        PropertyRow[] rows = [.. inspector.HandshakeRows!];
        // In wire order: which side's preamble goes first is the server's business.
        string[] sections = [.. rows.Where(r => r.IsSection).Select(r => r.Name)];
        await Assert.That(sections).IsEquivalentTo(new[] { "Preamble ▼", "ClientGreeting ▼", "Preamble ▲", "ServerGreeting ▲" }, TUnit.Assertions.Enums.CollectionOrdering.Any);
        await Assert.That(sections[^1]).IsEqualTo("ServerGreeting ▲");
        await Assert.That(rows.First(r => r.Name == "Magic").Value).IsEqualTo("4E 6E 50 14 ✓");
        PropertyRow token = rows.First(r => r.Name == "authenticationToken");
        await Assert.That(token.Value).StartsWith("bin 96 B · redacted · sha256 ");
        await Assert.That(token.IsNexNet).IsTrue();
        await Assert.That(rows.First(r => r.Name == "clientId").Value).EndsWith(" · session id");
        await Assert.That(rows.Count(r => r.Name == "invocationCredits")).IsEqualTo(2);

        AssemblyInfo assembly = inspector.Assembly!;
        await Assert.That(assembly.Title).IsEqualTo("Handshake");
        await Assert.That(assembly.Segments.Select(s => s.Label).ToArray())
            .IsEquivalentTo(new[] { "Pre ▼", "Greet ▼", "Pre ▲", "Greet ▲" }, TUnit.Assertions.Enums.CollectionOrdering.Any);
        await Assert.That(assembly.Segments[^1].Label).IsEqualTo("Greet ▲");
        await Assert.That(assembly.Segments.Single(s => s.IsCurrent).Label).IsEqualTo("Greet ▼");
        await Assert.That(assembly.Summary).StartsWith("4 frames · ");
        await Assert.That(assembly.Summary).EndsWith(" · auth ok");
        await Assert.That(assembly.Note).StartsWith("Preamble and ClientGreeting arrived in one read (#");
        await Assert.That(assembly.Note).EndsWith("token redacted");

        // The Decoded tab: the preamble that shared the read, the greeting, the read and the server's reply.
        PropertyRow[] decoded = [.. inspector.Rows!];
        await Assert.That(string.Join("|", decoded.Where(r => r.IsSection).Select(r => r.Name))).IsEqualTo("Preamble|ClientGreeting|Transport");
        await Assert.That(decoded.First(r => r.IsSection && r.Name == "Preamble").Value).EndsWith(" · same read");
        await Assert.That(decoded.First(r => r.Name == "Server reply").Value).StartsWith("ServerGreeting +");
        await Assert.That(decoded.First(r => r.IsSection && r.Name == "Transport").Value).EndsWith(" · 2 frames");
    }

    [Test]
    public async Task A_field_hovered_in_the_hex_view_lights_its_row()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        var (document, picks) = await ChatFixture.OpenAsync(documents);
        using InspectorViewModel inspector = new(documents);
        documents.Sync.Select(Selection.OfMessage(picks.Message, picks.Chunk));
        CaptureSession session = document.Session!;

        // A header byte: the hex view publishes it in the frame's coordinates.
        PropertyRow type = inspector.Rows!.First(r => r.Name == "Type");
        documents.Sync.SetHover(FieldHover.OfFrameBytes(picks.Chunk.Direction, picks.Chunk.Seq, type.Bytes.Offset, 1));
        await Assert.That(inspector.LinkedRow).IsSameReferenceAs(type);

        // A decoded field: the hex view publishes the decoded value's field.
        PropertyRow fileName = inspector.Rows!.First(r => r.Name is "fileName" or "FileName");
        documents.Sync.SetHover(new FieldHover(fileName.Bytes.Message, 3, fileName.Bytes.Offset, fileName.Bytes.Length));
        await Assert.That(inspector.LinkedRow).IsSameReferenceAs(fileName);

        documents.Sync.SetHover(FieldHover.None);
        await Assert.That(inspector.LinkedRow).IsNull();
        _ = session;
    }

    [Test]
    public async Task Handshake_segments_share_the_bar_one_to_two_and_label_their_direction()
    {
        AssemblySegment[] segments =
        [
            new(AssemblySegmentKind.Preamble, 0, 1, false, 0, TapDirection.Inbound),
            new(AssemblySegmentKind.Greeting, 0, 2, true, 0, TapDirection.Inbound),
            new(AssemblySegmentKind.Preamble, 0, 1, false, 0, TapDirection.Outbound),
            new(AssemblySegmentKind.Greeting, 0, 2, false, 0, TapDirection.Outbound),
        ];
        (double X, double Width)[] boxes = AssemblyBar.Layout(segments, 300);

        await Assert.That(segments.Select(s => s.Label).ToArray()).IsEquivalentTo(new[] { "Pre ▼", "Greet ▼", "Pre ▲", "Greet ▲" });
        await Assert.That(Math.Abs((boxes[1].Width / boxes[0].Width) - 2)).IsLessThan(0.01);
        await Assert.That(Math.Abs(boxes[3].X + boxes[3].Width - 300)).IsLessThan(0.5);
    }
}
