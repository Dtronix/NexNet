using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace NexNet.Inspector.Core.Schema;

/// <summary>
/// Walks one value of known schema type and writes its fields. It never throws on bad bytes: a value of the wrong
/// shape is skipped whole (<see cref="FieldKind.Unknown"/>), and bytes it cannot even skip become one
/// <see cref="FieldKind.Raw"/> field to the end, after which nothing else is decoded.
/// </summary>
internal sealed class FieldWriter
{
    private const sbyte PrimitiveArrayExt = 78;

    private static readonly string[] ItemNames =
    [
        "Item1", "Item2", "Item3", "Item4", "Item5", "Item6", "Item7", "Item8",
        "Item9", "Item10", "Item11", "Item12", "Item13", "Item14", "Item15", "Item16",
    ];

    private static readonly string[] ResetStartNames = ["Flags", "Version", "TotalValues"];
    private static readonly string[] ResetValuesNames = ["Flags", "Values"];
    private static readonly string[] FlagsOnlyNames = ["Flags"];
    private static readonly string[] ClearNames = ["Flags", "Version"];
    private static readonly string[] InsertNames = ["Flags", "Version", "Index", "Value"];
    private static readonly string[] MoveNames = ["Flags", "Version", "FromIndex", "ToIndex"];
    private static readonly string[] RemoveNames = ["Flags", "Version", "Index"];

    private static readonly string[] CollectionTagNames =
        ["ResetStart", "ResetValues", "ResetComplete", "Clear", "Insert", "Replace", "Move", "Remove", "Noop"];

    private static readonly SchemaTypeRef NullableInt32 =
        new(SchemaTypeKind.Nullable, args: [SchemaTypeRef.Primitive(SchemaPrimKind.Int32)]);

    private readonly ReadOnlyMemory<byte> _memory;
    private readonly SchemaDecoderOptions _options;
    private readonly List<DecodedField> _fields = [];
    private readonly List<int> _lastChild = [];
    private List<TypeArgs>? _contexts;
    private int _lastTop = -1;
    private int _pos;
    private bool _stopped;

    public FieldWriter(ReadOnlyMemory<byte> memory, SchemaDecoderOptions options)
    {
        _memory = memory;
        _options = options;
    }

    public List<DecodedField> Fields => _fields;

    /// <summary>Covers whatever is left after the root value as trailing bytes.</summary>
    public void Finish()
    {
        if (!_stopped && _pos < _memory.Length)
            RawRest(-1, null, -1, DecodeProblem.TrailingBytes);
    }

    public void Raw(DecodeProblem problem) => RawRest(-1, null, -1, problem);

    public int Value(SchemaTypeRef? type, string? name) => Value(type, -1, -1, name, -1, 0);

    /// <summary>
    /// Walks one value by its MessagePack shape alone (no schema): scalars by their wire type, arrays as sequences, maps
    /// as key/value entries, ext values skipped whole.
    /// </summary>
    public int Walk(string? name) => Walk(-1, name, -1, 0);

