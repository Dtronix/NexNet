using System.Buffers;
using NexNet.Inspector.Core.Schema;
using NexNet.Serialization;

namespace NexNet.Inspector.Tests.Schema;

public class SchemaReaderTests
{
    [Test]
    public async Task Generated_schema_reads_with_its_hash()
    {
        var bytes = SchemaKit.Bytes<DecodeServerNexus>();
        var schema = NexusSchema.Read(bytes);

        await Assert.That(schema.IrVersion).IsEqualTo(1);
        await Assert.That(schema.Hash).IsEqualTo(SchemaKit.Hash<DecodeServerNexus>());
        await Assert.That(SchemaHash.Compute(bytes)).IsEqualTo(SchemaKit.Hash<DecodeServerNexus>());

        var export = schema.Exports.Single();
        await Assert.That(export.Name).IsEqualTo("DecodeServerNexus");
        await Assert.That(export.ServerApi.Name).IsEqualTo("IDecodeServer");
        await Assert.That(export.ClientApi.Name).IsEqualTo("IDecodeClient");
        await Assert.That(export.Handshake.ServerNexusHash).IsEqualTo(export.ServerApi.Hash);
        await Assert.That(export.Handshake.ClientNexusHash).IsEqualTo(export.ClientApi.Hash);
        await Assert.That(export.ServerApi.Methods.Select(m => m.Name).Order().ToArray()).IsEquivalentTo(new[]
        {
            "Add", "Count", "Describe", "Fail", "Maybe", "Pair", "Reshape", "Store", "Stream", "Upload", "Wait", "Wrap",
        });
        await Assert.That(export.ClientApi.Methods.Single().Name).IsEqualTo("Greet");

        foreach (var method in export.ServerApi.Methods)
        {
            await Assert.That(export.ServerApi.TryGetMethod(method.Id, out var found)).IsTrue();
            await Assert.That(found).IsSameReferenceAs(method);
        }
    }

    [Test]
    public async Task Client_nexus_carries_the_same_pair()
    {
        var client = NexusSchema.Read(SchemaKit.Bytes<DecodeClientNexus>());
        await Assert.That(client.Hash).IsEqualTo(SchemaKit.Hash<DecodeClientNexus>());

        var export = client.Exports.Single();
        var server = SchemaKit.Server.Exports[0];
        await Assert.That(export.ClientApi.Hash).IsEqualTo(server.ClientApi.Hash);
        await Assert.That(export.ServerApi.Hash).IsEqualTo(server.ServerApi.Hash);
        await Assert.That(export.ServerApi.Methods.Select(m => (m.Name, m.Id)).ToArray())
            .IsEquivalentTo(server.ServerApi.Methods.Select(m => (m.Name, m.Id)).ToArray());
    }

