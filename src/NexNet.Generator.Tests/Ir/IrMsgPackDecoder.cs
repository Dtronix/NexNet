using NexNet.Generator.Ir;
using NexNet.Generator.Serialization;
using NexNet.Serialization;

namespace NexNet.Generator.Tests.Ir;

/// <summary>
/// Decodes the generator's MessagePack IR (<see cref="IrMsgPack"/>) with NexNet's own <see cref="MsgPackReader"/>, the
/// way the inspector's schema reader does. Reading every field back into the IR records proves the two agree.
/// </summary>
internal static class IrMsgPackDecoder
{
    public static NexusIr Decode(ReadOnlyMemory<byte> bytes)
    {
        var r = new MsgPackReader(bytes);
        r.ReadArrayHeader(3);
        var version = r.ReadInt32();
        var exports = Array(ref r, ReadExport);
        var types = Array(ref r, ReadDecl);
        if (!r.End)
            throw new FormatException("Trailing bytes after the IR.");
        return new NexusIr(version, exports, types);
    }

    private delegate T ReadItem<out T>(ref MsgPackReader reader);

    private static EquatableArray<T> Array<T>(ref MsgPackReader r, ReadItem<T> read)
        where T : IEquatable<T>
    {
        var count = r.ReadArrayHeader();
        var items = new T[count];
        for (var i = 0; i < count; i++)
            items[i] = read(ref r);
        return new EquatableArray<T>(items);
    }

    private static string Str(ref MsgPackReader r) => r.ReadString() ?? throw new FormatException("Unexpected nil string.");

    private static ExportIr ReadExport(ref MsgPackReader r)
    {
        r.ReadArrayHeader(8);
        var name = Str(ref r);
        var ns = Str(ref r);
        var targets = r.ReadInt32();
        var server = ReadInterface(ref r);
        var client = ReadInterface(ref r);
        r.ReadArrayHeader(3);
        var handshake = new HandshakeIr(r.ReadString(), r.ReadInt32(), r.ReadInt32());
        var accepted = Array(ref r, static (ref MsgPackReader x) =>
        {
            x.ReadArrayHeader(2);
            return new AcceptedIdIr(x.ReadInt32(), x.ReadUInt16());
        });
        return new ExportIr(name, ns, targets, server, client, handshake, accepted, r.ReadString(), null);
    }

    private static InterfaceIr ReadInterface(ref MsgPackReader r)
    {
        r.ReadArrayHeader(7);
        return new InterfaceIr(
            Str(ref r),
            Str(ref r),
            Str(ref r),
            r.ReadInt32(),
            Array(ref r, ReadMethod),
            Array(ref r, ReadCollection),
            Array(ref r, static (ref MsgPackReader x) =>
            {
                x.ReadArrayHeader(3);
                return new VersionIr(Str(ref x), x.ReadInt32(), Array(ref x, static (ref MsgPackReader y) => y.ReadUInt16()));
            }));
    }

    private static MethodIr ReadMethod(ref MsgPackReader r)
    {
        r.ReadArrayHeader(13);
        return new MethodIr(
            Str(ref r),
            r.ReadUInt16(),
            r.ReadInt32(),
            (ReturnKind)r.ReadByte(),
            ReadTypeRef(ref r),
            Array(ref r, static (ref MsgPackReader x) =>
            {
                x.ReadArrayHeader(5);
                return new ParamIr(Str(ref x), (ParamKind)x.ReadByte(), ReadTypeRef(ref x), x.ReadInt32(), x.ReadBoolean());
            }),
            r.ReadInt32(),
            r.ReadBoolean(),
            (PipeKind)r.ReadByte(),
            ReadTypeRef(ref r),
            r.ReadBoolean(),
            r.ReadBoolean(),
            r.ReadString(),
            null);
    }

    private static CollectionIr ReadCollection(ref MsgPackReader r)
    {
        r.ReadArrayHeader(8);
        return new CollectionIr(
            Str(ref r),
            r.ReadUInt16(),
            r.ReadInt32(),
            (CollectionKind)r.ReadByte(),
            (CollectionMode)r.ReadByte(),
            ReadTypeRef(ref r),
            r.ReadBoolean(),
            r.ReadString());
    }

    private static TypeRef? ReadTypeRef(ref MsgPackReader r)
    {
        if (r.TryReadNil())
            return null;
        r.ReadArrayHeader(9);
        var kind = (TypeRefKind)r.ReadByte();
        return new TypeRef(kind)
        {
            Prim = (PrimKind)r.ReadByte(),
            Clr = r.ReadString(),
            Args = Array(ref r, static (ref MsgPackReader x) => ReadTypeRef(ref x) ?? throw new FormatException("Nil type argument.")),
            DeclId = r.ReadString(),
            Ordinal = r.ReadInt32(),
            RefNullable = r.ReadBoolean(),
            JsNumber = r.ReadBoolean(),
            NativeFormatters = Array(ref r, static (ref MsgPackReader x) =>
            {
                x.ReadArrayHeader(2);
                return new NativeFormatterIr(x.ReadInt32(), Str(ref x));
            }),
        };
    }