    private int Walk(int parent, string? name, int element, int depth)
    {
        if (!TryToken(parent, name, element, out var token))
            return -1;
        if (_fields.Count >= _options.MaxFields || depth > _options.MaxDepth)
            return SkipValue(FieldKind.Unknown, parent, name, element, null, null, DecodeProblem.LimitReached);

        switch (token.Type)
        {
            case TokenType.Nil:
                return Leaf(FieldKind.Nil, token, default, parent, name, element, null, null);
            case TokenType.Boolean:
                return Leaf(FieldKind.Boolean, token, new DecodedScalar(token.Bits, 0), parent, name, element, null, null);
            case TokenType.UInt:
                return Leaf(FieldKind.UnsignedInteger, token, new DecodedScalar(token.Bits, 0), parent, name, element, null, null);
            case TokenType.Int:
                return Leaf(FieldKind.Integer, token, new DecodedScalar(token.Bits, 0), parent, name, element, null, null);
            case TokenType.Float32:
                return Leaf(FieldKind.Single, token, new DecodedScalar(token.Bits, 0), parent, name, element, null, null);
            case TokenType.Float64:
                return Leaf(FieldKind.Double, token, new DecodedScalar(token.Bits, 0), parent, name, element, null, null);
            case TokenType.Str:
                return Leaf(FieldKind.String, token, default, parent, name, element, null, null);
            case TokenType.Bin:
                return Leaf(FieldKind.Binary, token, default, parent, name, element, null, null);
            case TokenType.Array:
            {
                var index = Open(FieldKind.Sequence, parent, name, element, token, new DecodedScalar(token.Count, 0), null, null);
                for (var i = 0; i < token.Count && !_stopped; i++)
                    Walk(index, null, i, depth + 1);
                return Close(index);
            }

            case TokenType.Map:
            {
                var index = Open(FieldKind.Map, parent, name, element, token, new DecodedScalar(token.Count, 0), null, null);
                for (var i = 0; i < token.Count && !_stopped; i++)
                {
                    var entry = Add(FieldKind.MapEntry, index, null, i, _pos, 0, 0, default, null, null, DecodeProblem.None);
                    Walk(entry, "Key", 0, depth + 2);
                    Walk(entry, "Value", 1, depth + 2);
                    Close(entry);
                }

                return Close(index);
            }

            default:
                // An ext value has no meaning without the type that wrote it.
                return SkipValue(FieldKind.Unknown, parent, name, element, null, null, DecodeProblem.UnknownType);
        }
    }

    public void Arguments(SchemaMethod method)
    {
        if (!TryToken(-1, method.Name, -1, out var token))
            return;
        if (token.Type != TokenType.Array)
        {
            SkipValue(FieldKind.Unknown, -1, method.Name, -1, null, null, DecodeProblem.TypeMismatch);
            return;
        }

        var root = Open(FieldKind.Arguments, -1, method.Name, -1, token, default, null, null);
        for (var i = 0; i < token.Count && !_stopped; i++)
        {
            var parameter = method.GetWireParam(i);
            if (parameter is null)
                Value(null, -1, root, null, i, 1);
            else if (parameter.Kind is SchemaParamKind.DuplexPipe or SchemaParamKind.DuplexChannel)
                PipeId(root, parameter.Name, i, parameter.Type);
            else
                Value(parameter.Type, -1, root, parameter.Name, i, 1);
        }

        Close(root);
    }

    /// <summary>A collection's connect invocation: its arguments are the pipe ID alone (spec §7.6).</summary>
    public void CollectionArguments(SchemaCollection collection)
    {
        if (!TryToken(-1, collection.Name, -1, out var token))
            return;
        if (token.Type != TokenType.Array)
        {
            SkipValue(FieldKind.Unknown, -1, collection.Name, -1, null, null, DecodeProblem.TypeMismatch);
            return;
        }

        var root = Open(FieldKind.Arguments, -1, collection.Name, -1, token, default, null, null);
        for (var i = 0; i < token.Count && !_stopped; i++)
        {
            if (i == 0)
                PipeId(root, "pipe", 0, collection.Item);
            else
                Value(null, -1, root, null, i, 1);
        }

        Close(root);
    }

    /// <summary>An Exception result's detail: <c>[code: int32|nil, type: str|nil, message: str|nil]</c> (spec §4.4).</summary>
    public void ExceptionDetail()
    {
        if (!TryToken(-1, null, -1, out var token))
            return;
        if (token.Type != TokenType.Array)
        {
            SkipValue(FieldKind.Unknown, -1, null, -1, null, null, DecodeProblem.TypeMismatch);
            return;
        }

        var root = Open(FieldKind.ExceptionDetail, -1, "Exception", -1, token, default, null, null);
        var text = SchemaTypeRef.Primitive(SchemaPrimKind.String);
        for (var i = 0; i < token.Count && !_stopped; i++)
        {
            var (type, name) = i switch
            {
                0 => (NullableInt32, "Code"),
                1 => (text, "Type"),
                2 => (text, "Message"),
                _ => ((SchemaTypeRef?)null, (string?)null),
            };
            Value(type, -1, root, name, i, 1);
        }

        Close(root);
    }

