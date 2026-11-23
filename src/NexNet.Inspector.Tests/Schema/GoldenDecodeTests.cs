using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using NexNet.Inspector.Core.Schema;

namespace NexNet.Inspector.Tests.Schema;

/// <summary>
/// Decodes every value vector of tests/golden with a schema built from schemas.jsonl and the vectors' type expressions,
/// and checks field kinds, values and byte ranges against the typed JSON (tests/golden/README.md).
/// </summary>
public class GoldenDecodeTests
{
    private static readonly string[] ValueFiles =
        ["primitives.jsonl", "containers.jsonl", "objects.jsonl", "unions.jsonl", "ext78.jsonl", "collections.jsonl"];

    [Test]
    public async Task Every_value_vector_decodes_to_its_typed_json()
    {
        var golden = GoldenSchema.Load();
        var decoder = new SchemaDecoder(golden.Schema);
        var checkedCount = 0;
        var failures = new List<string>();

        foreach (var file in ValueFiles)
        {
            foreach (var vector in GoldenSchema.Vectors(file))
            {
                if (vector.Mode == "reject")
                    continue;

                var value = Decode(decoder, golden, vector);
                var problem = SchemaKit.CheckCoverage(value) ?? Compare(value, value.Fields[0], vector.Value!.Value, vector.Type == "CollectionListMessage");
                if (value.Fields[0].NextSibling >= 0)
                    problem ??= "more than one top-level field";
                if (problem != null)
                    failures.Add($"{vector.Id} ({vector.Type}): {problem}");
                checkedCount++;
            }
        }

        await Assert.That(failures).IsEmpty();
        await Assert.That(checkedCount).IsGreaterThan(400);
    }

    [Test]
    public async Task Reject_vectors_never_throw_and_flag_broken_bytes()
    {
        var golden = GoldenSchema.Load();
        var decoder = new SchemaDecoder(golden.Schema);
        var decoded = 0;
        var failures = new List<string>();

        foreach (var vector in GoldenSchema.Vectors("reject.jsonl"))
        {
            if (vector.Type != "CollectionListMessage" && !golden.TryParse(vector.Type, out _))
                continue;

            var value = Decode(decoder, golden, vector);
            decoded++;
            var problem = SchemaKit.CheckCoverage(value);
            // Byte-level damage always shows; value-level rules (ranges, UTF-8, depth) are the runtime's, not the viewer's.
            if (problem is null && vector.Error is "truncated" or "length" or "reserved" or "trailing" or "size"
                && value.Problem == DecodeProblem.None)
                problem = "decoded without a problem";
            if (problem != null)
                failures.Add($"{vector.Id} ({vector.Type}, {vector.Error}): {problem}");
        }

        await Assert.That(failures).IsEmpty();
        await Assert.That(decoded).IsGreaterThan(60);
    }

    private static DecodedValue Decode(SchemaDecoder decoder, GoldenSchema golden, GoldenVector vector)
    {
        if (vector.Type == "CollectionListMessage")
            return decoder.DecodeCollectionMessage(null, vector.Bytes);
        if (!golden.TryParse(vector.Type, out var type))
            throw new InvalidOperationException($"Unparsed type expression {vector.Type} ({vector.Id}).");
        return decoder.DecodeValue(type, vector.Bytes);
    }

