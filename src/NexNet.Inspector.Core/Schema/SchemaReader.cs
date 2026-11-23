using NexNet.Serialization;

namespace NexNet.Inspector.Core.Schema;

/// <summary>
/// Reads the MessagePack IR with NexNet's own reader. Every record is an array whose known fields come first; elements
/// past them are skipped, which is how the IR adds optional fields (nexnet-ir.md §3, §5.1).
/// </summary>
internal static class SchemaReader
{
    private const int DeclObject = 0;
    private const int DeclUnion = 1;
    private const int DeclEnum = 2;

    private delegate T ReadItem<out T>(ref MsgPackReader reader);

    public static NexusSchema Read(ReadOnlyMemory<byte> bytes)
    {
        var r = new MsgPackReader(bytes, NexusSerializerOptions.Trusted);
        var count = Begin(ref r, 3, "root");
        var version = r.ReadInt32();
        if (version != NexusSchema.SupportedIrVersion)
            throw new FormatException($"Unsupported IR version {version}; this reader understands version {NexusSchema.SupportedIrVersion}.");
        var exports = Array(ref r, ReadExport);
        var types = Array(ref r, ReadDecl);
        Skip(ref r, count, 3);
        if (!r.End)
            throw new FormatException("Trailing bytes after the schema.");
        return new NexusSchema(version, exports, types, SchemaHash.Compute(bytes.Span));
    }

    private static int Begin(ref MsgPackReader r, int known, string record)
    {
        var count = r.ReadArrayHeader();
        if (count < known)
            throw new FormatException($"A {record} record has {count} fields; at least {known} are required.");
        return count;
    }

    private static void Skip(ref MsgPackReader r, int count, int known)
    {
        for (var i = known; i < count; i++)
            r.Skip();
    }

    private static T[] Array<T>(ref MsgPackReader r, ReadItem<T> read)
    {
        var count = r.ReadArrayHeader();
        var items = new T[count];
        for (var i = 0; i < count; i++)
            items[i] = read(ref r);
        return items;
    }

    private static string Str(ref MsgPackReader r) => r.ReadString() ?? throw new FormatException("Unexpected nil string.");

    private static string[] Strings(ref MsgPackReader r) => Array(ref r, static (ref MsgPackReader x) => Str(ref x));

    private static SchemaExport ReadExport(ref MsgPackReader r)
    {
        var count = Begin(ref r, 8, "export");
        var name = Str(ref r);
        var ns = Str(ref r);
        var targets = r.ReadInt32();
        var server = ReadInterface(ref r);
        var client = ReadInterface(ref r);
        var handshakeCount = Begin(ref r, 3, "handshake");
        var handshake = new SchemaHandshake(r.ReadString(), r.ReadInt32(), r.ReadInt32());
        Skip(ref r, handshakeCount, 3);
        var accepted = Array(ref r, static (ref MsgPackReader x) =>
        {
            var n = Begin(ref x, 2, "accepted id");
            var id = new SchemaAcceptedId(x.ReadInt32(), x.ReadUInt16());
            Skip(ref x, n, 2);
            return id;
        });
        var docs = r.ReadString();
        Skip(ref r, count, 8);
        return new SchemaExport(name, ns, targets, server, client, handshake, accepted, docs);
    }

    private static SchemaInterface ReadInterface(ref MsgPackReader r)
    {
        var count = Begin(ref r, 7, "interface");
        var api = new SchemaInterface(
            Str(ref r),
            Str(ref r),
            Str(ref r),
            r.ReadInt32(),
            Array(ref r, ReadMethod),
            Array(ref r, ReadCollection),
            Array(ref r, static (ref MsgPackReader x) =>
            {
                var n = Begin(ref x, 3, "version");
                var version = new SchemaVersion(Str(ref x), x.ReadInt32(), Array(ref x, static (ref MsgPackReader y) => y.ReadUInt16()));
                Skip(ref x, n, 3);
                return version;
            }));
        Skip(ref r, count, 7);
        return api;
    }

    private static SchemaMethod ReadMethod(ref MsgPackReader r)
    {
        var count = Begin(ref r, 13, "method");
        var method = new SchemaMethod(
            Str(ref r),
            r.ReadUInt16(),
            r.ReadInt32(),
            (SchemaReturnKind)r.ReadByte(),
            ReadTypeRef(ref r),
            Array(ref r, static (ref MsgPackReader x) =>
            {
                var n = Begin(ref x, 5, "parameter");
                var parameter = new SchemaParam(Str(ref x), (SchemaParamKind)x.ReadByte(), ReadTypeRef(ref x), x.ReadInt32(), x.ReadBoolean());
                Skip(ref x, n, 5);
                return parameter;
            }),
            r.ReadInt32(),
            r.ReadBoolean(),
            (SchemaPipeKind)r.ReadByte(),
            ReadTypeRef(ref r),
            r.ReadBoolean(),
            r.ReadBoolean(),
            r.ReadString());
        Skip(ref r, count, 13);
        return method;
    }