    /// <summary>A collection message (spec §4.11): <c>[tag, [flags, ...]]</c>.</summary>
    public void CollectionMessage(SchemaTypeRef? item, SchemaTypeRef? values)
    {
        if (!TryToken(-1, null, -1, out var token))
            return;
        if (token.Type != TokenType.Array || token.Count != 2)
        {
            SkipValue(FieldKind.Unknown, -1, null, -1, null, null, DecodeProblem.TypeMismatch);
            return;
        }

        var root = Open(FieldKind.CollectionMessage, -1, null, -1, token, default, null, null);
        if (!TryToken(root, "Tag", 0, out var tagToken))
        {
            Close(root);
            return;
        }

        if (!tagToken.IsInteger)
        {
            SkipValue(FieldKind.Unknown, root, "Tag", 0, null, null, DecodeProblem.TypeMismatch);
            Value(null, -1, root, null, 1, 1);
            Close(root);
            return;
        }

        var tag = tagToken.Bits;
        var tagName = (ulong)tag < (ulong)CollectionTagNames.Length ? CollectionTagNames[tag] : null;
        Leaf(FieldKind.UnionTag, tagToken, new DecodedScalar(tag, 0), root, "Tag", 0, SchemaTypeRef.Primitive(SchemaPrimKind.UInt16), null,
            tagName is null ? DecodeProblem.UnknownTag : DecodeProblem.None);
        ref var rootField = ref Field(root);
        rootField.Value = new DecodedScalar(tag, 0);
        rootField.Name = tagName;

        if (!TryToken(root, tagName, 1, out var bodyToken))
        {
            Close(root);
            return;
        }

        if (bodyToken.Type != TokenType.Array)
        {
            SkipValue(FieldKind.Unknown, root, tagName, 1, null, null, DecodeProblem.TypeMismatch);
            Close(root);
            return;
        }

        var names = tag switch
        {
            0 => ResetStartNames,
            1 => ResetValuesNames,
            2 or 8 => FlagsOnlyNames,
            3 => ClearNames,
            4 or 5 => InsertNames,
            6 => MoveNames,
            7 => RemoveNames,
            _ => Array.Empty<string>(),
        };
        var body = Open(FieldKind.CollectionBody, root, tagName, 1, bodyToken, default, null, null);
        for (var i = 0; i < bodyToken.Count && !_stopped; i++)
        {
            var name = i < names.Length ? names[i] : null;
            var type = name switch
            {
                null => null,
                "Flags" => SchemaTypeRef.Primitive(SchemaPrimKind.Byte),
                "Value" => item,
                "Values" => values,
                _ => SchemaTypeRef.Primitive(SchemaPrimKind.Int32),
            };
            Value(type, -1, body, name, i, 2);
        }

        Close(body);
        Close(root);
    }

