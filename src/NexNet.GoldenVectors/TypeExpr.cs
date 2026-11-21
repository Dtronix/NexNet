using System.Globalization;
using System.Text;
using NexNet.Interop;

namespace NexNet.GoldenVectors;

/// <summary>
/// The type expressions of the golden format (tests/golden/README.md, "Types"): C#-like names such as <c>long</c>,
/// <c>int[]</c>, <c>List&lt;string&gt;</c>, <c>Dictionary&lt;string,int&gt;</c>, <c>(int,string)</c>, <c>int?</c> or a declared
/// type with type arguments, <c>InteropBox&lt;string&gt;</c>. <see cref="Parse"/> maps one to its <see cref="TypeSchema"/>, with
/// the same wire rules as the generator IR; <see cref="Print"/> writes the canonical expression of a schema.
/// </summary>
public static class TypeExpr
{
    /// <summary>The built-in leaf aliases, in <see cref="SchemaPrim"/> order.</summary>
    private static readonly (string Name, SchemaPrim Prim)[] Prims =
    [
        ("bool", SchemaPrim.Bool),
        ("sbyte", SchemaPrim.SByte),
        ("byte", SchemaPrim.Byte),
        ("short", SchemaPrim.Int16),
        ("ushort", SchemaPrim.UInt16),
        ("int", SchemaPrim.Int32),
        ("uint", SchemaPrim.UInt32),
        ("long", SchemaPrim.Int64),
        ("ulong", SchemaPrim.UInt64),
        ("float", SchemaPrim.Single),
        ("double", SchemaPrim.Double),
        ("half", SchemaPrim.Half),
        ("char", SchemaPrim.Char),
        ("decimal", SchemaPrim.Decimal),
        ("string", SchemaPrim.String),
        ("DateTime", SchemaPrim.DateTime),
        ("DateTimeOffset", SchemaPrim.DateTimeOffset),
        ("TimeSpan", SchemaPrim.TimeSpan),
        ("DateOnly", SchemaPrim.DateOnly),
        ("TimeOnly", SchemaPrim.TimeOnly),
        ("Guid", SchemaPrim.Guid),
        ("BigInteger", SchemaPrim.BigInteger),
        ("Uri", SchemaPrim.Uri),
        ("Version", SchemaPrim.Version),
    ];

    /// <summary>The alias of an embedded MessagePack value carried as its raw bytes (message bodies).</summary>
    public const string Raw = "raw";

    private static readonly Dictionary<string, SchemaPrim> PrimByName = Prims.ToDictionary(p => p.Name, p => p.Prim, StringComparer.Ordinal);
    private static readonly Dictionary<SchemaPrim, string> NameByPrim = Prims.ToDictionary(p => p.Prim, p => p.Name);

    /// <summary>The alias of a built-in leaf.</summary>
    public static string PrimName(SchemaPrim prim)
        => NameByPrim.TryGetValue(prim, out var name) ? name : throw new ArgumentOutOfRangeException(nameof(prim), prim, null);

    /// <summary>The leaf of an alias.</summary>
    public static SchemaPrim ParsePrim(string name)
        => PrimByName.TryGetValue(name, out var prim) ? prim : throw new FormatException("'" + name + "' is not a built-in type.");

    /// <summary>True for the element types carried in ext 78 primitive arrays.</summary>
    public static bool IsExtElement(SchemaPrim prim) => prim is SchemaPrim.SByte or SchemaPrim.Int16 or SchemaPrim.UInt16
        or SchemaPrim.Int32 or SchemaPrim.UInt32 or SchemaPrim.Int64 or SchemaPrim.UInt64 or SchemaPrim.Single
        or SchemaPrim.Double or SchemaPrim.Char or SchemaPrim.Half;

    /// <summary>Parses a type expression.</summary>
    /// <param name="text">The expression.</param>
    /// <param name="schemas">The declared types, externals and messages it may name.</param>
    /// <exception cref="FormatException">The expression is malformed or names an unknown type.</exception>
    public static TypeSchema Parse(string text, GoldenSchemas schemas)
    {
        var parser = new Parser(text, schemas);
        var type = parser.Type();
        if (!parser.AtEnd)
            throw parser.Error("unexpected '" + parser.Rest + "'");
        return type;
    }

