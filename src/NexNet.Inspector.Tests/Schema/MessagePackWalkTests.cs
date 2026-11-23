using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Serialization;

namespace NexNet.Inspector.Tests.Schema;

/// <summary>The schema-less MessagePack walk behind the hex view's "Decode selection as ▸ MessagePack value".</summary>
public class MessagePackWalkTests
{
    [Test]
    public async Task A_map_of_scalars_and_an_array_walks_by_wire_shape_with_exact_ranges()
    {
        // {"a": [1, -2, true, nil, 1.5, "hi", bin(2)]} then one trailing byte.
        byte[] bytes =
        [
            0x81, 0xA1, (byte)'a', 0x97, 0x01, 0xFE, 0xC3, 0xC0, 0xCB, 0x3F, 0xF8, 0, 0, 0, 0, 0, 0,
            0xA2, (byte)'h', (byte)'i', 0xC4, 0x02, 0xAB, 0xCD, 0x07,
        ];
        var range = new PayloadRange(MessageRef.ForFrame(TapDirection.Inbound, 3), 100, bytes.Length);
        var value = new SchemaDecoder(null).DecodeMessagePack(bytes, range, "value");
        DecodedField[] fields = value.Fields.ToArray();

        await Assert.That(fields[0].Kind).IsEqualTo(FieldKind.Map);
        await Assert.That(fields[0].Name).IsEqualTo("value");
        await Assert.That(value.FormatValue(fields[0])).IsEqualTo("[1]");
        await Assert.That(fields[1].Kind).IsEqualTo(FieldKind.MapEntry);
        await Assert.That((fields[2].Name, fields[2].Kind, value.GetText(fields[2]))).IsEqualTo(("Key", FieldKind.String, "a"));
        DecodedField array = fields[3];
        await Assert.That((array.Name, array.Kind, array.ChildCount)).IsEqualTo(("Value", FieldKind.Sequence, 7));
        string[] shown = fields.Where(f => f.Parent == array.Index).Select(f => f.Kind + "=" + value.FormatValue(f)).ToArray();
        await Assert.That(shown).IsEquivalentTo(new[]
        {
            "UnsignedInteger=1", "Integer=-2", "Boolean=true", "Nil=null", "Double=1.5", "String=hi", "Binary=ABCD",
        });
        await Assert.That(fields[0].Length).IsEqualTo(bytes.Length - 1);
        DecodedField trailing = fields[^1];
        await Assert.That((trailing.Kind, trailing.Problem, trailing.Offset, trailing.Length))
            .IsEqualTo((FieldKind.Raw, DecodeProblem.TrailingBytes, bytes.Length - 1, 1));
        await Assert.That(value.GetRange(fields[2])).IsEqualTo(new PayloadRange(range.Message, 101, 2));
    }

    [Test]
    public async Task A_schema_object_walks_as_its_array_and_bad_bytes_end_raw()
    {
        var bytes = NexusSerializer.Serialize(SchemaDecoderTests.SampleItem());
        var value = new SchemaDecoder(null).DecodeMessagePack(bytes);
        await Assert.That(value.Fields[0].Kind).IsEqualTo(FieldKind.Sequence);
        await Assert.That(value.Fields[0].ChildCount).IsEqualTo(8);
        await Assert.That(value.Fields[0].Length).IsEqualTo(bytes.Length);
        await Assert.That(value.Fields.ToArray().Count(f => f.Kind == FieldKind.Raw)).IsEqualTo(0);

        var truncated = new SchemaDecoder(null).DecodeMessagePack(new byte[] { 0x92, 0x01 });
        await Assert.That(truncated.Fields[^1].Kind).IsEqualTo(FieldKind.Raw);
        await Assert.That(truncated.Fields[^1].Problem).IsEqualTo(DecodeProblem.Truncated);

        var ext = new SchemaDecoder(null).DecodeMessagePack(new byte[] { 0xD4, 0x05, 0x01 });
        await Assert.That((ext.Fields[0].Kind, ext.Fields[0].Length)).IsEqualTo((FieldKind.Unknown, 3));
    }
}
