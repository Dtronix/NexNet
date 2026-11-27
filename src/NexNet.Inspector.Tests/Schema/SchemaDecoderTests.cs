using System.Buffers;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Tests.Assembly;
using NexNet.Messages;
using NexNet.Serialization;

namespace NexNet.Inspector.Tests.Schema;

public class SchemaDecoderTests
{
    private const string ItemId = "NexNet.Inspector.Tests.Schema.DecodeItem";

    internal static DecodeItem SampleItem() => new()
    {
        Id = 5,
        Name = "five",
        Shape = new DecodeCircle { Radius = 1.5 },
        Tags = ["a", "b"],
        Values = [1, 2, 3],
        Color = DecodeColor.Blue,
        Key = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
        Flags = DecodeFlags.A | DecodeFlags.B,
    };

    private static byte[] Body(BodyWriter write) => WireScript.Body(write);

    [Test]
    public async Task Object_decodes_every_member_with_exact_ranges()
    {
        var bytes = NexusSerializer.Serialize(SampleItem());
        var range = new PayloadRange(MessageRef.ForFrame(TapDirection.Outbound, 7), 10, bytes.Length);
        var value = new SchemaDecoder(SchemaKit.Server).DecodeValue(SchemaKit.Type(ItemId), bytes, range, "item");

        await Assert.That(value.Problem).IsEqualTo(DecodeProblem.None);
        await Assert.That(SchemaKit.CheckCoverage(value)).IsNull();
        var root = value.Fields[0];
        await Assert.That(root.Kind).IsEqualTo(FieldKind.Object);
        await Assert.That(root.Name).IsEqualTo("item");
        await Assert.That(root.Decl!.Name).IsEqualTo("DecodeItem");
        await Assert.That(SchemaKit.Children(value, root).Select(c => c.Name).ToArray())
            .IsEquivalentTo(new string?[] { "Id", "Name", "Shape", "Tags", "Values", "Color", "Key", "Flags" });

        var name = SchemaKit.At(value, "Name");
        await Assert.That(name.Kind).IsEqualTo(FieldKind.String);
        await Assert.That(value.GetText(name)).IsEqualTo("five");
        await Assert.That(value.GetBytes(name).ToArray()).IsEquivalentTo(new byte[] { 0xa4, (byte)'f', (byte)'i', (byte)'v', (byte)'e' });
        await Assert.That(value.GetRange(name)).IsEqualTo(new PayloadRange(range.Message, 10 + name.Offset, 5));
        await Assert.That(value.FindField(name.Offset + 2)).IsEqualTo(name.Index);
        await Assert.That(value.FindField(0)).IsEqualTo(0);

        await Assert.That(SchemaKit.At(value, "Id").Value.Int64).IsEqualTo(5);
        var shape = SchemaKit.At(value, "Shape");
        await Assert.That(shape.Kind).IsEqualTo(FieldKind.Union);
        await Assert.That(shape.Value.Int64).IsEqualTo(0);
        await Assert.That(SchemaKit.At(value, "Shape", "Tag").Kind).IsEqualTo(FieldKind.UnionTag);
        var radius = SchemaKit.At(value, "Shape", "DecodeCircle", "Radius");
        await Assert.That(radius.Kind).IsEqualTo(FieldKind.Double);
        await Assert.That(radius.Value.Double).IsEqualTo(1.5);

        var tags = SchemaKit.At(value, "Tags");
        await Assert.That(tags.Kind).IsEqualTo(FieldKind.Sequence);
        await Assert.That(SchemaKit.Children(value, tags).Select(t => value.GetText(t)).ToArray()).IsEquivalentTo(new string?[] { "a", "b" });

        var values = SchemaKit.At(value, "Values");
        await Assert.That(values.Kind).IsEqualTo(FieldKind.PrimitiveArray);
        await Assert.That(values.Value.Int64).IsEqualTo(3);
        await Assert.That(value.TryGetElement(values, 2, out var third)).IsTrue();
        await Assert.That(third.Int64).IsEqualTo(3);
        await Assert.That(value.TryGetElement(values, 3, out _)).IsFalse();
        await Assert.That(values.ContentLength).IsEqualTo(12);

        await Assert.That(value.FormatValue(SchemaKit.At(value, "Color"))).IsEqualTo("Blue");
        await Assert.That(value.FormatValue(SchemaKit.At(value, "Flags"))).IsEqualTo("A | B");
        await Assert.That(value.FormatValue(SchemaKit.At(value, "Key"))).IsEqualTo("00112233-4455-6677-8899-aabbccddeeff");
    }

