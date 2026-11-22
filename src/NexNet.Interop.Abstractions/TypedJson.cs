using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexNet.Serialization;

namespace NexNet.Interop;

/// <summary>
/// Converts between NexNet MessagePack values and the typed-JSON value model the harness uses for arguments and
/// results (the golden-vector value model):
/// <list type="bullet">
/// <item>null is <c>null</c>; booleans are booleans; strings, <c>Uri</c> and <c>Version</c> are strings;</item>
/// <item>integers are numbers, or strings beyond ±2^53; <c>char</c> is its UTF-16 code;</item>
/// <item>floats are bit-pattern strings: <c>"f32:0x3F800000"</c>, <c>"f64:0x…"</c>, <c>"f16:0x…"</c> (numbers are accepted on input);</item>
/// <item>bin is base64; ext 78 primitive arrays and sequences are arrays; maps are arrays of <c>[key, value]</c>; tuples are arrays;</item>
/// <item>objects are arrays by key (null in the gaps); unions are <c>[tag, value]</c>; enums are their underlying number;</item>
/// <item><c>DateTime</c> is its <c>ToBinary</c> value; <c>TimeSpan</c>/<c>TimeOnly</c> are ticks; <c>DateOnly</c> is its day number;
/// <c>DateTimeOffset</c> is <c>[utcTicks, offsetMinutes]</c>; <c>decimal</c> is <c>[lo, mid, hi, flags]</c>; <c>Guid</c> is
/// its "D" string; <c>BigInteger</c> is a decimal string;</item>
/// <item>types with a custom formatter are the base64 of their raw MessagePack value.</item>
/// </list>
/// </summary>
public static class TypedJson
{
    /// <summary>Largest integer carried as a JSON number (2^53 - 1).</summary>
    public const long MaxSafeInteger = 9007199254740991;

    private static readonly TypeSchema[] NoTypeArgs = Array.Empty<TypeSchema>();

    /// <summary>Converts one MessagePack value to typed JSON.</summary>
    /// <exception cref="FormatException">The value does not match the schema.</exception>
    public static JsonNode? FromMsgPack(ReadOnlyMemory<byte> data, TypeSchema schema, SchemaSet schemas)
    {
        var reader = new MsgPackReader(data);
        var node = Read(ref reader, schema, schemas);
        if (!reader.End)
            throw new FormatException("Trailing bytes after the " + schema + " value.");
        return node;
    }

    /// <summary>Converts a typed-JSON value to one MessagePack value.</summary>
    /// <exception cref="FormatException">The value does not match the schema.</exception>
    public static byte[] ToMsgPack(JsonNode? value, TypeSchema schema, SchemaSet schemas)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MsgPackWriter(buffer);
        Write(ref writer, value, schema, schemas);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    // ------------------------------------------------------------------ MessagePack -> JSON

