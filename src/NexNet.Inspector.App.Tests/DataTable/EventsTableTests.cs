using NexNet.Inspector.App.Controls.DataTable;
using NexNet.Inspector.App.Controls.DataTable.Events;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Messages;

namespace NexNet.Inspector.App.Tests.DataTable;

/// <summary>
/// The Events rows over a recorded Chat session (the IList adapter over the session's frame index) and the pane's
/// view-model: every frame once in capture order, frame ↔ row both ways, chunk numbering, selection through the sync
/// bus both ways with the trace tint, and the empty, loading and follow-live states per document kind.
/// </summary>
public sealed class EventsTableTests
{
    private static (RecordingFile File, CaptureSession Session) Open(string fixture)
    {
        RecordingFile file = RecordingFile.Open(Wait.Fixture(fixture));
        CaptureSession session = file.CreateSession(file.Sessions[0].SessionId);
        file.Index(session);
        return (file, session);
    }

    [Test]
    public async Task The_direction_filter_keeps_one_directions_frames_with_their_capture_numbers()
    {
        (RecordingFile file, CaptureSession session) = Open("chat-server.nntl");
        using (file)
        {
            EventsTableSource rows = new();
            rows.Attach(session);
            FrameKey[] order = session.FrameOrder.ToArray();
            int outbound = order.Count(k => k.Direction == TapDirection.Outbound);
            int resets = 0;
            rows.CollectionChanged += (_, e) => resets += e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset ? 1 : 0;

            rows.Filter = EventsFilter.Outbound;
            await Assert.That(resets).IsEqualTo(1);
            await Assert.That(rows.Count).IsEqualTo(outbound).And.IsGreaterThan(0).And.IsLessThan(order.Length);
            for (int i = 0; i < rows.Count; i++)
            {
                await Assert.That(rows.TryGetRow(i, out EventRow row)).IsTrue();
                await Assert.That(row.Frame.Direction).IsEqualTo(TapDirection.Outbound);
                // The number stays the frame's place in the whole capture.
                await Assert.That(row.Number).IsEqualTo(Array.IndexOf(order, row.Frame) + 1L);
                await Assert.That(rows.IndexOf(row.Frame)).IsEqualTo(i);
            }

            await Assert.That(rows.IndexOf(order.First(k => k.Direction == TapDirection.Inbound))).IsEqualTo(-1);

            rows.Filter = EventsFilter.Inbound;
            await Assert.That(rows.Count).IsEqualTo(order.Length - outbound);
            rows.Filter = EventsFilter.All;
            await Assert.That(rows.Count).IsEqualTo(order.Length);
            await Assert.That(rows.IndexOf(order[^1])).IsEqualTo(order.Length - 1);
        }
    }

    [Test]
    public async Task The_panes_filter_choice_filters_the_rows_and_keeps_the_selection_when_it_still_shows()
    {
        (RecordingFile file, CaptureSession session) = Open("chat-server.nntl");
        using (file)
        {
            SyncBus sync = new(new TestUi());
            using EventsViewModel events = new(sync);
            events.Rows.Attach(session);
            FrameKey outbound = session.FrameOrder.ToArray().Last(k => k.Direction == TapDirection.Outbound);
            sync.Select(Selection.OfFrame(outbound));
            await Assert.That(events.Filter.Labels.ToArray()).IsEquivalentTo(new[] { "Filter", "▼ IN only", "▲ OUT only" },
                TUnit.Assertions.Enums.CollectionOrdering.Matching);

            events.Filter.Value = EventsFilter.Outbound;
            await Assert.That(events.Rows.Filter).IsEqualTo(EventsFilter.Outbound);
            await Assert.That(events.SelectedIndex).IsEqualTo(events.Rows.IndexOf(outbound)).And.IsGreaterThanOrEqualTo(0);

            events.Filter.Value = EventsFilter.Inbound;
            await Assert.That(events.SelectedIndex).IsEqualTo(-1).Because("the selected frame is filtered out");
            await Assert.That(sync.Selection).IsEqualTo(Selection.OfFrame(outbound)).Because("the selection itself stays");
        }
    }

