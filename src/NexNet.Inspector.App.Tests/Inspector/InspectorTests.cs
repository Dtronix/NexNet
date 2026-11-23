using NexNet.Inspector.App.Controls;
using NexNet.Inspector.App.Controls.PropertyTree;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Tests.Inspector;

/// <summary>
/// The inspector over the Chat fixture: the decoded tree of SendAttachment's chunk 2 and its assembly bar, the raw
/// fallback without a schema (OQ3), the handshake tab, and the links with the sync bus (selection picks the frame, a
/// byte range picks the field, hover publishes the field, a click selects its bytes).
/// </summary>
public class InspectorTests
{
    private static PropertyRow Row(InspectorViewModel inspector, string name) => inspector.Rows!.First(r => r.Name == name);

    [Test]
    public async Task A_chunk_decodes_its_header_transport_and_invocation_with_a_five_chunk_assembly_bar()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        var (document, picks) = await ChatFixture.OpenAsync(documents);
        CaptureSession session = document.Session!;

        InspectorContent content;
        lock (session.Gate) content = InspectorProjection.Build(session, picks.Chunk)!;

        PropertyRow[] sections = [.. content.Tree.Rows.Where(r => r.IsSection)];
        await Assert.That(string.Join("|", sections.Select(s => s.Name))).IsEqualTo("Frame header|Transport|Invocation #4");
        await Assert.That(sections[0].Value).StartsWith("inbound · ");
        await Assert.That(sections[2].Value).IsEqualTo("reassembled");
        await Assert.That(content.Tree.Rows.First(r => r.Name == "Type").Value).IsEqualTo("0x72 · MessageChunk");
        await Assert.That(content.Tree.Rows.First(r => r.Name == "Body length").Value).IsEqualTo("65,535 · LE FF FF");
        await Assert.That(content.Tree.Rows.First(r => r.Name == "Kind").Value).IsEqualTo("0 · Argument");
        await Assert.That(content.Tree.Rows.First(r => r.Name == "NexNet split").Value).IsEqualTo("Start + 5 chunks");
        await Assert.That(content.Tree.Rows.First(r => r.Name == "Method").Value).IsEqualTo("IChatServer.SendAttachment");
        await Assert.That(content.Tree.Rows.Count(r => r.IsTransport)).IsGreaterThan(1);
        await Assert.That(content.IsHandshake).IsFalse();
        await Assert.That(content.IsFinal).IsTrue();
        await Assert.That(content.FrameLabel).IsEqualTo("#" + picks.Chunk.Seq.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));

        AssemblyInfo assembly = content.Assembly!;
        await Assert.That(assembly.Title).IsEqualTo("Invocation #4");
        await Assert.That(assembly.Segments.Length).IsEqualTo(6);
        await Assert.That(assembly.Segments[0].Kind).IsEqualTo(AssemblySegmentKind.Start);
        await Assert.That(assembly.Segments.Count(s => s.Kind == AssemblySegmentKind.Chunk)).IsEqualTo(5);
        await Assert.That(assembly.Segments.Single(s => s.IsCurrent).Number).IsEqualTo(2);
        await Assert.That(assembly.Segments.Sum(s => s.InterleavedBefore)).IsGreaterThan(0);
        await Assert.That(assembly.Summary).Contains("5 chunks");
        await Assert.That(assembly.Note).StartsWith("Chunk 2 arrived in reads #");
        await Assert.That(assembly.Note).Contains("frames interleaved");
    }

    [Test]
    public async Task Decoded_values_render_only_when_their_row_is_read()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        var (document, picks) = await ChatFixture.OpenAsync(documents);
        CaptureSession session = document.Session!;

        InspectorContent content;
        lock (session.Gate) content = InspectorProjection.Build(session, picks.Chunk)!;
        PropertyTreeModel tree = content.Tree;

        await Assert.That(tree.FormattedCount).IsEqualTo(0);
        PropertyRow attachment = tree.Rows.First(r => r.Name == "attachment");
        await Assert.That(attachment.HasChildren).IsTrue();
        // The single object argument shows its members (inspect-live: roomId, fileName, data).
        await Assert.That(attachment.IsExpanded).IsTrue();
        await Assert.That(attachment.Value).IsEqualTo("Attachment");
        await Assert.That(tree.FormattedCount).IsEqualTo(1);

        int before = tree.Rows.Count;
        tree.Toggle(attachment.Node);
        await Assert.That(tree.Rows.Count).IsLessThan(before);
        tree.Toggle(attachment.Node);
        await Assert.That(tree.Rows.Count).IsEqualTo(before);
        // Showing the children made rows for them but rendered none of their values.
        await Assert.That(tree.FormattedCount).IsEqualTo(1);
        PropertyRow fileName = tree.Rows.First(r => r.Name == "FileName" || r.Name == "fileName");
        await Assert.That(fileName.Value).StartsWith("\"");
        await Assert.That(fileName.Indent).IsEqualTo(PropertyTreeModel.IndentStep);
        // Every decoded field links to its bytes in the inbound stream.
        await Assert.That(fileName.Bytes.Direction).IsEqualTo(TapDirection.Inbound);
        await Assert.That(fileName.Bytes.Stream.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Without_a_schema_the_arguments_decode_raw()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        var (schemaDocument, _) = await ChatFixture.OpenAsync(documents);
        long sessionId = schemaDocument.Session!.SessionId;
        FileDocumentViewModel raw = await OpenRawAsync(documents, sessionId);
        CaptureSession session = raw.Session!;
        await Assert.That(raw.SchemaSource).IsEqualTo(SchemaSource.None);

        InspectorContent content;
        lock (session.Gate)
        {
            // The first invocation of the session: decoded raw, method unknown.
            InvocationRow join = session.Assembler.Invocations[1];
            content = InspectorProjection.Build(session, new FrameKey(join.Request.Direction, join.Request.Seq))!;
        }

        PropertyRow arguments = content.Tree.Rows.First(r => r.Name == "Arguments · raw");
        await Assert.That(arguments.IsMuted).IsTrue();
        await Assert.That(arguments.Value).Contains(" B · ");
        await Assert.That(content.Tree.Rows.First(r => r.Name == "Method").Value).Contains("no schema");
    }

    private static async Task<FileDocumentViewModel> OpenRawAsync(DocumentsViewModel documents, long sessionId)
    {
        FileDocumentViewModel document = (await documents.OpenRecordingAsync(Wait.Fixture("chat-server.nntl"), new RecordingOpenChoice(sessionId, SchemaChoice.Raw)))!;
        await document.Indexing;
        await Wait.UntilAsync(() => document.IndexingState == IndexingState.Complete, "the index to complete");
        return document;
    }

    [Test]
    public async Task The_inspector_follows_the_selection_and_shows_the_hint_when_nothing_is_selected()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        var (_, picks) = await ChatFixture.OpenAsync(documents);
        using InspectorViewModel inspector = new(documents);

        await Assert.That(inspector.HasContent).IsFalse();
        await Assert.That(inspector.ShowsHint).IsTrue();

        documents.Sync.Select(Selection.OfMessage(picks.Message, picks.Chunk));
        await Assert.That(inspector.Content!.Frame).IsEqualTo(picks.Chunk);
        await Assert.That(inspector.ShowsTree).IsTrue();
        await Assert.That(inspector.ShowsAssembly).IsTrue();
        await Assert.That(inspector.TabLabels[1]).IsEqualTo("Message trace");

        // An entity selection shows the entity's first frame: the request of the invocation.
        documents.Sync.Select(Selection.OfEntity(EntityKind.Invocation, 7));
        await Assert.That(inspector.Content!.Frame).IsEqualTo(new FrameKey(picks.Message.Direction, picks.Message.Seq));

        inspector.TabIndex = 1;
        await Assert.That(inspector.Tab).IsEqualTo(InspectorTab.Trace);
        await Assert.That(inspector.ShowsTree).IsFalse();
        // The assembly footer stays under the trace too.
        await Assert.That(inspector.ShowsAssembly).IsTrue();
        await Assert.That(inspector.ShowsTraceList).IsTrue();

        documents.Sync.Select(Selection.None);
        await Assert.That(inspector.HasContent).IsFalse();
    }

    [Test]
    public async Task A_greeting_names_the_second_tab_Handshake()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        await ChatFixture.OpenAsync(documents);
        using InspectorViewModel inspector = new(documents);

        documents.Sync.Select(Selection.OfEntity(EntityKind.Greeting, 0));

        await Assert.That(inspector.Content!.IsHandshake).IsTrue();
        await Assert.That(inspector.TabLabels[1]).IsEqualTo("Handshake");
        await Assert.That(inspector.Rows!.First(r => r.IsSection && r.Name == "ClientGreeting").Value).StartsWith("inbound · ");
        await Assert.That(inspector.Assembly!.Title).IsEqualTo("Handshake");
    }

    [Test]
    public async Task Hovering_a_field_publishes_it_and_clicking_selects_its_bytes_on_the_same_frame()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        var (_, picks) = await ChatFixture.OpenAsync(documents);
        using InspectorViewModel inspector = new(documents);
        documents.Sync.Select(Selection.OfMessage(picks.Message, picks.Chunk));
        InspectorContent content = inspector.Content!;

        PropertyRow type = Row(inspector, "Type");
        inspector.HoveredRow = type;
        FieldHover hover = documents.Sync.Hover;
        await Assert.That(hover.IsNone).IsFalse();
        await Assert.That(hover.Message).IsEqualTo(MessageRef.ForFrame(picks.Chunk.Direction, picks.Chunk.Seq));
        await Assert.That(hover.Length).IsEqualTo(1);
        await Assert.That(hover.Offset).IsLessThan(0); // the type byte comes before the body

        inspector.HoveredRow = null;
        await Assert.That(documents.Sync.Hover.IsNone).IsTrue();

        inspector.SelectedRow = type;
        Selection selection = documents.Sync.Selection;
        await Assert.That(selection.Kind).IsEqualTo(SelectionKind.ByteRange);
        await Assert.That(selection.Bytes).IsEqualTo(type.Bytes.Stream);
        await Assert.That(selection.Frame).IsEqualTo(picks.Chunk);
        // The inspector stays on its frame and keeps the row.
        await Assert.That(inspector.Content).IsSameReferenceAs(content);
        await Assert.That(inspector.SelectedRow).IsSameReferenceAs(type);
    }

    [Test]
    public async Task A_hex_byte_range_selects_the_frame_and_the_field_that_holds_it()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        var (document, picks) = await ChatFixture.OpenAsync(documents);
        using InspectorViewModel inspector = new(documents);
        FrameRecord chunk;
        lock (document.Session!.Gate) document.Session.TryGetFrame(picks.Chunk, out chunk);

        // As the hex view selects: a stream range with no frame named.
        documents.Sync.Select(Selection.OfBytes(TapDirection.Inbound, new StreamRange(chunk.Offset + 1, 2)));

        await Assert.That(inspector.Content!.Frame).IsEqualTo(picks.Chunk);
        await Assert.That(inspector.SelectedRow!.Name).IsEqualTo("Body length");

        // A byte of a nested decoded field opens its parent.
        documents.Sync.Select(Selection.OfMessage(picks.Message, new FrameKey(picks.Message.Direction, picks.Message.Seq)));
        PropertyTreeModel before = inspector.Tree!;
        int child = Row(inspector, "attachment").Node + 2;
        long inside = before.Node(child).Bytes.Stream.Offset;
        documents.Sync.Select(Selection.OfBytes(TapDirection.Inbound, new StreamRange(inside, 1)));
        PropertyRow attachment = Row(inspector, "attachment");
        await Assert.That(attachment.IsExpanded).IsTrue();
        await Assert.That(inspector.SelectedRow!.Node).IsGreaterThan(attachment.Node);
        await Assert.That(inspector.SelectedRow!.Bytes.Stream.Offset).IsLessThanOrEqualTo(inside);
    }
}
