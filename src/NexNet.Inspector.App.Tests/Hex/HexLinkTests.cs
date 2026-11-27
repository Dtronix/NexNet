using NexNet.Inspector.App.Controls.Hex;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;
using static NexNet.Inspector.App.Tests.Hex.HexCaptureBuilder;

namespace NexNet.Inspector.App.Tests.Hex;

/// <summary>
/// Hover ↔ field mapping: a hex byte resolves to its header field or to the decoded field that covers it (the field the
/// property tree lights), and a hovered or selected field maps back to the stream bytes that carry it.
/// </summary>
public class HexLinkTests
{
    internal static (RecordingFile File, CaptureSession Session) Open(string fixture)
    {
        RecordingFile file = RecordingFile.Open(Wait.Fixture(fixture));
        CaptureSession session = file.CreateSession(file.Sessions[0].SessionId);
        file.Index(session);
        return (file, session);
    }

    /// <summary>An invocation sent as one frame whose arguments decode to at least one named field.</summary>
    internal static (FrameRecord Frame, DecodedValue Value, int Field) DecodedArgument(CaptureSession session)
    {
        foreach (InvocationRow invocation in session.Assembler.Invocations.ToArray())
        {
            if (invocation.Arguments.IsNone || invocation.Arguments.Message.Kind != MessageRefKind.Frame)
                continue;
            DecodedValue value = session.Decoder.DecodeArguments(invocation);
            for (int i = 0; i < value.Fields.Length; i++)
            {
                DecodedField field = value.Fields[i];
                if (field.Depth == 1 && field.Name is not null && field.Length > 0 && field.FirstChild < 0
                    && session.TryGetFrame(new FrameKey(invocation.Arguments.Message.Direction, invocation.Arguments.Message.Seq), out FrameRecord frame))
                {
                    return (frame, value, i);
                }
            }
        }

        throw new InvalidOperationException("the fixture has no decodable invocation");
    }

    [Test]
    public async Task Header_bytes_resolve_to_their_header_field()
    {
        CaptureSession session = new HexCaptureBuilder()
            .Op(TapDirection.Inbound, 0.1, Join(Preamble(), Ping(), Chunk(0x032C, 0, Pattern(20))))
            .Session;
        HexLinker linker = new();
        HexFieldHit hit;
        // Single-threaded: nothing feeds the session while the test reads it.
        {
            await Assert.That(linker.TryResolve(session, TapDirection.Inbound, 3, out hit)).IsTrue();
            await Assert.That((hit.Role, hit.Field)).IsEqualTo((HexCellRole.Preamble, new HexRange(TapDirection.Inbound, 0, 8)));
            await Assert.That(linker.TryResolve(session, TapDirection.Inbound, 9, out hit)).IsTrue();
            await Assert.That((hit.Role, hit.Field, hit.Frame.Seq)).IsEqualTo((HexCellRole.Type, new HexRange(TapDirection.Inbound, 9, 10), 2L));
            await Assert.That(linker.TryResolve(session, TapDirection.Inbound, 11, out hit)).IsTrue();
            await Assert.That((hit.Role, hit.Field)).IsEqualTo((HexCellRole.Length, new HexRange(TapDirection.Inbound, 10, 12)));
            await Assert.That(linker.TryResolve(session, TapDirection.Inbound, 13, out hit)).IsTrue();
            await Assert.That((hit.Role, hit.Field)).IsEqualTo((HexCellRole.PostHeader, new HexRange(TapDirection.Inbound, 12, 15)));

            // A body without a decoded value (an orphan chunk, no schema) resolves to the whole body.
            await Assert.That(linker.TryResolve(session, TapDirection.Inbound, 20, out hit)).IsTrue();
            await Assert.That((hit.Role, hit.Field, hit.Hover.IsNone)).IsEqualTo((HexCellRole.Body, new HexRange(TapDirection.Inbound, 15, 35), true));
            await Assert.That(linker.TryResolve(session, TapDirection.Inbound, 35, out _)).IsFalse();
        }
    }