    private static JsonNode? Read(ref MsgPackReader r, TypeSchema s, SchemaSet set)
    {
        if (s.Kind == TypeSchemaKind.TypeParam)
            throw new FormatException("Unbound type parameter T" + s.Ordinal + ".");
        if (r.TryReadNil())
            return null;

        switch (s.Kind)
        {
            case TypeSchemaKind.Prim:
                return ReadPrim(ref r, s.Prim);
            case TypeSchemaKind.Bin:
            {
                var bytes = r.ReadBinaryToArray();
                return bytes == null ? null : JsonValue.Create(Convert.ToBase64String(bytes));
            }
            case TypeSchemaKind.PrimArray:
                return ReadPrimArray(ref r, s.Prim);
            case TypeSchemaKind.Seq when BoundWireForm(s) == TypeSchemaKind.PrimArray:
                return ReadPrimArray(ref r, s.ArgsArray[0].Prim);
            case TypeSchemaKind.Seq when BoundWireForm(s) == TypeSchemaKind.Bin:
                return JsonValue.Create(Convert.ToBase64String(r.ReadBinaryToArray() ?? System.Array.Empty<byte>()));
            case TypeSchemaKind.Seq:
            case TypeSchemaKind.Set:
            case TypeSchemaKind.Queue:
            case TypeSchemaKind.Stack:
            {
                var count = r.ReadArrayHeader();
                var array = new JsonArray();
                for (var i = 0; i < count; i++)
                    Push(array, Read(ref r, Arg(s, 0), set));
                return array;
            }
            case TypeSchemaKind.Map:
            {
                var count = r.ReadMapHeader();
                var array = new JsonArray();
                for (var i = 0; i < count; i++)
                {
                    var key = Read(ref r, Arg(s, 0), set);
                    var value = Read(ref r, Arg(s, 1), set);
                    Push(array, new JsonArray(key, value));
                }
                return array;
            }
            case TypeSchemaKind.Tuple:
            {
                var count = r.ReadArrayHeader();
                var array = new JsonArray();
                for (var i = 0; i < count; i++)
                {
                    if (i < s.ArgsArray.Length)
                    {
                        Push(array, Read(ref r, s.ArgsArray[i], set));
                    }
                    else
                    {
                        r.Skip();
                        Push(array, null);
                    }
                }
                return array;
            }
            case TypeSchemaKind.Nullable:
                return Read(ref r, Arg(s, 0), set);
            case TypeSchemaKind.Decl:
                return ReadDecl(ref r, set.Find(s.DeclId!), s.ArgsArray, set);
            case TypeSchemaKind.External:
                return JsonValue.Create(Convert.ToBase64String(r.ReadRawValue().ToArray()));
            default:
                throw new FormatException("Unknown schema kind " + s.Kind + ".");
        }
    }

    private static JsonNode? ReadDecl(ref MsgPackReader r, DeclSchema decl, TypeSchema[] typeArgs, SchemaSet set)
    {
        switch (decl)
        {
            case ObjectSchema obj:
            {
                var count = r.ReadArrayHeader();
                var array = new JsonArray();
                for (var i = 0; i < count; i++)
                {
                    if (obj.TryGetMember(i, out var member))
                    {
                        Push(array, Read(ref r, member.Type.Bind(typeArgs), set));
                    }
                    else
                    {
                        r.Skip();
                        Push(array, null);
                    }
                }
                return array;
            }
            case UnionSchema union:
            {
                r.ReadArrayHeader(2);
                var tag = r.ReadUInt16();
                if (!union.TryGetCase(tag, out var unionCase))
                    throw new FormatException("Union '" + union.Id + "' has no case " + tag + ".");
                var value = Read(ref r, TypeSchema.Decl(unionCase.CaseId, typeArgs), set);
                return new JsonArray(JsonValue.Create(tag), value);
            }
            case EnumSchema enumSchema:
                return ReadPrim(ref r, enumSchema.Underlying);
            default:
                throw new FormatException("Unknown declared schema '" + decl.Id + "'.");
        }
    }

