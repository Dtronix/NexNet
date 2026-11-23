using NexNet.Inspector.App.Controls.Hex;
using NexNet.Inspector.App.Controls.PropertyTree;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Tests.Inspector;

/// <summary>
/// Hovering any property-tree row lights its bytes in the hex view: header and transport rows (frame coordinates,
/// header bytes before the body) and decoded fields (message coordinates, through the fragments).
/// </summary>
public class TreeHexLinkTests
{
    [Test]
    public async Task Every_row_with_bytes_maps_its_hover_onto_the_same_stream_bytes()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        var (document, picks) = await ChatFixture.OpenAsync(documents);
        using InspectorViewModel inspector = new(documents);
        documents.Sync.Select(Selection.OfMessage(picks.Message, picks.Chunk));
        PropertyTreeModel tree = inspector.Tree!;
        tree.SetExpanded(inspector.Rows!.First(r => r.Name == "attachment").Node, true);
        CaptureSession session = document.Session!;
        HexLinker linker = new();
        FrameRecord chunk;
        lock (session.Gate) session.TryGetFrame(picks.Chunk, out chunk);

        int checkedRows = 0;
        List<string> wrong = [];
        foreach (PropertyRow row in inspector.Rows!.ToArray())
        {
            if (row.Bytes.IsNone) continue;
            inspector.HoveredRow = row;
            List<HexRange> ranges = [];
            lock (session.Gate) linker.AddHoverRanges(session, documents.Sync.Hover, ranges);
            checkedRows++;
            if (ranges.Count == 0 || ranges[0].Direction != row.Bytes.Direction || ranges[0].Start != row.Bytes.Stream.Offset
                || ranges[0].End != row.Bytes.Stream.End)
                wrong.Add(row.Name);
        }

        inspector.HoveredRow = inspector.Rows!.First(r => r.Name == "Type");
        List<HexRange> type = [];
        lock (session.Gate) linker.AddHoverRanges(session, documents.Sync.Hover, type);

        // A message selected from its chunk rings that chunk's type byte, as the screen shows it.
        HexHighlight highlight = new();
        lock (session.Gate) linker.Build(session, documents.Sync.Selection with { Kind = SelectionKind.Message }, FieldHover.None, null, highlight);
        await Assert.That(highlight.Selected).Contains(new HexRange(chunk.Direction, chunk.Offset, chunk.Offset + 1));

        await Assert.That(wrong).IsEmpty();
        await Assert.That(checkedRows).IsGreaterThan(8);
        await Assert.That(type.Single()).IsEqualTo(new HexRange(chunk.Direction, chunk.Offset, chunk.Offset + 1));
    }
}
