using NexNet.Generator.Ir;

namespace NexNet.Generator.Targets.JavaScript;

/// <summary>
/// Builds the JavaScript expression of the <c>Formatter&lt;T&gt;</c> of an IR type: runtime built-ins
/// (<c>$rt.int32Formatter</c>, <c>$rt.arrayFormatter(…)</c>), the generated formatters (<c>InteropItemFormatter</c>,
/// <c>InteropBoxFormatter(…)</c>) and the imported user formatters (D12). Used by the formatter emitter and by the
/// proxy emitter (J04), which passes the prefix it imports the generated formatter module under.
/// </summary>
internal sealed class JsFormatterExpressions
{
    private const string Rt = JsSyntax.Rt + ".";

    /// <summary>The formatter of the generic declaration itself inside its own factory.</summary>
    public const string Self = "$self";

    private readonly TypeScope _scope;
    private readonly string _formatters;
    private readonly Func<string, string> _userFormatter;
    private readonly Generic? _generic;
    private readonly Usage _usage;

    /// <param name="scope">The declared types.</param>
    /// <param name="formatters">Written before generated formatter names: "" in the formatter module, <c>fmt.</c> elsewhere.</param>
    /// <param name="userFormatter">The expression of an imported user formatter, from its <c>NexusTargetFormatter</c> spec.</param>
    public JsFormatterExpressions(TypeScope scope, string formatters, Func<string, string> userFormatter)
        : this(scope, formatters, userFormatter, null, new Usage())
    {
    }

    private JsFormatterExpressions(TypeScope scope, string formatters, Func<string, string> userFormatter, Generic? generic, Usage usage)
    {
        _scope = scope;
        _formatters = formatters;
        _userFormatter = userFormatter;
        _generic = generic;
        _usage = usage;
    }

    /// <summary>True once an expression used the <c>$seq</c> helper (a sequence of a type parameter).</summary>
    public bool UsesSeq => _usage.Seq;

    /// <summary>True once an expression used the <c>$lazy</c> helper (a recursive generic type).</summary>
    public bool UsesLazy => _usage.Lazy;

    /// <summary>
    /// The expressions inside the factory of a generic declaration: type parameter <c>i</c> is the formatter
    /// <c>f&lt;i&gt;</c>, the declaration itself (with its own type parameters) is <see cref="Self"/>, and factories of
    /// recursive generic types are created lazily so the factories do not recurse.
    /// </summary>
    public JsFormatterExpressions ForGeneric(string declId, int typeParamCount)
        => new(_scope, _formatters, _userFormatter, new Generic(declId, typeParamCount), _usage);

    /// <summary>The name of the formatter parameter of type parameter <paramref name="ordinal"/>.</summary>
    public static string TypeParamFormatter(int ordinal) => "f" + JsSyntax.Int(ordinal);

    /// <summary>The exported formatter (or factory) of a generated type: <c>InteropItemFormatter</c>.</summary>
    public static string FormatterName(string typeName) => typeName + "Formatter";

    /// <summary>The JavaScript expression of the formatter of <paramref name="type"/>.</summary>
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
                return Rt + "binFormatter";
            case TypeRefKind.PrimArray:
                return Rt + PrimArrayFormatter(type.Prim);
            case TypeRefKind.Seq:
                if (type.Args.Length == 1 && type.Args[0].Kind == TypeRefKind.TypeParam && type.Clr is "Array" or "List")
                {
                    // C# picks the wire form of T[] and List<T> once T is bound: ext 78 (bin for byte[]) for primitives.
                    _usage.Seq = true;
                    return _formatters + "$seq(" + Arg(0) + ", " + (type.Clr == "Array" ? "true" : "false") + ")";
                }

