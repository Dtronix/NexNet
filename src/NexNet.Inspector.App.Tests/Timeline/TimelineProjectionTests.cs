using NexNet.Inspector.App.Controls.Timeline;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Tests.Timeline;

/// <summary>
/// The timeline fed from I18's document model: a recorded Chat session projects to every op, frame, invocation, pipe
/// and gap of the capture, re-projecting adds nothing twice, and the view-model follows the active document.
/// </summary>
public class TimelineProjectionTests
{
    private static (RecordingFile File, CaptureSession Session) Open(string fixture)
    {
        RecordingFile file = RecordingFile.Open(Wait.Fixture(fixture));
        CaptureSession session = file.CreateSession(file.Sessions[0].SessionId);
        file.Index(session);
        return (file, session);
    }

    private static int Count(TimelineModel model, LaneKind kind) => model.FindLane(new LaneKey(kind, 0))?.Marks.ToArray()
        .Count(m => m.Kind == MarkKind.Block) ?? 0;

    [Test]
    public async Task A_recorded_session_projects_every_op_frame_invocation_pipe_and_gap()
    {
        (RecordingFile file, CaptureSession session) = Open("chat-server.nntl");
        using (file)
        {
            TimelineModel model = new();
            TimelineProjection projection = new(model);
            projection.Update(session);

            await Assert.That(Count(model, LaneKind.Reads)).IsEqualTo(session.Inbound.Ops.Length);
            await Assert.That(Count(model, LaneKind.Writes)).IsEqualTo(session.Outbound.Ops.Length);
            await Assert.That(Count(model, LaneKind.InboundFrames)).IsEqualTo(session.Assembler.GetFrames(TapDirection.Inbound).Length);
            await Assert.That(Count(model, LaneKind.OutboundFrames)).IsEqualTo(session.Assembler.GetFrames(TapDirection.Outbound).Length);
            await Assert.That(model.Invocations.Length).IsEqualTo(session.Assembler.Invocations.Length);
            await Assert.That(model.Invocations.Length).IsGreaterThan(0);
            await Assert.That(model.Lanes.Count(l => l.Key.Kind == LaneKind.Pipe)).IsEqualTo(session.Assembler.Pipes.Length);
            await Assert.That(model.Gaps.Length).IsEqualTo(session.Gaps.Length);
            await Assert.That(model.Bounds.Start).IsEqualTo(session.StartTicks);
            await Assert.That(model.Frequency).IsEqualTo(session.Header.StopwatchFrequency);
            await Assert.That(model.TransportName).IsNotNull();

            // A server session recorded from its start has a handshake; every invocation row is free of overlaps.
            await Assert.That(model.FindLane(new LaneKey(LaneKind.Session, 0))?.Count ?? 0).IsGreaterThan(0);
            foreach (TimelineLane lane in model.Lanes.Where(l => l.Key.Kind == LaneKind.InvocationRow))
            {
                TimelineMark[] marks = lane.Marks.ToArray();
                for (int i = 1; i < marks.Length; i++)
                    await Assert.That(marks[i].Start).IsGreaterThan(marks[i - 1].End);
            }

            // Named from the embedded schema.
            await Assert.That(Enumerable.Range(0, model.Invocations.Length)
                .Any(i => model.GetLabel(model.Invocations[i].Label)!.Contains(' ', StringComparison.Ordinal))).IsTrue();

            int before = model.Lanes.Sum(l => l.Count);
            projection.Update(session);
            await Assert.That(model.Lanes.Sum(l => l.Count)).IsEqualTo(before);
        }
    }

    [Test]
    public async Task The_view_model_follows_the_active_document()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        using TimelineViewModel timeline = new(documents);
        await Assert.That(timeline.Model.Lanes.Count).IsEqualTo(0);

        FileDocumentViewModel document = (await documents.OpenRecordingAsync(Wait.Fixture("chat-client.nntl")))!;
        await document.Indexing;
        await Wait.UntilAsync(() => document.IndexingState == IndexingState.Complete, "the index to complete");
        timeline.Refresh();

        await Assert.That(timeline.Document).IsSameReferenceAs(document);
        TimelineLane frames = timeline.Model.FindLane(new LaneKey(LaneKind.InboundFrames, 0))!;
        await Assert.That(frames.Count + timeline.Model.FindLane(new LaneKey(LaneKind.OutboundFrames, 0))!.Count).IsEqualTo(document.FrameCount);

        timeline.TogglePinCommand.Execute(new LaneKey(LaneKind.Reads, 0));
        await Assert.That(timeline.Model.IsPinned(new LaneKey(LaneKind.Reads, 0))).IsTrue();
        timeline.ToggleGroupCommand.Execute(LaneGroupKind.Frames);
        await Assert.That(timeline.Model.IsCollapsed(LaneGroupKind.Frames)).IsTrue();

        await documents.CloseAsync(document);
        await Assert.That(timeline.Document).IsNull();
        await Assert.That(timeline.Model.Lanes.Count).IsEqualTo(0);
    }
}
