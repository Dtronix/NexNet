using NexNet.Inspector.App.Controls.Compare;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Compare;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Tests.Compare;

/// <summary>
/// The Compare workspace (SYSTEM.md flow 5): two ticked documents make the compare when the workspace shows; Align
/// re-offsets file B; picking an entity pins it and fills the detail; the sidebar lists the union with Δ markers and
/// filters by search; a double-click opens the place in Inspect on that document; "Compare two sessions…" and "Open in
/// Compare" lead here.
/// </summary>
public sealed class CompareWorkspaceTests
{
    [Test]
    public async Task Two_ticked_documents_make_the_compare_once_the_workspace_shows()
    {
        await using CompareFixture f = await CompareFixture.OpenAsync(show: false);
        await Assert.That(f.Compare.IsShown).IsFalse();
        await Assert.That(f.Compare.EmptyText).StartsWith("Tick the checkboxes");

        f.A.IsIncludedInCompare = true;
        await Assert.That(f.Compare.EmptyText).StartsWith("Tick one more");
        f.B.IsIncludedInCompare = true;
        await Assert.That(f.Compare.Session).IsNull().Because("built when Compare shows, not before");

        f.Chrome.Workspace.Value = Workspace.Compare;
        await f.Compare.WhenComputedAsync();
        CompareSession session = f.Compare.Session!;
        using (Assert.Multiple())
        {
            await Assert.That(f.Compare.IsShown).IsTrue();
            await Assert.That(f.Compare.HasSession).IsTrue();
            await Assert.That(session.A).IsSameReferenceAs(f.A.Session);
            await Assert.That(session.B).IsSameReferenceAs(f.B.Session);
            await Assert.That(session.Alignment).IsEqualTo(Alignment.OnEvent);
            await Assert.That(f.Compare.DeltaCount).IsEqualTo(session.Deltas.Count).And.IsGreaterThan(0);
            await Assert.That(f.Compare.Rows.Session).IsSameReferenceAs(session);
            await Assert.That(f.Compare.Rows.DetailA).StartsWith("client side · recorded ").And.EndsWith("offset 0");
            await Assert.That(f.Compare.Rows.DetailB).StartsWith("server side · recorded ");
        }

        // Unticking one drops the compare; the same pair again is a new one.
        f.B.IsIncludedInCompare = false;
        await Assert.That(f.Compare.Session).IsNull();
        f.B.IsIncludedInCompare = true;
        await f.Compare.WhenComputedAsync();
        await Assert.That(f.Compare.Session).IsNotNull().And.IsNotSameReferenceAs(session);
    }

    [Test]
    public async Task Align_re_offsets_file_b_and_keeps_the_selection()
    {
        await using CompareFixture f = await CompareFixture.OpenAsync();
        CompareSession session = f.Compare.Session!;
        int attachment = Capture.CompareScreen.FindInvocation(session, "SendAttachment");
        f.Compare.Select(attachment);
        string onEvent = f.Compare.Rows.DetailB;
        int changes = 0;
        f.Compare.Rows.Changed += c => changes += (c & CompareChanges.Alignment) != 0 ? 1 : 0;

        f.Compare.Align.Value = Alignment.Relative;
        await f.Compare.WhenComputedAsync();
        using (Assert.Multiple())
        {
            await Assert.That(session.Alignment).IsEqualTo(Alignment.Relative);
            await Assert.That(f.Compare.Rows.DetailB).EndsWith("offset 0");
            await Assert.That(onEvent).IsNotEqualTo(f.Compare.Rows.DetailB);
            await Assert.That(changes).IsEqualTo(1);
            await Assert.That(f.Compare.Rows.Selected).IsEqualTo(attachment);
            await Assert.That(f.Compare.DeltaCount).IsEqualTo(session.Deltas.Count);
        }

        f.Compare.Align.Value = Alignment.WallClock;
        await f.Compare.WhenComputedAsync();
        await Assert.That(session.Clock.Mode).IsEqualTo(Alignment.WallClock);
    }