                return Rt + "arrayFormatter(" + Arg(0) + ")";
            case TypeRefKind.Queue:
            case TypeRefKind.Stack:
                return Rt + "arrayFormatter(" + Arg(0) + ")";
            case TypeRefKind.Set:
                return Rt + "setFormatter(" + Arg(0) + ")";
            case TypeRefKind.Map:
                return Rt + "mapFormatter(" + Arg(0) + ", " + Arg(1) + ")";
            case TypeRefKind.Tuple:
                switch (type.TupleKind)
                {
                    case "KVP":
                        return Rt + "keyValuePairFormatter(" + Arg(0) + ", " + Arg(1) + ")";
                    case "Value":
                        return Rt + "tupleFormatter(" + string.Join(", ", type.Args.Select(For)) + ")";
                    default:
                        // Tuple<…> is a class: nil is null.
                        return Rt + "nullableFormatter(" + Rt + "tupleFormatter(" + string.Join(", ", type.Args.Select(For)) + "))";
                }
            case TypeRefKind.Nullable:
                return Rt + "nullableFormatter(" + Arg(0) + ")";
            case TypeRefKind.TypeParam:
                if (_generic == null || type.Ordinal >= _generic.Count)
                    throw new InvalidOperationException("A type parameter outside a generic declaration.");
                return TypeParamFormatter(type.Ordinal);
            case TypeRefKind.Decl:
                return Decl(type);
            case TypeRefKind.External:
            {
                var formatter = type.NativeFormatters.FirstOrDefault(f => f.Target == (int)NexusTargets.JavaScript)?.Formatter
                                ?? throw new InvalidOperationException("No JavaScript formatter for " + type.Clr + " (NEXNET040).");
                return _userFormatter(formatter);
            }
            default:
                throw new InvalidOperationException("No JavaScript formatter for the type kind " + type.Kind + ".");
        }
    }

    /// <summary>True when the expression is a plain name (no call), so it can be used inline without hoisting.</summary>
    public static bool IsSimple(string expression) => expression.IndexOf('(') < 0;

    private string Decl(TypeRef type)
    {
        var decl = _scope.Find(type.DeclId) ?? throw new InvalidOperationException("The declared type '" + type.DeclId + "' is not in the IR.");
        var name = _formatters + FormatterName(JavaScriptTarget.Instance.TypeName(decl));
        if (type.Args.Length == 0)
            return name;

        if (_generic != null && decl.Id == _generic.DeclId && IsOwnTypeParams(type))
            return Self;

        var call = name + "(" + string.Join(", ", type.Args.Select(For)) + ")";
        var recursive = decl is ObjectDeclIr { IsRecursive: true } or UnionDeclIr { IsRecursive: true };
        if (_generic == null || !recursive)
            return call;

        _usage.Lazy = true;
        return _formatters + "$lazy(() => " + call + ")";
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

    /// <summary>The runtime formatter of a built-in leaf (exported by <c>@dtronix/nexnet</c>).</summary>
    public static string PrimFormatter(PrimKind prim) => prim switch
    {
        PrimKind.Bool => "boolFormatter",
        PrimKind.SByte => "sbyteFormatter",
        PrimKind.Byte => "byteFormatter",
        PrimKind.Int16 => "int16Formatter",
        PrimKind.UInt16 => "uint16Formatter",
        PrimKind.Int32 => "int32Formatter",
        PrimKind.UInt32 => "uint32Formatter",
        PrimKind.Int64 => "int64Formatter",
        PrimKind.UInt64 => "uint64Formatter",
        PrimKind.Single => "float32Formatter",
        PrimKind.Double => "float64Formatter",
        PrimKind.Half => "halfFormatter",
        PrimKind.Char => "charFormatter",
        PrimKind.Decimal => "decimalFormatter",
        PrimKind.String => "stringFormatter",
        PrimKind.DateTime => "dateTimeFormatter",
        PrimKind.DateTimeOffset => "dateTimeOffsetFormatter",
        PrimKind.TimeSpan => "timeSpanFormatter",
        PrimKind.DateOnly => "dateOnlyFormatter",
        PrimKind.TimeOnly => "timeOnlyFormatter",
        PrimKind.Guid => "guidFormatter",
        PrimKind.BigInteger => "bigIntegerFormatter",
        PrimKind.Uri => "uriFormatter",
        PrimKind.Version => "versionFormatter",
        _ => throw new InvalidOperationException("No JavaScript formatter for the primitive " + prim + "."),
    };

    /// <summary>The runtime formatter of an ext 78 primitive array.</summary>
    public static string PrimArrayFormatter(PrimKind prim) => prim switch
    {
        PrimKind.SByte => "int8ArrayFormatter",
        PrimKind.Int16 => "int16ArrayFormatter",
        PrimKind.UInt16 => "uint16ArrayFormatter",
        PrimKind.Int32 => "int32ArrayFormatter",
        PrimKind.UInt32 => "uint32ArrayFormatter",
        PrimKind.Int64 => "int64ArrayFormatter",
        PrimKind.UInt64 => "uint64ArrayFormatter",
        PrimKind.Single => "float32ArrayFormatter",
        PrimKind.Double => "float64ArrayFormatter",
        PrimKind.Half => "halfArrayFormatter",
        PrimKind.Char => "charArrayFormatter",
        _ => throw new InvalidOperationException("No JavaScript formatter for a primitive array of " + prim + "."),
    };

    private sealed class Generic
    {
        public Generic(string declId, int count)
        {
            DeclId = declId;
            Count = count;
        }

        public string DeclId { get; }

        public int Count { get; }
    }

    private sealed class Usage
    {
        public bool Seq { get; set; }

        public bool Lazy { get; set; }
    }
}