    private int Value(SchemaTypeRef? type, int ctx, int parent, string? name, int element, int depth)
    {
        if (!TryToken(parent, name, element, out var token))
            return -1;

        // Type parameters resolve against the type arguments of the enclosing generic declarations.
        while (type is { Kind: SchemaTypeKind.TypeParam })
        {
            if (ctx < 0 || _contexts is null)
            {
                type = null;
                break;
            }

            var frame = _contexts[ctx];
            type = type.Ordinal < frame.Args.Count ? frame.Args[type.Ordinal] : null;
            ctx = frame.Parent;
        }

        if (token.Type == TokenType.Nil)
            return Leaf(FieldKind.Nil, token, default, parent, name, element, type, null);
        if (type is null)
            return SkipValue(FieldKind.Unknown, parent, name, element, null, null, DecodeProblem.UnknownType);
        if (_fields.Count >= _options.MaxFields || depth > _options.MaxDepth)
            return SkipValue(FieldKind.Unknown, parent, name, element, type, null, DecodeProblem.LimitReached);

        switch (type.Kind)
        {
            case SchemaTypeKind.Prim:
                return Primitive(type, token, parent, name, element);

            case SchemaTypeKind.Bin:
                return IsBytes(token)
                    ? Leaf(FieldKind.Binary, token, default, parent, name, element, type, null)
                    : Mismatch(parent, name, element, type);

            case SchemaTypeKind.PrimArray:
                if (token.Type == TokenType.Ext)
                    return PrimitiveArray(token, parent, name, element, type);
                return token.Type == TokenType.Array
                    ? Elements(token, SchemaTypeRef.Primitive(type.Prim), -1, parent, name, element, type, depth)
                    : Mismatch(parent, name, element, type);

            case SchemaTypeKind.Seq:
            case SchemaTypeKind.Set:
            case SchemaTypeKind.Queue:
            case SchemaTypeKind.Stack:
            {
                var elementType = type.Args.Count > 0 ? type.Args[0] : null;
                if (token.Type == TokenType.Array)
                    return Elements(token, elementType, ctx, parent, name, element, type, depth);
                return token.Type == TokenType.Ext
                    ? PrimitiveArray(token, parent, name, element, type)
                    : Mismatch(parent, name, element, type);
            }

            case SchemaTypeKind.Map:
                return token.Type == TokenType.Map
                    ? Map(token, type, ctx, parent, name, element, depth)
                    : Mismatch(parent, name, element, type);

            case SchemaTypeKind.Tuple:
                return token.Type == TokenType.Array
                    ? Tuple(token, type, ctx, parent, name, element, depth)
                    : Mismatch(parent, name, element, type);

            case SchemaTypeKind.Nullable:
                return Value(type.Args.Count > 0 ? type.Args[0] : null, ctx, parent, name, element, depth);

            case SchemaTypeKind.Decl:
                if (type.Decl is not { } decl)
                    return SkipValue(FieldKind.Unknown, parent, name, element, type, null, DecodeProblem.UnknownType);
                return Declared(decl, PushArgs(type.Args, ctx), token, parent, name, element, type, depth);

            case SchemaTypeKind.External:
                return SkipValue(FieldKind.External, parent, name, element, type, null, DecodeProblem.None);

            default:
                return SkipValue(FieldKind.Unknown, parent, name, element, type, null, DecodeProblem.UnknownType);
        }
    }