    [Test]
    public async Task Arguments_follow_wire_positions()
    {
        var decoder = new SchemaDecoder(SchemaKit.Server);

        var wait = decoder.DecodeArguments(SchemaKit.Method("Wait"), Body((ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(1);
            w.Write(250);
        }));
        await Assert.That(wait.Fields[0].Kind).IsEqualTo(FieldKind.Arguments);
        await Assert.That(wait.Fields[0].Name).IsEqualTo("Wait");
        await Assert.That(SchemaKit.At(wait, "milliseconds").Value.Int64).IsEqualTo(250);

        var upload = decoder.DecodeArguments(SchemaKit.Method("Upload"), Body((ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(2);
            w.Write(2);
            w.Write((byte)1);
        }));
        await Assert.That(SchemaKit.At(upload, "tag").Kind).IsEqualTo(FieldKind.Integer);
        var pipe = SchemaKit.At(upload, "pipe");
        await Assert.That(pipe.Kind).IsEqualTo(FieldKind.PipeId);
        await Assert.That(pipe.Value.Int64).IsEqualTo(1);
        await Assert.That(SchemaKit.CheckCoverage(upload)).IsNull();
    }

    [Test]
    public async Task Unknown_method_and_missing_schema_stay_raw()
    {
        var bytes = Body((ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(1);
            w.Write("x");
        });

        var unknown = new SchemaDecoder(SchemaKit.Server).DecodeArguments(null, bytes);
        await Assert.That(unknown.IsRaw).IsTrue();
        await Assert.That(unknown.Problem).IsEqualTo(DecodeProblem.UnknownMethod);
        await Assert.That(unknown.Fields[0].Length).IsEqualTo(bytes.Length);

        var none = new SchemaDecoder(null);
        await Assert.That(none.DecodeArguments(null, bytes).Problem).IsEqualTo(DecodeProblem.NoSchema);
        await Assert.That(none.DecodeResult(null, InvocationResultState.CompletedResult, bytes).Problem).IsEqualTo(DecodeProblem.NoSchema);

        // Through a session: method 999 is not in the schema, method Add is.
        var add = SchemaKit.Method("Add");
        var script = new WireScript()
            .Message(TapDirection.Outbound, MessageType.ClientGreeting, ClientGreeting)
            .Invocation(TapDirection.Outbound, 1, 999, InvocationFlags.None, (ref MsgPackWriter w) =>
            {
                w.WriteArrayHeader(1);
                w.Write("x");
            })
            .Invocation(TapDirection.Outbound, 2, add.Id, InvocationFlags.None, (ref MsgPackWriter w) =>
            {
                w.WriteArrayHeader(3);
                w.Write(1);
                w.Write(2L);
                w.Write((byte)3);
            });
        var replay = SessionReplay.Run(script, SchemaKit.Server);
        await Assert.That(replay.Assembler.Role).IsEqualTo(NntlSessionRole.Client);
        var invocations = replay.Assembler.Invocations.ToArray();
        var raw = replay.Decoder.DecodeArguments(invocations[0]);
        await Assert.That(raw.IsRaw).IsTrue();
        await Assert.That(raw.Problem).IsEqualTo(DecodeProblem.UnknownMethod);
        var decoded = replay.Decoder.DecodeArguments(invocations[1]);
        await Assert.That(decoded.Problem).IsEqualTo(DecodeProblem.None);
        await Assert.That(SchemaKit.At(decoded, "c").Value.UInt64).IsEqualTo(3UL);

        var noSchema = SessionReplay.Run(script, null);
        await Assert.That(noSchema.Decoder.DecodeArguments(noSchema.Assembler.Invocations[1]).Problem).IsEqualTo(DecodeProblem.NoSchema);
    }