    // Compares a decoded field with its typed JSON value; returns why they differ, or null.
    private static string? Compare(DecodedValue value, DecodedField field, JsonElement expected, bool collection)
    {
        var path = field.Name ?? (field.ElementIndex >= 0 ? $"[{field.ElementIndex}]" : "root");
        if (expected.ValueKind == JsonValueKind.Null)
        {
            // A value in an unused object key is skipped by readers (null in the JSON); the decoder shows it as unknown.
            return field.Kind == FieldKind.Nil || (field.Kind == FieldKind.Unknown && field.Name is null && field.Problem == DecodeProblem.None)
                ? null
                : $"{path}: expected nil, got {field.Kind}";
        }
        if (field.Problem != DecodeProblem.None && !(collection && field.Problem == DecodeProblem.UnknownType))
            return $"{path}: {field.Kind} with problem {field.Problem}";

        var children = SchemaKit.Children(value, field);
        switch (field.Kind)
        {
            case FieldKind.Boolean:
                return expected.GetBoolean() == field.Value.Boolean ? null : $"{path}: boolean";
            case FieldKind.DateTime:
            {
                // The decoder shows the wire bits; a reader takes kind 11 as Local (10), as the canonical JSON does.
                var wire = field.Value.Int64;
                var canonical = wire < 0 ? wire & ~(1L << 62) : wire;
                return Integer(expected) == canonical ? null : $"{path}: {wire} != {expected}";
            }
            case FieldKind.Integer:
            case FieldKind.TimeSpan:
            case FieldKind.DateOnly:
            case FieldKind.TimeOnly:
                return Integer(expected) == field.Value.Int64 ? null : $"{path}: {field.Value.Int64} != {expected}";
            case FieldKind.UnsignedInteger:
            case FieldKind.Char:
            case FieldKind.UnionTag:
                return Integer(expected) == field.Value.UInt64 ? null : $"{path}: {field.Value.UInt64} != {expected}";
            case FieldKind.Enum:
            {
                var actual = field.Decl is SchemaEnumDecl { IsUnsigned: true } ? (BigInteger)field.Value.UInt64 : field.Value.Int64;
                return Integer(expected) == actual ? null : $"{path}: enum {actual} != {expected}";
            }
            case FieldKind.Single:
                return Bits(expected, "f32:") == (uint)BitConverter.SingleToInt32Bits(field.Value.Single) ? null : $"{path}: f32";
            case FieldKind.Double:
                return Bits(expected, "f64:") == (ulong)field.Value.Int64 ? null : $"{path}: f64";
            case FieldKind.Half:
                return Bits(expected, "f16:") == (ushort)field.Value.Int64 ? null : $"{path}: f16";
            case FieldKind.String:
            case FieldKind.Uri:
            case FieldKind.Version:
                return expected.GetString() == value.GetText(field) ? null : $"{path}: '{value.GetText(field)}' != {expected}";
            case FieldKind.Binary:
                return Base64(expected, value.GetContent(field), path);
            case FieldKind.External:
            case FieldKind.Unknown when collection:
                return Base64(expected, value.GetBytes(field), path);
            case FieldKind.BigInteger:
                return expected.GetString() == new BigInteger(value.GetContent(field)).ToString(CultureInfo.InvariantCulture) ? null : $"{path}: BigInteger";
            case FieldKind.Guid:
                return expected.GetString() == new Guid(value.GetContent(field), bigEndian: true).ToString("D") ? null : $"{path}: Guid";
            case FieldKind.Decimal:
            {
                var parts = expected.EnumerateArray().Select(e => e.GetInt32()).ToArray();
                var lo = (int)field.Value.Int64;
                var mid = (int)(field.Value.Int64 >> 32);
                var hi = (int)field.Value.Extra;
                var flags = (int)(field.Value.Extra >> 32);
                return parts.SequenceEqual([lo, mid, hi, flags]) ? null : $"{path}: decimal";
            }
            case FieldKind.DateTimeOffset:
            {
                var parts = expected.EnumerateArray().ToArray();
                return Integer(parts[0]) == field.Value.Int64 && Integer(parts[1]) == field.Value.Extra ? null : $"{path}: DateTimeOffset";
            }
            case FieldKind.PrimitiveArray:
            {
                var items = expected.EnumerateArray().ToArray();
                if (items.Length != field.Value.Int64)
                    return $"{path}: {field.Value.Int64} elements, expected {items.Length}";
                var kind = DecodedValue.GetElementKind(field);
                for (var i = 0; i < items.Length; i++)
                {
                    value.TryGetElement(field, i, out var element);
                    var ok = kind switch
                    {
                        FieldKind.Integer => Integer(items[i]) == element.Int64,
                        FieldKind.UnsignedInteger or FieldKind.Char => Integer(items[i]) == element.UInt64,
                        FieldKind.Single => Bits(items[i], "f32:") == (uint)element.Int64,
                        FieldKind.Double => Bits(items[i], "f64:") == (ulong)element.Int64,
                        FieldKind.Half => Bits(items[i], "f16:") == (ushort)element.Int64,
                        _ => false,
                    };
                    if (!ok)
                        return $"{path}[{i}]: element {element.Int64} != {items[i]}";
                }

                return null;
            }
            case FieldKind.Sequence:
            case FieldKind.Tuple:
            case FieldKind.Union:
            case FieldKind.CollectionMessage:
            case FieldKind.CollectionBody:
                return Elements(value, children, expected.EnumerateArray().ToArray(), path, exact: true, collection);
            case FieldKind.Object:
                return Elements(value, children, expected.EnumerateArray().ToArray(), path, exact: false, collection);
            case FieldKind.Map:
            {
                var entries = expected.EnumerateArray().ToArray();
                if (entries.Length != children.Count)
                    return $"{path}: {children.Count} entries, expected {entries.Length}";
                for (var i = 0; i < entries.Length; i++)
                {
                    var pair = SchemaKit.Children(value, children[i]);
                    var problem = Compare(value, pair[0], entries[i][0], collection) ?? Compare(value, pair[1], entries[i][1], collection);
                    if (problem != null)
                        return $"{path}.{problem}";
                }

                return null;
            }
            default:
                return $"{path}: unexpected {field.Kind} for {expected}";
        }
    }