    [Test]
    public async Task Methods_describe_parameters_pipes_and_cancellation()
    {
        var add = SchemaKit.Method("Add");
        await Assert.That(add.ReturnKind).IsEqualTo(SchemaReturnKind.TaskOf);
        await Assert.That(add.ReturnType!.Prim).IsEqualTo(SchemaPrimKind.Int64);
        await Assert.That(add.Parameters.Select(p => (p.Name, p.Type!.Prim, p.WireIndex)).ToArray()).IsEquivalentTo(new[]
        {
            ("a", SchemaPrimKind.Int32, 1), ("b", SchemaPrimKind.Int64, 2), ("c", SchemaPrimKind.Byte, 3),
        });
        await Assert.That(add.PipeArgumentIndex).IsEqualTo(-1);

        var upload = SchemaKit.Method("Upload");
        await Assert.That(upload.PipeKind).IsEqualTo(SchemaPipeKind.DuplexPipe);
        await Assert.That(upload.PipeArgumentIndex).IsEqualTo(1);
        await Assert.That(upload.GetWireParam(1)!.Kind).IsEqualTo(SchemaParamKind.DuplexPipe);
        await Assert.That(upload.ReturnKind).IsEqualTo(SchemaReturnKind.Task);

        var stream = SchemaKit.Method("Stream");
        await Assert.That(stream.PipeKind).IsEqualTo(SchemaPipeKind.Channel);
        await Assert.That(stream.PipeArgumentIndex).IsEqualTo(0);
        await Assert.That(stream.PipeType!.Decl!.Name).IsEqualTo("DecodeItem");

        var wait = SchemaKit.Method("Wait");
        await Assert.That(wait.HasCancellation).IsTrue();
        await Assert.That(wait.SerializedCount).IsEqualTo(1);
        await Assert.That(wait.Parameters[1].Kind).IsEqualTo(SchemaParamKind.CancellationToken);
        await Assert.That(wait.Parameters[1].WireIndex).IsEqualTo(0);
        await Assert.That(wait.GetWireParam(1)).IsNull();

        var maybe = SchemaKit.Method("Maybe");
        await Assert.That(maybe.Parameters[0].Type!.Kind).IsEqualTo(SchemaTypeKind.Nullable);
        var color = maybe.Parameters[1].Type!.Args[0];
        await Assert.That(color.Decl).IsTypeOf<SchemaEnumDecl>();
        await Assert.That(((SchemaEnumDecl)color.Decl!).Underlying).IsEqualTo(SchemaPrimKind.Int16);

        var pair = SchemaKit.Method("Pair");
        await Assert.That(pair.Parameters[0].Type!.Kind).IsEqualTo(SchemaTypeKind.Tuple);
        await Assert.That(pair.Parameters[1].Type!.IsKeyValuePair).IsTrue();

        var count = SchemaKit.Method("Count");
        await Assert.That(count.Parameters.Select(p => p.Type!.Kind).ToArray())
            .IsEquivalentTo(new[] { SchemaTypeKind.Seq, SchemaTypeKind.PrimArray, SchemaTypeKind.Set });
        await Assert.That(count.ReturnType!.Kind).IsEqualTo(SchemaTypeKind.Map);

        var wrap = SchemaKit.Method("Wrap");
        await Assert.That(wrap.Parameters[0].Type!.Decl!.Name).IsEqualTo("DecodeBox");
        await Assert.That(wrap.Parameters[0].Type!.Args.Single().Prim).IsEqualTo(SchemaPrimKind.Int32);
    }

    [Test]
    public async Task Types_and_collections_are_linked()
    {
        var schema = SchemaKit.Server;
        var shape = (SchemaUnionDecl)schema.Types.Single(t => t.Name == "DecodeShape");
        await Assert.That(shape.TryGetCase(1, out var square)).IsTrue();
        await Assert.That(square!.Decl!.Name).IsEqualTo("DecodeSquare");

        var squareDecl = (SchemaObjectDecl)square.Decl;
        await Assert.That(squareDecl.ArrayLength).IsEqualTo(3);
        await Assert.That(squareDecl.GetMember(1)).IsNull();
        await Assert.That(squareDecl.GetMember(2)!.Name).IsEqualTo("Label");

        var flags = (SchemaEnumDecl)schema.Types.Single(t => t.Name == "DecodeFlags");
        await Assert.That(flags.IsFlags).IsTrue();
        await Assert.That(flags.IsUnsigned).IsTrue();

        var box = (SchemaObjectDecl)schema.Types.Single(t => t.Name == "DecodeBox");
        await Assert.That(box.TypeParams.Single()).IsEqualTo("T");
        await Assert.That(box.GetMember(0)!.Type.Kind).IsEqualTo(SchemaTypeKind.TypeParam);

        var items = schema.Exports[0].ServerApi.Collections.Single();
        await Assert.That(items.Name).IsEqualTo("Items");
        await Assert.That(items.Kind).IsEqualTo(SchemaCollectionKind.List);
        await Assert.That(items.Mode).IsEqualTo(SchemaCollectionMode.BiDirectional);
        await Assert.That(items.Item!.Decl!.Name).IsEqualTo("DecodeItem");
        await Assert.That(items.ValuesType!.Kind).IsEqualTo(SchemaTypeKind.Seq);
        await Assert.That(schema.Exports[0].ServerApi.TryGetCollection(items.Id, out _)).IsTrue();
        await Assert.That(schema.Exports[0].ServerApi.TryGetMethod(items.Id, out _)).IsFalse();
    }