    [Test]
    public async Task Truncated_value_keeps_what_decoded_and_ends_raw()
    {
        var bytes = NexusSerializer.Serialize(SampleItem());
        var decoder = new SchemaDecoder(SchemaKit.Server);
        for (var cut = 0; cut < bytes.Length; cut++)
        {
            var value = decoder.DecodeValue(SchemaKit.Type(ItemId), bytes.AsMemory(0, cut));
            await Assert.That(SchemaKit.CheckCoverage(value)).IsNull().Because($"cut {cut}");
            var last = value.Fields[^1];
            await Assert.That(last.Kind).IsEqualTo(FieldKind.Raw).Because($"cut {cut}");
            await Assert.That(last.Problem).IsEqualTo(DecodeProblem.Truncated).Because($"cut {cut}");
            await Assert.That(last.End).IsEqualTo(cut);
            await Assert.That(value.Problem).IsEqualTo(DecodeProblem.Truncated);
        }

        // Cut inside Tags: Id, Name and Shape stay decoded.
        var tagsAt = decoder.DecodeValue(SchemaKit.Type(ItemId), bytes).Fields.ToArray().Single(f => f.Name == "Tags").Offset;
        var partial = decoder.DecodeValue(SchemaKit.Type(ItemId), bytes.AsMemory(0, tagsAt + 2));
        await Assert.That(value: SchemaKit.At(partial, "Shape", "DecodeCircle", "Radius").Value.Double).IsEqualTo(1.5);
        await Assert.That(SchemaKit.At(partial, "Tags").Kind).IsEqualTo(FieldKind.Sequence);
    }

    [Test]
    public async Task Wrong_shapes_are_skipped_and_siblings_still_decode()
    {
        var value = new SchemaDecoder(SchemaKit.Server).DecodeArguments(SchemaKit.Method("Add"), Body((ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(4);
            w.Write("not an int");
            w.Write(2L);
            w.Write((byte)3);
            w.Write(true);
        }));

        await Assert.That(SchemaKit.CheckCoverage(value)).IsNull();
        var a = SchemaKit.At(value, "a");
        await Assert.That(a.Kind).IsEqualTo(FieldKind.Unknown);
        await Assert.That(a.Problem).IsEqualTo(DecodeProblem.TypeMismatch);
        await Assert.That(a.Length).IsEqualTo(11);
        await Assert.That(SchemaKit.At(value, "b").Value.Int64).IsEqualTo(2);
        var extra = SchemaKit.Children(value, value.Fields[0])[3];
        await Assert.That(extra.Kind).IsEqualTo(FieldKind.Unknown);
        await Assert.That(extra.Problem).IsEqualTo(DecodeProblem.UnknownType);
        await Assert.That(value.Problem).IsEqualTo(DecodeProblem.TypeMismatch);
    }

    [Test]
    public async Task Trailing_bytes_and_malformed_codes_are_flagged()
    {
        var decoder = new SchemaDecoder(SchemaKit.Server);
        var trailing = decoder.DecodeValue(SchemaTypeRef.Primitive(SchemaPrimKind.Int32), new byte[] { 0x05, 0x06, 0x07 });
        await Assert.That(trailing.Fields.Length).IsEqualTo(2);
        await Assert.That(trailing.Fields[0].NextSibling).IsEqualTo(1);
        await Assert.That(trailing.Fields[1].Problem).IsEqualTo(DecodeProblem.TrailingBytes);
        await Assert.That(trailing.Fields[1].Length).IsEqualTo(2);

        var malformed = decoder.DecodeValue(SchemaKit.Type(ItemId), new byte[] { 0x92, 0x01, 0xc1, 0x00 });
        await Assert.That(malformed.Problem).IsEqualTo(DecodeProblem.Malformed);
        await Assert.That(SchemaKit.CheckCoverage(malformed)).IsNull();
        await Assert.That(SchemaKit.At(malformed, "Id").Value.Int64).IsEqualTo(1);
    }

