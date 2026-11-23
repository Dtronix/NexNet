using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Sample;

namespace NexNet.Inspector.App.Tests.Documents;

/// <summary>Recordings open as documents, index in the background, and share one window and sync bus.</summary>
public class DocumentsViewModelTests
{
    [Test]
    public async Task A_recording_indexes_in_the_background_and_lands_in_the_recents()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.File("settings.json"));
        var ui = new TestUi();
        await using var documents = new DocumentsViewModel(ui, store);
        var progress = new List<double>();

        // Subscribed as the document becomes active, which is before its index starts.
        documents.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(DocumentsViewModel.Active) || documents.Active is not FileDocumentViewModel opened)
                return;
            opened.PropertyChanged += (_, p) =>
            {
                if (p.PropertyName == nameof(FileDocumentViewModel.IndexingProgress))
                {
                    lock (progress)
                        progress.Add(opened.IndexingProgress);
                }
            };
        };

        var path = Wait.Fixture("chat-server.nntl");
        var document = (await documents.OpenRecordingAsync(path))!;

        await Assert.That(documents.Active).IsSameReferenceAs(document);
        await Assert.That(documents.HasDocuments).IsTrue();
        await document.Indexing;
        await Wait.UntilAsync(() => document.IndexingState == IndexingState.Complete, "the index to complete");

        await Assert.That(document.IndexingProgress).IsEqualTo(1.0);
        await Assert.That(document.IsIndexing).IsFalse();
        await Assert.That(document.FrameCount).IsGreaterThan(0);
        await Assert.That(document.GapCount).IsGreaterThan(0);
        await Assert.That(document.CaptureState).IsEqualTo(CaptureState.Ended);
        await Assert.That(document.SchemaStatus).IsEqualTo(SchemaStatus.Embedded);
        await Assert.That(document.HasWarnings).IsFalse();
        await Assert.That(document.IsDirty).IsFalse();
        await Assert.That(progress.Count).IsGreaterThan(0);
        await Assert.That(progress.Zip(progress.Skip(1)).All(p => p.First <= p.Second)).IsTrue();

        // The file's time range is the window until the user zooms.
        await Assert.That(documents.Sync.Window).IsEqualTo(document.TimeRange);

        var recent = store.Current.RecentFiles.Single();
        await Assert.That(recent.Path).IsEqualTo(Path.GetFullPath(path));
        await Assert.That(recent.Sessions).IsEqualTo(1);
        var reloaded = new SettingsStore(store.Path);
        reloaded.Load();
        await Assert.That(reloaded.Current.RecentFiles.Single().Path).IsEqualTo(Path.GetFullPath(path));
    }

    [Test]
    public async Task Damaged_and_legacy_recordings_open_with_their_warning_and_schema_state()
    {
        var ui = new TestUi();
        await using var documents = new DocumentsViewModel(ui, null) { WorkspaceSchema = NexusSchema.Of<ChatServerNexus>() };

        var truncated = (await documents.OpenRecordingAsync(Wait.Fixture("server-all-truncated.nntl")))!;
        await truncated.Indexing;
        await Assert.That(truncated.HasWarnings).IsTrue();
        await Assert.That(truncated.Warnings).IsEqualTo(RecordingWarnings.IndexFromScan | RecordingWarnings.Damaged);
        await Assert.That(truncated.Damage.Count).IsEqualTo(1);

        var legacy = (await documents.OpenRecordingAsync(Wait.Fixture("legacy-schema.nntl")))!;
        await legacy.Indexing;
        await Wait.UntilAsync(() => legacy.SchemaStatus == SchemaStatus.Mismatch, "the mismatch state");
        await Assert.That(legacy.SchemaSource).IsEqualTo(SchemaSource.Embedded);

        var raw = (await documents.OpenRecordingAsync(Wait.Fixture("legacy-schema.nntl"),
            new RecordingOpenChoice(legacy.Session!.SessionId, SchemaChoice.Raw)))!;
        await raw.Indexing;
        await Wait.UntilAsync(() => raw.FrameCount > 0, "frames");
        await Assert.That(raw.SchemaSource).IsEqualTo(SchemaSource.None);
    }

    [Test]
    public async Task A_multi_session_recording_asks_the_picker_and_a_canceled_pick_opens_nothing()
    {
        var ui = new TestUi();
        await using var documents = new DocumentsViewModel(ui, null);
        long? offered = null;
        documents.SessionPicker = recording =>
        {
            offered = recording.Sessions.Count;
            return Task.FromResult<RecordingOpenChoice?>(null);
        };

        await Assert.That(await documents.OpenRecordingAsync(Wait.Fixture("server-all.nntl"))).IsNull();
        await Assert.That(offered).IsEqualTo(3);
        await Assert.That(documents.HasDocuments).IsFalse();

        documents.SessionPicker = recording => Task.FromResult<RecordingOpenChoice?>(
            new RecordingOpenChoice(recording.Sessions[2].SessionId, SchemaChoice.Embedded));
        var third = (await documents.OpenRecordingAsync(Wait.Fixture("server-all.nntl")))!;
        await Assert.That(third.Session!.SessionId).IsEqualTo(third.Recording.Sessions[2].SessionId);
        await Assert.That(third.Title).Contains("Session 0x");
    }

    [Test]
    public async Task Activate_close_and_ctrl_tab_follow_most_recent_use()
    {
        var ui = new TestUi();
        await using var documents = new DocumentsViewModel(ui, null);
        var a = (await documents.OpenRecordingAsync(Wait.Fixture("chat-server.nntl")))!;
        var b = (await documents.OpenRecordingAsync(Wait.Fixture("chat-client.nntl")))!;
        var c = (await documents.OpenRecordingAsync(Wait.Fixture("server-all.nntl")))!;
        await Task.WhenAll(a.Indexing, b.Indexing, c.Indexing);

        // Reference order checks: an equivalency assertion would walk each document's whole object graph.
        await Assert.That(documents.Documents.SequenceEqual(new DocumentViewModel[] { a, b, c })).IsTrue();
        await Assert.That(documents.Active).IsSameReferenceAs(c);

        documents.ActivateCommand.Execute(a);
        await Assert.That(documents.RecentOrder.SequenceEqual(new DocumentViewModel[] { a, c, b })).IsTrue();

        // Ctrl+Tab toggles between the two most recent; Ctrl+Shift+Tab reaches the least recent.
        documents.CycleCommand.Execute(false);
        await Assert.That(documents.Active).IsSameReferenceAs(c);
        documents.CycleCommand.Execute(false);
        await Assert.That(documents.Active).IsSameReferenceAs(a);
        documents.CycleCommand.Execute(true);
        await Assert.That(documents.Active).IsSameReferenceAs(b);

        // Closing the active document activates the most recent other one.
        await Assert.That(await documents.CloseAsync(b)).IsTrue();
        await Assert.That(documents.Active).IsSameReferenceAs(a);
        await Assert.That(documents.Documents.Count).IsEqualTo(2);
        await documents.CloseAsync(a);
        await documents.CloseAsync(c);
        await Assert.That(documents.Active).IsNull();
        await Assert.That(documents.HasDocuments).IsFalse();
        await Assert.That(documents.Sync.Window.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Each_document_keeps_its_selection_and_the_window_keeps_its_place_across_tabs()
    {
        var ui = new TestUi();
        await using var documents = new DocumentsViewModel(ui, null);
        var a = (await documents.OpenRecordingAsync(Wait.Fixture("chat-server.nntl")))!;
        await a.Indexing;
        var b = (await documents.OpenRecordingAsync(Wait.Fixture("chat-client.nntl")))!;
        await b.Indexing;
        documents.Activate(a);

        var frame = new FrameKey(TapDirection.Inbound, 3);
        var aStart = a.Session!.StartTicks;
        var aWindow = TimeWindow.FromDuration(aStart + 1_000, 5_000);
        documents.Sync.SetWindow(aWindow);
        documents.Sync.Select(Selection.OfFrame(frame), aStart + 2_000);

        // The window is shared: b shows the same offset from its own start, at the same zoom.
        documents.Activate(b);
        await Assert.That(documents.Sync.Selection.IsNone).IsTrue();
        var expected = aWindow.Rebase(aStart, a.Frequency, b.Session!.StartTicks, b.Frequency);
        await Assert.That(documents.Sync.Window).IsEqualTo(expected);
        await Assert.That(documents.Sync.Window.Duration).IsEqualTo(aWindow.Duration);

        documents.Activate(a);
        await Assert.That(documents.Sync.Selection).IsEqualTo(Selection.OfFrame(frame));
        await Assert.That(documents.Sync.Window).IsEqualTo(aWindow);
        await Assert.That(documents.Sync.Playhead).IsEqualTo(aStart + 2_000);
    }

    [Test]
    public async Task Closing_a_dirty_document_asks_and_cancel_keeps_it()
    {
        var ui = new TestUi();
        await using var documents = new DocumentsViewModel(ui, null);
        var document = (await documents.OpenRecordingAsync(Wait.Fixture("chat-server.nntl")))!;
        await document.Indexing;
        var asked = 0;
        documents.ClosePrompt = _ =>
        {
            asked++;
            return Task.FromResult(CloseDecision.Cancel);
        };

        // A recording is saved data: closing it never asks.
        await Assert.That(await documents.CloseAsync(document)).IsTrue();
        await Assert.That(asked).IsEqualTo(0);

        var dirty = new DirtyDocument(ui);
        documents.Documents.Add(dirty);
        await Assert.That(await documents.CloseAsync(dirty)).IsFalse();
        await Assert.That(asked).IsEqualTo(1);
        await Assert.That(documents.Documents).Contains(dirty);

        documents.ClosePrompt = _ => Task.FromResult(CloseDecision.Discard);
        await Assert.That(await documents.CloseAsync(dirty)).IsTrue();
        await Assert.That(dirty.Disposed).IsTrue();
    }

    private sealed class DirtyDocument : DocumentViewModel
    {
        public DirtyDocument(Services.IUiDispatcher ui)
            : base(DocumentKind.Live, ui) => IsDirty = true;

        public bool Disposed { get; private set; }

        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