    /// <summary>The canonical expression of a schema. Declared ids are printed as they are.</summary>
    public static string Print(TypeSchema type)
    {
        var sb = new StringBuilder();
        Print(sb, type);
        return sb.ToString();
    }

    private static void Print(StringBuilder sb, TypeSchema t)
    {
        switch (t.Kind)
        {
            case TypeSchemaKind.Prim:
                sb.Append(PrimName(t.Prim));
                break;
            case TypeSchemaKind.Bin:
                sb.Append("byte[]");
                break;
            case TypeSchemaKind.PrimArray:
                sb.Append(PrimName(t.Prim)).Append("[]");
                break;
            case TypeSchemaKind.Seq when t.Clr == "Array":
                Print(sb, t.Args[0]);
                sb.Append("[]");
                break;
            case TypeSchemaKind.Seq:
                Generic(sb, t.Clr ?? "IEnumerable", t.Args);
                break;
            case TypeSchemaKind.Set:
                Generic(sb, "HashSet", t.Args);
                break;
            case TypeSchemaKind.Queue:
                Generic(sb, "Queue", t.Args);
                break;
            case TypeSchemaKind.Stack:
                Generic(sb, "Stack", t.Args);
                break;
            case TypeSchemaKind.Map:
                Generic(sb, "Dictionary", t.Args);
                break;
            case TypeSchemaKind.Tuple:
                sb.Append('(');
                for (var i = 0; i < t.Args.Count; i++)
                {
                    if (i > 0)
                        sb.Append(',');
                    Print(sb, t.Args[i]);
                }

                sb.Append(')');
                break;
            case TypeSchemaKind.Nullable:
                Print(sb, t.Args[0]);
                sb.Append('?');
                break;
            case TypeSchemaKind.Decl:
                if (t.Args.Count == 0)
                    sb.Append(t.DeclId);
                else
                    Generic(sb, t.DeclId!, t.Args);
                break;
            case TypeSchemaKind.TypeParam:
                sb.Append('$').Append(t.Ordinal.ToString(CultureInfo.InvariantCulture));
                break;
            case TypeSchemaKind.External:
                sb.Append(t.Clr);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(t), t.Kind, null);
        }
    }

    private static void Generic(StringBuilder sb, string name, IReadOnlyList<TypeSchema> args)
    {
        sb.Append(name).Append('<');
        for (var i = 0; i < args.Count; i++)
        {
            if (i > 0)
                sb.Append(',');
            Print(sb, args[i]);
        }

        sb.Append('>');
    }

    /// <summary>
    /// The wire form of a sequence container of <paramref name="element"/>: <c>T[]</c>, <c>List&lt;T&gt;</c>, <c>Memory&lt;T&gt;</c>
    /// and <c>ReadOnlyMemory&lt;T&gt;</c> of an ext 78 element are primitive arrays; <c>byte[]</c> and byte memory are bin; anything
    /// else is an element-wise array.
    /// </summary>
    private static TypeSchema Sequence(string container, TypeSchema element)
    {
        var prim = element.Kind == TypeSchemaKind.Prim ? element.Prim : SchemaPrim.None;
        switch (container)
        {
            case "Array":
            case "Memory":
            case "ReadOnlyMemory":
                if (prim == SchemaPrim.Byte)
                    return TypeSchema.Bin();
                return IsExtElement(prim) ? TypeSchema.PrimArray(prim) : TypeSchema.Seq(element, container);
            case "List":
                return IsExtElement(prim) ? TypeSchema.PrimArray(prim) : TypeSchema.Seq(element, container);
            case "ReadOnlySequence":
            case "ArraySegment":
                return prim == SchemaPrim.Byte ? TypeSchema.Bin() : throw new FormatException(container + " is only supported for byte.");
            default:
                return TypeSchema.Seq(element, container);
        }
    }

    private sealed class Parser
    {
        private readonly string _text;
        private readonly GoldenSchemas _schemas;
        private int _pos;

        public Parser(string text, GoldenSchemas schemas)
        {
            _text = text ?? throw new ArgumentNullException(nameof(text));
            _schemas = schemas;
        }

        public bool AtEnd => _pos >= _text.Length;

        public string Rest => _text.Substring(_pos);

        public FormatException Error(string message) => new("Type '" + _text + "' at " + _pos + ": " + message + ".");

        public TypeSchema Type()
        {
            var type = Base();
            while (!AtEnd)
            {
                if (Peek('[') && _pos + 1 < _text.Length && _text[_pos + 1] == ']')
                {
                    _pos += 2;
                    type = Sequence("Array", type);
                }
                else if (Peek('?'))
                {
                    _pos++;
                    type = TypeSchema.Nullable(type);
                }
                else
                {
                    break;
                }
            }

            return type;
        }

        private TypeSchema Base()
        {
            if (Peek('('))
            {
                _pos++;
                var items = new List<TypeSchema> { Type() };
                while (Peek(','))
                {
                    _pos++;
                    items.Add(Type());
                }

                Expect(')');
                return TypeSchema.Tuple(items.ToArray());
            }

            if (Peek('$'))
            {
                _pos++;
                var start = _pos;
                while (!AtEnd && char.IsAsciiDigit(_text[_pos]))
                    _pos++;
                if (start == _pos)
                    throw Error("expected a type parameter number");
                return TypeSchema.TypeParam(int.Parse(_text.AsSpan(start, _pos - start), NumberStyles.None, CultureInfo.InvariantCulture));
            }

            var name = Name();
            var args = new List<TypeSchema>();
            if (Peek('<'))
            {
                _pos++;
                args.Add(Type());
                while (Peek(','))
                {
                    _pos++;
                    args.Add(Type());
                }

                Expect('>');
            }

            return Resolve(name, args);
        }

        private TypeSchema Resolve(string name, List<TypeSchema> args)
        {
            TypeSchema One()
                => args.Count == 1 ? args[0] : throw Error(name + " takes one type argument");

            TypeSchema[] Two()
                => args.Count == 2 ? args.ToArray() : throw Error(name + " takes two type arguments");

            switch (name)
            {
                case "List":
                case "Memory":
                case "ReadOnlyMemory":
                case "ReadOnlySequence":
                case "ArraySegment":
                case "IList":
                case "IReadOnlyList":
                case "IEnumerable":
                case "ICollection":
                case "IReadOnlyCollection":
                    return Sequence(name, One());
                case "HashSet":
                    return TypeSchema.Set(One());
                case "Queue":
                    return TypeSchema.Queue(One());
                case "Stack":
                    return TypeSchema.Stack(One());
                case "Dictionary":
                case "IDictionary":
                case "IReadOnlyDictionary":
                {
                    var kv = Two();
                    return TypeSchema.Map(kv[0], kv[1]);
                }
                case "KeyValuePair":
                    return TypeSchema.Tuple(Two());
                case "Tuple":
                case "ValueTuple":
                    return args.Count > 0 ? TypeSchema.Tuple(args.ToArray()) : throw Error(name + " needs type arguments");
                case Raw:
                    return args.Count == 0 ? TypeSchema.External(Raw) : throw Error("raw takes no type arguments");
            }

            if (PrimByName.TryGetValue(name, out var prim))
                return args.Count == 0 ? TypeSchema.Primitive(prim) : throw Error(name + " takes no type arguments");

            if (_schemas.IsExternal(name))
                return args.Count == 0 ? TypeSchema.External(name) : throw Error(name + " takes no type arguments");

            if (_schemas.TryGetDecl(name, out var decl))
            {
                var expected = decl switch
                {
                    ObjectSchema o => o.TypeParameterCount,
                    UnionSchema u => u.TypeParameterCount,
                    _ => 0,
                };
                if (args.Count != expected)
                    throw Error(name + " takes " + expected + " type arguments");
                return TypeSchema.Decl(name, args.ToArray());
            }

            throw Error("unknown type '" + name + "'");
        }

        private string Name()
        {
            var start = _pos;
            while (!AtEnd && (char.IsAsciiLetterOrDigit(_text[_pos]) || _text[_pos] == '_'))
                _pos++;
            if (start == _pos)
                throw Error("expected a type name");
            return _text.Substring(start, _pos - start);
        }

        private bool Peek(char c) => _pos < _text.Length && _text[_pos] == c;

        private void Expect(char c)
        {
            if (!Peek(c))
                throw Error("expected '" + c + "'");
            _pos++;
        }
    }
}