    [Test]
    public async Task Limits_skip_whole_values()
    {
        var bytes = NexusSerializer.Serialize(SampleItem());
        var fields = new SchemaDecoder(SchemaKit.Server, new SchemaDecoderOptions { MaxFields = 3 }).DecodeValue(SchemaKit.Type(ItemId), bytes);
        await Assert.That(SchemaKit.CheckCoverage(fields)).IsNull();
        await Assert.That(fields.Problem).IsEqualTo(DecodeProblem.LimitReached);
        await Assert.That(SchemaKit.At(fields, "Name").Kind).IsEqualTo(FieldKind.String);
        await Assert.That(SchemaKit.At(fields, "Shape").Problem).IsEqualTo(DecodeProblem.LimitReached);

        var depth = new SchemaDecoder(SchemaKit.Server, new SchemaDecoderOptions { MaxDepth = 1 }).DecodeValue(SchemaKit.Type(ItemId), bytes);
        await Assert.That(SchemaKit.CheckCoverage(depth)).IsNull();
        await Assert.That(SchemaKit.At(depth, "Shape").Kind).IsEqualTo(FieldKind.Union);
        await Assert.That(SchemaKit.At(depth, "Shape", "DecodeCircle").Problem).IsEqualTo(DecodeProblem.None);
        await Assert.That(SchemaKit.At(depth, "Shape", "DecodeCircle", "Radius").Problem).IsEqualTo(DecodeProblem.LimitReached);
    }

    [Test]
    public async Task Generic_arguments_resolve_and_bind_wire_forms()
    {
        var wrap = SchemaKit.Method("Wrap");
        var bytes = NexusSerializer.Serialize(new DecodeBox<int> { Value = 4, More = [5, 6] });
        var value = new SchemaDecoder(SchemaKit.Server).DecodeValue(wrap.Parameters[0].Type, bytes);

        await Assert.That(value.Problem).IsEqualTo(DecodeProblem.None);
        await Assert.That(SchemaKit.At(value, "Value").Kind).IsEqualTo(FieldKind.Integer);
        // List<T> bound to int is an ext 78 array on the wire.
        await Assert.That(SchemaKit.At(value, "More").Kind).IsEqualTo(FieldKind.PrimitiveArray);

        var result = NexusSerializer.Serialize(new DecodeBox<DecodeItem> { Value = SampleItem(), More = [new DecodeItem { Id = 9 }] });
        var decoded = new SchemaDecoder(SchemaKit.Server).DecodeResult(wrap, InvocationResultState.CompletedResult, result);
        await Assert.That(decoded.Problem).IsEqualTo(DecodeProblem.None);
        await Assert.That(decoded.Fields[0].Name).IsEqualTo("Result");
        await Assert.That(value: SchemaKit.At(decoded, "Value", "Name").Kind).IsEqualTo(FieldKind.String);
        var more = SchemaKit.At(decoded, "More");
        await Assert.That(more.Kind).IsEqualTo(FieldKind.Sequence);
        await Assert.That(SchemaKit.Children(decoded, more).Single().Decl!.Name).IsEqualTo("DecodeItem");
    }

    [Test]
    public async Task Results_and_exception_details_decode()
    {
        var decoder = new SchemaDecoder(SchemaKit.Server);
        var add = decoder.DecodeResult(SchemaKit.Method("Add"), InvocationResultState.CompletedResult, new byte[] { 0x06 });
        await Assert.That(add.Fields[0].Kind).IsEqualTo(FieldKind.Integer);
        await Assert.That(add.Fields[0].Value.Int64).IsEqualTo(6);

        var detail = decoder.DecodeResult(null, InvocationResultState.Exception, Body((ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(3);
            w.Write(42);
            w.Write("System.InvalidOperationException");
            w.Write("boom");
        }));
        await Assert.That(detail.Fields[0].Kind).IsEqualTo(FieldKind.ExceptionDetail);
        await Assert.That(SchemaKit.At(detail, "Code").Value.Int64).IsEqualTo(42);
        await Assert.That(detail.GetText(SchemaKit.At(detail, "Message"))).IsEqualTo("boom");
    }