    private static JsonNode? ReadPrim(ref MsgPackReader r, SchemaPrim prim)
    {
        switch (prim)
        {
            case SchemaPrim.Bool:
                return JsonValue.Create(r.ReadBoolean());
            case SchemaPrim.SByte:
            case SchemaPrim.Byte:
            case SchemaPrim.Int16:
            case SchemaPrim.UInt16:
            case SchemaPrim.Int32:
            case SchemaPrim.UInt32:
            case SchemaPrim.Int64:
            case SchemaPrim.DateTime:
            case SchemaPrim.TimeSpan:
            case SchemaPrim.TimeOnly:
                return Integer(r.ReadInt64());
            case SchemaPrim.Char:
                return JsonValue.Create((int)r.ReadUInt16());
            case SchemaPrim.DateOnly:
                return Integer(r.ReadInt32());
            case SchemaPrim.UInt64:
                return Unsigned(r.ReadUInt64());
            case SchemaPrim.Single:
                return Single(r.ReadSingle());
            case SchemaPrim.Double:
                return Double(r.ReadDouble());
            case SchemaPrim.Half:
                return Half((Half)r.ReadSingle());
            case SchemaPrim.String:
            case SchemaPrim.Uri:
            case SchemaPrim.Version:
                return JsonValue.Create(r.ReadString());
            case SchemaPrim.DateTimeOffset:
            {
                r.ReadArrayHeader(2);
                var utcTicks = r.ReadInt64();
                var offsetMinutes = r.ReadInt16();
                return new JsonArray(Integer(utcTicks), JsonValue.Create(offsetMinutes));
            }
            case SchemaPrim.Decimal:
            {
                var bytes = new byte[16];
                ReadFixedBinary(ref r, bytes, "decimal");
                var array = new JsonArray();
                for (var i = 0; i < 4; i++)
                    Push(array, JsonValue.Create(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 4))));
                return array;
            }
            case SchemaPrim.Guid:
            {
                var bytes = new byte[16];
                ReadFixedBinary(ref r, bytes, "Guid");
                return JsonValue.Create(new Guid(bytes, bigEndian: true).ToString("D"));
            }
            case SchemaPrim.BigInteger:
            {
                var length = r.ReadBinaryHeader();
                var bytes = new byte[length];
                r.ReadRawBytes(bytes);
                return JsonValue.Create(new BigInteger(bytes, isUnsigned: false, isBigEndian: false).ToString(CultureInfo.InvariantCulture));
            }
            default:
                throw new FormatException("Unknown primitive " + prim + ".");
        }
    }

    private static void ReadFixedBinary(ref MsgPackReader r, Span<byte> destination, string what)
    {
        var length = r.ReadBinaryHeader();
        if (length != destination.Length)
            throw new FormatException(what + " must be " + destination.Length + " bytes.");
        r.ReadRawBytes(destination);
    }

    private static JsonNode ReadPrimArray(ref MsgPackReader r, SchemaPrim element)
    {
        var length = r.ReadExtHeader(out var typeCode);
        if (typeCode != NexusExtType.PrimitiveArray)
            throw new FormatException("Expected ext " + NexusExtType.PrimitiveArray + ", got ext " + typeCode + ".");
        if (length < 1)
            throw new FormatException("Primitive array without a kind byte.");

        var bytes = new byte[length];
        r.ReadRawBytes(bytes);
        if (bytes[0] != (byte)ToPrimitiveKind(element))
            throw new FormatException("Primitive array kind " + bytes[0] + " does not match " + element + ".");

        var size = ElementSize(element);
        if ((length - 1) % size != 0)
            throw new FormatException("Primitive array length is not a multiple of the element size.");

        var array = new JsonArray();
        for (var offset = 1; offset < length; offset += size)
            Push(array, ReadElement(element, bytes.AsSpan(offset, size)));
        return array;
    }

    private static JsonNode ReadElement(SchemaPrim element, ReadOnlySpan<byte> b) => element switch
    {
        SchemaPrim.SByte => Integer((sbyte)b[0]),
        SchemaPrim.Int16 => Integer(BinaryPrimitives.ReadInt16LittleEndian(b)),
        SchemaPrim.UInt16 => Integer(BinaryPrimitives.ReadUInt16LittleEndian(b)),
        SchemaPrim.Char => Integer(BinaryPrimitives.ReadUInt16LittleEndian(b)),
        SchemaPrim.Int32 => Integer(BinaryPrimitives.ReadInt32LittleEndian(b)),
        SchemaPrim.UInt32 => Integer(BinaryPrimitives.ReadUInt32LittleEndian(b)),
        SchemaPrim.Int64 => Integer(BinaryPrimitives.ReadInt64LittleEndian(b)),
        SchemaPrim.UInt64 => Unsigned(BinaryPrimitives.ReadUInt64LittleEndian(b)),
        SchemaPrim.Single => Single(BinaryPrimitives.ReadSingleLittleEndian(b)),
        SchemaPrim.Double => Double(BinaryPrimitives.ReadDoubleLittleEndian(b)),
        SchemaPrim.Half => Half(BinaryPrimitives.ReadHalfLittleEndian(b)),
        _ => throw new FormatException(element + " is not a primitive array element."),
    };

    private static JsonNode Integer(long value)
        => value is >= -MaxSafeInteger and <= MaxSafeInteger
            ? JsonValue.Create(value)
            : JsonValue.Create(value.ToString(CultureInfo.InvariantCulture));

    private static JsonNode Unsigned(ulong value)
        => value <= MaxSafeInteger
            ? JsonValue.Create((long)value)
            : JsonValue.Create(value.ToString(CultureInfo.InvariantCulture));

    private static JsonNode Single(float value)
        => JsonValue.Create("f32:0x" + BitConverter.SingleToUInt32Bits(value).ToString("X8", CultureInfo.InvariantCulture));

    private static JsonNode Double(double value)
        => JsonValue.Create("f64:0x" + BitConverter.DoubleToUInt64Bits(value).ToString("X16", CultureInfo.InvariantCulture));

    private static JsonNode Half(Half value)
        => JsonValue.Create("f16:0x" + BitConverter.HalfToUInt16Bits(value).ToString("X4", CultureInfo.InvariantCulture));

    // ------------------------------------------------------------------ JSON -> MessagePack

    private static void Write(ref MsgPackWriter w, JsonNode? v, TypeSchema s, SchemaSet set)
    {
        if (s.Kind == TypeSchemaKind.TypeParam)
            throw new FormatException("Unbound type parameter T" + s.Ordinal + ".");
        if (v is null)
        {
            w.WriteNil();
            return;
        }

        switch (s.Kind)
        {
            case TypeSchemaKind.Prim:
                WritePrim(ref w, v, s.Prim);
                break;
            case TypeSchemaKind.Bin:
                w.WriteBinary(Base64(v));
                break;
            case TypeSchemaKind.PrimArray:
                WritePrimArray(ref w, AsArray(v), s.Prim);
                break;
            case TypeSchemaKind.Seq when BoundWireForm(s) == TypeSchemaKind.PrimArray:
                WritePrimArray(ref w, AsArray(v), s.ArgsArray[0].Prim);
                break;
            case TypeSchemaKind.Seq when BoundWireForm(s) == TypeSchemaKind.Bin:
                w.WriteBinary(Base64(v));
                break;
            case TypeSchemaKind.Seq:
            case TypeSchemaKind.Set:
            case TypeSchemaKind.Queue:
            case TypeSchemaKind.Stack:
            {
                var array = AsArray(v);
                w.WriteArrayHeader(array.Count);
                foreach (var item in array)
                    Write(ref w, item, Arg(s, 0), set);
                break;
            }
            case TypeSchemaKind.Map:
            {
                var array = AsArray(v);
                w.WriteMapHeader(array.Count);
                foreach (var item in array)
                {
                    var pair = AsArray(item);
                    if (pair.Count != 2)
                        throw new FormatException("A map entry must be [key, value].");
                    Write(ref w, pair[0], Arg(s, 0), set);
                    Write(ref w, pair[1], Arg(s, 1), set);
                }
                break;
            }
            case TypeSchemaKind.Tuple:
            {
                var array = AsArray(v);
                if (array.Count != s.ArgsArray.Length)
                    throw new FormatException("A tuple of " + s.ArgsArray.Length + " needs " + s.ArgsArray.Length + " elements.");
                w.WriteArrayHeader(array.Count);
                for (var i = 0; i < array.Count; i++)
                    Write(ref w, array[i], s.ArgsArray[i], set);
                break;
            }
            case TypeSchemaKind.Nullable:
                Write(ref w, v, Arg(s, 0), set);
                break;
            case TypeSchemaKind.Decl:
                WriteDecl(ref w, v, set.Find(s.DeclId!), s.ArgsArray, set);
                break;
            case TypeSchemaKind.External:
                w.WriteRaw(Base64(v));
                break;
            default:
                throw new FormatException("Unknown schema kind " + s.Kind + ".");
        }
    }

    private static void WriteDecl(ref MsgPackWriter w, JsonNode v, DeclSchema decl, TypeSchema[] typeArgs, SchemaSet set)
    {
        switch (decl)
        {
            case ObjectSchema obj:
            {
                var array = AsArray(v);
                w.WriteArrayHeader(array.Count);
                for (var i = 0; i < array.Count; i++)
                {
                    if (obj.TryGetMember(i, out var member))
                        Write(ref w, array[i], member.Type.Bind(typeArgs), set);
                    else if (array[i] is null)
                        w.WriteNil();
                    else
                        throw new FormatException("'" + obj.Id + "' has no member with key " + i + ".");
                }
                break;
            }
            case UnionSchema union:
            {
                var array = AsArray(v);
                if (array.Count != 2)
                    throw new FormatException("A union value must be [tag, value].");
                var tag = checked((ushort)ParseInt64(array[0]));
                if (!union.TryGetCase(tag, out var unionCase))
                    throw new FormatException("Union '" + union.Id + "' has no case " + tag + ".");
                w.WriteArrayHeader(2);
                w.Write(tag);
                Write(ref w, array[1], TypeSchema.Decl(unionCase.CaseId, typeArgs), set);
                break;
            }
            case EnumSchema enumSchema:
                WritePrim(ref w, v, enumSchema.Underlying);
                break;
            default:
                throw new FormatException("Unknown declared schema '" + decl.Id + "'.");
        }
    }

    private static void WritePrim(ref MsgPackWriter w, JsonNode v, SchemaPrim prim)
    {
        switch (prim)
        {
            case SchemaPrim.Bool:
                w.Write(v.GetValue<bool>());
                break;
            case SchemaPrim.SByte:
                w.Write(checked((sbyte)ParseInt64(v)));
                break;
            case SchemaPrim.Byte:
                w.Write(checked((byte)ParseInt64(v)));
                break;
            case SchemaPrim.Int16:
                w.Write(checked((short)ParseInt64(v)));
                break;
            case SchemaPrim.UInt16:
            case SchemaPrim.Char:
                w.Write(checked((ushort)ParseInt64(v)));
                break;
            case SchemaPrim.Int32:
            case SchemaPrim.DateOnly:
                w.Write(checked((int)ParseInt64(v)));
                break;
            case SchemaPrim.UInt32:
                w.Write(checked((uint)ParseInt64(v)));
                break;
            case SchemaPrim.Int64:
            case SchemaPrim.DateTime:
            case SchemaPrim.TimeSpan:
            case SchemaPrim.TimeOnly:
                w.Write(ParseInt64(v));
                break;
            case SchemaPrim.UInt64:
                w.Write(ParseUInt64(v));
                break;
            case SchemaPrim.Single:
                w.Write(ParseSingle(v));
                break;
            case SchemaPrim.Double:
                w.Write(ParseDouble(v));
                break;
            case SchemaPrim.Half:
                w.Write((float)ParseHalf(v));
                break;
            case SchemaPrim.String:
            case SchemaPrim.Uri:
            case SchemaPrim.Version:
                w.Write(String(v));
                break;
            case SchemaPrim.DateTimeOffset:
            {
                var array = AsArray(v);
                if (array.Count != 2)
                    throw new FormatException("A DateTimeOffset must be [utcTicks, offsetMinutes].");
                w.WriteArrayHeader(2);
                w.Write(ParseInt64(array[0]));
                w.Write(checked((short)ParseInt64(array[1])));
                break;
            }
            case SchemaPrim.Decimal:
            {
                var array = AsArray(v);
                if (array.Count != 4)
                    throw new FormatException("A decimal must be [lo, mid, hi, flags].");
                var bytes = new byte[16];
                for (var i = 0; i < 4; i++)
                    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), unchecked((int)ParseInt64(array[i])));
                w.WriteBinary(bytes);
                break;
            }
            case SchemaPrim.Guid:
            {
                var bytes = new byte[16];
                Guid.Parse(String(v)).TryWriteBytes(bytes, bigEndian: true, out _);
                w.WriteBinary(bytes);
                break;
            }
            case SchemaPrim.BigInteger:
                w.WriteBinary(BigInteger.Parse(String(v), NumberStyles.Integer, CultureInfo.InvariantCulture).ToByteArray(isUnsigned: false, isBigEndian: false));
                break;
            default:
                throw new FormatException("Unknown primitive " + prim + ".");
        }
    }

    private static void WritePrimArray(ref MsgPackWriter w, JsonArray array, SchemaPrim element)
    {
        var size = ElementSize(element);
        var bytes = new byte[checked(1 + array.Count * size)];
        bytes[0] = (byte)ToPrimitiveKind(element);
        for (var i = 0; i < array.Count; i++)
        {
            var item = array[i] ?? throw new FormatException("A primitive array cannot hold null.");
            var b = bytes.AsSpan(1 + i * size, size);
            switch (element)
            {
                case SchemaPrim.SByte: b[0] = unchecked((byte)checked((sbyte)ParseInt64(item))); break;
                case SchemaPrim.Int16: BinaryPrimitives.WriteInt16LittleEndian(b, checked((short)ParseInt64(item))); break;
                case SchemaPrim.UInt16:
                case SchemaPrim.Char: BinaryPrimitives.WriteUInt16LittleEndian(b, checked((ushort)ParseInt64(item))); break;
                case SchemaPrim.Int32: BinaryPrimitives.WriteInt32LittleEndian(b, checked((int)ParseInt64(item))); break;
                case SchemaPrim.UInt32: BinaryPrimitives.WriteUInt32LittleEndian(b, checked((uint)ParseInt64(item))); break;
                case SchemaPrim.Int64: BinaryPrimitives.WriteInt64LittleEndian(b, ParseInt64(item)); break;
                case SchemaPrim.UInt64: BinaryPrimitives.WriteUInt64LittleEndian(b, ParseUInt64(item)); break;
                case SchemaPrim.Single: BinaryPrimitives.WriteSingleLittleEndian(b, ParseSingle(item)); break;
                case SchemaPrim.Double: BinaryPrimitives.WriteDoubleLittleEndian(b, ParseDouble(item)); break;
                case SchemaPrim.Half: BinaryPrimitives.WriteHalfLittleEndian(b, ParseHalf(item)); break;
                default: throw new FormatException(element + " is not a primitive array element.");
            }
        }

        w.WriteExtHeader(NexusExtType.PrimitiveArray, bytes.Length);
        w.WriteRaw(bytes);
    }

    // ------------------------------------------------------------------ helpers

    private static void Push(JsonArray array, JsonNode? node) => array.Add(node);

    /// <summary>
    /// The wire form of a sequence. A generic <c>T[]</c>/<c>List&lt;T&gt;</c>/<c>Memory&lt;T&gt;</c> member bound to a primitive
    /// resolves to the built-in formatter of the closed type at run time: the ext 78 primitive array (bin for
    /// <c>byte[]</c>/<c>Memory&lt;byte&gt;</c>), not an element-wise array.
    /// </summary>
    private static TypeSchemaKind BoundWireForm(TypeSchema seq)
    {
        if (seq.ArgsArray.Length != 1 || seq.ArgsArray[0].Kind != TypeSchemaKind.Prim)
            return TypeSchemaKind.Seq;

        var element = seq.ArgsArray[0].Prim;
        switch (seq.Clr)
        {
            case "Array":
            case "Memory":
            case "ReadOnlyMemory":
                if (element == SchemaPrim.Byte)
                    return TypeSchemaKind.Bin;
                return IsExtElement(element) ? TypeSchemaKind.PrimArray : TypeSchemaKind.Seq;
            case "List":
                return IsExtElement(element) ? TypeSchemaKind.PrimArray : TypeSchemaKind.Seq;
            default:
                return TypeSchemaKind.Seq;
        }
    }

    private static bool IsExtElement(SchemaPrim element) => element is SchemaPrim.SByte or SchemaPrim.Int16 or SchemaPrim.UInt16
        or SchemaPrim.Int32 or SchemaPrim.UInt32 or SchemaPrim.Int64 or SchemaPrim.UInt64 or SchemaPrim.Single or SchemaPrim.Double
        or SchemaPrim.Char or SchemaPrim.Half;

    private static TypeSchema Arg(TypeSchema s, int index)
        => index < s.ArgsArray.Length ? s.ArgsArray[index] : throw new FormatException(s.Kind + " schema is missing argument " + index + ".");

    private static JsonArray AsArray(JsonNode? v)
        => v as JsonArray ?? throw new FormatException("Expected a JSON array, got " + (v == null ? "null" : v.GetValueKind().ToString()) + ".");

    private static string String(JsonNode v)
        => v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : throw new FormatException("Expected a JSON string, got " + v.GetValueKind() + ".");

    private static byte[] Base64(JsonNode v) => Convert.FromBase64String(String(v));

    /// <summary>The digits of a JSON integer: a number or a string.</summary>
    private static string IntegerText(JsonNode? v)
    {
        if (v is null)
            throw new FormatException("Expected an integer, got null.");
        return v.GetValueKind() switch
        {
            JsonValueKind.Number => v.ToJsonString(),
            JsonValueKind.String => v.GetValue<string>(),
            var kind => throw new FormatException("Expected an integer, got " + kind + "."),
        };
    }

    private static long ParseInt64(JsonNode? v) => long.Parse(IntegerText(v), NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static ulong ParseUInt64(JsonNode? v) => ulong.Parse(IntegerText(v), NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static bool TryBits(JsonNode v, string prefix, out ulong bits)
    {
        bits = 0;
        if (v.GetValueKind() != JsonValueKind.String)
            return false;
        var text = v.GetValue<string>();
        if (!text.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        bits = ulong.Parse(text.AsSpan(prefix.Length), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return true;
    }

    private static double ParseNumber(JsonNode v)
        => v.GetValueKind() == JsonValueKind.Number
            ? double.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture)
            : throw new FormatException("Expected a float bit pattern (\"f32:0x…\", \"f64:0x…\", \"f16:0x…\") or a number.");

    private static double ParseDouble(JsonNode v)
    {
        if (TryBits(v, "f64:0x", out var bits))
            return BitConverter.UInt64BitsToDouble(bits);
        if (TryBits(v, "f32:0x", out bits))
            return BitConverter.UInt32BitsToSingle(checked((uint)bits));
        if (TryBits(v, "f16:0x", out bits))
            return (double)BitConverter.UInt16BitsToHalf(checked((ushort)bits));
        return ParseNumber(v);
    }

    private static float ParseSingle(JsonNode v)
    {
        if (TryBits(v, "f32:0x", out var bits))
            return BitConverter.UInt32BitsToSingle(checked((uint)bits));
        if (TryBits(v, "f16:0x", out bits))
            return (float)BitConverter.UInt16BitsToHalf(checked((ushort)bits));
        return (float)ParseDouble(v);
    }

    private static Half ParseHalf(JsonNode v)
        => TryBits(v, "f16:0x", out var bits)
            ? BitConverter.UInt16BitsToHalf(checked((ushort)bits))
            : (Half)ParseSingle(v);

    private static PrimitiveKind ToPrimitiveKind(SchemaPrim element) => element switch
    {
        SchemaPrim.SByte => PrimitiveKind.SByte,
        SchemaPrim.Int16 => PrimitiveKind.Int16,
        SchemaPrim.UInt16 => PrimitiveKind.UInt16,
        SchemaPrim.Int32 => PrimitiveKind.Int32,
        SchemaPrim.UInt32 => PrimitiveKind.UInt32,
        SchemaPrim.Int64 => PrimitiveKind.Int64,
        SchemaPrim.UInt64 => PrimitiveKind.UInt64,
        SchemaPrim.Single => PrimitiveKind.Single,
        SchemaPrim.Double => PrimitiveKind.Double,
        SchemaPrim.Char => PrimitiveKind.Char,
        SchemaPrim.Half => PrimitiveKind.Half,
        _ => throw new FormatException(element + " is not a primitive array element."),
    };

    private static int ElementSize(SchemaPrim element) => element switch
    {
        SchemaPrim.SByte => 1,
        SchemaPrim.Int16 or SchemaPrim.UInt16 or SchemaPrim.Char or SchemaPrim.Half => 2,
        SchemaPrim.Int32 or SchemaPrim.UInt32 or SchemaPrim.Single => 4,
        SchemaPrim.Int64 or SchemaPrim.UInt64 or SchemaPrim.Double => 8,
        _ => throw new FormatException(element + " is not a primitive array element."),
    };
}