    private int Declared(SchemaTypeDecl decl, int ctx, in MsgPackToken token, int parent, string? name, int element,
        SchemaTypeRef? type, int depth)
    {
        switch (decl)
        {
            case SchemaObjectDecl obj:
            {
                if (token.Type != TokenType.Array)
                    return Mismatch(parent, name, element, type, decl);

                var index = Open(FieldKind.Object, parent, name, element, token, default, type, decl);
                // Unused keys are nil, and keys past the known ones belong to a newer peer (spec §10.2): readers skip
                // both, so a value there is not a problem, only one the schema cannot describe.
                for (var i = 0; i < token.Count && !_stopped; i++)
                {
                    if (obj.GetMember(i) is { } member)
                        Value(member.Type, ctx, index, member.Name, i, depth + 1);
                    else if (TryToken(index, null, i, out var unused))
                        Unused(unused, index, i);
                }

                return Close(index);
            }

            case SchemaUnionDecl union:
            {
                if (token.Type != TokenType.Array || token.Count != 2)
                    return Mismatch(parent, name, element, type, decl);

                var index = Open(FieldKind.Union, parent, name, element, token, default, type, decl);
                if (!TryToken(index, "Tag", 0, out var tagToken))
                    return Close(index);

                if (!tagToken.IsInteger || (ulong)tagToken.Bits > ushort.MaxValue)
                {
                    SkipValue(FieldKind.Unknown, index, "Tag", 0, null, null, DecodeProblem.TypeMismatch);
                    Value(null, -1, index, null, 1, depth + 1);
                    return Close(index);
                }

                var tag = (ushort)tagToken.Bits;
                var known = union.TryGetCase(tag, out var unionCase) && unionCase.Decl is not null;
                Leaf(FieldKind.UnionTag, tagToken, new DecodedScalar(tag, 0), index, "Tag", 0,
                    SchemaTypeRef.Primitive(SchemaPrimKind.UInt16), null, known ? DecodeProblem.None : DecodeProblem.UnknownTag);
                Field(index).Value = new DecodedScalar(tag, 0);

                if (!known)
                {
                    SkipValue(FieldKind.Unknown, index, null, 1, null, null, DecodeProblem.UnknownTag);
                    return Close(index);
                }

                var caseDecl = unionCase!.Decl!;
                if (TryToken(index, caseDecl.Name, 1, out var caseToken))
                {
                    // The cases share the union's type parameters, so they decode in its context.
                    if (caseToken.Type == TokenType.Nil)
                        Leaf(FieldKind.Nil, caseToken, default, index, caseDecl.Name, 1, null, caseDecl);
                    else
                        Declared(caseDecl, ctx, caseToken, index, caseDecl.Name, 1, null, depth + 1);
                }

                return Close(index);
            }

            case SchemaEnumDecl:
                return token.IsInteger
                    ? Leaf(FieldKind.Enum, token, new DecodedScalar(token.Bits, 0), parent, name, element, type, decl)
                    : Mismatch(parent, name, element, type, decl);

            default:
                return SkipValue(FieldKind.Unknown, parent, name, element, type, decl, DecodeProblem.UnknownType);
        }
    }

