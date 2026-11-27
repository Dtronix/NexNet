using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.Tests.Inspector;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Sample;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.App.Tests.Documents;

/// <summary>
/// Bookmarks (I44, D3): the recording key, the settings that keep bookmarks per recording across runs, their caps, the
/// jump that restores a window and scope, and re-keying when a document is saved or a section exported.
/// </summary>
public class BookmarkTests
{
    private static TimeWindow Quarter(TimeWindow w, int n) => new(w.Start + (w.Duration * n / 4), w.Start + (w.Duration * (n + 1) / 4));

    [Test]
    public async Task The_recording_key_is_stable_across_reopen_and_differs_per_session_and_per_file()
    {
        string all = Wait.Fixture("server-all.nntl");
        List<string> keys = [];
        using (RecordingFile first = RecordingFile.Open(all))
        {
            foreach (var open in first.Sessions)
            {
                RecordingKey key = RecordingKey.Of(first, open.SessionId);
                await Assert.That(key.IsPersistent).IsTrue();
                await Assert.That(key.Value).Matches("^[0-9a-f]{64}$");
                // Reopened: the same key.
                await Assert.That(RecordingKey.Of(all, open.SessionId)).IsEqualTo(key);
                keys.Add(key.Value);
            }
        }

        await Assert.That(keys.Count).IsGreaterThanOrEqualTo(3);
        await Assert.That(keys.Distinct().Count()).IsEqualTo(keys.Count).Because("every session of one file has its own key");

        // Another recording of the same conversation (client side) keys differently; a renamed copy of a file does not.
        using var temp = new TempDirectory();
        string server = Wait.Fixture("chat-server.nntl");
        string client = Wait.Fixture("chat-client.nntl");
        long serverSession;
        using (RecordingFile r = RecordingFile.Open(server)) serverSession = r.Sessions[0].SessionId;
        long clientSession;
        using (RecordingFile r = RecordingFile.Open(client)) clientSession = r.Sessions[0].SessionId;
        string renamed = temp.File("renamed elsewhere.nntl");
        File.Copy(server, renamed);
        await Assert.That(RecordingKey.Of(server, serverSession)).IsNotEqualTo(RecordingKey.Of(client, clientSession));
        await Assert.That(RecordingKey.Of(renamed, serverSession)).IsEqualTo(RecordingKey.Of(server, serverSession));
        await Assert.That(RecordingKey.Live(3, 0x1BCB).IsPersistent).IsFalse();
        await Assert.That(RecordingKey.Live(3, 0x1BCB)).IsNotEqualTo(RecordingKey.Live(4, 0x1BCB));
    }