    [Test]
    public async Task The_sidebar_lists_the_union_pins_the_selection_and_searches()
    {
        await using CompareFixture f = await CompareFixture.OpenAsync();
        CompareSession session = f.Compare.Session!;
        CompareEntityItem[] items = f.Compare.Entities.ToArray();
        using (Assert.Multiple())
        {
            await Assert.That(items[0].Kind).IsEqualTo(CompareEntityItemKind.Category);
            await Assert.That(items[0].Label).IsEqualTo("Invocations");
            await Assert.That(items.Count(i => i.IsCategory)).IsEqualTo(5);
            await Assert.That(items.Where(i => i.IsGroup).All(i => i.HasDelta == session.Groups[i.Group].Differing > 0)).IsTrue();
            await Assert.That(items.Any(i => i.IsGroup && i.Label.StartsWith("SendMessage ×", StringComparison.Ordinal))).IsTrue();
        }

        // Picking a group pins its first differing entity, first under its category, selected.
        CompareEntityItem sends = items.First(i => i.IsGroup && i.Label.StartsWith("SendMessage", StringComparison.Ordinal));
        f.Compare.SelectedEntity = sends;
        CompareEntityItem pinned = f.Compare.Entities.Single(i => i.IsPinned);
        using (Assert.Multiple())
        {
            await Assert.That(f.Compare.SelectedEntity).IsSameReferenceAs(pinned);
            await Assert.That(f.Compare.Entities[1]).IsSameReferenceAs(pinned);
            await Assert.That(pinned.Match).IsEqualTo(CompareRowsControl.FirstDiffering(session, session.Groups[sends.Group]));
            await Assert.That(pinned.Count).EndsWith(" fr");
            await Assert.That(f.Compare.Detail?.Index).IsEqualTo(pinned.Match);
        }

        // A category click folds it.
        f.Compare.SelectedEntity = f.Compare.Entities.First(i => i.IsCategory && i.Category == CompareCategory.Pipes);
        await Assert.That(f.Compare.Entities.Any(i => i.IsGroup && i.Category == CompareCategory.Pipes)).IsFalse();

        f.Compare.SearchText = "upload";
        CompareEntityItem[] found = f.Compare.Entities.ToArray();
        using (Assert.Multiple())
        {
            await Assert.That(found.Where(i => i.IsGroup).All(i => i.Label.Contains("upload", StringComparison.OrdinalIgnoreCase))).IsTrue();
            await Assert.That(found.Count(i => i.IsGroup)).IsGreaterThanOrEqualTo(1);
            await Assert.That(found.Any(i => i.IsCategory && i.Category == CompareCategory.Pipes && i.IsExpanded)).IsTrue();
        }

        // Unticking a group takes its rows out of the aligned view.
        CompareEntityItem upload = found.First(i => i.IsGroup);
        upload.IsShown = false;
        await Assert.That(f.Compare.Rows.IsGroupShown(upload.Group)).IsFalse();
    }

    [Test]
    public async Task A_double_click_opens_the_entity_in_Inspect_on_its_document()
    {
        await using CompareFixture f = await CompareFixture.OpenAsync();
        CompareSession session = f.Compare.Session!;
        int attachment = Capture.CompareScreen.FindInvocation(session, "SendAttachment");
        EntityMatch match = session.Matches[attachment];

        f.Compare.Rows.RequestOpen(CompareSide.B, attachment, session.GetStart(match, CompareSide.B));
        using (Assert.Multiple())
        {
            await Assert.That(f.Chrome.Workspace.Value).IsEqualTo(Workspace.Inspect);
            await Assert.That(f.Documents.Active).IsSameReferenceAs(f.B);
            await Assert.That(f.Documents.Sync.Selection).IsEqualTo(Selection.OfEntity(EntityKind.Invocation, match.B.Index));
            await Assert.That(f.Documents.Sync.Window.Contains(match.B.StartTicks)).IsTrue();
            await Assert.That(f.Documents.Sync.Window.Contains(match.B.EndTicks)).IsTrue();
        }

        // A time with no entity moves the playhead there, on A's clock.
        f.Chrome.Workspace.Value = Workspace.Compare;
        long time = session.Clock.AnchorTime;
        f.Compare.Rows.RequestOpen(CompareSide.A, -1, time);
        using (Assert.Multiple())
        {
            await Assert.That(f.Documents.Active).IsSameReferenceAs(f.A);
            await Assert.That(f.Documents.Sync.Playhead).IsEqualTo(session.Clock.ToTicks(CompareSide.A, time));
        }
    }

