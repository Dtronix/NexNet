using NexNet.Inspector.App.Controls.Ladder;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Session;
using NexNet.Messages;

namespace NexNet.Inspector.App.Tests.Ladder;

/// <summary>
/// The ladder fed from I18's document model: a recorded Chat session projects to a row per frame in completion order,
/// with invocation targets, numbered chunks and named endpoints; re-projecting adds nothing twice, and the view-model
/// follows the active document and offers both row modes.
/// </summary>
public class LadderProjectionTests
{
    private static (RecordingFile File, CaptureSession Session) Open(string fixture)
    {
        RecordingFile file = RecordingFile.Open(Wait.Fixture(fixture));
        CaptureSession session = file.CreateSession(file.Sessions[0].SessionId);
        file.Index(session);
        return (file, session);
    }

    [Test]
    public async Task A_recorded_session_projects_a_row_per_frame_in_completion_order()
    {
        (RecordingFile file, CaptureSession session) = Open("chat-server.nntl");
        using (file)
        {
            LadderModel model = new();
            LadderProjection projection = new(model);
            projection.Update(session);

            await Assert.That(model.Count).IsEqualTo(session.FrameCount);
            await Assert.That(model.Count).IsGreaterThan(0);
            FrameKey[] order = session.FrameOrder.ToArray();
            for (int i = 0; i < order.Length; i++)
                await Assert.That(model[i].Frame).IsEqualTo(order[i]);
            await Assert.That(model.Origin).IsEqualTo(session.StartTicks);
            await Assert.That(model.Frequency).IsEqualTo(session.Header.StopwatchFrequency);
            await Assert.That(model.RightEndpoint).IsEqualTo(session.Role == NntlSessionRole.Server ? "Server" : "Client");
            await Assert.That(model.LeftEndpoint).StartsWith(session.Role == NntlSessionRole.Server ? "Client" : "Server");

            // Every invocation frame names its invocation; a server from its start shows its preamble first.
            LadderRow[] rows = model.Rows.ToArray();
            foreach (LadderRow row in rows.Where(r => r.Type is MessageType.Invocation or MessageType.InvocationResult))
            {
                await Assert.That(row.Target).IsEqualTo(LadderTarget.Invocation);
                await Assert.That(row.Entity).IsEqualTo(EntityKind.Invocation);
                await Assert.That(row.TargetId).IsEqualTo(session.Assembler.Invocations[row.EntityIndex].InvocationId);
            }

            await Assert.That(rows.Any(r => r.Type == MessageType.Invocation)).IsTrue();
            await Assert.That(rows.Any(r => r.Note == LadderNote.Completed)).IsTrue();
            await Assert.That(rows.Any(r => r.Name >= 0)).IsTrue().Because("methods are named from the embedded schema");

            // Chunks of one message count 1, 2, 3 … in order.
            foreach (IGrouping<int, LadderRow> chunks in rows.Where(r => r.Part > 0).GroupBy(r => r.Message))
            {
                int[] parts = chunks.Select(r => r.Part).ToArray();
                await Assert.That(parts).IsEquivalentTo(Enumerable.Range(1, parts.Length).ToArray());
                await Assert.That(model.ChunkTotal(chunks.Key)).IsEqualTo(parts.Length);
            }

            int version = model.Version;
            projection.Update(session);
            await Assert.That(model.Count).IsEqualTo(session.FrameCount);
            await Assert.That(model.Version).IsEqualTo(version + 1);
        }
    }

    [Test]
    public async Task The_view_model_follows_the_active_document_and_offers_both_row_modes()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        using LadderViewModel ladder = new(documents);
        await Assert.That(ladder.Model.Count).IsEqualTo(0);
        await Assert.That(ladder.Mode.Value).IsEqualTo(LadderMode.Compact);
        await Assert.That(ladder.Mode.Labels).IsEquivalentTo(new[] { "18 px rows", "Time-scaled" });
        await Assert.That(ladder.IsSynced).IsTrue();
        await Assert.That(ladder.Sync).IsSameReferenceAs(documents.Sync);

        FileDocumentViewModel document = (await documents.OpenRecordingAsync(Wait.Fixture("chat-client.nntl")))!;
        await document.Indexing;
        await Wait.UntilAsync(() => document.IndexingState == IndexingState.Complete, "the index to complete");
        ladder.Refresh();

        await Assert.That(ladder.Document).IsSameReferenceAs(document);
        await Assert.That(ladder.Model.Count).IsEqualTo(document.Session!.FrameCount);
        await Assert.That(ladder.Model.RightEndpoint).IsEqualTo("Client");

        await documents.CloseAsync(document);
        await Assert.That(ladder.Document).IsNull();
        await Assert.That(ladder.Model.Count).IsEqualTo(0);
    }
}