    private int Primitive(SchemaTypeRef type, in MsgPackToken token, int parent, string? name, int element)
    {
        var span = _memory.Span;
        switch (type.Prim)
        {
            case SchemaPrimKind.Bool when token.Type == TokenType.Boolean:
                return Leaf(FieldKind.Boolean, token, new DecodedScalar(token.Bits, 0), parent, name, element, type, null);

            case SchemaPrimKind.SByte or SchemaPrimKind.Int16 or SchemaPrimKind.Int32 or SchemaPrimKind.Int64 when token.IsInteger:
                return Leaf(FieldKind.Integer, token, new DecodedScalar(token.Bits, 0), parent, name, element, type, null);

            case SchemaPrimKind.Byte or SchemaPrimKind.UInt16 or SchemaPrimKind.UInt32 or SchemaPrimKind.UInt64 when token.IsInteger:
                return Leaf(FieldKind.UnsignedInteger, token, new DecodedScalar(token.Bits, 0), parent, name, element, type, null);

            case SchemaPrimKind.Char when token.IsInteger:
                return Leaf(FieldKind.Char, token, new DecodedScalar(token.Bits, 0), parent, name, element, type, null);

            case SchemaPrimKind.Single when IsNumber(token):
            {
                // A float32 keeps its exact bits (NaN payloads included); other forms are converted.
                var bits = token.Type == TokenType.Float32 ? (int)token.Bits : BitConverter.SingleToInt32Bits((float)ToDouble(token));
                return Leaf(FieldKind.Single, token, new DecodedScalar(bits, 0), parent, name, element, type, null);
            }

            case SchemaPrimKind.Double when IsNumber(token):
            {
                var bits = token.Type == TokenType.Float64 ? token.Bits : BitConverter.DoubleToInt64Bits(ToDouble(token));
                return Leaf(FieldKind.Double, token, new DecodedScalar(bits, 0), parent, name, element, type, null);
            }

            case SchemaPrimKind.Half when IsNumber(token):
            {
                var half = token.Type == TokenType.Float32 ? (Half)BitConverter.Int32BitsToSingle((int)token.Bits) : (Half)ToDouble(token);
                return Leaf(FieldKind.Half, token, new DecodedScalar(BitConverter.HalfToUInt16Bits(half), 0), parent, name, element, type, null);
            }

            case SchemaPrimKind.Decimal when IsBytes(token) && token.Length - token.HeaderLength == 16:
            {
                var data = span.Slice(_pos + token.HeaderLength, 16);
                var value = new DecodedScalar(BinaryPrimitives.ReadInt64LittleEndian(data), BinaryPrimitives.ReadInt64LittleEndian(data.Slice(8)));
                return Leaf(FieldKind.Decimal, token, value, parent, name, element, type, null);
            }

            case SchemaPrimKind.String when token.Type == TokenType.Str:
                return Leaf(FieldKind.String, token, default, parent, name, element, type, null);

            case SchemaPrimKind.Uri when token.Type == TokenType.Str:
                return Leaf(FieldKind.Uri, token, default, parent, name, element, type, null);

            case SchemaPrimKind.Version when token.Type == TokenType.Str:
                return Leaf(FieldKind.Version, token, default, parent, name, element, type, null);

            case SchemaPrimKind.DateTime when token.IsInteger:
                return Leaf(FieldKind.DateTime, token, new DecodedScalar(token.Bits, 0), parent, name, element, type, null);

            case SchemaPrimKind.TimeSpan when token.IsInteger:
                return Leaf(FieldKind.TimeSpan, token, new DecodedScalar(token.Bits, 0), parent, name, element, type, null);

            case SchemaPrimKind.DateOnly when token.IsInteger:
                return Leaf(FieldKind.DateOnly, token, new DecodedScalar(token.Bits, 0), parent, name, element, type, null);

            case SchemaPrimKind.TimeOnly when token.IsInteger:
                return Leaf(FieldKind.TimeOnly, token, new DecodedScalar(token.Bits, 0), parent, name, element, type, null);

            case SchemaPrimKind.DateTimeOffset when token.Type == TokenType.Array && token.Count == 2:
            {
                // [UtcTicks, offsetMinutes] is one value to a reader: kept as a single leaf over both integers.
                var at = _pos + token.HeaderLength;
                if (MsgPackToken.TryRead(span, at, out var ticks) != TokenResult.Ok || !ticks.IsInteger
                    || MsgPackToken.TryRead(span, at + ticks.Length, out var offset) != TokenResult.Ok || !offset.IsInteger)
                    return Mismatch(parent, name, element, type);

                var index = Add(FieldKind.DateTimeOffset, parent, name, element, _pos, token.HeaderLength + ticks.Length + offset.Length,
                    token.HeaderLength, new DecodedScalar(ticks.Bits, offset.Bits), type, null, DecodeProblem.None);
                _pos += token.HeaderLength + ticks.Length + offset.Length;
                return index;
            }

            case SchemaPrimKind.Guid when IsBytes(token) && token.Length - token.HeaderLength == 16:
                return Leaf(FieldKind.Guid, token, default, parent, name, element, type, null);

            case SchemaPrimKind.BigInteger when IsBytes(token):
                return Leaf(FieldKind.BigInteger, token, default, parent, name, element, type, null);

            default:
                return Mismatch(parent, name, element, type);
        }
    }

    private int PrimitiveArray(in MsgPackToken token, int parent, string? name, int element, SchemaTypeRef type)
    {
        var data = _memory.Span.Slice(_pos + token.HeaderLength, token.Length - token.HeaderLength);
        var size = data.Length > 0 ? PrimitiveArrays.ElementSize(data[0]) : 0;
        if (token.ExtType != PrimitiveArrayExt || size == 0 || (data.Length - 1) % size != 0)
            return Mismatch(parent, name, element, type);

        var index = Add(FieldKind.PrimitiveArray, parent, name, element, _pos, token.Length, token.HeaderLength + 1,
            new DecodedScalar((data.Length - 1) / size, data[0]), type, null, DecodeProblem.None);
        _pos += token.Length;
        return index;
    }