    [Test]
    public async Task Unknown_trailing_fields_are_skipped()
    {
        var plain = NexusSchema.Read(HandWritten(extra: false));
        var extended = NexusSchema.Read(HandWritten(extra: true));

        foreach (var schema in new[] { plain, extended })
        {
            var export = schema.Exports.Single();
            await Assert.That(export.Name).IsEqualTo("Hand");
            var method = export.ServerApi.Methods.Single();
            await Assert.That(method.Name).IsEqualTo("Put");
            await Assert.That(method.Parameters.Single().Type!.Decl!.Name).IsEqualTo("Thing");
            await Assert.That(method.ReturnType!.Kind).IsEqualTo(SchemaTypeKind.Nullable);
            await Assert.That(export.ServerApi.Collections.Single().Item!.Prim).IsEqualTo(SchemaPrimKind.String);
            await Assert.That(export.ServerApi.Versions.Single().MethodIds.ToArray()).IsEquivalentTo(new ushort[] { 0, 1 });

            var thing = (SchemaObjectDecl)schema.Types.Single(t => t.Id == "Ns.Thing");
            await Assert.That(thing.GetMember(0)!.DefaultLiteral!.Value).IsEqualTo("7");
            var union = (SchemaUnionDecl)schema.Types.Single(t => t.Id == "Ns.Choice");
            await Assert.That(union.Cases.Single().Decl).IsSameReferenceAs(thing);
            var color = (SchemaEnumDecl)schema.Types.Single(t => t.Id == "Ns.Color");
            await Assert.That(color.TryGetName(-2, out var name)).IsTrue();
            await Assert.That(name).IsEqualTo("Down");
        }
    }

    [Test]
    public async Task Bad_schemas_are_refused_without_throwing()
    {
        var good = SchemaKit.Bytes<DecodeServerNexus>();
        for (var length = 0; length < good.Length; length += Math.Max(1, good.Length / 97))
        {
            await Assert.That(NexusSchema.TryRead(good.AsMemory(0, length), out var schema, out var error)).IsFalse();
            await Assert.That(schema).IsNull();
            await Assert.That(error).IsNotNull();
        }

        var newer = (byte[])good.Clone();
        newer[1] = 2; // root is [irVersion, ...]: fixarray then the version fixint
        await Assert.That(NexusSchema.TryRead(newer, out _, out var versionError)).IsFalse();
        await Assert.That(versionError!).Contains("IR version 2");
        await Assert.That(() => NexusSchema.Read(newer)).Throws<FormatException>();

        var trailing = good.Append((byte)0xc0).ToArray();
        await Assert.That(NexusSchema.TryRead(trailing, out _, out _)).IsFalse();
    }

    [Test]
    public async Task Hash_round_trips_through_its_stored_form()
    {
        var bytes = SchemaKit.Bytes<DecodeServerNexus>();
        var hash = SchemaKit.Hash<DecodeServerNexus>();
        var stored = SchemaHash.ToBytes(hash);

        await Assert.That(stored.Length).IsEqualTo(SchemaHash.Size);
        await Assert.That(SchemaHash.TryRead(stored, out var read)).IsTrue();
        await Assert.That(read).IsEqualTo(hash);
        await Assert.That(SchemaHash.Matches(stored, bytes)).IsTrue();
        await Assert.That(SchemaHash.Matches(stored, SchemaKit.Hash<DecodeClientNexus>())).IsFalse();
        await Assert.That(SchemaHash.Matches(ReadOnlySpan<byte>.Empty, hash)).IsFalse();
        await Assert.That(SchemaHash.Compute(ReadOnlySpan<byte>.Empty)).IsEqualTo(0xcbf29ce484222325UL);
        await Assert.That(SchemaHash.Compute("a"u8)).IsEqualTo(0xaf63dc4c8601ec8cUL);

        Span<byte> written = stackalloc byte[SchemaHash.Size];
        SchemaHash.Write(hash, written);
        await Assert.That(written.SequenceEqual(stored)).IsTrue();
    }

    /// <summary>
    /// A schema written field by field (docs/internals/nexnet-ir.md §5.2). With <paramref name="extra"/>, every record
    /// gets one more element, as a newer IR version that appends an optional field would write.
    /// </summary>
    private static byte[] HandWritten(bool extra)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var w = new MsgPackWriter(buffer);

        void Record(ref MsgPackWriter writer, int fields) => writer.WriteArrayHeader(fields + (extra ? 1 : 0));
        void Extra(ref MsgPackWriter writer)
        {
            if (extra)
                writer.Write("future");
        }

        void TypeRef(ref MsgPackWriter writer, byte kind, byte prim, string? id)
        {
            Record(ref writer, 9);
            writer.Write(kind);
            writer.Write(prim);
            writer.WriteNil();
            writer.WriteArrayHeader(0);
            if (id is null)
                writer.WriteNil();
            else
                writer.Write(id);
            writer.Write(0);
            writer.Write(false);
            writer.Write(false);
            writer.WriteArrayHeader(0);
            Extra(ref writer);
        }