    [Test]
    public async Task A_body_byte_resolves_to_its_decoded_field_and_back()
    {
        (RecordingFile file, CaptureSession session) = Open("chat-server.nntl");
        using (file)
        {
            // Single-threaded: nothing feeds the session while the test reads it.
            {
                (FrameRecord frame, DecodedValue value, int index) = DecodedArgument(session);
                DecodedField field = value.Fields[index];
                long payload = value.Range.Offset + field.Offset;
                long byteOffset = frame.BodyOffset + payload + (field.Length / 2);

                HexLinker linker = new();
                await Assert.That(linker.TryResolve(session, frame.Direction, byteOffset, out HexFieldHit hit)).IsTrue();
                await Assert.That(hit.Hover.IsNone).IsFalse();
                await Assert.That(hit.Hover.FieldIndex).IsEqualTo(value.FindField((int)(payload - value.Range.Offset + (field.Length / 2))));
                await Assert.That(hit.Hover.Message).IsEqualTo(value.Range.Message);
                await Assert.That(hit.Field.Contains(frame.Direction, byteOffset)).IsTrue();

                // The tree's hover maps back to the same stream bytes.
                FieldHover tree = new(value.Range.Message, index, payload, field.Length);
                List<HexRange> ranges = [];
                linker.AddStreamRanges(session, tree.Message, tree.Offset, tree.Length, ranges);
                await Assert.That(ranges.Count).IsEqualTo(1);
                await Assert.That(ranges[0]).IsEqualTo(new HexRange(frame.Direction, frame.BodyOffset + payload, frame.BodyOffset + payload + field.Length));

                HexHighlight highlight = new();
                linker.Build(session, Selection.None, tree, null, highlight);
                await Assert.That(highlight.Hovered.Single()).IsEqualTo(ranges[0]);
                await Assert.That(highlight.HasSelection).IsFalse();
            }
        }
    }

    [Test]
    public async Task A_selected_message_selects_every_frame_that_carried_it()
    {
        byte[] start = Frame(Messages.MessageType.MessageStart, MessageStartBody(7, 40));
        CaptureSession session = new HexCaptureBuilder()
            .Op(TapDirection.Inbound, 0.1, Join(Preamble(), start, Chunk(7, 0, Pattern(20)), Ping(), Chunk(7, 0, Pattern(20, 1))))
            .Session;

        // Single-threaded: nothing feeds the session while the test reads it.
        {
            MessageRef message = session.Assembler.GetMessage(TapDirection.Inbound, 2);
            await Assert.That(message.Kind).IsEqualTo(MessageRefKind.Assembled);
            HexHighlight highlight = new();
            new HexLinker().Build(session, Selection.OfMessage(message, new FrameKey(TapDirection.Inbound, 2)), FieldHover.None, null, highlight);
            await Assert.That(highlight.IsSelected(TapDirection.Inbound, 2)).IsTrue();
            await Assert.That(highlight.IsSelected(TapDirection.Inbound, 4)).IsTrue();
            await Assert.That(highlight.IsSelected(TapDirection.Inbound, 3)).IsFalse();
            // Only the first fragment's type byte is ringed.
            await Assert.That(highlight.Selected.Count).IsEqualTo(1);

            // A field that spans both chunks lights up in each.
            List<HexRange> ranges = [];
            new HexLinker().AddStreamRanges(session, message, 15, 10, ranges);
            await Assert.That(ranges.Count).IsEqualTo(2);
            await Assert.That(ranges.Sum(r => r.End - r.Start)).IsEqualTo(10);
        }
    }

    private static byte[] MessageStartBody(ushort id, int total)
    {
        // [id, kind, inner type, total length] (spec §4.8).
        System.Buffers.ArrayBufferWriter<byte> buffer = new();
        NexNet.Serialization.MsgPackWriter writer = new(buffer);
        writer.WriteArrayHeader(4);
        writer.Write(id);
        writer.Write((byte)0);
        writer.Write((byte)Messages.MessageType.Invocation);
        writer.Write((uint)total);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }
}