    private int Elements(in MsgPackToken token, SchemaTypeRef? elementType, int ctx, int parent, string? name, int element,
        SchemaTypeRef type, int depth)
    {
        var index = Open(FieldKind.Sequence, parent, name, element, token, new DecodedScalar(token.Count, 0), type, null);
        for (var i = 0; i < token.Count && !_stopped; i++)
            Value(elementType, ctx, index, null, i, depth + 1);
        return Close(index);
    }

    private int Map(in MsgPackToken token, SchemaTypeRef type, int ctx, int parent, string? name, int element, int depth)
    {
        var keyType = type.Args.Count > 0 ? type.Args[0] : null;
        var valueType = type.Args.Count > 1 ? type.Args[1] : null;
        var index = Open(FieldKind.Map, parent, name, element, token, new DecodedScalar(token.Count, 0), type, null);
        for (var i = 0; i < token.Count && !_stopped; i++)
        {
            var entry = Add(FieldKind.MapEntry, index, null, i, _pos, 0, 0, default, null, null, DecodeProblem.None);
            Value(keyType, ctx, entry, "Key", 0, depth + 2);
            Value(valueType, ctx, entry, "Value", 1, depth + 2);
            Close(entry);
        }

        return Close(index);
    }

    private int Tuple(in MsgPackToken token, SchemaTypeRef type, int ctx, int parent, string? name, int element, int depth)
    {
        var keyValue = type.IsKeyValuePair;
        var index = Open(FieldKind.Tuple, parent, name, element, token, new DecodedScalar(token.Count, 0), type, null);
        for (var i = 0; i < token.Count && !_stopped; i++)
        {
            var itemName = keyValue ? (i == 0 ? "Key" : i == 1 ? "Value" : null) : i < ItemNames.Length ? ItemNames[i] : null;
            Value(i < type.Args.Count ? type.Args[i] : null, ctx, index, itemName, i, depth + 1);
        }

        return Close(index);
    }

    private void Unused(in MsgPackToken token, int parent, int element)
    {
        if (token.Type == TokenType.Nil)
            Leaf(FieldKind.Nil, token, default, parent, null, element, null, null);
        else
            SkipValue(FieldKind.Unknown, parent, null, element, null, null, DecodeProblem.None);
    }

    private void PipeId(int parent, string name, int element, SchemaTypeRef? itemType)
    {
        if (!TryToken(parent, name, element, out var token))
            return;
        if (token.IsInteger)
            Leaf(FieldKind.PipeId, token, new DecodedScalar(token.Bits, 0), parent, name, element, itemType, null);
        else
            Mismatch(parent, name, element, itemType);
    }

    private int PushArgs(IReadOnlyList<SchemaTypeRef> args, int parent)
    {
        if (args.Count == 0)
            return -1;
        _contexts ??= [];
        _contexts.Add(new TypeArgs(args, parent));
        return _contexts.Count - 1;
    }

    // Reads the next header; on bytes that cannot be read, covers the rest as raw and stops.
    private bool TryToken(int parent, string? name, int element, out MsgPackToken token)
    {
        token = default;
        if (_stopped)
            return false;

        var result = MsgPackToken.TryRead(_memory.Span, _pos, out token);
        if (result == TokenResult.Ok)
            return true;

        RawRest(parent, name, element, result == TokenResult.Truncated ? DecodeProblem.Truncated : DecodeProblem.Malformed);
        return false;
    }

    private int Mismatch(int parent, string? name, int element, SchemaTypeRef? type, SchemaTypeDecl? decl = null)
        => SkipValue(FieldKind.Unknown, parent, name, element, type, decl, DecodeProblem.TypeMismatch);

    private int SkipValue(FieldKind kind, int parent, string? name, int element, SchemaTypeRef? type, SchemaTypeDecl? decl,
        DecodeProblem problem)
    {
        var start = _pos;
        var end = start;
        var result = MsgPackToken.TrySkip(_memory.Span, ref end);
        if (result != TokenResult.Ok)
            return RawRest(parent, name, element, result == TokenResult.Truncated ? DecodeProblem.Truncated : DecodeProblem.Malformed);

        var index = Add(kind, parent, name, element, start, end - start, 0, default, type, decl, problem);
        _pos = end;
        return index;
    }

