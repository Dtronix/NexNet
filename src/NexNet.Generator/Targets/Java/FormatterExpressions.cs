using NexNet.Generator.Ir;

namespace NexNet.Generator.Targets.Java;

/// <summary>
/// Builds the Java expression of the <c>Formatter&lt;T&gt;</c> of an IR type: runtime built-ins
/// (<c>com.dtronix.nexnet.serialization.Formatters</c>), the generated <c>Formatters</c> constants and factories, and the
/// user formatters of <c>UserFormatters</c>. Used by the formatter emitter and by the proxy emitter (K04).
/// </summary>
internal sealed class FormatterExpressions
{
    private const string Rt = JavaSyntax.RuntimeFormatters + ".";

    private readonly TypeScope _scope;
    private readonly string _formatters;
    private readonly string _userFormatters;
    private readonly ObjectGenerics? _generic;

    /// <param name="scope">The declared types.</param>
    /// <param name="formatters">How the generated <c>Formatters</c> class is written where the expression goes.</param>
    /// <param name="userFormatters">How the generated <c>UserFormatters</c> class is written there.</param>
    public FormatterExpressions(TypeScope scope, string formatters, string userFormatters)
        : this(scope, formatters, userFormatters, null)
    {
    }

    private FormatterExpressions(TypeScope scope, string formatters, string userFormatters, ObjectGenerics? generic)
    {
        _scope = scope;
        _formatters = formatters;
        _userFormatters = userFormatters;
        _generic = generic;
    }

    /// <summary>True once an expression used the generated <c>seq</c> helper (a sequence of a type parameter).</summary>
    public bool UsesSeq { get; private set; }

    /// <summary>True once an expression used the generated <c>lazy</c> helper (a recursive generic type).</summary>
    public bool UsesLazy { get; private set; }

    /// <summary>
    /// The expressions inside the formatter class of a generic declaration: type parameter <c>i</c> is the formatter
    /// <c>f&lt;i&gt;</c>, the declaration itself (with its own type parameters) is <c>this</c>, and factories of other
    /// recursive generic types are created lazily so the constructors do not recurse.
    /// </summary>
    public FormatterExpressions ForGeneric(string declId, int typeParamCount)
        => new(_scope, _formatters, _userFormatters, new ObjectGenerics(declId, typeParamCount));

    /// <summary>The name of the formatter parameter of type parameter <paramref name="ordinal"/>.</summary>
    public static string TypeParamFormatter(int ordinal) => "f" + JavaSyntax.Int(ordinal);

    /// <summary>The Java expression of the formatter of <paramref name="type"/>.</summary>
    public string For(TypeRef type)
    {
        string Arg(int i) => i < type.Args.Length
            ? For(type.Args[i])
            : throw new InvalidOperationException("Malformed type reference " + type.Kind + ".");

        switch (type.Kind)
        {
            case TypeRefKind.Prim:
                return Rt + PrimFormatter(type.Prim);
            case TypeRefKind.Bin:
                return Rt + "BINARY";
            case TypeRefKind.PrimArray:
                return Rt + PrimArrayFormatter(type.Prim);
            case TypeRefKind.Seq:
                if (type.Args.Length == 1 && type.Args[0].Kind == TypeRefKind.TypeParam && type.Clr is "Array" or "List")
                {
                    // C# picks the wire form of T[] and List<T> once T is bound: ext 78 (or bin for byte[]) for primitives.
                    UsesSeq = true;
                    return _formatters + ".seq(" + Arg(0) + ", " + (type.Clr == "Array" ? "true" : "false") + ")";
                }

                return Rt + "list(" + Arg(0) + ")";
            case TypeRefKind.Set:
                return Rt + "set(" + Arg(0) + ")";
            case TypeRefKind.Queue:
                return Rt + "queue(" + Arg(0) + ")";
            case TypeRefKind.Stack:
                return Rt + "stack(" + Arg(0) + ")";
            case TypeRefKind.Map:
                return Rt + "map(" + Arg(0) + ", " + Arg(1) + ")";
            case TypeRefKind.Tuple:
                return type.TupleKind == "KVP"
                    ? Rt + "keyValuePair(" + Arg(0) + ", " + Arg(1) + ")"
                    : Rt + "tuple(" + string.Join(", ", type.Args.Select(For)) + ")";
            case TypeRefKind.Nullable:
                return Rt + "nullable(" + Arg(0) + ")";
            case TypeRefKind.TypeParam:
                if (_generic == null || type.Ordinal >= _generic.Count)
                    throw new InvalidOperationException("A type parameter outside a generic declaration.");
                return TypeParamFormatter(type.Ordinal);
            case TypeRefKind.Decl:
                return Decl(type);
            case TypeRefKind.External:
            {
                var formatter = type.NativeFormatters.FirstOrDefault(f => f.Target == (int)NexusTargets.Java)?.Formatter
                                ?? throw new InvalidOperationException("No Java formatter for " + type.Clr + " (NEXNET040).");
                return _userFormatters + "." + UserFormatterField(formatter);
            }
            default:
                throw new InvalidOperationException("No Java formatter for the type kind " + type.Kind + ".");
        }
    }

