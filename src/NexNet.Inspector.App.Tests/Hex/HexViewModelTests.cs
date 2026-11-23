using NexNet.Inspector.App.Controls.Hex;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Tests.Hex;

/// <summary>The Hex pane's view-model: it follows the active document, and its IN/OUT toggles filter but never both turn off.</summary>
public class HexViewModelTests
{
    [Test]
    public async Task The_direction_toggles_filter_but_never_both_turn_off()
    {
        using HexViewModel hex = new(new SyncBus(new TestUi()));
        await Assert.That(hex.Directions).IsEqualTo(HexDirections.Both);
        hex.ShowsInbound = false;
        await Assert.That(hex.Directions).IsEqualTo(HexDirections.Outbound);
        hex.ShowsOutbound = false;
        await Assert.That((hex.ShowsInbound, hex.ShowsOutbound)).IsEqualTo((true, false));
        await Assert.That(hex.Directions).IsEqualTo(HexDirections.Inbound);
        await Assert.That(hex.RowLabel).IsEqualTo("16 B/row");
        await Assert.That(hex.IsEmpty).IsTrue();
    }

    [Test]
    public async Task The_view_model_follows_the_active_document()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        using HexViewModel hex = new(documents);
        await Assert.That(hex.IsEmpty).IsTrue();

        FileDocumentViewModel document = (await documents.OpenRecordingAsync(Wait.Fixture("chat-client.nntl")))!;
        await document.Indexing;
        await Wait.UntilAsync(() => document.IndexingState == IndexingState.Complete, "the index to complete");
        hex.Refresh();

        await Assert.That(hex.Document).IsSameReferenceAs(document);
        await Assert.That(hex.Layout.Session).IsSameReferenceAs(document.Session);
        await Assert.That(hex.IsEmpty).IsFalse();
        long both = hex.Layout.LineCount;
        int ops = hex.Layout.OpCount;
        await Assert.That(ops).IsEqualTo((int)document.OpCount);

        hex.ShowsOutbound = false;
        await Assert.That(hex.Layout.Directions).IsEqualTo(HexDirections.Inbound);
        await Assert.That(hex.Layout.LineCount).IsLessThan(both);
        await Assert.That(hex.Layout.OpCount).IsEqualTo(document.Session!.Inbound.Ops.Length);

        await documents.CloseAsync(document);
        await Assert.That(hex.Document).IsNull();
        await Assert.That(hex.IsEmpty).IsTrue();
        await Assert.That(hex.Layout.Session).IsNull();
    }

    [Test]
    public async Task Bookmarks_become_gutter_marks_a_binding_sees_change()
    {
        using HexViewModel hex = new(new SyncBus(new TestUi()));
        List<string?> changed = [];
        hex.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        HexPosition position = new(NexNet.Inspector.Core.Model.TapDirection.Outbound, 0x40);

        await Assert.That(hex.ToggleBookmark(position)).IsTrue();
        IReadOnlyList<HexRange> marks = hex.BookmarkMarks;
        await Assert.That(marks.ToArray()).IsEquivalentTo(new[] { new HexRange(NexNet.Inspector.Core.Model.TapDirection.Outbound, 0x40, 0x41) },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(changed).Contains(nameof(HexViewModel.BookmarkMarks));

        await Assert.That(hex.ToggleBookmark(position)).IsFalse();
        await Assert.That(hex.BookmarkMarks.Count).IsEqualTo(0);
        await Assert.That(hex.BookmarkMarks).IsNotSameReferenceAs(marks);
    }
}