        Record(ref w, 3);
        w.Write(1);
        w.WriteArrayHeader(1);

        // Export
        Record(ref w, 8);
        w.Write("Hand");
        w.Write("Ns");
        w.Write(0);

        // Server API: one method, one collection, one version.
        Record(ref w, 7);
        w.Write("Ns.IHand");
        w.Write("IHand");
        w.Write("Ns");
        w.Write(11);
        w.WriteArrayHeader(1);
        Record(ref w, 13);
        w.Write("Put");
        w.Write(0);
        w.Write(5);
        w.Write((byte)2);
        TypeRef(ref w, 9, 0, null);
        w.WriteArrayHeader(1);
        Record(ref w, 5);
        w.Write("thing");
        w.Write((byte)0);
        TypeRef(ref w, 10, 0, "Ns.Thing");
        w.Write(1);
        w.Write(false);
        Extra(ref w);
        w.Write(1);
        w.Write(false);
        w.Write((byte)0);
        w.WriteNil();
        w.Write(false);
        w.Write(false);
        w.WriteNil();
        Extra(ref w);
        w.WriteArrayHeader(1);
        Record(ref w, 8);
        w.Write("Names");
        w.Write(1);
        w.Write(9);
        w.Write((byte)1);
        w.Write((byte)1);
        TypeRef(ref w, 0, 15, null);
        w.Write(false);
        w.WriteNil();
        Extra(ref w);
        w.WriteArrayHeader(1);
        Record(ref w, 3);
        w.Write("v1");
        w.Write(11);
        w.WriteArrayHeader(2);
        w.Write(0);
        w.Write(1);
        Extra(ref w);
        Extra(ref w);

        // Client API: empty.
        Record(ref w, 7);
        w.Write("Ns.IHandClient");
        w.Write("IHandClient");
        w.Write("Ns");
        w.Write(12);
        w.WriteArrayHeader(0);
        w.WriteArrayHeader(0);
        w.WriteArrayHeader(0);
        Extra(ref w);

        Record(ref w, 3);
        w.Write("v1");
        w.Write(11);
        w.Write(12);
        Extra(ref w);
        w.WriteArrayHeader(1);
        Record(ref w, 2);
        w.Write(12);
        w.Write(0);
        Extra(ref w);
        w.WriteNil();
        Extra(ref w);

        // Types: an enum, a union and an object, ordered by id.
        w.WriteArrayHeader(3);
        Record(ref w, 10);
        w.Write(2);
        Common(ref w, "Ns.Color", "Color");
        w.Write((byte)6);
        w.Write(false);
        w.WriteArrayHeader(1);
        Record(ref w, 2);
        w.Write("Down");
        w.Write(-2);
        Extra(ref w);
        Extra(ref w);

        Record(ref w, 11);
        w.Write(1);
        Common(ref w, "Ns.Choice", "Choice");
        w.Write(false);
        w.WriteArrayHeader(0);
        w.Write(false);
        w.WriteArrayHeader(1);
        Record(ref w, 3);
        w.Write(0);
        w.Write("Ns.Thing");
        w.Write(1);
        Extra(ref w);
        Extra(ref w);

        Record(ref w, 15);
        w.Write(0);
        Common(ref w, "Ns.Thing", "Thing");
        w.Write(false);
        w.Write(false);
        w.Write(false);
        w.WriteArrayHeader(0);
        w.WriteNil();
        w.Write(1);
        w.Write(false);
        w.WriteArrayHeader(1);
        Record(ref w, 7);
        w.Write("Count");
        w.Write(0);
        TypeRef(ref w, 0, 6, null);
        w.Write(false);
        w.Write(false);
        Record(ref w, 2);
        w.Write((byte)2);
        w.Write("7");
        Extra(ref w);
        w.WriteNil();
        Extra(ref w);
        Extra(ref w);

        Extra(ref w);
        w.Flush();
        return buffer.WrittenSpan.ToArray();

        static void Common(ref MsgPackWriter writer, string id, string name)
        {
            writer.Write(id);
            writer.Write(id);
            writer.Write("Ns");
            writer.Write(name);
            writer.WriteArrayHeader(0);
            writer.WriteNil();
        }
    }
}