    private string Decl(TypeRef type)
    {
        var decl = _scope.Find(type.DeclId) ?? throw new InvalidOperationException("The declared type '" + type.DeclId + "' is not in the IR.");
        var name = _formatters + "." + JavaTarget.Instance.TypeName(decl);
        if (type.Args.Length == 0)
            return name;

        if (_generic != null && decl.Id == _generic.DeclId && IsOwnTypeParams(type))
            return "this";

        var call = name + "(" + string.Join(", ", type.Args.Select(For)) + ")";
        var recursive = decl is ObjectDeclIr { IsRecursive: true } or UnionDeclIr { IsRecursive: true };
        if (_generic == null || !recursive)
            return call;

        UsesLazy = true;
        return _formatters + ".lazy(() -> " + call + ")";
    }

    private bool IsOwnTypeParams(TypeRef type)
    {
        if (type.Args.Length != _generic!.Count)
            return false;
        for (var i = 0; i < type.Args.Length; i++)
        {
            if (type.Args[i].Kind != TypeRefKind.TypeParam || type.Args[i].Ordinal != i)
                return false;
        }

        return true;
    }

    /// <summary>The field of <c>UserFormatters</c> holding a native formatter: its class name with '.' as '_'.</summary>
    public static string UserFormatterField(string formatterClass)
    {
        var chars = formatterClass.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!(char.IsLetterOrDigit(chars[i]) || chars[i] == '_' || chars[i] == '$'))
                chars[i] = '_';
        }

        var name = new string(chars);
        return name.Length > 0 && char.IsDigit(name[0]) ? "_" + name : name;
    }

    /// <summary>The runtime formatter constant of a built-in leaf.</summary>
    public static string PrimFormatter(PrimKind prim) => prim switch
    {
        PrimKind.Bool => "BOOLEAN",
        PrimKind.SByte => "SBYTE",
        PrimKind.Byte => "BYTE",
        PrimKind.Int16 => "SHORT",
        PrimKind.UInt16 => "USHORT",
        PrimKind.Int32 => "INT",
        PrimKind.UInt32 => "UINT",
        PrimKind.Int64 => "LONG",
        PrimKind.UInt64 => "ULONG",
        PrimKind.Single => "FLOAT",
        PrimKind.Double => "DOUBLE",
        PrimKind.Half => "HALF",
        PrimKind.Char => "CHAR",
        PrimKind.Decimal => "DECIMAL",
        PrimKind.String => "STRING",
        PrimKind.DateTime => "DATE_TIME",
        PrimKind.DateTimeOffset => "DATE_TIME_OFFSET",
        PrimKind.TimeSpan => "TIME_SPAN",
        PrimKind.DateOnly => "DATE_ONLY",
        PrimKind.TimeOnly => "TIME_ONLY",
        PrimKind.Guid => "GUID",
        PrimKind.BigInteger => "BIG_INTEGER",
        PrimKind.Uri => "URI",
        PrimKind.Version => "VERSION",
        _ => throw new InvalidOperationException("No Java formatter for the primitive " + prim + "."),
    };

    /// <summary>The runtime formatter constant of an ext 78 primitive array.</summary>
    public static string PrimArrayFormatter(PrimKind prim) => prim switch
    {
        PrimKind.SByte => "SBYTE_ARRAY",
        PrimKind.Int16 => "SHORT_ARRAY",
        PrimKind.UInt16 => "USHORT_ARRAY",
        PrimKind.Int32 => "INT_ARRAY",
        PrimKind.UInt32 => "UINT_ARRAY",
        PrimKind.Int64 => "LONG_ARRAY",
        PrimKind.UInt64 => "ULONG_ARRAY",
        PrimKind.Single => "FLOAT_ARRAY",
        PrimKind.Double => "DOUBLE_ARRAY",
        PrimKind.Half => "HALF_ARRAY",
        PrimKind.Char => "CHAR_ARRAY",
        _ => throw new InvalidOperationException("No Java formatter for a primitive array of " + prim + "."),
    };

    private sealed class ObjectGenerics
    {
        public ObjectGenerics(string declId, int count)
        {
            DeclId = declId;
            Count = count;
        }

        public string DeclId { get; }

        public int Count { get; }
    }
}
