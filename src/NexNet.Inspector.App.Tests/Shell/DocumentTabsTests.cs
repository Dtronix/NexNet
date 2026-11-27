using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;

namespace NexNet.Inspector.App.Tests.Shell;

/// <summary>The documents as tabs: markers, titles and Compare's two inclusion checkboxes (OQ11).</summary>
public sealed class DocumentTabsTests
{
    private static (DocumentsViewModel Documents, TabDocument[] Tabs) Open(params (DocumentKind Kind, string Title)[] tabs)
    {
        TestUi ui = new();
        DocumentsViewModel documents = new(ui, null);
        TabDocument[] opened = [.. tabs.Select(t => new TabDocument(ui, t.Kind, t.Title))];
        foreach (TabDocument tab in opened) documents.Add(tab);
        return (documents, opened);
    }

    [Test]
    public async Task Live_tabs_get_the_live_marker_and_files_alternate_their_two_colours()
    {
        (DocumentsViewModel documents, TabDocument[] t) = Open((DocumentKind.Live, "Live · 0x3A21"), (DocumentKind.File, "chat-repro.nntl"),
            (DocumentKind.File, "server-all.nntl"), (DocumentKind.File, "third.nntl"));
        await using (documents)
        {
            using (Assert.Multiple())
            {
                await Assert.That(t.Select(d => d.Marker)).IsEquivalentTo(
                    new[] { TabMarker.Live, TabMarker.FileInbound, TabMarker.FileOutbound, TabMarker.FileInbound }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
                await Assert.That(t[0].IsLive).IsTrue();
                await Assert.That(t[2].HasOutboundMarker).IsTrue();
                await Assert.That(t[1].CloseLabel).IsEqualTo("Close chat-repro.nntl");
            }

            // The colours follow the opening order, not the tabs left: the next file takes the one after the last file opened.
            await documents.CloseAsync(t[1]);
            TabDocument next = new(new TestUi(), DocumentKind.File, "next.nntl");
            documents.Add(next);
            await Assert.That(next.Marker).IsEqualTo(TabMarker.FileOutbound);
        }
    }

    [Test]
    public async Task The_tab_titles_follow_the_design()
    {
        await Assert.That(LiveDocumentViewModel.TabTitle(0x7F3A21)).IsEqualTo("Live · 0x3A21");
        await Assert.That(LiveDocumentViewModel.SessionLabel(0x3A21)).IsEqualTo("Session 0x3A21");
        await Assert.That(LiveDocumentViewModel.SessionHex(0x0042)).IsEqualTo("0x0042");
    }

    [Test]
    public async Task Compare_takes_at_most_two_documents()
    {
        (DocumentsViewModel documents, TabDocument[] t) = Open((DocumentKind.File, "a.nntl"), (DocumentKind.File, "b.nntl"), (DocumentKind.Live, "Live · 0x0001"));
        await using (documents)
        {
            List<string?> announced = [];
            t[2].PropertyChanged += (_, e) => announced.Add(e.PropertyName);

            t[0].IsIncludedInCompare = true;
            await Assert.That(documents.CanCompare).IsFalse();
            t[1].IsIncludedInCompare = true;

            using (Assert.Multiple())
            {
                await Assert.That(documents.ComparedCount).IsEqualTo(2);
                await Assert.That(documents.CanCompare).IsTrue();
                await Assert.That(documents.Compared).IsEquivalentTo(new DocumentViewModel[] { t[0], t[1] }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
                await Assert.That(t[2].CanIncludeInCompare).IsFalse().Because("a third checkbox is disabled");
                await Assert.That(t[0].CanIncludeInCompare).IsTrue().Because("a ticked one can still be unticked");
            }

            announced.Clear();
            t[2].IsIncludedInCompare = true;
            using (Assert.Multiple())
            {
                await Assert.That(t[2].IsIncludedInCompare).IsFalse().Because("a third tick is refused");
                await Assert.That(documents.ComparedCount).IsEqualTo(2);
                await Assert.That(announced).Contains(nameof(DocumentViewModel.IsIncludedInCompare)).Because("a bound checkbox flips back");
            }

            t[0].IsIncludedInCompare = false;
            await Assert.That(t[2].CanIncludeInCompare).IsTrue();
            t[2].IsIncludedInCompare = true;
            await Assert.That(documents.Compared).IsEquivalentTo(new DocumentViewModel[] { t[1], t[2] }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task Closing_a_ticked_document_frees_its_place_in_compare()
    {
        (DocumentsViewModel documents, TabDocument[] t) = Open((DocumentKind.File, "a.nntl"), (DocumentKind.File, "b.nntl"), (DocumentKind.File, "c.nntl"));
        await using (documents)
        {
            t[0].IsIncludedInCompare = true;
            t[1].IsIncludedInCompare = true;
            await documents.CloseAsync(t[1]);

            using (Assert.Multiple())
            {
                await Assert.That(documents.ComparedCount).IsEqualTo(1);
                await Assert.That(t[1].IsIncludedInCompare).IsFalse();
                await Assert.That(t[2].CanIncludeInCompare).IsTrue();
            }

            // A closed document no longer reaches its old owner.
            t[1].IsIncludedInCompare = true;
            await Assert.That(t[1].IsIncludedInCompare).IsFalse();
            await Assert.That(documents.ComparedCount).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Closing_an_unsaved_live_tab_asks_first_and_closes_when_nothing_asks()
    {
        TestUi ui = new();
        await using DocumentsViewModel documents = new(ui, null);
        TabDocument live = new(ui, DocumentKind.Live, "Live · 0x3A21", dirty: true);
        documents.Add(live);
        CloseDecision answer = CloseDecision.Cancel;
        int asked = 0;
        documents.ClosePrompt = d =>
        {
            asked++;
            return Task.FromResult(answer);
        };

        await documents.CloseDocumentCommand.ExecuteAsync(live);
        await Assert.That(documents.Documents).Contains(live).Because("Cancel keeps it");
        await Assert.That(asked).IsEqualTo(1);

        documents.ClosePrompt = null;
        await documents.CloseDocumentCommand.ExecuteAsync(live);
        await Assert.That(documents.Documents).DoesNotContain(live).Because("with no prompt to ask, it closes");
        await Assert.That(live.Disposed).IsTrue();
    }
}