    [Test]
    public async Task Bookmarks_persist_across_runs_with_their_scope_and_stay_with_their_recording()
    {
        using var temp = new TempDirectory();
        string settings = temp.File("settings.json");
        TimeWindow scope;
        TimeWindow first;
        TimeWindow second;
        string defaultName;
        await using (var documents = new DocumentsViewModel(new TestUi(), new SettingsStore(settings)))
        {
            var (document, picks) = await ChatFixture.OpenAsync(documents);
            documents.Sync.SetWindow(picks.Window, byUser: false);
            await Assert.That(documents.CanBookmark).IsTrue();
            scope = picks.Window;
            documents.FilterToView();
            first = Quarter(picks.Window, 1);
            documents.Sync.SetWindow(first);
            await Assert.That(documents.AddBookmark("  The attachment  ")).IsNotNull();
            documents.ClearScope();
            second = Quarter(picks.Window, 3);
            documents.Sync.SetWindow(second);
            defaultName = TimeWindowReadoutViewModel.Format(second, document.Session!.StartTicks, document.Frequency);
            await Assert.That(documents.AddBookmark()!.Value.Name).IsEqualTo(defaultName);
            await Assert.That(document.BookmarkKey.IsPersistent).IsTrue();
        }

        // A new run: the settings file is read back and the same recording shows the same bookmarks.
        var store = new SettingsStore(settings);
        await Assert.That(store.Load()).IsEqualTo(SettingsLoadOutcome.Loaded);
        await Assert.That(store.Current.Bookmarks.Count).IsEqualTo(1);
        await using (var documents = new DocumentsViewModel(new TestUi(), store))
        {
            var (document, _) = await ChatFixture.OpenAsync(documents);
            var list = documents.ActiveBookmarks;
            await Assert.That(list.Count).IsEqualTo(2);
            await Assert.That(list[0].Name).IsEqualTo("The attachment");
            await Assert.That(list[0].Window).IsEqualTo(first);
            await Assert.That(list[0].Scope?.Window).IsEqualTo(scope);
            await Assert.That(list[0].Scope?.Label).IsEqualTo(ScopeLabels.View);
            await Assert.That(list[1].Name).IsEqualTo(defaultName);
            await Assert.That(list[1].Window).IsEqualTo(second);
            await Assert.That(list[1].Scope).IsNull();
            await Assert.That(store.Current.Bookmarks[0].Key).IsEqualTo(document.BookmarkKey.Value);

            // Another recording has none of them.
            var other = (await documents.OpenRecordingAsync(Wait.Fixture("chat-client.nntl")))!;
            await Assert.That(documents.Active).IsSameReferenceAs(other);
            await Assert.That(documents.ActiveBookmarks.Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Jumping_restores_the_window_and_pushes_the_bookmarks_scope()
    {
        await using var documents = new DocumentsViewModel(new TestUi(), null);
        var (document, picks) = await ChatFixture.OpenAsync(documents);
        SyncBus sync = documents.Sync;
        TimeWindow scope = new(picks.Window.Start + (picks.Window.Duration / 4), picks.Window.End - (picks.Window.Duration / 4));
        TimeWindow inner = Quarter(scope, 2);
        TimeWindow wide = picks.Window;

        // Bookmark 1: a window inside a scope; bookmark 2: an unscoped, wider view.
        documents.PushScope(TimeScope.Of(scope, "Invocation #7"));
        sync.SetWindow(inner);
        documents.AddBookmark("scoped");
        documents.ClearScope();
        sync.SetWindow(wide);
        documents.AddBookmark("wide");
        sync.SetWindow(document.TimeRange);

        await Assert.That(documents.JumpToBookmark(0)).IsTrue();
        await Assert.That(sync.Window).IsEqualTo(inner);
        await Assert.That(document.ScopeDepth).IsEqualTo(1);
        await Assert.That(document.Scope?.Window).IsEqualTo(scope);
        await Assert.That(document.Scope?.Label).IsEqualTo("Invocation #7");
        await Assert.That(sync.Scope).IsEqualTo(scope);

        // Again: the scope is already current, so nothing nests.
        sync.SetWindow(scope);
        documents.JumpToBookmark(0);
        await Assert.That(document.ScopeDepth).IsEqualTo(1);
        await Assert.That(sync.Window).IsEqualTo(inner);

        // The wide view does not fit in the scope: the scopes clear and the window is restored.
        await Assert.That(documents.JumpToBookmark(1)).IsTrue();
        await Assert.That(document.IsScoped).IsFalse();
        await Assert.That(sync.Window).IsEqualTo(wide);

        // Inside an outer scope the bookmark's scope nests (Back returns to the outer one); outside one, it replaces them.
        documents.PushScope(TimeScope.Of(wide, ScopeLabels.View));
        documents.JumpToBookmark(0);
        await Assert.That(document.ScopeDepth).IsEqualTo(2);
        await Assert.That(sync.Window).IsEqualTo(inner);
        documents.PopScope();
        await Assert.That(document.Scope?.Window).IsEqualTo(wide);
        documents.ClearScope();
        documents.PushScope(TimeScope.Of(Quarter(wide, 0), ScopeLabels.Typed));
        documents.JumpToBookmark(0);
        await Assert.That(document.ScopeDepth).IsEqualTo(1);
        await Assert.That(document.Scope?.Window).IsEqualTo(scope);

        await Assert.That(documents.JumpToBookmark(2)).IsFalse();
        await Assert.That(documents.JumpToBookmark(-1)).IsFalse();
    }

    [Test]
    public async Task The_caps_hold_per_recording_and_in_total_least_recently_used_first()
    {
        var now = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        static SavedBookmark[] Many(int n) =>
            [.. Enumerable.Range(0, n).Select(i => new SavedBookmark("b" + i, i * 10L, (i * 10L) + 5, null, default))];

        // One recording: cut to the per-recording cap.
        var settings = new AppSettings().WithBookmarks("a", Many(AppSettings.MaxBookmarksPerRecording + 10), now);
        await Assert.That(settings.BookmarksFor("a").Count).IsEqualTo(AppSettings.MaxBookmarksPerRecording);

        // Past the total, the least recently used recordings go first (whole), never the one just changed.
        int recordings = (AppSettings.MaxBookmarks / AppSettings.MaxBookmarksPerRecording) + 2;
        settings = new AppSettings();
        for (int r = 0; r < recordings; r++) settings = settings.WithBookmarks("r" + r, Many(AppSettings.MaxBookmarksPerRecording), now.AddMinutes(r));
        await Assert.That(settings.Bookmarks.Sum(e => e.Items.Count)).IsLessThanOrEqualTo(AppSettings.MaxBookmarks);
        await Assert.That(settings.Bookmarks[0].Key).IsEqualTo("r" + (recordings - 1));
        await Assert.That(settings.BookmarksFor("r0").Count).IsEqualTo(0);
        await Assert.That(settings.BookmarksFor("r1").Count).IsEqualTo(0);
        await Assert.That(settings.BookmarksFor("r2").Count).IsEqualTo(AppSettings.MaxBookmarksPerRecording);
        // Touching an old recording brings it first again; an empty list removes it.
        settings = settings.WithBookmarks("r2", Many(3), now.AddHours(1));
        await Assert.That(settings.Bookmarks[0].Key).IsEqualTo("r2");
        settings = settings.WithBookmarks("r2", [], now.AddHours(2));
        await Assert.That(settings.BookmarksFor("r2").Count).IsEqualTo(0);

        // The store refuses the bookmark past the cap.
        var store = new BookmarkStore(null);
        var key = new RecordingKey("k", true);
        for (int i = 0; i < AppSettings.MaxBookmarksPerRecording; i++)
            await Assert.That(store.Add(key, new ViewBookmark("b", new TimeWindow(i, i + 1), null, now))).IsTrue();
        await Assert.That(store.CanAdd(key)).IsFalse();
        await Assert.That(store.Add(key, new ViewBookmark("over", new TimeWindow(1000, 1001), null, now))).IsFalse();
        await Assert.That(store.Get(key).Count).IsEqualTo(AppSettings.MaxBookmarksPerRecording);
        // Blank names and empty windows are refused; rename and delete act by index.
        var other = new RecordingKey("o", true);
        await Assert.That(store.Add(other, new ViewBookmark("   ", new TimeWindow(0, 1), null, now))).IsFalse();
        await Assert.That(store.Add(other, new ViewBookmark("x", default, null, now))).IsFalse();
        await Assert.That(store.Add(other, new ViewBookmark("x", new TimeWindow(0, 1), null, now))).IsTrue();
        await Assert.That(store.Rename(other, 0, "  ")).IsFalse();
        await Assert.That(store.Rename(other, 0, "y")).IsTrue();
        await Assert.That(store.Get(other)[0].Name).IsEqualTo("y");
        await Assert.That(store.Remove(other, 1)).IsFalse();
        await Assert.That(store.Remove(other, 0)).IsTrue();
        await Assert.That(store.Get(other).Count).IsEqualTo(0);
    }

    [Test]
    public async Task The_settings_file_round_trips_bookmarks_and_a_hand_edited_one_is_cleaned()
    {
        using var temp = new TempDirectory();
        var now = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        var store = new SettingsStore(temp.File("settings.json"));
        SavedBookmark[] items =
        [
            new("one", 100, 200, new SavedScope(50, 250, "Current view"), now),
            new("two", 300, 400, null, now.AddSeconds(1)),
        ];
        await Assert.That(store.Update(s => s.WithBookmarks("abc", items, now))).IsTrue();

        var reader = new SettingsStore(temp.File("settings.json"));
        await Assert.That(reader.Load()).IsEqualTo(SettingsLoadOutcome.Loaded);
        var read = reader.Current.BookmarksFor("abc");
        await Assert.That(read.Count).IsEqualTo(2);
        await Assert.That(read[0]).IsEqualTo(items[0]);
        await Assert.That(read[1]).IsEqualTo(items[1]);
        await Assert.That(reader.Current.Bookmarks[0].LastUsed).IsEqualTo(now);

        // Blank keys and names, empty windows and duplicate recordings dropped; a scope with no time forgotten.
        await File.WriteAllTextAsync(temp.File("settings.json"), """
            {
              "bookmarks": [
                { "key": "", "lastUsed": "2026-11-26T12:00:00+00:00", "items": [ { "name": "x", "start": 1, "end": 2 } ] },
                { "key": "k", "lastUsed": "2026-11-26T12:00:00+00:00", "items": [
                    { "name": "  ", "start": 1, "end": 2 },
                    { "name": "reversed", "start": 5, "end": 2 },
                    { "name": " kept ", "start": 1, "end": 2, "scope": { "start": 9, "end": 9, "label": "x" } } ] },
                { "key": "k", "lastUsed": "2026-11-26T12:00:00+00:00", "items": [ { "name": "dup", "start": 1, "end": 2 } ] },
                { "key": "empty", "lastUsed": "2026-11-26T12:00:00+00:00", "items": [] }
              ]
            }
            """);
        await Assert.That(reader.Load()).IsEqualTo(SettingsLoadOutcome.Loaded);
        await Assert.That(reader.Current.Bookmarks.Count).IsEqualTo(1);
        var kept = reader.Current.BookmarksFor("k");
        await Assert.That(kept.Count).IsEqualTo(1);
        await Assert.That(kept[0].Name).IsEqualTo("kept");
        await Assert.That(kept[0].Scope).IsNull();
    }

    [Test]
    public async Task Save_and_export_rekey_a_files_bookmarks_and_export_takes_only_those_in_range()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.File("settings.json"));
        await using var documents = new DocumentsViewModel(new TestUi(), store);
        var (document, picks) = await ChatFixture.OpenAsync(documents);
        documents.SavePicker = name => Task.FromResult<string?>(temp.File(name));
        TimeWindow range = picks.Window;
        TimeWindow inside = Quarter(range, 1);
        TimeWindow outside = new(document.TimeRange.Start, document.TimeRange.Start + (range.Duration / 4));
        documents.PushScope(TimeScope.Of(new TimeWindow(range.Start - range.Duration, range.End), ScopeLabels.Typed));
        documents.Sync.SetWindow(inside);
        documents.AddBookmark("inside");
        documents.ClearScope();
        documents.Sync.SetWindow(outside);
        documents.AddBookmark("outside");

        // A whole copy keys like the recording it copies: its bookmarks are already there.
        string copy = temp.File("copy.nntl");
        await documents.SaveToAsync(document, copy);
        await Assert.That(RecordingKey.Of(copy, document.Session!.SessionId)).IsEqualTo(document.BookmarkKey);

        // A section export keys differently and takes only the bookmarks inside its range, their scope cut to it.
        documents.Sync.SetWindow(range, byUser: false);
        var dialog = new ExportSectionDialogViewModel(documents, document);
        await Assert.That(dialog.HasBookmarks).IsTrue();
        await Assert.That(dialog.IncludeBookmarks).IsTrue();
        await Assert.That(dialog.BookmarksText).IsEqualTo("Include bookmarks in range · 1 of 2");
        await dialog.ExportCommand.ExecuteAsync(null);
        string exported = (await dialog.Result)!;
        RecordingKey key = RecordingKey.Of(exported, document.Session!.SessionId);
        await Assert.That(key).IsNotEqualTo(document.BookmarkKey);
        var copied = documents.Bookmarks.Get(key);
        await Assert.That(copied.Count).IsEqualTo(1);
        await Assert.That(copied[0].Name).IsEqualTo("inside");
        await Assert.That(copied[0].Window).IsEqualTo(inside);
        await Assert.That(copied[0].Scope?.Window).IsEqualTo(range);
        await Assert.That(store.Current.BookmarksFor(key.Value).Count).IsEqualTo(1);

        // Opened, the exported file lists them; unticked, an export takes none.
        await documents.OpenRecordingAsync(exported);
        await Assert.That(documents.ActiveBookmarks.Count).IsEqualTo(1);
        documents.Activate(document);
        documents.Sync.SetWindow(Quarter(range, 0), byUser: false);
        var plain = new ExportSectionDialogViewModel(documents, document) { IncludeBookmarks = false };
        await plain.ExportCommand.ExecuteAsync(null);
        await Assert.That(documents.Bookmarks.Get(RecordingKey.Of((await plain.Result)!, document.Session!.SessionId)).Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_live_documents_bookmarks_are_in_memory_until_saved_then_rekeyed_to_the_file_and_its_tab()
    {
        await using var harness = await ChatServerHarness.StartAsync(allowRecording: true);
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.File("settings.json"));
        await using var documents = new DocumentsViewModel(new TestUi(), store);
        var client = await harness.ConnectClientAsync();
        var info = await harness.WaitForSessionAsync();
        EndpointAddress.TryParse(harness.EndpointAddress, out var address, out _);
        var connection = await InspectorConnection.ConnectAsync(address, "app-test-endpoint-token"u8.ToArray(), EndpointConnectOptions.Default);
        var live = await documents.AttachAsync(connection, info, InspectorOverflow.Block, snapshot: true);
        await client.Proxy.Join("ada");
        await client.Proxy.SendMessage(new ChatMessage { Room = "general", Author = "ada", Text = "hello" }, CancellationToken.None);
        await client.Proxy.SendAttachment(new Attachment { Room = "general", FileName = "a.bin", Data = new byte[18_000] });
        await Wait.UntilAsync(() => live.FrameCount >= 4, "frames to arrive");
        await Wait.UntilAsync(() => !documents.Sync.Window.IsEmpty, "the live window");

        await Assert.That(live.BookmarkKey.IsPersistent).IsFalse();
        var mark = documents.AddBookmark("live view");
        await Assert.That(mark).IsNotNull();
        await Assert.That(store.Current.Bookmarks.Count).IsEqualTo(0).Because("a live key stays in memory");

        string path = temp.File("live.nntl");
        await documents.SaveToAsync(live, path);
        RecordingKey saved = RecordingKey.Of(path, live.Session!.SessionId);
        var persisted = store.Current.BookmarksFor(saved.Value);
        await Assert.That(persisted.Count).IsEqualTo(1);
        await Assert.That(persisted[0].Name).IsEqualTo("live view");
        await Assert.That(persisted[0].Start).IsEqualTo(mark!.Value.Window.Start);
        await Assert.That(documents.ActiveBookmarks.Count).IsEqualTo(1).Because("the live tab keeps its own");

        // The tab becomes the file: same bookmarks under the file's key; the live key is forgotten.
        RecordingKey liveKey = live.BookmarkKey;
        await client.DisconnectAsync();
        var file = await documents.ReplaceWithRecordingAsync(live, path);
        await Assert.That(file.BookmarkKey).IsEqualTo(saved);
        await Assert.That(documents.ActiveBookmarks.Count).IsEqualTo(1);
        await Assert.That(documents.ActiveBookmarks[0].Window).IsEqualTo(mark.Value.Window);
        await Assert.That(documents.Bookmarks.Get(liveKey).Count).IsEqualTo(0);
    }
}
