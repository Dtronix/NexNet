using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Sample;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.App.Tests.Documents;

/// <summary>
/// Session end and save (I29) against a live tapped Chat server: the session-ended strip, Save .nntl… (the saved file
/// reopens with the same frames and entities and takes over the tab), the unsaved-close prompt (OQ5), Discard, Attach to
/// another session, saving while live, and a detach resetting the view.
/// </summary>
public class SessionEndTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public required ChatServerHarness Harness { get; init; }
        public required TempDirectory Temp { get; init; }
        public required SettingsStore Store { get; init; }
        public required DocumentsViewModel Documents { get; init; }
        public required SessionEndedViewModel Ended { get; init; }
        public Queue<CloseDecision> Answers { get; } = new();
        public List<string> Prompts { get; } = [];
        public string? NextPath { get; set; }
        public int AttachElsewhere { get; set; }

        public async ValueTask DisposeAsync()
        {
            await Documents.DisposeAsync();
            await Harness.DisposeAsync();
            Temp.Dispose();
        }
    }

    private static async Task<Rig> StartAsync()
    {
        var harness = await ChatServerHarness.StartAsync(allowRecording: true);
        var temp = new TempDirectory();
        var store = new SettingsStore(temp.File("settings.json"));
        var documents = new DocumentsViewModel(new TestUi(), store);
        Rig? rig = null;
        var ended = new SessionEndedViewModel(documents, () => rig!.AttachElsewhere++);
        rig = new Rig { Harness = harness, Temp = temp, Store = store, Documents = documents, Ended = ended };
        documents.SavePicker = _ => Task.FromResult(rig.NextPath);
        documents.SaveHandler = async d => await documents.SaveAsync(d) is not null;
        documents.ClosePrompt = d =>
        {
            rig.Prompts.Add(d.Title);
            return Task.FromResult(rig.Answers.Dequeue());
        };
        return rig;
    }

    private static async Task<LiveDocumentViewModel> AttachAsync(Rig rig, InspectorSessionInfo session)
    {
        EndpointAddress.TryParse(rig.Harness.EndpointAddress, out var address, out _);
        var connection = await InspectorConnection.ConnectAsync(address, "app-test-endpoint-token"u8.ToArray(), EndpointConnectOptions.Default);
        return await rig.Documents.AttachAsync(connection, session, InspectorOverflow.Block, snapshot: true);
    }

    // Join, a message and an 18 KB attachment: enough for several invocations and both directions. The captured stream
    // trails the client's calls: wait until every invocation has its result (the attachment's request starts an
    // invocation before its result arrives), so nothing of the conversation is still on its way.
    private static async Task ConverseAsync(NexusClient<ChatClientNexus, ChatClientNexus.ServerProxy> client, LiveDocumentViewModel document)
    {
        await client.Proxy.Join("ada");
        await client.Proxy.SendMessage(new ChatMessage { Room = "general", Author = "ada", Text = "hello" }, CancellationToken.None);
        await client.Proxy.SendAttachment(new Attachment { Room = "general", FileName = "a.bin", Data = new byte[18_000] });
        await Wait.UntilAsync(() => document.Session is { } s && Settled(s) && document.IsDirty, "the conversation");
    }

    [Test]
    public async Task An_ended_session_shows_the_strip_and_Save_makes_the_tab_the_same_recording()
    {
        await using var rig = await StartAsync();
        var client = await rig.Harness.ConnectClientAsync();
        var info = await rig.Harness.WaitForSessionAsync();
        var live = await AttachAsync(rig, info);
        await Assert.That(rig.Ended.Live).IsSameReferenceAs(live);
        await ConverseAsync(client, live);
        await Assert.That(rig.Ended.IsShown).IsFalse();

        await client.DisconnectAsync();
        await Wait.UntilAsync(() => live.State == LiveState.Ended && rig.Ended.IsShown, "the session to end");
        await Assert.That(rig.Ended.Title).IsEqualTo("Session " + LiveDocumentViewModel.SessionHex(info.Id) + " ended");
        await Assert.That(rig.Ended.Detail).StartsWith("DisconnectGraceful (21) from client · ");
        await Assert.That(rig.Ended.Detail).Contains(" attached · " + RecordingText.Plural(live.FrameCount, "event") + " · ");
        await Assert.That(rig.Ended.Detail).EndsWith(" · not saved");
        await Assert.That(rig.Ended.Hint).IsEqualTo(SessionEndedViewModel.StaysOpenHint);
        await Assert.That(live.EndedBy).IsEqualTo("client");

        // Save .nntl…: the file is written, the tab becomes it (same place, still active), the live tab and its connection go.
        var captured = live.Session!;
        rig.NextPath = rig.Temp.File("saved.nntl");
        await rig.Ended.SaveCommand.ExecuteAsync(null);
        await Assert.That(rig.Ended.Error).IsNull();
        await Assert.That(File.Exists(rig.NextPath)).IsTrue();
        await Assert.That(File.Exists(rig.NextPath + ".partial")).IsFalse();
        await Assert.That(live.SavedPath).IsEqualTo(Path.GetFullPath(rig.NextPath));
        var file = rig.Documents.Documents.Single() as FileDocumentViewModel;
        await Assert.That(file).IsNotNull();
        await Assert.That(rig.Documents.Active).IsSameReferenceAs(file);
        await Assert.That(rig.Ended.IsShown).IsFalse();
        await Assert.That(rig.Store.Current.RecentFiles[0].Path).IsEqualTo(Path.GetFullPath(rig.NextPath));
        await file!.Indexing;
        await Wait.UntilAsync(() => file.IndexingState == IndexingState.Complete, "the saved file to index");

        // The recording holds what the live document held: frames, their order, bytes and entities.
        var saved = file.Session!;
        await Assert.That(saved.SessionId).IsEqualTo(captured.SessionId);
        await Assert.That(saved.State).IsEqualTo(CaptureState.Ended);
        await Assert.That(saved.Close).IsEqualTo(captured.Close);
        await Assert.That(file.FrameCount).IsEqualTo(captured.FrameCount);
        await Assert.That(saved.FrameOrder.ToArray().SequenceEqual(captured.FrameOrder.ToArray())).IsTrue();
        foreach (var direction in new[] { TapDirection.Inbound, TapDirection.Outbound })
        {
            await Assert.That(saved.Assembler.GetFrames(direction).ToArray().SequenceEqual(captured.Assembler.GetFrames(direction).ToArray()))
                .IsTrue().Because(direction.ToString());
            await Assert.That(saved.GetStore(direction).ByteCount).IsEqualTo(captured.GetStore(direction).ByteCount);
        }

        await Assert.That(saved.Assembler.Invocations.ToArray().SequenceEqual(captured.Assembler.Invocations.ToArray())).IsTrue();
        await Assert.That(saved.Assembler.Greetings.ToArray().SequenceEqual(captured.Assembler.Greetings.ToArray())).IsTrue();
        await Assert.That(saved.Assembler.Pipes.ToArray().SequenceEqual(captured.Assembler.Pipes.ToArray())).IsTrue();
        await Assert.That(saved.Snapshot is not null).IsEqualTo(captured.Snapshot is not null);
        await Assert.That(saved.Schema.Source).IsEqualTo(SchemaSource.Embedded);
        await Assert.That(saved.StartTicks).IsEqualTo(captured.StartTicks);
        await Assert.That(rig.Prompts).IsEmpty();
    }

    [Test]
    public async Task Closing_an_unsaved_live_tab_asks_save_discard_or_cancel()
    {
        await using var rig = await StartAsync();
        var client = await rig.Harness.ConnectClientAsync();
        var info = await rig.Harness.WaitForSessionAsync();
        var live = await AttachAsync(rig, info);
        await ConverseAsync(client, live);

        rig.Answers.Enqueue(CloseDecision.Cancel);
        await Assert.That(await rig.Documents.CloseAsync(live)).IsFalse();
        await Assert.That(rig.Documents.Documents.Contains(live)).IsTrue();

        // Save, but the picker is canceled: nothing is lost, the tab stays.
        rig.Answers.Enqueue(CloseDecision.Save);
        rig.NextPath = null;
        await Assert.That(await rig.Documents.CloseAsync(live)).IsFalse();
        await Assert.That(rig.Documents.Documents.Contains(live)).IsTrue();
        await Assert.That(rig.Prompts.Count).IsEqualTo(2);
        await Assert.That(rig.Prompts[0]).IsEqualTo(LiveDocumentViewModel.TabTitle(info.Id));

        // Save to a file: written, then the tab closes.
        rig.Answers.Enqueue(CloseDecision.Save);
        rig.NextPath = rig.Temp.File("closed.nntl");
        var frames = live.FrameCount;
        await Assert.That(await rig.Documents.CloseAsync(live)).IsTrue();
        await Assert.That(rig.Documents.HasDocuments).IsFalse();
        using (var recording = RecordingFile.Open(rig.NextPath))
        {
            var session = recording.CreateSession(recording.Sessions[0].SessionId);
            recording.Index(session);
            await Assert.That(session.FrameCount).IsGreaterThanOrEqualTo(frames);
            await Assert.That(session.Close!.Value.Kind).IsEqualTo(NntlCloseKind.RecordingStopped);
        }

        // Discard drops the data; a tab with nothing captured closes without asking.
        var again = await AttachAsync(rig, info);
        await client.Proxy.SendMessage(new ChatMessage { Room = "general", Author = "ada", Text = "more" }, CancellationToken.None);
        await Wait.UntilAsync(() => again.IsDirty, "new data");
        rig.Answers.Enqueue(CloseDecision.Discard);
        await Assert.That(await rig.Documents.CloseAsync(again)).IsTrue();
        await Assert.That(rig.Prompts.Count).IsEqualTo(4);

        var clean = await AttachAsync(rig, info);
        await Assert.That(clean.IsDirty).IsFalse();
        await Assert.That(await rig.Documents.CloseAsync(clean)).IsTrue();
        await Assert.That(rig.Prompts.Count).IsEqualTo(4);
    }

    [Test]
    public async Task Saving_while_live_is_clean_until_more_arrives()
    {
        await using var rig = await StartAsync();
        var client = await rig.Harness.ConnectClientAsync();
        var info = await rig.Harness.WaitForSessionAsync();
        var live = await AttachAsync(rig, info);
        await ConverseAsync(client, live);

        var path = rig.Temp.File("live.nntl");
        await rig.Documents.SaveToAsync(live, path);
        await Assert.That(live.IsDirty).IsFalse();
        await Assert.That(live.SavedPath).IsEqualTo(path);
        await Assert.That(live.State).IsEqualTo(LiveState.Live);
        await Assert.That(rig.Documents.Documents.Single()).IsSameReferenceAs(live);
        await Assert.That(DocumentsViewModel.SuggestedFileName(live, live.Session!, new DateTimeOffset(2026, 11, 23, 12, 4, 0, TimeSpan.Zero)))
            .IsEqualTo("server-" + LiveDocumentViewModel.SessionHex(info.Id) + "-20261123-120400.nntl");

        await client.Proxy.SendMessage(new ChatMessage { Room = "general", Author = "ada", Text = "after" }, CancellationToken.None);
        await Wait.UntilAsync(() => live.IsDirty, "the document to be dirty again");

        // The unsaved prompt then says what is new since the save.
        var prompt = new UnsavedCloseDialogViewModel(live);
        await Assert.That(prompt.Body).StartsWith("More arrived since the save as live.nntl");
        prompt.CancelCommand.Execute(null);
        await Assert.That(await prompt.Result).IsEqualTo(CloseDecision.Cancel);
    }

    [Test]
    public async Task Attach_elsewhere_asks_first_and_Discard_closes_without_asking()
    {
        await using var rig = await StartAsync();
        var first = await rig.Harness.ConnectClientAsync();
        var info = await rig.Harness.WaitForSessionAsync();
        var second = await rig.Harness.ConnectClientAsync();
        await rig.Harness.WaitForSessionAsync(2);
        var area = new SessionAreaViewModel(rig.Documents);
        var live = await AttachAsync(rig, info);
        await ConverseAsync(first, live);
        await first.DisconnectAsync();
        await Wait.UntilAsync(() => rig.Ended.IsShown, "the strip");

        rig.Ended.AttachElsewhereCommand.Execute(null);
        await Assert.That(rig.AttachElsewhere).IsEqualTo(1);

        // Picking another session drops the ended data: the prompt asks first, and Cancel keeps it.
        await area.RefreshSessionsAsync();
        var other = area.Sessions.Single(s => s.Info.Id != info.Id);
        rig.Answers.Enqueue(CloseDecision.Cancel);
        await area.ChooseCommand.ExecuteAsync(other);
        await Assert.That(live.State).IsEqualTo(LiveState.Ended);
        await Assert.That(rig.Ended.IsShown).IsTrue();

        rig.Answers.Enqueue(CloseDecision.Discard);
        await area.ChooseCommand.ExecuteAsync(other);
        await Assert.That(area.Error).IsNull();
        await Assert.That(live.State).IsEqualTo(LiveState.Live);
        await Assert.That(live.AttachedSession!.Value.Id).IsEqualTo(other.Info.Id);
        await Assert.That(rig.Ended.IsShown).IsFalse();
        await Assert.That(rig.Prompts.Count).IsEqualTo(2);

        await ConverseAsync(second, live);
        await second.DisconnectAsync();
        await Wait.UntilAsync(() => rig.Ended.IsShown, "the second session's strip");
        await rig.Ended.DiscardCommand.ExecuteAsync(null);
        await Assert.That(rig.Documents.HasDocuments).IsFalse();
        await Assert.That(rig.Prompts.Count).IsEqualTo(2);
        await Assert.That(rig.Ended.Live).IsNull();
        await Assert.That(rig.Ended.IsShown).IsFalse();
        await area.DisposeAsync();
    }

    [Test]
    public async Task Detach_from_the_popover_stops_the_stream_keeps_the_data_and_Save_works()
    {
        await using var rig = await StartAsync();
        var area = new SessionAreaViewModel(rig.Documents);
        var client = await rig.Harness.ConnectClientAsync();
        var info = await rig.Harness.WaitForSessionAsync();
        var live = await AttachAsync(rig, info);
        await ConverseAsync(client, live);

        // A selection on the live data, the window following it.
        var sync = rig.Documents.Sync;
        var session = live.Session!;
        FrameKey key;
        long ticks;
        lock (session.Gate)
        {
            key = session.FrameOrder[0];
            session.TryGetFrame(key, out var frame);
            ticks = frame.StartTicks;
        }

        sync.Select(Selection.OfFrame(key), ticks);
        await Assert.That(area.Live).IsSameReferenceAs(live);
        await Assert.That(area.CanDetach).IsTrue();
        await Assert.That(area.DetachLabel).IsEqualTo("Detach from " + LiveDocumentViewModel.SessionHex(info.Id));
        await Assert.That(area.DetachDetail).IsEqualTo("Stops viewing · the captured data stays in the tab");

        area.IsPopoverOpen = true;
        await area.DetachCommand.ExecuteAsync(null);
        await Assert.That(area.IsPopoverOpen).IsFalse();
        await Assert.That(live.State).IsEqualTo(LiveState.Ended);
        await Assert.That(live.IsDetachedByUser).IsTrue();
        await Assert.That(live.IsStreaming).IsFalse();
        await Assert.That(area.CanDetach).IsFalse();
        await Assert.That(area.DetachCommand.CanExecute(null)).IsFalse();

        // The data stays and the view is not reset.
        await Assert.That(live.Session).IsSameReferenceAs(session);
        await Assert.That(live.IsDirty).IsTrue();
        await Assert.That(sync.Selection).IsEqualTo(Selection.OfFrame(key));
        await Assert.That(sync.Window.IsEmpty).IsFalse();
        await Wait.UntilAsync(() => rig.Ended.IsShown, "the session-ended strip");
        await Assert.That(rig.Ended.Title).IsEqualTo("Detached from " + LiveDocumentViewModel.SessionLabel(info.Id));
        await Assert.That(rig.Ended.Detail).StartsWith(LiveDocumentViewModel.DetachedReason + " · ");
        await Assert.That(rig.Ended.Detail).EndsWith(" · not saved");

        // The server stopped streaming to this viewer: more traffic adds nothing, and the endpoint takes a new attach on
        // the same connection (one stream per connection: it would answer Busy while the old one still ran).
        int frames;
        lock (session.Gate)
            frames = session.FrameCount;
        await client.Proxy.SendMessage(new ChatMessage { Room = "general", Author = "ada", Text = "after" }, CancellationToken.None);
        await Task.Delay(300);
        int after;
        lock (session.Gate)
            after = session.FrameCount;
        await Assert.That(after).IsEqualTo(frames);
        var probe = await live.Connection.Client.AttachAsync(new InspectorAttachRequest(InspectorTargetKind.Session, info.Id, InspectorOverflow.Drop, false, false));
        await live.Connection.Client.DetachAsync();
        await probe.DisposeAsync();

        // Save .nntl… from the strip: the tab becomes the saved recording, with what was captured before the detach.
        rig.NextPath = rig.Temp.File("detached.nntl");
        await rig.Ended.SaveCommand.ExecuteAsync(null);
        await Assert.That(rig.Ended.Error).IsNull();
        await Assert.That(File.Exists(rig.NextPath)).IsTrue();
        var file = rig.Documents.Documents.Single() as FileDocumentViewModel;
        await Assert.That(file).IsNotNull();
        await file!.Indexing;
        await Wait.UntilAsync(() => file.IndexingState == IndexingState.Complete, "the saved file to index");
        await Assert.That(file.FrameCount).IsEqualTo(frames);
        await area.DisposeAsync();
    }

    [Test]
    public async Task Detach_while_Record_all_runs_stops_viewing_only_and_closing_asks_first()
    {
        await using var rig = await StartAsync();
        var area = new SessionAreaViewModel(rig.Documents) { RecordingDirectory = () => rig.Temp.File("recordings") };
        var client = await rig.Harness.ConnectClientAsync();
        var info = await rig.Harness.WaitForSessionAsync();
        var live = await AttachAsync(rig, info);
        await ConverseAsync(client, live);

        await area.RecordAllCommand.ExecuteAsync(null);
        await Assert.That(area.IsRecordingAll).IsTrue();
        await Assert.That(area.DetachDetail).IsEqualTo("Stops viewing only · Record all keeps recording until you stop it");

        await area.DetachCommand.ExecuteAsync(null);
        await Assert.That(live.State).IsEqualTo(LiveState.Ended);
        await Assert.That(area.IsRecordingAll).IsTrue();

        // Record all stops by its own toggle, and its file holds the session.
        await area.RecordAllCommand.ExecuteAsync(null);
        await Assert.That(area.IsRecordingAll).IsFalse();
        await Assert.That(area.LastRecording).IsNotNull();
        await Assert.That(File.Exists(area.LastRecording!)).IsTrue();

        // The detached tab holds unsaved data: closing it asks (OQ5), and Cancel keeps it.
        rig.Answers.Enqueue(CloseDecision.Cancel);
        await Assert.That(await rig.Documents.CloseAsync(live)).IsFalse();
        await Assert.That(rig.Prompts.Count).IsEqualTo(1);
        await Assert.That(live.Session).IsNotNull();
        await area.DisposeAsync();
    }

    // Join, SendMessage, the ReceiveMessage it broadcasts back and SendAttachment, each with its result (or one-way).
    private static bool Settled(CaptureSession session)
    {
        lock (session.Gate)
        {
            var invocations = session.Assembler.Invocations;
            if (invocations.Length < 4)
                return false;
            foreach (var invocation in invocations)
            {
                if (invocation.State == InvocationState.Pending)
                    return false;
            }

            return true;
        }
    }
}
