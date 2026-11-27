using System.Collections.Specialized;
using NexNet.Inspector.App.Controls.PropertyTree;
using NexNet.Inspector.App.Tests.Capture;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Tests.Inspector;

/// <summary>The property tree's flat model: subtrees, flattening, expand / collapse as one range change, reveal, lazy rows.</summary>
public class PropertyTreeModelTests
{
    private static string Names(PropertyTreeModel tree) => string.Join("|", tree.Rows.Select(r => r.Name));

    [Test]
    public async Task Sections_and_fields_flatten_in_order_with_their_depths()
    {
        PropertyTreeModel tree = InspectGallery.SampleTree();

        await Assert.That(Names(tree)).IsEqualTo("Frame header|Type|Body length|Invocation|Transport|#4471|Invocation #812|NexNet split|Method|authenticationToken");
        await Assert.That(tree.Rows[0].IsSection).IsTrue();
        await Assert.That(tree.Rows[0].Glyph).IsEqualTo("▾");
        await Assert.That(tree.Rows[1].Indent).IsEqualTo(0);
        await Assert.That(tree.Node(0).SubtreeEnd).IsEqualTo(4);
        await Assert.That(tree.Rows[5].IsTransport).IsTrue();
        await Assert.That(tree.Rows[7].IsNexNet).IsTrue();
    }

    [Test]
    public async Task Collapsing_and_expanding_a_section_changes_the_rows_by_one_range()
    {
        PropertyTreeModel tree = InspectGallery.SampleTree();
        List<NotifyCollectionChangedEventArgs> changes = [];
        tree.Rows.CollectionChanged += (_, e) => changes.Add(e);
        PropertyRow transport = tree.Rows[4];

        tree.Toggle(transport.Node);

        await Assert.That(tree.Rows.Count).IsEqualTo(9);
        await Assert.That(transport.IsExpanded).IsFalse();
        await Assert.That(transport.Glyph).IsEqualTo("▸");
        await Assert.That(changes.Count).IsEqualTo(1);
        await Assert.That(changes[0].Action).IsEqualTo(NotifyCollectionChangedAction.Remove);
        await Assert.That(changes[0].OldStartingIndex).IsEqualTo(5);
        await Assert.That(changes[0].OldItems!.Count).IsEqualTo(1);
        await Assert.That(tree.Rows[5].Name).IsEqualTo("Invocation #812");

        tree.Toggle(transport.Node);

        await Assert.That(tree.Rows.Count).IsEqualTo(10);
        await Assert.That(changes[1].Action).IsEqualTo(NotifyCollectionChangedAction.Add);
        await Assert.That(changes[1].NewStartingIndex).IsEqualTo(5);
        await Assert.That(tree.Rows[5].Name).IsEqualTo("#4471");
        // The same row object comes back: containers can keep it.
        await Assert.That(tree.Rows[5]).IsSameReferenceAs((PropertyRow)changes[0].OldItems![0]!);
    }

    [Test]
    public async Task Nested_fields_open_on_demand_and_a_hidden_subtree_keeps_its_state()
    {
        PropertyTreeModel tree = new();
        tree.Section("S", null);
        int parent = tree.Field("parent", "obj");
        tree.Field("child", "1", depth: 1);
        int inner = tree.Field("inner", "obj", depth: 1);
        tree.Field("leaf", "2", depth: 2);
        tree.Field("sibling", "3");
        tree.Complete();

        // Fields start collapsed: only the section, parent and sibling show.
        await Assert.That(Names(tree)).IsEqualTo("S|parent|sibling");
        await Assert.That(tree.RowOf(parent).Glyph).IsEqualTo("▸");

        tree.SetExpanded(inner, true); // hidden: applies when its parent opens
        await Assert.That(tree.Rows.Count).IsEqualTo(3);
        tree.SetExpanded(parent, true);
        await Assert.That(Names(tree)).IsEqualTo("S|parent|child|inner|leaf|sibling");
        await Assert.That(tree.RowOf(inner).Indent).IsEqualTo(PropertyTreeModel.IndentStep);
        await Assert.That(tree.RowOf(inner + 1).Indent).IsEqualTo(2 * PropertyTreeModel.IndentStep);

        tree.SetExpanded(parent, false);
        await Assert.That(tree.Rows.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Reveal_finds_the_deepest_field_holding_a_stream_byte_and_opens_its_ancestors()
    {
        PropertyTreeModel tree = new();
        PayloadMap map = PayloadMap.ForFrame(TapDirection.Inbound, 7, 1000);
        tree.Section("S", null);
        int outer = tree.Field("outer", "obj", bytes: map.Map(0, 10));
        int inner = tree.Field("inner", "x", bytes: map.Map(4, 2), depth: 1);
        tree.Field("other direction", "y", bytes: new PropertyBytes(map.Message, 4, 2, TapDirection.Outbound, new(1004, 2)));
        tree.Complete();

        await Assert.That(tree.Reveal(TapDirection.Inbound, 1005)).IsEqualTo(inner);
        await Assert.That(tree.PositionOf(inner)).IsGreaterThan(0);
        await Assert.That(tree.Reveal(TapDirection.Inbound, 1001)).IsEqualTo(outer);
        await Assert.That(tree.Reveal(TapDirection.Inbound, 1010)).IsEqualTo(-1);
    }

    [Test]
    public async Task Rows_are_made_only_when_read()
    {
        PropertyTreeModel tree = new();
        tree.Section("S", null);
        for (int i = 0; i < 10_000; i++) tree.Field("f" + i, "v");
        tree.Complete();

        await Assert.That(tree.Rows.Count).IsEqualTo(10_001);
        await Assert.That(tree.RowsMade).IsEqualTo(0);
        _ = tree.Rows[5_000].Value;
        await Assert.That(tree.RowsMade).IsEqualTo(1);
    }

    [Test]
    public async Task A_frame_map_puts_header_bytes_before_the_body_and_a_fragment_map_clips_to_one_fragment()
    {
        PropertyBytes header = PayloadMap.ForFrame(TapDirection.Inbound, 3, 100).Map(-6, 1);
        await Assert.That(header.Stream).IsEqualTo(new Core.Assembly.StreamRange(94, 1));
        await Assert.That(header.Message.Kind).IsEqualTo(Core.Assembly.MessageRefKind.Frame);

        Core.Assembly.MessageRef message = new(Core.Assembly.MessageRefKind.Assembled, TapDirection.Inbound, 10, 0);
        PayloadMap fragments = PayloadMap.ForFragments(message,
        [
            new(TapDirection.Inbound, 10, 0, 500, 0, 1, 1),
            new(TapDirection.Inbound, 11, 0, 600, 100, 2, 2),
            new(TapDirection.Inbound, 13, 100, 900, 100, 4, 4),
        ]);
        PropertyBytes spanning = fragments.Map(90, 20);
        await Assert.That(spanning.Offset).IsEqualTo(90);
        await Assert.That(spanning.Length).IsEqualTo(20);
        await Assert.That(spanning.Stream).IsEqualTo(new Core.Assembly.StreamRange(690, 10));
        await Assert.That(fragments.Map(150, 4).Stream).IsEqualTo(new Core.Assembly.StreamRange(950, 4));
    }
}