    private static string? Elements(DecodedValue value, List<DecodedField> children, JsonElement[] expected, string path, bool exact,
        bool collection)
    {
        // An object from a newer peer has extra (unknown) elements; one from an older peer is shorter and its missing
        // members read as defaults, which the canonical JSON lists.
        if (exact && children.Count != expected.Length)
            return $"{path}: {children.Count} elements, expected {expected.Length}";

        for (var i = 0; i < children.Count; i++)
        {
            if (i >= expected.Length)
            {
                if (children[i].Kind != FieldKind.Unknown)
                    return $"{path}[{i}]: extra element is {children[i].Kind}";
                continue;
            }

            var problem = Compare(value, children[i], expected[i], collection);
            if (problem != null)
                return $"{path}.{problem}";
        }

        return null;
    }

    private static BigInteger Integer(JsonElement element)
        => element.ValueKind == JsonValueKind.String
            ? BigInteger.Parse(element.GetString()!, CultureInfo.InvariantCulture)
            : BigInteger.Parse(element.GetRawText(), CultureInfo.InvariantCulture);

    private static ulong Bits(JsonElement element, string prefix)
    {
        var text = element.GetString()!;
        if (!text.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected a {prefix} bit pattern, got {text}.");
        return ulong.Parse(text.AsSpan(prefix.Length + 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static string? Base64(JsonElement expected, ReadOnlySpan<byte> actual, string path)
        => expected.GetString() == Convert.ToBase64String(actual) ? null : $"{path}: bytes {Convert.ToHexString(actual)} != {expected}";
}

internal sealed record GoldenVector(string Id, string Type, string Mode, string? Error, JsonElement? Value, byte[] Bytes);

/// <summary>The declared types of tests/golden/schemas.jsonl as an inspector schema, and a parser of type expressions.</summary>
internal sealed class GoldenSchema
{
    private static readonly HashSet<string> SeqInterfaces =
        ["IList", "IReadOnlyList", "IEnumerable", "ICollection", "IReadOnlyCollection"];

    private static readonly HashSet<string> MapNames = ["Dictionary", "IDictionary", "IReadOnlyDictionary"];

    private readonly Dictionary<string, SchemaTypeDecl> _decls;
    private readonly HashSet<string> _externals;

    private GoldenSchema(NexusSchema schema, HashSet<string> externals)
    {
        Schema = schema;
        _externals = externals;
        _decls = schema.Types.ToDictionary(t => t.Id);
    }

    public NexusSchema Schema { get; }

    public static GoldenSchema Load()
    {
        var types = new List<SchemaTypeDecl>();
        var externals = new HashSet<string>();
        var lines = Lines("schemas.jsonl").ToList();
        foreach (var line in lines)
        {
            if (line.GetProperty("kind").GetString() == "external")
                externals.Add(line.GetProperty("id").GetString()!);
        }

        // Member types refer to each other: parse them against a parser that knows every id up front.
        var ids = lines.Where(l => l.GetProperty("kind").GetString() is "object" or "union" or "enum")
            .Select(l => l.GetProperty("id").GetString()!).ToHashSet();
        var parser = new TypeParser(ids, externals);
        foreach (var line in lines)
        {
            var id = line.GetProperty("id").GetString()!;
            switch (line.GetProperty("kind").GetString())
            {
                case "object":
                    var members = line.GetProperty("members").EnumerateArray().Select(m => new SchemaMember(
                        m[1].GetString()!, m[0].GetInt32(), parser.Parse(m[2].GetString()!), true, false, null, null)).ToArray();
                    types.Add(new SchemaObjectDecl(id, id, "", id, [], null,
                        line.TryGetProperty("struct", out var isStruct) && isStruct.GetBoolean(), false, false,
                        Enumerable.Range(0, line.GetProperty("typeParams").GetInt32()).Select(i => "T" + i).ToArray(),
                        null, line.GetProperty("length").GetInt32(), false, members));
                    break;
                case "union":
                    types.Add(new SchemaUnionDecl(id, id, "", id, [], null, false,
                        Enumerable.Range(0, line.GetProperty("typeParams").GetInt32()).Select(i => "T" + i).ToArray(), false,
                        line.GetProperty("cases").EnumerateArray().Select(c => new SchemaUnionCase((ushort)c[0].GetInt32(), c[1].GetString()!, 1)).ToArray()));
                    break;
                case "enum":
                    var underlying = ((SchemaTypeRef)parser.Parse(line.GetProperty("underlying").GetString()!)).Prim;
                    types.Add(new SchemaEnumDecl(id, id, "", id, [], null, underlying, false, []));
                    break;
            }
        }

        var schema = new NexusSchema(1, [], types.ToArray(), 0);
        return new GoldenSchema(schema, externals);
    }

    public bool TryParse(string expression, out SchemaTypeRef type)
    {
        try
        {
            type = new TypeParser(_decls.Keys.ToHashSet(), _externals) { Decls = _decls }.Parse(expression);
            return true;
        }
        catch (FormatException)
        {
            type = null!;
            return false;
        }
    }

    public static IEnumerable<GoldenVector> Vectors(string file)
    {
        foreach (var line in Lines(file))
        {
            yield return new GoldenVector(
                line.GetProperty("id").GetString()!,
                line.GetProperty("type").GetString()!,
                line.GetProperty("mode").GetString()!,
                line.TryGetProperty("error", out var error) ? error.GetString() : null,
                line.TryGetProperty("value", out var value) ? value.Clone() : null,
                Convert.FromHexString(line.GetProperty("hex").GetString()!));
        }
    }

    private static IEnumerable<JsonElement> Lines(string file)
    {
        foreach (var line in File.ReadAllLines(Path.Combine(RepositoryRoot(), "tests", "golden", file)))
        {
            if (line.Length == 0)
                continue;
            using var doc = JsonDocument.Parse(line);
            yield return doc.RootElement.Clone();
        }
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "NexNet.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>Type expressions of the golden README, mapped to IR type references the way the generator maps .NET types.</summary>
    private sealed class TypeParser(HashSet<string> ids, HashSet<string> externals)
    {
        private string _text = "";
        private int _pos;

        public Dictionary<string, SchemaTypeDecl>? Decls { get; init; }

        public SchemaTypeRef Parse(string expression)
        {
            _text = expression;
            _pos = 0;
            var type = ParseType();
            if (_pos != _text.Length)
                throw new FormatException($"Unexpected '{_text[_pos..]}' in {expression}.");
            return type;
        }

        private SchemaTypeRef ParseType()
        {
            SchemaTypeRef type;
            if (Peek('('))
            {
                _pos++;
                var elements = List(')');
                type = new SchemaTypeRef(SchemaTypeKind.Tuple, clr: "ValueTuple", args: elements);
            }
            else if (Peek('$'))
            {
                _pos++;
                type = new SchemaTypeRef(SchemaTypeKind.TypeParam, ordinal: int.Parse(Name(), CultureInfo.InvariantCulture));
            }
            else
            {
                var name = Name();
                var args = Peek('<') ? Generic() : [];
                type = Named(name, args);
            }

            while (true)
            {
                if (Peek('['))
                {
                    _pos += 2;
                    type = Collection("Array", type);
                }
                else if (Peek('?'))
                {
                    _pos++;
                    type = new SchemaTypeRef(SchemaTypeKind.Nullable, args: [type]);
                }
                else
                {
                    return type;
                }
            }
        }

        private SchemaTypeRef[] Generic()
        {
            _pos++;
            return List('>');
        }

        private SchemaTypeRef[] List(char close)
        {
            var items = new List<SchemaTypeRef> { ParseType() };
            while (Peek(','))
            {
                _pos++;
                items.Add(ParseType());
            }

            if (!Peek(close))
                throw new FormatException($"Expected '{close}' in {_text}.");
            _pos++;
            return items.ToArray();
        }

        private SchemaTypeRef Named(string name, SchemaTypeRef[] args)
        {
            switch (name)
            {
                case "List":
                case "Memory":
                case "ReadOnlyMemory":
                    return Collection(name, args[0]);
                case "ReadOnlySequence":
                case "ArraySegment":
                    return new SchemaTypeRef(SchemaTypeKind.Bin, clr: name);
                case "HashSet":
                    return new SchemaTypeRef(SchemaTypeKind.Set, clr: name, args: args);
                case "Queue":
                    return new SchemaTypeRef(SchemaTypeKind.Queue, clr: name, args: args);
                case "Stack":
                    return new SchemaTypeRef(SchemaTypeKind.Stack, clr: name, args: args);
                case "Tuple":
                case "ValueTuple":
                case "KeyValuePair":
                    return new SchemaTypeRef(SchemaTypeKind.Tuple, clr: name, args: args);
            }

            if (SeqInterfaces.Contains(name))
                return new SchemaTypeRef(SchemaTypeKind.Seq, clr: name, args: args);
            if (MapNames.Contains(name))
                return new SchemaTypeRef(SchemaTypeKind.Map, clr: name, args: args);
            if (Prim(name) is { } prim)
                return SchemaTypeRef.Primitive(prim);
            if (externals.Contains(name))
                return new SchemaTypeRef(SchemaTypeKind.External, clr: name);
            if (!ids.Contains(name))
                throw new FormatException($"Unknown type {name}.");

            var decl = new SchemaTypeRef(SchemaTypeKind.Decl, declId: name, args: args);
            if (Decls != null)
                decl.Decl = Decls[name];
            return decl;
        }

        // T[], List<T>, Memory<T>: bin for bytes (not List<byte>), ext 78 for the primitive array element types.
        private static SchemaTypeRef Collection(string clr, SchemaTypeRef element)
        {
            if (element.Kind == SchemaTypeKind.Prim && element.Prim == SchemaPrimKind.Byte && clr != "List")
                return new SchemaTypeRef(SchemaTypeKind.Bin, clr: clr);
            if (element.Kind == SchemaTypeKind.Prim && SchemaTypeRef.IsPrimArrayElement(element.Prim))
                return new SchemaTypeRef(SchemaTypeKind.PrimArray, element.Prim, clr);
            return new SchemaTypeRef(SchemaTypeKind.Seq, clr: clr, args: [element]);
        }

        private static SchemaPrimKind? Prim(string name) => name switch
        {
            "bool" => SchemaPrimKind.Bool,
            "sbyte" => SchemaPrimKind.SByte,
            "byte" => SchemaPrimKind.Byte,
            "short" => SchemaPrimKind.Int16,
            "ushort" => SchemaPrimKind.UInt16,
            "int" => SchemaPrimKind.Int32,
            "uint" => SchemaPrimKind.UInt32,
            "long" => SchemaPrimKind.Int64,
            "ulong" => SchemaPrimKind.UInt64,
            "float" => SchemaPrimKind.Single,
            "double" => SchemaPrimKind.Double,
            "half" => SchemaPrimKind.Half,
            "char" => SchemaPrimKind.Char,
            "decimal" => SchemaPrimKind.Decimal,
            "string" => SchemaPrimKind.String,
            "DateTime" => SchemaPrimKind.DateTime,
            "DateTimeOffset" => SchemaPrimKind.DateTimeOffset,
            "TimeSpan" => SchemaPrimKind.TimeSpan,
            "DateOnly" => SchemaPrimKind.DateOnly,
            "TimeOnly" => SchemaPrimKind.TimeOnly,
            "Guid" => SchemaPrimKind.Guid,
            "BigInteger" => SchemaPrimKind.BigInteger,
            "Uri" => SchemaPrimKind.Uri,
            "Version" => SchemaPrimKind.Version,
            _ => null,
        };

        private bool Peek(char c) => _pos < _text.Length && _text[_pos] == c;

        private string Name()
        {
            var start = _pos;
            while (_pos < _text.Length && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_'))
                _pos++;
            if (_pos == start)
                throw new FormatException($"Expected a name at {_pos} in {_text}.");
            return _text[start.._pos];
        }
    }
}