    [Test]
    public async Task Compare_two_sessions_opens_both_sessions_of_the_recording_from_its_path()
    {
        // Posted UI work runs here, on the test's thread, as it would on the UI thread: indexing threads only queue it.
        QueuedUi ui = new();
        await using DocumentsViewModel documents = new(ui, null);
        ShellChromeViewModel chrome = new(documents);
        using CompareWorkspaceViewModel compare = new(documents, chrome);
        FileDocumentViewModel other = (await documents.OpenRecordingAsync(Wait.Fixture("chat-client.nntl")))!;
        await other.Indexing;
        ui.RunAll();
        other.IsIncludedInCompare = true;

        using RecordingFile recording = RecordingFile.Open(Wait.Fixture("server-all.nntl"));
        bool took = await compare.CompareRecordingAsync(recording);
        foreach (DocumentViewModel opened in documents.Documents) await ((FileDocumentViewModel)opened).Indexing;
        // The compare builds on the thread pool and lands through the (queued) UI, then refreshes once both settled.
        await Wait.UntilAsync(() =>
        {
            ui.RunAll();
            return compare.Session is { IsStale: false } && !compare.IsComputing;
        }, "the compare of both sessions");
        DocumentViewModel[] ticked = documents.Compared.ToArray();
        using (Assert.Multiple())
        {
            await Assert.That(took).IsTrue();
            await Assert.That(documents.Documents.Count).IsEqualTo(3);
            await Assert.That(other.IsIncludedInCompare).IsFalse();
            await Assert.That(ticked.Length).IsEqualTo(2);
            await Assert.That(ticked[0].Session!.SessionId).IsEqualTo(recording.Sessions[0].SessionId);
            await Assert.That(ticked[1].Session!.SessionId).IsEqualTo(recording.Sessions[1].SessionId);
            await Assert.That(chrome.Workspace.Value).IsEqualTo(Workspace.Compare);
            await Assert.That(compare.Session).IsNotNull();
            await Assert.That(compare.Session!.IsStale).IsFalse().Because("refreshed once both settled");
        }

        using RecordingFile single = RecordingFile.Open(Wait.Fixture("chat-client.nntl"));
        await Assert.That(await compare.CompareRecordingAsync(single)).IsFalse();
    }

    [Test]
    public async Task The_title_bars_range_follows_the_compare_window_while_Compare_shows()
    {
        await using CompareFixture f = await CompareFixture.OpenAsync();
        TimeWindowReadoutViewModel readout = f.Chrome.TimeWindow;
        CompareRowsModel rows = f.Compare.Rows;
        await Assert.That(readout.Text).IsEqualTo(TimeWindowReadoutViewModel.Format(rows.Window, 0, CompareClock.Frequency));
        await Assert.That(readout.Text).StartsWith("0.0 – ");

        rows.SetWindow(TimeWindow.FromDuration(rows.Window.Start + (rows.Window.Duration / 4), rows.Window.Duration / 2));
        await Assert.That(readout.Text).IsEqualTo(TimeWindowReadoutViewModel.Format(rows.Window, 0, CompareClock.Frequency));

        // − and + zoom the compare's window, not the documents' shared one.
        TimeWindow shared = f.Documents.Sync.Window;
        long duration = rows.Window.Duration;
        readout.ZoomInCommand.Execute(null);
        await Assert.That(rows.Window.Duration).IsEqualTo(duration / 2);
        await Assert.That(f.Documents.Sync.Window).IsEqualTo(shared);

        // Back in Inspect it reads the active document's window again.
        f.Chrome.Workspace.Value = Workspace.Inspect;
        string inspect = f.Documents.Sync.Window.IsEmpty || f.Documents.Active is not { Session: { } session } active
            ? ""
            : TimeWindowReadoutViewModel.Format(f.Documents.Sync.Window, session.StartTicks, active.Frequency);
        await Assert.That(readout.Text).IsEqualTo(inspect);
    }

    [Test]
    public async Task Open_in_Compare_selects_the_entity_selected_in_Inspect()
    {
        await using CompareFixture f = await CompareFixture.OpenAsync(show: false);
        f.Documents.Activate(f.A);
        int invocation = f.A.Session!.Assembler.Invocations.Length - 1;
        f.Documents.Sync.Select(Selection.OfEntity(EntityKind.Invocation, invocation));

        f.Compare.Show();
        await f.Compare.WhenComputedAsync();
        CompareSession session = f.Compare.Session!;
        int expected = -1;
        for (int i = 0; i < session.Matches.Length; i++)
        {
            if (session.Matches[i].Key.Kind == CompareEntityKind.Invocation && session.Matches[i].A.Index == invocation) expected = i;
        }

        using (Assert.Multiple())
        {
            await Assert.That(expected).IsGreaterThanOrEqualTo(0);
            await Assert.That(f.Compare.Rows.Selected).IsEqualTo(expected);
            await Assert.That(f.Compare.Detail?.Index).IsEqualTo(expected);
        }
    }
}