    [Test]
    public async Task Collection_messages_decode_values_with_the_item_type()
    {
        var collection = SchemaKit.Server.Exports[0].ServerApi.Collections.Single();
        var item = NexusSerializer.Serialize(SampleItem());
        var insert = Body((ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(2);
            w.Write((ushort)4);
            w.WriteArrayHeader(4);
            w.Write((byte)1);
            w.Write(3);
            w.Write(0);
            w.WriteRaw(item);
        });

        var decoder = new SchemaDecoder(SchemaKit.Server);
        var value = decoder.DecodeCollectionMessage(collection, insert);
        await Assert.That(value.Problem).IsEqualTo(DecodeProblem.None);
        await Assert.That(SchemaKit.CheckCoverage(value)).IsNull();
        await Assert.That(value.Fields[0].Kind).IsEqualTo(FieldKind.CollectionMessage);
        await Assert.That(value.Fields[0].Name).IsEqualTo("Insert");
        await Assert.That(SchemaKit.At(value, "Insert", "Flags").Value.Int64).IsEqualTo(1);
        await Assert.That(SchemaKit.At(value, "Insert", "Version").Value.Int64).IsEqualTo(3);
        await Assert.That(value.GetText(SchemaKit.At(value, "Insert", "Value", "Name"))).IsEqualTo("five");

        var reset = Body((ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(2);
            w.Write((ushort)1);
            w.WriteArrayHeader(2);
            w.Write((byte)0);
            w.WriteArrayHeader(2);
            w.WriteRaw(item);
            w.WriteRaw(item);
        });
        var values = decoder.DecodeCollectionMessage(collection, reset);
        await Assert.That(values.Problem).IsEqualTo(DecodeProblem.None);
        var list = SchemaKit.At(values, "ResetValues", "Values");
        await Assert.That(list.Kind).IsEqualTo(FieldKind.Sequence);
        await Assert.That(list.ChildCount).IsEqualTo(2);

        var unknownTag = decoder.DecodeCollectionMessage(collection, new byte[] { 0x92, 0x63, 0x91, 0x00 });
        await Assert.That(unknownTag.Problem).IsEqualTo(DecodeProblem.UnknownTag);
        await Assert.That(SchemaKit.CheckCoverage(unknownTag)).IsNull();
    }

    [Test]
    public async Task Scalars_render_on_demand()
    {
        var decoder = new SchemaDecoder(SchemaKit.Server);
        string Format<T>(SchemaPrimKind prim, T input)
        {
            var value = decoder.DecodeValue(SchemaTypeRef.Primitive(prim), NexusSerializer.Serialize(input));
            return value.FormatValue(value.Fields[0]);
        }

        await Assert.That(Format(SchemaPrimKind.Decimal, -12.34m)).IsEqualTo("-12.34");
        await Assert.That(Format(SchemaPrimKind.DateTime, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc))).IsEqualTo("2020-01-02T03:04:05.0000000Z");
        await Assert.That(Format(SchemaPrimKind.TimeSpan, TimeSpan.FromSeconds(90))).IsEqualTo("00:01:30");
        await Assert.That(Format(SchemaPrimKind.Char, 'x')).IsEqualTo("'x' U+0078");
        await Assert.That(Format(SchemaPrimKind.Single, 1.5f)).IsEqualTo("1.5");
        await Assert.That(Format(SchemaPrimKind.UInt64, ulong.MaxValue)).IsEqualTo("18446744073709551615");
        await Assert.That(Format(SchemaPrimKind.DateOnly, new DateOnly(2021, 5, 6))).IsEqualTo("2021-05-06");
        await Assert.That(Format(SchemaPrimKind.BigInteger, new System.Numerics.BigInteger(-5))).IsEqualTo("-5");
        await Assert.That(Format(SchemaPrimKind.DateTimeOffset, new DateTimeOffset(2020, 1, 1, 12, 0, 0, TimeSpan.FromHours(2))))
            .IsEqualTo("2020-01-01T12:00:00.0000000+02:00");
    }

    private static void ClientGreeting(ref MsgPackWriter w)
    {
        var export = SchemaKit.Server.Exports[0];
        w.WriteArrayHeader(6);
        w.WriteNil();
        w.Write(export.Handshake.ServerNexusHash);
        w.Write(export.Handshake.ClientNexusHash);
        w.WriteNil();
        w.Write(65536);
        w.Write(32);
    }
}