    private static SchemaCollection ReadCollection(ref MsgPackReader r)
    {
        var count = Begin(ref r, 8, "collection");
        var collection = new SchemaCollection(
            Str(ref r),
            r.ReadUInt16(),
            r.ReadInt32(),
            (SchemaCollectionKind)r.ReadByte(),
            (SchemaCollectionMode)r.ReadByte(),
            ReadTypeRef(ref r),
            r.ReadBoolean(),
            r.ReadString());
        Skip(ref r, count, 8);
        return collection;
    }

    private static SchemaTypeRef? ReadTypeRef(ref MsgPackReader r)
    {
        if (r.TryReadNil())
            return null;

        var count = Begin(ref r, 9, "type reference");
        var type = new SchemaTypeRef(
            (SchemaTypeKind)r.ReadByte(),
            (SchemaPrimKind)r.ReadByte(),
            r.ReadString(),
            Array(ref r, static (ref MsgPackReader x) => ReadTypeRef(ref x) ?? throw new FormatException("Nil type argument.")),
            r.ReadString(),
            r.ReadInt32(),
            r.ReadBoolean(),
            r.ReadBoolean(),
            Array(ref r, static (ref MsgPackReader x) =>
            {
                var n = Begin(ref x, 2, "native formatter");
                var formatter = new SchemaNativeFormatter(x.ReadInt32(), Str(ref x));
                Skip(ref x, n, 2);
                return formatter;
            }));
        Skip(ref r, count, 9);
        return type;
    }

    private static SchemaTypeDecl ReadDecl(ref MsgPackReader r)
    {
        var count = r.ReadArrayHeader();
        if (count < 7)
            throw new FormatException($"A type declaration has {count} fields; at least 7 are required.");

        var kind = r.ReadInt32();
        var id = Str(ref r);
        var clrMetadataName = Str(ref r);
        var ns = Str(ref r);
        var name = Str(ref r);
        var containing = Strings(ref r);
        var docs = r.ReadString();

        SchemaTypeDecl decl;
        int known;
        switch (kind)
        {
            case DeclObject when count >= 15:
                known = 15;
                decl = new SchemaObjectDecl(id, clrMetadataName, ns, name, containing, docs,
                    r.ReadBoolean(),
                    r.ReadBoolean(),
                    r.ReadBoolean(),
                    Strings(ref r),
                    r.ReadString(),
                    r.ReadInt32(),
                    r.ReadBoolean(),
                    Array(ref r, static (ref MsgPackReader x) =>
                    {
                        var n = Begin(ref x, 7, "member");
                        var member = new SchemaMember(
                            Str(ref x),
                            x.ReadInt32(),
                            ReadTypeRef(ref x) ?? throw new FormatException("Nil member type."),
                            x.ReadBoolean(),
                            x.ReadBoolean(),
                            ReadLiteral(ref x),
                            x.ReadString());
                        Skip(ref x, n, 7);
                        return member;
                    }));
                break;

            case DeclUnion when count >= 11:
                known = 11;
                decl = new SchemaUnionDecl(id, clrMetadataName, ns, name, containing, docs,
                    r.ReadBoolean(),
                    Strings(ref r),
                    r.ReadBoolean(),
                    Array(ref r, static (ref MsgPackReader x) =>
                    {
                        var n = Begin(ref x, 3, "union case");
                        var unionCase = new SchemaUnionCase(x.ReadUInt16(), Str(ref x), x.ReadInt32());
                        Skip(ref x, n, 3);
                        return unionCase;
                    }));
                break;

            case DeclEnum when count >= 10:
                known = 10;
                decl = new SchemaEnumDecl(id, clrMetadataName, ns, name, containing, docs,
                    (SchemaPrimKind)r.ReadByte(),
                    r.ReadBoolean(),
                    Array(ref r, static (ref MsgPackReader x) =>
                    {
                        var n = Begin(ref x, 2, "enum member");
                        var member = new SchemaEnumMember(Str(ref x), x.ReadInt64());
                        Skip(ref x, n, 2);
                        return member;
                    }));
                break;

            default:
                throw new FormatException($"Unknown declaration kind {kind} with {count} fields ({id}).");
        }

        Skip(ref r, count, known);
        return decl;
    }

    private static SchemaLiteral? ReadLiteral(ref MsgPackReader r)
    {
        if (r.TryReadNil())
            return null;
        var count = Begin(ref r, 2, "literal");
        var literal = new SchemaLiteral((SchemaLiteralKind)r.ReadByte(), r.ReadString());
        Skip(ref r, count, 2);
        return literal;
    }
}