    [Test]
    public async Task Every_frame_is_one_row_in_capture_order_and_found_again_by_its_key()
    {
        (RecordingFile file, CaptureSession session) = Open("chat-server.nntl");
        using (file)
        {
            EventsTableSource rows = new();
            int resets = 0, added = 0;
            rows.CollectionChanged += (_, e) =>
            {
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++;
                else added += e.NewItems!.Count;
            };
            rows.Attach(session);

            await Assert.That(rows.Count).IsEqualTo(session.FrameCount).And.IsGreaterThan(10);
            await Assert.That(resets).IsEqualTo(1);
            await Assert.That(added).IsEqualTo(session.FrameCount);
            await Assert.That(rows.Refresh()).IsEqualTo(0);

            FrameKey[] order = session.FrameOrder.ToArray();
            double lastMs = double.MinValue;
            for (int i = 0; i < rows.Count; i++)
            {
                await Assert.That(rows.IndexOf(order[i])).IsEqualTo(i);
                await Assert.That(rows.TryGetRow(i, out EventRow row)).IsTrue();
                await Assert.That(row.Frame).IsEqualTo(order[i]);
                await Assert.That(row.Number).IsEqualTo(i + 1L);
                await Assert.That(EventText.Type(row).Length).IsGreaterThan(0);
                session.TryGetFrame(order[i], out FrameRecord frame);
                await Assert.That(row.Length).IsEqualTo(frame.Length);
                await Assert.That(row.Flags).IsEqualTo(frame.Flags);
                await Assert.That(row.TimeMs).IsGreaterThanOrEqualTo(0);
                lastMs = Math.Max(lastMs, row.TimeMs);
            }

            await Assert.That(rows.IndexOf(new FrameKey(TapDirection.Inbound, 1_000_000))).IsEqualTo(-1);

            // A server captured from its start: the first row is the client's preamble.
            await Assert.That(rows.TryGetRow(0, out EventRow first)).IsTrue();
            await Assert.That(EventText.Type(first)).IsEqualTo("Preamble");

            // Invocation rows name their target, from the embedded schema where it knows the method.
            List<EventRow> all = [];
            for (int i = 0; i < rows.Count; i++)
            {
                rows.TryGetRow(i, out EventRow row);
                all.Add(row);
            }

            await Assert.That(all.Any(r => r.Type == MessageType.Invocation && r.Target == EventTargetKind.Invocation && r.MethodName is not null)).IsTrue();

            // The chunks of a large message count up within it.
            EventRow[] chunks = [.. all.Where(r => r.Type == MessageType.MessageChunk)];
            await Assert.That(chunks.Length).IsGreaterThan(1);
            {
                EventRow[] ofFirst = [.. chunks.Where(c => c.Message == chunks[0].Message)];
                await Assert.That(ofFirst.Select(c => c.ChunkNumber)).IsEquivalentTo(Enumerable.Range(1, ofFirst.Length));
                await Assert.That(ofFirst.All(c => c.ChunkCount is 0 || c.ChunkCount == ofFirst.Length)).IsTrue();
            }
        }
    }