    private int RawRest(int parent, string? name, int element, DecodeProblem problem)
    {
        var index = Add(FieldKind.Raw, parent, name, element, _pos, _memory.Length - _pos, 0, default, null, null, problem);
        _pos = _memory.Length;
        _stopped = true;
        return index;
    }

    private int Leaf(FieldKind kind, in MsgPackToken token, DecodedScalar value, int parent, string? name, int element,
        SchemaTypeRef? type, SchemaTypeDecl? decl, DecodeProblem problem = DecodeProblem.None)
    {
        var index = Add(kind, parent, name, element, _pos, token.Length, token.HeaderLength, value, type, decl, problem);
        _pos += token.Length;
        return index;
    }

    // Starts a container: its header is consumed now, its length is set by Close once the children are written.
    private int Open(FieldKind kind, int parent, string? name, int element, in MsgPackToken token, DecodedScalar value,
        SchemaTypeRef? type, SchemaTypeDecl? decl)
    {
        var index = Add(kind, parent, name, element, _pos, token.HeaderLength, token.HeaderLength, value, type, decl, DecodeProblem.None);
        _pos += token.HeaderLength;
        return index;
    }

    private int Close(int index)
    {
        ref var field = ref Field(index);
        field.Length = _pos - field.Offset;
        return index;
    }

    private ref DecodedField Field(int index) => ref CollectionsMarshal.AsSpan(_fields)[index];

    private int Add(FieldKind kind, int parent, string? name, int element, int offset, int length, int header,
        DecodedScalar value, SchemaTypeRef? type, SchemaTypeDecl? decl, DecodeProblem problem)
    {
        var index = _fields.Count;
        var fields = _fields;
        fields.Add(new DecodedField
        {
            Index = index,
            Parent = parent,
            FirstChild = -1,
            NextSibling = -1,
            Depth = parent < 0 ? 0 : fields[parent].Depth + 1,
            Kind = kind,
            Problem = problem,
            Name = name,
            ElementIndex = element,
            Type = type,
            Decl = decl,
            Offset = offset,
            Length = length,
            HeaderLength = header,
            Value = value,
        });
        _lastChild.Add(-1);

        var span = CollectionsMarshal.AsSpan(fields);
        var previous = parent < 0 ? _lastTop : _lastChild[parent];
        if (previous >= 0)
            span[previous].NextSibling = index;
        if (parent < 0)
        {
            _lastTop = index;
            return index;
        }

        ref var owner = ref span[parent];
        if (owner.FirstChild < 0)
            owner.FirstChild = index;
        owner.ChildCount++;
        _lastChild[parent] = index;
        return index;
    }

    // A str header is accepted wherever bin is, as NexNet's reader does (golden vectors bin.from-str, guid.from-str).
    private static bool IsBytes(in MsgPackToken token) => token.Type is TokenType.Bin or TokenType.Str;

    private static bool IsNumber(in MsgPackToken token)
        => token.Type is TokenType.Float32 or TokenType.Float64 or TokenType.UInt or TokenType.Int;

    private static double ToDouble(in MsgPackToken token) => token.Type switch
    {
        TokenType.Float32 => BitConverter.Int32BitsToSingle((int)token.Bits),
        TokenType.Float64 => BitConverter.Int64BitsToDouble(token.Bits),
        TokenType.UInt => (ulong)token.Bits,
        _ => token.Bits,
    };

    private readonly record struct TypeArgs(IReadOnlyList<SchemaTypeRef> Args, int Parent);
}

/// <summary>Ext 78 element kinds (spec §10.5).</summary>
internal static class PrimitiveArrays
{
    public static int ElementSize(byte kind) => kind switch
    {
        1 => 1,
        2 or 3 or 10 or 11 => 2,
        4 or 5 or 8 => 4,
        6 or 7 or 9 => 8,
        _ => 0,
    };
}