    private static EquatableArray<string> Strings(ref MsgPackReader r) => Array(ref r, static (ref MsgPackReader x) => Str(ref x));

    private static TypeDeclIr ReadDecl(ref MsgPackReader r)
    {
        var length = r.ReadArrayHeader();
        var kind = r.ReadInt32();
        var id = Str(ref r);
        var clrMetadataName = Str(ref r);
        var ns = Str(ref r);
        var name = Str(ref r);
        var containing = Strings(ref r);
        var docs = r.ReadString();

        switch (kind)
        {
            case IrMsgPack.DeclObject when length == 15:
                return new ObjectDeclIr(id, clrMetadataName, ns, name, containing, docs,
                    r.ReadBoolean(),
                    r.ReadBoolean(),
                    r.ReadBoolean(),
                    Strings(ref r),
                    r.ReadString(),
                    r.ReadInt32(),
                    r.ReadBoolean(),
                    Array(ref r, static (ref MsgPackReader x) =>
                    {
                        x.ReadArrayHeader(7);
                        return new MemberIr(
                            Str(ref x),
                            x.ReadInt32(),
                            ReadTypeRef(ref x) ?? throw new FormatException("Nil member type."),
                            x.ReadBoolean(),
                            x.ReadBoolean(),
                            ReadLiteral(ref x),
                            x.ReadString());
                    }));

            case IrMsgPack.DeclUnion when length == 11:
                return new UnionDeclIr(id, clrMetadataName, ns, name, containing, docs,
                    r.ReadBoolean(),
                    Strings(ref r),
                    r.ReadBoolean(),
                    Array(ref r, static (ref MsgPackReader x) =>
                    {
                        x.ReadArrayHeader(3);
                        return new UnionCaseIr(x.ReadUInt16(), Str(ref x), x.ReadInt32());
                    }));

            case IrMsgPack.DeclEnum when length == 10:
                return new EnumDeclIr(id, clrMetadataName, ns, name, containing, docs,
                    (PrimKind)r.ReadByte(),
                    r.ReadBoolean(),
                    Array(ref r, static (ref MsgPackReader x) =>
                    {
                        x.ReadArrayHeader(2);
                        return new EnumMemberIr(Str(ref x), x.ReadInt64());
                    }));
        }

        throw new FormatException($"Unknown declaration kind {kind} with {length} fields.");
    }

    private static LiteralIr? ReadLiteral(ref MsgPackReader r)
    {
        if (r.TryReadNil())
            return null;
        r.ReadArrayHeader(2);
        return new LiteralIr((LiteralKind)r.ReadByte(), r.ReadString());
    }

    /// <summary>
    /// Copies any MessagePack value tree with NexNet's <see cref="MsgPackWriter"/>. Its output is canonical (smallest
    /// headers, unsigned family for non-negative integers), so a copy equal to the input proves the input is too.
    /// </summary>
    public static byte[] CanonicalCopy(ReadOnlyMemory<byte> bytes)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        var reader = new MsgPackReader(bytes);
        var writer = new MsgPackWriter(buffer);
        while (!reader.End)
            CopyValue(ref reader, ref writer);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void CopyValue(ref MsgPackReader r, ref MsgPackWriter w)
    {
        var code = r.PeekCode();
        if (code == MsgPackCode.Nil)
        {
            r.ReadNil();
            w.WriteNil();
        }
        else if (code is MsgPackCode.True or MsgPackCode.False)
        {
            w.Write(r.ReadBoolean());
        }
        else if (code is (>= MsgPackCode.MinFixArray and <= MsgPackCode.MaxFixArray) or MsgPackCode.Array16 or MsgPackCode.Array32)
        {
            var count = r.ReadArrayHeader();
            w.WriteArrayHeader(count);
            for (var i = 0; i < count; i++)
                CopyValue(ref r, ref w);
        }
        else if (code is (>= MsgPackCode.MinFixStr and <= MsgPackCode.MaxFixStr) or MsgPackCode.Str8 or MsgPackCode.Str16 or MsgPackCode.Str32)
        {
            w.Write(r.ReadString());
        }
        else if (code == MsgPackCode.UInt64)
        {
            w.WriteUInt64Forced(r.ReadUInt64());
        }
        else
        {
            // Every other code the IR uses is an integer; anything else throws here.
            w.Write(r.ReadInt64());
        }
    }
}