    [Test]
    public async Task Selection_flows_both_ways_through_the_sync_bus_and_tints_the_traced_message()
    {
        (RecordingFile file, CaptureSession session) = Open("chat-server.nntl");
        using (file)
        {
            SyncBus sync = new(new TestUi());
            using EventsViewModel events = new(sync);
            events.Rows.Attach(session);

            // A row picked in the table selects its frame and moves the playhead.
            events.SelectedIndex = 4;
            events.Rows.TryGetRow(4, out EventRow picked);
            await Assert.That(sync.Selection).IsEqualTo(Selection.OfFrame(picked.Frame));
            await Assert.That(sync.Playhead).IsEqualTo(picked.Ticks);

            // A frame selected elsewhere selects its row.
            FrameKey other = session.FrameOrder[9];
            sync.Select(Selection.OfFrame(other));
            await Assert.That(events.SelectedIndex).IsEqualTo(9);

            // Something that is not a frame clears it.
            sync.Select(Selection.OfEntity(EntityKind.Invocation, 0));
            await Assert.That(events.SelectedIndex).IsEqualTo(-1);
            await Assert.That(events.Trace.IsNone).IsTrue();

            // A frame of a message split across frames traces that message: its rows carry the tint.
            int split = -1;
            for (int i = 0; i < events.Rows.Count && split < 0; i++)
            {
                if (events.Rows.TryGetRow(i, out EventRow row) && row.Message.Kind != MessageRefKind.Frame) split = i;
            }

            await Assert.That(split).IsGreaterThanOrEqualTo(0);
            {
                events.SelectedIndex = split;
                events.Rows.TryGetRow(split, out EventRow selected);
                await Assert.That(events.Trace).IsEqualTo(selected.Message);
                await Assert.That(selected.IsTraced).IsTrue();
                int tinted = 0;
                for (int i = 0; i < events.Rows.Count; i++)
                {
                    events.Rows.TryGetRow(i, out EventRow row);
                    if (row.IsTraced) tinted++;
                    await Assert.That(row.IsTraced).IsEqualTo(row.Message == selected.Message);
                }

                await Assert.That(tinted).IsGreaterThanOrEqualTo(1);

                // Enter traces: the message becomes the selection everyone follows.
                events.TraceSelectedCommand.Execute(null);
                await Assert.That(sync.Selection.Kind).IsEqualTo(SelectionKind.Message);
                await Assert.That(sync.Selection.Message).IsEqualTo(selected.Message);
                await Assert.That(events.SelectedIndex).IsEqualTo(split);
            }
        }
    }

    [Test]
    public async Task The_view_model_follows_the_active_document_with_its_states()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        using EventsViewModel events = new(documents);
        await Assert.That(events.Rows.Count).IsEqualTo(0);
        await Assert.That(events.EmptyText).IsEqualTo(EventsViewModel.NoDocumentText);
        await Assert.That(events.IsLoading).IsFalse();

        FileDocumentViewModel document = (await documents.OpenRecordingAsync(Wait.Fixture("chat-client.nntl")))!;
        await document.Indexing;
        await Wait.UntilAsync(() => document.IndexingState == IndexingState.Complete, "the index to complete");
        events.Refresh();

        await Assert.That(events.Document).IsSameReferenceAs(document);
        await Assert.That(events.Rows.Count).IsEqualTo(document.FrameCount).And.IsGreaterThan(0);
        await Assert.That(events.IsLoading).IsFalse();
        await Assert.That(events.EmptyText).IsEqualTo(EventsViewModel.FileEmptyText);

        // A recording has no live edge: the table cannot turn following on.
        events.FollowLive = true;
        await Assert.That(events.FollowLive).IsFalse();
        await Assert.That(documents.Sync.FollowLive).IsFalse();

        await documents.CloseAsync(document);
        await Assert.That(events.Document).IsNull();
        await Assert.That(events.Rows.Count).IsEqualTo(0);
    }

    [Test]
    public async Task The_events_view_binds_the_table_to_the_view_model()
    {
        (RecordingFile file, CaptureSession session) = Open("chat-server.nntl");
        using (file)
        {
            (int count, DataTableState state, int selected, bool following) = await HeadlessSession.Run(() =>
            {
                SyncBus sync = new(new TestUi());
                using EventsViewModel events = new(sync);
                events.Rows.Attach(session);
                Views.Inspect.EventsView view = new() { DataContext = events, Height = 400 };
                Avalonia.Controls.Window host = new() { Width = 640, Height = 420, Content = view };
                host.Show();
                UiPump.Frames();
                sync.Select(Selection.OfFrame(session.FrameOrder[3]));
                UiPump.Frames();
                var result = (view.Grid.ItemCount, view.Grid.State, view.Grid.SelectedIndex, view.Grid.FollowTail);
                host.Close();
                return result;
            });

            await Assert.That(count).IsEqualTo(session.FrameCount);
            await Assert.That(state).IsEqualTo(DataTableState.Rows);
            await Assert.That(selected).IsEqualTo(3);
            await Assert.That(following).IsFalse();
        }
    }
}
